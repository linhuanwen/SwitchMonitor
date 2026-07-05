using System;
using Newtonsoft.Json;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 单相报警阈值配置（电流或功率）。
    /// </summary>
    public class PhaseThresholdConfig
    {
        /// <summary>是否启用报警上限</summary>
        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        /// <summary>报警上限值（单位：电流 A / 功率 kW）</summary>
        [JsonProperty("upperLimit")]
        public float UpperLimit { get; set; }

        /// <summary>报警上限线颜色（如 "#FF0000"）</summary>
        [JsonProperty("upperColor")]
        public string UpperColor { get; set; }

        /// <summary>报警上限线型（"solid" / "dash" / "dot"）</summary>
        [JsonProperty("upperLineStyle")]
        public string UpperLineStyle { get; set; }

        /// <summary>是否启用报警下限（预留，不做逻辑）</summary>
        [JsonProperty("lowerEnabled")]
        public bool LowerEnabled { get; set; }

        /// <summary>报警下限值（预留，不做逻辑）</summary>
        [JsonProperty("lowerLimit")]
        public float LowerLimit { get; set; }

        /// <summary>报警下限线颜色（预留）</summary>
        [JsonProperty("lowerColor")]
        public string LowerColor { get; set; }

        /// <summary>报警下限线型（预留）</summary>
        [JsonProperty("lowerLineStyle")]
        public string LowerLineStyle { get; set; }

        /// <summary>
        /// 创建带默认值的电流阈值配置。
        /// 默认上限 2.0A，红色虚线。
        /// </summary>
        public static PhaseThresholdConfig CreateCurrentDefault()
        {
            return new PhaseThresholdConfig
            {
                Enabled = true,
                UpperLimit = 2.0f,
                UpperColor = "#FF0000",
                UpperLineStyle = "dash",
                LowerEnabled = false,
                LowerLimit = 0.0f,
                LowerColor = "#0000FF",
                LowerLineStyle = "dash"
            };
        }

        /// <summary>
        /// 创建带默认值的功率阈值配置。
        /// 默认上限 1.5kW，红色虚线。
        /// </summary>
        public static PhaseThresholdConfig CreatePowerDefault()
        {
            return new PhaseThresholdConfig
            {
                Enabled = true,
                UpperLimit = 1.5f,
                UpperColor = "#FF0000",
                UpperLineStyle = "dash",
                LowerEnabled = false,
                LowerLimit = 0.0f,
                LowerColor = "#0000FF",
                LowerLineStyle = "dash"
            };
        }

        /// <summary>
        /// 深度克隆当前配置。
        /// </summary>
        public PhaseThresholdConfig Clone()
        {
            return new PhaseThresholdConfig
            {
                Enabled = this.Enabled,
                UpperLimit = this.UpperLimit,
                UpperColor = this.UpperColor,
                UpperLineStyle = this.UpperLineStyle,
                LowerEnabled = this.LowerEnabled,
                LowerLimit = this.LowerLimit,
                LowerColor = this.LowerColor,
                LowerLineStyle = this.LowerLineStyle
            };
        }
    }

    /// <summary>
    /// 报警阈值配置根对象。
    /// 序列化到 config.json 的 alarmThresholds 节。
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class AlarmThresholdConfig
    {
        /// <summary>电流曲线阈值配置</summary>
        [JsonProperty("current")]
        public PhaseThresholdConfig Current { get; set; }

        /// <summary>功率曲线阈值配置</summary>
        [JsonProperty("power")]
        public PhaseThresholdConfig Power { get; set; }

        /// <summary>
        /// Current.Enabled 且 Current 不为 null 时返回 true。
        /// </summary>
        [JsonIgnore]
        public bool IsCurrentThresholdActive
        {
            get { return Current != null && Current.Enabled; }
        }

        /// <summary>
        /// Power.Enabled 且 Power 不为 null 时返回 true。
        /// </summary>
        [JsonIgnore]
        public bool IsPowerThresholdActive
        {
            get { return Power != null && Power.Enabled; }
        }

        /// <summary>
        /// 创建带默认值的报警阈值配置。
        /// </summary>
        public static AlarmThresholdConfig CreateDefault()
        {
            return new AlarmThresholdConfig
            {
                Current = PhaseThresholdConfig.CreateCurrentDefault(),
                Power = PhaseThresholdConfig.CreatePowerDefault()
            };
        }

        /// <summary>
        /// 深度克隆当前配置（用于"取消"场景）。
        /// </summary>
        public AlarmThresholdConfig Clone()
        {
            return new AlarmThresholdConfig
            {
                Current = this.Current != null ? this.Current.Clone() : PhaseThresholdConfig.CreateCurrentDefault(),
                Power = this.Power != null ? this.Power.Clone() : PhaseThresholdConfig.CreatePowerDefault()
            };
        }

        /// <summary>
        /// 序列化时在外层包裹 alarmThresholds 根节点。
        /// </summary>
        public string ToJson()
        {
            var wrapper = new { alarmThresholds = this };
            return JsonConvert.SerializeObject(wrapper, Formatting.Indented);
        }

        /// <summary>
        /// 从 JSON 字符串反序列化（支持有无 alarmThresholds 根节点）。
        /// </summary>
        public static AlarmThresholdConfig FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return CreateDefault();

            try
            {
                // 尝试解析带 alarmThresholds 根节点的格式
                var wrapper = JsonConvert.DeserializeAnonymousType(json,
                    new { alarmThresholds = (AlarmThresholdConfig)null });
                if (wrapper != null && wrapper.alarmThresholds != null)
                    return wrapper.alarmThresholds;
            }
            catch (JsonException)
            {
                // 降级：尝试直接反序列化
            }

            try
            {
                var config = JsonConvert.DeserializeObject<AlarmThresholdConfig>(json);
                return config ?? CreateDefault();
            }
            catch (JsonException)
            {
                return CreateDefault();
            }
        }
    }
}
