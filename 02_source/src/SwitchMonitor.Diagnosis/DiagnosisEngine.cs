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
        private string _rulesDir;
        private string _parsedDataDir;

        // 已告警"无基线"的道岔集合，避免重复日志刷屏
        private readonly HashSet<string> _warnedNoBaseline = new HashSet<string>();

        // T1 trend results cache: switchId -> 最新趋势结果（避免同一事件重复触发）
        private readonly Dictionary<string, DiagnosisResult> _t1Cache = new Dictionary<string, DiagnosisResult>();

        /// <summary>
        /// 初始化引擎：加载 rulesDir 目录下的 thresholds.json + baselines.json。
        /// 文件缺失/损坏 → 使用内置默认阈值 + 空基线，并 Logger.Warning。
        /// </summary>
        public void Initialize(string rulesDir)
        {
            _rulesDir = rulesDir;
            string thresholdsPath = Path.Combine(rulesDir, "thresholds.json");
            string baselinesPath = Path.Combine(rulesDir, "baselines.json");

            _thresholds = ThresholdStore.Load(thresholdsPath);
            _baselines = BaselineStore.Load(baselinesPath);

            _warnedNoBaseline.Clear();
            _t1Cache.Clear();

            int ruleCount = _thresholds.rules != null ? _thresholds.rules.Count : 0;
            int baselineCount = _baselines.Switches != null ? _baselines.Switches.Count : 0;
            Logger.Info(string.Format("DiagnosisEngine 初始化完成: {0} 条规则, {1} 台道岔基线",
                ruleCount, baselineCount));
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
        public List<DiagnosisResult> Diagnose(string switchId, CurveFeatures f)
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

            // === 获取该道岔基线 ===
            SwitchBaseline baseline = null;
            if (_baselines != null && _baselines.Switches != null)
            {
                _baselines.Switches.TryGetValue(switchId, out baseline);
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
                // 加载该道岔的参考曲线
                string refDir = Path.Combine(_rulesDir, "reference_curves");
                string refPath = Path.Combine(refDir, switchId + ".json");
                var refCurve = ReferenceCurveStore.Load(refPath);
                if (refCurve == null || refCurve.Values == null || refCurve.Values.Count == 0)
                    return;

                // 需要当前曲线的功率采样值来做逐点对比
                // FeatureExtractor 不保存原始值序列，我们需要从 SwitchEvent 获取
                // 这里增加一个机制：通过 CurveFeatures 携带原始值引用
                // 如果原始值不可用，跳过 P1 对比
                var rawValues = f.RawValues;
                if (rawValues == null || rawValues.Count == 0)
                    return;

                int currentSpikeIndex = f.SpikeIndex;

                var result = ProfileComparer.CompareP1WithThreshold(
                    rawValues, refCurve.Values,
                    currentSpikeIndex, refCurve.AlignIndex,
                    b.RefConvMean, t);

                if (result != null)
                {
                    results.Add(result);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("P1 形态对比失败 switchId=" + switchId + ": " + ex.Message);
            }
        }
    }
}
