using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SwitchMonitor.Storage;

namespace SwitchMonitor.DataForwarder
{
    /// <summary>
    /// 推送引擎 — 轮询 SQLite → 合并窗口 → gzip 打包 → POST 给订阅者 → 更新同步状态。
    /// 该类的所有公共方法均为线程安全。
    /// </summary>
    public class PushEngine : IDisposable
    {
        private readonly ForwarderConfig _config;
        private readonly StorageManager _storage;
        private readonly SyncStateManager _syncState;
        private readonly object _lock = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        // 轮询定时器
        private Timer _pollTimer;

        // 合并窗口状态
        private bool _isCollecting;
        private DateTime _mergeDeadline;
        private List<EventRecord> _pendingBatch;
        private long _batchMaxTimestamp;
        private long _lastPushedTimestamp;

        private bool _disposed;

        public PushEngine(ForwarderConfig config, StorageManager storage, SyncStateManager syncState)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _storage = storage ?? throw new ArgumentNullException("storage");
            _syncState = syncState ?? throw new ArgumentNullException("syncState");
            _pendingBatch = new List<EventRecord>();
        }

        /// <summary>启动轮询。intervalMs: 轮询间隔（毫秒）。</summary>
        public void Start(int intervalMs = 1000)
        {
            // 从 sync_state 恢复初始 lastPushedTimestamp（取所有订阅者中的最小值）
            _lastPushedTimestamp = long.MaxValue;
            foreach (var sub in _config.Subscribers)
            {
                long ts = _syncState.GetLastTimestamp(_config.StationId, sub);
                if (ts < _lastPushedTimestamp)
                    _lastPushedTimestamp = ts;
            }
            if (_lastPushedTimestamp == long.MaxValue)
                _lastPushedTimestamp = 0;

            _pollTimer = new Timer(OnPollTick, null, intervalMs, intervalMs);
        }

        /// <summary>停止轮询。</summary>
        public void Stop()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _pollTimer.Dispose();
                _pollTimer = null;
            }
        }

        /// <summary>定时器回调。</summary>
        private void OnPollTick(object state)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // 异常不崩溃进程，记录日志
                System.Diagnostics.Debug.WriteLine("PushEngine.Tick error: " + ex.Message);
            }
        }

        /// <summary>一次轮询周期。</summary>
        internal void Tick()
        {
            lock (_lock)
            {
                // 1. 查询新事件
                List<EventRecord> newRows = _storage.GetEventsSince(_lastPushedTimestamp);
                if (newRows.Count == 0)
                {
                    // 如果正在收集且已超时，刷新现有批次
                    if (_isCollecting && DateTime.Now >= _mergeDeadline)
                        FlushBatch();
                    return;
                }

                // 2. 进入收集状态
                if (!_isCollecting)
                {
                    _isCollecting = true;
                    _mergeDeadline = DateTime.Now.AddMilliseconds(_config.MergeWindowMs);
                    _pendingBatch = new List<EventRecord>();
                    _batchMaxTimestamp = 0;
                }

                // 3. 累积事件
                _pendingBatch.AddRange(newRows);
                foreach (var row in newRows)
                {
                    if (row.Timestamp > _batchMaxTimestamp)
                        _batchMaxTimestamp = row.Timestamp;
                }

                // 4. 检查是否到达合并窗口截止
                if (DateTime.Now >= _mergeDeadline)
                    FlushBatch();
            }
        }

        /// <summary>刷新当前批次：打包 → 推送 → 更新同步状态。</summary>
        private void FlushBatch()
        {
            if (_pendingBatch.Count == 0)
            {
                _isCollecting = false;
                return;
            }

            // 序列化 + gzip 压缩
            var batchData = new Dictionary<string, object>
            {
                { "stationId", _config.StationId },
                { "batchTimestamp", _batchMaxTimestamp },
                { "events", _pendingBatch.ConvertAll(e => (object)ApiHandlers.EventToDict(e)) }
            };
            string json = _serializer.Serialize(batchData);
            byte[] rawData = Encoding.UTF8.GetBytes(json);
            byte[] gzipData = CompressGzip(rawData);

            // 推送给每个订阅者
            long pushTimestamp = _batchMaxTimestamp;
            foreach (string subscriber in _config.Subscribers)
            {
                bool success = PushToSubscriber(subscriber, gzipData);
                if (success)
                {
                    _syncState.UpdateTimestamp(_config.StationId, subscriber, pushTimestamp);
                }
            }

            // 持久化同步状态
            _syncState.Save();

            // 更新 lastPushedTimestamp（取所有订阅者中最小的成功时间戳）
            _lastPushedTimestamp = pushTimestamp;

            // 重置收集状态
            _isCollecting = false;
            _pendingBatch.Clear();
            _batchMaxTimestamp = 0;
        }

        /// <summary>
        /// POST gzip 数据给订阅者。重试 3 次（间隔 2s/4s/8s）。
        /// 返回 true 表示推送成功。
        /// </summary>
        private bool PushToSubscriber(string subscriber, byte[] gzipData)
        {
            string url = string.Format("http://{0}/api/receive", subscriber);

            int[] retryDelays = { 0, 2000, 4000, 8000 }; // 首次 + 3 次重试
            for (int attempt = 0; attempt < retryDelays.Length; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(retryDelays[attempt]);

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Headers.Add("Content-Encoding", "gzip");
                    request.Timeout = 10000;
                    request.ContentLength = gzipData.Length;

                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(gzipData, 0, gzipData.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                            return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        string.Format("Push to {0} attempt {1}/{2} failed: {3}",
                            subscriber, attempt + 1, retryDelays.Length, ex.Message));
                }
            }

            return false; // 全部重试失败
        }

        /// <summary>gzip 压缩字节数组。</summary>
        public static byte[] CompressGzip(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionMode.Compress, true))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>gzip 解压字节数组（测试用）。</summary>
        public static byte[] DecompressGzip(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
            }
        }
    }
}
