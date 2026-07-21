using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SwitchMonitor.Tests")]

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 标准曲线构建器。
    /// 将人工参考曲线（单条形态模板）与统计基线（数千条曲线的中位数）
    /// 融合为一条兼具形态保真与统计稳健的标准曲线。
    ///
    /// 算法六步：
    ///   1. 提取参考曲线五阶段特征
    ///   2. 计算各段缩放因子 α = baseline.Ref* / ref.*，clamp + fusionWeight 混合
    ///   3. 时间轴线性重采样至基线时长
    ///   4. 重新提取重采样后的段边界
    ///   5. 逐点幅度缩放 + 段边界平滑过渡
    ///   6. 输出 StandardCurve
    /// </summary>
    public static class StandardCurveBuilder
    {
        /// <summary>
        /// 构建标准曲线。
        /// </summary>
        /// <param name="referenceCurve">人工设定的参考曲线</param>
        /// <param name="baseline">统计基线</param>
        /// <param name="fusionWeight">融合强度：0=保持原参考曲线, 1=完全对齐基线（默认）</param>
        /// <param name="clampMin">α 下限，默认 0.7</param>
        /// <param name="clampMax">α 上限，默认 1.3</param>
        /// <param name="blendHalfWidth">段边界过渡半宽（点数），默认 3</param>
        /// <returns>标准曲线；参考曲线无效或基线为空时返回 null</returns>
        public static StandardCurve Build(
            ReferenceCurve referenceCurve,
            SwitchBaseline baseline,
            double fusionWeight = 1.0,
            double clampMin = 0.7,
            double clampMax = 1.3,
            int blendHalfWidth = 3)
        {
            // ── 前置校验 ──
            if (referenceCurve == null || referenceCurve.Values == null || referenceCurve.Values.Count == 0)
                return null;
            if (baseline == null)
                return null;

            var refValues = referenceCurve.Values;
            double sampleInterval = referenceCurve.SampleInterval;
            if (sampleInterval <= 0.0)
                sampleInterval = 0.04;

            // ── Step 1: 提取参考曲线五阶段特征 ──
            var refFeat = FeatureExtractor.Extract(refValues);
            if (!refFeat.IsValid)
                return null;

            // ── Step 2: 计算各段缩放因子 ──
            double α_t_raw = baseline.RefDurationSec / Math.Max(refFeat.DurationSec, 0.01);
            double α_spike_raw = baseline.RefSpikePeak / Math.Max(refFeat.SpikePeak, 0.001);
            // baseline Ref* 为 0 表示 JSON 中缺失该字段（旧版基线），回退到 1.0
            double α_unlock_raw = (refFeat.UnlockMean > 0.001 && baseline.RefUnlockMean > 0.001)
                ? baseline.RefUnlockMean / refFeat.UnlockMean : 1.0;
            double α_conv_raw = (refFeat.ConvMean > 0.001 && baseline.RefConvMean > 0.001)
                ? baseline.RefConvMean / refFeat.ConvMean : 1.0;
            double α_lock_raw = (refFeat.LockMean > 0.001 && baseline.RefLockMean > 0.001)
                ? baseline.RefLockMean / refFeat.LockMean : 1.0;
            double α_tail_raw = (refFeat.TailMean > 0.001 && baseline.RefTailMean > 0.001)
                ? baseline.RefTailMean / refFeat.TailMean : 1.0;

            // clamp + fusionWeight 混合
            double α_t     = MixAlpha(α_t_raw,     fusionWeight, clampMin, clampMax);
            double α_spike = MixAlpha(α_spike_raw,  fusionWeight, clampMin, clampMax);
            double α_unlock = MixAlpha(α_unlock_raw, fusionWeight, clampMin, clampMax);
            double α_conv  = MixAlpha(α_conv_raw,   fusionWeight, clampMin, clampMax);
            double α_lock  = MixAlpha(α_lock_raw,   fusionWeight, clampMin, clampMax);
            double α_tail  = MixAlpha(α_tail_raw,   fusionWeight, clampMin, clampMax);

            // ── Step 3: 时间轴线性重采样 ──
            // 取基线和参考曲线中较长者，避免截断参考曲线的尾部
            int baselineLen = (int)Math.Round(baseline.RefDurationSec / sampleInterval);
            int refLen = refValues.Count;
            int targetLen = Math.Max(10, Math.Max(baselineLen, refLen));
            var resampled = ResampleLinear(refValues, targetLen);

            // ── Step 4: 重新提取重采样后的段边界 ──
            var resampledFeat = FeatureExtractor.Extract(resampled);
            if (!resampledFeat.IsValid)
                return null;

            // ── Step 5: 逐点幅度缩放 + 段边界平滑过渡 ──
            int si = resampledFeat.SpikeIndex;
            int ae = resampledFeat.ActiveEnd;
            int n = resampled.Count;

            var standardValues = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double α_i = GetPointAlpha(i, si,
                    resampledFeat.UnlockEnd, resampledFeat.LockStart, ae, n,
                    α_spike, α_unlock, α_conv, α_lock, α_tail,
                    blendHalfWidth);
                standardValues.Add(Math.Round(resampled[i] * α_i, 3));
            }

            // ── Step 6: 构建输出 ──
            return new StandardCurve
            {
                SwitchId = referenceCurve.SwitchId,
                Direction = refFeat.Direction,
                SampleInterval = sampleInterval,
                AlignIndex = resampledFeat.SpikeIndex,
                Values = standardValues,
                FusionWeight = fusionWeight,
                ReferenceSource = "reference_curves/" + ReferenceCurveStore.MakeFileName(referenceCurve.SwitchId, referenceCurve.Direction),
                BaselineComputedAt = baseline.Direction ?? "",
                AlphaTime = Math.Round(α_t, 4),
                AlphaSpike = Math.Round(α_spike, 4),
                AlphaUnlock = Math.Round(α_unlock, 4),
                AlphaConv = Math.Round(α_conv, 4),
                AlphaLock = Math.Round(α_lock, 4),
                AlphaTail = Math.Round(α_tail, 4),
                ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        /// <summary>
        /// 将中位标准曲线与人工参考曲线逐点线性混合。
        /// result[i] = medianCurve.OriginalMedianValues[i] × w + refResampled[i] × (1 − w)
        /// </summary>
        /// <param name="medianCurve">中位标准曲线（需含 OriginalMedianValues，若为 null 则回退用 Values）</param>
        /// <param name="referenceCurve">人工参考曲线</param>
        /// <param name="fusionWeight">融合权重 0~1。0=纯参考曲线，1=纯中位曲线</param>
        /// <returns>融合后的 StandardCurve（Values 已更新，OriginalMedianValues 保留）；输入无效时返回 null</returns>
        public static StandardCurve Blend(
            StandardCurve medianCurve,
            ReferenceCurve referenceCurve,
            double fusionWeight)
        {
            // ── 前置校验 ──
            if (medianCurve == null || medianCurve.Values == null || medianCurve.Values.Count < 10)
                return null;
            if (referenceCurve == null || referenceCurve.Values == null || referenceCurve.Values.Count < 10)
                return null;

            // Clamp w to [0, 1]
            double w = fusionWeight < 0.0 ? 0.0 : (fusionWeight > 1.0 ? 1.0 : fusionWeight);

            // 确定中位基准值：优先使用 OriginalMedianValues，回退到 Values（向后兼容旧文件）
            List<double> medianSource;
            if (medianCurve.OriginalMedianValues != null && medianCurve.OriginalMedianValues.Count > 0)
            {
                medianSource = medianCurve.OriginalMedianValues;
            }
            else
            {
                medianSource = new List<double>(medianCurve.Values);
                // 升级旧文件：将 Values 作为 OriginalMedianValues
                medianCurve.OriginalMedianValues = new List<double>(medianCurve.Values);
            }

            int medianLen = medianSource.Count;
            int refLen = referenceCurve.Values.Count;

            // ── 长度对齐：将参考曲线重采样到与中位曲线等长 ──
            List<double> refResampled;
            if (refLen == medianLen)
            {
                refResampled = referenceCurve.Values;
            }
            else
            {
                refResampled = ResampleLinear(referenceCurve.Values, medianLen);
            }

            // ── 逐点混合 ──
            var blendedValues = new List<double>(medianLen);
            for (int i = 0; i < medianLen; i++)
            {
                double val = medianSource[i] * w + refResampled[i] * (1.0 - w);
                blendedValues.Add(Math.Round(val, 3));
            }

            // ── 构造返回值 ──
            return new StandardCurve
            {
                SwitchId = medianCurve.SwitchId,
                Direction = medianCurve.Direction,
                SampleInterval = medianCurve.SampleInterval,
                AlignIndex = medianCurve.AlignIndex,
                Values = blendedValues,
                OriginalMedianValues = new List<double>(medianSource),
                FusionWeight = w,
                ReferenceSource = "blend: median × " + w.ToString("F2") + " + reference × " + (1.0 - w).ToString("F2"),
                BaselineComputedAt = medianCurve.BaselineComputedAt,
                AlphaTime = medianCurve.AlphaTime,
                AlphaSpike = medianCurve.AlphaSpike,
                AlphaUnlock = medianCurve.AlphaUnlock,
                AlphaConv = medianCurve.AlphaConv,
                AlphaLock = medianCurve.AlphaLock,
                AlphaTail = medianCurve.AlphaTail,
                ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        // ═══════════════════════════════════════════════════════════
        //  内部算法
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// clamp 到 [min, max]，再按 fusionWeight 混合到 1.0：
        /// α = 1.0 + (clamped - 1.0) × w
        /// </summary>
        private static double MixAlpha(double raw, double w, double min, double max)
        {
            double clamped = raw < min ? min : (raw > max ? max : raw);
            return 1.0 + (clamped - 1.0) * w;
        }

        /// <summary>
        /// 线性重采样：在连续域 [0, N-1] 中均匀采样 targetCount 个点。
        /// </summary>
        internal static List<double> ResampleLinear(List<double> src, int targetCount)
        {
            if (targetCount <= 0)
                return new List<double>();

            int N = src.Count;
            if (N == 0)
                return new List<double>();

            if (targetCount == 1)
                return new List<double> { src[0] };

            if (N == 1)
            {
                var result = new List<double>(targetCount);
                for (int i = 0; i < targetCount; i++)
                    result.Add(src[0]);
                return result;
            }

            var output = new List<double>(targetCount);
            for (int k = 0; k < targetCount; k++)
            {
                double x = k * (N - 1) / (double)(targetCount - 1);
                int left = (int)Math.Floor(x);
                int right = left + 1;
                if (right >= N)
                    right = N - 1;
                double frac = x - left;
                double val = src[left] * (1.0 - frac) + src[right] * frac;
                output.Add(val);
            }

            return output;
        }

        /// <summary>
        /// 对单个点计算所在的 α 值。
        /// 五段定义与 FeatureExtractor 完全一致，段间过渡区线性混合。
        ///
        /// 段边界（与 FeatureExtractor.Extract 对齐）：
        ///   Unlock:  [si+2,  si+14)     α_unlock
        ///   Conv:    [si+20, ae-40)     α_conv
        ///   Lock:    [ae-40, ae-22)     α_lock        (仅 ae > 50)
        ///   Tail:    [ae-22, ae-2)      α_tail        (仅 ae > 30)
        ///
        /// 过渡区（hw = blendHalfWidth）：
        ///   spike → unlock:  [si, si+2) 区域线性过渡
        ///   unlock → conv:   [unlockEnd-hw, unlockEnd+hw) 线性过渡
        ///   conv → lock:     [lockStart-hw, lockStart+hw) 线性过渡
        ///   lock → tail:     [ae-22-hw, ae-22+hw) 线性过渡
        /// </summary>
        internal static double GetPointAlpha(
            int i, int si, int unlockEnd, int lockStart, int ae, int n,
            double α_spike, double α_unlock, double α_conv,
            double α_lock, double α_tail,
            int hw)
        {
            // 使用传入的物理边界（来自 FeatureExtractor 检测结果）
            bool hasLock = lockStart > 0 && lockStart < ae;
            bool hasTail = ae > 30;

            int unlockStart  = si + 2;
            int convStart    = unlockEnd;      // 转换从解锁终点开始
            int convEnd      = hasLock ? lockStart : ae;  // 转换持续到密贴拐点
            int lockSegEnd   = hasLock ? ae - 22 : n;     // 锁闭段预估终点（接点切换 ~22 点前）
            int tailStart    = hasTail ? ae - 22 : n;
            int tailEnd      = hasTail ? ae - 2  : n;

            // 边界修正
            if (unlockStart < 0) unlockStart = 0;
            if (convStart < unlockStart) convStart = unlockStart;
            if (convEnd < convStart) convEnd = convStart;
            if (lockSegEnd < convEnd) lockSegEnd = convEnd;
            if (tailStart < lockSegEnd) tailStart = lockSegEnd;
            if (tailEnd < tailStart) tailEnd = tailStart;

            // ── 过渡区 1: spike → unlock ──
            // 过渡区间 [si, unlockStart)，线性从 α_spike → α_unlock
            if (i >= si && i < unlockStart)
            {
                if (unlockStart > si)
                    return Lerp(α_spike, α_unlock, (double)(i - si) / (unlockStart - si));
                return α_unlock;
            }

            // ── 过渡区 2: unlock → conv ──
            // 过渡区间 [unlockEnd-hw, convStart+hw) = [convStart-hw, convStart+hw)
            if (i >= convStart - hw && i < convStart + hw)
            {
                int transStart = convStart - hw;
                int transEnd = convStart + hw;
                if (transEnd > transStart)
                    return Lerp(α_unlock, α_conv, (double)(i - transStart) / (transEnd - transStart));
                return α_conv;
            }

            // ── 过渡区 3: conv → lock ──
            if (hasLock && i >= convEnd - hw && i < lockStart + hw)
            {
                int transStart = convEnd - hw;
                int transEnd = lockStart + hw;
                if (transEnd > transStart)
                {
                    double t = (double)(i - transStart) / (transEnd - transStart);
                    if (t < 0.0) t = 0.0;
                    if (t > 1.0) t = 1.0;
                    return Lerp(α_conv, α_lock, t);
                }
            }

            // ── 过渡区 4: lock → tail ──
            if (hasTail && hasLock && i >= lockSegEnd - hw && i < tailStart + hw)
            {
                int transStart = lockSegEnd - hw;
                int transEnd = tailStart + hw;
                if (transEnd > transStart)
                {
                    double t = (double)(i - transStart) / (transEnd - transStart);
                    if (t < 0.0) t = 0.0;
                    if (t > 1.0) t = 1.0;
                    return Lerp(α_lock, α_tail, t);
                }
            }
            // lock → tail 过渡但无锁闭段：conv → tail
            if (hasTail && !hasLock && i >= convEnd - hw && i < tailStart + hw)
            {
                int transStart = convEnd - hw;
                int transEnd = tailStart + hw;
                if (transEnd > transStart)
                {
                    double t = (double)(i - transStart) / (transEnd - transStart);
                    if (t < 0.0) t = 0.0;
                    if (t > 1.0) t = 1.0;
                    return Lerp(α_conv, α_tail, t);
                }
            }

            // ── 确定当前点所属段 ──
            if (i < si)
                return α_spike;

            if (i < convStart)  // [si, convStart) → unlock
                return α_unlock;

            if (i < convEnd)    // [convStart, convEnd) → conv
                return α_conv;

            if (hasLock && i >= lockStart && i < lockSegEnd)
                return α_lock;

            if (hasTail && i >= tailStart && i < tailEnd)
                return α_tail;

            // 尾部残余点（ae-2 以后）
            if (hasTail && i >= tailEnd)
                return α_tail;

            // 无对应段时回退到 conv（转换段覆盖大部分区域）
            if (hasLock && i >= convEnd && i < lockStart)
                return α_conv;

            // 最终的兜底
            return α_tail;
        }

        /// <summary>
        /// 线性插值：lerp(a, b, t)，t ∈ [0, 1]
        /// </summary>
        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }
    }
}
