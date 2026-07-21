using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 中位曲线构建器。
    /// 从一批同一道岔同方向的历史功率曲线中，按 spike 点对齐后
    /// 逐点取中位数，生成一条统计代表性标准曲线。
    ///
    /// 与 StandardCurveBuilder 的区别：
    ///   StandardCurveBuilder = 单条参考曲线形态 × 基线标量比值 → 缩放融合
    ///   MedianCurveBuilder    = N 条历史曲线 spike 对齐 → 逐点中位数
    ///
    /// 中位曲线不依赖任何单条"模板事件"，形状完全由历史数据集体决定。
    /// </summary>
    public static class MedianCurveBuilder
    {
        /// <summary>
        /// 从一组功率曲线构建 spike 对齐的逐点中位标准曲线。
        /// </summary>
        /// <param name="powerCurves">同方向的历史功率值序列（kW，0.04s/点）</param>
        /// <param name="switchId">道岔标识</param>
        /// <param name="direction">动作方向</param>
        /// <param name="sampleInterval">采样间隔，默认 0.04s</param>
        /// <param name="minCurves">最少曲线数，低于此值返回 null</param>
        /// <returns>中位标准曲线；样本不足时返回 null</returns>
        public static StandardCurve Build(
            List<List<double>> powerCurves,
            string switchId,
            string direction,
            double sampleInterval = 0.04,
            int minCurves = 10)
        {
            if (powerCurves == null || powerCurves.Count < minCurves)
                return null;

            // ── Step 1: 过滤无效曲线，计算每条曲线的 spike 位置 ──
            int n = powerCurves.Count;
            var validCurves = new List<List<double>>();
            var spikeIndices = new List<int>();

            for (int i = 0; i < n; i++)
            {
                var curve = powerCurves[i];
                if (curve == null || curve.Count < 10)
                    continue;

                // 找 spike 峰值位置（前 15 点内最大值，与 FeatureExtractor 一致）
                int headLen = Math.Min(15, curve.Count);
                double spikePeak = curve[0];
                int spikeIdx = 0;
                for (int j = 1; j < headLen; j++)
                {
                    if (curve[j] > spikePeak)
                    {
                        spikePeak = curve[j];
                        spikeIdx = j;
                    }
                }

                validCurves.Add(curve);
                spikeIndices.Add(spikeIdx);
            }

            if (validCurves.Count < minCurves)
                return null;

            int validN = validCurves.Count;

            // ── Step 2: 过滤 spike 位置异常的曲线 ──
            // 计算 spike 位置的中位数，排除偏离超过 5 个采样点的曲线
            var sortedSpikes = new List<int>(spikeIndices);
            sortedSpikes.Sort();
            int medianSpike = sortedSpikes[sortedSpikes.Count / 2];

            var filteredCurves = new List<List<double>>();
            var filteredSpikes = new List<int>();
            for (int i = 0; i < validN; i++)
            {
                if (Math.Abs(spikeIndices[i] - medianSpike) <= 5)
                {
                    filteredCurves.Add(validCurves[i]);
                    filteredSpikes.Add(spikeIndices[i]);
                }
            }

            if (filteredCurves.Count < minCurves)
                return null;

            validCurves = filteredCurves;
            spikeIndices = filteredSpikes;
            validN = validCurves.Count;

            // ── Step 3: 确定公共时间网格 ──
            int maxBefore = 0;
            int maxAfter = 0;
            for (int i = 0; i < validN; i++)
            {
                int before = spikeIndices[i];
                int after = validCurves[i].Count - spikeIndices[i] - 1;
                if (before > maxBefore) maxBefore = before;
                if (after > maxAfter) maxAfter = after;
            }

            int gridSize = maxBefore + 1 + maxAfter;
            int alignIndex = maxBefore; // spike 在网格中的位置

            // ── Step 4: 逐点计算中位数 ──
            int halfN = validN / 2;
            var rawResult = new List<double>();

            for (int pos = 0; pos < gridSize; pos++)
            {
                var valuesAtPos = new List<double>(validN);
                int offset = pos - maxBefore; // 相对于 spike 的偏移

                for (int i = 0; i < validN; i++)
                {
                    int actualIdx = spikeIndices[i] + offset;
                    if (actualIdx >= 0 && actualIdx < validCurves[i].Count)
                    {
                        valuesAtPos.Add(validCurves[i][actualIdx]);
                    }
                }

                // 至少一半的曲线在该位置有数据才保留
                if (valuesAtPos.Count >= halfN)
                {
                    valuesAtPos.Sort();
                    double median;
                    int cnt = valuesAtPos.Count;
                    if (cnt % 2 == 1)
                        median = valuesAtPos[cnt / 2];
                    else
                        median = (valuesAtPos[cnt / 2 - 1] + valuesAtPos[cnt / 2]) / 2.0;
                    rawResult.Add(Math.Round(median, 3));
                }
            }

            if (rawResult.Count == 0)
                return null;

            // ── Step 5: 裁掉尾部近零区域（与 FeatureExtractor.ActiveEnd 逻辑一致） ──
            double peakAll = 0.0;
            foreach (double v in rawResult) { if (v > peakAll) peakAll = v; }
            double threshold = Math.Max(peakAll * 0.05, 0.01);
            int activeEnd = 0;
            for (int i = 0; i < rawResult.Count; i++)
            {
                if (rawResult[i] > threshold)
                    activeEnd = i;
            }
            // 保留到 activeEnd（包含），再加少量拖尾
            int keepLen = Math.Min(rawResult.Count, activeEnd + 5);
            if (keepLen < 10) keepLen = rawResult.Count; // 保底

            var values = new List<double>(keepLen);
            for (int i = 0; i < keepLen; i++)
                values.Add(rawResult[i]);

            // ── Step 6: 构建 StandardCurve ──
            return new StandardCurve
            {
                SwitchId = switchId,
                Direction = direction,
                SampleInterval = sampleInterval,
                AlignIndex = alignIndex < values.Count ? alignIndex : 0,
                Values = values,
                OriginalMedianValues = new List<double>(values),
                FusionWeight = 0.0,          // 不适用：中位曲线非融合产生
                ReferenceSource = "median_of_" + validN + "_curves",
                BaselineComputedAt = "",
                AlphaTime = 1.0,
                AlphaSpike = 1.0,
                AlphaUnlock = 1.0,
                AlphaConv = 1.0,
                AlphaLock = 1.0,
                AlphaTail = 1.0,
                ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
    }
}
