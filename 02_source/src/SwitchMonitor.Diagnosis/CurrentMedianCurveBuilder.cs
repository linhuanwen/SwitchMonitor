using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 三相电流中位曲线构建器。
    /// 从一批同一道岔同方向的历史三相电流曲线中，按各相 spike 点独立对齐后
    /// 逐相逐点取中位数，生成一条统计代表性三相电流标准曲线。
    ///
    /// 与功率 MedianCurveBuilder 对应，但对 A/B/C 三相独立处理。
    ///
    /// 与 CurrentStandardCurveBuilder 的区别：
    ///   CurrentStandardCurveBuilder = 单条参考曲线形态 × 基线标量比值 → 缩放融合
    ///   CurrentMedianCurveBuilder    = N 条历史曲线 spike 对齐 → 逐点中位数
    ///
    /// 中位曲线不依赖任何单条"模板事件"，形状完全由历史数据集体决定。
    /// </summary>
    public static class CurrentMedianCurveBuilder
    {
        /// <summary>
        /// 从一组三相电流曲线构建 spike 对齐的逐点中位标准曲线。
        /// </summary>
        /// <param name="currentCurvesA">A 相历史电流值序列（A，0.04s/点）</param>
        /// <param name="currentCurvesB">B 相历史电流值序列</param>
        /// <param name="currentCurvesC">C 相历史电流值序列</param>
        /// <param name="switchId">道岔标识</param>
        /// <param name="direction">动作方向</param>
        /// <param name="sampleInterval">采样间隔，默认 0.04s</param>
        /// <param name="minCurves">最少曲线数，低于此值返回 null</param>
        /// <returns>中位电流标准曲线；样本不足时返回 null</returns>
        public static List<PhaseCurrentStandardCurve> Build(
            List<List<double>> currentCurvesA,
            List<List<double>> currentCurvesB,
            List<List<double>> currentCurvesC,
            string switchId,
            string direction,
            double sampleInterval = 0.04,
            int minCurves = 10)
        {
            if (currentCurvesA == null || currentCurvesA.Count < minCurves) return null;
            if (currentCurvesB == null || currentCurvesB.Count < minCurves) return null;
            if (currentCurvesC == null || currentCurvesC.Count < minCurves) return null;

            // 各相独立构建中位波形
            var resultA = BuildPhase(currentCurvesA, minCurves);
            var resultB = BuildPhase(currentCurvesB, minCurves);
            var resultC = BuildPhase(currentCurvesC, minCurves);

            if (resultA == null && resultB == null && resultC == null)
                return null;

            var computedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            int sourceCount = Math.Max(
                resultA != null ? resultA.SourceCount : 0,
                Math.Max(resultB != null ? resultB.SourceCount : 0, resultC != null ? resultC.SourceCount : 0));
            var results = new List<PhaseCurrentStandardCurve>();
            // A 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = switchId, Direction = direction, Phase = "A",
                SampleInterval = sampleInterval,
                AlignIndex = resultA != null ? resultA.AlignIndex : 0,
                Values = resultA != null ? resultA.Values : new List<double>(),
                OriginalMedianValues = resultA != null ? new List<double>(resultA.Values) : new List<double>(),
                FusionWeight = 0.0,
                ReferenceSource = "median_of_" + sourceCount + "_curves",
                BaselineComputedAt = "", AlphaTime = 1.0,
                AlphaSpike = 1.0, AlphaUnlock = 1.0, AlphaConv = 1.0, AlphaLock = 1.0, AlphaTail = 1.0,
                ComputedAt = computedAt
            });
            // B 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = switchId, Direction = direction, Phase = "B",
                SampleInterval = sampleInterval,
                AlignIndex = resultB != null ? resultB.AlignIndex : 0,
                Values = resultB != null ? resultB.Values : new List<double>(),
                OriginalMedianValues = resultB != null ? new List<double>(resultB.Values) : new List<double>(),
                FusionWeight = 0.0,
                ReferenceSource = "median_of_" + sourceCount + "_curves",
                BaselineComputedAt = "", AlphaTime = 1.0,
                AlphaSpike = 1.0, AlphaUnlock = 1.0, AlphaConv = 1.0, AlphaLock = 1.0, AlphaTail = 1.0,
                ComputedAt = computedAt
            });
            // C 相
            results.Add(new PhaseCurrentStandardCurve
            {
                SwitchId = switchId, Direction = direction, Phase = "C",
                SampleInterval = sampleInterval,
                AlignIndex = resultC != null ? resultC.AlignIndex : 0,
                Values = resultC != null ? resultC.Values : new List<double>(),
                OriginalMedianValues = resultC != null ? new List<double>(resultC.Values) : new List<double>(),
                FusionWeight = 0.0,
                ReferenceSource = "median_of_" + sourceCount + "_curves",
                BaselineComputedAt = "", AlphaTime = 1.0,
                AlphaSpike = 1.0, AlphaUnlock = 1.0, AlphaConv = 1.0, AlphaLock = 1.0, AlphaTail = 1.0,
                ComputedAt = computedAt
            });
            return results;
        }

        // ═══════════════════════════════════════════════════════════
        //  内部：单相中位曲线构建
        // ═══════════════════════════════════════════════════════════

        /// <summary>单相构建结果</summary>
        private class PhaseMedianResult
        {
            public List<double> Values;
            public int AlignIndex;
            public int SourceCount;
        }

        /// <summary>
        /// 对单相曲线列表构建 spike 对齐的逐点中位曲线。
        /// 算法与功率 MedianCurveBuilder.Build 完全一致。
        /// </summary>
        private static PhaseMedianResult BuildPhase(List<List<double>> curves, int minCurves)
        {
            if (curves == null || curves.Count < minCurves)
                return null;

            int n = curves.Count;

            // ── Step 1: 过滤无效曲线，计算每条曲线的 spike 位置 ──
            var validCurves = new List<List<double>>();
            var spikeIndices = new List<int>();

            for (int i = 0; i < n; i++)
            {
                var curve = curves[i];
                if (curve == null || curve.Count < 10)
                    continue;

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
            int alignIndex = maxBefore;

            // ── Step 4: 逐点计算中位数 ──
            int halfN = validN / 2;
            var rawResult = new List<double>();

            for (int pos = 0; pos < gridSize; pos++)
            {
                var valuesAtPos = new List<double>(validN);
                int offset = pos - maxBefore;

                for (int i = 0; i < validN; i++)
                {
                    int actualIdx = spikeIndices[i] + offset;
                    if (actualIdx >= 0 && actualIdx < validCurves[i].Count)
                        valuesAtPos.Add(validCurves[i][actualIdx]);
                }

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

            // ── Step 5: 裁掉尾部近零区域 ──
            double peakAll = 0.0;
            foreach (double v in rawResult) { if (v > peakAll) peakAll = v; }
            double threshold = Math.Max(peakAll * 0.05, 0.01);
            int activeEnd = 0;
            for (int i = 0; i < rawResult.Count; i++)
            {
                if (rawResult[i] > threshold)
                    activeEnd = i;
            }
            int keepLen = Math.Min(rawResult.Count, activeEnd + 5);
            if (keepLen < 10) keepLen = rawResult.Count;

            var values = new List<double>(keepLen);
            for (int i = 0; i < keepLen; i++)
                values.Add(rawResult[i]);

            return new PhaseMedianResult
            {
                Values = values,
                AlignIndex = alignIndex < values.Count ? alignIndex : 0,
                SourceCount = validN
            };
        }
    }
}
