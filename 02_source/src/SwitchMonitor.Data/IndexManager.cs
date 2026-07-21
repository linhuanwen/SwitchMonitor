using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using SwitchMonitor.Storage;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 数据索引管理器（SQLite 后端）。
    /// 底层存储从 JSON 文件切换为 SQLite，对外接口保持不变。
    /// 维护内存索引缓存以加速 UI 查询。
    /// </summary>
    public class IndexManager
    {
        private string _parsedDataDir;
        private StorageManager _storage;
        private readonly JavaScriptSerializer _serializer;
        private readonly object _lock = new object();

        /// <summary>parsed_data 根目录路径（供 DiagnosisRunner 等外部调用方使用）</summary>
        public string ParsedDataDir { get { return _parsedDataDir; } }

        /// <summary>底层 StorageManager 实例（供 DataMigrator 等使用）</summary>
        public StorageManager Storage { get { return _storage; } }

        // 内存索引缓存：switchId → date → timestamps[]
        private Dictionary<string, Dictionary<string, List<long>>> _index;

        public IndexManager(string parsedDataDir)
        {
            _parsedDataDir = parsedDataDir;
            _serializer = new JavaScriptSerializer();
            _serializer.MaxJsonLength = int.MaxValue;
            _index = new Dictionary<string, Dictionary<string, List<long>>>();

            // SQLite 数据库文件位于 parsed_data/switch_events.db
            string dbPath = Path.Combine(parsedDataDir, "switch_events.db");
            _storage = new StorageManager(dbPath);
        }

        /// <summary>
        /// 切换数据目录并重新初始化索引（用于站点切换）
        /// </summary>
        public void ChangeDataDir(string newParsedDataDir)
        {
            if (_storage != null)
            {
                try { _storage.Dispose(); } catch { }
            }

            _parsedDataDir = newParsedDataDir;
            lock (_lock)
            {
                _index = new Dictionary<string, Dictionary<string, List<long>>>();
            }

            string dbPath = Path.Combine(newParsedDataDir, "switch_events.db");

            _storage = new StorageManager(dbPath);

            // 切换站点时也检查 JSON→SQLite 迁移
            var migrator = new DataMigrator(_storage);
            if (!migrator.IsMigrated())
            {
                try
                {
                    var migResult = migrator.Migrate(newParsedDataDir);
                    if (!migResult.Success && !migResult.Skipped)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "站点迁移失败: " + migResult.Message);
                    }
                }
                catch (Exception migEx)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "站点迁移异常: " + migEx.Message);
                }
            }

            Initialize();
        }

        /// <summary>
        /// 从 SQLite 重建内存索引缓存。
        /// </summary>
        public void Initialize()
        {
            if (!Directory.Exists(_parsedDataDir))
                Directory.CreateDirectory(_parsedDataDir);

            try
            {
                var switchIds = _storage.GetAllSwitchIds();
                var newIndex = new Dictionary<string, Dictionary<string, List<long>>>();

                foreach (string switchId in switchIds)
                {
                    var dates = _storage.GetDates(switchId);
                    var dateDict = new Dictionary<string, List<long>>();
                    foreach (string date in dates)
                    {
                        var timestamps = _storage.GetTimestamps(switchId, date);
                        if (timestamps.Count > 0)
                            dateDict[date] = timestamps;
                    }
                    if (dateDict.Count > 0)
                        newIndex[switchId] = dateDict;
                }

                lock (_lock)
                {
                    _index = newIndex;
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

        /// <summary>
        /// 获取某转辙机某天的所有时间戳（降序）
        /// </summary>
        public List<long> GetTimestamps(string switchId, string date)
        {
            // 先查缓存
            lock (_lock)
            {
                Dictionary<string, List<long>> dateDict;
                if (_index.TryGetValue(switchId, out dateDict))
                {
                    List<long> timestamps;
                    if (dateDict.TryGetValue(date, out timestamps))
                        return new List<long>(timestamps);
                }
            }

            // 缓存未命中，查 SQLite 并更新缓存
            var result = _storage.GetTimestamps(switchId, date);
            if (result.Count > 0)
            {
                lock (_lock)
                {
                    if (!_index.ContainsKey(switchId))
                        _index[switchId] = new Dictionary<string, List<long>>();
                    _index[switchId][date] = result;
                }
            }
            return result;
        }

        /// <summary>
        /// 获取所有已索引的转辙机 ID 列表
        /// </summary>
        public List<string> GetAllSwitchIds()
        {
            lock (_lock)
            {
                if (_index.Count > 0)
                    return new List<string>(_index.Keys);
            }
            return _storage.GetAllSwitchIds();
        }

        /// <summary>
        /// 获取某转辙机所有有数据的日期列表（降序）
        /// </summary>
        public List<string> GetDates(string switchId)
        {
            lock (_lock)
            {
                Dictionary<string, List<long>> dateDict;
                if (_index.TryGetValue(switchId, out dateDict))
                {
                    var dates = new List<string>(dateDict.Keys);
                    dates.Sort((a, b) => b.CompareTo(a));
                    return dates;
                }
            }
            return _storage.GetDates(switchId);
        }

        /// <summary>
        /// 加载某天的完整曲线数据（从 SQLite，映射回 SwitchEvent）
        /// </summary>
        public List<SwitchEvent> LoadDayData(string switchId, string date)
        {
            var records = _storage.GetEventsByDate(switchId, date);
            var events = new List<SwitchEvent>(records.Count);
            foreach (var rec in records)
                events.Add(DataMigrator.RecordToSwitchEvent(rec));
            return events;
        }

        /// <summary>
        /// 保存某天的曲线数据并更新索引（写入 SQLite）
        /// </summary>
        public void SaveDayData(string switchId, string date, List<SwitchEvent> events)
        {
            // SwitchEvent → EventRecord 映射
            var records = new List<EventRecord>();
            if (events != null)
            {
                foreach (var evt in events)
                    records.Add(DataMigrator.SwitchEventToRecord(switchId, evt));
            }

            // 写入 SQLite（先删后插，幂等）
            _storage.SaveDayData(switchId, date, records);

            // 更新内存索引
            var timestamps = new List<long>();
            if (events != null)
            {
                foreach (var evt in events)
                    timestamps.Add(evt.Timestamp);
            }
            timestamps.Sort((a, b) => b.CompareTo(a)); // 降序

            lock (_lock)
            {
                if (!_index.ContainsKey(switchId))
                    _index[switchId] = new Dictionary<string, List<long>>();

                if (timestamps.Count > 0)
                    _index[switchId][date] = timestamps;
                else
                    _index[switchId].Remove(date);
            }
        }

        // ── D4: 诊断结果存储（SQLite 后端） ──

        /// <summary>
        /// 保存某天的诊断结果到 events.diag_json 列。
        /// </summary>
        public void SaveDayDiagnosis(string switchId, string date, List<EventDiagnosis> diagnoses)
        {
            if (diagnoses == null || diagnoses.Count == 0)
                return;

            var diagRecords = new List<DiagnosisRecord>();
            foreach (var d in diagnoses)
            {
                diagRecords.Add(new DiagnosisRecord
                {
                    Timestamp = d.Timestamp,
                    Level = d.Level,
                    DiagJson = _serializer.Serialize(d)
                });
            }

            _storage.SaveDayDiagnoses(switchId, date, diagRecords);
        }

        /// <summary>
        /// 加载某天的诊断结果。无数据 → 返回空列表。
        /// </summary>
        public List<EventDiagnosis> LoadDayDiagnosis(string switchId, string date)
        {
            var result = new List<EventDiagnosis>();
            var jsons = _storage.GetDiagnosisJsonsByDate(switchId, date);

            foreach (string json in jsons)
            {
                try
                {
                    var diag = _serializer.Deserialize<EventDiagnosis>(json);
                    if (diag != null)
                        result.Add(diag);
                }
                catch { /* 跳过解析失败的行 */ }
            }
            return result;
        }

        /// <summary>
        /// 加载告警索引（从 events.diag_json 动态计算）。
        /// 格式：switchId → date → {"预警": n, "报警": n, "故障": n}
        /// </summary>
        public Dictionary<string, Dictionary<string, Dictionary<string, int>>> LoadAlarmsIndex()
        {
            var result = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();

            try
            {
                var switchIds = _storage.GetAllSwitchIds();
                foreach (string switchId in switchIds)
                {
                    var dates = _storage.GetDates(switchId);
                    foreach (string date in dates)
                    {
                        var jsons = _storage.GetDiagnosisJsonsByDate(switchId, date);
                        if (jsons.Count == 0)
                            continue;

                        int warningCount = 0, alarmCount = 0, faultCount = 0;
                        foreach (string json in jsons)
                        {
                            try
                            {
                                var diag = _serializer.Deserialize<EventDiagnosis>(json);
                                if (diag != null)
                                {
                                    switch (diag.Level)
                                    {
                                        case "预警": warningCount++; break;
                                        case "报警": alarmCount++; break;
                                        case "故障": faultCount++; break;
                                    }
                                }
                            }
                            catch { }
                        }

                        if (warningCount == 0 && alarmCount == 0 && faultCount == 0)
                            continue;

                        if (!result.ContainsKey(switchId))
                            result[switchId] = new Dictionary<string, Dictionary<string, int>>();

                        result[switchId][date] = new Dictionary<string, int>
                        {
                            { "预警", warningCount },
                            { "报警", alarmCount },
                            { "故障", faultCount }
                        };
                    }
                }
            }
            catch { }

            return result;
        }
    }
}
