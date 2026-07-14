using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 基线计算器。
    /// 输入某台道岔的全部历史特征列表，按 CONTEXT.md §5 算法计算基线值。
    /// 算法与 Python diag_reference_check.py 的 build_baseline() 一致。
    /// </summary>
    public static class BaselineBuilder
    {
        /// <summary>
        /// 从特征列表计算基线。
        /// 正常样本数小于 minSamples 时返回 null（该台不建基线）。
        /// </summary>
        /// <param name="allFeatures">该台道岔全部历史曲线特征</param>
        /// <param name="minSamples">最小正常样本数，默认 30</param>
        /// <param name="direction">动作方向过滤（null = 不过滤，否则只取匹配方向的特征）</param>
        /// <returns>基线值；样本不足时返回 null</returns>
        public static SwitchBaseline Build(List<CurveFeatures> allFeatures, int minSamples = 30, string direction = null)
        {
            if (allFeatures == null || allFeatures.Count == 0)
                return null;

            // 步骤 1：过滤 — 排除 IsFullWindow、!IsValid、DurationSec < 2.4；可选用方向过滤
            var pool = new List<CurveFeatures>();
            foreach (var f in allFeatures)
            {
                if (f.IsValid && !f.IsFullWindow && f.DurationSec >= 2.4)
                {
                    if (direction != null && f.Direction != direction)
                        continue;
                    pool.Add(f);
                }
            }

            if (pool.Count == 0)
                return null;

            // 步骤 2：med = DurationSec 中位数
            var durations = new List<double>(pool.Count);
            foreach (var f in pool)
            {
                durations.Add(f.DurationSec);
            }
            double med = Median(durations);

            // 步骤 3：正常样本 = |DurationSec − med| < med × 0.15
            var normal = new List<CurveFeatures>();
            double threshold = med * 0.15;
            foreach (var f in pool)
            {
                if (Math.Abs(f.DurationSec - med) < threshold)
                {
                    normal.Add(f);
                }
            }

            if (normal.Count < minSamples)
                return null;

            // 步骤 4：各 ref* = 正常样本对应特征的中位数
            // DurationSec 保留 2 位，其余保留 3 位
            var baseline = new SwitchBaseline();

            var normalDurations = new List<double>(normal.Count);
            var normalSpikes = new List<double>(normal.Count);
            var normalUnlocks = new List<double>(normal.Count);
            var normalConvs = new List<double>(normal.Count);
            var normalLocks = new List<double>(normal.Count);
            var normalTails = new List<double>(normal.Count);

            foreach (var f in normal)
            {
                normalDurations.Add(f.DurationSec);
                normalSpikes.Add(f.SpikePeak);
                normalUnlocks.Add(f.UnlockMean);
                normalConvs.Add(f.ConvMean);
                normalLocks.Add(f.LockMean);
                normalTails.Add(f.TailMean);
            }

            baseline.RefDurationSec = Math.Round(Median(normalDurations), 2);
            baseline.RefSpikePeak = Math.Round(Median(normalSpikes), 3);
            baseline.RefUnlockMean = Math.Round(Median(normalUnlocks), 3);
            baseline.RefConvMean = Math.Round(Median(normalConvs), 3);
            baseline.RefLockMean = Math.Round(Median(normalLocks), 3);
            baseline.RefTailMean = Math.Round(Median(normalTails), 3);
            baseline.SampleCount = normal.Count;
            baseline.Direction = direction;

            return baseline;
        }

        /// <summary>
        /// 中位数计算，与 Python statistics.median 语义一致：
        /// 奇数个取中间值，偶数个取中间两值平均。
        /// </summary>
        private static double Median(List<double> values)
        {
            int n = values.Count;
            if (n == 0) return 0.0;

            // 复制一份排序，避免修改原列表
            var sorted = new List<double>(values);
            sorted.Sort();

            if (n % 2 == 1)
            {
                return sorted[n / 2];
            }
            else
            {
                return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
            }
        }
    }
}
