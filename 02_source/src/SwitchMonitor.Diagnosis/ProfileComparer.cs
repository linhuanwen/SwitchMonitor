using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// P1 逐点形态对比器。
    /// 将当前曲线与参考曲线按 spikeIndex 对齐后，在重叠区间计算 maxAbsDev 和 areaDiffRatio。
    /// </summary>
    public static class ProfileComparer
    {
        /// <summary>
        /// 对比当前曲线与参考曲线（使用默认 P1 阈值: areaDiffRatio > 0.25 或 maxAbsDev > refConvMean × 1.0）。
        /// </summary>
        /// <param name="currentCurve">当前功率曲线值序列</param>
        /// <param name="referenceCurve">参考曲线值序列</param>
        /// <param name="currentSpikeIndex">当前曲线 spikeIndex</param>
        /// <param name="refAlignIndex">参考曲线 alignIndex</param>
        /// <param name="refConvMean">参考转换段均值</param>
        /// <param name="sampleInterval">采样间隔（未使用，保留兼容）</param>
        /// <returns>触发时返回 DiagnosisResult，否则 null</returns>
        public static DiagnosisResult CompareP1(
            List<double> currentCurve,
            List<double> referenceCurve,
            int currentSpikeIndex,
            int refAlignIndex,
            double refConvMean,
            double sampleInterval = 0.04)
        {
            var threshold = new RuleThreshold
            {
                enabled = true,
                level = "预警",
                areaDiffRatioThreshold = 0.25,
                maxAbsDevRatio = 1.0
            };

            return CompareP1WithThreshold(currentCurve, referenceCurve,
                currentSpikeIndex, refAlignIndex, refConvMean, threshold);
        }

        /// <summary>
        /// 对比当前曲线与参考曲线（使用自定义 P1 阈值）。
        /// </summary>
        /// <param name="currentCurve">当前功率曲线值序列</param>
        /// <param name="referenceCurve">参考曲线值序列</param>
        /// <param name="currentSpikeIndex">当前曲线 spikeIndex</param>
        /// <param name="refAlignIndex">参考曲线 alignIndex</param>
        /// <param name="refConvMean">参考转换段均值 (kW)</param>
        /// <param name="threshold">P1 规则阈值</param>
        /// <returns>触发时返回 DiagnosisResult，否则 null</returns>
        public static DiagnosisResult CompareP1WithThreshold(
            List<double> currentCurve,
            List<double> referenceCurve,
            int currentSpikeIndex,
            int refAlignIndex,
            double refConvMean,
            RuleThreshold threshold)
        {
            if (!threshold.enabled)
                return null;

            if (currentCurve == null || currentCurve.Count == 0)
                return null;
            if (referenceCurve == null || referenceCurve.Count == 0)
                return null;

            // 1. 对齐：计算偏移量
            int offset = refAlignIndex - currentSpikeIndex;

            // 2. 计算重叠区间（排除尖峰前区间，即从 refAlignIndex 开始）
            int overlapStart = Math.Max(refAlignIndex, 0); // 尖峰后
            int curStart = overlapStart - offset;
            int refStart = overlapStart;

            int overlapEnd = Math.Min(
                currentCurve.Count - offset,
                referenceCurve.Count);
            // 实际结束应是当前曲线对应到参考曲线的映射
            int curMappedEnd = currentCurve.Count + offset;
            overlapEnd = Math.Min(curMappedEnd, referenceCurve.Count);
            overlapEnd = Math.Max(overlapEnd, overlapStart + 1); // 至少 1 点

            // 3. 逐点计算 |差|
            double sumAbsDiff = 0.0;
            double maxAbsDev = 0.0;
            double sumRefValue = 0.0;
            int pointCount = 0;

            for (int refIdx = overlapStart; refIdx < overlapEnd; refIdx++)
            {
                int curIdx = refIdx - offset;
                if (curIdx < 0 || curIdx >= currentCurve.Count)
                    continue;

                double refVal = referenceCurve[refIdx];
                double curVal = currentCurve[curIdx];

                double absDiff = Math.Abs(curVal - refVal);
                sumAbsDiff += absDiff;
                if (absDiff > maxAbsDev)
                    maxAbsDev = absDiff;
                sumRefValue += Math.Abs(refVal);
                pointCount++;
            }

            if (pointCount == 0)
                return null;

            // 4. 计算指标
            double areaDiffRatio = sumRefValue > 0 ? sumAbsDiff / sumRefValue : 0.0;
            double maxAbsDevRatio = refConvMean > 0 ? maxAbsDev / refConvMean : 0.0;

            maxAbsDev = Math.Round(maxAbsDev, 3);
            areaDiffRatio = Math.Round(areaDiffRatio, 3);

            // 5. 判定
            bool triggered = areaDiffRatio > threshold.areaDiffRatioThreshold ||
                             maxAbsDevRatio > threshold.maxAbsDevRatio;

            if (!triggered)
                return null;

            string reason = "";
            if (areaDiffRatio > threshold.areaDiffRatioThreshold)
                reason += string.Format("面积偏差比{0:F3}>{1:F2}", areaDiffRatio, threshold.areaDiffRatioThreshold);
            if (maxAbsDevRatio > threshold.maxAbsDevRatio)
            {
                if (reason.Length > 0) reason += "；";
                reason += string.Format("最大偏差{0:F3}kW>{1:F3}kW×{2:F1}",
                    maxAbsDev, refConvMean, threshold.maxAbsDevRatio);
            }

            return new DiagnosisResult
            {
                RuleId = "P1",
                RuleName = "曲线形态偏离",
                Level = threshold.level,
                Description = string.Format(
                    "曲线形态偏离参考，areaDiffRatio={0:F3} maxAbsDev={1:F3}kW，refConvMean={2:F3}kW。{3}",
                    areaDiffRatio, maxAbsDev, refConvMean, reason),
                Value = areaDiffRatio,
                Reference = refConvMean
            };
        }
    }
}
