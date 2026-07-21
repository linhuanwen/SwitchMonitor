using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SwitchMonitor.Storage;

namespace SwitchMonitor.Network
{
    /// <summary>
    /// 数据接收端点 — 后台 HttpListener，处理 POST /api/receive。
    /// 接收站机 DataForwarder 推送的数据包，解压 gzip JSON，
    /// 写入对应站点的 SQLite 数据库。
    /// </summary>
    public class ReceiveEndpoint : IDisposable
    {
        private readonly NetworkConfig _config;
        private readonly CatchupState _catchupState;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private HttpListener _listener;
        private Thread _listenThread;
        private bool _running;
        private bool _disposed;

        // 按站缓存 StorageManager，避免重复打开数据库
        private readonly Dictionary<string, StorageManager> _storageCache = new Dictionary<string, StorageManager>();
        private readonly object _storageLock = new object();

        /// <summary>收到数据时触发。参数: stationId, eventCount</summary>
        public event Action<string, int> OnDataReceived;

        /// <summary>接收出错时触发。参数: 错误消息</summary>
        public event Action<string> OnError;

        public ReceiveEndpoint(NetworkConfig config, CatchupState catchupState)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _catchupState = catchupState ?? throw new ArgumentNullException("catchupState");
            _listener = new HttpListener();
            _listenThread = new Thread(ListenLoop);
            _listenThread.IsBackground = true;
        }

        /// <summary>启动 HTTP 监听。</summary>
        public void Start()
        {
            string prefix = string.Format("http://+:{0}/", _config.ListenPort);

            try
            {
                _listener.Prefixes.Add(prefix);
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // + 通配符不可用（无管理员权限），回退到 localhost
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _config.ListenPort));
                _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", _config.ListenPort));
                _listener.Start();
            }

            _running = true;
            _listenThread.Start();
        }

        /// <summary>停止 HTTP 监听。</summary>
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

        /// <summary>获取监听端口（测试用）。</summary>
        public int Port { get { return _config.ListenPort; } }

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
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ReceiveEndpoint listen error: " + ex.Message);
                }
            }
        }

        private void OnRequest(IAsyncResult ar)
        {
            HttpListener listener = (HttpListener)ar.AsyncState;
            HttpListenerContext ctx = null;

            try { ctx = listener.EndGetContext(ar); }
            catch { return; }

            try
            {
                string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

                if (path == "/api/receive" && ctx.Request.HttpMethod == "POST")
                {
                    HandleReceive(ctx);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ReceiveEndpoint request error: " + ex.Message);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>处理 POST /api/receive 请求。</summary>
        private void HandleReceive(HttpListenerContext ctx)
        {
            // 读取请求体
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }

            // 检查 gzip 压缩
            string contentEncoding = ctx.Request.Headers["Content-Encoding"];
            if (contentEncoding != null && contentEncoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body = DecompressGzip(body);
            }

            // 解析 JSON
            string json = Encoding.UTF8.GetString(body);
            var batch = _serializer.Deserialize<Dictionary<string, object>>(json);

            string stationId = (string)batch["stationId"];
            long batchTimestamp = Convert.ToInt64(batch["batchTimestamp"]);

            // 解析事件列表
            object eventsObj;
            if (!batch.TryGetValue("events", out eventsObj) || eventsObj == null)
            {
                WriteJsonResponse(ctx, "{\"received\":0}", 200);
                return;
            }

            var eventDicts = (System.Collections.ArrayList)eventsObj;

            // 写入对应站点的 SQLite
            var storage = GetStorage(stationId);
            int count = 0;
            long maxTs = 0;

            foreach (Dictionary<string, object> evtDict in eventDicts)
            {
                var rec = DictToEventRecord(evtDict);
                long id = storage.InsertOrIgnoreEvent(rec);
                if (id >= 0)
                {
                    count++;
                    if (rec.Timestamp > maxTs)
                        maxTs = rec.Timestamp;
                }
            }

            // 更新最后接收时间戳
            if (maxTs > 0)
            {
                long currentMax = _catchupState.GetLastTimestamp(stationId);
                if (maxTs > currentMax)
                {
                    _catchupState.UpdateTimestamp(stationId, maxTs);
                    _catchupState.Save();
                }
            }

            // 通知
            if (OnDataReceived != null && count > 0)
                OnDataReceived(stationId, count);

            // 响应
            string responseJson = string.Format("{{\"received\":{0}}}", count);
            WriteJsonResponse(ctx, responseJson, 200);
        }

        /// <summary>
        /// 从 JSON 字典构建 EventRecord。BLOB 字段从 Base64 解码。
        /// </summary>
        internal static EventRecord DictToEventRecord(Dictionary<string, object> dict)
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

        /// <summary>
        /// 获取或创建某站的 StorageManager（缓存）。
        /// </summary>
        internal StorageManager GetStorage(string stationId)
        {
            lock (_storageLock)
            {
                StorageManager storage;
                if (_storageCache.TryGetValue(stationId, out storage))
                    return storage;

                string dbPath = GetDbPathForStation(stationId);
                storage = new StorageManager(dbPath);
                _storageCache[stationId] = storage;
                return storage;
            }
        }

        /// <summary>根据站 ID 计算数据库路径。</summary>
        internal string GetDbPathForStation(string stationId)
        {
            string baseDir = _config.ParsedDataDir;
            if (!Path.IsPathRooted(baseDir))
                baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, baseDir);

            return Path.Combine(baseDir, stationId + ".db");
        }

        /// <summary>gzip 解压。</summary>
        private static byte[] DecompressGzip(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        /// <summary>写入 JSON 响应。</summary>
        private static void WriteJsonResponse(HttpListenerContext ctx, string json, int statusCode)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();

                // 清理 StorageManager 缓存
                lock (_storageLock)
                {
                    foreach (var storage in _storageCache.Values)
                    {
                        try { storage.Dispose(); } catch { }
                    }
                    _storageCache.Clear();
                }

                if (_listener != null)
                {
                    try { ((IDisposable)_listener).Dispose(); } catch { }
                }
            }
        }
    }
}
