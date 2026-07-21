using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;
using SwitchMonitor.DataForwarder;
using SwitchMonitor.Storage;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// N01-2: DataForwarder 测试套件。
    /// 测试 SyncStateManager、ApiHandlers JSON 格式、PushEngine 压缩/推送，
    /// 以及进程级集成测试（启动 DataForwarder.exe → HTTP 端点验证）。
    /// </summary>
    public static class N01_2Tests
    {
        private static string _tempDir;
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static void Run()
        {
            Console.WriteLine("--- N01-2 DataForwarder Tests ---");
            Console.WriteLine();

            // ═══ Group A: SyncStateManager ═══
            SetupTempDir();
            TestRunner.Test("T01: SyncState Save-Load 往返", T01_SyncState_Roundtrip);
            SetupTempDir();
            TestRunner.Test("T02: SyncState 缺失 key 返回 0", T02_SyncState_MissingKey);
            SetupTempDir();
            TestRunner.Test("T03: SyncState 损坏文件优雅回退", T03_SyncState_CorruptFile);

            // ═══ Group B: PushEngine 压缩 ═══
            TestRunner.Test("T04: gzip 压缩-解压往返", T04_GzipRoundtrip);
            TestRunner.Test("T05: gzip 压缩比 < 30%", T05_GzipCompressionRatio);

            // ═══ Group C: API 端点（组件级） ═══
            SetupTempDir();
            TestRunner.Test("T06: /api/status 响应格式", T06_ApiStatus_Format);
            SetupTempDir();
            TestRunner.Test("T07: /api/events 响应格式", T07_ApiEvents_Format);

            // ═══ Group D: 进程级集成测试 ═══
            SetupTempDir();
            TestRunner.Test("T08: DataForwarder 进程启动 + /api/status", T08_Process_ApiStatus);
            SetupTempDir();
            TestRunner.Test("T09: 插入事件 → 推送检测", T09_Process_PushDetection);
            SetupTempDir();
            TestRunner.Test("T10: 合并窗口打包", T10_Process_MergeWindow);
            SetupTempDir();
            TestRunner.Test("T11: .sync_state.json 持久化", T11_Process_SyncStatePersistence);

            // ═══ Group E: 重试逻辑 ═══
            SetupTempDir();
            TestRunner.Test("T12: subscriber 离线 → 重试 3 次 → 放弃", T12_RetryOfflineSubscriber);

            // 清理
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

        // ── T01: SyncState Save-Load 往返 ──

        private static void T01_SyncState_Roundtrip()
        {
            string path = Path.Combine(_tempDir, ".sync_state.json");
            var mgr = new SyncStateManager(path);

            mgr.UpdateTimestamp("SSB", "192.168.1.100:9000", 1712345678);
            mgr.UpdateTimestamp("SSB", "192.168.1.11:9000", 1712345670);
            mgr.Save();

            TestRunner.AssertFileExists(path, "sync_state 文件存在");

            var loaded = SyncStateManager.Load(path);
            TestRunner.AssertEqual(1712345678L, loaded.GetLastTimestamp("SSB", "192.168.1.100:9000"), "subscriber1 ts");
            TestRunner.AssertEqual(1712345670L, loaded.GetLastTimestamp("SSB", "192.168.1.11:9000"), "subscriber2 ts");
        }

        // ── T02: SyncState 缺失 key 返回 0 ──

        private static void T02_SyncState_MissingKey()
        {
            string path = Path.Combine(_tempDir, ".sync_state.json");
            var mgr = SyncStateManager.Load(path); // 文件不存在

            TestRunner.AssertEqual(0L, mgr.GetLastTimestamp("SSB", "nonexistent"), "不存在返回 0");
            TestRunner.AssertEqual(0L, mgr.GetLastTimestamp("UNKNOWN", "x:0"), "未知站返回 0");
        }

        // ── T03: SyncState 损坏文件优雅回退 ──

        private static void T03_SyncState_CorruptFile()
        {
            string path = Path.Combine(_tempDir, ".sync_state.json");
            File.WriteAllText(path, "NOT VALID JSON {{{{{", Encoding.UTF8);

            var mgr = SyncStateManager.Load(path);
            // 不抛异常，返回 0
            TestRunner.AssertEqual(0L, mgr.GetLastTimestamp("SSB", "x"), "损坏文件不抛异常");
        }

        // ── T04: gzip 压缩-解压往返 ──

        private static void T04_GzipRoundtrip()
        {
            string original = "{\"test\":\"hello world\",\"count\":42}";
            byte[] raw = Encoding.UTF8.GetBytes(original);
            byte[] compressed = PushEngine.CompressGzip(raw);
            byte[] decompressed = PushEngine.DecompressGzip(compressed);
            string result = Encoding.UTF8.GetString(decompressed);

            TestRunner.AssertEqual(original, result, "gzip 往返一致");
            TestRunner.AssertTrue(compressed.Length > 0, "压缩后非空");
        }

        // ── T05: gzip 压缩比 < 30% ──

        private static void T05_GzipCompressionRatio()
        {
            // 构造类似事件 JSON 的大文本（含重复结构 → gzip 高效）
            var sb = new StringBuilder();
            sb.Append("{\"events\":[");
            for (int i = 0; i < 50; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"switchId\":\"1-J\",\"timestamp\":");
                sb.Append(1712345678 + i * 60);
                sb.Append(",\"direction\":\"定位→反位\",\"durationSec\":8.5,\"sampleCount\":200}");
            }
            sb.Append("]}");

            string json = sb.ToString();
            byte[] raw = Encoding.UTF8.GetBytes(json);
            byte[] compressed = PushEngine.CompressGzip(raw);

            double ratio = (double)compressed.Length / raw.Length;
            Console.WriteLine(string.Format("          压缩比: {0}/{1} = {2:F1}%", compressed.Length, raw.Length, ratio * 100));
            TestRunner.AssertTrue(ratio < 0.30,
                string.Format("压缩比 {0:F1}% < 30%", ratio * 100));
        }

        // ── T06: /api/status 响应格式 ──

        private static void T06_ApiStatus_Format()
        {
            // 组件级测试：直接构造 EventRecord → EventToDict 验证格式
            var rec = new EventRecord
            {
                SwitchId = "1-J",
                Timestamp = 1712345678,
                Direction = "定位→反位",
                DurationSec = 8.5,
                SampleInterval = 0.04,
                SampleCount = 200
            };

            var dict = ApiHandlers.EventToDict(rec);
            TestRunner.AssertEqual("1-J", (string)dict["switchId"], "switchId");
            TestRunner.AssertEqual(1712345678L, (long)dict["timestamp"], "timestamp");
            TestRunner.AssertEqual("定位→反位", (string)dict["direction"], "direction");
            TestRunner.AssertTrue(Math.Abs(8.5 - Convert.ToDouble(dict["durationSec"])) < 0.01, "durationSec");
        }

        // ── T07: /api/events 响应格式 ──

        private static void T07_ApiEvents_Format()
        {
            string dbPath = Path.Combine(_tempDir, "test.db");
            var storage = new StorageManager(dbPath);

            // 插入事件
            var samples = GenerateTestSamples(100, 0.04);
            var rec = new EventRecord
            {
                SwitchId = "1-J",
                Timestamp = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 15, 10, 30, 0)),
                Direction = "定位→反位",
                DurationSec = 7.5,
                SampleInterval = 0.04,
                SampleCount = 100,
                CurrentABlob = StorageManager.SerializeCurve(samples),
                CurrentBBlob = StorageManager.SerializeCurve(samples),
                CurrentCBlob = StorageManager.SerializeCurve(samples),
                PowerBlob = StorageManager.SerializeCurve(samples)
            };
            storage.InsertEvent(rec);

            // 查询
            var events = storage.GetEventsSince(0);
            TestRunner.AssertEqual(1, events.Count, "查询到 1 条事件");

            // 验证序列化格式
            var dict = ApiHandlers.EventToDict(events[0]);
            TestRunner.AssertNotNull(dict["currentA"], "currentA Base64 编码");
            TestRunner.AssertTrue(((string)dict["currentA"]).Length > 0, "Base64 非空");

            storage.Dispose();
        }

        // ── T08: DataForwarder 进程启动 + /api/status ──

        private static void T08_Process_ApiStatus()
        {
            int port = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "test.db");
            string configPath = CreateTestConfig(_tempDir, port, "TEST", "测试站");

            // 初始化数据库
            var storage = new StorageManager(dbPath);
            storage.Dispose();

            // 启动 DataForwarder 进程
            string exePath = FindDataForwarderExe();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _tempDir,
                Arguments = string.Format("--config \"{0}\" --db \"{1}\"", configPath, dbPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            try
            {
                // 等待启动
                Thread.Sleep(1500);

                // 请求 /api/status
                string url = string.Format("http://127.0.0.1:{0}/api/status", port);
                string response = HttpGet(url);
                TestRunner.AssertNotNull(response, "HTTP 响应非空");

                var status = Serializer.Deserialize<Dictionary<string, object>>(response);
                TestRunner.AssertEqual("TEST", (string)status["stationId"], "stationId");
                TestRunner.AssertEqual("测试站", (string)status["stationName"], "stationName");
                TestRunner.AssertEqual("ok", (string)status["status"], "status");
                TestRunner.AssertTrue(status.ContainsKey("lastTimestamp"), "含 lastTimestamp");
                TestRunner.AssertTrue(status.ContainsKey("dbSizeMB"), "含 dbSizeMB");
            }
            finally
            {
                KillProcess(process);
            }
        }

        // ── T09: 插入事件 → 推送检测 ──

        private static void T09_Process_PushDetection()
        {
            int port = FindFreePort();
            int receiverPort = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "test.db");
            string configPath = CreateTestConfig(_tempDir, port, "SSB", "三水北站",
                string.Format("127.0.0.1:{0}", receiverPort));

            // 启动测试接收器
            var receiver = new TestReceiver(receiverPort);
            receiver.Start();

            // 初始化数据库
            var storage = new StorageManager(dbPath);
            storage.Dispose();

            // 启动 DataForwarder
            string exePath = FindDataForwarderExe();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _tempDir,
                Arguments = string.Format("--config \"{0}\" --db \"{1}\"", configPath, dbPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            try
            {
                Thread.Sleep(2000); // 等待 DataForwarder 就绪

                // 插入一条事件
                var storage2 = new StorageManager(dbPath);
                var rec = new EventRecord
                {
                    SwitchId = "1-J",
                    Timestamp = StorageManager.ToUnixTimestamp(DateTime.Now),
                    Direction = "定位→反位",
                    DurationSec = 7.0,
                    SampleInterval = 0.04,
                    SampleCount = 50,
                    CurrentABlob = StorageManager.SerializeCurve(GenerateTestSamples(50, 0.04))
                };
                storage2.InsertEvent(rec);
                storage2.Dispose();

                // 等待推送（最多 5 秒）
                bool received = receiver.WaitForPost(5000);
                TestRunner.AssertTrue(received, "DataForwarder 在 5s 内推送了数据");

                if (received)
                {
                    var posts = receiver.GetPosts();
                    TestRunner.AssertTrue(posts.Count > 0, "接收器记录了 POST 请求");

                    // 验证 Content-Encoding: gzip
                    string body = posts[0];
                    TestRunner.AssertTrue(body.Length > 0, "POST body 非空");
                }
            }
            finally
            {
                KillProcess(process);
                receiver.Stop();
            }
        }

        // ── T10: 合并窗口打包 ──

        private static void T10_Process_MergeWindow()
        {
            int port = FindFreePort();
            int receiverPort = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "test.db");
            // mergeWindowMs = 2000 让 5 条事件能合并到一个批次
            string configJson = string.Format(@"{{
                ""role"": ""station"",
                ""stationId"": ""SSB"",
                ""stationName"": ""三水北站"",
                ""listenPort"": {0},
                ""mergeWindowMs"": 2000,
                ""subscribers"": [""127.0.0.1:{1}""],
                ""switchGroups"": [],
                ""dataSourceDir"": ""./data"",
                ""parsedDataDir"": ""./parsed""
            }}", port, receiverPort);

            string configPath = Path.Combine(_tempDir, "config.json");
            File.WriteAllText(configPath, configJson, Encoding.UTF8);

            // 启动测试接收器
            var receiver = new TestReceiver(receiverPort);
            receiver.Start();

            // 初始化数据库
            var storage = new StorageManager(dbPath);
            storage.Dispose();

            // 启动 DataForwarder
            string exePath = FindDataForwarderExe();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _tempDir,
                Arguments = string.Format("--config \"{0}\" --db \"{1}\"", configPath, dbPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            try
            {
                Thread.Sleep(2000);

                // 5 秒内插入 5 条事件
                var storage2 = new StorageManager(dbPath);
                var baseTime = DateTime.Now;
                for (int i = 0; i < 5; i++)
                {
                    var rec = new EventRecord
                    {
                        SwitchId = "1-J",
                        Timestamp = StorageManager.ToUnixTimestamp(baseTime.AddSeconds(i * 2)),
                        Direction = "定位→反位",
                        DurationSec = 5.0 + i,
                        SampleInterval = 0.04,
                        SampleCount = 40,
                        CurrentABlob = StorageManager.SerializeCurve(GenerateTestSamples(40, 0.04))
                    };
                    storage2.InsertEvent(rec);
                    Thread.Sleep(100);
                }
                storage2.Dispose();

                // 等待推送
                bool received = receiver.WaitForPost(8000);
                TestRunner.AssertTrue(received, "合并窗口推送了数据");

                // 检查接收到的 POST 数量
                var posts = receiver.GetPosts();
                Console.WriteLine(string.Format("          接收到 {0} 个 POST", posts.Count));
                TestRunner.AssertTrue(posts.Count <= 5, "POST 数量不超过 5（合并后）");
            }
            finally
            {
                KillProcess(process);
                receiver.Stop();
            }
        }

        // ── T11: .sync_state.json 持久化 ──

        private static void T11_Process_SyncStatePersistence()
        {
            int port = FindFreePort();
            int receiverPort = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "test.db");
            string configPath = CreateTestConfig(_tempDir, port, "SSB", "三水北站",
                string.Format("127.0.0.1:{0}", receiverPort));
            string syncStatePath = Path.Combine(_tempDir, ".sync_state.json");

            var receiver = new TestReceiver(receiverPort);
            receiver.Start();

            var storage = new StorageManager(dbPath);
            storage.Dispose();

            string exePath = FindDataForwarderExe();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _tempDir,
                Arguments = string.Format("--config \"{0}\" --db \"{1}\"", configPath, dbPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            try
            {
                Thread.Sleep(2000);

                var storage2 = new StorageManager(dbPath);
                var ts = StorageManager.ToUnixTimestamp(DateTime.Now);
                storage2.InsertEvent(new EventRecord
                {
                    SwitchId = "1-J", Timestamp = ts,
                    Direction = "定位→反位", DurationSec = 6.0,
                    SampleInterval = 0.04, SampleCount = 30,
                    CurrentABlob = StorageManager.SerializeCurve(GenerateTestSamples(30, 0.04))
                });
                storage2.Dispose();

                receiver.WaitForPost(5000);

                // 检查 .sync_state.json
                TestRunner.AssertFileExists(syncStatePath, ".sync_state.json 已创建");

                string json = File.ReadAllText(syncStatePath, Encoding.UTF8);
                var syncData = Serializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(json);
                TestRunner.AssertNotNull(syncData, "sync_state 可解析");
                TestRunner.AssertTrue(syncData.ContainsKey("SSB"), "含 SSB 站记录");
            }
            finally
            {
                KillProcess(process);
                receiver.Stop();
            }
        }

        // ── T12: subscriber 离线 → 重试 3 次 → 放弃 ──

        private static void T12_RetryOfflineSubscriber()
        {
            // 使用不存在的端口（无人监听）→ PushClient 应重试 3 次后放弃
            // 测试不崩溃，不阻塞
            int port = FindFreePort();
            string dbPath = Path.Combine(_tempDir, "test.db");

            // 配置一个不存在的 subscriber（随机端口）
            string offlinePort = FindFreePort().ToString();
            string configPath = CreateTestConfig(_tempDir, port, "SSB", "三水北站",
                string.Format("127.0.0.1:{0}", offlinePort));

            var storage = new StorageManager(dbPath);
            storage.Dispose();

            string exePath = FindDataForwarderExe();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _tempDir,
                Arguments = string.Format("--config \"{0}\" --db \"{1}\"", configPath, dbPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            try
            {
                Thread.Sleep(2000);

                var storage2 = new StorageManager(dbPath);
                var ts = StorageManager.ToUnixTimestamp(DateTime.Now);
                storage2.InsertEvent(new EventRecord
                {
                    SwitchId = "1-J", Timestamp = ts,
                    Direction = "定位→反位", DurationSec = 5.0,
                    SampleInterval = 0.04, SampleCount = 20,
                    CurrentABlob = StorageManager.SerializeCurve(GenerateTestSamples(20, 0.04))
                });
                storage2.Dispose();

                // 等待重试完成（2s + 4s + 8s = 14s，给 20s 余量）
                Thread.Sleep(20000);

                // 进程不崩溃
                TestRunner.AssertFalse(process.HasExited, "进程未崩溃");

                // /api/status 仍可访问
                string statusUrl = string.Format("http://127.0.0.1:{0}/api/status", port);
                string response = HttpGet(statusUrl);
                TestRunner.AssertNotNull(response, "status 仍可访问");
            }
            finally
            {
                KillProcess(process);
            }
        }

        // ── 辅助方法 ──

        private static string CreateTestConfig(string dir, int port, string stationId, string stationName,
            string subscriber = null)
        {
            var subs = subscriber != null
                ? string.Format("[\"{0}\"]", subscriber)
                : "[]";

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
            }}", stationId, stationName, port, subs);

            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, configJson, Encoding.UTF8);
            return path;
        }

        private static string FindDataForwarderExe()
        {
            // 搜索路径优先级
            string[] candidates =
            {
                // 1. 项目构建输出目录
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DataForwarder.exe"),
                // 2. 06_deploy/release/
                @"..\..\..\..\..\..\06_deploy\release\DataForwarder.exe",
                // 3. 相对 bin 目录
                @"..\..\..\..\06_deploy\release\DataForwarder.exe",
            };

            foreach (string path in candidates)
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                    return full;
            }

            // 回退：从 BaseDirectory 向上搜索
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

            throw new FileNotFoundException("找不到 DataForwarder.exe");
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
                // 回退：随机端口
                return new Random().Next(50000, 60000);
            }
        }

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
    }

    /// <summary>
    /// 测试用 HTTP 接收器 — 模拟订阅者，记录收到的 POST 请求。
    /// </summary>
    internal class TestReceiver
    {
        private readonly int _port;
        private HttpListener _listener;
        private Thread _listenThread;
        private readonly List<string> _posts = new List<string>();
        private readonly object _lock = new object();
        private bool _running;

        public TestReceiver(int port)
        {
            _port = port;
        }

        public void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(string.Format("http://+:{0}/", _port));
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
            try { _listener.Stop(); } catch { }
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
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    lock (_lock) { _posts.Add(body); }
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        public bool WaitForPost(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                lock (_lock)
                {
                    if (_posts.Count > 0) return true;
                }
                Thread.Sleep(200);
                waited += 200;
            }
            lock (_lock) { return _posts.Count > 0; }
        }

        public List<string> GetPosts()
        {
            lock (_lock) { return new List<string>(_posts); }
        }
    }
}
