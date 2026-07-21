using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 电流基线构建器。
    /// 输入某台道岔的全部电流特征列表，按迭代中位数 + MAD 过滤算法计算基线。
    /// 算法与功率 BaselineBuilder 类似，但操作 20 维特征向量。
    /// </summary>
    public static class CurrentBaselineBuilder
    {
        /// <summary>
        /// 从电流特征列表计算基线。
        /// </summary>
        /// <param name="allFeatures">该台道岔全部历史电流特征</param>
        /// <param name="minSamples">最小正常样本数，默认 30</param>
        /// <param name="direction">动作方向过滤（null = 不过滤，否则只取匹配方向的特征）</param>
        /// <returns>基线值；样本不足时返回 null</returns>
        public static CurrentBaseline Build(List<CurrentFeatures> allFeatures, int minSamples = 30, string direction = null)
        {
            if (allFeatures == null || allFeatures.Count == 0)
                return null;

            // Step 1 — 前置过滤：排除 IsValid==false、IsFullWindow==true、DurationSec < 2.4；可选用方向过滤
            var pool = new List<CurrentFeatures>();
            foreach (var f in allFeatures)
            {
                if (f.IsValid && !f.IsFullWindow && f.DurationSec >= 2.4)
                {
                    // 方向过滤：仅当特征方向已知且与目标方向不匹配时才跳过
                    // 未知方向（null）视为匹配所有方向，避免数据不足
                    if (direction != null && f.Direction != null && f.Direction != direction)
                        continue;
                    pool.Add(f);
                }
            }

            if (pool.Count == 0)
                return null;

            // Step 2 — 第一次中位数聚合 → baseline_0 [20]
            double[] baseline0 = ComputeMedianVector(pool);

            // Step 3 — MAD 过滤（迭代剔除）
            var retained = MADFilter(pool, baseline0);

            if (retained.Count < minSamples)
                return null;

            // Step 4 — 第二次中位数聚合 → baseline_final [20]
            double[] baselineFinal = ComputeMedianVector(retained);

            // 输出 CurrentBaseline
            var baseline = PackBaseline(baselineFinal, retained.Count);
            baseline.Direction = direction;
            return baseline;
        }

        /// <summary>
        /// 对特征列表的每维取中位数，返回 20 维向量。
        /// 顺序：SpikePeakA, SpikeIndexA, UnlockMeanA, ConvMeanA, LockMeanA, TailMeanA,
        ///       SpikePeakB, SpikeIndexB, UnlockMeanB, ConvMeanB, LockMeanB, TailMeanB,
        ///       SpikePeakC, SpikeIndexC, UnlockMeanC, ConvMeanC, LockMeanC, TailMeanC,
        ///       DurationSec, MaxUnbalanceRatio
        /// </summary>
        private static double[] ComputeMedianVector(List<CurrentFeatures> features)
        {
            int n = features.Count;
            var spikePeakA = new List<double>(n);
            var spikeIndexA = new List<double>(n);
            var unlockMeanA = new List<double>(n);
            var convMeanA = new List<double>(n);
            var lockMeanA = new List<double>(n);
            var tailMeanA = new List<double>(n);

            var spikePeakB = new List<double>(n);
            var spikeIndexB = new List<double>(n);
            var unlockMeanB = new List<double>(n);
            var convMeanB = new List<double>(n);
            var lockMeanB = new List<double>(n);
            var tailMeanB = new List<double>(n);

            var spikePeakC = new List<double>(n);
            var spikeIndexC = new List<double>(n);
            var unlockMeanC = new List<double>(n);
            var convMeanC = new List<double>(n);
            var lockMeanC = new List<double>(n);
            var tailMeanC = new List<double>(n);

            var durationSec = new List<double>(n);
            var maxUnbalance = new List<double>(n);

            foreach (var f in features)
            {
                spikePeakA.Add(f.SpikePeakA); spikeIndexA.Add(f.SpikeIndexA);
                unlockMeanA.Add(f.UnlockMeanA); convMeanA.Add(f.ConvMeanA);
                lockMeanA.Add(f.LockMeanA); tailMeanA.Add(f.TailMeanA);

                spikePeakB.Add(f.SpikePeakB); spikeIndexB.Add(f.SpikeIndexB);
                unlockMeanB.Add(f.UnlockMeanB); convMeanB.Add(f.ConvMeanB);
                lockMeanB.Add(f.LockMeanB); tailMeanB.Add(f.TailMeanB);

                spikePeakC.Add(f.SpikePeakC); spikeIndexC.Add(f.SpikeIndexC);
                unlockMeanC.Add(f.UnlockMeanC); convMeanC.Add(f.ConvMeanC);
                lockMeanC.Add(f.LockMeanC); tailMeanC.Add(f.TailMeanC);

                durationSec.Add(f.DurationSec);
                maxUnbalance.Add(f.MaxUnbalanceRatio);
            }

            return new double[20]
            {
                Median(spikePeakA), Median(spikeIndexA), Median(unlockMeanA), Median(convMeanA), Median(lockMeanA), Median(tailMeanA),
                Median(spikePeakB), Median(spikeIndexB), Median(unlockMeanB), Median(convMeanB), Median(lockMeanB), Median(tailMeanB),
                Median(spikePeakC), Median(spikeIndexC), Median(unlockMeanC), Median(convMeanC), Median(lockMeanC), Median(tailMeanC),
                Median(durationSec), Median(maxUnbalance)
            };
        }

        /// <summary>
        /// MAD 过滤：计算每条曲线到 baseline0 的标准化欧氏距离，剔除超过阈值的曲线。
        /// 距离公式：dist_i = sqrt(Σ((feature_j - baseline0_j) / MAD_j)^2)
        /// 阈值：medDist + 3.0 × madDist
        /// </summary>
        private static List<CurrentFeatures> MADFilter(List<CurrentFeatures> features, double[] baseline0)
        {
            int m = features.Count;

            // 计算每维的 MAD
            double[] mads = ComputeMADs(features, baseline0);

            // 计算每条曲线的标准化距离
            var distances = new double[m];
            for (int i = 0; i < m; i++)
            {
                distances[i] = StandardizedDistance(features[i], baseline0, mads);
            }

            // 距离的中位数和 MAD
            double medDist = Median(new List<double>(distances));
            var absDeviations = new List<double>(m);
            for (int i = 0; i < m; i++)
            {
                absDeviations.Add(Math.Abs(distances[i] - medDist));
            }
            double madDist = Median(absDeviations);

            // 阈值
            double threshold = medDist + 3.0 * madDist;

            // 保留距离 ≤ 阈值的曲线
            var retained = new List<CurrentFeatures>();
            for (int i = 0; i < m; i++)
            {
                if (distances[i] <= threshold)
                {
                    retained.Add(features[i]);
                }
            }

            return retained;
        }

        /// <summary>计算每维特征的 MAD（中位数绝对偏差）</summary>
        private static double[] ComputeMADs(List<CurrentFeatures> features, double[] baseline0)
        {
            int m = features.Count;
            var deviations = new List<double>[20];
            for (int j = 0; j < 20; j++)
                deviations[j] = new List<double>(m);

            foreach (var f in features)
            {
                double[] vec = FeatureToVector(f);
                for (int j = 0; j < 20; j++)
                {
                    deviations[j].Add(Math.Abs(vec[j] - baseline0[j]));
                }
            }

            double[] mads = new double[20];
            for (int j = 0; j < 20; j++)
            {
                mads[j] = Median(deviations[j]);
                if (mads[j] < 1e-12)
                    mads[j] = 1e-6;  // 防止除零
            }

            return mads;
        }

        /// <summary>计算标准化欧氏距离</summary>
        private static double StandardizedDistance(CurrentFeatures f, double[] baseline, double[] mads)
        {
            double[] vec = FeatureToVector(f);
            double sumSq = 0.0;
            for (int j = 0; j < 20; j++)
            {
                double z = (vec[j] - baseline[j]) / mads[j];
                sumSq += z * z;
            }
            return Math.Sqrt(sumSq);
        }

        /// <summary>将 CurrentFeatures 展开为 20 维数组</summary>
        private static double[] FeatureToVector(CurrentFeatures f)
        {
            return new double[20]
            {
                f.SpikePeakA, (double)f.SpikeIndexA, f.UnlockMeanA, f.ConvMeanA, f.LockMeanA, f.TailMeanA,
                f.SpikePeakB, (double)f.SpikeIndexB, f.UnlockMeanB, f.ConvMeanB, f.LockMeanB, f.TailMeanB,
                f.SpikePeakC, (double)f.SpikeIndexC, f.UnlockMeanC, f.ConvMeanC, f.LockMeanC, f.TailMeanC,
                f.DurationSec, f.MaxUnbalanceRatio
            };
        }

        /// <summary>将 20 维中位数向量打包为 CurrentBaseline</summary>
        private static CurrentBaseline PackBaseline(double[] b, int sampleCount)
        {
            return new CurrentBaseline
            {
                RefSpikePeakA = Math.Round(b[0], 3),
                RefSpikeIndexA = (int)Math.Round(b[1]),
                RefUnlockMeanA = Math.Round(b[2], 3),
                RefConvMeanA = Math.Round(b[3], 3),
                RefLockMeanA = Math.Round(b[4], 3),
                RefTailMeanA = Math.Round(b[5], 3),

                RefSpikePeakB = Math.Round(b[6], 3),
                RefSpikeIndexB = (int)Math.Round(b[7]),
                RefUnlockMeanB = Math.Round(b[8], 3),
                RefConvMeanB = Math.Round(b[9], 3),
                RefLockMeanB = Math.Round(b[10], 3),
                RefTailMeanB = Math.Round(b[11], 3),

                RefSpikePeakC = Math.Round(b[12], 3),
                RefSpikeIndexC = (int)Math.Round(b[13]),
                RefUnlockMeanC = Math.Round(b[14], 3),
                RefConvMeanC = Math.Round(b[15], 3),
                RefLockMeanC = Math.Round(b[16], 3),
                RefTailMeanC = Math.Round(b[17], 3),

                RefDurationSec = Math.Round(b[18], 2),
                RefMaxUnbalanceRatio = Math.Round(b[19], 3),

                SampleCount = sampleCount
            };
        }

        /// <summary>
        /// 中位数计算，与 Python statistics.median 语义一致：
        /// 奇数个取中间值，偶数个取中间两值平均。
        /// </summary>
        private static double Median(List<double> values)
        {
            int n = values.Count;
            if (n == 0) return 0.0;

            var sorted = new List<double>(values);
            sorted.Sort();

            if (n % 2 == 1)
                return sorted[n / 2];
            else
                return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }
    }
}
