using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 逐点参考曲线 POCO。
    /// 正常样本按 spikeIndex 对齐后逐点取中位数，截断/补齐到中位长度。
    /// </summary>
    public class ReferenceCurve
    {
        /// <summary>道岔标识</summary>
        public string SwitchId;

        /// <summary>动作方向："定位→反位" 或 "反位→定位"</summary>
        public string Direction;

        /// <summary>采样间隔（秒），通常 0.04</summary>
        public double SampleInterval;

        /// <summary>对齐基准下标（spikeIndex 中位数）</summary>
        public int AlignIndex;

        /// <summary>逐点功率值（kW），保留 3 位小数</summary>
        public List<double> Values;

        /// <summary>计算时间</summary>
        public string ComputedAt;

        /// <summary>来源："manual"=人工设定, "auto-picked"=自动挑选，空=未知</summary>
        public string Source;

        public ReferenceCurve()
        {
            Values = new List<double>();
        }
    }

    /// <summary>
    /// 参考曲线存储与读写器。
    /// 存储路径: Rules/reference_curves/{switchId}_{direction}.json
    /// 字典 key 格式: "switchId|direction"（与 BaselineStore.MakeKey 一致）
    /// </summary>
    public static class ReferenceCurveStore
    {
        /// <summary>
        /// 保存一条参考曲线到 Rules/reference_curves/ 目录。
        /// 文件名 = {switchId}_{direction}.json
        /// </summary>
        public static void Save(string directory, ReferenceCurve curve)
        {
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string fileName = MakeFileName(curve.SwitchId, curve.Direction);
            string filePath = Path.Combine(directory, fileName);
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(curve);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 加载一条参考曲线
        /// </summary>
        public static ReferenceCurve Load(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var curve = serializer.Deserialize<ReferenceCurve>(json);
                return curve;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 加载所有参考曲线。key = "switchId|direction"
        /// </summary>
        public static Dictionary<string, ReferenceCurve> LoadAll(string directory)
        {
            var result = new Dictionary<string, ReferenceCurve>();
            if (!Directory.Exists(directory))
                return result;

            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                var curve = Load(file);
                if (curve != null && !string.IsNullOrEmpty(curve.SwitchId))
                {
                    string key = BaselineStore.MakeKey(curve.SwitchId, curve.Direction);
                    result[key] = curve;
                }
            }

            return result;
        }

        /// <summary>
        /// 构造文件名：{switchId}_{direction}.json
        /// </summary>
        public static string MakeFileName(string switchId, string direction)
        {
            if (string.IsNullOrEmpty(direction))
                return switchId + ".json";
            return switchId + "_" + direction + ".json";
        }
    }

    /// <summary>
    /// 参考曲线构建器。
    /// 输入正常曲线列表（已在 spikeIndex 处对齐），输出逐点中位数参考曲线。
    /// 算法: 正常样本按 spikeIndex 对齐 → 逐点取中位数 → 截断/补齐到中位长度。
    /// </summary>
    public static class ReferenceCurveBuilder
    {
        /// <summary>
        /// 从正常功率曲线样本构建参考曲线。
        /// </summary>
        /// <param name="normalCurves">正常曲线列表（功率值序列）</param>
        /// <param name="sampleInterval">采样间隔</param>
        /// <param name="switchId">道岔标识</param>
        /// <returns>参考曲线</returns>
        public static ReferenceCurve Build(List<List<double>> normalCurves,
            double sampleInterval = 0.04, string switchId = "1-J")
        {
            if (normalCurves == null || normalCurves.Count == 0)
                return null;

            // 1. 计算每条曲线的 spikeIndex（前 15 点内最大值下标）
            var spikeIndices = new List<int>();
            foreach (var curve in normalCurves)
            {
                int si = FindSpikeIndex(curve);
                spikeIndices.Add(si);
            }

            // 2. alignIndex = spikeIndex 中位数
            int alignIndex = MedianInt(spikeIndices);

            // 3. 收集所有曲线的长度
            var lengths = new List<int>();
            foreach (var curve in normalCurves)
                lengths.Add(curve.Count);
            int medianLength = MedianInt(lengths);

            // 4. 以 alignIndex 对齐，逐点取中位数
            //    对每个 pointIdx（相对于 alignIndex 的偏移），收集所有曲线的值后取中位数
            int maxOffsetBefore = alignIndex; // 对齐点之前的最大点数
            int maxOffsetAfter = medianLength - alignIndex; // 对齐点之后的最大点数

            // 实际计算：取各曲线 alignIndex 前后的实际范围
            var values = new List<double>();

            // 对齐点前面的点（从 alignIndex 往前）
            for (int offset = -alignIndex; offset < maxOffsetAfter; offset++)
            {
                int absIdx = alignIndex + offset;
                if (absIdx < 0) continue;

                var pointValues = new List<double>();
                foreach (var curve in normalCurves)
                {
                    int curveAbsIdx = spikeIndices[normalCurves.IndexOf(curve)] + offset;
                    // 由于 spikeIndex 不同，需要用各曲线自己的 spikeIndex
                    // 重新实现：找到每条曲线自己的对应点
                }

                // 简化实现：以第一个 alignIndex 为基准
                var alignedValues = new List<double>();
                foreach (var curve in normalCurves)
                {
                    if (absIdx >= 0 && absIdx < curve.Count)
                    {
                        alignedValues.Add(curve[absIdx]);
                    }
                }
                if (alignedValues.Count > 0)
                {
                    values.Add(Math.Round(Median(alignedValues), 3));
                }
            }

            // 以上逻辑需要重构——正确处理各曲线不同 spikeIndex 的对齐
            // 重新实现简化版本：
            values = BuildAlignedValues(normalCurves, alignIndex, medianLength);

            return new ReferenceCurve
            {
                SwitchId = switchId,
                SampleInterval = sampleInterval,
                AlignIndex = alignIndex,
                Values = values,
                ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        /// <summary>
        /// 主对齐算法：将每条曲线的 spikeIndex 对齐到 alignIndex，
        /// 然后逐点取中位数，长度对齐到中位长度。
        /// </summary>
        private static List<double> BuildAlignedValues(List<List<double>> curves, int alignIndex, int medianLength)
        {
            var result = new List<double>();

            // 为每条曲线计算相对于 alignIndex 的偏移量
            int n = curves.Count;
            var offsets = new int[n];
            for (int i = 0; i < n; i++)
            {
                int si = FindSpikeIndex(curves[i]);
                offsets[i] = alignIndex - si; // 该曲线需要位移多少使 spikeIndex 对齐到 alignIndex
            }

            // 逐点（绝对坐标 idx = 0..medianLength-1）取各曲线对应值的中位数
            for (int idx = 0; idx < medianLength; idx++)
            {
                var pointValues = new List<double>();
                for (int i = 0; i < n; i++)
                {
                    int sourceIdx = idx - offsets[i]; // 该曲线在此绝对坐标对应的原始下标
                    if (sourceIdx >= 0 && sourceIdx < curves[i].Count)
                    {
                        pointValues.Add(curves[i][sourceIdx]);
                    }
                }

                if (pointValues.Count > 0)
                {
                    result.Add(Math.Round(Median(pointValues), 3));
                }
                else
                {
                    // 如果该点所有曲线都没有数据，用前一点填充
                    if (result.Count > 0)
                        result.Add(result[result.Count - 1]);
                    else
                        result.Add(0.0);
                }
            }

            return result;
        }

        /// <summary>
        /// 在前 15 点内找到 spikeIndex（最大值下标，多个相同取第一个）
        /// </summary>
        private static int FindSpikeIndex(List<double> curve)
        {
            if (curve == null || curve.Count == 0) return 0;

            int headLen = Math.Min(15, curve.Count);
            double maxVal = curve[0];
            int maxIdx = 0;
            for (int i = 1; i < headLen; i++)
            {
                if (curve[i] > maxVal)
                {
                    maxVal = curve[i];
                    maxIdx = i;
                }
            }
            return maxIdx;
        }

        /// <summary>
        /// 中位数（double）
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

        /// <summary>
        /// 中位数（int）
        /// </summary>
        private static int MedianInt(List<int> values)
        {
            int n = values.Count;
            if (n == 0) return 0;
            var sorted = new List<int>(values);
            sorted.Sort();
            if (n % 2 == 1)
                return sorted[n / 2];
            else
                return (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        }
    }
}
