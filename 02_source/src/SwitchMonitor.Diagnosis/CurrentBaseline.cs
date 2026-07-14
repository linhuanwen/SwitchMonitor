using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 单台道岔的电流基线 POCO。
    /// 20 项统计特征的中位数值，包含三相独立基线 + 三相汇总基线。
    /// </summary>
    public class CurrentBaseline
    {
        // ── A 相基线（6 项）──
        public double RefSpikePeakA;
        public int RefSpikeIndexA;          // 中位数取整
        public double RefUnlockMeanA;
        public double RefConvMeanA;
        public double RefLockMeanA;
        public double RefTailMeanA;

        // ── B 相基线（6 项）──
        public double RefSpikePeakB;
        public int RefSpikeIndexB;
        public double RefUnlockMeanB;
        public double RefConvMeanB;
        public double RefLockMeanB;
        public double RefTailMeanB;

        // ── C 相基线（6 项）──
        public double RefSpikePeakC;
        public int RefSpikeIndexC;
        public double RefUnlockMeanC;
        public double RefConvMeanC;
        public double RefLockMeanC;
        public double RefTailMeanC;

        // ── 三相汇总基线（2 项）──
        public double RefDurationSec;
        public double RefMaxUnbalanceRatio;

        // ── 元数据 ──
        /// <summary>参与统计的正常曲线数</summary>
        public int SampleCount;
        /// <summary>动作方向："定位→反位" 或 "反位→定位"</summary>
        public string Direction;
        /// <summary>数据起始日期 "yyyy-MM-dd"</summary>
        public string DateFrom;
        /// <summary>数据结束日期 "yyyy-MM-dd"</summary>
        public string DateTo;
    }

    /// <summary>
    /// current_baselines.json 存储容器与读写器。
    /// 不存在或损坏时返回空 Store，不抛异常。
    /// key 格式："switchId|direction"（如 "4-J|定位→反位"），支持按方向分离基线。
    /// </summary>
    public class CurrentBaselineStore
    {
        /// <summary>计算时间 "yyyy-MM-dd HH:mm:ss"</summary>
        public string ComputedAt;

        /// <summary>key = "switchId|direction"（如 "1-J|定位→反位"）</summary>
        public Dictionary<string, CurrentBaseline> Switches;

        public CurrentBaselineStore()
        {
            Switches = new Dictionary<string, CurrentBaseline>();
        }

        /// <summary>
        /// 构造存储 key："switchId|direction"
        /// </summary>
        public static string MakeKey(string switchId, string direction)
        {
            if (string.IsNullOrEmpty(direction))
                return switchId;
            return switchId + "|" + direction;
        }

        /// <summary>
        /// 从路径加载 current_baselines.json。
        /// 文件不存在或 JSON 损坏时返回空的 CurrentBaselineStore（不抛异常）。
        /// </summary>
        public static CurrentBaselineStore Load(string path)
        {
            if (!File.Exists(path))
            {
                return new CurrentBaselineStore();
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var store = serializer.Deserialize<CurrentBaselineStore>(json);
                if (store == null || store.Switches == null)
                {
                    return new CurrentBaselineStore();
                }
                return store;
            }
            catch
            {
                return new CurrentBaselineStore();
            }
        }

        /// <summary>
        /// 保存到 current_baselines.json。
        /// </summary>
        public void Save(string path)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string json = serializer.Serialize(this);

            // 确保目录存在
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, json, Encoding.UTF8);
        }
    }
}
