using System;
using System.Collections.Generic;
using System.Linq;
using SwitchMonitor.Data;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 三相电流曲线特征提取器。
    /// 每相独立执行五阶段分割（启动尖峰→解锁段→转换段→锁闭段→缓放段），
    /// 各相用自己的 spikeIndex 独立切分。
    ///
    /// 五阶段位置：spikeIndex → +2→+14 → +20→activeEnd-40 → activeEnd-40→-22 → activeEnd-22→-2
    /// </summary>
    public static class CurrentFeatureExtractor
    {
        /// <summary>
        /// 从 SwitchEvent 的三相电流提取 20 维特征。
        /// 三相电流数据缺失或不完整时 IsValid=false，但不会抛异常。
        /// </summary>
        public static CurrentFeatures Extract(SwitchEvent evt)
        {
            var f = new CurrentFeatures();

            if (evt == null)
            {
                return f;
            }

            // 传递动作方向
            f.Direction = evt.Direction;

            // 提取各相 value 列表
            var valuesA = ExtractValues(evt.CurrentA);
            var valuesB = ExtractValues(evt.CurrentB);
            var valuesC = ExtractValues(evt.CurrentC);

            f.RawValuesA = valuesA;
            f.RawValuesB = valuesB;
            f.RawValuesC = valuesC;

            // 采样点数（取各相最大值，优先 A 相）
            int nA = valuesA.Count;
            int nB = valuesB.Count;
            int nC = valuesC.Count;
            int n = Math.Max(nA, Math.Max(nB, nC));
            f.SampleCount = n;
            f.IsFullWindow = n >= 780;

            // 各相独立提取
            var phaseA = ExtractPhaseInternal(valuesA);
            var phaseB = ExtractPhaseInternal(valuesB);
            var phaseC = ExtractPhaseInternal(valuesC);

            // 合并各相特征
            CopyPhaseFeatures(ref f, phaseA, 'A');
            CopyPhaseFeatures(ref f, phaseB, 'B');
            CopyPhaseFeatures(ref f, phaseC, 'C');

            // 整体有效性：至少有一相有效
            f.IsValid = phaseA.IsValid || phaseB.IsValid || phaseC.IsValid;

            // 三相汇总：DurationSec = max(activeEnd) × 0.04
            int maxActiveEnd = Math.Max(phaseA.ActiveEnd, Math.Max(phaseB.ActiveEnd, phaseC.ActiveEnd));
            f.ActiveEnd = maxActiveEnd;
            if (f.IsValid)
            {
                f.DurationSec = Math.Round((maxActiveEnd + 1) * 0.04, 2);
            }

            // MaxUnbalanceRatio：基于转换段均值的三相不平衡度
            f.MaxUnbalanceRatio = ComputeMaxUnbalanceRatio(phaseA.ConvMean, phaseB.ConvMean, phaseC.ConvMean);

            return f;
        }

        /// <summary>
        /// 从单相采样值列表提取特征（供测试/验证使用）。
        /// 返回的 CurrentFeatures 仅填充 A 相字段。
        /// </summary>
        public static CurrentFeatures ExtractPhase(IList<double> values)
        {
            var internalF = ExtractPhaseInternal(values);
            var f = new CurrentFeatures();
            f.RawValuesA = new List<double>(values);
            f.SampleCount = values.Count;
            f.IsFullWindow = values.Count >= 780;
            f.IsValid = internalF.IsValid;
            f.ActiveEnd = internalF.ActiveEnd;

            CopyPhaseFeatures(ref f, internalF, 'A');

            if (f.IsValid)
            {
                f.DurationSec = Math.Round((internalF.ActiveEnd + 1) * 0.04, 2);
            }

            return f;
        }

        // ──────────────── 内部实现 ────────────────

        /// <summary>内部阶段特征结构</summary>
        private struct PhaseFeaturesInternal
        {
            public bool IsValid;
            public int ActiveEnd;
            public double SpikePeak;
            public int SpikeIndex;
            public double UnlockMean;
            public double ConvMean;
            public double LockMean;
            public double TailMean;
        }

        /// <summary>从 [t, v] 对中提取 v 列</summary>
        private static List<double> ExtractValues(List<double[]> pairs)
        {
            if (pairs == null || pairs.Count == 0)
                return new List<double>();

            var values = new List<double>(pairs.Count);
            foreach (var pair in pairs)
            {
                if (pair != null && pair.Length >= 2)
                    values.Add(pair[1]);
                else
                    values.Add(0.0);
            }
            return values;
        }

        /// <summary>对单相采样值执行五阶段分割</summary>
        private static PhaseFeaturesInternal ExtractPhaseInternal(IList<double> values)
        {
            var f = new PhaseFeaturesInternal();
            int n = values.Count;

            double peakAll = n > 0 ? values.Max() : 0.0;
            f.IsValid = n > 0 && peakAll > 0.01;

            if (!f.IsValid)
                return f;

            // 有效动作终点（去掉尾部零填充）
            double threshold = Math.Max(peakAll * 0.05, 0.01);
            int activeEnd = 0;
            for (int i = 0; i < n; i++)
            {
                if (values[i] > threshold)
                    activeEnd = i;
            }
            f.ActiveEnd = activeEnd;

            // ① 启动尖峰：前 15 点内找最大值（多个相同取第一个）
            int headLen = Math.Min(15, n);
            double spikePeak = values[0];
            int spikeIndex = 0;
            for (int i = 1; i < headLen; i++)
            {
                if (values[i] > spikePeak)
                {
                    spikePeak = values[i];
                    spikeIndex = i;
                }
            }
            f.SpikePeak = Math.Round(spikePeak, 3);
            f.SpikeIndex = spikeIndex;

            // ② 解锁段：[spikeIndex+2, spikeIndex+14) 共 12 点
            int ulStart = spikeIndex + 2;
            int ulEnd = Math.Min(spikeIndex + 14, n);
            f.UnlockMean = ulStart < ulEnd
                ? Math.Round(SegmentMean(values, ulStart, ulEnd), 3)
                : 0.0;

            // ③ 转换段：首选 [spikeIndex+20, activeEnd-40)
            //    退化：[spikeIndex+2, activeEnd)；再退化：[0, activeEnd]
            int convStart, convEnd;
            if (activeEnd - 40 > spikeIndex + 20)
            {
                convStart = spikeIndex + 20;
                convEnd = activeEnd - 40;
            }
            else
            {
                convStart = spikeIndex + 2;
                convEnd = activeEnd;
            }
            if (convStart >= convEnd)
            {
                convStart = 0;
                convEnd = activeEnd + 1;
            }
            f.ConvMean = convStart < convEnd
                ? Math.Round(SegmentMean(values, convStart, convEnd), 3)
                : 0.0;

            // ④ 锁闭段：[activeEnd-40, activeEnd-22)；activeEnd ≤ 50 → lockMean = 0
            if (activeEnd > 50)
            {
                int lockStart = activeEnd - 40;
                int lockEnd = activeEnd - 22;
                if (lockStart < 0) lockStart = 0;
                f.LockMean = lockStart < lockEnd
                    ? Math.Round(SegmentMean(values, lockStart, lockEnd), 3)
                    : 0.0;
            }
            else
            {
                f.LockMean = 0.0;
            }

            // ⑤ 缓放段：[activeEnd-22, activeEnd-2)；activeEnd ≤ 30 → tailMean = 0
            if (activeEnd > 30)
            {
                int tailStart = activeEnd - 22;
                int tailEnd = activeEnd - 2;
                if (tailStart < 0) tailStart = 0;
                f.TailMean = tailStart < tailEnd
                    ? Math.Round(SegmentMean(values, tailStart, tailEnd), 3)
                    : 0.0;
            }
            else
            {
                f.TailMean = 0.0;
            }

            return f;
        }

        /// <summary>将内部阶段特征复制到 CurrentFeatures 的某相字段</summary>
        private static void CopyPhaseFeatures(ref CurrentFeatures f, PhaseFeaturesInternal phase, char which)
        {
            switch (which)
            {
                case 'A':
                    f.SpikePeakA = phase.SpikePeak;
                    f.SpikeIndexA = phase.SpikeIndex;
                    f.UnlockMeanA = phase.UnlockMean;
                    f.ConvMeanA = phase.ConvMean;
                    f.LockMeanA = phase.LockMean;
                    f.TailMeanA = phase.TailMean;
                    break;
                case 'B':
                    f.SpikePeakB = phase.SpikePeak;
                    f.SpikeIndexB = phase.SpikeIndex;
                    f.UnlockMeanB = phase.UnlockMean;
                    f.ConvMeanB = phase.ConvMean;
                    f.LockMeanB = phase.LockMean;
                    f.TailMeanB = phase.TailMean;
                    break;
                case 'C':
                    f.SpikePeakC = phase.SpikePeak;
                    f.SpikeIndexC = phase.SpikeIndex;
                    f.UnlockMeanC = phase.UnlockMean;
                    f.ConvMeanC = phase.ConvMean;
                    f.LockMeanC = phase.LockMean;
                    f.TailMeanC = phase.TailMean;
                    break;
            }
        }

        /// <summary>
        /// 计算三相最大不平衡度。
        /// 基于转换段均值（ConvMean），因为转换段样本最充足、最稳定。
        /// 公式：max(|ConvMean_A − threePhaseMean|, ...) / threePhaseMean
        /// </summary>
        private static double ComputeMaxUnbalanceRatio(double convA, double convB, double convC)
        {
            double threePhaseMean = (convA + convB + convC) / 3.0;
            if (threePhaseMean < 0.001)
                return 0.0;

            double maxDev = Math.Max(
                Math.Abs(convA - threePhaseMean),
                Math.Max(
                    Math.Abs(convB - threePhaseMean),
                    Math.Abs(convC - threePhaseMean)));
            return Math.Round(maxDev / threePhaseMean, 3);
        }

        /// <summary>计算 [start, end) 区间均值</summary>
        private static double SegmentMean(IList<double> values, int start, int end)
        {
            double sum = 0.0;
            int count = 0;
            for (int i = start; i < end && i < values.Count; i++)
            {
                sum += values[i];
                count++;
            }
            return count > 0 ? sum / count : 0.0;
        }
    }
}
