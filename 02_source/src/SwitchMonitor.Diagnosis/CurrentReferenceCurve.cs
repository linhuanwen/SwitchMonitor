using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 三相电流参考曲线 POCO。
    /// 正常样本按各相 spikeIndex 对齐后逐相逐点取中位数，截断/补齐到中位长度。
    /// 与功率 ReferenceCurve 对应，但包含 A/B/C 三相独立波形。
    /// </summary>
    public class CurrentReferenceCurve
    {
        /// <summary>道岔标识</summary>
        public string SwitchId;

        /// <summary>动作方向："定位→反位" 或 "反位→定位"</summary>
        public string Direction;

        /// <summary>采样间隔（秒），通常 0.04</summary>
        public double SampleInterval;

        /// <summary>A 相对齐基准下标（spikeIndex 中位数）</summary>
        public int AlignIndexA;

        /// <summary>B 相对齐基准下标（spikeIndex 中位数）</summary>
        public int AlignIndexB;

        /// <summary>C 相对齐基准下标（spikeIndex 中位数）</summary>
        public int AlignIndexC;

        /// <summary>A 相逐点电流值（A），保留 3 位小数</summary>
        public List<double> ValuesA;

        /// <summary>B 相逐点电流值（A）</summary>
        public List<double> ValuesB;

        /// <summary>C 相逐点电流值（A）</summary>
        public List<double> ValuesC;

        /// <summary>计算时间</summary>
        public string ComputedAt;

        /// <summary>来源："manual"=人工设定, "auto-picked"=自动挑选，空=未知</summary>
        public string Source;

        public CurrentReferenceCurve()
        {
            ValuesA = new List<double>();
            ValuesB = new List<double>();
            ValuesC = new List<double>();
        }
    }

    /// <summary>
    /// 电流参考曲线存储与读写器。
    /// 存储路径: Rules/current_reference_curves/{switchId}_{direction}.json
    /// 字典 key 格式: "switchId|direction"（与 BaselineStore.MakeKey 一致）
    /// </summary>
    public static class CurrentReferenceCurveStore
    {
        /// <summary>
        /// 保存一条电流参考曲线到 Rules/current_reference_curves/ 目录。
        /// 文件名 = {switchId}_{direction}.json
        /// </summary>
        public static void Save(string directory, CurrentReferenceCurve curve)
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
        /// 加载一条电流参考曲线。文件不存在或 JSON 损坏时返回 null。
        /// </summary>
        public static CurrentReferenceCurve Load(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var curve = serializer.Deserialize<CurrentReferenceCurve>(json);
                return curve;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 加载目录下全部电流参考曲线。目录不存在时返回空字典。
        /// key = "switchId|direction"
        /// </summary>
        public static Dictionary<string, CurrentReferenceCurve> LoadAll(string directory)
        {
            var result = new Dictionary<string, CurrentReferenceCurve>();
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
    /// 电流参考曲线构建器。
    /// 输入正常电流曲线样本（三相独立列表），按各相 spikeIndex 对齐后
    /// 逐相逐点取中位数，生成三相参考曲线。
    ///
    /// 算法：正常样本 → 各相独立 spike 对齐 → 逐相逐点中位数 → 截断/补齐到中位长度。
    /// 与功率 ReferenceCurveBuilder 对应。
    /// </summary>
    public static class CurrentReferenceCurveBuilder
    {
        /// <summary>
        /// 从正常三相电流曲线样本构建参考曲线。
        /// </summary>
        /// <param name="normalCurvesA">A 相正常曲线列表</param>
        /// <param name="normalCurvesB">B 相正常曲线列表</param>
        /// <param name="normalCurvesC">C 相正常曲线列表</param>
        /// <param name="sampleInterval">采样间隔，默认 0.04s</param>
        /// <param name="switchId">道岔标识</param>
        /// <param name="direction">动作方向</param>
        /// <returns>三相电流参考曲线；任一相样本不足时返回 null</returns>
        public static CurrentReferenceCurve Build(
            List<List<double>> normalCurvesA,
            List<List<double>> normalCurvesB,
            List<List<double>> normalCurvesC,
            double sampleInterval = 0.04,
            string switchId = "1-J",
            string direction = null)
        {
            if (normalCurvesA == null || normalCurvesA.Count == 0) return null;
            if (normalCurvesB == null || normalCurvesB.Count == 0) return null;
            if (normalCurvesC == null || normalCurvesC.Count == 0) return null;

            int n = Math.Min(normalCurvesA.Count, Math.Min(normalCurvesB.Count, normalCurvesC.Count));
            if (n == 0) return null;

            // 计算各相的中位长度
            int medianLenA = MedianInt(normalCurvesA.ConvertAll(c => c.Count));
            int medianLenB = MedianInt(normalCurvesB.ConvertAll(c => c.Count));
            int medianLenC = MedianInt(normalCurvesC.ConvertAll(c => c.Count));

            // 各相独立构建对齐后的中位波形
            var valuesA = BuildPhaseAligned(normalCurvesA, medianLenA, out int alignIdxA);
            var valuesB = BuildPhaseAligned(normalCurvesB, medianLenB, out int alignIdxB);
            var valuesC = BuildPhaseAligned(normalCurvesC, medianLenC, out int alignIdxC);

            return new CurrentReferenceCurve
            {
                SwitchId = switchId,
                Direction = direction,
                SampleInterval = sampleInterval,
                AlignIndexA = alignIdxA,
                AlignIndexB = alignIdxB,
                AlignIndexC = alignIdxC,
                ValuesA = valuesA,
                ValuesB = valuesB,
                ValuesC = valuesC,
                ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Source = "auto-picked"
            };
        }

        /// <summary>
        /// 对单相曲线列表执行 spike 对齐 → 逐点中位数。
        /// 返回对齐后的中位值序列和对齐基准下标。
        /// </summary>
        private static List<double> BuildPhaseAligned(List<List<double>> curves, int medianLength, out int alignIndex)
        {
            alignIndex = 0;
            int n = curves.Count;
            if (n == 0) return new List<double>();

            // 1. 计算各曲线的 spikeIndex
            var spikeIndices = new List<int>(n);
            foreach (var curve in curves)
                spikeIndices.Add(FindSpikeIndex(curve));

            // 2. alignIndex = spikeIndex 中位数
            alignIndex = MedianInt(spikeIndices);

            // 3. 计算各曲线对齐偏移量
            var offsets = new int[n];
            for (int i = 0; i < n; i++)
                offsets[i] = alignIndex - spikeIndices[i];

            // 4. 逐点取中位数
            var result = new List<double>();
            for (int idx = 0; idx < medianLength; idx++)
            {
                var pointValues = new List<double>();
                for (int i = 0; i < n; i++)
                {
                    int sourceIdx = idx - offsets[i];
                    if (sourceIdx >= 0 && sourceIdx < curves[i].Count)
                        pointValues.Add(curves[i][sourceIdx]);
                }

                if (pointValues.Count > 0)
                    result.Add(Math.Round(Median(pointValues), 3));
                else if (result.Count > 0)
                    result.Add(result[result.Count - 1]);
                else
                    result.Add(0.0);
            }

            return result;
        }

        /// <summary>在前 15 点内找到 spikeIndex（最大值下标，多个相同取第一个）</summary>
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

    /// <summary>
    /// 单相电流参考曲线 POCO。
    /// 每相独立存储为一个 JSON 文件，与 PhaseCurrentStandardCurve 对应。
    /// 文件命名: {switchId}_{direction}_{phase}.json
    /// </summary>
    public class PhaseCurrentReferenceCurve
    {
        public string SwitchId;
        public string Direction;
        public string Phase;
        public double SampleInterval;
        public int AlignIndex;
        public List<double> Values;
        public string ComputedAt;
        public string Source;
        public string SourceDateTime;

        public PhaseCurrentReferenceCurve()
        {
            Values = new List<double>();
        }
    }

    /// <summary>
    /// 分相电流参考曲线存储与读写器。
    /// 存储路径: Rules/current_reference_curves/{switchId}_{direction}_{phase}.json
    /// 字典 key 格式: "switchId|direction|phase"
    /// </summary>
    public static class PhaseCurrentReferenceCurveStore
    {
        public static void Save(string directory, PhaseCurrentReferenceCurve curve)
        {
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            string fileName = MakeFileName(curve.SwitchId, curve.Direction, curve.Phase);
            string filePath = Path.Combine(directory, fileName);
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(curve);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        public static PhaseCurrentReferenceCurve Load(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                return new JavaScriptSerializer().Deserialize<PhaseCurrentReferenceCurve>(json);
            }
            catch { return null; }
        }

        public static Dictionary<string, PhaseCurrentReferenceCurve> LoadAll(string directory)
        {
            var result = new Dictionary<string, PhaseCurrentReferenceCurve>();
            if (!Directory.Exists(directory)) return result;
            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                var curve = Load(file);
                if (curve != null && !string.IsNullOrEmpty(curve.SwitchId)
                    && !string.IsNullOrEmpty(curve.Phase))
                {
                    string key = MakeKey(curve.SwitchId, curve.Direction, curve.Phase);
                    result[key] = curve;
                }
            }
            return result;
        }

        public static string MakeFileName(string switchId, string direction, string phase)
        {
            if (string.IsNullOrEmpty(direction))
                return switchId + "_" + (phase ?? "") + ".json";
            return switchId + "_" + direction + "_" + (phase ?? "") + ".json";
        }

        public static string MakeKey(string switchId, string direction, string phase)
        {
            return switchId + "|" + (direction ?? "") + "|" + (phase ?? "");
        }
    }
}
