using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 三相电流标准曲线构建器。
    /// 将人工参考曲线（三相形态模板）与统计基线（20 维标量锚定）
    /// 融合为一条兼具形态保真与统计稳健的三相电流标准曲线。
    ///
    /// 算法与功率 StandardCurveBuilder 完全对应，但对 A/B/C 三相独立执行：
    ///   1. 提取参考曲线各相五阶段特征
    ///   2. 计算各相各段缩放因子 α = baseline.Ref* / ref.*，clamp + fusionWeight 混合
    ///   3. 三相统一时间轴线性重采样至基线时长
    ///   4. 重新提取重采样后的各相段边界
    ///   5. 逐相逐点幅度缩放 + 段边界平滑过渡
    ///   6. 输出 CurrentStandardCurve
    /// </summary>
    public static class CurrentStandardCurveBuilder
    {
        /// <summary>
        /// 构建三相电流标准曲线。
        /// </summary>
        /// <param name="referenceCurve">人工设定的三相电流参考曲线</param>
        /// <param name="baseline">电流统计基线（20 维标量）</param>
        /// <param name="fusionWeight">融合强度：0=保持原参考曲线, 1=完全对齐基线（默认）</param>
        /// <param name="clampMin">α 下限，默认 0.7</param>
        /// <param name="clampMax">α 上限，默认 1.3</param>
        /// <param name="blendHalfWidth">段边界过渡半宽（点数），默认 3</param>
        /// <returns>三相电流标准曲线列表（3 条 PhaseCurrentStandardCurve，A/B/C 各一）；参考曲线无效或基线为空时返回 null</returns>
        public static List<PhaseCurrentStandardCurve> Build(
            CurrentReferenceCurve referenceCurve,
            CurrentBaseline baseline,
            double fusionWeight = 1.0,
            double clampMin = 0.7,
            double clampMax = 1.3,
            int blendHalfWidth = 3)
        {
            // ── 前置校验 ──
            if (referenceCurve == null)
                return null;
            if (baseline == null)
                return null;

            double sampleInterval = referenceCurve.SampleInterval;
            if (sampleInterval <= 0.0)
                sampleInterval = 0.04;

            // ── Step 1: 各相独立提取参考曲线特征 ──
            var refFeatA = BuildPhaseFeatures(referenceCurve.ValuesA);
            var refFeatB = BuildPhaseFeatures(referenceCurve.ValuesB);
            var refFeatC = BuildPhaseFeatures(referenceCurve.ValuesC);

            if (refFeatA == null && refFeatB == null && refFeatC == null)
                return null;

            // ── Step 2: 计算各相各段缩放因子 ──
            // 时长 α 三相共用（取自基线 RefDurationSec 与参考曲线中最长时长之比）
            double refDuration = 0.0;
            if (refFeatA != null && refFeatA.DurationSec > refDuration) refDuration = refFeatA.DurationSec;
            if (refFeatB != null && refFeatB.DurationSec > refDuration) refDuration = refFeatB.DurationSec;
            if (refFeatC != null && refFeatC.DurationSec > refDuration) refDuration = refFeatC.DurationSec;
            if (refDuration < 0.01) refDuration = 0.01;

            double α_t_raw = baseline.RefDurationSec / refDuration;
            double α_t = MixAlpha(α_t_raw, fusionWeight, clampMin, clampMax);

            // A 相 α
            double α_spikeA = 1.0, α_unlockA = 1.0, α_convA = 1.0, α_lockA = 1.0, α_tailA = 1.0;
            if (refFeatA != null)
                ComputePhaseAlphas(refFeatA, baseline, 'A', fusionWeight, clampMin, clampMax,
                    out α_spikeA, out α_unlockA, out α_convA, out α_lockA, out α_tailA);

            // B 相 α
            double α_spikeB = 1.0, α_unlockB = 1.0, α_convB = 1.0, α_lockB = 1.0, α_tailB = 1.0;
            if (refFeatB != null)
                ComputePhaseAlphas(refFeatB, baseline, 'B', fusionWeight, clampMin, clampMax,
                    out α_spikeB, out α_unlockB, out α_convB, out α_lockB, out α_tailB);

            // C 相 α
            double α_spikeC = 1.0, α_unlockC = 1.0, α_convC = 1.0, α_lockC = 1.0, α_tailC = 1.0;
            if (refFeatC != null)
                ComputePhaseAlphas(refFeatC, baseline, 'C', fusionWeight, clampMin, clampMax,
                    out α_spikeC, out α_unlockC, out α_convC, out α_lockC, out α_tailC);

            // ── Step 3: 时间轴线性重采样 ──
            // 目标长度：基线时长对应的点数与各相参考曲线长度的最大值（三相统一）
            int baselineLen = (int)Math.Round(baseline.RefDurationSec / sampleInterval);
            int maxRefLen = 10;
            if (referenceCurve.ValuesA != null && referenceCurve.ValuesA.Count > maxRefLen)
                maxRefLen = referenceCurve.ValuesA.Count;
            if (referenceCurve.ValuesB != null && referenceCurve.ValuesB.Count > maxRefLen)
                maxRefLen = referenceCurve.ValuesB.Count;
            if (referenceCurve.ValuesC != null && referenceCurve.ValuesC.Count > maxRefLen)
                maxRefLen = referenceCurve.ValuesC.Count;

            int targetLen = Math.Max(10, Math.Max(baselineLen, maxRefLen));

            var resampledA = referenceCurve.ValuesA != null && referenceCurve.ValuesA.Count > 0
                ? StandardCurveBuilder.ResampleLinear(referenceCurve.ValuesA, targetLen)
                : new List<double>();
            var resampledB = referenceCurve.ValuesB != null && referenceCurve.ValuesB.Count > 0
                ? StandardCurveBuilder.ResampleLinear(referenceCurve.ValuesB, targetLen)
                : new List<double>();
            var resampledC = referenceCurve.ValuesC != null && referenceCurve.ValuesC.Count > 0
                ? StandardCurveBuilder.ResampleLinear(referenceCurve.ValuesC, targetLen)
                : new List<double>();

            // ── Step 4: 重新提取重采样后的各相段边界 ──
            var resampledFeatA = BuildPhaseFeatures(resampledA);
            var resampledFeatB = BuildPhaseFeatures(resampledB);
            var resampledFeatC = BuildPhaseFeatures(resampledC);

            // ── Step 5: 各相独立逐点幅度缩放 + 段边界平滑过渡 ──
            var standardValuesA = ApplyPhaseScaling(resampledA, resampledFeatA,
                α_spikeA, α_unlockA, α_convA, α_lockA, α_tailA, blendHalfWidth);
            var standardValuesB = ApplyPhaseScaling(resampledB, resampledFeatB,
                α_spikeB, α_unlockB, α_convB, α_lockB, α_tailB, blendHalfWidth);
            var standardValuesC = ApplyPhaseScaling(resampledC, resampledFeatC,
                α_spikeC, α_unlockC, α_convC, α_lockC, α_tailC, blendHalfWidth);

            // ── Step 6: 构建输出 ── 每相独立 PhaseCurrentStandardCurve
            var computedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var results = new List<PhaseCurrentStandardCurve>();

            // A 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = referenceCurve.SwitchId,
                Direction = referenceCurve.Direction,
                Phase = "A",
                SampleInterval = sampleInterval,
                AlignIndex = resampledFeatA != null ? resampledFeatA.SpikeIndex : 0,
                Values = standardValuesA,
                OriginalMedianValues = new List<double>(standardValuesA),
                FusionWeight = fusionWeight,
                ReferenceSource = "current_reference_curves/" + CurrentReferenceCurveStore.MakeFileName(referenceCurve.SwitchId, referenceCurve.Direction) + "_A.json",
                BaselineComputedAt = baseline.Direction ?? "",
                AlphaTime = Math.Round(α_t, 4),
                AlphaSpike = Math.Round(α_spikeA, 4),
                AlphaUnlock = Math.Round(α_unlockA, 4),
                AlphaConv = Math.Round(α_convA, 4),
                AlphaLock = Math.Round(α_lockA, 4),
                AlphaTail = Math.Round(α_tailA, 4),
                ComputedAt = computedAt
            });
            // B 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = referenceCurve.SwitchId,
                Direction = referenceCurve.Direction,
                Phase = "B",
                SampleInterval = sampleInterval,
                AlignIndex = resampledFeatB != null ? resampledFeatB.SpikeIndex : 0,
                Values = standardValuesB,
                OriginalMedianValues = new List<double>(standardValuesB),
                FusionWeight = fusionWeight,
                ReferenceSource = "current_reference_curves/" + CurrentReferenceCurveStore.MakeFileName(referenceCurve.SwitchId, referenceCurve.Direction) + "_B.json",
                BaselineComputedAt = baseline.Direction ?? "",
                AlphaTime = Math.Round(α_t, 4),
                AlphaSpike = Math.Round(α_spikeB, 4),
                AlphaUnlock = Math.Round(α_unlockB, 4),
                AlphaConv = Math.Round(α_convB, 4),
                AlphaLock = Math.Round(α_lockB, 4),
                AlphaTail = Math.Round(α_tailB, 4),
                ComputedAt = computedAt
            });
            // C 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = referenceCurve.SwitchId,
                Direction = referenceCurve.Direction,
                Phase = "C",
                SampleInterval = sampleInterval,
                AlignIndex = resampledFeatC != null ? resampledFeatC.SpikeIndex : 0,
                Values = standardValuesC,
                OriginalMedianValues = new List<double>(standardValuesC),
                FusionWeight = fusionWeight,
                ReferenceSource = "current_reference_curves/" + CurrentReferenceCurveStore.MakeFileName(referenceCurve.SwitchId, referenceCurve.Direction) + "_C.json",
                BaselineComputedAt = baseline.Direction ?? "",
                AlphaTime = Math.Round(α_t, 4),
                AlphaSpike = Math.Round(α_spikeC, 4),
                AlphaUnlock = Math.Round(α_unlockC, 4),
                AlphaConv = Math.Round(α_convC, 4),
                AlphaLock = Math.Round(α_lockC, 4),
                AlphaTail = Math.Round(α_tailC, 4),
                ComputedAt = computedAt
            });

            return results;
        }

        /// <summary>
        /// 将中位电流标准曲线与人工电流参考曲线逐相逐点线性混合。
        /// result[i] = medianCurve.ValuesX[i] × w + refResampledX[i] × (1 − w)
        /// </summary>
        /// <param name="medianCurve">中位电流标准曲线（需含 OriginalMedianValues，若为 null 则回退用 Values）</param>
        /// <param name="referenceCurve">人工电流参考曲线</param>
        /// <param name="fusionWeight">融合权重 0~1。0=纯参考曲线，1=纯中位曲线</param>
        /// <returns>融合后的分相电流标准曲线列表（A/B/C）；输入无效时返回 null</returns>
        public static List<PhaseCurrentStandardCurve> Blend(
            PhaseCurrentStandardCurve medianCurveA,
            PhaseCurrentStandardCurve medianCurveB,
            PhaseCurrentStandardCurve medianCurveC,
            CurrentReferenceCurve referenceCurve,
            double fusionWeight)
        {
            // ── 前置校验 ──
            if (medianCurveA == null || medianCurveB == null || medianCurveC == null)
                return null;
            if (referenceCurve == null)
                return null;

            double w = fusionWeight < 0.0 ? 0.0 : (fusionWeight > 1.0 ? 1.0 : fusionWeight);

            // 确定中位基准值：优先使用 OriginalMedianValues，回退到 Values
            List<double> medianSourceA = GetMedianSource(medianCurveA.OriginalMedianValues, medianCurveA.Values);
            List<double> medianSourceB = GetMedianSource(medianCurveB.OriginalMedianValues, medianCurveB.Values);
            List<double> medianSourceC = GetMedianSource(medianCurveC.OriginalMedianValues, medianCurveC.Values);

            // 各相独立重采样 + 混合
            var blendedA = BlendPhase(medianSourceA, referenceCurve.ValuesA, w);
            var blendedB = BlendPhase(medianSourceB, referenceCurve.ValuesB, w);
            var blendedC = BlendPhase(medianSourceC, referenceCurve.ValuesC, w);

            var computedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var results = new List<PhaseCurrentStandardCurve>();
            // A 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = medianCurveA.SwitchId, Direction = medianCurveA.Direction, Phase = "A",
                SampleInterval = medianCurveA.SampleInterval, AlignIndex = medianCurveA.AlignIndex,
                Values = blendedA, OriginalMedianValues = new List<double>(medianSourceA),
                FusionWeight = w, ReferenceSource = medianCurveA.ReferenceSource,
                BaselineComputedAt = medianCurveA.BaselineComputedAt,
                AlphaTime = medianCurveA.AlphaTime,
                AlphaSpike = medianCurveA.AlphaSpike, AlphaUnlock = medianCurveA.AlphaUnlock,
                AlphaConv = medianCurveA.AlphaConv, AlphaLock = medianCurveA.AlphaLock, AlphaTail = medianCurveA.AlphaTail,
                ComputedAt = computedAt
            });
            // B 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = medianCurveB.SwitchId, Direction = medianCurveB.Direction, Phase = "B",
                SampleInterval = medianCurveB.SampleInterval, AlignIndex = medianCurveB.AlignIndex,
                Values = blendedB, OriginalMedianValues = new List<double>(medianSourceB),
                FusionWeight = w, ReferenceSource = medianCurveB.ReferenceSource,
                BaselineComputedAt = medianCurveB.BaselineComputedAt,
                AlphaTime = medianCurveB.AlphaTime,
                AlphaSpike = medianCurveB.AlphaSpike, AlphaUnlock = medianCurveB.AlphaUnlock,
                AlphaConv = medianCurveB.AlphaConv, AlphaLock = medianCurveB.AlphaLock, AlphaTail = medianCurveB.AlphaTail,
                ComputedAt = computedAt
            });
            // C 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = medianCurveC.SwitchId, Direction = medianCurveC.Direction, Phase = "C",
                SampleInterval = medianCurveC.SampleInterval, AlignIndex = medianCurveC.AlignIndex,
                Values = blendedC, OriginalMedianValues = new List<double>(medianSourceC),
                FusionWeight = w, ReferenceSource = medianCurveC.ReferenceSource,
                BaselineComputedAt = medianCurveC.BaselineComputedAt,
                AlphaTime = medianCurveC.AlphaTime,
                AlphaSpike = medianCurveC.AlphaSpike, AlphaUnlock = medianCurveC.AlphaUnlock,
                AlphaConv = medianCurveC.AlphaConv, AlphaLock = medianCurveC.AlphaLock, AlphaTail = medianCurveC.AlphaTail,
                ComputedAt = computedAt
            });
            return results;
        }

        // ═══════════════════════════════════════════════════════════
        //  内部算法
        // ═══════════════════════════════════════════════════════════

        /// <summary>内部阶段特征结构（与 CurrentFeatureExtractor.PhaseFeaturesInternal 对应）</summary>
        private class PhaseFeatures
        {
            public bool IsValid;
            public int ActiveEnd;
            public double SpikePeak;
            public int SpikeIndex;
            public int UnlockEnd;
            public int LockStart;
            public double UnlockMean;
            public double ConvMean;
            public double LockMean;
            public double TailMean;
            public double DurationSec;
            public int SampleCount;
        }

        /// <summary>
        /// 从单相值列表提取阶段特征。
        /// 委托给 CurrentFeatureExtractor.ExtractPhase，转换为内部 PhaseFeatures。
        /// </summary>
        private static PhaseFeatures BuildPhaseFeatures(List<double> values)
        {
            if (values == null || values.Count == 0)
                return null;

            var cf = CurrentFeatureExtractor.ExtractPhase(values);
            if (!cf.IsValid)
                return null;

            return new PhaseFeatures
            {
                IsValid = cf.IsValid,
                ActiveEnd = cf.ActiveEnd,
                SpikePeak = cf.SpikePeakA,
                SpikeIndex = cf.SpikeIndexA,
                UnlockEnd = cf.UnlockEndA,
                LockStart = cf.LockStartA,
                UnlockMean = cf.UnlockMeanA,
                ConvMean = cf.ConvMeanA,
                LockMean = cf.LockMeanA,
                TailMean = cf.TailMeanA,
                DurationSec = cf.DurationSec,
                SampleCount = cf.SampleCount
            };
        }

        /// <summary>计算单相各段缩放因子</summary>
        private static void ComputePhaseAlphas(
            PhaseFeatures refFeat, CurrentBaseline baseline, char phase,
            double fusionWeight, double clampMin, double clampMax,
            out double α_spike, out double α_unlock, out double α_conv,
            out double α_lock, out double α_tail)
        {
            double refSpikePeak, refUnlockMean, refConvMean, refLockMean, refTailMean;
            double blSpikePeak, blUnlockMean, blConvMean, blLockMean, blTailMean;

            GetPhaseFields(refFeat, baseline, phase,
                out refSpikePeak, out refUnlockMean, out refConvMean, out refLockMean, out refTailMean,
                out blSpikePeak, out blUnlockMean, out blConvMean, out blLockMean, out blTailMean);

            double α_spike_raw = blSpikePeak / Math.Max(refSpikePeak, 0.001);
            double α_unlock_raw = (refUnlockMean > 0.001 && blUnlockMean > 0.001)
                ? blUnlockMean / refUnlockMean : 1.0;
            double α_conv_raw = (refConvMean > 0.001 && blConvMean > 0.001)
                ? blConvMean / refConvMean : 1.0;
            double α_lock_raw = (refLockMean > 0.001 && blLockMean > 0.001)
                ? blLockMean / refLockMean : 1.0;
            double α_tail_raw = (refTailMean > 0.001 && blTailMean > 0.001)
                ? blTailMean / refTailMean : 1.0;

            α_spike = MixAlpha(α_spike_raw, fusionWeight, clampMin, clampMax);
            α_unlock = MixAlpha(α_unlock_raw, fusionWeight, clampMin, clampMax);
            α_conv = MixAlpha(α_conv_raw, fusionWeight, clampMin, clampMax);
            α_lock = MixAlpha(α_lock_raw, fusionWeight, clampMin, clampMax);
            α_tail = MixAlpha(α_tail_raw, fusionWeight, clampMin, clampMax);
        }

        /// <summary>获取指定相的特征字段与基线字段</summary>
        private static void GetPhaseFields(
            PhaseFeatures refFeat, CurrentBaseline baseline, char phase,
            out double refSpikePeak, out double refUnlockMean, out double refConvMean,
            out double refLockMean, out double refTailMean,
            out double blSpikePeak, out double blUnlockMean, out double blConvMean,
            out double blLockMean, out double blTailMean)
        {
            refSpikePeak = refFeat.SpikePeak;
            refUnlockMean = refFeat.UnlockMean;
            refConvMean = refFeat.ConvMean;
            refLockMean = refFeat.LockMean;
            refTailMean = refFeat.TailMean;

            switch (phase)
            {
                case 'A':
                    blSpikePeak = baseline.RefSpikePeakA;
                    blUnlockMean = baseline.RefUnlockMeanA;
                    blConvMean = baseline.RefConvMeanA;
                    blLockMean = baseline.RefLockMeanA;
                    blTailMean = baseline.RefTailMeanA;
                    break;
                case 'B':
                    blSpikePeak = baseline.RefSpikePeakB;
                    blUnlockMean = baseline.RefUnlockMeanB;
                    blConvMean = baseline.RefConvMeanB;
                    blLockMean = baseline.RefLockMeanB;
                    blTailMean = baseline.RefTailMeanB;
                    break;
                case 'C':
                default:
                    blSpikePeak = baseline.RefSpikePeakC;
                    blUnlockMean = baseline.RefUnlockMeanC;
                    blConvMean = baseline.RefConvMeanC;
                    blLockMean = baseline.RefLockMeanC;
                    blTailMean = baseline.RefTailMeanC;
                    break;
            }
        }

        /// <summary>
        /// 对单相重采样后的值应用逐点幅度缩放。
        /// 复用 StandardCurveBuilder.GetPointAlpha 实现段间平滑过渡。
        /// </summary>
        private static List<double> ApplyPhaseScaling(
            List<double> resampled, PhaseFeatures feat,
            double α_spike, double α_unlock, double α_conv, double α_lock, double α_tail,
            int blendHalfWidth)
        {
            if (resampled == null || resampled.Count == 0)
                return new List<double>();

            if (feat == null || !feat.IsValid)
            {
                // 无有效特征 → 不做缩放，原样返回
                return new List<double>(resampled);
            }

            int si = feat.SpikeIndex;
            int ae = feat.ActiveEnd;
            int n = resampled.Count;

            var result = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double α_i = StandardCurveBuilder.GetPointAlpha(
                    i, si, feat.UnlockEnd, feat.LockStart, ae, n,
                    α_spike, α_unlock, α_conv, α_lock, α_tail,
                    blendHalfWidth);
                result.Add(Math.Round(resampled[i] * α_i, 3));
            }

            return result;
        }

        /// <summary>获取中位源——优先 OriginalMedianValues，回退到 Values（向后兼容）</summary>
        private static List<double> GetMedianSource(List<double> originalMedian, List<double> values)
        {
            if (originalMedian != null && originalMedian.Count > 0)
                return originalMedian;

            // 回退到 Values，同时升级：将 Values 作为 OriginalMedianValues
            if (values != null && values.Count > 0)
                return new List<double>(values);

            return new List<double>();
        }

        /// <summary>对单相执行重采样 + 混合</summary>
        private static List<double> BlendPhase(List<double> medianSource, List<double> refValues, double w)
        {
            if (medianSource.Count == 0 && (refValues == null || refValues.Count == 0))
                return new List<double>();

            if (medianSource.Count == 0)
                return new List<double>(refValues);

            int medianLen = medianSource.Count;
            int refLen = refValues != null ? refValues.Count : 0;

            List<double> refResampled;
            if (refLen == medianLen)
            {
                refResampled = refValues;
            }
            else if (refLen > 0)
            {
                refResampled = StandardCurveBuilder.ResampleLinear(refValues, medianLen);
            }
            else
            {
                refResampled = new List<double>();
            }

            var blended = new List<double>(medianLen);
            for (int i = 0; i < medianLen; i++)
            {
                double refVal = i < refResampled.Count ? refResampled[i] : 0.0;
                double val = medianSource[i] * w + refVal * (1.0 - w);
                blended.Add(Math.Round(val, 3));
            }

            return blended;
        }

        /// <summary>
        /// clamp 到 [min, max]，再按 fusionWeight 混合到 1.0：
        /// α = 1.0 + (clamped - 1.0) × w
        /// 与 StandardCurveBuilder.MixAlpha 等价。
        /// </summary>
        private static double MixAlpha(double raw, double w, double min, double max)
        {
            double clamped = raw < min ? min : (raw > max ? max : raw);
            return 1.0 + (clamped - 1.0) * w;
        }
    }
}
