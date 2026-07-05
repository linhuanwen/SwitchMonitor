using System;
using System.Collections.Generic;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// Digit(*).dat 二进制文件解析器。
    /// 将 Python 参考实现 parse_local_receive.py 的 parse_digit_file() 翻译为 C#。
    ///
    /// 记录格式（可变长度）:
    ///   - 4 字节 timestamp (little-endian uint32)
    ///   - 2 字节 record_type / count (big-endian uint16)
    ///   - record_type × 2 字节点数据 (big-endian uint16 each)
    ///   - 1 字节 checksum/padding
    ///   总大小 = 7 + 2 × record_type
    ///
    /// 每个点数据字:
    ///   - 高字节: state/quality code
    ///   - 低字节: point ID
    /// </summary>
    public class DigitParser
    {
        // 文件头偏移常量
        private const int HEADER_TS_START_OFFSET = 12;
        private const int HEADER_TS_END_OFFSET = 16;
        private const int HEADER_SIZE_GUARD = 24;

        // 数据扫描起始偏移
        private const int SCAN_START_OFFSET = 1000;

        // 验证边界
        private const int TS_TOLERANCE_SECONDS = 60;
        private const int MIN_RECORD_TYPE = 1;
        private const int MAX_RECORD_TYPE = 20;

        // 最小记录大小 (7 bytes)
        private const int MIN_RECORD_SIZE = 7;

        /// <summary>解析过程中遇到的警告和错误</summary>
        public List<string> Errors { get; private set; }

        public DigitParser()
        {
            Errors = new List<string>();
        }

        /// <summary>
        /// 解析 Digit(*).dat 文件的全部字节内容。
        /// 返回开关量状态事件列表。
        /// </summary>
        /// <param name="fileData">文件的完整字节数组</param>
        /// <param name="fileSource">来源文件名（用于数据库记录）</param>
        /// <returns>解析出的状态事件列表</returns>
        public List<StatusEvent> Parse(byte[] fileData, string fileSource)
        {
            Errors.Clear();

            if (fileData == null || fileData.Length < HEADER_SIZE_GUARD)
            {
                Errors.Add(string.Format("文件 {0}: 数据不足，总长 {1} 字节 < {2}",
                    fileSource, fileData != null ? fileData.Length : 0, HEADER_SIZE_GUARD));
                return new List<StatusEvent>();
            }

            // 读取文件头时间戳
            long tsStart = BitConverter.ToUInt32(fileData, HEADER_TS_START_OFFSET);
            long tsEnd = BitConverter.ToUInt32(fileData, HEADER_TS_END_OFFSET);

            // 从偏移 1000 起扫描，找到第一条有效数据记录
            int dataStart = FindFirstRecord(fileData, tsStart, tsEnd);

            if (dataStart < 0)
            {
                Errors.Add(string.Format("文件 {0}: 未找到有效数据记录 (ts_start={1}, ts_end={2})",
                    fileSource, tsStart, tsEnd));
                return new List<StatusEvent>();
            }

            // 逐条解析记录
            var events = new List<StatusEvent>();
            int i = dataStart;

            while (i + MIN_RECORD_SIZE <= fileData.Length)
            {
                long ts = BitConverter.ToUInt32(fileData, i);

                // 验证时间戳范围
                if (ts < tsStart - TS_TOLERANCE_SECONDS || ts > tsEnd + TS_TOLERANCE_SECONDS)
                    break;

                // 读取 record_type (big-endian uint16)
                ushort recordType = ReadBigEndianUInt16(fileData, i + 4);

                // 验证 record_type
                if (recordType < MIN_RECORD_TYPE || recordType > MAX_RECORD_TYPE)
                    break;

                // 计算记录总大小: 7 + 2 * recordType
                int recordSize = MIN_RECORD_SIZE + 2 * recordType;
                if (i + recordSize > fileData.Length)
                    break;

                // 解析每个点数据 (big-endian uint16)
                int pointsStart = i + 6;
                for (int p = 0; p < recordType; p++)
                {
                    ushort rawValue = ReadBigEndianUInt16(fileData, pointsStart + p * 2);

                    var statusEvent = new StatusEvent
                    {
                        FileSource = fileSource,
                        Timestamp = ts,
                        PointId = rawValue & 0xFF,           // 低字节
                        StateByte = (rawValue >> 8) & 0xFF,   // 高字节
                        RawValue = rawValue,
                    };
                    events.Add(statusEvent);
                }

                i += recordSize;
            }

            if (events.Count == 0)
            {
                Errors.Add(string.Format("文件 {0}: 未解析出有效事件", fileSource));
            }

            return events;
        }

        /// <summary>
        /// 便捷方法：从文件路径读取字节并解析。
        /// 用于 FileWatcherService 的委托注入（Func&lt;string, List&lt;StatusEvent&gt;&gt;）。
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <returns>解析出的状态事件列表</returns>
        public List<StatusEvent> ParseFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException("filePath");

            byte[] fileData = System.IO.File.ReadAllBytes(filePath);
            string fileName = System.IO.Path.GetFileName(filePath);
            return Parse(fileData, fileName);
        }

        /// <summary>
        /// 从指定偏移开始扫描，找到第一条满足验证条件的数据记录起始位置。
        /// </summary>
        /// <returns>记录起始偏移，找不到返回 -1</returns>
        private static int FindFirstRecord(byte[] data, long tsStart, long tsEnd)
        {
            for (int i = SCAN_START_OFFSET; i <= data.Length - MIN_RECORD_SIZE - 2; i++)
            {
                long ts = BitConverter.ToUInt32(data, i);

                // 验证时间戳在容差范围内
                if (ts < tsStart - TS_TOLERANCE_SECONDS || ts > tsEnd + TS_TOLERANCE_SECONDS)
                    continue;

                // 验证 record_type 在有效范围
                ushort recordType = ReadBigEndianUInt16(data, i + 4);
                if (recordType >= MIN_RECORD_TYPE && recordType <= MAX_RECORD_TYPE)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 从字节数组读取 big-endian uint16
        /// </summary>
        private static ushort ReadBigEndianUInt16(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }
    }
}
