using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 数据库中的单条曲线采样记录（对应 CurveSamples 表的一行）
    /// </summary>
    public class CurveSampleRecord
    {
        /// <summary>记录 ID</summary>
        public int Id { get; set; }

        /// <summary>关联的动作 ID</summary>
        public int ActionId { get; set; }

        /// <summary>采样序号 (0, 1, 2, ...)</summary>
        public int SampleIndex { get; set; }

        /// <summary>采样时间戳 (Unix timestamp)</summary>
        public long Timestamp { get; set; }

        /// <summary>相别: "A" / "B" / "C" / "P"(功率)</summary>
        public string Phase { get; set; }

        /// <summary>电流 (A)</summary>
        public float Current { get; set; }

        /// <summary>电压 (V)</summary>
        public float Voltage { get; set; }

        /// <summary>功率 (W 或 kW)</summary>
        public float Power { get; set; }

        /// <summary>原始采样值</summary>
        public float RawValue { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}] Phase={1} I={2:F2}A V={3:F1}V P={4:F1}W",
                SampleIndex, Phase, Current, Voltage, Power);
        }
    }
}
