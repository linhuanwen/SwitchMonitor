using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SwitchMonitor.Data;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 诊断运行器：封装 FeatureExtractor + DiagnosisEngine + DiagnosisAggregator 的组合调用。
    /// 供 UI/DiagTool 在装配 DiagnoseHook 时使用，一行完成：engine.Diagnose + FeatureExtractor.Extract + 汇总。
    /// </summary>
    public static class DiagnosisRunner
    {
        /// <summary>
        /// 最近一次 CreateHook 创建的 DiagnosisEngine 实例，供外部热更新标准曲线。
        /// </summary>
        public static DiagnosisEngine LastEngine { get; private set; }

        /// <summary>
        /// 根据配置创建诊断钩子委托（工厂方法）。
        /// 返回 null 表示诊断已禁用（config.Diagnosis.Enabled == false）。
        /// </summary>
        public static Func<string, SwitchEvent, EventDiagnosis> CreateHook(DiagnosisConfig diagConfig, string parsedDataDir = null)
        {
            if (diagConfig == null || !diagConfig.Enabled)
                return null;

            string rulesDir = diagConfig.RulesDir;
            if (!Path.IsPathRooted(rulesDir))
                rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);

            // 基线目录 = 站点 parsed_data 目录（按站点隔离），未提供时回退到 rulesDir
            string baselinesDir = parsedDataDir;
            if (!string.IsNullOrEmpty(baselinesDir) && !Path.IsPathRooted(baselinesDir))
                baselinesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, baselinesDir);

            var engine = new DiagnosisEngine();
            engine.Initialize(rulesDir, baselinesDir);
            LastEngine = engine;

            // D6: 设置 parsed_data 目录用于 T1 趋势分析
            if (!string.IsNullOrEmpty(parsedDataDir))
            {
                if (!Path.IsPathRooted(parsedDataDir))
                    parsedDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, parsedDataDir);
                engine.SetParsedDataDir(parsedDataDir);
            }

            var capturedEngine = engine;
            string capturedParsedDir = parsedDataDir;
            return (switchId, evt) => Run(capturedEngine, switchId, evt, capturedParsedDir);
        }

        /// <summary>
        /// 对一次道岔动作执行完整诊断流程：特征提取 → 规则评估 → 聚合 → 日志。
        /// </summary>
        /// <param name="engine">已初始化的诊断引擎</param>
        /// <param name="switchId">道岔标识（如 "4-J"）</param>
        /// <param name="evt">道岔动作事件（需含 Power 采样数据）</param>
        /// <returns>EventDiagnosis（Data 项目 POCO，可直接序列化存储）</returns>
        public static EventDiagnosis Run(IDiagnosisEngine engine, string switchId, SwitchEvent evt, string parsedDataDir = null)
        {
            if (engine == null)
                throw new ArgumentNullException("engine");
            if (evt == null)
                throw new ArgumentNullException("evt");

            // 1. 特征提取
            var features = FeatureExtractor.Extract(evt);

            // 2. 规则评估（传递方向以选择对应基线）
            List<DiagnosisResult> results;
            try
            {
                results = engine.Diagnose(switchId, features, evt.Direction);
            }
            catch (Exception ex)
            {
                // 诊断失败不向上抛，记录错误日志后返回采集异常
                Logger.Error("诊断引擎执行失败 switchId=" + switchId + " eventTs=" + evt.Timestamp, ex);
                return new EventDiagnosis
                {
                    Timestamp = evt.Timestamp,
                    Level = DiagnosisLevel.Alarm,
                    Results = new List<DiagnosisItem>
                    {
                        new DiagnosisItem
                        {
                            RuleId = "R0",
                            RuleName = "采集异常",
                            Level = DiagnosisLevel.Alarm,
                            Description = "诊断引擎执行异常: " + ex.Message,
                            Value = 0,
                            Reference = 0
                        }
                    }
                };
            }

            // 3. 综合级别
            string overallLevel = DiagnosisAggregator.OverallLevel(results);

            // 4. 转换 DiagnosisResult → DiagnosisItem（Data POCO）
            var items = new List<DiagnosisItem>();
            foreach (var r in results)
            {
                items.Add(new DiagnosisItem
                {
                    RuleId = r.RuleId,
                    RuleName = r.RuleName,
                    Level = r.Level,
                    Description = r.Description,
                    Value = r.Value,
                    Reference = r.Reference
                });
            }

            // 5. 写诊断日志 diag.log
            WriteDiagLog(switchId, evt, features, results, overallLevel);

            // 6. D6: 追加特征到 features.json（用于 T1 趋势分析）
            if (!string.IsNullOrEmpty(parsedDataDir) && features.IsValid)
            {
                try
                {
                    FeaturesStore.Append(parsedDataDir, switchId,
                        evt.Timestamp, features.DurationSec, features.SpikePeak,
                        features.UnlockMean, features.ConvMean, features.LockMean, features.TailMean,
                        evt.Direction);
                }
                catch (Exception ex)
                {
                    // features.json 写入失败不中断诊断
                    Logger.Warning("features.json 追加失败 switchId=" + switchId + ": " + ex.Message);
                }
            }

            // 6b. D7: 追加电流特征到 current_features.json
            if (!string.IsNullOrEmpty(parsedDataDir))
            {
                try
                {
                    var currentFeatures = CurrentFeatureExtractor.Extract(evt);
                    CurrentFeaturesStore.Append(parsedDataDir, switchId, evt.Timestamp, currentFeatures);
                }
                catch (Exception ex)
                {
                    // current_features.json 写入失败不中断诊断
                    Logger.Warning("current_features.json 追加失败 switchId=" + switchId + ": " + ex.Message);
                }
            }

            return new EventDiagnosis
            {
                Timestamp = evt.Timestamp,
                Level = overallLevel,
                Results = items
            };
        }

        /// <summary>
        /// 重跑全部已导入数据的诊断（不重新导 CSV）。
        /// 遍历 index 中所有 switchId×date，对每天的每个事件重新执行 FeatureExtract + Diagnose。
        /// 完成后覆盖 .diag.json + alarms_index.json。
        /// </summary>
        /// <param name="indexManager">已初始化的 IndexManager（指向 parsed_data 目录）</param>
        /// <param name="engine">已初始化的诊断引擎</param>
        public static void RerunAll(IndexManager indexManager, IDiagnosisEngine engine)
        {
            if (indexManager == null)
                throw new ArgumentNullException("indexManager");
            if (engine == null)
                throw new ArgumentNullException("engine");

            int totalEvents = 0;
            int totalAbnormal = 0;

            // 获取全部转辙机 ID
            var switchIds = indexManager.GetAllSwitchIds();
            foreach (var switchId in switchIds)
            {
                var dates = indexManager.GetDates(switchId);
                foreach (var date in dates)
                {
                    var dayEvents = indexManager.LoadDayData(switchId, date);
                    var diagnoses = new List<EventDiagnosis>();

                    foreach (var evt in dayEvents)
                    {
                        EventDiagnosis result;
                        try
                        {
                            result = Run(engine, switchId, evt, indexManager.ParsedDataDir); // 方向已通过 evt.Direction 传入
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(string.Format("RerunAll 诊断失败 switchId={0} eventTs={1}", switchId, evt.Timestamp), ex);
                            result = new EventDiagnosis
                            {
                                Timestamp = evt.Timestamp,
                                Level = DiagnosisLevel.Alarm,
                                Results = new List<DiagnosisItem>
                                {
                                    new DiagnosisItem
                                    {
                                        RuleId = "R0",
                                        RuleName = "采集异常",
                                        Level = DiagnosisLevel.Alarm,
                                        Description = "诊断引擎执行异常: " + ex.Message,
                                        Value = 0,
                                        Reference = 0
                                    }
                                }
                            };
                        }
                        diagnoses.Add(result);
                        totalEvents++;
                        if (result.Level != DiagnosisLevel.Normal)
                            totalAbnormal++;
                    }

                    // 覆盖写入 .diag.json
                    try
                    {
                        indexManager.SaveDayDiagnosis(switchId, date, diagnoses);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(string.Format("RerunAll 保存失败 switchId={0} date={1}", switchId, date), ex);
                    }
                }
            }

            Logger.Info(string.Format("RerunAll 完成: 共 {0} 个事件, {1} 条异常", totalEvents, totalAbnormal));
        }

        /// <summary>
        /// 重跑指定转辙机列表的诊断（不重新导 CSV）。
        /// 与 RerunAll 逻辑一致，但只处理传入的 switchIds。
        /// </summary>
        /// <param name="indexManager">已初始化的 IndexManager</param>
        /// <param name="engine">已初始化的诊断引擎</param>
        /// <param name="switchIds">要处理的转辙机 ID 列表</param>
        public static void RerunSelected(IndexManager indexManager, IDiagnosisEngine engine, List<string> switchIds)
        {
            if (indexManager == null)
                throw new ArgumentNullException("indexManager");
            if (engine == null)
                throw new ArgumentNullException("engine");
            if (switchIds == null || switchIds.Count == 0)
                return;

            int totalEvents = 0;
            int totalAbnormal = 0;

            foreach (var switchId in switchIds)
            {
                var dates = indexManager.GetDates(switchId);
                foreach (var date in dates)
                {
                    var dayEvents = indexManager.LoadDayData(switchId, date);
                    var diagnoses = new List<EventDiagnosis>();

                    foreach (var evt in dayEvents)
                    {
                        EventDiagnosis result;
                        try
                        {
                            result = Run(engine, switchId, evt, indexManager.ParsedDataDir);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(string.Format("RerunSelected 诊断失败 switchId={0} eventTs={1}", switchId, evt.Timestamp), ex);
                            result = new EventDiagnosis
                            {
                                Timestamp = evt.Timestamp,
                                Level = DiagnosisLevel.Alarm,
                                Results = new List<DiagnosisItem>
                                {
                                    new DiagnosisItem
                                    {
                                        RuleId = "R0",
                                        RuleName = "采集异常",
                                        Level = DiagnosisLevel.Alarm,
                                        Description = "诊断引擎执行异常: " + ex.Message,
                                        Value = 0,
                                        Reference = 0
                                    }
                                }
                            };
                        }
                        diagnoses.Add(result);
                        totalEvents++;
                        if (result.Level != DiagnosisLevel.Normal)
                            totalAbnormal++;
                    }

                    try
                    {
                        indexManager.SaveDayDiagnosis(switchId, date, diagnoses);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(string.Format("RerunSelected 保存失败 switchId={0} date={1}", switchId, date), ex);
                    }
                }
            }

            Logger.Info(string.Format("RerunSelected 完成 ({0} 台转辙机): 共 {1} 个事件, {2} 条异常",
                switchIds.Count, totalEvents, totalAbnormal));
        }

        /// <summary>
        /// 写入诊断运行日志到 diag.log。
        /// 格式紧贴 PRD §4.7 / D4 §C 规格。
        /// </summary>
        private static void WriteDiagLog(string switchId, SwitchEvent evt, CurveFeatures f,
            List<DiagnosisResult> results, string overallLevel)
        {
            var sb = new StringBuilder();

            // 行1: [时间戳] switchId=... eventTs=...
            sb.AppendFormat("switchId={0} eventTs={1}", switchId, evt.Timestamp);

            if (results.Count == 0)
            {
                // 正常事件仅一行概要
                sb.AppendFormat(" → {0} (R0-R8 无命中)", overallLevel);
                Logger.LogDiagnosis(sb.ToString());
                return;
            }

            sb.AppendLine();

            // Features 行
            sb.AppendFormat("  Features: dur={0:F2}s spikePeak={1:F3} convMean={2:F3} lockMean={3:F3} tailMean={4:F3} stepRatio={5:F3}",
                f.DurationSec, f.SpikePeak, f.ConvMean, f.LockMean, f.TailMean, f.StepRatio);
            sb.AppendFormat(" unlockMean={0:F3} activeEnd={1} isFullWindow={2} isValid={3}",
                f.UnlockMean, f.ActiveEnd, f.IsFullWindow, f.IsValid);
            sb.AppendLine();

            // Baseline 行（如果基线非零则输出）
            // 基线值嵌入在 DiagnosisResult.Reference 中，不在此方法重复获取
            // 只输出引擎已使用的特征参照值

            // 规则结果逐条
            bool terminated = false;
            foreach (var r in results)
            {
                sb.AppendFormat("  {0}: {1}", r.RuleId, r.Description);
                if ((r.RuleId == "R1" || r.RuleId == "R2") && !terminated)
                {
                    sb.Append(" — 终止");
                    terminated = true;
                }
                sb.AppendLine();
            }

            // Overall 行
            sb.AppendFormat("  Overall: {0} ({1}条)", overallLevel, results.Count);

            Logger.LogDiagnosis(sb.ToString());
        }
    }
}
