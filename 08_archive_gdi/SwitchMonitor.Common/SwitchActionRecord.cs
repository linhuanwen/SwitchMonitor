using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 道岔动作列表记录（用于 DataGridView 展示的轻量级模型）
    /// </summary>
    public class SwitchActionRecord
    {
        /// <summary>数据库 ID</summary>
        public int Id { get; set; }

        /// <summary>来源文件名</summary>
        public string FileSource { get; set; }

        /// <summary>道岔标识</summary>
        public string SwitchId { get; set; }

        /// <summary>动作开始时间 (Unix timestamp)</summary>
        public long StartTime { get; set; }

        /// <summary>动作结束时间 (Unix timestamp)</summary>
        public long EndTime { get; set; }

        /// <summary>动作方向</summary>
        public string Direction { get; set; }

        /// <summary>相数 (1 或 3)</summary>
        public int PhaseCount { get; set; }

        /// <summary>每相采样点数</summary>
        public int SampleCount { get; set; }

        /// <summary>采样率 (Hz)</summary>
        public int SampleRate { get; set; }

        /// <summary>格式化后的开始时间字符串</summary>
        public string StartTimeDisplay
        {
            get
            {
                try
                {
                    var dt = DateTimeHelper.FromUnixTimestamp(StartTime);
                    return dt.ToString("yyyy-MM-dd HH:mm:ss");
                }
                catch
                {
                    return StartTime.ToString();
                }
            }
        }

        public override string ToString()
        {
            return string.Format("Id={0} Switch={1} Dir={2} Time={3} Samples={4}",
                Id, SwitchId, Direction, StartTimeDisplay, SampleCount);
        }
    }
}
