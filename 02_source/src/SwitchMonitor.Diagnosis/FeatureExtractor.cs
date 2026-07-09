using System;
using System.Collections.Generic;
using System.Linq;
using SwitchMonitor.Data;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 功率曲线特征提取器 + 五阶段分割。
    /// 算法严格按 CONTEXT.md §3 规格实现，与 Python diag_reference_check.py 一致。
    /// </summary>
    public static class FeatureExtractor
    {
        /// <summary>
        /// 核心入口：从功率采样值序列（kW，0.04s/点）提取 12 维特征。
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

            // ② 解锁段：[spikeIndex+2, spikeIndex+14) 共 12 点
            int ulStart = spikeIndex + 2;
            int ulEnd = Math.Min(spikeIndex + 14, n);
            if (ulStart < ulEnd)
            {
                f.UnlockMean = Math.Round(SegmentMean(values, ulStart, ulEnd), 3);
            }
            else
            {
                f.UnlockMean = 0.0;
            }

            // ③ 转换段：首选 [spikeIndex+20, activeEnd-40)
            //    若无效退化为 [spikeIndex+2, activeEnd)
            //    仍空则取 [0, activeEnd]
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
                convEnd = activeEnd + 1; // [0, activeEnd] inclusive
            }
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

            // 台阶比：转换段等分三份，前1/3长度 < 5 点时恒为 1.0
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

            // ⑤ 缓放尾段：[activeEnd-22, activeEnd-2) 共 20 点；activeEnd ≤ 30 时 tailMean = 0
            if (activeEnd > 30)
            {
                int tailStart = activeEnd - 22;
                int tailEnd = activeEnd - 2;
                if (tailStart < 0) tailStart = 0;
                if (tailStart < tailEnd)
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
                f.TailMean = 0.0;
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
            return Extract(values);
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
    }
}
