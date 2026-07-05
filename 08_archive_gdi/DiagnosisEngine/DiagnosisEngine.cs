using System;
using System.Collections.Generic;
using System.Linq;
using SwitchMonitor.Common;
using SwitchMonitor.Diagnosis;

namespace DiagnosisEngine
{
    /// <summary>
    /// 规则驱动的诊断引擎实现。
    /// 从 JSON 配置文件加载诊断规则，对每次道岔动作运行诊断并输出分级结论。
    ///
    /// V1 诊断规则（5 条）：
    /// 1. 转换时间异常 — 实际转换时间 vs 参考转换时间
    /// 2. 采样数异常 — 采样数 < 历史最小值的 80%
    /// 3. 解锁段峰值异常 — 前 15% 采样点最大电流/功率 vs 参考值
    /// 4. 转换段稳态异常 — 中间 20%~80% 平均值 vs 参考值
    /// 5. 锁闭段峰值异常 — 后 15% 采样点最大电流/功率 vs 参考值
    /// </summary>
    public class DiagnosisEngine : IDiagnosisEngine
    {
        private List<RuleConfig> _rules;
        private bool _initialized;

        /// <summary>
        /// 从指定路径加载全部 .json 规则配置文件。
        /// </summary>
        /// <param name="rulesPath">规则配置目录路径</param>
        public void Initialize(string rulesPath)
        {
            if (string.IsNullOrEmpty(rulesPath))
                throw new ArgumentException("规则路径不能为空", nameof(rulesPath));

            var collection = RuleConfigCollection.LoadFromDirectory(rulesPath);
            _rules = collection.Rules ?? new List<RuleConfig>();
            _initialized = true;
        }

        /// <summary>
        /// 对一次道岔动作数据执行所有已启用的诊断规则。
        /// </summary>
        /// <param name="data">道岔动作数据包</param>
        /// <returns>诊断结论列表（每条启用规则返回一条结果）</returns>
        public List<DiagnosisResult> Diagnose(SwitchActionData data)
        {
            if (!_initialized)
                throw new InvalidOperationException("诊断引擎未初始化，请先调用 Initialize()");

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var results = new List<DiagnosisResult>();

            foreach (var rule in _rules)
            {
                if (!rule.Enabled)
                    continue;

                var result = ExecuteRule(rule, data);
                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// 根据规则的 name 分发到对应的诊断逻辑。
        /// </summary>
        private DiagnosisResult ExecuteRule(RuleConfig rule, SwitchActionData data)
        {
            try
            {
                switch (rule.Name)
                {
                    case "conversion_time":
                        return CheckConversionTime(rule, data);
                    case "sample_count":
                        return CheckSampleCount(rule, data);
                    case "unlock_peak":
                        return CheckUnlockPeak(rule, data);
                    case "conversion_steady":
                        return CheckConversionSteady(rule, data);
                    case "lock_peak":
                        return CheckLockPeak(rule, data);
                    default:
                        return new DiagnosisResult
                        {
                            RuleName = rule.DisplayName ?? rule.Name,
                            Level = DiagnosisLevel.Normal,
                            Description = string.Format("未知规则类型: {0}", rule.Name),
                        };
                }
            }
            catch (Exception ex)
            {
                // 任何规则执行异常都不应中断整体诊断流程
                return new DiagnosisResult
                {
                    RuleName = rule.DisplayName ?? rule.Name,
                    Level = DiagnosisLevel.Normal,
                    Description = string.Format("规则执行异常: {0}", ex.Message),
                };
            }
        }

        // ================================================================
        // 规则 1: 转换时间异常（阈值类）
        // ================================================================

        /// <summary>
        /// 计算实际转换时间 = 采样总数 / 采样率，与参考值比较。
        /// 偏差 ≤ 0.5s: 正常；0.5~1.5s: 预警；>1.5s: 报警。
        /// </summary>
        private DiagnosisResult CheckConversionTime(RuleConfig rule, SwitchActionData data)
        {
            float referenceSeconds = rule.GetFloatParam("referenceSeconds", 5.8f);
            float warningDeviation = rule.GetFloatParam("warningDeviation", 0.5f);
            float alarmDeviation = rule.GetFloatParam("alarmDeviation", 1.5f);

            // 计算实际转换时间
            float actualSeconds;
            int sampleCount = (data.Samples != null) ? data.Samples.Count : 0;
            int sampleRate = data.SampleRate > 0 ? data.SampleRate : 25;

            if (sampleCount == 0)
            {
                actualSeconds = 0;
            }
            else
            {
                actualSeconds = (float)sampleCount / sampleRate;
            }

            float deviation = Math.Abs(actualSeconds - referenceSeconds);

            string level;
            string description;

            if (deviation <= warningDeviation)
            {
                level = DiagnosisLevel.Normal;
                description = string.Format("转换时间 {0:F2}s，在正常范围内 (参考 {1:F2}s)",
                    actualSeconds, referenceSeconds);
            }
            else if (deviation <= alarmDeviation)
            {
                level = DiagnosisLevel.Warning;
                description = string.Format("转换时间 {0:F2}s，偏差 {1:F2}s，超出预警线 (参考 {2:F2}s ± {3:F1}s)",
                    actualSeconds, deviation, referenceSeconds, warningDeviation);
            }
            else
            {
                level = DiagnosisLevel.Alarm;
                description = string.Format("转换时间 {0:F2}s，偏差 {1:F2}s，超出报警线 (参考 {2:F2}s ± {3:F1}s)",
                    actualSeconds, deviation, referenceSeconds, alarmDeviation);
            }

            return new DiagnosisResult
            {
                RuleName = rule.DisplayName ?? "转换时间异常",
                Level = level,
                Description = description,
                AbnormalValue = actualSeconds,
                ReferenceValue = referenceSeconds,
            };
        }

        // ================================================================
        // 规则 2: 采样数异常（阈值类）
        // ================================================================

        /// <summary>
        /// 采样数小于参考最小值的 80%: 报警。
        /// 意味道岔动作不完整或数据丢失。
        /// </summary>
        private DiagnosisResult CheckSampleCount(RuleConfig rule, SwitchActionData data)
        {
            int referenceMinCount = rule.GetIntParam("referenceMinCount", 150);
            int sampleCount = (data.Samples != null) ? data.Samples.Count : 0;
            float threshold = referenceMinCount * 0.8f;

            string level;
            string description;

            if (sampleCount >= threshold)
            {
                level = DiagnosisLevel.Normal;
                description = string.Format("采样数 {0}，正常 (阈值 {1:F0})",
                    sampleCount, threshold);
            }
            else
            {
                level = DiagnosisLevel.Alarm;
                description = string.Format("采样数 {0} 低于正常范围的 80% (参考最小值 {1}，阈值 {2:F0})，疑似数据丢失或动作不完整",
                    sampleCount, referenceMinCount, threshold);
            }

            return new DiagnosisResult
            {
                RuleName = rule.DisplayName ?? "采样数异常",
                Level = level,
                Description = description,
                AbnormalValue = sampleCount,
                ReferenceValue = referenceMinCount,
            };
        }

        // ================================================================
        // 规则 3: 解锁段峰值异常（形态类）
        // ================================================================

        /// <summary>
        /// 取前 15% 采样点的最大电流值作为解锁峰值，与参考值比较。
        /// 超出 30%: 预警；超出 50%: 报警。
        /// </summary>
        private DiagnosisResult CheckUnlockPeak(RuleConfig rule, SwitchActionData data)
        {
            float referenceValue = rule.GetFloatParam("referenceValue", 3.5f);
            float warningRatio = rule.GetFloatParam("warningRatio", 1.3f);
            float alarmRatio = rule.GetFloatParam("alarmRatio", 1.5f);

            float unlockPeak = GetUnlockPeak(data);

            float ratio = (referenceValue > 0) ? unlockPeak / referenceValue : 1.0f;

            string level;
            string description;
            float? abnormalValue = unlockPeak > 0 ? unlockPeak : (float?)null;

            if (ratio <= warningRatio)
            {
                level = DiagnosisLevel.Normal;
                description = string.Format("解锁段电流峰值 {0:F2}A，正常 (参考 {1:F2}A)",
                    unlockPeak, referenceValue);
            }
            else if (ratio <= alarmRatio)
            {
                level = DiagnosisLevel.Warning;
                description = string.Format("解锁段电流峰值偏高 ({0:F2}x 参考值)，疑似密贴过紧",
                    ratio);
            }
            else
            {
                level = DiagnosisLevel.Alarm;
                description = string.Format("解锁段电流峰值严重偏高 ({0:F2}x 参考值)，疑似密贴过紧",
                    ratio);
            }

            return new DiagnosisResult
            {
                RuleName = rule.DisplayName ?? "解锁段峰值异常",
                Level = level,
                Description = description,
                AbnormalValue = abnormalValue,
                ReferenceValue = referenceValue,
            };
        }

        // ================================================================
        // 规则 4: 转换段稳态异常（形态类）
        // ================================================================

        /// <summary>
        /// 取中间 20%~80% 采样点的平均值作为转换段稳态值，与参考值比较。
        /// 超出 30%: 预警。
        /// </summary>
        private DiagnosisResult CheckConversionSteady(RuleConfig rule, SwitchActionData data)
        {
            float referenceValue = rule.GetFloatParam("referenceValue", 2.8f);
            float warningRatio = rule.GetFloatParam("warningRatio", 1.3f);

            float conversionAvg = GetConversionSteadyAverage(data);

            float ratio = (referenceValue > 0) ? conversionAvg / referenceValue : 1.0f;

            string level;
            string description;
            float? abnormalValue = conversionAvg > 0 ? conversionAvg : (float?)null;

            if (ratio <= warningRatio)
            {
                level = DiagnosisLevel.Normal;
                description = string.Format("转换段电流均值 {0:F2}A，正常 (参考 {1:F2}A)",
                    conversionAvg, referenceValue);
            }
            else
            {
                level = DiagnosisLevel.Warning;
                description = string.Format("转换段电流持续偏高 ({0:F2}x 参考值)，疑似滑床板缺油或卡阻",
                    ratio);
            }

            return new DiagnosisResult
            {
                RuleName = rule.DisplayName ?? "转换段稳态异常",
                Level = level,
                Description = description,
                AbnormalValue = abnormalValue,
                ReferenceValue = referenceValue,
            };
        }

        // ================================================================
        // 规则 5: 锁闭段峰值异常（形态类）
        // ================================================================

        /// <summary>
        /// 取后 15% 采样点的最大电流值作为锁闭峰值，与参考值比较。
        /// 超出 30%: 预警；超出 50%: 报警。
        /// </summary>
        private DiagnosisResult CheckLockPeak(RuleConfig rule, SwitchActionData data)
        {
            float referenceValue = rule.GetFloatParam("referenceValue", 3.2f);
            float warningRatio = rule.GetFloatParam("warningRatio", 1.3f);
            float alarmRatio = rule.GetFloatParam("alarmRatio", 1.5f);

            float lockPeak = GetLockPeak(data);

            float ratio = (referenceValue > 0) ? lockPeak / referenceValue : 1.0f;

            string level;
            string description;
            float? abnormalValue = lockPeak > 0 ? lockPeak : (float?)null;

            if (ratio <= warningRatio)
            {
                level = DiagnosisLevel.Normal;
                description = string.Format("锁闭段电流峰值 {0:F2}A，正常 (参考 {1:F2}A)",
                    lockPeak, referenceValue);
            }
            else if (ratio <= alarmRatio)
            {
                level = DiagnosisLevel.Warning;
                description = string.Format("锁闭段电流峰值偏高 ({0:F2}x 参考值)，疑似密贴调整过紧",
                    ratio);
            }
            else
            {
                level = DiagnosisLevel.Alarm;
                description = string.Format("锁闭段电流峰值严重偏高 ({0:F2}x 参考值)，疑似密贴调整过紧",
                    ratio);
            }

            return new DiagnosisResult
            {
                RuleName = rule.DisplayName ?? "锁闭段峰值异常",
                Level = level,
                Description = description,
                AbnormalValue = abnormalValue,
                ReferenceValue = referenceValue,
            };
        }

        // ================================================================
        // 数据提取工具方法
        // ================================================================

        /// <summary>
        /// 提取 A 相电流采样值数组，按 SampleIndex 排序。
        /// </summary>
        private static float[] ExtractCurrentValues(SwitchActionData data)
        {
            if (data.Samples == null || data.Samples.Count == 0)
                return new float[0];

            // 优先取 A 相，如果没有 A 相数据则取所有相的平均
            var phaseASamples = data.Samples
                .Where(s => s.Phase == "A")
                .OrderBy(s => s.Index)
                .Select(s => s.Current)
                .ToArray();

            if (phaseASamples.Length > 0)
                return phaseASamples;

            // 如果没找到 A 相，尝试取所有相的 RawValue 或 Current
            return data.Samples
                .OrderBy(s => s.Index)
                .Select(s => s.Current > 0 ? s.Current : s.RawValue)
                .ToArray();
        }

        /// <summary>
        /// 获取解锁段峰值：前 15% 采样点的最大电流值。
        /// </summary>
        private static float GetUnlockPeak(SwitchActionData data)
        {
            float[] values = ExtractCurrentValues(data);
            if (values.Length == 0) return 0f;

            int unlockEnd = Math.Max(1, (int)(values.Length * 0.15));
            float max = 0f;
            for (int i = 0; i < unlockEnd; i++)
            {
                if (values[i] > max) max = values[i];
            }
            return max;
        }

        /// <summary>
        /// 获取转换段稳态平均值：中间 20%~80% 采样点的平均值。
        /// </summary>
        private static float GetConversionSteadyAverage(SwitchActionData data)
        {
            float[] values = ExtractCurrentValues(data);
            if (values.Length == 0) return 0f;

            int start = (int)(values.Length * 0.2);
            int end = (int)(values.Length * 0.8);

            if (end <= start) return 0f;

            float sum = 0f;
            int count = 0;
            for (int i = start; i < end && i < values.Length; i++)
            {
                sum += values[i];
                count++;
            }

            return count > 0 ? sum / count : 0f;
        }

        /// <summary>
        /// 获取锁闭段峰值：后 15% 采样点的最大电流值。
        /// </summary>
        private static float GetLockPeak(SwitchActionData data)
        {
            float[] values = ExtractCurrentValues(data);
            if (values.Length == 0) return 0f;

            int lockStart = Math.Max(0, (int)(values.Length * 0.85));
            float max = 0f;
            for (int i = lockStart; i < values.Length; i++)
            {
                if (values[i] > max) max = values[i];
            }
            return max;
        }
    }
}
