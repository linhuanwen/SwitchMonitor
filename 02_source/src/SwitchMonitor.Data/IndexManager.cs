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

        /// <summary>parsed_data 根目录路径（供 DiagnosisRunner 等外部调用方使用）</summary>
        public string ParsedDataDir { get { return _parsedDataDir; } }
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
        /// 获取所有已索引的转辙机 ID 列表
        /// </summary>
        public List<string> GetAllSwitchIds()
        {
            lock (_lock)
            {
                return new List<string>(_index.Keys);
            }
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

        // ── D4: 诊断结果存储 ──

        /// <summary>
        /// 保存某天的诊断结果到 .diag.json 并更新 alarms_index.json。
        /// 与 SaveDayData 配套使用：先写日数据，再写诊断结果。
        /// 写入顺序：alarms_index.json（小文件、加锁）先写 → .diag.json 后写。
        /// 若 alarms_index 更新失败，.diag.json 不会写入，保持两者一致。
        /// </summary>
        public void SaveDayDiagnosis(string switchId, string date, List<EventDiagnosis> diagnoses)
        {
            // 确保目录存在
            string dir = Path.Combine(_parsedDataDir, switchId);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 先更新 alarms_index.json（小文件、加锁写入，失败则中断，不污染 .diag.json）
            UpdateAlarmsIndex(switchId, date, diagnoses);

            // 再写 .diag.json（与 {date}.json 并列）
            string filePath = Path.Combine(dir, date + ".diag.json");
            string json = _serializer.Serialize(diagnoses);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 加载某天的诊断结果。文件缺失 → 返回空列表。
        /// </summary>
        public List<EventDiagnosis> LoadDayDiagnosis(string switchId, string date)
        {
            string filePath = Path.Combine(_parsedDataDir, switchId, date + ".diag.json");
            if (!File.Exists(filePath))
                return new List<EventDiagnosis>();

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var list = _serializer.Deserialize<List<EventDiagnosis>>(json);
                return list ?? new List<EventDiagnosis>();
            }
            catch
            {
                return new List<EventDiagnosis>();
            }
        }

        /// <summary>
        /// 加载 alarms_index.json 全文（线程安全）。
        /// 文件缺失 → 返回空字典。
        /// </summary>
        public Dictionary<string, Dictionary<string, Dictionary<string, int>>> LoadAlarmsIndex()
        {
            lock (_lock)
            {
                return LoadAlarmsIndexInternal();
            }
        }

        /// <summary>
        /// 更新 alarms_index.json：统计某天非正常事件计数。
        /// 线程安全（在 _lock 内操作）。
        /// </summary>
        private void UpdateAlarmsIndex(string switchId, string date, List<EventDiagnosis> diagnoses)
        {
            // 统计该日非正常事件计数
            int warningCount = 0;
            int alarmCount = 0;
            int faultCount = 0;

            foreach (var d in diagnoses)
            {
                switch (d.Level)
                {
                    case "预警": warningCount++; break;
                    case "报警": alarmCount++; break;
                    case "故障": faultCount++; break;
                }
            }

            lock (_lock)
            {
                // 加载/初始化 alarms_index
                var alarmsIndex = LoadAlarmsIndexInternal();

                // 如果当天没有任何非正常事件，删除该日期条目（如存在）
                if (warningCount == 0 && alarmCount == 0 && faultCount == 0)
                {
                    if (alarmsIndex.ContainsKey(switchId))
                    {
                        alarmsIndex[switchId].Remove(date);
                        if (alarmsIndex[switchId].Count == 0)
                            alarmsIndex.Remove(switchId);
                    }
                }
                else
                {
                    // 确保 switchId 条目存在
                    if (!alarmsIndex.ContainsKey(switchId))
                        alarmsIndex[switchId] = new Dictionary<string, Dictionary<string, int>>();

                    // 写入三个键（恒输出，含 0）
                    alarmsIndex[switchId][date] = new Dictionary<string, int>
                    {
                        { "预警", warningCount },
                        { "报警", alarmCount },
                        { "故障", faultCount }
                    };
                }

                // 持久化
                string indexPath = Path.Combine(_parsedDataDir, "alarms_index.json");
                string json = _serializer.Serialize(alarmsIndex);
                File.WriteAllText(indexPath, json, Encoding.UTF8);
            }
        }

        /// <summary>
        /// 内部读取 alarms_index.json（无锁，由调用方持有 _lock）。
        /// </summary>
        private Dictionary<string, Dictionary<string, Dictionary<string, int>>> LoadAlarmsIndexInternal()
        {
            string indexPath = Path.Combine(_parsedDataDir, "alarms_index.json");
            if (!File.Exists(indexPath))
                return new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();

            try
            {
                string json = File.ReadAllText(indexPath, Encoding.UTF8);
                var index = _serializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, int>>>>(json);
                return index ?? new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();
            }
            catch
            {
                return new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();
            }
        }
    }
}
