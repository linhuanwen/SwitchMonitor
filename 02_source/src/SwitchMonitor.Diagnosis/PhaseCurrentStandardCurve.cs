using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 单相电流标准曲线 POCO。
    /// 替代原捆绑 A/B/C 三相的 CurrentStandardCurve，每相独立存储为一个 JSON 文件。
    /// 文件命名: {switchId}_{direction}_{phase}.json（如 1-J_定位→反位_A.json）
    ///
    /// 与功率 StandardCurve 对应，但仅含单相波形。
    /// 48 条 = 8 开关 × 2 方向 × 3 相（原为 8 条捆绑文件）。
    /// </summary>
    public class PhaseCurrentStandardCurve
    {
        /// <summary>道岔标识（如 "1-J"）</summary>
        public string SwitchId;

        /// <summary>动作方向（"定位→反位" 或 "反位→定位"）</summary>
        public string Direction;

        /// <summary>相标识（"A" / "B" / "C"）</summary>
        public string Phase;

        /// <summary>采样间隔（秒），通常 0.04</summary>
        public double SampleInterval;

        /// <summary>对齐基准下标（spikeIndex）</summary>
        public int AlignIndex;

        /// <summary>逐点电流值（A），保留 3 位小数</summary>
        public List<double> Values;

        /// <summary>原始中位曲线值（未经融合），始终保留用于重新融合计算</summary>
        public List<double> OriginalMedianValues;

        /// <summary>融合权重 0~1。0=保持原参考曲线，1=完全对齐基线</summary>
        public double FusionWeight;

        /// <summary>来源参考曲线的标识</summary>
        public string ReferenceSource;

        /// <summary>来源基线的计算时间 "yyyy-MM-dd HH:mm:ss"</summary>
        public string BaselineComputedAt;

        /// <summary>时长缩放因子（目标时长 / 参考时长）</summary>
        public double AlphaTime;

        /// <summary>启动尖峰段缩放因子</summary>
        public double AlphaSpike;

        /// <summary>解锁段缩放因子</summary>
        public double AlphaUnlock;

        /// <summary>转换段缩放因子</summary>
        public double AlphaConv;

        /// <summary>锁闭段缩放因子</summary>
        public double AlphaLock;

        /// <summary>缓放段缩放因子</summary>
        public double AlphaTail;

        /// <summary>计算时间 "yyyy-MM-dd HH:mm:ss"</summary>
        public string ComputedAt;

        public PhaseCurrentStandardCurve()
        {
            Values = new List<double>();
            OriginalMedianValues = new List<double>();
        }
    }

    /// <summary>
    /// 分相电流标准曲线存储与读写器。
    /// 存储路径: Rules/current_standard_curves/{switchId}_{direction}_{phase}.json
    /// 字典 key 格式: "switchId|direction|phase"（如 "1-J|定位→反位|A"）
    /// </summary>
    public static class PhaseCurrentStandardCurveStore
    {
        /// <summary>
        /// 保存一条分相电流标准曲线。
        /// 文件名 = {switchId}_{direction}_{phase}.json
        /// </summary>
        /// <param name="directory">标准曲线目录（如 Rules/current_standard_curves/）</param>
        /// <param name="curve">分相电流标准曲线对象</param>
        public static void Save(string directory, PhaseCurrentStandardCurve curve)
        {
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string fileName = MakeFileName(curve.SwitchId, curve.Direction, curve.Phase);
            string filePath = Path.Combine(directory, fileName);
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(curve);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 加载一条分相电流标准曲线。文件不存在或 JSON 损坏时返回 null。
        /// </summary>
        public static PhaseCurrentStandardCurve Load(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var curve = serializer.Deserialize<PhaseCurrentStandardCurve>(json);
                return curve;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 加载目录下全部分相电流标准曲线。目录不存在时返回空字典。
        /// key = "switchId|direction|phase"（3 段式）
        /// </summary>
        public static Dictionary<string, PhaseCurrentStandardCurve> LoadAll(string directory)
        {
            var result = new Dictionary<string, PhaseCurrentStandardCurve>();
            if (!Directory.Exists(directory))
                return result;

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

        /// <summary>
        /// 构造文件名：{switchId}_{direction}_{phase}.json
        /// </summary>
        public static string MakeFileName(string switchId, string direction, string phase)
        {
            if (string.IsNullOrEmpty(direction))
                return switchId + "_" + (phase ?? "") + ".json";
            return switchId + "_" + direction + "_" + (phase ?? "") + ".json";
        }

        /// <summary>
        /// 构造存储 key："switchId|direction|phase"
        /// </summary>
        public static string MakeKey(string switchId, string direction, string phase)
        {
            return switchId + "|" + (direction ?? "") + "|" + (phase ?? "");
        }
    }
}
