using System;
using System.Collections.Generic;
using System.Linq;
using SwitchMonitor.Data;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 功率曲线特征提取器 + 五阶段物理边界分割。
    ///
    /// 五阶段以物理事件为边界（非固定偏移量）：
    ///   ① 启动尖峰 (SpikePeak) — 前 15 点最大值
    ///   ② 解锁段 (UnlockMean) — 启动→最后锁钩落下（经验比例+局部方差精化）
    ///   ③ 转换段 (ConvMean/ConvMax) — 解锁终点→密贴拐点
    ///   ④ 锁闭段 (LockMean) — 密贴拐点→锁闭爬升结束
    ///   ⑤ 缓放段 (TailMean) — 锁闭结束→归零前
    ///
    /// 物理模型依据 ZYJ7 外锁闭装置工作原理：
    ///   - 解锁看锁钩数量：3台机≈4.9s, 2台机≈3.7s
    ///   - 转换看动程长度：18号尖轨≈4.6s, 其他≈2.7s
    ///   - 锁闭基本恒定 ≈1.9-2.0s
    /// </summary>
    public static class FeatureExtractor
    {
        /// <summary>
        /// 核心入口：从功率采样值序列（kW，0.04s/点）提取 13 维特征。
        /// </summary>
        public static CurveFeatures Extract(IList<double> values)
        {
            var f = new CurveFeatures();
            // 保存原始值供 P1 逐点对比使用
            f.RawValues = new List<double>(values);
            int n = values.Count;
            f.SampleCount = n;
            f.IsFullWindow = n >= 780;

            double peakAll = 0.0;
            if (n > 0)
            {
                peakAll = values.Max();
            }
            f.IsValid = n > 0 && peakAll > 0.01;

            if (!f.IsValid)
            {
                return f;
            }

            // 有效动作终点（去掉尾部零填充）
            double threshold = Math.Max(peakAll * 0.05, 0.01);
            int activeEnd = 0;
            for (int i = 0; i < n; i++)
            {
                if (values[i] > threshold)
                {
                    activeEnd = i;
                }
            }
            f.ActiveEnd = activeEnd;
            f.DurationSec = Math.Round((activeEnd + 1) * 0.04, 2);

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

            // ── ② 检测解锁终点（物理边界：最后锁钩落下位置）──
            int unlockEnd = DetectUnlockEnd(values, spikeIndex, activeEnd);
            f.UnlockEnd = unlockEnd;
            if (unlockEnd > spikeIndex + 1)
            {
                f.UnlockMean = Math.Round(SegmentMean(values, spikeIndex + 2, unlockEnd + 1), 3);
            }
            else
            {
                // 退化：spikeIndex+2 到 activeEnd*0.5
                int fallbackEnd = Math.Max(spikeIndex + 14, (int)(activeEnd * 0.5));
                f.UnlockMean = Math.Round(SegmentMean(values, spikeIndex + 2, fallbackEnd), 3);
            }

            // ── ③ 检测密贴拐点 + 锁闭峰值 ──
            int lockStart, lockPeak;
            DetectContactAndLock(values, activeEnd, out lockStart, out lockPeak);
            f.LockStart = lockStart;
            if (lockStart < 0)
            {
                // 退化：用旧算法的 activeEnd-40 作为锁闭起点
                lockStart = activeEnd > 50 ? activeEnd - 40 : activeEnd;
            }

            // 转换段 = 解锁终点 → 密贴拐点
            int convStart = (unlockEnd > spikeIndex) ? unlockEnd + 1 : spikeIndex + 14;
            int convEnd = lockStart;
            if (convEnd > convStart && convStart < n)
            {
                if (convStart < convEnd)
                {
                    f.ConvMean = Math.Round(SegmentMean(values, convStart, convEnd), 3);
                    f.ConvMax = Math.Round(SegmentMax(values, convStart, convEnd), 3);
                }
                else
                {
                    f.ConvMean = 0.0;
                    f.ConvMax = 0.0;
                }

                // 台阶比：转换段等分三份
                int convLen = convEnd - convStart;
                int third = convLen / 3;
                if (third >= 5)
                {
                    double frontMean = SegmentMean(values, convStart, convStart + third);
                    double backMean = SegmentMean(values, convEnd - third, convEnd);
                    f.StepRatio = Math.Round(backMean / Math.Max(frontMean, 0.01), 3);
                }
                else
                {
                    f.StepRatio = 1.0;
                }
            }
            else
            {
                f.ConvMean = 0.0;
                f.ConvMax = 0.0;
                f.StepRatio = 1.0;
            }

            // ── ④ 锁闭段 + 缓放段 ──
            if (lockPeak >= 0 && lockStart >= 0 && lockPeak > lockStart)
            {
                // 锁闭终点：峰值后功率回落到密贴前水平
                double preRampLevel = lockStart >= 5
                    ? SegmentMean(values, lockStart - 5, lockStart + 1)
                    : values[lockStart];
                int postPeakSearchEnd = Math.Min(lockPeak + 40, activeEnd - 5);
                int lockEnd = lockPeak + 5; // 默认峰值后 5 点
                for (int i = lockPeak + 8; i < postPeakSearchEnd && i < n; i++)
                {
                    if (values[i] <= preRampLevel * 1.08 || values[i] <= values[lockPeak] * 0.55)
                    {
                        lockEnd = i;
                        break;
                    }
                }
                f.LockMean = Math.Round(SegmentMean(values, lockStart, lockEnd + 1), 3);
                int tailStart = lockEnd + 1;
                int tailEnd = activeEnd - 2;
                if (tailEnd > tailStart && activeEnd > 30)
                {
                    f.TailMean = Math.Round(SegmentMean(values, tailStart, tailEnd), 3);
                }
                else
                {
                    f.TailMean = 0.0;
                }
            }
            else
            {
                // 退化：旧算法
                if (activeEnd > 50)
                {
                    int lsOld = Math.Max(0, activeEnd - 40);
                    int leOld = activeEnd - 22;
                    if (lsOld < leOld)
                        f.LockMean = Math.Round(SegmentMean(values, lsOld, leOld), 3);
                    else
                        f.LockMean = 0.0;
                }
                else
                {
                    f.LockMean = 0.0;
                }
                // 旧算法缓放段
                if (activeEnd > 30)
                {
                    int ts = Math.Max(0, activeEnd - 22);
                    int te = activeEnd - 2;
                    if (ts < te)
                        f.TailMean = Math.Round(SegmentMean(values, ts, te), 3);
                    else
                        f.TailMean = 0.0;
                }
                else
                {
                    f.TailMean = 0.0;
                }
            }

            return f;
        }

        /// <summary>
        /// 便捷入口：从 SwitchEvent.Power 的 [t, v] 对中抽取 v 列后调用 Extract。
        /// Power 为空列表时返回 IsValid=false。
        /// </summary>
        public static CurveFeatures Extract(SwitchEvent evt)
        {
            if (evt == null || evt.Power == null || evt.Power.Count == 0)
            {
                return new CurveFeatures { IsValid = false };
            }
            var values = new List<double>(evt.Power.Count);
            foreach (var pair in evt.Power)
            {
                if (pair != null && pair.Length >= 2)
                {
                    values.Add(pair[1]);
                }
                else
                {
                    values.Add(0.0);
                }
            }
            var features = Extract(values);
            features.Direction = evt.Direction;
            return features;
        }

        /// <summary>
        /// 计算 values 在 [start, end) 区间的算术平均值
        /// </summary>
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

        /// <summary>
        /// 计算 values 在 [start, end) 区间的最大值
        /// </summary>
        private static double SegmentMax(IList<double> values, int start, int end)
        {
            double max = double.MinValue;
            bool found = false;
            for (int i = start; i < end && i < values.Count; i++)
            {
                if (values[i] > max)
                {
                    max = values[i];
                    found = true;
                }
            }
            return found ? max : 0.0;
        }

        // ──────────────── 物理边界检测 ────────────────

        /// <summary>
        /// 检测解锁终点 — 经验比例 + 局部方差精化法。
        /// J型（duration ≥ 10s, 3台机）：解锁 ≈ 42% × 总时长
        /// X型（duration < 10s, 2台机）：解锁 ≈ 43% × 总时长
        /// </summary>
        internal static int DetectUnlockEnd(IList<double> values, int spikeIndex, int activeEnd)
        {
            int n = values.Count;
            double durSec = (activeEnd + 1) * 0.04;
            bool isJType = durSec >= 10.0;
            double ratio = isJType ? 0.42 : 0.43;

            int baseIdx = spikeIndex + (int)((activeEnd - spikeIndex) * ratio);

            // 搜索窗口: 基准 ±20 点
            int searchStart = Math.Max(spikeIndex + 5, baseIdx - 20);
            int searchEnd = Math.Min((int)(activeEnd * 0.55), baseIdx + 20);
            if (searchEnd <= searchStart + 10)
                return baseIdx;

            // 精化: 找局部方差最小的 10 点窗口（最稳定 = 转换段开始）
            var smooth = MovingAverage(values, 7);
            int bestIdx = baseIdx;
            double bestVar = double.MaxValue;
            int window = 10;
            for (int i = searchStart; i < searchEnd - window && i + window < smooth.Length; i++)
            {
                double var = WindowVariance(smooth, i, i + window);
                if (var < bestVar)
                {
                    bestVar = var;
                    bestIdx = i + window / 2;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// 检测密贴拐点 + 锁闭峰值 — "先找峰，再找谷"法。
        /// 1. 在尾部区域找到锁闭爬升峰值
        /// 2. 从峰值向左回溯，找爬升前的谷底（密贴拐点）
        /// 返回: lockStart, lockPeak（-1 表示检测失败，需退化）
        /// </summary>
        internal static void DetectContactAndLock(IList<double> values, int activeEnd,
            out int lockStart, out int lockPeak)
        {
            lockStart = -1;
            lockPeak = -1;
            int n = values.Count;

            // 1. 找锁闭峰值：[activeEnd*0.7, activeEnd-5] 内最大值
            int peakSearchStart = (int)(activeEnd * 0.70);
            int peakSearchEnd = Math.Max(peakSearchStart + 10, activeEnd - 5);
            if (peakSearchEnd <= peakSearchStart || peakSearchEnd > n)
                return;

            double peakVal = values[peakSearchStart];
            int peakIdx = peakSearchStart;
            for (int i = peakSearchStart + 1; i < peakSearchEnd && i < n; i++)
            {
                if (values[i] > peakVal)
                {
                    peakVal = values[i];
                    peakIdx = i;
                }
            }
            lockPeak = peakIdx;

            // 2. 从峰值向左找谷底：[lockPeak-35, lockPeak-6]
            int valleySearchStart = Math.Max((int)(activeEnd * 0.55), lockPeak - 35);
            int valleySearchEnd = lockPeak - 6;
            if (valleySearchEnd <= valleySearchStart)
                return;

            var smooth = MovingAverage(values, 5);
            double minVal = smooth[valleySearchStart];
            int minIdx = valleySearchStart;
            for (int i = valleySearchStart + 1; i < valleySearchEnd && i < smooth.Length; i++)
            {
                if (smooth[i] < minVal)
                {
                    minVal = smooth[i];
                    minIdx = i;
                }
            }

            // 3. 验证：谷底和峰值之间应有足够的功率上升
            double rise = peakVal - minVal;
            if (rise < 0.02) // 上升太小，不是真正的锁闭爬升
                return;

            lockStart = minIdx;
        }

        /// <summary>
        /// 简单移动平均平滑（用于边界检测的内部辅助函数）
        /// </summary>
        internal static double[] MovingAverage(IList<double> values, int window)
        {
            int n = values.Count;
            var result = new double[n];
            int half = window / 2;
            for (int i = 0; i < n; i++)
            {
                int start = Math.Max(0, i - half);
                int end = Math.Min(n, i + half + 1);
                double sum = 0.0;
                for (int j = start; j < end; j++)
                    sum += values[j];
                result[i] = sum / (end - start);
            }
            return result;
        }

        /// <summary>
        /// 计算窗口内方差
        /// </summary>
        private static double WindowVariance(double[] values, int start, int end)
        {
            double sum = 0.0, sumSq = 0.0;
            int count = 0;
            for (int i = start; i < end && i < values.Length; i++)
            {
                sum += values[i];
                sumSq += values[i] * values[i];
                count++;
            }
            if (count < 3) return double.MaxValue;
            double mean = sum / count;
            return (sumSq / count) - (mean * mean);
        }
    }
}
