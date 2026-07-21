using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Network
{
    /// <summary>站机在线状态</summary>
    public enum StationStatus
    {
        Unknown,
        Online,
        Offline
    }

    /// <summary>站机状态变更事件参数</summary>
    public class StationStateChangedEventArgs : EventArgs
    {
        public string StationId { get; set; }
        public string StationName { get; set; }
        public StationStatus OldStatus { get; set; }
        public StationStatus NewStatus { get; set; }
        public long LastTimestamp { get; set; }
    }

    /// <summary>
    /// 站机监控器 — 定时器驱动，每 N 分钟探测所有站机的 /api/status。
    /// 管理在线/离线状态，连续失败达到阈值后标记离线。
    /// 离线→在线时触发 StationStateChanged 事件（DataCatcher 监听此事件自动补拉）。
    /// </summary>
    public class StationMonitor : IDisposable
    {
        private readonly NetworkConfig _config;
        private readonly CatchupState _catchupState;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private Timer _timer;
        private bool _running;
        private bool _disposed;

        // 按站状态
        private readonly Dictionary<string, StationStatus> _statuses = new Dictionary<string, StationStatus>();
        private readonly Dictionary<string, int> _consecutiveFailures = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _lastHeartbeat = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, long> _lastKnownTimestamp = new Dictionary<string, long>();
        private readonly object _stateLock = new object();

        /// <summary>站机状态变更事件。离线→在线时 DataCatcher 自动触发补拉。</summary>
        public event EventHandler<StationStateChangedEventArgs> StationStateChanged;

        /// <summary>探测出错时触发。</summary>
        public event Action<string> OnError;

        public StationMonitor(NetworkConfig config, CatchupState catchupState)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _catchupState = catchupState ?? throw new ArgumentNullException("catchupState");

            // 初始化所有站点状态为 Unknown
            foreach (var station in config.Stations)
            {
                _statuses[station.Id] = StationStatus.Unknown;
                _consecutiveFailures[station.Id] = 0;
                _lastKnownTimestamp[station.Id] = _catchupState.GetLastTimestamp(station.Id);
            }
        }

        /// <summary>启动定时探测。</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;

            // 立即执行第一次探测
            ThreadPool.QueueUserWorkItem(_ => ProbeAllStations());

            // 定时器
            _timer = new Timer(OnTimerTick, null, _config.ProbeIntervalMs, _config.ProbeIntervalMs);
        }

        /// <summary>停止定时探测。</summary>
        public void Stop()
        {
            _running = false;
            if (_timer != null)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _timer.Dispose();
                _timer = null;
            }
        }

        private void OnTimerTick(object state)
        {
            try { ProbeAllStations(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("StationMonitor tick error: " + ex.Message);
            }
        }

        /// <summary>探测所有配置的站机。</summary>
        private void ProbeAllStations()
        {
            foreach (var station in _config.Stations)
            {
                ProbeStation(station);
            }
        }

        /// <summary>
        /// 探测单个站机：GET /api/status。
        /// 公开方法，测试可直接调用。
        /// </summary>
        public StationStatus ProbeStation(StationInfo station)
        {
            if (station == null)
                return StationStatus.Unknown;

            bool success = false;
            long remoteTimestamp = 0;

            try
            {
                string url = station.BaseUrl + "/api/status";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = _config.HttpTimeoutMs;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    var statusData = _serializer.Deserialize<Dictionary<string, object>>(json);

                    if (statusData.ContainsKey("lastTimestamp"))
                        remoteTimestamp = Convert.ToInt64(statusData["lastTimestamp"]);

                    string st = statusData.ContainsKey("status") ? (string)statusData["status"] : "";
                    success = (st == "ok" || (int)response.StatusCode == 200);
                }
            }
            catch (WebException)
            {
                success = false;
            }
            catch (Exception)
            {
                success = false;
            }

            // 更新状态
            UpdateStationState(station, success, remoteTimestamp);

            lock (_stateLock)
            {
                return _statuses.ContainsKey(station.Id) ? _statuses[station.Id] : StationStatus.Unknown;
            }
        }

        /// <summary>更新站机状态，管理在线/离线转换。</summary>
        private void UpdateStationState(StationInfo station, bool success, long remoteTimestamp)
        {
            lock (_stateLock)
            {
                StationStatus oldStatus = _statuses.ContainsKey(station.Id)
                    ? _statuses[station.Id] : StationStatus.Unknown;

                int failures = _consecutiveFailures.ContainsKey(station.Id)
                    ? _consecutiveFailures[station.Id] : 0;

                if (success)
                {
                    // 重置失败计数
                    _consecutiveFailures[station.Id] = 0;
                    _lastHeartbeat[station.Id] = DateTime.Now;
                    if (remoteTimestamp > 0)
                        _lastKnownTimestamp[station.Id] = remoteTimestamp;

                    StationStatus newStatus = StationStatus.Online;

                    // 状态变更通知（仅当从非在线变为在线时）
                    if (oldStatus != StationStatus.Online)
                    {
                        _statuses[station.Id] = newStatus;
                        FireStateChanged(station, oldStatus, newStatus);
                    }
                    else
                    {
                        _statuses[station.Id] = newStatus;
                    }
                }
                else
                {
                    failures++;
                    _consecutiveFailures[station.Id] = failures;

                    if (failures >= _config.OfflineThreshold)
                    {
                        StationStatus newStatus = StationStatus.Offline;

                        if (oldStatus != StationStatus.Offline)
                        {
                            _statuses[station.Id] = newStatus;
                            FireStateChanged(station, oldStatus, newStatus);
                        }
                        else
                        {
                            _statuses[station.Id] = newStatus;
                        }
                    }
                    else
                    {
                        // 首次失败但未达阈值，保持原状态
                        // 若之前是 Unknown，保持 Unknown
                    }
                }
            }
        }

        private void FireStateChanged(StationInfo station, StationStatus oldStatus, StationStatus newStatus)
        {
            var handler = StationStateChanged;
            if (handler != null)
            {
                long lastTs = _catchupState.GetLastTimestamp(station.Id);
                handler(this, new StationStateChangedEventArgs
                {
                    StationId = station.Id,
                    StationName = station.Name,
                    OldStatus = oldStatus,
                    NewStatus = newStatus,
                    LastTimestamp = lastTs
                });
            }
        }

        /// <summary>获取指定站机当前状态。</summary>
        public StationStatus GetStatus(string stationId)
        {
            lock (_stateLock)
            {
                StationStatus status;
                _statuses.TryGetValue(stationId, out status);
                return status;
            }
        }

        /// <summary>获取所有站机状态快照。</summary>
        public Dictionary<string, StationStatus> GetAllStatuses()
        {
            lock (_stateLock)
            {
                return new Dictionary<string, StationStatus>(_statuses);
            }
        }

        /// <summary>获取站机连续失败次数（测试用）。</summary>
        public int GetConsecutiveFailures(string stationId)
        {
            lock (_stateLock)
            {
                int failures;
                _consecutiveFailures.TryGetValue(stationId, out failures);
                return failures;
            }
        }

        /// <summary>手动触发状态变更事件（测试用）。</summary>
        internal void FireStateChangedForTest(string stationId, StationStatus oldStatus, StationStatus newStatus)
        {
            var station = _config.Stations.Find(s => s.Id == stationId);
            if (station != null)
                FireStateChanged(station, oldStatus, newStatus);
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
