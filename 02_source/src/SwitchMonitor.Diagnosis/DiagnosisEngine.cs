using System;
using System.Collections.Generic;
using System.IO;
using SwitchMonitor.Data;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// D3+D6 规则引擎：R0-R8 + T1/P1 诊断规则评估。
    /// 实现 IDiagnosisEngine 接口，无状态可重入（除 Initialize 加载的配置外）。
    /// </summary>
    public class DiagnosisEngine : IDiagnosisEngine
    {
        private ThresholdStore _thresholds;
        private BaselineStore _baselines;
        private Dictionary<string, StandardCurve> _standardCurves;
        private string _rulesDir;
        private string _baselinesDir;
        private string _parsedDataDir;

        // 已告警"无基线"的道岔集合，避免重复日志刷屏
        private readonly HashSet<string> _warnedNoBaseline = new HashSet<string>();

        // T1 trend results cache: switchId -> 最新趋势结果（避免同一事件重复触发）
        private readonly Dictionary<string, DiagnosisResult> _t1Cache = new Dictionary<string, DiagnosisResult>();

        // D9: 近邻正常事件特征缓存 — switchId → 最近 N 条正常事件的特征（最新的在前）
        private readonly Dictionary<string, List<CurveFeatures>> _recentNormalCache = new Dictionary<string, List<CurveFeatures>>();
        private const int MaxRecentNormalCache = 30; // 最多保留 30 条（覆盖约 3 天）

        /// <summary>
        /// 初始化引擎：加载 rulesDir 目录下的 thresholds.json（全局规则），
        /// 以及 baselinesDir 目录下的 baselines.json + standard_curves/（按站点隔离）。
        /// baselinesDir 为 null 时回退到 rulesDir（向后兼容）。
        /// 站点目录下基线文件缺失时，自动从全局 rulesDir 迁移（一次性升级兼容）。
        /// 文件缺失/损坏 → 使用内置默认阈值 + 空基线，并 Logger.Warning。
        /// </summary>
        public void Initialize(string rulesDir, string baselinesDir = null)
        {
            _rulesDir = rulesDir;
            _baselinesDir = baselinesDir ?? rulesDir;
            string thresholdsPath = Path.Combine(rulesDir, "thresholds.json");

            // 迁移：站点基线目录下无 baselines.json 时，尝试从全局 rulesDir 复制
            MigrateBaselinesIfNeeded(rulesDir, _baselinesDir);

            string baselinesPath = Path.Combine(_baselinesDir, "baselines.json");

            _thresholds = ThresholdStore.Load(thresholdsPath);
            _baselines = BaselineStore.Load(baselinesPath);

            // 加载标准曲线（优先于参考曲线用于 P1 对比）
            string standardCurveDir = Path.Combine(_baselinesDir, "standard_curves");
            _standardCurves = StandardCurveStore.LoadAll(standardCurveDir);

            _warnedNoBaseline.Clear();
            _t1Cache.Clear();

            int ruleCount = _thresholds.rules != null ? _thresholds.rules.Count : 0;
            int baselineCount = _baselines.Switches != null ? _baselines.Switches.Count : 0;
            int scCount = _standardCurves != null ? _standardCurves.Count : 0;
            Logger.Info(string.Format("DiagnosisEngine 初始化完成: {0} 条规则, {1} 台道岔基线, {2} 条标准曲线",
                ruleCount, baselineCount, scCount));
        }

        /// <summary>
        /// 显式实现 IDiagnosisEngine.Initialize(string)，满足接口契约。
        /// 调用双参数版本，baselinesDir 回退到 rulesDir。
        /// </summary>
        void IDiagnosisEngine.Initialize(string rulesDir)
        {
            Initialize(rulesDir, null);
        }

        /// <summary>
        /// 热切换基线目录：重新加载 baselines.json + standard_curves/，不重读 thresholds.json。
        /// 用于用户在站点间切换时即时生效，无需重建引擎实例。
        /// </summary>
        public void ReloadBaselines(string baselinesDir)
        {
            if (string.IsNullOrEmpty(baselinesDir))
                return;

            _baselinesDir = baselinesDir;

            // 迁移：新站点目录下无 baselines.json 时，尝试从全局 rulesDir 复制
            MigrateBaselinesIfNeeded(_rulesDir, baselinesDir);

            string baselinesPath = Path.Combine(baselinesDir, "baselines.json");
            string standardCurveDir = Path.Combine(baselinesDir, "standard_curves");

            _baselines = BaselineStore.Load(baselinesPath);
            _standardCurves = StandardCurveStore.LoadAll(standardCurveDir);

            // 清空告警和缓存（换了站点，之前的缓存不再有效）
            _warnedNoBaseline.Clear();
            _t1Cache.Clear();
            _recentNormalCache.Clear();

            int baselineCount = _baselines.Switches != null ? _baselines.Switches.Count : 0;
            int scCount = _standardCurves != null ? _standardCurves.Count : 0;
            Logger.Info(string.Format("DiagnosisEngine 基线已重载: {0} 台道岔基线, {1} 条标准曲线 (目录: {2})",
                baselineCount, scCount, baselinesDir));
        }

        /// <summary>
        /// 一次性迁移：站点基线目录下无 baselines.json 时，从全局 rulesDir 复制。
        /// 升级兼容——避免用户手动到每个站点重建基线。
        /// 同时迁移 standard_curves/ 下所有文件。
        /// </summary>
        private static void MigrateBaselinesIfNeeded(string rulesDir, string baselinesDir)
        {
            if (string.IsNullOrEmpty(rulesDir) || string.IsNullOrEmpty(baselinesDir))
                return;
            // rulesDir 和 baselinesDir 相同时无需迁移（未启用站点隔离）
            if (string.Equals(rulesDir, baselinesDir, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                // ── 迁移 baselines.json ──
                string stationBaselinesPath = Path.Combine(baselinesDir, "baselines.json");
                string globalBaselinesPath = Path.Combine(rulesDir, "baselines.json");
                if (!File.Exists(stationBaselinesPath) && File.Exists(globalBaselinesPath))
                {
                    string dir = Path.GetDirectoryName(stationBaselinesPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.Copy(globalBaselinesPath, stationBaselinesPath, overwrite: false);
                    Logger.Info(string.Format("基线已自动迁移: {0} → {1}",
                        globalBaselinesPath, stationBaselinesPath));
                }

                // ── 迁移 current_baselines.json ──
                string stationCurrentPath = Path.Combine(baselinesDir, "current_baselines.json");
                string globalCurrentPath = Path.Combine(rulesDir, "current_baselines.json");
                if (!File.Exists(stationCurrentPath) && File.Exists(globalCurrentPath))
                {
                    File.Copy(globalCurrentPath, stationCurrentPath, overwrite: false);
                    Logger.Info(string.Format("电流基线已自动迁移: {0} → {1}",
                        globalCurrentPath, stationCurrentPath));
                }

                // ── 迁移 standard_curves/ ──
                string stationScDir = Path.Combine(baselinesDir, "standard_curves");
                string globalScDir = Path.Combine(rulesDir, "standard_curves");
                if (!Directory.Exists(stationScDir) && Directory.Exists(globalScDir))
                {
                    // 复制整个目录（递归）
                    CopyDirectoryRecursive(globalScDir, stationScDir);
                    Logger.Info(string.Format("标准曲线已自动迁移: {0} → {1}",
                        globalScDir, stationScDir));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("基线迁移失败（不影响功能，可手动重建）: " + ex.Message);
            }
        }

        /// <summary>
        /// 递归复制目录（用于 standard_curves 迁移）
        /// </summary>
        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: false);
            }
            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string destSub = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectoryRecursive(subDir, destSub);
            }
        }

        /// <summary>
        /// 设置 D6 所需的 parsed_data 目录（用于读取 features.json 执行 T1 分析）。
        /// </summary>
        public void SetParsedDataDir(string parsedDataDir)
        {
            _parsedDataDir = parsedDataDir;
        }

        /// <summary>
        /// 对一次道岔动作进行诊断。
        /// 判定顺序：R0 → R1 → R2 命中即终止；否则 R3-R8 全部评估可多命。
        /// </summary>
        public List<DiagnosisResult> Diagnose(string switchId, CurveFeatures f, string direction = null)
        {
            var results = new List<DiagnosisResult>();

            // === R0: 采集异常（不可配置、恒启用、恒"报警"） ===
            if (!f.IsValid)
            {
                results.Add(new DiagnosisResult
                {
                    RuleId = "R0",
                    RuleName = "采集异常",
                    Level = DiagnosisLevel.Alarm,
                    Description = "采集异常，曲线无效",
                    Value = f.SampleCount,
                    Reference = 0.0
                });
                return results; // R0 终止
            }

            // === 获取该道岔对应方向的基线 ===
            SwitchBaseline baseline = null;
            if (_baselines != null && _baselines.Switches != null)
            {
                // 1) 精确匹配：switchId + direction
                string exactKey = BaselineStore.MakeKey(switchId, direction);
                if (!_baselines.Switches.TryGetValue(exactKey, out baseline))
                {
                    // 2) 降级：另一方向的基线（同 switchId 但不同方向）
                    string fallbackDir = (direction == BaselineStore.DirNormalToReverse)
                        ? BaselineStore.DirReverseToNormal
                        : BaselineStore.DirNormalToReverse;
                    string fallbackKey = BaselineStore.MakeKey(switchId, fallbackDir);
                    if (_baselines.Switches.TryGetValue(fallbackKey, out baseline))
                    {
                        if (!_warnedNoBaseline.Contains(switchId + "|dir"))
                        {
                            _warnedNoBaseline.Add(switchId + "|dir");
                            Logger.Warning(string.Format("道岔{0}缺少方向'{1}'的基线，降级使用'{2}'方向基线",
                                switchId, direction, fallbackDir));
                        }
                    }
                    else
                    {
                        // 3) 再降级：尝试无方向的旧格式 key（向后兼容）
                        if (!_baselines.Switches.TryGetValue(switchId, out baseline))
                        {
                            if (!_warnedNoBaseline.Contains(switchId))
                            {
                                _warnedNoBaseline.Add(switchId);
                                Logger.Warning("道岔" + switchId + "无基线，仅执行硬规则");
                            }
                        }
                    }
                }
            }

            // === 无基线：仅评估 R1 的 isFullWindow 分支 ===
            if (baseline == null)
            {
                if (!_warnedNoBaseline.Contains(switchId))
                {
                    _warnedNoBaseline.Add(switchId);
                    Logger.Warning("道岔" + switchId + "无基线，仅执行硬规则");
                }

                if (f.IsFullWindow)
                {
                    results.Add(MakeR1Result(f, baseline, GetThreshold("R1")));
                }
                return results; // 空列表 = 正常
            }

            // === R1: 动作超时/未完成 ===
            RuleThreshold t1 = GetThreshold("R1");
            if (t1 != null && t1.enabled)
            {
                double durOver = t1.durOverRefSeconds;
                if (f.IsFullWindow || f.DurationSec > baseline.RefDurationSec + durOver)
                {
                    results.Add(MakeR1Result(f, baseline, t1));
                    return results; // R1 终止
                }
            }

            // === R2: 动作夭折 ===
            RuleThreshold t2 = GetThreshold("R2");
            if (t2 != null && t2.enabled)
            {
                if (f.DurationSec < baseline.RefDurationSec * t2.durUnderRefRatio)
                {
                    results.Add(new DiagnosisResult
                    {
                        RuleId = "R2",
                        RuleName = "动作夭折",
                        Level = t2.level,
                        Description = string.Format("动作时长{0:F2}s，不足参考{1:F2}s的{2:P0}，动作夭折",
                            f.DurationSec, baseline.RefDurationSec, t2.durUnderRefRatio),
                        Value = f.DurationSec,
                        Reference = baseline.RefDurationSec
                    });
                    return results; // R2 终止
                }
            }

            // === R3-R8: 依次评估，全部命中加入结果列表 ===

            // R3: 动作时长偏差
            EvaluateR3(f, baseline, results);

            // R4: 启动峰值偏高
            EvaluateR4(f, baseline, results);

            // R5: 转换段功率偏高
            EvaluateR5(f, baseline, results);

            // R6: 转换段台阶突变
            EvaluateR6(f, baseline, results);

            // R7: 解锁段偏高
            EvaluateR7(f, baseline, results);

            // R8: 缓放段异常
            EvaluateR8(f, baseline, results);

            // R9: 锁闭段异常
            EvaluateR9(f, baseline, results);

            // ── D6: T1 渐变劣化 + P1 逐点对比（不终止，可多命）──
            EvaluateT1(switchId, f, baseline, results);
            EvaluateP1(switchId, f, baseline, results);

            return results; // 空列表 = 正常
        }

        /// <summary>
        /// 获取规则阈值，带 enabled 检查简化调用。
        /// </summary>
        private RuleThreshold GetThreshold(string ruleId)
        {
            if (_thresholds == null)
                return null;
            return _thresholds.Get(ruleId);
        }

        // ── R1 结果构造 ──

        private static DiagnosisResult MakeR1Result(CurveFeatures f, SwitchBaseline b, RuleThreshold t)
        {
            string level = (t != null) ? t.level : DiagnosisLevel.Fault;
            double refDur = (b != null) ? b.RefDurationSec : 0.0;
            double durOver = (t != null) ? t.durOverRefSeconds : 3.0;

            string desc;
            if (f.IsFullWindow && (b == null || f.DurationSec <= refDur + durOver))
            {
                desc = string.Format("动作时长{0:F2}s，打满录制窗口≥31.2s，疑似卡阻/空转未完成",
                    f.DurationSec);
            }
            else
            {
                desc = string.Format("动作时长{0:F2}s，超过参考{1:F2}s+{2:F1}s，疑似卡阻/空转未完成",
                    f.DurationSec, refDur, durOver);
            }

            return new DiagnosisResult
            {
                RuleId = "R1",
                RuleName = "动作超时/未完成",
                Level = level,
                Description = desc,
                Value = f.DurationSec,
                Reference = refDur
            };
        }

        // ── R3: 动作时长偏差 ──

        private void EvaluateR3(CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("R3");
            if (t == null || !t.enabled) return;

            double deviation = Math.Abs(f.DurationSec - b.RefDurationSec);
            if (deviation > t.maxDeviationSeconds)
            {
                results.Add(new DiagnosisResult
                {
                    RuleId = "R3",
                    RuleName = "动作时长偏差",
                    Level = t.level,
                    Description = string.Format("动作时长偏差{0:F2}s，超出阈值{1:F2}s，疑似阻力变化",
                        deviation, t.maxDeviationSeconds),
                    Value = f.DurationSec,
                    Reference = b.RefDurationSec
                });
            }
        }

        // ── R4: 启动峰值偏高 ──

        private void EvaluateR4(CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("R4");
            if (t == null || !t.enabled) return;

            if (f.SpikePeak > b.RefSpikePeak * t.overRefRatio)
            {
                results.Add(new DiagnosisResult
                {
                    RuleId = "R4",
                    RuleName = "启动峰值偏高",
                    Level = t.level,
                    Description = string.Format("启动峰值{0:F3}kW，超过参考{1:F3}kW的{2:F1}倍，疑似启动回路/机械卡滞",
                        f.SpikePeak, b.RefSpikePeak, t.overRefRatio),
                    Value = f.SpikePeak,
                    Reference = b.RefSpikePeak
                });
            }
        }

        // ── R5: 转换段功率偏高 ──

        private void EvaluateR5(CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("R5");
            if (t == null || !t.enabled) return;

            if (f.ConvMean > b.RefConvMean * t.overRefRatio)
            {
                results.Add(new DiagnosisResult
                {
                    RuleId = "R5",
                    RuleName = "转换段功率偏高",
                    Level = t.level,
                    Description = string.Format("转换段功率{0:F3}kW，超过参考{1:F3}kW的{2:F1}倍，疑似转换阻力增大",
                        f.ConvMean, b.RefConvMean, t.overRefRatio),
                    Value = f.ConvMean,
                    Reference = b.RefConvMean
                });
            }
        }

        // ── R6: 转换段台阶突变 ──

        private void EvaluateR6(CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("R6");
            if (t == null || !t.enabled) return;

            if (f.StepRatio > t.maxStepRatio || f.StepRatio < t.minStepRatio)
            {
                string reason = f.StepRatio > t.maxStepRatio ? "偏大（中途受阻）" : "偏小（空转）";
                results.Add(new DiagnosisResult
                {
                    RuleId = "R6",
                    RuleName = "转换段台阶突变",
                    Level = t.level,
                    Description = string.Format("转换段台阶比{0:F3}，超出正常范围[{1:F2}, {2:F2}]，{3}",
                        f.StepRatio, t.minStepRatio, t.maxStepRatio, reason),
                    Value = f.StepRatio,
                    Reference = 1.0 // 理想台阶比为 1.0
                });
            }
        }

        // ── R7: 解锁段偏高 ──

        private void EvaluateR7(CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("R7");
            if (t == null || !t.enabled) return;

            if (f.UnlockMean > b.RefUnlockMean * t.overRefRatio)
            {
                results.Add(new DiagnosisResult
                {
                    RuleId = "R7",
                    RuleName = "解锁段偏高",
                    Level = t.level,
                    Description = string.Format("解锁段功率{0:F3}kW，超过参考{1:F3}kW的{2:F1}倍，疑似密贴过紧/卡缺口",
                        f.UnlockMean, b.RefUnlockMean, t.overRefRatio),
                    Value = f.UnlockMean,
                    Reference = b.RefUnlockMean
                });
            }
        }

        // ── R8: 缓放段异常 ──

        private void EvaluateR8(CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("R8");
            if (t == null || !t.enabled) return;

            // R8 特例：TailMean == 0 视为"缓放段缺失"
            if (f.TailMean == 0.0)
            {
                results.Add(new DiagnosisResult
                {
                    RuleId = "R8",
                    RuleName = "缓放段异常",
                    Level = t.level,
                    Description = "缓放段缺失（功率曲线尾部无缓放段），疑似锁闭/开闭器异常",
                    Value = 0.0,
                    Reference = b.RefTailMean
                });
                return;
            }

            // 偏离参考值 ±deviationRatio
            double deviation = Math.Abs(f.TailMean - b.RefTailMean) / Math.Max(b.RefTailMean, 0.001);
            if (deviation > t.deviationRatio)
            {
                string direction = f.TailMean > b.RefTailMean ? "偏高" : "偏低";
                results.Add(new DiagnosisResult
                {
                    RuleId = "R8",
                    RuleName = "缓放段异常",
                    Level = t.level,
                    Description = string.Format("缓放段功率{0:F3}kW，{1}参考{2:F3}kW超过{3:P0}，疑似锁闭/开闭器异常",
                        f.TailMean, direction, b.RefTailMean, t.deviationRatio),
                    Value = f.TailMean,
                    Reference = b.RefTailMean
                });
            }
        }

        // ── R9: 锁闭段异常 ──

        private void EvaluateR9(CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("R9");
            if (t == null || !t.enabled) return;

            // 无基线时不触发
            if (b == null) return;

            // R9 特例：LockMean == 0 视为"锁闭段缺失"（极短曲线无锁闭段）
            if (f.LockMean == 0.0)
            {
                // activeEnd > 50 本该有锁闭段但提取为 0，视为异常
                if (f.ActiveEnd > 50)
                {
                    results.Add(new DiagnosisResult
                    {
                        RuleId = "R9",
                        RuleName = "锁闭段异常",
                        Level = t.level,
                        Description = "锁闭段缺失（转换与缓放之间无卸载凹口），疑似锁闭机构卡滞/开闭器异常",
                        Value = 0.0,
                        Reference = b.RefLockMean
                    });
                }
                return;
            }

            // 判据①：偏离参考值 ±deviationRatio
            double deviation = Math.Abs(f.LockMean - b.RefLockMean) / Math.Max(b.RefLockMean, 0.001);
            if (deviation > t.deviationRatio)
            {
                string direction = f.LockMean > b.RefLockMean ? "偏高" : "偏低";
                results.Add(new DiagnosisResult
                {
                    RuleId = "R9",
                    RuleName = "锁闭段异常",
                    Level = t.level,
                    Description = string.Format("锁闭段功率{0:F3}kW，{1}参考{2:F3}kW超过{3:P0}，疑似锁闭机构卡滞/开闭器异常",
                        f.LockMean, direction, b.RefLockMean, t.deviationRatio),
                    Value = f.LockMean,
                    Reference = b.RefLockMean
                });
                return;
            }

            // 判据②：锁闭段显著高于转换段（凹口消失）
            if (f.ConvMean > 0 && f.LockMean > f.ConvMean * 1.2)
            {
                results.Add(new DiagnosisResult
                {
                    RuleId = "R9",
                    RuleName = "锁闭段异常",
                    Level = t.level,
                    Description = string.Format("锁闭段功率{0:F3}kW，显著高于转换段{1:F3}kW（凹口消失），疑似锁闭机构卡滞",
                        f.LockMean, f.ConvMean),
                    Value = f.LockMean,
                    Reference = f.ConvMean
                });
            }
        }

        // ── T1: 渐变劣化预警 ──

        private void EvaluateT1(string switchId, CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("T1");
            if (t == null || !t.enabled) return;
            if (string.IsNullOrEmpty(_parsedDataDir)) return;

            try
            {
                // 读取 features.json（若存在）
                string featuresPath = Path.Combine(_parsedDataDir, switchId, "features.json");
                var store = FeaturesStore.Load(featuresPath);
                if (store == null || store.Rows == null || store.Rows.Count < t.trendDays)
                    return;

                // 对 convMean 执行趋势分析
                var result = TrendAnalyzer.AnalyzeT1(store, b.RefConvMean,
                    t.trendRatio, t.trendDays, "convMean");

                if (result != null)
                {
                    // 去重：同一天只触发一次（通过 t1Cache）
                    string cacheKey = switchId + "|" + DateTime.Now.ToString("yyyy-MM-dd");
                    if (!_t1Cache.ContainsKey(cacheKey))
                    {
                        _t1Cache[cacheKey] = result;
                        results.Add(result);
                    }
                }

                // 对 durationSec 执行趋势分析
                var durResult = TrendAnalyzer.AnalyzeT1(store, b.RefDurationSec,
                    t.trendRatio, t.trendDays, "durationSec");
                if (durResult != null)
                {
                    string cacheKey = switchId + "|dur|" + DateTime.Now.ToString("yyyy-MM-dd");
                    if (!_t1Cache.ContainsKey(cacheKey))
                    {
                        _t1Cache[cacheKey] = durResult;
                        results.Add(durResult);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("T1 趋势分析失败 switchId=" + switchId + ": " + ex.Message);
            }
        }

        // ── P1: 逐点形态对比 ──

        private void EvaluateP1(string switchId, CurveFeatures f, SwitchBaseline b, List<DiagnosisResult> results)
        {
            RuleThreshold t = GetThreshold("P1");
            if (t == null || !t.enabled) return;
            if (string.IsNullOrEmpty(_rulesDir)) return;

            try
            {
                var rawValues = f.RawValues;
                if (rawValues == null || rawValues.Count == 0)
                    return;

                List<double> templateValues;
                int templateAlignIndex;
                string templateSource;

                // 优先使用标准曲线（统计校准 + 形态保真），叠加近邻漂移
                // 按方向精确匹配
                string scKey = BaselineStore.MakeKey(switchId, f.Direction);
                if (_standardCurves != null && _standardCurves.TryGetValue(scKey, out var sc)
                    && sc.Values != null && sc.Values.Count > 0)
                {
                    // D9: 尝试近邻漂移调整
                    StandardCurve adjusted = TryApplyDrift(switchId, sc);
                    if (adjusted != null)
                    {
                        templateValues = adjusted.Values;
                        templateAlignIndex = adjusted.AlignIndex;
                        templateSource = "standard+drift";
                    }
                    else
                    {
                        templateValues = sc.Values;
                        templateAlignIndex = sc.AlignIndex;
                        templateSource = "standard";
                    }
                }
                else
                {
                    // 回退到人工参考曲线（按方向匹配）
                    string refFileName = ReferenceCurveStore.MakeFileName(switchId, f.Direction);
                    string refPath = Path.Combine(_rulesDir, "reference_curves", refFileName);
                    var refCurve = ReferenceCurveStore.Load(refPath);
                    if (refCurve == null || refCurve.Values == null || refCurve.Values.Count == 0)
                        return;

                    templateValues = refCurve.Values;
                    templateAlignIndex = refCurve.AlignIndex;
                    templateSource = "reference";
                }

                var result = ProfileComparer.CompareP1WithThreshold(
                    rawValues, templateValues,
                    f.SpikeIndex, templateAlignIndex,
                    b.RefConvMean, t);

                if (result != null)
                {
                    // 标注使用的模板类型
                    result.Description += string.Format("（模板: {0}）", templateSource);
                    results.Add(result);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("P1 形态对比失败 switchId=" + switchId + ": " + ex.Message);
            }
        }

        // ── D9: Drift 辅助方法 ──

        /// <summary>
        /// 尝试对标准曲线应用近邻漂移，生成当日温度调整版 S'。
        /// 近邻不足 20 条 → 返回 null（调用方回退到原始标准曲线）。
        /// </summary>
        private StandardCurve TryApplyDrift(string switchId, StandardCurve sc)
        {
            List<CurveFeatures> neighbors;
            if (!_recentNormalCache.TryGetValue(switchId, out neighbors) || neighbors == null)
                return null;

            if (neighbors.Count < DriftEstimator.DefaultNeighborCount)
                return null;

            try
            {
                var drift = DriftEstimator.Estimate(sc, neighbors, DriftEstimator.DefaultNeighborCount);

                // 如果所有 drift 都为 1.0（刚好等于标准曲线），跳过调整
                if (Math.Abs(drift.DriftSpike - 1.0) < 0.001 &&
                    Math.Abs(drift.DriftUnlock - 1.0) < 0.001 &&
                    Math.Abs(drift.DriftConv - 1.0) < 0.001 &&
                    Math.Abs(drift.DriftLock - 1.0) < 0.001 &&
                    Math.Abs(drift.DriftTail - 1.0) < 0.001)
                    return null;

                var adjusted = DriftEstimator.ApplyDrift(sc, drift);
                return adjusted;
            }
            catch (Exception ex)
            {
                Logger.Warning("Drift 调整失败 switchId=" + switchId + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 热更新内存中的标准曲线（用于融合权重变更后即时生效，无需重建引擎）。
        /// </summary>
        /// <param name="switchId">道岔标识</param>
        /// <param name="direction">动作方向</param>
        /// <param name="curve">新的标准曲线（null 表示移除）</param>
        public void UpdateStandardCurve(string switchId, string direction, StandardCurve curve)
        {
            if (_standardCurves == null)
                _standardCurves = new Dictionary<string, StandardCurve>();
            string key = BaselineStore.MakeKey(switchId, direction);
            if (curve != null)
                _standardCurves[key] = curve;
            else
                _standardCurves.Remove(key);
        }

        /// <summary>
        /// 将当前正常事件的特征加入近邻缓存。
        /// 在 Diagnose() 返回空结果（正常）后由调用方调用。
        /// 自动维护最多 MaxRecentNormalCache 条，保持时间顺序。
        /// </summary>
        public void CacheNormalFeatures(string switchId, CurveFeatures features)
        {
            if (features == null || !features.IsValid)
                return;

            if (!_recentNormalCache.ContainsKey(switchId))
                _recentNormalCache[switchId] = new List<CurveFeatures>();

            var cache = _recentNormalCache[switchId];

            // 插入到最前面（最新的在前）
            cache.Insert(0, new CurveFeatures
            {
                IsValid = true,
                SpikePeak = features.SpikePeak,
                SpikeIndex = features.SpikeIndex,
                ActiveEnd = features.ActiveEnd,
                DurationSec = features.DurationSec,
                UnlockMean = features.UnlockMean,
                ConvMean = features.ConvMean,
                LockMean = features.LockMean,
                TailMean = features.TailMean,
                SampleCount = features.SampleCount,
                Direction = features.Direction
            });

            // 修剪到最大长度
            while (cache.Count > MaxRecentNormalCache)
                cache.RemoveAt(cache.Count - 1);
        }

        /// <summary>
        /// 获取指定道岔的近邻缓存条目数（用于诊断/测试）。
        /// </summary>
        public int GetRecentNormalCount(string switchId)
        {
            List<CurveFeatures> cache;
            if (_recentNormalCache.TryGetValue(switchId, out cache) && cache != null)
                return cache.Count;
            return 0;
        }
    }
}
