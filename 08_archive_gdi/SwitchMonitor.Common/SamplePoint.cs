using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 单个采样点的数据
    /// </summary>
    public class SamplePoint
    {
        /// <summary>采样序号 (0, 1, 2, ...)</summary>
        public int Index { get; set; }

        /// <summary>该采样点的 Unix 时间戳</summary>
        public long Timestamp { get; set; }

        /// <summary>相别: "A" / "B" / "C" / "P"(功率)</summary>
        public string Phase { get; set; }

        /// <summary>电流 (A)</summary>
        public float Current { get; set; }

        /// <summary>电压 (V)</summary>
        public float Voltage { get; set; }

        /// <summary>功率 (W)</summary>
        public float Power { get; set; }

        /// <summary>原始采样值（无法区分电流/电压/功率时使用）</summary>
        public float RawValue { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}] T={1} Phase={2} I={3:F2}A V={4:F1}V P={5:F1}W",
                Index, Timestamp, Phase, Current, Voltage, Power);
        }
    }
}
