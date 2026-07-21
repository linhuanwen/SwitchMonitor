using System;
using System.Collections.Generic;
using System.Linq;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 各段漂移估计系数。>1 表示当前水平高于标准曲线，<1 表示低于。
    /// </summary>
    public class SegmentDrift
    {
        public double DriftSpike;
        public double DriftUnlock;
        public double DriftConv;
        public double DriftLock;
        public double DriftTail;
        public int NeighborCount;
        public string ComputedAt;
    }

    /// <summary>
    /// D9 近邻漂移估计器（Layer 2：日内温度适应）。
    ///
    /// 从最近 N 条正常事件的特征中估计各段当前漂移系数，
    /// 并将漂移应用到标准曲线生成当日温度调整版 S'，供 P1 逐点对比使用。
    ///
    /// 关键设计：
    ///   - drift_seg = median(近邻.segMean) / standardCurve 对应段均值
    ///   - drift clamp 到 [0.85, 1.15]（比 baseline 的 [0.7, 1.3] 更紧）
    ///   - ApplyDrift 复用 StandardCurveBuilder.GetPointAlpha 逐点 α 分配逻辑
    /// </summary>
    public static class DriftEstimator
    {
        /// <summary>默认近邻数量（覆盖约 1-2 天）</summary>
        public const int DefaultNeighborCount = 20;

        /// <summary>drift clamp 下限</summary>
        public const double ClampMin = 0.85;

        /// <summary>drift clamp 上限</summary>
        public const double ClampMax = 1.15;

        /// <summary>
        /// 从最近 N 条正常事件的特征中估计各段当前漂移系数。
        /// drift_seg = median(近邻.segMean) / standardCurve 对应段均值
        /// 近邻不足 N 条 → 返回全 1.0（无调整）。
        /// </summary>
        /// <param name="standardCurve">标准曲线（用于提取各段均值基准）</param>
        /// <param name="recentNormalFeatures">最近 N 条正常事件的特征（按时间倒序，最新的在前）</param>
        /// <param name="neighborCount">近邻数量，默认 20</param>
        public static SegmentDrift Estimate(
            StandardCurve standardCurve,
            List<CurveFeatures> recentNormalFeatures,
            int neighborCount = DefaultNeighborCount)
        {
            var drift = new SegmentDrift
            {
                DriftSpike = 1.0,
                DriftUnlock = 1.0,
                DriftConv = 1.0,
                DriftLock = 1.0,
                DriftTail = 1.0,
                NeighborCount = 0,
                ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // 前置校验
            if (standardCurve == null || standardCurve.Values == null || standardCurve.Values.Count == 0)
                return drift;

            if (recentNormalFeatures == null || recentNormalFeatures.Count < neighborCount)
            {
                drift.NeighborCount = recentNormalFeatures != null ? recentNormalFeatures.Count : 0;
                return drift;
            }

            // 提取标准曲线的各段均值基准
            var scFeat = FeatureExtractor.Extract(standardCurve.Values);
            if (!scFeat.IsValid)
                return drift;

            // 取最近 N 条
            var recent = recentNormalFeatures.Take(neighborCount).ToList();

            // 对各段计算 median / scSegMean
            drift.NeighborCount = recentNormalFeatures.Count;
            drift.DriftSpike  = ClampDrift(Median(recent, f => f.SpikePeak)  / Math.Max(scFeat.SpikePeak, 0.001));
            drift.DriftUnlock = ClampDrift(Median(recent, f => f.UnlockMean) / Math.Max(scFeat.UnlockMean, 0.001));
            drift.DriftConv   = ClampDrift(Median(recent, f => f.ConvMean)   / Math.Max(scFeat.ConvMean, 0.001));

            // Lock/Tail: SC 段均值为 0 时 drift=1.0（不调整）
            drift.DriftLock = scFeat.LockMean > 0.001
                ? ClampDrift(Median(recent, f => f.LockMean) / scFeat.LockMean)
                : 1.0;
            drift.DriftTail = scFeat.TailMean > 0.001
                ? ClampDrift(Median(recent, f => f.TailMean) / scFeat.TailMean)
                : 1.0;

            return drift;
        }

        /// <summary>
        /// 将各段 drift 应用到标准曲线，生成当日温度调整版 S'。
        /// 内部复用 StandardCurveBuilder.GetPointAlpha 的逐点 α 分配逻辑。
        /// </summary>
        /// <param name="baseCurve">原始标准曲线</param>
        /// <param name="drift">漂移系数</param>
        /// <returns>调整后的标准曲线（Values 已按 drift 逐点缩放），输入无效时返回 null</returns>
        public static StandardCurve ApplyDrift(
            StandardCurve baseCurve,
            SegmentDrift drift)
        {
            if (baseCurve == null || baseCurve.Values == null || baseCurve.Values.Count == 0)
                return null;
            if (drift == null)
                return null;

            var values = baseCurve.Values;

            // 提取标准曲线的五阶段边界（用于逐点 α 分配）
            var feat = FeatureExtractor.Extract(values);
            if (!feat.IsValid)
                return CloneCurve(baseCurve, values); // 无法提取特征 → 返回原样

            int si = feat.SpikeIndex;
            int ae = feat.ActiveEnd;
            int n = values.Count;
            int hw = 3; // 与 StandardCurveBuilder 一致

            var adjustedValues = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double driftAlpha = StandardCurveBuilder.GetPointAlpha(
                    i, si, feat.UnlockEnd, feat.LockStart, ae, n,
                    drift.DriftSpike, drift.DriftUnlock, drift.DriftConv,
                    drift.DriftLock, drift.DriftTail,
                    hw);
                adjustedValues.Add(Math.Round(values[i] * driftAlpha, 3));
            }

            return new StandardCurve
            {
                SwitchId = baseCurve.SwitchId,
                Direction = baseCurve.Direction,
                SampleInterval = baseCurve.SampleInterval,
                AlignIndex = baseCurve.AlignIndex,
                Values = adjustedValues,
                OriginalMedianValues = baseCurve.OriginalMedianValues != null
                    ? new List<double>(baseCurve.OriginalMedianValues) : null,
                FusionWeight = baseCurve.FusionWeight,
                ReferenceSource = baseCurve.ReferenceSource + " (drift-adjusted)",
                BaselineComputedAt = baseCurve.BaselineComputedAt,
                AlphaTime = baseCurve.AlphaTime,
                AlphaSpike = baseCurve.AlphaSpike,
                AlphaUnlock = baseCurve.AlphaUnlock,
                AlphaConv = baseCurve.AlphaConv,
                AlphaLock = baseCurve.AlphaLock,
                AlphaTail = baseCurve.AlphaTail,
                ComputedAt = drift.ComputedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        // ═══════════════════════════════════════════════════════════════
        //  内部辅助
        // ═══════════════════════════════════════════════════════════════

        /// <summary>clamp drift 到 [0.85, 1.15]</summary>
        private static double ClampDrift(double raw)
        {
            if (raw < ClampMin) return ClampMin;
            if (raw > ClampMax) return ClampMax;
            return raw;
        }

        /// <summary>计算一组特征中某个属性的中位数</summary>
        private static double Median(List<CurveFeatures> features, Func<CurveFeatures, double> selector)
        {
            if (features == null || features.Count == 0)
                return 0.0;

            var values = new List<double>(features.Count);
            foreach (var f in features)
                values.Add(selector(f));
            values.Sort();

            int mid = values.Count / 2;
            if (values.Count % 2 == 1)
                return values[mid];
            else
                return (values[mid - 1] + values[mid]) / 2.0;
        }

        /// <summary>克隆标准曲线（Values 不变）</summary>
        private static StandardCurve CloneCurve(StandardCurve original, List<double> values)
        {
            return new StandardCurve
            {
                SwitchId = original.SwitchId,
                Direction = original.Direction,
                SampleInterval = original.SampleInterval,
                AlignIndex = original.AlignIndex,
                Values = new List<double>(values),
                OriginalMedianValues = original.OriginalMedianValues != null
                    ? new List<double>(original.OriginalMedianValues) : null,
                FusionWeight = original.FusionWeight,
                ReferenceSource = original.ReferenceSource,
                BaselineComputedAt = original.BaselineComputedAt,
                AlphaTime = original.AlphaTime,
                AlphaSpike = original.AlphaSpike,
                AlphaUnlock = original.AlphaUnlock,
                AlphaConv = original.AlphaConv,
                AlphaLock = original.AlphaLock,
                AlphaTail = original.AlphaTail,
                ComputedAt = original.ComputedAt
            };
        }
    }
}
