using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 单台道岔的基线 POCO。
    /// 6 项统计特征的中位数值，作为 D3 规则引擎 R1-R9 的参照基准。
    /// </summary>
    public class SwitchBaseline
    {
        /// <summary>动作时长参考值（秒）</summary>
        public double RefDurationSec;

        /// <summary>启动尖峰参考值（kW）</summary>
        public double RefSpikePeak;

        /// <summary>解锁段均值参考值（kW）</summary>
        public double RefUnlockMean;

        /// <summary>转换段均值参考值（kW）</summary>
        public double RefConvMean;

        /// <summary>锁闭段均值参考值（kW）</summary>
        public double RefLockMean;

        /// <summary>缓放段均值参考值（kW）</summary>
        public double RefTailMean;

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
    /// baselines.json 存储容器与读写器。
    /// 不存在或损坏时返回空 Store，不抛异常。
    /// key 格式："switchId|direction"（如 "4-J|定位→反位"），支持按方向分离基线。
    /// </summary>
    public class BaselineStore
    {
        /// <summary>计算时间 "yyyy-MM-dd HH:mm:ss"</summary>
        public string ComputedAt;

        /// <summary>key = "switchId|direction"（如 "1-J|定位→反位"）</summary>
        public Dictionary<string, SwitchBaseline> Switches;

        public BaselineStore()
        {
            Switches = new Dictionary<string, SwitchBaseline>();
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
        /// 从存储 key 提取 switchId
        /// </summary>
        public static string SwitchIdFromKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return key;
            int idx = key.IndexOf('|');
            return idx >= 0 ? key.Substring(0, idx) : key;
        }

        /// <summary>
        /// 两个动作方向常量
        /// </summary>
        public const string DirNormalToReverse = "定位→反位";
        public const string DirReverseToNormal = "反位→定位";

        /// <summary>
        /// 从路径加载 baselines.json。
        /// 文件不存在或 JSON 损坏时返回空的 BaselineStore（不抛异常）。
        /// </summary>
        public static BaselineStore Load(string path)
        {
            if (!File.Exists(path))
            {
                return new BaselineStore();
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var store = serializer.Deserialize<BaselineStore>(json);
                if (store == null || store.Switches == null)
                {
                    return new BaselineStore();
                }
                return store;
            }
            catch
            {
                return new BaselineStore();
            }
        }

        /// <summary>
        /// 保存到 baselines.json。
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
