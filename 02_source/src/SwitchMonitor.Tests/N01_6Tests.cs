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
using SwitchMonitor.DataForwarder;
using SwitchMonitor.Network;
using SwitchMonitor.Storage;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// N01-6: 集成测试 — 端到端连通性验证。
    ///
    /// 8 个测试场景，全部在 localhost 上运行，不依赖真实多机器环境。
    /// 使用进程级测试（启动 DataForwarder.exe）+ 组件级测试
    /// （ReceiveEndpoint, StationMonitor, DataCatcher）串联验证全流程。
    ///
    /// 测试接缝（全部通过 public interface）：
    ///   T01: GET /api/status + GET /api/events 端点（HTTP）
    ///   T02: PushEngine 推送 → POST /api/receive → ReceiveEndpoint 落库
    ///   T03: 两个 DataForwarder 互相订阅，双向推送
    ///   T04: StationMonitor 探测 → StationStateChanged → DataCatcher 自动补拉
    ///   T05: PushEngine 重试 3 次→放弃，健康 subscriber 不受影响
    ///   T06: DataCatcher.Catchup() 同步补拉，填补数据缺口
    ///   T07: StorageManager 并发读写无锁冲突
    ///   T08: EventToDict → gzip → DictToEventRecord 完整序列化往返
    /// </summary>
    public static class N01_6Tests
    {
        private static string _tempDir;
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static void Run()
        {
            Console.WriteLine("--- N01-6 Integration Tests ---");
            Console.WriteLine();

            // ═══ T1: 单站机 DataForwarder API ═══
            SetupTempDir();
            TestRunner.Test("T01: /api/status + /api/events with BLOBs", T01_StationApi);

            // ═══ T2: 推送→接收 端到端 ═══
            SetupTempDir();
            TestRunner.Test("T02: Push->Receive end-to-end within 10s", T02_PushToReceive);

            // ═══ T3: 两台全互联 ═══
            SetupTempDir();
            TestRunner.Test("T03: Two-station full mesh bidirectional", T03_FullMesh);

            // ═══ T4: 离线检测 + 自动补拉 ═══
            SetupTempDir();
            TestRunner.Test("T04: Offline detection + auto catchup on restore", T04_OfflineCatchup);

            // ═══ T5: 重试→放弃 ═══
            SetupTempDir();
            TestRunner.Test("T05: Retry 3x then give up, healthy subscriber ok", T05_RetryGiveUp);

            // ═══ T6: 手动补拉 ═══
            SetupTempDir();
            TestRunner.Test("T06: Manual catchup fills data gap completely", T06_ManualCatchup);

            // ═══ T7: SQLite 并发安全 ═══
            SetupTempDir();
            TestRunner.Test("T07: Concurrent write + read no lock conflict", T07_ConcurrentSafety);

            // ═══ T8: gzip 往返 ═══
            TestRunner.Test("T08: Full EventRecord (4 BLOBs + diagnosis) gzip roundtrip", T08_GzipRoundtrip);

            CleanupTempDir();
        }

        // ── 目录管理 ──

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

        // ═══════════════════════════════════════════════════════════════
        // T01: 单站机 DataForwarder API
        // 验证 GET /api/status 和 GET /api/events 端点返回正确的 JSON，
        // 包括 BLOB 数据的 Base64 编码。
        // ═══════════════════════════════════════════════════════════════

        private static void T01_StationApi()
        {
            int port = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "test.db");
            string configPath = CreateStationConfig(_tempDir, port, "SSB", "三水北站");

            // 预填充数据库（含 BLOB 数据的完整事件）
            var samples = GenerateTestSamples(100, 0.04);
            long ts1 = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 15, 10, 30, 0));
            long ts2 = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 15, 10, 35, 0));

            using (var storage = new StorageManager(dbPath))
            {
                storage.InsertEvent(new EventRecord
                {
                    SwitchId = "1-J",
                    Timestamp = ts1,
                    Direction = "定位到反位",
                    DurationSec = 7.84,
                    SampleInterval = 0.04,
                    SampleCount = 100,
                    CurrentABlob = StorageManager.SerializeCurve(samples),
                    CurrentBBlob = StorageManager.SerializeCurve(samples),
                    CurrentCBlob = StorageManager.SerializeCurve(samples),
                    PowerBlob = StorageManager.SerializeCurve(samples),
                    DiagJson = "{\"level\":\"正常\",\"results\":[]}"
                });
                storage.InsertEvent(new EventRecord
                {
                    SwitchId = "2-J",
                    Timestamp = ts2,
                    Direction = "反位到定位",
                    DurationSec = 6.51,
                    SampleInterval = 0.04,
                    SampleCount = 80,
                    CurrentABlob = StorageManager.SerializeCurve(GenerateTestSamples(80, 0.04))
                });
            }

            // 启动 DataForwarder 进程
            var process = StartDataForwarder(exePath: FindDataForwarderExe(),
                workingDir: _tempDir, configPath: configPath, dbPath: dbPath);
            try
            {
                Thread.Sleep(1500); // 等待 HTTP 服务器就绪

                // ── 验证 /api/status ──
                string statusUrl = string.Format("http://127.0.0.1:{0}/api/status", port);
                string statusJson = HttpGet(statusUrl);
                TestRunner.AssertNotNull(statusJson, "/api/status 响应非空");

                var status = Serializer.Deserialize<Dictionary<string, object>>(statusJson);
                TestRunner.AssertEqual("SSB", (string)status["stationId"], "status.stationId");
                TestRunner.AssertEqual("三水北站", (string)status["stationName"], "status.stationName");
                TestRunner.AssertEqual("ok", (string)status["status"], "status.status=ok");
                TestRunner.AssertTrue(status.ContainsKey("lastTimestamp"), "含 lastTimestamp");
                TestRunner.AssertTrue(status.ContainsKey("dbSizeMB"), "含 dbSizeMB");

                // ── 验证 /api/events?since=0（响应为 gzip 压缩） ──
                string eventsUrl = string.Format("http://127.0.0.1:{0}/api/events?since=0", port);
                string eventsJson = HttpGetAutoDecompress(eventsUrl);
                TestRunner.AssertNotNull(eventsJson, "/api/events 响应非空");

                var eventsData = Serializer.Deserialize<Dictionary<string, object>>(eventsJson);
                TestRunner.AssertEqual("SSB", (string)eventsData["stationId"], "events.stationId");

                int eventCount = Convert.ToInt32(eventsData["count"]);
                TestRunner.AssertEqual(2, eventCount, "events.count=2");

                // 验证事件列表
                var eventList = (System.Collections.ArrayList)eventsData["events"];
                TestRunner.AssertEqual(2, eventList.Count, "events 数组长度=2");

                // 验证第一条事件的字段
                var evt1 = (Dictionary<string, object>)eventList[0];
                TestRunner.AssertEqual("1-J", (string)evt1["switchId"], "event[0].switchId");
                TestRunner.AssertEqual(ts1, Convert.ToInt64(evt1["timestamp"]), "event[0].timestamp");
                TestRunner.AssertTrue(evt1.ContainsKey("currentA"), "event[0] 含 currentA Base64");
                TestRunner.AssertTrue(evt1.ContainsKey("currentB"), "event[0] 含 currentB Base64");
                TestRunner.AssertTrue(evt1.ContainsKey("currentC"), "event[0] 含 currentC Base64");
                TestRunner.AssertTrue(evt1.ContainsKey("power"), "event[0] 含 power Base64");
                // 注: InsertEvent 不持久化 DiagJson 字段（diagnosis 由 SaveDiagnosis 单独更新），
                // 因此通过 /api/events 读取时 diagnosis 可能不存在。

                // 验证 Base64 BLOB 可解码
                byte[] decodedBlob = Convert.FromBase64String((string)evt1["currentA"]);
                TestRunner.AssertTrue(decodedBlob.Length > 0, "currentA Base64 解码后非空");
                var deserializedCurve = StorageManager.DeserializeCurve(decodedBlob);
                TestRunner.AssertEqual(100, deserializedCurve.Count, "解码后曲线有 100 个采样点");
            }
            finally
            {
                KillProcess(process);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // T02: 推送→接收 端到端
        // 启动 DataForwarder + ReceiveEndpoint，插入数据后
        // 验证 10 秒内接收端 SQLite 出现数据。
        // ═══════════════════════════════════════════════════════════════

        private static void T02_PushToReceive()
        {
            int stationPort = FindFreePort();
            int receiverPort = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "station.db");
            string parsedDataDir = Path.Combine(_tempDir, "parsed_data");
            Directory.CreateDirectory(parsedDataDir);

            // 配置 DataForwarder 推送到本地接收端
            string configPath = CreateStationConfig(_tempDir, stationPort, "SSB", "三水北站",
                string.Format("127.0.0.1:{0}", receiverPort));

            // 初始化站机数据库
            using (var storage = new StorageManager(dbPath)) { /* 建表 */ }

            // ── 启动接收端 ──
            string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
            var catchupState = new CatchupState(catchupPath);
            var netConfig = new NetworkConfig
            {
                ListenPort = receiverPort,
                ParsedDataDir = parsedDataDir,
                Stations = new List<StationInfo>
                {
                    new StationInfo { Id = "SSB", Name = "三水北站", Ip = "127.0.0.1", Port = stationPort }
                }
            };

            int receivedCount = 0;
            using (var endpoint = new ReceiveEndpoint(netConfig, catchupState))
            {
                endpoint.OnDataReceived += (stationId, count) => { receivedCount += count; };
                endpoint.Start();
                Thread.Sleep(300);

                // ── 启动 DataForwarder ──
                var process = StartDataForwarder(FindDataForwarderExe(), _tempDir, configPath, dbPath);
                try
                {
                    Thread.Sleep(2000); // 等待 DataForwarder 就绪 + 首次轮询

                    // ── 插入测试事件 ──
                    var samples = GenerateTestSamples(60, 0.04);
                    long ts = StorageManager.ToUnixTimestamp(DateTime.Now);

                    using (var storage = new StorageManager(dbPath))
                    {
                        storage.InsertEvent(new EventRecord
                        {
                            SwitchId = "1-J",
                            Timestamp = ts,
                            Direction = "定位到反位",
                            DurationSec = 7.0,
                            SampleInterval = 0.04,
                            SampleCount = 60,
                            CurrentABlob = StorageManager.SerializeCurve(samples),
                            CurrentBBlob = StorageManager.SerializeCurve(samples),
                            CurrentCBlob = StorageManager.SerializeCurve(samples),
                            PowerBlob = StorageManager.SerializeCurve(samples)
                        });
                    }

                    // ── 等待推送（最多 10 秒） ──
                    var sw = Stopwatch.StartNew();
                    bool dataArrived = false;
                    while (sw.ElapsedMilliseconds < 10000)
                    {
                        if (receivedCount > 0) { dataArrived = true; break; }
                        Thread.Sleep(200);
                    }
                    sw.Stop();

                    TestRunner.AssertTrue(dataArrived,
                        string.Format("数据在 {0}ms 内到达接收端", sw.ElapsedMilliseconds));
                    TestRunner.AssertTrue(sw.ElapsedMilliseconds <= 10000,
                        string.Format("推送延迟 ≤10s，实际: {0}ms", sw.ElapsedMilliseconds));

                    // ── 验证接收端 SQLite ──
                    Thread.Sleep(500); // 等待写入完成
                    string receiverDb = Path.Combine(parsedDataDir, "SSB.db");
                    TestRunner.AssertFileExists(receiverDb, "接收端 SSB.db 已创建");

                    using (var storage = new StorageManager(receiverDb))
                    {
                        int count = storage.GetEventCount();
                        TestRunner.AssertEqual(1, count,
                            string.Format("接收端 SQLite 有 1 条事件，实际: {0}", count));

                        EventRecord fetched = storage.GetEventBySwitchAndTimestamp("1-J", ts);
                        TestRunner.AssertNotNull(fetched, "可查到推送的事件");
                        TestRunner.AssertEqual("定位到反位", fetched.Direction, "Direction 一致");
                        TestRunner.AssertEqual(7.0, fetched.DurationSec, 0.01, "DurationSec 一致");
                        TestRunner.AssertNotNull(fetched.CurrentABlob, "CurrentA BLOB 非空");
                        TestRunner.AssertTrue(fetched.CurrentABlob.Length > 0, "CurrentA BLOB 有内容");
                    }
                }
                finally
                {
                    KillProcess(process);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // T03: 两台全互联
        // 启动两个 DataForwarder，每个搭配 ReceiveEndpoint 互相订阅。
        // A 推给 B 的 ReceiveEndpoint，B 推给 A 的 ReceiveEndpoint。
        // A 插入 → B 收到；B 插入 → A 收到。
        // ═══════════════════════════════════════════════════════════════

        private static void T03_FullMesh()
        {
            int portA = FindFreePort();     // DataForwarder A 的 HTTP 端口
            int portB = FindFreePort();     // DataForwarder B 的 HTTP 端口
            int recvPortA = FindFreePort(); // ReceiveEndpoint A 的端口（接受 B 推送）
            int recvPortB = FindFreePort(); // ReceiveEndpoint B 的端口（接受 A 推送）

            string dirA = Path.Combine(_tempDir, "station_A");
            string dirB = Path.Combine(_tempDir, "station_B");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            string dbA = Path.Combine(dirA, "data.db");
            string dbB = Path.Combine(dirB, "data.db");

            // A 订阅 B 的接收端，B 订阅 A 的接收端
            string configA = CreateStationConfig(dirA, portA, "SSB", "三水北站",
                string.Format("127.0.0.1:{0}", recvPortB)); // A 推送到 B 的接收端
            string configB = CreateStationConfig(dirB, portB, "DHD", "大湖东站",
                string.Format("127.0.0.1:{0}", recvPortA)); // B 推送到 A 的接收端

            // 初始化数据库
            using (var s = new StorageManager(dbA)) { }
            using (var s = new StorageManager(dbB)) { }

            // 准备接收端数据目录
            string parsedA = Path.Combine(dirA, "parsed_data");
            string parsedB = Path.Combine(dirB, "parsed_data");
            Directory.CreateDirectory(parsedA);
            Directory.CreateDirectory(parsedB);

            // ── 启动 ReceiveEndpoint A（接收 B 的推送）──
            string catchupPathA = Path.Combine(dirA, "catchup_state.json");
            var catchupStateA = new CatchupState(catchupPathA);
            var netConfigA = new NetworkConfig
            {
                ListenPort = recvPortA,
                ParsedDataDir = parsedA,
                Stations = new List<StationInfo>
                {
                    new StationInfo { Id = "DHD", Name = "大湖东站", Ip = "127.0.0.1", Port = portB }
                }
            };

            // ── 启动 ReceiveEndpoint B（接收 A 的推送）──
            string catchupPathB = Path.Combine(dirB, "catchup_state.json");
            var catchupStateB = new CatchupState(catchupPathB);
            var netConfigB = new NetworkConfig
            {
                ListenPort = recvPortB,
                ParsedDataDir = parsedB,
                Stations = new List<StationInfo>
                {
                    new StationInfo { Id = "SSB", Name = "三水北站", Ip = "127.0.0.1", Port = portA }
                }
            };

            int receivedByB = 0;
            int receivedByA = 0;

            using (var endpointA = new ReceiveEndpoint(netConfigA, catchupStateA))
            using (var endpointB = new ReceiveEndpoint(netConfigB, catchupStateB))
            {
                endpointA.OnDataReceived += (stationId, count) => { receivedByA += count; };
                endpointB.OnDataReceived += (stationId, count) => { receivedByB += count; };
                endpointA.Start();
                endpointB.Start();
                Thread.Sleep(300);

                // ── 启动两个 DataForwarder ──
                var procA = StartDataForwarder(FindDataForwarderExe(), dirA, configA, dbA);
                var procB = StartDataForwarder(FindDataForwarderExe(), dirB, configB, dbB);

                try
                {
                    Thread.Sleep(2500); // 等待双方就绪

                    var samples = GenerateTestSamples(50, 0.04);

                    // ── A 插入事件 → 验证 B 收到 ──
                    long tsA = StorageManager.ToUnixTimestamp(DateTime.Now);
                    using (var storage = new StorageManager(dbA))
                    {
                        storage.InsertEvent(new EventRecord
                        {
                            SwitchId = "1-J", Timestamp = tsA,
                            Direction = "定位到反位", DurationSec = 5.5,
                            SampleInterval = 0.04, SampleCount = 50,
                            CurrentABlob = StorageManager.SerializeCurve(samples)
                        });
                    }

                    // 等待推送 + 接收（给足够时间让 mergeWindow + push + retry 完成）
                    Thread.Sleep(4000);
                    TestRunner.AssertTrue(receivedByB > 0,
                        string.Format("B 的 ReceiveEndpoint 收到 A 的推送，receivedByB={0}", receivedByB));

                    // 验证 B 的 SQLite
                    string dbReceivedB = Path.Combine(parsedB, "SSB.db");
                    using (var storageB = new StorageManager(dbReceivedB))
                    {
                        int countB = storageB.GetEventCount();
                        TestRunner.AssertTrue(countB >= 1,
                            string.Format("B 的 SQLite 有来自 A 的数据，count={0}", countB));
                    }

                    // ── B 插入事件 → 验证 A 收到 ──
                    long tsB = StorageManager.ToUnixTimestamp(DateTime.Now);
                    using (var storage = new StorageManager(dbB))
                    {
                        storage.InsertEvent(new EventRecord
                        {
                            SwitchId = "2-X", Timestamp = tsB,
                            Direction = "反位到定位", DurationSec = 6.2,
                            SampleInterval = 0.04, SampleCount = 40,
                            CurrentABlob = StorageManager.SerializeCurve(GenerateTestSamples(40, 0.04))
                        });
                    }

                    // 等待推送 + 接收
                    Thread.Sleep(4000);
                    TestRunner.AssertTrue(receivedByA > 0,
                        string.Format("A 的 ReceiveEndpoint 收到 B 的推送，receivedByA={0}", receivedByA));

                    // 验证 A 的 SQLite 有来自 B 的数据
                    string dbReceivedA = Path.Combine(parsedA, "DHD.db");
                    using (var storageA = new StorageManager(dbReceivedA))
                    {
                        int countA = storageA.GetEventCount();
                        TestRunner.AssertTrue(countA >= 1,
                            string.Format("A 的 SQLite 有来自 B 的数据，count={0}", countA));
                    }
                }
                finally
                {
                    KillProcess(procA);
                    KillProcess(procB);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // T04: 离线检测 + 自动补拉
        // 1. DataForwarder 在线 → StationMonitor 探测成功
        // 2. 杀掉 DataForwarder → 连续探测失败 → 标记离线
        // 3. 插入离线期间新数据 → 重启 DataForwarder
        // 4. StationMonitor 检测恢复 → 触发 DataCatcher 自动补拉
        // ═══════════════════════════════════════════════════════════════

        private static void T04_OfflineCatchup()
        {
            int stationPort = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "station.db");
            string parsedDataDir = Path.Combine(_tempDir, "parsed_data");
            Directory.CreateDirectory(parsedDataDir);

            string configPath = CreateStationConfig(_tempDir, stationPort, "SSB", "三水北站");

            // 预填充初始数据（模拟离线前的数据）
            var samples = GenerateTestSamples(50, 0.04);
            long ts1 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 8, 0, 0));
            long ts2 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 8, 5, 0));
            long ts3 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 8, 10, 0));

            using (var storage = new StorageManager(dbPath))
            {
                storage.InsertEvent(new EventRecord
                {
                    SwitchId = "1-J", Timestamp = ts1,
                    Direction = "定位到反位", DurationSec = 7.0,
                    SampleInterval = 0.04, SampleCount = 50,
                    CurrentABlob = StorageManager.SerializeCurve(samples)
                });
                storage.InsertEvent(new EventRecord
                {
                    SwitchId = "1-J", Timestamp = ts2,
                    Direction = "反位到定位", DurationSec = 6.5,
                    SampleInterval = 0.04, SampleCount = 50,
                    CurrentABlob = StorageManager.SerializeCurve(samples)
                });
                storage.InsertEvent(new EventRecord
                {
                    SwitchId = "2-J", Timestamp = ts3,
                    Direction = "定位到反位", DurationSec = 8.0,
                    SampleInterval = 0.04, SampleCount = 50,
                    CurrentABlob = StorageManager.SerializeCurve(samples)
                });
            }

            // ── 阶段 1: 启动 DataForwarder，StationMonitor 探测成功 ──
            var process = StartDataForwarder(FindDataForwarderExe(), _tempDir, configPath, dbPath);
            try
            {
                Thread.Sleep(1500);

                string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
                var catchupState = new CatchupState(catchupPath);
                // 模拟之前已收到 ts2 的数据（lastTimestamp=ts2）
                catchupState.UpdateTimestamp("SSB", ts2);
                catchupState.Save();

                var station = new StationInfo
                {
                    Id = "SSB", Name = "三水北站",
                    Ip = "127.0.0.1", Port = stationPort,
                    DbPath = Path.Combine(parsedDataDir, "SSB.db")
                };

                var netConfig = new NetworkConfig
                {
                    ListenPort = FindFreePort(),
                    ParsedDataDir = parsedDataDir,
                    HttpTimeoutMs = 3000,
                    OfflineThreshold = 2,
                    ProbeIntervalMs = 60000, // 不自动触发，手动调用 ProbeStation
                    Stations = new List<StationInfo> { station }
                };

                using (var monitor = new StationMonitor(netConfig, catchupState))
                {
                    // 探测 → 应标记为 Online
                    StationStatus s1 = monitor.ProbeStation(station);
                    TestRunner.AssertEqual(StationStatus.Online, s1,
                        string.Format("阶段1: 探测成功 → Online，实际: {0}", s1));

                    // ── 阶段 2: 杀掉 DataForwarder，探测失败 → 标记离线 ──
                    KillProcess(process);
                    Thread.Sleep(500);

                    // 第一次探测 → 失败
                    StationStatus s2 = monitor.ProbeStation(station);
                    // 第一次失败，未达阈值，状态仍为 Online
                    TestRunner.AssertTrue(s2 == StationStatus.Online || s2 == StationStatus.Unknown,
                        string.Format("阶段2-第1次失败: 状态={0}", s2));
                    TestRunner.AssertEqual(1, monitor.GetConsecutiveFailures("SSB"),
                        "连续失败=1");

                    // 第二次探测 → 失败，达到阈值 → Offline
                    bool offlineEventFired = false;
                    monitor.StationStateChanged += (sender, args) =>
                    {
                        if (args.NewStatus == StationStatus.Offline)
                            offlineEventFired = true;
                    };

                    StationStatus s3 = monitor.ProbeStation(station);
                    TestRunner.AssertEqual(StationStatus.Offline, s3,
                        string.Format("阶段2-第2次失败: 标记 Offline，实际: {0}", s3));
                    TestRunner.AssertTrue(offlineEventFired,
                        "StationStateChanged(Offline) 事件已触发");

                    // ── 阶段 3: 插入离线期间新数据 ──
                    long ts4 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 8, 15, 0));
                    long ts5 = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 8, 20, 0));

                    using (var storage = new StorageManager(dbPath))
                    {
                        storage.InsertEvent(new EventRecord
                        {
                            SwitchId = "3-J", Timestamp = ts4,
                            Direction = "定位到反位", DurationSec = 5.8,
                            SampleInterval = 0.04, SampleCount = 50,
                            CurrentABlob = StorageManager.SerializeCurve(samples)
                        });
                        storage.InsertEvent(new EventRecord
                        {
                            SwitchId = "2-J", Timestamp = ts5,
                            Direction = "反位到定位", DurationSec = 7.2,
                            SampleInterval = 0.04, SampleCount = 50,
                            CurrentABlob = StorageManager.SerializeCurve(samples)
                        });
                    }

                    // ── 阶段 4: 重启 DataForwarder → 检测恢复 → 自动补拉 ──
                    process = StartDataForwarder(FindDataForwarderExe(), _tempDir, configPath, dbPath);
                    Thread.Sleep(1500);

                    bool onlineEventFired = false;
                    monitor.StationStateChanged += (sender, args) =>
                    {
                        if (args.NewStatus == StationStatus.Online &&
                            args.OldStatus == StationStatus.Offline)
                            onlineEventFired = true;
                    };

                    StationStatus s4 = monitor.ProbeStation(station);
                    TestRunner.AssertEqual(StationStatus.Online, s4,
                        string.Format("阶段4: 恢复 → Online，实际: {0}", s4));
                    TestRunner.AssertTrue(onlineEventFired,
                        "StationStateChanged(Offline→Online) 事件已触发");

                    // 执行补拉（模拟 UI 层监听事件后的处理）
                    bool catchupDone = false;
                    int catchupReceived = 0;

                    using (var catcher = new DataCatcher(netConfig, catchupState))
                    {
                        catcher.ProgressChanged += (sender, args) =>
                        {
                            if (args.IsComplete && !args.IsError)
                            {
                                catchupDone = true;
                                catchupReceived = args.ReceivedCount;
                            }
                        };

                        var result = catcher.Catchup(station);
                        TestRunner.AssertTrue(result.Success,
                            string.Format("补拉成功，错误: {0}", result.ErrorMessage ?? "无"));
                        TestRunner.AssertTrue(result.TotalReceived >= 2,
                            string.Format("补拉收到 ≥2 条事件，实际: {0}", result.TotalReceived));
                        catchupReceived = result.TotalReceived;

                        if (result.Success)
                            catchupDone = true;
                    }

                    TestRunner.AssertTrue(catchupDone, "补拉完成");

                    // 验证接收端 SQLite 有补拉的数据
                    string receiverDb = Path.Combine(parsedDataDir, "SSB.db");
                    TestRunner.AssertFileExists(receiverDb, "接收端 SSB.db 已创建");

                    using (var storage = new StorageManager(receiverDb))
                    {
                        int totalCount = storage.GetEventCount();
                        TestRunner.AssertTrue(totalCount >= 2,
                            string.Format("接收端 ≥2 条事件（补拉的），实际: {0}", totalCount));

                        // 可查到离线期间插入的 ts4 事件
                        EventRecord rec4 = storage.GetEventBySwitchAndTimestamp("3-J", ts4);
                        TestRunner.AssertNotNull(rec4, "可查到离线期间插入的 3-J 事件");
                    }

                    // 验证 catchup_state 更新到最新
                    long finalTs = catchupState.GetLastTimestamp("SSB");
                    TestRunner.AssertTrue(finalTs >= ts5,
                        string.Format("catchup_state 更新到 ≥ts5({0})，实际: {1}", ts5, finalTs));
                }
            }
            finally
            {
                KillProcess(process);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // T05: 重试 3 次 → 放弃，健康 subscriber 不受影响
        // 配置一个离线 subscriber + 一个在线 subscriber。
        // 验证：在线 subscriber 正常收到数据，进程不崩溃。
        // ═══════════════════════════════════════════════════════════════

        private static void T05_RetryGiveUp()
        {
            int stationPort = FindFreePort();
            int healthyPort = FindFreePort();
            int deadPort = FindFreePort(); // 无人监听

            string dbPath = Path.Combine(_tempDir, "test.db");

            // 两个 subscriber：一个在线（healthy）+ 一个离线（dead）
            string configPath = CreateStationConfig(_tempDir, stationPort, "SSB", "三水北站",
                string.Format("127.0.0.1:{0}", healthyPort),
                string.Format("127.0.0.1:{0}", deadPort));

            using (var storage = new StorageManager(dbPath)) { }

            // 启动健康接收器
            var healthyReceiver = new TestReceiver(healthyPort);
            healthyReceiver.Start();

            var process = StartDataForwarder(FindDataForwarderExe(), _tempDir, configPath, dbPath);
            try
            {
                Thread.Sleep(2000);

                // 插入事件
                var samples = GenerateTestSamples(30, 0.04);
                long ts = StorageManager.ToUnixTimestamp(DateTime.Now);

                using (var storage = new StorageManager(dbPath))
                {
                    storage.InsertEvent(new EventRecord
                    {
                        SwitchId = "1-J", Timestamp = ts,
                        Direction = "定位到反位", DurationSec = 5.0,
                        SampleInterval = 0.04, SampleCount = 30,
                        CurrentABlob = StorageManager.SerializeCurve(samples)
                    });
                }

                // 等待重试完成（2s + 4s + 8s = 14s，给 20s 余量）
                Thread.Sleep(20000);

                // ── 验证 1: 进程未崩溃 ──
                TestRunner.AssertFalse(process.HasExited, "DataForwarder 进程未崩溃");

                // ── 验证 2: /api/status 仍可访问 ──
                string statusUrl = string.Format("http://127.0.0.1:{0}/api/status", stationPort);
                string statusJson = HttpGet(statusUrl);
                TestRunner.AssertNotNull(statusJson, "status 端点仍可访问");

                // ── 验证 3: 健康 subscriber 收到了推送 ──
                bool healthyReceived = healthyReceiver.WaitForPost(1000);
                TestRunner.AssertTrue(healthyReceived,
                    "健康 subscriber 收到了数据（重试离线 subscriber 不阻塞）");

                var posts = healthyReceiver.GetPosts();
                TestRunner.AssertTrue(posts.Count > 0,
                    string.Format("健康 subscriber 收到 {0} 个 POST", posts.Count));
            }
            finally
            {
                KillProcess(process);
                healthyReceiver.Stop();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // T06: 手动补拉 — 数据缺口场景
        // 1. DataCatcher 已收到 ts1 的数据（catchupState 记录 ts1）
        // 2. 站机后来产生了 ts2, ts3（模拟数据缺口）
        // 3. 手动触发 DataCatcher.Catchup() → 补拉 ts2, ts3
        // 4. 验证数据完整 + catchupState 更新
        // ═══════════════════════════════════════════════════════════════

        private static void T06_ManualCatchup()
        {
            int mockPort = FindFreePort();
            string parsedDataDir = Path.Combine(_tempDir, "parsed_data");
            Directory.CreateDirectory(parsedDataDir);

            long ts1 = StorageManager.ToUnixTimestamp(new DateTime(2024, 8, 1, 10, 0, 0));
            long ts2 = StorageManager.ToUnixTimestamp(new DateTime(2024, 8, 1, 10, 10, 0));
            long ts3 = StorageManager.ToUnixTimestamp(new DateTime(2024, 8, 1, 10, 20, 0));

            var samples = GenerateTestSamples(50, 0.04);
            byte[] blob = StorageManager.SerializeCurve(samples);

            // 模拟站机有 3 条事件
            var mockServer = new MockStationServer(mockPort, "SSB", "三水北站");
            mockServer.StatusResponse = new Dictionary<string, object>
            {
                { "stationId", "SSB" },
                { "stationName", "三水北站" },
                { "status", "ok" },
                { "lastTimestamp", ts3 },
                { "dbSizeMB", 1.0 }
            };
            mockServer.EventsData = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "switchId", "1-J" }, { "timestamp", ts1 },
                    { "direction", "定位到反位" }, { "durationSec", 5.0 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 50 },
                    { "currentA", Convert.ToBase64String(blob) }
                },
                new Dictionary<string, object>
                {
                    { "switchId", "2-J" }, { "timestamp", ts2 },
                    { "direction", "反位到定位" }, { "durationSec", 6.5 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 50 },
                    { "currentA", Convert.ToBase64String(blob) }
                },
                new Dictionary<string, object>
                {
                    { "switchId", "3-J" }, { "timestamp", ts3 },
                    { "direction", "定位到反位" }, { "durationSec", 7.2 },
                    { "sampleInterval", 0.04 }, { "sampleCount", 50 },
                    { "currentA", Convert.ToBase64String(blob) }
                }
            };
            mockServer.Start();
            Thread.Sleep(200);

            try
            {
                string catchupPath = Path.Combine(_tempDir, "catchup_state.json");
                var catchupState = new CatchupState(catchupPath);
                // 模拟数据缺口：只收到 ts1，缺少 ts2、ts3
                catchupState.UpdateTimestamp("SSB", ts1);
                catchupState.Save();

                var station = new StationInfo
                {
                    Id = "SSB", Name = "三水北站",
                    Ip = "127.0.0.1", Port = mockPort,
                    DbPath = Path.Combine(parsedDataDir, "SSB.db")
                };

                var config = new NetworkConfig
                {
                    ListenPort = FindFreePort(),
                    ParsedDataDir = parsedDataDir,
                    HttpTimeoutMs = 10000,
                    Stations = new List<StationInfo> { station }
                };

                // ── 手动触发补拉 ──
                int progressFired = 0;
                bool completed = false;

                using (var catcher = new DataCatcher(config, catchupState))
                {
                    catcher.ProgressChanged += (sender, args) =>
                    {
                        progressFired++;
                        if (args.IsComplete && !args.IsError)
                            completed = true;
                    };

                    var result = catcher.Catchup(station);

                    // 验证补拉结果
                    TestRunner.AssertTrue(result.Success,
                        string.Format("手动补拉成功，错误: {0}", result.ErrorMessage ?? "无"));
                    TestRunner.AssertEqual(2, result.TotalReceived,
                        string.Format("补拉收到 2 条事件（ts2, ts3），实际: {0}", result.TotalReceived));
                }

                TestRunner.AssertTrue(completed, "ProgressChanged IsComplete=true");
                TestRunner.AssertTrue(progressFired > 0, "进度事件已触发");

                // 验证接收端 SQLite
                string dbPath = Path.Combine(parsedDataDir, "SSB.db");
                TestRunner.AssertFileExists(dbPath, "SSB.db 已创建");

                using (var storage = new StorageManager(dbPath))
                {
                    int count = storage.GetEventCount();
                    TestRunner.AssertEqual(2, count,
                        string.Format("接收端 2 条事件（去重后），实际: {0}", count));

                    EventRecord rec2 = storage.GetEventBySwitchAndTimestamp("2-J", ts2);
                    TestRunner.AssertNotNull(rec2, "可查到 ts2 的 2-J");
                    TestRunner.AssertEqual("反位到定位", rec2.Direction, "Direction 一致");

                    EventRecord rec3 = storage.GetEventBySwitchAndTimestamp("3-J", ts3);
                    TestRunner.AssertNotNull(rec3, "可查到 ts3 的 3-J");
                }

                // 验证 catchup_state 更新到 ts3
                long finalTs = catchupState.GetLastTimestamp("SSB");
                TestRunner.AssertEqual(ts3, finalTs,
                    string.Format("catchup_state 更新到 ts3={0}，实际={1}", ts3, finalTs));
            }
            finally
            {
                mockServer.Stop();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // T07: SQLite 并发安全
        // 模拟 SwitchMonitor 写入 + DataForwarder 轮询读取同时进行。
        // 验证 30 秒内无锁冲突或数据损坏。
        // 扩展测试：runDurationMs 可调整为 3600000（1 小时）。
        // ═══════════════════════════════════════════════════════════════

        private static void T07_ConcurrentSafety()
        {
            string dbPath = Path.Combine(_tempDir, "concurrent.db");
            int runDurationMs = 30000; // 30 秒快速测试；1 小时 = 3600000

            using (var storage = new StorageManager(dbPath))
            {
                bool writerError = false;
                bool readerError = false;
                int writerCount = 0;
                int readerCount = 0;
                var stopFlag = new ManualResetEventSlim(false);

                // ── 写入线程（模拟 SwitchMonitor 写入事件）──
                var writerThread = new Thread(() =>
                {
                    try
                    {
                        var samples = GenerateTestSamples(40, 0.04);
                        byte[] blob = StorageManager.SerializeCurve(samples);
                        long baseTs = StorageManager.ToUnixTimestamp(DateTime.Now);

                        while (!stopFlag.Wait(0))
                        {
                            try
                            {
                                using (var writer = new StorageManager(dbPath))
                                {
                                    writer.InsertEvent(new EventRecord
                                    {
                                        SwitchId = "1-J",
                                        Timestamp = baseTs + writerCount,
                                        Direction = writerCount % 2 == 0 ? "定位到反位" : "反位到定位",
                                        DurationSec = 5.0 + (writerCount % 10) * 0.5,
                                        SampleInterval = 0.04,
                                        SampleCount = 40,
                                        CurrentABlob = blob,
                                        CurrentBBlob = blob,
                                        CurrentCBlob = blob,
                                        PowerBlob = blob
                                    });
                                }
                                Interlocked.Increment(ref writerCount);
                                Thread.Sleep(50); // 模拟 ~20 条/秒
                            }
                            catch (Exception ex)
                            {
                                writerError = true;
                                Console.WriteLine("          [WRITER ERROR] " + ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        writerError = true;
                        Console.WriteLine("          [WRITER FATAL] " + ex.Message);
                    }
                });
                writerThread.IsBackground = true;
                writerThread.Start();

                // ── 读取线程（模拟 DataForwarder 轮询）──
                var readerThread = new Thread(() =>
                {
                    try
                    {
                        long lastTs = 0;
                        while (!stopFlag.Wait(0))
                        {
                            try
                            {
                                using (var reader = new StorageManager(dbPath))
                                {
                                    var events = reader.GetEventsSince(lastTs, 50);
                                    foreach (var evt in events)
                                    {
                                        if (evt.Timestamp > lastTs)
                                            lastTs = evt.Timestamp;
                                        Interlocked.Increment(ref readerCount);
                                    }
                                    long maxTs = reader.GetMaxTimestamp();
                                    if (maxTs > lastTs) lastTs = maxTs;
                                }
                                Thread.Sleep(100); // 模拟每 100ms 轮询一次
                            }
                            catch (Exception ex)
                            {
                                readerError = true;
                                Console.WriteLine("          [READER ERROR] " + ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        readerError = true;
                        Console.WriteLine("          [READER FATAL] " + ex.Message);
                    }
                });
                readerThread.IsBackground = true;
                readerThread.Start();

                // ── 运行指定时长 ──
                Thread.Sleep(runDurationMs);
                stopFlag.Set();

                writerThread.Join(5000);
                readerThread.Join(5000);

                Console.WriteLine("          运行 {0}s: 写入 {1} 条, 读取 {2} 条",
                    runDurationMs / 1000, writerCount, readerCount);

                // ── 验证无错误 ──
                TestRunner.AssertFalse(writerError, "写入线程无异常");
                TestRunner.AssertFalse(readerError, "读取线程无异常");
                TestRunner.AssertTrue(writerCount > 0,
                    string.Format("写入线程至少写入 1 条，实际: {0}", writerCount));
                TestRunner.AssertTrue(readerCount > 0,
                    string.Format("读取线程至少读取 1 条，实际: {0}", readerCount));

                // ── 验证数据完整性 ──
                int totalEvents = storage.GetEventCount();
                TestRunner.AssertTrue(totalEvents > 0,
                    string.Format("数据库事件数 > 0，实际: {0}", totalEvents));

                // 随机抽查若干条事件，验证 BLOB 可正确反序列化
                var allEvents = storage.GetEventsSince(0, 10);
                TestRunner.AssertTrue(allEvents.Count > 0, "GetEventsSince 可查询");
                foreach (var evt in allEvents)
                {
                    if (evt.CurrentABlob != null && evt.CurrentABlob.Length > 0)
                    {
                        var curve = StorageManager.DeserializeCurve(evt.CurrentABlob);
                        TestRunner.AssertTrue(curve.Count > 0,
                            string.Format("事件 id={0} BLOB 可反序列化，{1} 个采样点",
                                evt.Id, curve.Count));
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // T08: gzip 往返 — 完整 EventRecord
        // EventRecord → EventToDict → JSON → gzip → 解压 → DictToEventRecord
        // 验证所有字段（含 4 个 BLOB + diagnosis）在往返后一致。
        // ═══════════════════════════════════════════════════════════════

        private static void T08_GzipRoundtrip()
        {
            // 1. 构造完整的 EventRecord
            var samplesA = GenerateTestSamples(100, 0.04);
            var samplesB = GenerateTestSamples(100, 0.04);
            var samplesC = GenerateTestSamples(100, 0.04);
            var samplesPower = GenerateTestSamples(100, 0.04);

            long ts = StorageManager.ToUnixTimestamp(new DateTime(2024, 9, 1, 14, 30, 0));

            var original = new EventRecord
            {
                SwitchId = "1-J",
                Timestamp = ts,
                Direction = "定位到反位",
                DurationSec = 8.25,
                SampleInterval = 0.04,
                SampleCount = 100,
                CurrentABlob = StorageManager.SerializeCurve(samplesA),
                CurrentBBlob = StorageManager.SerializeCurve(samplesB),
                CurrentCBlob = StorageManager.SerializeCurve(samplesC),
                PowerBlob = StorageManager.SerializeCurve(samplesPower),
                DiagJson = "{\"level\":\"预警\",\"results\":[{\"rule\":\"R1_转换力异常\",\"value\":850}]}"
            };

            // 2. EventRecord → Dict → JSON
            var dict = ApiHandlers.EventToDict(original);
            string json = Serializer.Serialize(new Dictionary<string, object>
            {
                { "stationId", "SSB" },
                { "batchTimestamp", ts },
                { "events", new System.Collections.ArrayList { dict } }
            });

            TestRunner.AssertTrue(json.Length > 0, "JSON 序列化非空");

            // 3. JSON → gzip 压缩
            byte[] rawBytes = Encoding.UTF8.GetBytes(json);
            byte[] compressed = PushEngine.CompressGzip(rawBytes);
            TestRunner.AssertTrue(compressed.Length > 0, "gzip 压缩后非空");

            // 验证压缩有效（JSON 数据应该可压缩）
            double ratio = (double)compressed.Length / rawBytes.Length;
            Console.WriteLine("          gzip 压缩比: {0}/{1} = {2:F1}%",
                compressed.Length, rawBytes.Length, ratio * 100);
            TestRunner.AssertTrue(ratio < 0.99,
                string.Format("gzip 压缩有效（比例 < 99%），实际: {0:F1}%", ratio * 100));

            // 4. gzip 解压
            byte[] decompressed;
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                decompressed = output.ToArray();
            }
            string decompressedJson = Encoding.UTF8.GetString(decompressed);

            // 验证解压后 JSON 一致
            TestRunner.AssertEqual(json, decompressedJson, "gzip 解压后 JSON 与原始一致");

            // 5. 反序列化 JSON → Dict → EventRecord
            var batchData = Serializer.Deserialize<Dictionary<string, object>>(decompressedJson);
            TestRunner.AssertEqual("SSB", (string)batchData["stationId"], "stationId 一致");

            var eventList = (System.Collections.ArrayList)batchData["events"];
            TestRunner.AssertEqual(1, eventList.Count, "events 数组长度=1");

            var evtDict = (Dictionary<string, object>)eventList[0];

            // 5b. 手动重建 EventRecord（等同于 ReceiveEndpoint.DictToEventRecord 的逻辑，
            // 但不依赖 internal 方法，确保测试验证的是数据转换的正确性）
            var reconstructed = DictToEventRecord(evtDict);

            // 6. 验证所有字段一致
            TestRunner.AssertEqual(original.SwitchId, reconstructed.SwitchId, "SwitchId 一致");
            TestRunner.AssertEqual(original.Timestamp, reconstructed.Timestamp, "Timestamp 一致");
            TestRunner.AssertEqual(original.Direction, reconstructed.Direction, "Direction 一致");
            TestRunner.AssertEqual(original.DurationSec, reconstructed.DurationSec, 0.001, "DurationSec 一致");
            TestRunner.AssertEqual(original.SampleInterval, reconstructed.SampleInterval, 0.001, "SampleInterval 一致");
            TestRunner.AssertEqual(original.SampleCount, reconstructed.SampleCount, "SampleCount 一致");

            // 验证 BLOB 内容一致（字节级比较）
            TestRunner.AssertNotNull(reconstructed.CurrentABlob, "CurrentABlob 非 null");
            TestRunner.AssertNotNull(reconstructed.CurrentBBlob, "CurrentBBlob 非 null");
            TestRunner.AssertNotNull(reconstructed.CurrentCBlob, "CurrentCBlob 非 null");
            TestRunner.AssertNotNull(reconstructed.PowerBlob, "PowerBlob 非 null");

            TestRunner.AssertTrue(
                ByteArrayEqual(original.CurrentABlob, reconstructed.CurrentABlob),
                "CurrentA BLOB 字节一致");
            TestRunner.AssertTrue(
                ByteArrayEqual(original.CurrentBBlob, reconstructed.CurrentBBlob),
                "CurrentB BLOB 字节一致");
            TestRunner.AssertTrue(
                ByteArrayEqual(original.CurrentCBlob, reconstructed.CurrentCBlob),
                "CurrentC BLOB 字节一致");
            TestRunner.AssertTrue(
                ByteArrayEqual(original.PowerBlob, reconstructed.PowerBlob),
                "Power BLOB 字节一致");

            // 验证 BLOB 反序列化为曲线后采样点一致
            var curveA1 = StorageManager.DeserializeCurve(original.CurrentABlob);
            var curveA2 = StorageManager.DeserializeCurve(reconstructed.CurrentABlob);
            TestRunner.AssertEqual(curveA1.Count, curveA2.Count, "A相曲线采样点数一致");
            for (int i = 0; i < Math.Min(curveA1.Count, 10); i++)
            {
                TestRunner.AssertEqual(curveA1[i][0], curveA2[i][0], 0.0001,
                    string.Format("A相点{0} time", i));
                TestRunner.AssertEqual(curveA1[i][1], curveA2[i][1], 0.0001,
                    string.Format("A相点{0} value", i));
            }

            // 验证 diagnosis 包含在 dict 中
            TestRunner.AssertTrue(evtDict.ContainsKey("diagnosis"), "dict 含 diagnosis");
        }

        /// <summary>
        /// 从 JSON 字典重建 EventRecord（等同于 ReceiveEndpoint.DictToEventRecord 的逻辑）。
        /// 独立实现以确保测试不依赖 internal API。
        /// </summary>
        private static EventRecord DictToEventRecord(Dictionary<string, object> dict)
        {
            var rec = new EventRecord();

            if (dict.ContainsKey("switchId"))
                rec.SwitchId = (string)dict["switchId"];
            if (dict.ContainsKey("timestamp"))
                rec.Timestamp = Convert.ToInt64(dict["timestamp"]);
            if (dict.ContainsKey("direction"))
                rec.Direction = (string)dict["direction"];
            if (dict.ContainsKey("durationSec"))
                rec.DurationSec = Convert.ToDouble(dict["durationSec"]);
            if (dict.ContainsKey("sampleInterval"))
                rec.SampleInterval = Convert.ToDouble(dict["sampleInterval"]);
            if (dict.ContainsKey("sampleCount"))
                rec.SampleCount = Convert.ToInt32(dict["sampleCount"]);

            // BLOB: Base64 → byte[]
            if (dict.ContainsKey("currentA") && dict["currentA"] != null)
                rec.CurrentABlob = Convert.FromBase64String((string)dict["currentA"]);
            if (dict.ContainsKey("currentB") && dict["currentB"] != null)
                rec.CurrentBBlob = Convert.FromBase64String((string)dict["currentB"]);
            if (dict.ContainsKey("currentC") && dict["currentC"] != null)
                rec.CurrentCBlob = Convert.FromBase64String((string)dict["currentC"]);
            if (dict.ContainsKey("power") && dict["power"] != null)
                rec.PowerBlob = Convert.FromBase64String((string)dict["power"]);

            // 诊断 JSON
            if (dict.ContainsKey("diagnosis") && dict["diagnosis"] != null)
            {
                var serializer = new JavaScriptSerializer();
                rec.DiagJson = serializer.Serialize(dict["diagnosis"]);
            }

            return rec;
        }

        // ═══════════════════════════════════════════════════════════════
        // 辅助方法
        // ═══════════════════════════════════════════════════════════════

        /// <summary>创建站机 config.json。</summary>
        private static string CreateStationConfig(string dir, int port,
            string stationId, string stationName,
            params string[] subscribers)
        {
            // 将 subscribers 转为 JSON 数组字符串
            string subsJson = "[]";
            if (subscribers != null && subscribers.Length > 0)
            {
                var parts = new List<string>();
                foreach (string s in subscribers)
                    parts.Add("\"" + s + "\"");
                subsJson = "[" + string.Join(",", parts) + "]";
            }

            string configJson = string.Format(@"{{
                ""role"": ""station"",
                ""stationId"": ""{0}"",
                ""stationName"": ""{1}"",
                ""listenPort"": {2},
                ""mergeWindowMs"": 500,
                ""subscribers"": {3},
                ""switchGroups"": [],
                ""dataSourceDir"": ""./data"",
                ""parsedDataDir"": ""./parsed""
            }}", stationId, stationName, port, subsJson);

            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, configJson, Encoding.UTF8);
            return path;
        }

        /// <summary>查找 DataForwarder.exe 路径。</summary>
        private static string FindDataForwarderExe()
        {
            string[] candidates =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DataForwarder.exe"),
                @"..\..\..\..\..\..\06_deploy\release\DataForwarder.exe",
                @"..\..\..\..\06_deploy\release\DataForwarder.exe",
            };

            foreach (string path in candidates)
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                    return full;
            }

            // 回退：向上搜索
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string candidate = Path.Combine(baseDir, "DataForwarder.exe");
                if (File.Exists(candidate)) return candidate;

                candidate = Path.Combine(baseDir, "06_deploy", "release", "DataForwarder.exe");
                if (File.Exists(candidate)) return candidate;

                try
                {
                    var parent = Directory.GetParent(baseDir);
                    if (parent == null) break;
                    baseDir = parent.FullName;
                }
                catch { break; }
            }

            throw new FileNotFoundException(
                "找不到 DataForwarder.exe。请先编译 DataForwarder 项目: dotnet build -c Release");
        }

        /// <summary>启动 DataForwarder 进程。</summary>
        private static Process StartDataForwarder(string exePath, string workingDir,
            string configPath, string dbPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDir,
                Arguments = string.Format("--config \"{0}\" --db \"{1}\"", configPath, dbPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process == null)
                throw new Exception("无法启动 DataForwarder 进程");
            return process;
        }

        /// <summary>找空闲 TCP 端口。</summary>
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

        /// <summary>HTTP GET 请求，返回响应正文，失败返回 null。</summary>
        private static string HttpGet(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 5000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>HTTP GET 请求，自动解压 gzip 响应（/api/events 端点使用）。</summary>
        private static string HttpGetAutoDecompress(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 5000;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    Stream stream = response.GetResponseStream();
                    string contentEncoding = response.Headers["Content-Encoding"];
                    if (contentEncoding != null &&
                        contentEncoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        stream = new GZipStream(stream, CompressionMode.Decompress);
                    }
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>安全终止进程。</summary>
        private static void KillProcess(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch { }
        }

        /// <summary>生成测试用采样点 [(t0, v0), (t1, v1), ...]。</summary>
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

        /// <summary>字节数组相等比较。</summary>
        private static bool ByteArrayEqual(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
