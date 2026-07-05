using System;
using System.Collections.Generic;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 一次道岔动作的完整数据包（诊断引擎的输入）
    /// </summary>
    public class SwitchActionData
    {
        /// <summary>车站名称</summary>
        public string StationName { get; set; }

        /// <summary>道岔标识（如 "SW_01"）</summary>
        public string SwitchId { get; set; }

        /// <summary>动作开始时间 (Unix timestamp)</summary>
        public long StartTime { get; set; }

        /// <summary>动作结束时间 (Unix timestamp)</summary>
        public long EndTime { get; set; }

        /// <summary>动作方向: "定位->反位" / "反位->定位" / "未知"</summary>
        public string Direction { get; set; }

        /// <summary>采样率 (Hz)，默认 25</summary>
        public int SampleRate { get; set; }

        /// <summary>相数（1 或 3）</summary>
        public int PhaseCount { get; set; }

        /// <summary>每相采样点数</summary>
        public int SampleCount { get; set; }

        /// <summary>来源文件名</summary>
        public string FileSource { get; set; }

        /// <summary>所有采样点数据</summary>
        public List<SamplePoint> Samples { get; set; }

        /// <summary>动作前电压 (V)</summary>
        public float? VoltageBefore { get; set; }

        /// <summary>动作后电压 (V)</summary>
        public float? VoltageAfter { get; set; }

        public SwitchActionData()
        {
            Samples = new List<SamplePoint>();
            SampleRate = 25;
        }

        public override string ToString()
        {
            return string.Format("Switch={0} Dir={1} Start={2} End={3} Samples={4} Rate={5}Hz",
                SwitchId, Direction, StartTime, EndTime,
                Samples != null ? Samples.Count : 0, SampleRate);
        }
    }
}
