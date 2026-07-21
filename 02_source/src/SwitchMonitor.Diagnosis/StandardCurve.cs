using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 标准曲线 POCO。
    /// 由人工参考曲线（形态模板）与统计基线（水平锚定）融合生成，
    /// 兼具形态保真与统计稳健性。可直接作为 P1 逐点对比的模板使用。
    /// </summary>
    public class StandardCurve
    {
        /// <summary>道岔标识</summary>
        public string SwitchId;

        /// <summary>动作方向："定位→反位" 或 "反位→定位"</summary>
        public string Direction;

        /// <summary>采样间隔（秒），通常 0.04</summary>
        public double SampleInterval;

        /// <summary>对齐基准下标（重采样后的 spikeIndex），用于 P1 对齐</summary>
        public int AlignIndex;

        /// <summary>逐点功率值（kW），保留 3 位小数</summary>
        public List<double> Values;

        /// <summary>原始中位曲线值（未经融合），始终保留用于重新融合计算</summary>
        public List<double> OriginalMedianValues;

        // ── 融合溯源 ──

        /// <summary>融合权重 0~1。0=保持原参考曲线，1=完全对齐基线</summary>
        public double FusionWeight;

        /// <summary>来源参考曲线的标识（如 "reference_curves/1-J.json"）</summary>
        public string ReferenceSource;

        /// <summary>来源基线的计算时间 "yyyy-MM-dd HH:mm:ss"</summary>
        public string BaselineComputedAt;

        // ── 审计追踪：各段实际应用的缩放因子 ──

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

        public StandardCurve()
        {
            Values = new List<double>();
            OriginalMedianValues = new List<double>();
        }
    }

    /// <summary>
    /// 标准曲线存储与读写器。
    /// 存储路径: Rules/standard_curves/{switchId}_{direction}.json
    /// 字典 key 格式: "switchId|direction"（与 BaselineStore.MakeKey 一致）
    /// </summary>
    public static class StandardCurveStore
    {
        /// <summary>
        /// 保存一条标准曲线到标准曲线目录。
        /// 文件名 = {switchId}_{direction}.json
        /// </summary>
        /// <param name="directory">标准曲线目录（如 Rules/standard_curves/）</param>
        /// <param name="curve">标准曲线对象</param>
        public static void Save(string directory, StandardCurve curve)
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
        /// 加载一条标准曲线。文件不存在或 JSON 损坏时返回 null。
        /// </summary>
        public static StandardCurve Load(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var curve = serializer.Deserialize<StandardCurve>(json);
                return curve;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 加载目录下全部标准曲线。目录不存在时返回空字典。
        /// key = "switchId|direction"
        /// </summary>
        public static Dictionary<string, StandardCurve> LoadAll(string directory)
        {
            var result = new Dictionary<string, StandardCurve>();
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
}
