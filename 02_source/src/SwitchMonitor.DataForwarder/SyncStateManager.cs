using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.DataForwarder
{
    /// <summary>
    /// .sync_state.json 持久化管理器。
    /// 独立文件，不写 SQLite，避免与 SwitchMonitor 主程序锁冲突。
    /// 格式: {"SSB": {"192.168.1.100:9000": 1712345678, ...}}
    /// </summary>
    public class SyncStateManager
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private Dictionary<string, Dictionary<string, long>> _data;

        public SyncStateManager(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException("filePath");
            _data = new Dictionary<string, Dictionary<string, long>>();
        }

        /// <summary>获取某站对某订阅者的最后推送时间戳。未记录返回 0。</summary>
        public long GetLastTimestamp(string stationId, string subscriber)
        {
            lock (_lock)
            {
                if (_data.TryGetValue(stationId, out var subs))
                {
                    if (subs.TryGetValue(subscriber, out long ts))
                        return ts;
                }
                return 0;
            }
        }

        /// <summary>更新某站对某订阅者的推送进度（内存操作，需调用 Save 持久化）。</summary>
        public void UpdateTimestamp(string stationId, string subscriber, long timestamp)
        {
            lock (_lock)
            {
                if (!_data.ContainsKey(stationId))
                    _data[stationId] = new Dictionary<string, long>();
                _data[stationId][subscriber] = timestamp;
            }
        }

        /// <summary>持久化到磁盘。使用写临时文件 + Move 保证原子性。</summary>
        public void Save()
        {
            lock (_lock)
            {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(_data);

                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string tmpPath = _filePath + ".tmp";
                File.WriteAllText(tmpPath, json, Encoding.UTF8);
                File.Delete(_filePath);
                File.Move(tmpPath, _filePath);
            }
        }

        /// <summary>从磁盘加载同步状态。文件不存在或损坏时返回空状态。</summary>
        public static SyncStateManager Load(string filePath)
        {
            var manager = new SyncStateManager(filePath);
            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    var data = serializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(json);
                    if (data != null)
                        manager._data = data;
                }
            }
            catch
            {
                // 文件损坏 → 空状态，从头开始推送
                manager._data = new Dictionary<string, Dictionary<string, long>>();
            }
            return manager;
        }
    }
}
