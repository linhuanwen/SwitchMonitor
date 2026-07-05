using System;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 已处理文件的追踪记录
    /// </summary>
    public class ProcessedFileRecord
    {
        /// <summary>记录 ID</summary>
        public int Id { get; set; }

        /// <summary>文件完整路径</summary>
        public string FilePath { get; set; }

        /// <summary>文件大小 (bytes)</summary>
        public long FileSize { get; set; }

        /// <summary>最后处理时间</summary>
        public string LastProcessedTime { get; set; }

        /// <summary>从该文件导入的记录行数</summary>
        public int RowCount { get; set; }

        /// <summary>处理状态: "processed" / "error" / "skipped"</summary>
        public string Status { get; set; }

        /// <summary>错误信息（仅当 Status = "error" 时有值）</summary>
        public string ErrorMessage { get; set; }

        /// <summary>文件类型: "SwitchCurve" / "Digit" / "Unknown"</summary>
        public string FileType { get; set; }

        /// <summary>记录创建时间</summary>
        public string CreatedAt { get; set; }

        public ProcessedFileRecord()
        {
            Status = "processed";
            FileType = "Unknown";
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1} Rows={2} Status={3}",
                Id, FilePath, RowCount, Status);
        }
    }
}
