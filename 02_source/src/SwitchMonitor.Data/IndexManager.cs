using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 中间 JSON 数据索引管理器
    /// 负责 parsed_data/ 目录下的 index.json 和日数据文件的读写
    /// </summary>
    public class IndexManager
    {
        private readonly string _parsedDataDir;
        private readonly JavaScriptSerializer _serializer;
        private readonly object _lock = new object();
        private Dictionary<string, Dictionary<string, List<long>>> _index;

        public IndexManager(string parsedDataDir)
        {
            _parsedDataDir = parsedDataDir;
            _serializer = new JavaScriptSerializer();
            _serializer.MaxJsonLength = int.MaxValue; // 避免大数据量日报表超出默认2MB限制
            _index = new Dictionary<string, Dictionary<string, List<long>>>();
        }

        /// <summary>
        /// 确保 parsed_data 目录和 index.json 存在
        /// </summary>
        public void Initialize()
        {
            if (!Directory.Exists(_parsedDataDir))
                Directory.CreateDirectory(_parsedDataDir);

            string indexPath = Path.Combine(_parsedDataDir, "index.json");
            if (File.Exists(indexPath))
            {
                try
                {
                    string json = File.ReadAllText(indexPath, Encoding.UTF8);
                    var loaded = _serializer.Deserialize<Dictionary<string, Dictionary<string, List<long>>>>(json);
                    lock (_lock)
                    {
                        _index = loaded ?? new Dictionary<string, Dictionary<string, List<long>>>();
                    }
                }
                catch
                {
                    lock (_lock)
                    {
                        _index = new Dictionary<string, Dictionary<string, List<long>>>();
                    }
                }
            }
        }

        /// <summary>
        /// 获取某转辙机某天的所有时间戳（降序）
        /// </summary>
        public List<long> GetTimestamps(string switchId, string date)
        {
            lock (_lock)
            {
                Dictionary<string, List<long>> dateDict;
                if (_index.TryGetValue(switchId, out dateDict))
                {
                    List<long> timestamps;
                    if (dateDict.TryGetValue(date, out timestamps))
                    {
                        return new List<long>(timestamps); // 返回副本，避免外部修改
                    }
                }
            }
            return new List<long>();
        }

        /// <summary>
        /// 获取某转辙机所有有数据的日期列表
        /// </summary>
        public List<string> GetDates(string switchId)
        {
            lock (_lock)
            {
                Dictionary<string, List<long>> dateDict;
                if (_index.TryGetValue(switchId, out dateDict))
                {
                    var dates = new List<string>(dateDict.Keys);
                    dates.Sort((a, b) => b.CompareTo(a)); // 降序（最新在前）
                    return dates;
                }
            }
            return new List<string>();
        }

        /// <summary>
        /// 加载某天的完整曲线数据
        /// </summary>
        public List<SwitchEvent> LoadDayData(string switchId, string date)
        {
            string filePath = Path.Combine(_parsedDataDir, switchId, date + ".json");
            if (!File.Exists(filePath))
                return new List<SwitchEvent>();

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var events = _serializer.Deserialize<List<SwitchEvent>>(json);
                return events ?? new List<SwitchEvent>();
            }
            catch
            {
                return new List<SwitchEvent>();
            }
        }

        /// <summary>
        /// 保存某天的曲线数据并更新索引
        /// </summary>
        public void SaveDayData(string switchId, string date, List<SwitchEvent> events)
        {
            // 确保目录存在
            string dir = Path.Combine(_parsedDataDir, switchId);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 写入日数据 JSON
            string filePath = Path.Combine(dir, date + ".json");
            string json = _serializer.Serialize(events);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            // 更新内存索引
            var timestamps = new List<long>();
            foreach (var evt in events)
                timestamps.Add(evt.Timestamp);

            timestamps.Sort((a, b) => b.CompareTo(a)); // 降序

            lock (_lock)
            {
                if (!_index.ContainsKey(switchId))
                    _index[switchId] = new Dictionary<string, List<long>>();

                _index[switchId][date] = timestamps;
            }

            // 写入 index.json
            SaveIndex();
        }

        /// <summary>
        /// 持久化 index.json
        /// </summary>
        private void SaveIndex()
        {
            string indexPath = Path.Combine(_parsedDataDir, "index.json");
            string json;
            lock (_lock)
            {
                json = _serializer.Serialize(_index);
            }
            File.WriteAllText(indexPath, json, Encoding.UTF8);
        }
    }
}
