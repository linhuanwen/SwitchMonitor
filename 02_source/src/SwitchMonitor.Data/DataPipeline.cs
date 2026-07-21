using System;
using System.Collections.Generic;
using System.IO;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// CSV → JSON 数据导入流水线
    /// 扫描数据源目录 → 解析 CSM2010 CSV → 合并相位 → 输出 JSON → 更新索引
    /// </summary>
    public class DataPipeline
    {
        private readonly AppConfig _config;
        private readonly IndexManager _indexManager;
        private readonly string _dataSourceDir;
        private readonly CsvDataReader _reader;
        private readonly DigitSwitchRegistry _digitRegistry;
        private readonly List<DigitEvent> _digitTimeline;  // null = digit 数据不可用
        private readonly HashSet<int> _digitPointIdsOfInterest;

        /// <summary>进度回调: (消息, 百分比0-100)</summary>
        public event Action<string, int> OnProgress;

        /// <summary>总共导入的事件数</summary>
        public int TotalEventsImported { get; private set; }

        /// <summary>
        /// D4 诊断钩子：switchId, SwitchEvent → EventDiagnosis。
        /// 由 UI/DiagTool 在装配时挂载 DiagnosisRunner.Run。
        /// null（默认）时不执行诊断。
        /// </summary>
        public Func<string, SwitchEvent, EventDiagnosis> DiagnoseHook;

        public DataPipeline(AppConfig config, IndexManager indexManager)
        {
            _config = config;
            _indexManager = indexManager;
            _reader = new CsvDataReader();

            // 解析数据源目录（相对于程序所在目录）
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(config.DataSourceDir))
            {
                _dataSourceDir = Path.Combine(baseDir, config.DataSourceDir);
                if (!Directory.Exists(_dataSourceDir))
                {
                    // 回退：尝试把 DataSourceDir 当作绝对路径
                    if (Directory.Exists(config.DataSourceDir))
                        _dataSourceDir = config.DataSourceDir;
                }
            }
            else
            {
                _dataSourceDir = baseDir;
            }

            // ── 加载 digit 配置和数据 ──
            _digitRegistry = LoadDigitRegistry(config);
            if (_digitRegistry != null)
            {
                _digitPointIdsOfInterest = _digitRegistry.GetAllPointIds();
                _digitTimeline = LoadDigitTimeline(config);
            }
        }

        /// <summary>
        /// 加载 digit.ini 道岔点号配置
        /// </summary>
        private static DigitSwitchRegistry LoadDigitRegistry(AppConfig config)
        {
            // 优先：直接解析 digit.ini
            if (!string.IsNullOrEmpty(config.DigitIniPath) && File.Exists(config.DigitIniPath))
            {
                try
                {
                    return DigitSwitchRegistry.LoadFromIni(config.DigitIniPath);
                }
                catch (Exception ex)
                {
                    Logger.Warning("解析 digit.ini 失败: " + ex.Message);
                }
            }

            // 回退：尝试同目录下的 switch_digit_config.json
            if (!string.IsNullOrEmpty(config.DigitIniPath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(config.DigitIniPath);
                    string jsonPath = Path.Combine(dir ?? ".", "switch_digit_config.json");
                    if (File.Exists(jsonPath))
                        return DigitSwitchRegistry.Load(jsonPath);
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 加载 Digit(*).dat 开关量时间线
        /// </summary>
        private static List<DigitEvent> LoadDigitTimeline(AppConfig config)
        {
            if (string.IsNullOrEmpty(config.DigitDataDir) || !Directory.Exists(config.DigitDataDir))
                return null;

            try
            {
                var reader = new DigitReader();

                // 收集所有关心点号
                var pointIds = new HashSet<int>();
                foreach (var group in config.SwitchGroups)
                {
                    if (group.DbPointId.HasValue) pointIds.Add(group.DbPointId.Value);
                    if (group.FbPointId.HasValue) pointIds.Add(group.FbPointId.Value);
                }

                if (pointIds.Count == 0)
                    return null;

                return reader.BuildTimeline(config.DigitDataDir, pointIds);
            }
            catch (Exception ex)
            {
                Logger.Warning("加载 digit 数据失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 导入所有转辙机组的数据（使用构造函数中解析的数据源目录）
        /// </summary>
        public void ImportAll()
        {
            ImportAll(_dataSourceDir);
        }

        /// <summary>
        /// 导入所有转辙机组的数据（使用自定义数据源目录）
        /// </summary>
        public void ImportAll(string customSourceDir)
        {
            TotalEventsImported = 0;

            string sourceDir = customSourceDir ?? _dataSourceDir;

            if (!Directory.Exists(sourceDir))
            {
                ReportProgress(string.Format("数据源目录不存在: {0}", sourceDir), 0);
                return;
            }

            var groups = _config.SwitchGroups;
            int total = groups.Count;

            for (int i = 0; i < total; i++)
            {
                var group = groups[i];
                int percent = (i * 100) / total;
                ReportProgress(string.Format("正在导入 {0} ({1}/{2})...", group.Label, i + 1, total), percent);

                try
                {
                    int count = ImportSwitchGroup(group, sourceDir);
                    TotalEventsImported += count;
                }
                catch (Exception ex)
                {
                    ReportProgress(string.Format("导入 {0} 失败: {1}", group.Label, ex.Message), percent);
                }
            }

            ReportProgress(string.Format("导入完成，共 {0} 个动作事件", TotalEventsImported), 100);
        }

        /// <summary>
        /// 导入单个转辙机组的数据
        /// </summary>
        /// <returns>导入的事件数</returns>
        private int ImportSwitchGroup(SwitchGroup group, string sourceDir)
        {
            // 构建两个配对文件的路径
            // 配对规则: DataFileIndex N → SwitchCurve(N).csv + SwitchCurve(N+3).csv
            int idx1 = group.DataFileIndex;
            int idx2 = group.DataFileIndex + 3;

            string file1 = Path.Combine(sourceDir, string.Format("SwitchCurve({0}).csv", idx1));
            string file2 = Path.Combine(sourceDir, string.Format("SwitchCurve({0}).csv", idx2));

            // 读取两个文件的所有行（传入文件索引以便正确检测相位）
            var allRows = new List<CsvRow>();

            if (File.Exists(file1))
            {
                var rows1 = _reader.ReadFile(file1, idx1, false);
                allRows.AddRange(rows1);
            }

            if (File.Exists(file2))
            {
                var rows2 = _reader.ReadFile(file2, idx2, true);
                allRows.AddRange(rows2);
            }

            if (allRows.Count == 0)
                return 0;

            // 按时间戳分组，合并相位数据为一个 SwitchEvent
            var eventMap = new Dictionary<long, SwitchEvent>();

            foreach (var row in allRows)
            {
                SwitchEvent evt;
                if (!eventMap.TryGetValue(row.Timestamp, out evt))
                {
                    evt = new SwitchEvent
                    {
                        Timestamp = row.Timestamp,
                        DateTimeStr = row.DateTimeStr,
                        SampleInterval = 0.04
                    };
                    eventMap[row.Timestamp] = evt;
                }

                // 根据相位类型分配采样数据
                switch (row.PhaseType)
                {
                    case 0: // 功率
                        evt.Power = ArrayToPairedList(row.Samples, row.SampleCount, 0.04);
                        break;
                    case 1: // A相电流
                        evt.CurrentA = ArrayToPairedList(row.Samples, row.SampleCount, 0.04);
                        break;
                    case 2: // B相电流
                        evt.CurrentB = ArrayToPairedList(row.Samples, row.SampleCount, 0.04);
                        break;
                    case 3: // C相电流
                        evt.CurrentC = ArrayToPairedList(row.Samples, row.SampleCount, 0.04);
                        break;
                }

                // 取最大的 SampleCount
                if (row.SampleCount > evt.SampleCount)
                    evt.SampleCount = row.SampleCount;
            }

            // 按日期分组
            var dateGroups = new Dictionary<string, List<SwitchEvent>>();

            foreach (var kvp in eventMap)
            {
                var evt = kvp.Value;

                // 计算 Duration
                evt.Duration = Math.Round(evt.SampleCount * evt.SampleInterval, 3);

                // 从 DateTimeStr 提取日期部分 "yyyy-MM-dd"
                string date;
                if (evt.DateTimeStr != null && evt.DateTimeStr.Length >= 10)
                    date = evt.DateTimeStr.Substring(0, 10);
                else
                    date = "unknown";

                List<SwitchEvent> dayList;
                if (!dateGroups.TryGetValue(date, out dayList))
                {
                    dayList = new List<SwitchEvent>();
                    dateGroups[date] = dayList;
                }
                dayList.Add(evt);
            }

            // 对每天的 event 按时间戳排序（升序），使用 digit 数据判定方向
            int totalEvents = 0;

            // 为该 switch group 创建方向判定器
            DirectionResolver directionResolver = null;
            if (_digitRegistry != null && _digitTimeline != null)
            {
                DigitPointIds ptIds;
                if (_digitRegistry.TryGetConfig(group.Id, out ptIds) &&
                    ptIds.db_point_id > 0 && ptIds.fb_point_id > 0)
                {
                    directionResolver = new DirectionResolver(
                        ptIds.db_point_id, ptIds.fb_point_id, _digitTimeline);
                }
            }

            // 交替推断状态：无 digit 数据时，按道岔交替规律推断方向
            // 道岔不可连续两次同方向动作 — 序列必为 定位→反位, 反位→定位, 定位→反位, ...
            string altLastDir = null;

            foreach (var kvp in dateGroups)
            {
                var events = kvp.Value;
                events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                // 使用 digit 数据判定方向（流式查找）
                if (directionResolver != null)
                {
                    directionResolver.Reset();
                    foreach (var evt in events)
                    {
                        string direction = directionResolver.Resolve(evt.Timestamp);
                        if (direction != null)
                            evt.Direction = direction;
                        // else: 保持默认值 "未知"
                    }
                }
                else
                {
                    // 无 digit 数据：按交替规律推断方向
                    // 首次从 反位→定位 开始，使第一个事件标注为 定位→反位
                    if (altLastDir == null)
                        altLastDir = "反位→定位";
                    foreach (var evt in events)
                    {
                        if (string.IsNullOrEmpty(evt.Direction) || evt.Direction == "未知")
                        {
                            altLastDir = (altLastDir == "定位→反位")
                                ? "反位→定位"
                                : "定位→反位";
                            evt.Direction = altLastDir;
                        }
                    }
                }

                // 保存到 parsed_data 并更新索引
                _indexManager.SaveDayData(group.Id, kvp.Key, events);

                // D4: 保存后执行诊断（若 DiagnoseHook 已挂载）
                if (DiagnoseHook != null)
                {
                    try
                    {
                        var diagnoses = new List<EventDiagnosis>(events.Count);
                        foreach (var evt in events)
                        {
                            EventDiagnosis diag;
                            try
                            {
                                diag = DiagnoseHook(group.Id, evt);
                                if (diag == null)
                                {
                                    // 诊断钩子返回 null → 创建默认正常诊断（确保每个事件都有对应记录）
                                    diag = new EventDiagnosis
                                    {
                                        Timestamp = evt.Timestamp,
                                        Level = "正常",
                                        Results = new List<DiagnosisItem>()
                                    };
                                }
                            }
                            catch (Exception ex)
                            {
                                // 单个事件诊断抛异常 → 创建缺省报警（与 RerunAll 行为一致）
                                Logger.Error(string.Format("诊断失败 switchId={0} eventTs={1}", group.Id, evt.Timestamp), ex);
                                diag = new EventDiagnosis
                                {
                                    Timestamp = evt.Timestamp,
                                    Level = "报警",
                                    Results = new List<DiagnosisItem>
                                    {
                                        new DiagnosisItem
                                        {
                                            RuleId = "R0",
                                            RuleName = "采集异常",
                                            Level = "报警",
                                            Description = "诊断引擎执行异常: " + ex.Message,
                                            Value = 0,
                                            Reference = 0
                                        }
                                    }
                                };
                            }
                            diagnoses.Add(diag);
                        }

                        // 始终保存诊断结果（diagnoses 数量与 events 一致），
                        // 即使全为正常也能正确清除旧的 alarms_index 条目
                        _indexManager.SaveDayDiagnosis(group.Id, kvp.Key, diagnoses);
                    }
                    catch (Exception ex)
                    {
                        // 诊断环节整体异常不中断导入
                        Logger.Error(string.Format("诊断批次失败 switchId={0} date={1}", group.Id, kvp.Key), ex);
                    }
                }

                totalEvents += events.Count;
            }

            return totalEvents;
        }

        /// <summary>
        /// 将采样数组转为 List{double[]}（[t, v] 对格式）
        /// </summary>
        private static List<double[]> ArrayToPairedList(double[] samples, int count, double interval)
        {
            var list = new List<double[]>(count);
            for (int i = 0; i < count; i++)
                list.Add(new double[] { Math.Round(i * interval, 3), samples[i] });
            return list;
        }

        private void ReportProgress(string message, int percent)
        {
            var handler = OnProgress;
            if (handler != null)
                handler(message, percent);
        }
    }
}
