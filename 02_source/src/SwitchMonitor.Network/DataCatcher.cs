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
    /// <summary>补拉进度事件参数</summary>
    public class CatchupProgressEventArgs : EventArgs
    {
        public string StationId { get; set; }
        public string StationName { get; set; }
        public int ReceivedCount { get; set; }
        public int TotalCount { get; set; }
        public bool IsComplete { get; set; }
        public bool IsError { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>补拉结果</summary>
    public class CatchupResult
    {
        public bool Success { get; set; }
        public int TotalReceived { get; set; }
        public long LastTimestamp { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 数据补拉器 — 从站机拉取增量数据写入本地 SQLite。
    /// 支持自动补拉（StationMonitor 离线→在线触发）和手动补拉（UI 按钮触发）。
    ///
    /// 边收边写，全部完成后才更新 lastTimestamp。
    /// 中途失败不更新标记，下次从同一 since 重拉。
    /// </summary>
    public class DataCatcher : IDisposable
    {
        private readonly NetworkConfig _config;
        private readonly CatchupState _catchupState;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly object _catchLock = new object();
        private bool _disposed;

        // 按站缓存 StorageManager
        private readonly Dictionary<string, StorageManager> _storageCache = new Dictionary<string, StorageManager>();
        private readonly object _storageLock = new object();

        /// <summary>补拉进度事件。在后台线程触发，UI 需封送。</summary>
        public event EventHandler<CatchupProgressEventArgs> ProgressChanged;

        public DataCatcher(NetworkConfig config, CatchupState catchupState)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _catchupState = catchupState ?? throw new ArgumentNullException("catchupState");
        }

        /// <summary>
        /// 异步补拉数据（通过 ThreadPool 执行）。
        /// </summary>
        public void CatchupAsync(StationInfo station)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Catchup(station); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("DataCatcher async error: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// 同步补拉数据。从 lastTimestamp 开始拉取所有增量事件。
        /// 边收边写，全部完成后才更新 lastTimestamp。
        /// </summary>
        public CatchupResult Catchup(StationInfo station)
        {
            if (station == null)
                return new CatchupResult { Success = false, ErrorMessage = "station is null" };

            long since = _catchupState.GetLastTimestamp(station.Id);
            var result = new CatchupResult();
            var storage = GetStorage(station.Id);

            try
            {
                // 1. 请求 /api/events?since=xxx
                string url = string.Format("{0}/api/events?since={1}", station.BaseUrl, since);

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = _config.HttpTimeoutMs * 3; // 补拉给更长超时（30s）

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var responseStream = response.GetResponseStream())
                {
                    // 处理 gzip 压缩
                    Stream stream = responseStream;
                    string contentEncoding = response.Headers["Content-Encoding"];
                    if (contentEncoding != null && contentEncoding.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        stream = new GZipStream(responseStream, CompressionMode.Decompress);
                    }

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();

                        // 2. 解析响应
                        var data = _serializer.Deserialize<Dictionary<string, object>>(json);
                        int totalCount = data.ContainsKey("count") ? Convert.ToInt32(data["count"]) : 0;

                        object eventsObj;
                        if (!data.TryGetValue("events", out eventsObj) || eventsObj == null)
                        {
                            result.Success = true;
                            result.LastTimestamp = since;
                            ReportProgress(station, 0, 0, true, false, null);
                            return result;
                        }

                        var eventDicts = (System.Collections.ArrayList)eventsObj;

                        // 3. 边收边写
                        long maxTs = since;
                        int received = 0;

                        foreach (Dictionary<string, object> evtDict in eventDicts)
                        {
                            var rec = ReceiveEndpoint.DictToEventRecord(evtDict);

                            // 尝试插入（INSERT OR IGNORE 去重）
                            long id = -1;
                            try
                            {
                                id = storage.InsertOrIgnoreEvent(rec);
                            }
                            catch (Exception ex)
                            {
                                // 单条写入失败不中断整个补拉
                                System.Diagnostics.Debug.WriteLine(
                                    string.Format("DataCatcher insert error: {0}", ex.Message));
                            }

                            if (id >= 0)
                                received++;

                            if (rec.Timestamp > maxTs)
                                maxTs = rec.Timestamp;

                            // 进度通知
                            if (received % _config.CatchupProgressInterval == 0)
                            {
                                ReportProgress(station, received, totalCount, false, false, null);
                            }
                        }

                        // 4. 全部完成后更新 lastTimestamp
                        // 关键：只有全部成功才更新，中途失败不更新
                        if (maxTs > since)
                        {
                            _catchupState.UpdateTimestamp(station.Id, maxTs);
                            _catchupState.Save();
                        }

                        result.Success = true;
                        result.TotalReceived = received;
                        result.LastTimestamp = maxTs;

                        ReportProgress(station, received, totalCount, true, false, null);
                    }
                }
            }
            catch (WebException ex)
            {
                // 网络错误：不更新 lastTimestamp
                result.Success = false;
                result.ErrorMessage = string.Format("网络错误: {0}", ex.Message);
                ReportProgress(station, 0, 0, true, true, result.ErrorMessage);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                ReportProgress(station, 0, 0, true, true, result.ErrorMessage);
            }

            return result;
        }

        private void ReportProgress(StationInfo station, int received, int total,
            bool isComplete, bool isError, string errorMessage)
        {
            var handler = ProgressChanged;
            if (handler != null)
            {
                handler(this, new CatchupProgressEventArgs
                {
                    StationId = station.Id,
                    StationName = station.Name,
                    ReceivedCount = received,
                    TotalCount = total,
                    IsComplete = isComplete,
                    IsError = isError,
                    ErrorMessage = errorMessage
                });
            }
        }

        /// <summary>获取或创建某站的 StorageManager（缓存）。</summary>
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                lock (_storageLock)
                {
                    foreach (var storage in _storageCache.Values)
                    {
                        try { storage.Dispose(); } catch { }
                    }
                    _storageCache.Clear();
                }
            }
        }
    }
}
