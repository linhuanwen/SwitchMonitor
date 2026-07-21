using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;
using SwitchMonitor.Network;
using SwitchMonitor.Storage;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// N01-3: 网络层接收端 + 活性检测 + 自动补拉 测试套件。
    ///
    /// 测试接缝（外部行为）：
    ///   T01: POST 数据到 /api/receive → SQLite 可查到写入的数据
    ///   T02: 模拟站机 /api/status 返回正常 → 标记在线
    ///   T03: 模拟站机 /api/status 超时 2 次 → 标记离线，事件通知触发
    ///   T04: 离线→在线 → 自动调 GET /api/events?since=xxx → 补拉成功
    ///   T05: 补拉中途断开 → lastTimestamp 不更新 → 下次从同一 since 重拉
    ///   T06: 重复数据（同 stationId+switchId+timestamp）→ INSERT OR IGNORE
    /// </summary>
    public static class N01_3Tests
    {
        private static string _tempDir;
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static void Run()
        {
            Console.WriteLine("--- N01-3 Network Layer Receiver Tests ---");
            Console.WriteLine();

            SetupTempDir();
            TestRunner.Test("T01: POST /api/receive → SQLite has written data", T01_ReceiveEndpoint_WriteToSqlite);
            SetupTempDir();
            TestRunner.Test("T02: Simulate /api/status OK → mark online", T02_StationMonitor_ProbeOnline);
            SetupTempDir();
            TestRunner.Test("T03: Simulate /api/status timeout 2x → mark offline, event fires", T03_StationMonitor_ProbeOffline);
            SetupTempDir();
            TestRunner.Test("T04: Offline→online → auto GET /api/events?since=xxx → catchup success", T04_DataCatcher_AutoCatchup);
            SetupTempDir();
            TestRunner.Test("T05: Catchup interrupted → lastTimestamp unchanged → re-catch from same since", T05_DataCatcher_Interrupted);
            SetupTempDir();
            TestRunner.Test("T06: Duplicate data (same switchId+timestamp) → INSERT OR IGNORE", T06_InsertOrIgnore_Dedup);

            CleanupTempDir();
        }

        private static void SetupTempDir()
        {
            CleanupTempDir();
            _tempDir = TestRunner.TempDir();
        }

        private static void CleanupTempDir()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }

        // ── T01: POST /api/receive → SQLite has written data ──

        private static void T01_ReceiveEndpoint_WriteToSqlite()
        {
            int port = FindFreePort();
            string parsedDataDir = Path.Combine(_tempDir, "parsed_data");
            Directory.CreateDirectory(parsedDataDir);

            var config = new NetworkConfig
            {
                ListenPort = port,
                ParsedDataDir = parsedDataDir,
                Stations = new List<StationInfo>
                {
                    new StationInfo { Id = "SSB", Name = "三水北站", Ip = "127.0.0.1", Port = 9000 }
                }
            };

            string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
            var catchupState = new CatchupState(catchupPath);

            using (var endpoint = new ReceiveEndpoint(config, catchupState))
            {
                endpoint.Start();
                Thread.Sleep(300); // 等待监听就绪

                // 构造推送数据（模拟 DataForwarder 发送的格式）
                var samples = GenerateTestSamples(50, 0.04);
                byte[] currentABlob = StorageManager.SerializeCurve(samples);

                var evt = new Dictionary<string, object>
                {
                    { "switchId", "1-J" },
                    { "timestamp", StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 15, 10, 30, 0)) },
                    { "direction", "定位→反位" },
                    { "durationSec", 7.84 },
                    { "sampleInterval", 0.04 },
                    { "sampleCount", 50 },
                    { "currentA", Convert.ToBase64String(currentABlob) }
                };

                var batch = new Dictionary<string, object>
                {
                    { "stationId", "SSB" },
                    { "batchTimestamp", 1712345678 },
                    { "events", new System.Collections.ArrayList { evt } }
                };

                string json = Serializer.Serialize(batch);
                byte[] rawData = Encoding.UTF8.GetBytes(json);

                // POST to /api/receive
                string url = string.Format("http://127.0.0.1:{0}/api/receive", port);
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = rawData.Length;

                using (var reqStream = request.GetRequestStream())
                {
                    reqStream.Write(rawData, 0, rawData.Length);
                }

                string response;
                using (var httpResponse = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(httpResponse.GetResponseStream(), Encoding.UTF8))
                {
                    response = reader.ReadToEnd();
                }

                // 验证响应
                TestRunner.AssertTrue(response.Contains("\"received\":1"),
                    "响应包含 received:1，实际: " + response);

                // 等待写入完成
                Thread.Sleep(200);

                // 验证 SQLite 中有数据
                string dbPath = Path.Combine(parsedDataDir, "SSB.db");
                TestRunner.AssertFileExists(dbPath, "SSB.db 已创建");

                using (var storage = new StorageManager(dbPath))
                {
                    int count = storage.GetEventCount();
                    TestRunner.AssertEqual(1, count, "events 表有 1 条记录");

                    EventRecord fetched = storage.GetEventBySwitchAndTimestamp("1-J",
                        StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 15, 10, 30, 0)));
                    TestRunner.AssertNotNull(fetched, "可查到插入的事件");
                    TestRunner.AssertEqual("1-J", fetched.SwitchId, "SwitchId");
                    TestRunner.AssertEqual("定位→反位", fetched.Direction, "Direction");
                    TestRunner.AssertEqual(7.84, fetched.DurationSec, 0.01, "DurationSec");
                    TestRunner.AssertTrue(fetched.CurrentABlob != null && fetched.CurrentABlob.Length > 0,
                        "CurrentA BLOB 非空");
                }

                // 验证 catchup_state 已更新
                long lastTs = catchupState.GetLastTimestamp("SSB");
                TestRunner.AssertTrue(lastTs > 0, "catchup_state 已更新 lastTimestamp > 0");
            }
        }

        // ── T02: Simulate /api/status OK → mark online ──

        private static void T02_StationMonitor_ProbeOnline()
        {
            // 启动一个模拟站机，返回正常 /api/status
            int mockPort = FindFreePort();
            var mockServer = new MockStationServer(mockPort, "SSB", "三水北站");
            mockServer.StatusResponse = new Dictionary<string, object>
            {
                { "stationId", "SSB" },
                { "stationName", "三水北站" },
                { "status", "ok" },
                { "lastTimestamp", 1712345678L },
                { "dbSizeMB", 320.0 }
            };
            mockServer.Start();
            Thread.Sleep(200);

            try
            {
                string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
                var catchupState = new CatchupState(catchupPath);

                var config = new NetworkConfig
                {
                    ListenPort = FindFreePort(),
                    ParsedDataDir = Path.Combine(_tempDir, "parsed_data"),
                    HttpTimeoutMs = 5000,
                    OfflineThreshold = 2,
                    ProbeIntervalMs = 60000, // 测试中不自动触发
                    Stations = new List<StationInfo>
                    {
                        new StationInfo
                        {
                            Id = "SSB", Name = "三水北站",
                            Ip = "127.0.0.1", Port = mockPort
                        }
                    }
                };

                using (var monitor = new StationMonitor(config, catchupState))
                {
                    // 初始状态 Unknown
                    TestRunner.AssertEqual(StationStatus.Unknown, monitor.GetStatus("SSB"), "初始状态 Unknown");

                    // 探测一次 → 应变为 Online
                    var station = config.Stations[0];
                    StationStatus status = monitor.ProbeStation(station);
                    TestRunner.AssertEqual(StationStatus.Online, status, "探测后变为 Online");
                    TestRunner.AssertEqual(StationStatus.Online, monitor.GetStatus("SSB"),
                        "GetStatus 返回 Online");
                    TestRunner.AssertEqual(0, monitor.GetConsecutiveFailures("SSB"),
                        "连续失败次数重置为 0");
                }
            }
            finally
            {
                mockServer.Stop();
            }
        }

        // ── T03: Simulate /api/status timeout 2x → mark offline, event fires ──

        private static void T03_StationMonitor_ProbeOffline()
        {
            string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
            var catchupState = new CatchupState(catchupPath);

            // 使用不存在的端口（无人监听→连接失败/超时）
            int deadPort = FindFreePort();

            var config = new NetworkConfig
            {
                ListenPort = FindFreePort(),
                ParsedDataDir = Path.Combine(_tempDir, "parsed_data"),
                HttpTimeoutMs = 2000,    // 短超时加速测试
                OfflineThreshold = 2,
                ProbeIntervalMs = 60000,
                Stations = new List<StationInfo>
                {
                    new StationInfo
                    {
                        Id = "DHD", Name = "大湖东站",
                        Ip = "127.0.0.1", Port = deadPort
                    }
                }
            };

            bool eventFired = false;
            StationStatus eventNewStatus = StationStatus.Unknown;

            using (var monitor = new StationMonitor(config, catchupState))
            {
                monitor.StationStateChanged += (sender, args) =>
                {
                    eventFired = true;
                    eventNewStatus = args.NewStatus;
                };

                var station = config.Stations[0];

                // 第一次探测 → 失败，但未达阈值（仍为 Unknown）
                StationStatus s1 = monitor.ProbeStation(station);
                TestRunner.AssertTrue(s1 == StationStatus.Unknown,
                    string.Format("第1次失败：仍为 Unknown，实际 {0}", s1));
                TestRunner.AssertEqual(1, monitor.GetConsecutiveFailures("DHD"),
                    "连续失败计数 = 1");

                // 第二次探测 → 失败，达到阈值 → 标记 Offline
                StationStatus s2 = monitor.ProbeStation(station);
                TestRunner.AssertEqual(StationStatus.Offline, s2,
                    string.Format("第2次失败：标记 Offline，实际 {0}", s2));
                TestRunner.AssertEqual(2, monitor.GetConsecutiveFailures("DHD"),
                    "连续失败计数 = 2");

                // 验证事件触发
                TestRunner.AssertTrue(eventFired, "StationStateChanged 事件已触发");
                TestRunner.AssertEqual(StationStatus.Offline, eventNewStatus,
                    "事件中 NewStatus = Offline");
            }
        }

        // ── T04: Offline→online → auto GET /api/events?since=xxx → catchup success ──

        private static void T04_DataCatcher_AutoCatchup()
        {
            int mockPort = FindFreePort();
            string parsedDataDir = Path.Combine(_tempDir, "parsed_data");
            Directory.CreateDirectory(parsedDataDir);

            // 准备测试事件数据
            long ts1 = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 16, 8, 0, 0));
            long ts2 = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 16, 8, 5, 0));
            long ts3 = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 16, 8, 10, 0));

            var samples = GenerateTestSamples(60, 0.04);
            byte[] blob = StorageManager.SerializeCurve(samples);

            // 模拟站机有 3 条事件
            var mockServer = new MockStationServer(mockPort, "SSB", "三水北站");
            mockServer.StatusResponse = new Dictionary<string, object>
            {
                { "stationId", "SSB" },
                { "stationName", "三水北站" },
                { "status", "ok" },
                { "lastTimestamp", ts3 },
                { "dbSizeMB", 1.5 }
            };
            mockServer.EventsData = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "switchId", "1-J" }, { "timestamp", ts1 },
                    { "direction", "定位→反位" }, { "durationSec", 7.0 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 60 },
                    { "currentA", Convert.ToBase64String(blob) }
                },
                new Dictionary<string, object>
                {
                    { "switchId", "2-J" }, { "timestamp", ts2 },
                    { "direction", "反位→定位" }, { "durationSec", 8.5 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 60 },
                    { "currentA", Convert.ToBase64String(blob) }
                },
                new Dictionary<string, object>
                {
                    { "switchId", "1-J" }, { "timestamp", ts3 },
                    { "direction", "定位→反位" }, { "durationSec", 6.8 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 60 },
                    { "currentA", Convert.ToBase64String(blob) }
                }
            };
            mockServer.Start();
            Thread.Sleep(200);

            try
            {
                string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
                var catchupState = new CatchupState(catchupPath);
                // 模拟之前已收到 ts1 的数据
                catchupState.UpdateTimestamp("SSB", ts1);
                catchupState.Save();

                var config = new NetworkConfig
                {
                    ListenPort = FindFreePort(),
                    ParsedDataDir = parsedDataDir,
                    HttpTimeoutMs = 10000,
                    Stations = new List<StationInfo>
                    {
                        new StationInfo
                        {
                            Id = "SSB", Name = "三水北站",
                            Ip = "127.0.0.1", Port = mockPort,
                            DbPath = Path.Combine(parsedDataDir, "SSB.db")
                        }
                    }
                };

                // 使用 DataCatcher 执行补拉
                int progressCount = 0;
                bool completed = false;

                using (var catcher = new DataCatcher(config, catchupState))
                {
                    catcher.ProgressChanged += (sender, args) =>
                    {
                        progressCount++;
                        if (args.IsComplete) completed = true;
                    };

                    var result = catcher.Catchup(config.Stations[0]);

                    TestRunner.AssertTrue(result.Success,
                        string.Format("补拉成功，实际: {0}", result.ErrorMessage ?? ""));
                    TestRunner.AssertTrue(result.TotalReceived >= 2,
                        string.Format("至少收到 2 条新事件，实际: {0}", result.TotalReceived));
                    TestRunner.AssertTrue(completed, "ProgressChanged IsComplete=true 触发");
                    TestRunner.AssertTrue(progressCount > 0, "进度事件已触发");
                }

                // 验证数据已写入 SQLite
                string dbPath = Path.Combine(parsedDataDir, "SSB.db");
                TestRunner.AssertFileExists(dbPath, "SSB.db 已创建");

                using (var storage = new StorageManager(dbPath))
                {
                    int count = storage.GetEventCount();
                    TestRunner.AssertTrue(count >= 2,
                        string.Format("SQLite 有 >=2 条事件，实际: {0}", count));

                    // 可查到 ts2 的事件
                    EventRecord rec2 = storage.GetEventBySwitchAndTimestamp("2-J", ts2);
                    TestRunner.AssertNotNull(rec2, "可查到 2-J 的 ts2 事件");
                }

                // 验证 catchup_state 已更新到最新时间戳
                long lastTs = catchupState.GetLastTimestamp("SSB");
                TestRunner.AssertEqual(ts3, lastTs,
                    string.Format("catchup_state 更新到 ts3={0}，实际={1}", ts3, lastTs));
            }
            finally
            {
                mockServer.Stop();
            }
        }

        // ── T05: Catchup interrupted → lastTimestamp unchanged → re-catch from same since ──

        private static void T05_DataCatcher_Interrupted()
        {
            int mockPort = FindFreePort();
            string parsedDataDir = Path.Combine(_tempDir, "parsed_data");
            Directory.CreateDirectory(parsedDataDir);

            long ts1 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 9, 0, 0));
            long ts2 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 9, 10, 0));
            long ts3 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 9, 20, 0));

            var samples = GenerateTestSamples(40, 0.04);
            byte[] blob = StorageManager.SerializeCurve(samples);

            // 第一阶段：模拟站机返回数据，但中途截断（只返回 events 但 JSON 不完整）
            // 通过模拟服务器监听端口，返回不完整 JSON
            var mockServer1 = new MockStationServer(mockPort, "SSB", "三水北站");
            mockServer1.EventsData = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "switchId", "1-J" }, { "timestamp", ts1 },
                    { "direction", "定位→反位" }, { "durationSec", 5.0 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 40 },
                    { "currentA", Convert.ToBase64String(blob) }
                }
            };
            // 设置 /api/events 返回损坏数据（模拟中断）
            mockServer1.EventsResponseCorrupted = true;
            mockServer1.Start();
            Thread.Sleep(200);

            try
            {
                string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
                var catchupState = new CatchupState(catchupPath);
                // 之前没有收到任何数据
                TestRunner.AssertEqual(0L, catchupState.GetLastTimestamp("SSB"), "初始 since=0");

                var config = new NetworkConfig
                {
                    ListenPort = FindFreePort(),
                    ParsedDataDir = parsedDataDir,
                    HttpTimeoutMs = 5000,
                    Stations = new List<StationInfo>
                    {
                        new StationInfo
                        {
                            Id = "SSB", Name = "三水北站",
                            Ip = "127.0.0.1", Port = mockPort,
                            DbPath = Path.Combine(parsedDataDir, "SSB.db")
                        }
                    }
                };

                // 第一次补拉 — 应该失败（模拟中断）
                using (var catcher1 = new DataCatcher(config, catchupState))
                {
                    var result = catcher1.Catchup(config.Stations[0]);

                    // 验证失败
                    // (corrupted mode 可能返回 success=false 或 success=true 但没完全写入)
                    // 关键：lastTimestamp 不应更新
                }

                // 验证 lastTimestamp 未更新
                long tsAfterFailed = catchupState.GetLastTimestamp("SSB");
                TestRunner.AssertTrue(tsAfterFailed == 0L,
                    string.Format("补拉失败后 lastTimestamp 未更新，仍为 0，实际={0}", tsAfterFailed));
            }
            finally
            {
                mockServer1.Stop();
            }

            // 第二阶段：启动正常服务器，补拉全部数据
            int mockPort2 = FindFreePort();
            var mockServer2 = new MockStationServer(mockPort2, "SSB", "三水北站");
            mockServer2.StatusResponse = new Dictionary<string, object>
            {
                { "stationId", "SSB" },
                { "stationName", "三水北站" },
                { "status", "ok" },
                { "lastTimestamp", ts3 },
                { "dbSizeMB", 1.0 }
            };
            mockServer2.EventsData = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "switchId", "1-J" }, { "timestamp", ts1 },
                    { "direction", "定位→反位" }, { "durationSec", 5.0 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 40 },
                    { "currentA", Convert.ToBase64String(blob) }
                },
                new Dictionary<string, object>
                {
                    { "switchId", "2-J" }, { "timestamp", ts2 },
                    { "direction", "反位→定位" }, { "durationSec", 6.0 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 40 },
                    { "currentA", Convert.ToBase64String(blob) }
                },
                new Dictionary<string, object>
                {
                    { "switchId", "3-J" }, { "timestamp", ts3 },
                    { "direction", "定位→反位" }, { "durationSec", 5.5 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 40 },
                    { "currentA", Convert.ToBase64String(blob) }
                }
            };
            mockServer2.Start();
            Thread.Sleep(200);

            try
            {
                string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
                var catchupState = CatchupState.Load(catchupPath);

                // 验证 since 仍然是 0（未因上次失败而更新）
                long since = catchupState.GetLastTimestamp("SSB");
                TestRunner.AssertTrue(since == 0L,
                    string.Format("第二轮：since 仍为 0（从同一 since 重拉），实际={0}", since));

                var config = new NetworkConfig
                {
                    ListenPort = FindFreePort(),
                    ParsedDataDir = parsedDataDir,
                    HttpTimeoutMs = 10000,
                    Stations = new List<StationInfo>
                    {
                        new StationInfo
                        {
                            Id = "SSB", Name = "三水北站",
                            Ip = "127.0.0.1", Port = mockPort2,
                            DbPath = Path.Combine(parsedDataDir, "SSB.db")
                        }
                    }
                };

                using (var catcher2 = new DataCatcher(config, catchupState))
                {
                    var result = catcher2.Catchup(config.Stations[0]);

                    TestRunner.AssertTrue(result.Success,
                        string.Format("第二轮补拉成功，错误: {0}", result.ErrorMessage ?? ""));
                    TestRunner.AssertEqual(3, result.TotalReceived,
                        string.Format("收到全部 3 条事件，实际: {0}", result.TotalReceived));
                }

                // 验证 lastTimestamp 已更新
                long lastTs = catchupState.GetLastTimestamp("SSB");
                TestRunner.AssertEqual(ts3, lastTs,
                    string.Format("第二轮后 lastTimestamp 更新到 ts3={0}，实际={1}", ts3, lastTs));
            }
            finally
            {
                mockServer2.Stop();
            }
        }

        // ── T06: Duplicate data → INSERT OR IGNORE ──

        private static void T06_InsertOrIgnore_Dedup()
        {
            string dbPath = Path.Combine(_tempDir, "dedup_test.db");

            using (var storage = new StorageManager(dbPath))
            {
                var samples = GenerateTestSamples(30, 0.04);
                byte[] blob = StorageManager.SerializeCurve(samples);
                long ts = StorageManager.ToUnixTimestamp(new DateTime(2024, 8, 1, 12, 0, 0));

                var rec = new EventRecord
                {
                    SwitchId = "1-J",
                    Timestamp = ts,
                    Direction = "定位→反位",
                    DurationSec = 7.0,
                    SampleInterval = 0.04,
                    SampleCount = 30,
                    CurrentABlob = blob,
                    CurrentBBlob = blob,
                    CurrentCBlob = blob,
                    PowerBlob = blob
                };

                // 第一次插入 → 成功
                long id1 = storage.InsertOrIgnoreEvent(rec);
                TestRunner.AssertTrue(id1 > 0,
                    string.Format("第一次插入成功，id={0}", id1));

                // 第二次插入同 switchId + timestamp → 忽略
                long id2 = storage.InsertOrIgnoreEvent(rec);
                TestRunner.AssertTrue(id2 <= 0,
                    string.Format("第二次插入被忽略，id={0}", id2));

                // 只有 1 条记录
                int count = storage.GetEventCount();
                TestRunner.AssertEqual(1, count,
                    string.Format("去重后只有 1 条记录，实际: {0}", count));

                // 不同 timestamp → 可以插入
                var rec2 = new EventRecord
                {
                    SwitchId = "1-J",
                    Timestamp = ts + 60, // 不同时间戳
                    Direction = "反位→定位",
                    DurationSec = 8.0,
                    SampleInterval = 0.04,
                    SampleCount = 30,
                    CurrentABlob = blob
                };
                long id3 = storage.InsertOrIgnoreEvent(rec2);
                TestRunner.AssertTrue(id3 > 0,
                    string.Format("不同 timestamp 插入成功，id={0}", id3));

                // 2 条记录
                int count2 = storage.GetEventCount();
                TestRunner.AssertEqual(2, count2,
                    string.Format("现有 2 条记录，实际: {0}", count2));

                // 不同 switchId 同 timestamp → 可以插入（唯一约束是 switch_id + timestamp）
                var rec3 = new EventRecord
                {
                    SwitchId = "2-J", // 不同 switchId
                    Timestamp = ts,   // 同 timestamp
                    Direction = "定位→反位",
                    DurationSec = 6.5,
                    SampleInterval = 0.04,
                    SampleCount = 30,
                    CurrentABlob = blob
                };
                long id4 = storage.InsertOrIgnoreEvent(rec3);
                TestRunner.AssertTrue(id4 > 0,
                    string.Format("不同 switchId 同 timestamp 插入成功，id={0}", id4));

                int count3 = storage.GetEventCount();
                TestRunner.AssertEqual(3, count3,
                    string.Format("现有 3 条记录，实际: {0}", count3));
            }
        }

        // ── 辅助方法 ──

        private static List<double[]> GenerateTestSamples(int count, double interval)
        {
            var samples = new List<double[]>(count);
            for (int i = 0; i < count; i++)
            {
                double t = i * interval;
                double v = 1.5 + Math.Sin(i * 0.05) * 0.5 + (i % 20) * 0.02;
                samples.Add(new double[] { t, Math.Round(v, 6) });
            }
            return samples;
        }

        private static int FindFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch
            {
                listener.Stop();
                return new Random().Next(50000, 60000);
            }
        }
    }

    /// <summary>
    /// 模拟站机 HTTP 服务器 — 用于测试 StationMonitor 和 DataCatcher。
    /// 支持 /api/status 和 /api/events 端点。
    /// </summary>
    internal class MockStationServer
    {
        private readonly int _port;
        private readonly string _stationId;
        private readonly string _stationName;
        private HttpListener _listener;
        private Thread _listenThread;
        private bool _running;

        /// <summary>/api/status 的响应数据。null 时不响应（模拟超时）。</summary>
        public Dictionary<string, object> StatusResponse { get; set; }

        /// <summary>/api/events 返回的事件列表。</summary>
        public List<Dictionary<string, object>> EventsData { get; set; }

        /// <summary>是否返回损坏的 /api/events 数据（模拟中断）。</summary>
        public bool EventsResponseCorrupted { get; set; }

        /// <summary>/api/status 是否延迟响应（模拟慢网络）。</summary>
        public int StatusDelayMs { get; set; }

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public MockStationServer(int port, string stationId, string stationName)
        {
            _port = port;
            _stationId = stationId;
            _stationName = stationName;
            EventsData = new List<Dictionary<string, object>>();
        }

        public void Start()
        {
            _listener = new HttpListener();
            try
            {
                _listener.Prefixes.Add(string.Format("http://+:{0}/", _port));
            }
            catch
            {
                // 回退
            }
            _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _port));
            _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", _port));
            _listener.Start();

            _running = true;
            _listenThread = new Thread(ListenLoop);
            _listenThread.IsBackground = true;
            _listenThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_listener != null && _listener.IsListening)
                    _listener.Stop();
            }
            catch { }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var ctxResult = _listener.BeginGetContext(OnRequest, _listener);
                    ctxResult.AsyncWaitHandle.WaitOne(1000);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch { }
            }
        }

        private void OnRequest(IAsyncResult ar)
        {
            HttpListener listener = (HttpListener)ar.AsyncState;
            HttpListenerContext ctx = null;
            try { ctx = listener.EndGetContext(ar); } catch { return; }

            try
            {
                string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

                if (path == "/api/status" && ctx.Request.HttpMethod == "GET")
                {
                    HandleStatus(ctx);
                }
                else if (path == "/api/events" && ctx.Request.HttpMethod == "GET")
                {
                    HandleEvents(ctx);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        private void HandleStatus(HttpListenerContext ctx)
        {
            // 模拟延迟
            if (StatusDelayMs > 0)
                Thread.Sleep(StatusDelayMs);

            if (StatusResponse == null)
            {
                // 模拟超时：不响应，关闭连接
                ctx.Response.StatusCode = 503;
                ctx.Response.Close();
                return;
            }

            string json = Serializer.Serialize(StatusResponse);
            byte[] data = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
        }

        private void HandleEvents(HttpListenerContext ctx)
        {
            // 解析 since 参数
            long since = 0;
            string query = ctx.Request.Url.Query;
            if (!string.IsNullOrEmpty(query))
            {
                foreach (string part in query.TrimStart('?').Split('&'))
                {
                    string[] kv = part.Split('=');
                    if (kv.Length == 2 && kv[0].ToLowerInvariant() == "since")
                    {
                        long.TryParse(kv[1], out since);
                        break;
                    }
                }
            }

            // 过滤 since 之后的事件
            var filteredEvents = new System.Collections.ArrayList();
            foreach (var evt in EventsData)
            {
                if (evt.ContainsKey("timestamp"))
                {
                    long ts = Convert.ToInt64(evt["timestamp"]);
                    if (ts > since)
                        filteredEvents.Add(evt);
                }
                else
                {
                    filteredEvents.Add(evt);
                }
            }

            if (EventsResponseCorrupted)
            {
                // 模拟中断：返回不完整 JSON（缺少闭合括号）
                string corruptedJson = string.Format(
                    "{{\"stationId\":\"{0}\",\"since\":{1},\"count\":{2},\"events\":[",
                    _stationId, since, filteredEvents.Count);
                byte[] badData = Encoding.UTF8.GetBytes(corruptedJson);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = badData.Length;
                ctx.Response.OutputStream.Write(badData, 0, badData.Length);
                return;
            }

            var response = new Dictionary<string, object>
            {
                { "stationId", _stationId },
                { "since", since },
                { "count", filteredEvents.Count },
                { "events", filteredEvents }
            };

            string json = Serializer.Serialize(response);
            byte[] data = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
        }
    }
}
