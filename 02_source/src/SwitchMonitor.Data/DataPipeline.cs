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

            // 对每天的 event 按时间戳排序（升序），交替分配方向
            int totalEvents = 0;
            foreach (var kvp in dateGroups)
            {
                var events = kvp.Value;
                events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                for (int i = 0; i < events.Count; i++)
                {
                    events[i].Direction = (i % 2 == 0) ? "定位→反位" : "反位→定位";
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
                            try
                            {
                                var diag = DiagnoseHook(group.Id, evt);
                                if (diag != null)
                                    diagnoses.Add(diag);
                            }
                            catch (Exception ex)
                            {
                                // 单个事件诊断失败不中断导入
                                Logger.Error(string.Format("诊断失败 switchId={0} eventTs={1}", group.Id, evt.Timestamp), ex);
                            }
                        }

                        if (diagnoses.Count > 0)
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
