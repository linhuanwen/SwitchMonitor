using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// Unix 时间戳与 DateTime 之间的转换辅助方法。
    /// .NET Framework 4.0 没有 DateTimeOffset.FromUnixTimeSeconds / ToUnixTimeSeconds。
    /// </summary>
    public static class DateTimeHelper
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// 将 Unix 时间戳（秒）转换为本地 DateTime。
        /// </summary>
        public static DateTime FromUnixTimestamp(long unixTimestamp)
        {
            return UnixEpoch.AddSeconds(unixTimestamp).ToLocalTime();
        }

        /// <summary>
        /// 将 DateTime 转换为 Unix 时间戳（秒）。
        /// </summary>
        public static long ToUnixTimestamp(DateTime dateTime)
        {
            return (long)(dateTime.ToUniversalTime() - UnixEpoch).TotalSeconds;
        }

        /// <summary>
        /// 将当前 DateTime 转换为 Unix 时间戳（秒）。
        /// </summary>
        public static long ToUnixTimestampNow()
        {
            return ToUnixTimestamp(DateTime.UtcNow);
        }
    }
}
