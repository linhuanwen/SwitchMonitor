using System;
using System.Collections.Generic;

namespace SwitchMonitor.Storage
{
    /// <summary>
    /// 事件表行记录 — Storage 层的轻量 POCO。
    /// 不依赖 SwitchMonitor.Data，避免循环引用。
    /// </summary>
    public class EventRecord
    {
        public long Id { get; set; }
        public string SwitchId { get; set; }
        public long Timestamp { get; set; }
        public string DateTimeStr { get; set; }
        public string Direction { get; set; }
        public double DurationSec { get; set; }
        public double SampleInterval { get; set; }
        public int SampleCount { get; set; }

        /// <summary>A 相电流 BLOB 原始字节</summary>
        public byte[] CurrentABlob { get; set; }

        /// <summary>B 相电流 BLOB 原始字节</summary>
        public byte[] CurrentBBlob { get; set; }

        /// <summary>C 相电流 BLOB 原始字节</summary>
        public byte[] CurrentCBlob { get; set; }

        /// <summary>功率 BLOB 原始字节</summary>
        public byte[] PowerBlob { get; set; }

        /// <summary>诊断结果 JSON 字符串</summary>
        public string DiagJson { get; set; }

        /// <summary>创建时间</summary>
        public string CreatedAt { get; set; }

        public EventRecord()
        {
            Direction = "未知";
            SampleInterval = 0.04;
        }
    }

    /// <summary>
    /// 诊断结果记录 — Storage 层的轻量 POCO。
    /// </summary>
    public class DiagnosisRecord
    {
        public long Timestamp { get; set; }
        public string Level { get; set; }
        public string DiagJson { get; set; }
    }
}
