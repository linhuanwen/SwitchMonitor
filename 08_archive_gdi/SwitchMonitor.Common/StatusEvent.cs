using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 开关量状态变化事件
    /// </summary>
    public class StatusEvent
    {
        /// <summary>来源文件名</summary>
        public string FileSource { get; set; }

        /// <summary>事件时间戳 (Unix timestamp)</summary>
        public long Timestamp { get; set; }

        /// <summary>采集点号</summary>
        public int PointId { get; set; }

        /// <summary>状态码 (如 0x2f = 吸起, 0x00 = 落下)</summary>
        public int StateByte { get; set; }

        /// <summary>原始 16-bit 值</summary>
        public int RawValue { get; set; }

        /// <summary>关联道岔标识（如果能匹配到）</summary>
        public string SwitchId { get; set; }

        public override string ToString()
        {
            return string.Format("T={0} Point={1} State=0x{2:X2} Raw=0x{3:X4} Switch={4}",
                Timestamp, PointId, StateByte, RawValue, SwitchId);
        }
    }
}
