using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Network
{
    /// <summary>
    /// 补拉状态持久化管理器 — 按站维护 lastTimestamp。
    /// 独立 JSON 文件，不写 SQLite，避免锁冲突。
    /// 格式: {"SSB": 1712345678, "DHD": 1712345600}
    /// </summary>
    public class CatchupState
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private Dictionary<string, long> _timestamps;

        public CatchupState(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException("filePath");
            _timestamps = new Dictionary<string, long>();
        }

        /// <summary>获取某站最后接收的时间戳。未记录返回 0。</summary>
        public long GetLastTimestamp(string stationId)
        {
            lock (_lock)
            {
                long ts;
                _timestamps.TryGetValue(stationId ?? "", out ts);
                return ts;
            }
        }

        /// <summary>更新某站最后接收的时间戳（内存操作，需调用 Save 持久化）。</summary>
        public void UpdateTimestamp(string stationId, long timestamp)
        {
            if (string.IsNullOrEmpty(stationId))
                return;

            lock (_lock)
            {
                _timestamps[stationId] = timestamp;
            }
        }

        /// <summary>获取所有站的时间戳快照。</summary>
        public Dictionary<string, long> GetAllTimestamps()
        {
            lock (_lock)
            {
                return new Dictionary<string, long>(_timestamps);
            }
        }

        /// <summary>持久化到磁盘。使用写临时文件 + Move 保证原子性。</summary>
        public void Save()
        {
            lock (_lock)
            {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(_timestamps);

                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string tmpPath = _filePath + ".tmp";
                File.WriteAllText(tmpPath, json, Encoding.UTF8);

                if (File.Exists(_filePath))
                    File.Delete(_filePath);

                File.Move(tmpPath, _filePath);
            }
        }

        /// <summary>从磁盘加载。文件不存在或损坏返回空状态。</summary>
        public static CatchupState Load(string filePath)
        {
            var state = new CatchupState(filePath);
            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    var data = serializer.Deserialize<Dictionary<string, long>>(json);
                    if (data != null)
                        state._timestamps = data;
                }
            }
            catch
            {
                // 文件损坏 → 空状态，从零开始补拉
                state._timestamps = new Dictionary<string, long>();
            }
            return state;
        }
    }
}
