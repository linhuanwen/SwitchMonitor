using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 参考曲线记录（对应 ReferenceCurves 表的一行）。
    /// 用于：参考曲线管理窗口、设定/清除操作。
    /// </summary>
    public class ReferenceCurveRecord
    {
        /// <summary>记录 ID</summary>
        public long Id { get; set; }

        /// <summary>道岔标识</summary>
        public string SwitchId { get; set; }

        /// <summary>参考曲线来源的动作 ID（关联 SwitchActions 表）</summary>
        public int ActionId { get; set; }

        /// <summary>设定时间（yyyy-MM-dd HH:mm:ss 格式的本地时间字符串）</summary>
        public string SetTime { get; set; }

        /// <summary>备注（用户可选）</summary>
        public string Description { get; set; }

        /// <summary>是否当前使用中（1=活跃，0=已失效）</summary>
        public bool IsActive { get; set; }

        /// <summary>来源动作的开始时间 (Unix timestamp)，从关联的 SwitchActions 表 JOIN 获取</summary>
        public long SourceActionTime { get; set; }

        /// <summary>格式化后的来源动作时间字符串</summary>
        public string SourceActionTimeDisplay
        {
            get
            {
                try
                {
                    if (SourceActionTime > 0)
                    {
                        var dt = DateTimeHelper.FromUnixTimestamp(SourceActionTime);
                        return dt.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                }
                catch { }
                return SourceActionTime > 0 ? SourceActionTime.ToString() : "";
            }
        }

        /// <summary>格式化后的设定时间字符串（简短版，仅日期）</summary>
        public string SetTimeDateDisplay
        {
            get
            {
                if (!string.IsNullOrEmpty(SetTime) && SetTime.Length >= 10)
                    return SetTime.Substring(0, 10);
                return SetTime ?? "";
            }
        }

        public override string ToString()
        {
            string desc = !string.IsNullOrEmpty(Description) ? " (" + Description + ")" : "";
            return string.Format("SwitchId={0}, SetTime={1}{2}, Active={3}",
                SwitchId, SetTimeDateDisplay, desc, IsActive);
        }
    }
}
