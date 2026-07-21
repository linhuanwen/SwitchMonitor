using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Storage;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// JSON → SQLite 一次性数据迁移器。
    /// 读取 parsed_data/ 目录下的 JSON 文件，导入到 SQLite events 表。
    /// 迁移完成后写入 .migrated 标记文件，避免重复执行。
    /// </summary>
    public class DataMigrator
    {
        private readonly StorageManager _storage;
        private readonly JavaScriptSerializer _serializer;
        private readonly string _markerPath;

        /// <summary>迁移标记文件名</summary>
        public const string MarkerFileName = ".migrated_to_sqlite";

        /// <summary>
        /// 创建迁移器实例。
        /// </summary>
        /// <param name="storage">目标 StorageManager</param>
        public DataMigrator(StorageManager storage)
        {
            _storage = storage ?? throw new ArgumentNullException("storage");
            _serializer = new JavaScriptSerializer();
            _serializer.MaxJsonLength = int.MaxValue;

            string dbDir = Path.GetDirectoryName(storage.DbPath);
            _markerPath = Path.Combine(dbDir ?? ".", MarkerFileName);
        }

        /// <summary>
        /// 检查迁移是否已完成。
        /// </summary>
        public bool IsMigrated()
        {
            return File.Exists(_markerPath);
        }

        /// <summary>
        /// 从 parsed_data 目录迁移 JSON 数据到 SQLite。
        /// </summary>
        /// <param name="parsedDataDir">parsed_data 根目录</param>
        /// <returns>迁移统计信息</returns>
        public MigrationResult Migrate(string parsedDataDir)
        {
            if (IsMigrated())
                return new MigrationResult { Skipped = true, Message = "迁移标记已存在，跳过" };

            if (!Directory.Exists(parsedDataDir))
                return new MigrationResult { Skipped = true, Message = "parsed_data 目录不存在，跳过" };

            var result = new MigrationResult();
            int totalEvents = 0;
            int totalDiagnoses = 0;
            var errors = new List<string>();

            try
            {
                // 枚举所有转辙机目录
                foreach (string switchDir in Directory.GetDirectories(parsedDataDir))
                {
                    string switchId = Path.GetFileName(switchDir);

                    // 跳过非数据目录
                    if (switchId.StartsWith("."))
                        continue;

                    // 枚举该转辙机下的所有日数据 JSON 文件
                    string[] jsonFiles = Directory.GetFiles(switchDir, "*.json");
                    foreach (string jsonFile in jsonFiles)
                    {
                        string fileName = Path.GetFileName(jsonFile);

                        // .diag.json 稍后处理
                        if (fileName.EndsWith(".diag.json"))
                            continue;

                        // 提取日期: "2024-01-15.json" → "2024-01-15"
                        string date = Path.GetFileNameWithoutExtension(fileName);
                        if (date.Length != 10)
                            continue;

                        try
                        {
                            string json = File.ReadAllText(jsonFile, Encoding.UTF8);
                            var events = _serializer.Deserialize<List<SwitchEvent>>(json);

                            if (events != null && events.Count > 0)
                            {
                                // SwitchEvent → EventRecord 转换
                                var records = new List<EventRecord>();
                                foreach (var evt in events)
                                {
                                    // 修复缺失字段
                                    if (evt.Timestamp == 0 && !string.IsNullOrEmpty(evt.DateTimeStr))
                                        evt.Timestamp = ParseTimestamp(evt.DateTimeStr);
                                    if (evt.DateTimeStr == null && evt.Timestamp > 0)
                                        evt.DateTimeStr = StorageManager.UnixTimestampToDateTimeStr(evt.Timestamp);
                                    if (evt.SampleInterval <= 0)
                                        evt.SampleInterval = 0.04;

                                    records.Add(SwitchEventToRecord(switchId, evt));
                                }

                                _storage.SaveDayData(switchId, date, records);
                                totalEvents += events.Count;

                                // 迁移诊断文件
                                string diagFile = Path.Combine(switchDir, date + ".diag.json");
                                if (File.Exists(diagFile))
                                {
                                    try
                                    {
                                        string diagJson = File.ReadAllText(diagFile, Encoding.UTF8);
                                        var diagnoses = _serializer.Deserialize<List<EventDiagnosis>>(diagJson);
                                        if (diagnoses != null && diagnoses.Count > 0)
                                        {
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
                                            totalDiagnoses += diagnoses.Count;
                                        }
                                    }
                                    catch (Exception diagEx)
                                    {
                                        errors.Add(string.Format("{0}/{1}: 诊断迁移失败 - {2}",
                                            switchId, date, diagEx.Message));
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add(string.Format("{0}/{1}: {2}", switchId, date, ex.Message));
                        }
                    }
                }

                // 写入迁移标记
                File.WriteAllText(_markerPath,
                    string.Format("migrated_at={0:yyyy-MM-dd HH:mm:ss}\ntotal_events={1}\ntotal_diagnoses={2}\n",
                        DateTime.Now, totalEvents, totalDiagnoses),
                    Encoding.UTF8);

                result.Success = true;
                result.TotalEvents = totalEvents;
                result.TotalDiagnoses = totalDiagnoses;
                result.Errors = errors;
                result.Message = string.Format("迁移完成: {0} 条事件, {1} 条诊断, {2} 个错误",
                    totalEvents, totalDiagnoses, errors.Count);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "迁移失败: " + ex.Message;
                result.Errors = errors;
            }

            return result;
        }

        /// <summary>
        /// SwitchEvent → EventRecord 映射。
        /// </summary>
        public static EventRecord SwitchEventToRecord(string switchId, SwitchEvent evt)
        {
            return new EventRecord
            {
                SwitchId = switchId,
                Timestamp = evt.Timestamp,
                DateTimeStr = evt.DateTimeStr,
                Direction = evt.Direction,
                DurationSec = evt.Duration,
                SampleInterval = evt.SampleInterval,
                SampleCount = evt.SampleCount,
                CurrentABlob = StorageManager.SerializeCurve(evt.CurrentA),
                CurrentBBlob = StorageManager.SerializeCurve(evt.CurrentB),
                CurrentCBlob = StorageManager.SerializeCurve(evt.CurrentC),
                PowerBlob = StorageManager.SerializeCurve(evt.Power)
            };
        }

        /// <summary>
        /// EventRecord → SwitchEvent 映射。
        /// </summary>
        public static SwitchEvent RecordToSwitchEvent(EventRecord rec)
        {
            return new SwitchEvent
            {
                Timestamp = rec.Timestamp,
                DateTimeStr = rec.DateTimeStr,
                Direction = rec.Direction,
                Duration = rec.DurationSec,
                SampleInterval = rec.SampleInterval,
                SampleCount = rec.SampleCount,
                CurrentA = StorageManager.DeserializeCurve(rec.CurrentABlob),
                CurrentB = StorageManager.DeserializeCurve(rec.CurrentBBlob),
                CurrentC = StorageManager.DeserializeCurve(rec.CurrentCBlob),
                Power = StorageManager.DeserializeCurve(rec.PowerBlob)
            };
        }

        /// <summary>
        /// 解析 "yyyy-MM-dd HH:mm:ss" 为 Unix 时间戳。
        /// </summary>
        private static long ParseTimestamp(string dateTimeStr)
        {
            DateTime dt;
            if (DateTime.TryParseExact(dateTimeStr, "yyyy-MM-dd HH:mm:ss", null,
                System.Globalization.DateTimeStyles.None, out dt))
            {
                return StorageManager.ToUnixTimestamp(dt);
            }
            return 0;
        }
    }

    /// <summary>
    /// 迁移结果
    /// </summary>
    public class MigrationResult
    {
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public int TotalEvents { get; set; }
        public int TotalDiagnoses { get; set; }
        public string Message { get; set; }
        public List<string> Errors { get; set; }

        public MigrationResult()
        {
            Errors = new List<string>();
        }
    }
}
