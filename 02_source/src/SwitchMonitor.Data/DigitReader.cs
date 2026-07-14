using System;
using System.Collections.Generic;
using System.IO;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// Digit(*).dat 二进制文件读取器。
    /// 移植自 08_archive_gdi/SwitchMonitor.Data/DigitParser.cs，
    /// 参考 Python parse_local_receive.py 的 parse_digit_file()。
    ///
    /// 记录格式（可变长度）:
    ///   - 4 字节 timestamp (little-endian uint32)
    ///   - 2 字节 record_type / count (big-endian uint16)
    ///   - record_type × 2 字节点数据 (big-endian uint16 each)
    ///   总大小 = 7 + 2 × record_type
    ///
    /// 每个点数据字:
    ///   - 高字节: state/quality code (0x2f = 吸起/动作, 0x00 = 落下)
    ///   - 低字节: point ID
    /// </summary>
    public class DigitReader
    {
        // 文件头偏移常量
        private const int HEADER_TS_START_OFFSET = 12;
        private const int HEADER_TS_END_OFFSET = 16;
        private const int HEADER_SIZE_GUARD = 24;

        // 数据扫描起始偏移（跳过文件头）
        private const int SCAN_START_OFFSET = 1000;

        // 验证边界
        private const int TS_TOLERANCE_SECONDS = 60;
        private const int MIN_RECORD_TYPE = 1;
        private const int MAX_RECORD_TYPE = 20;
        private const int MIN_RECORD_SIZE = 7;  // 4(timestamp) + 2(type) + 1(min)

        /// <summary>
        /// 解析单个 Digit(*).dat 文件。
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <returns>解析出的开关量事件列表</returns>
        public List<DigitEvent> ParseFile(string filePath)
        {
            return ParseFile(filePath, null);
        }

        /// <summary>
        /// 解析单个 Digit(*).dat 文件，仅保留指定点号的事件。
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <param name="pointIdsOfInterest">关心的点号集合（null=保留全部）</param>
        /// <returns>解析出的开关量事件列表</returns>
        public List<DigitEvent> ParseFile(string filePath, HashSet<int> pointIdsOfInterest)
        {
            var events = new List<DigitEvent>();

            if (!File.Exists(filePath))
                return events;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(filePath);
            }
            catch
            {
                return events;
            }

            if (data.Length < HEADER_SIZE_GUARD)
                return events;

            // 读取文件头时间戳
            long tsStart = BitConverter.ToUInt32(data, HEADER_TS_START_OFFSET);
            long tsEnd = BitConverter.ToUInt32(data, HEADER_TS_END_OFFSET);

            // 扫描找到第一条有效记录
            int dataStart = FindFirstRecord(data, tsStart, tsEnd);
            if (dataStart < 0)
                return events;

            // 逐条解析
            int i = dataStart;
            while (i + MIN_RECORD_SIZE <= data.Length)
            {
                long ts = BitConverter.ToUInt32(data, i);

                // 验证时间戳范围
                if (ts < tsStart - TS_TOLERANCE_SECONDS || ts > tsEnd + TS_TOLERANCE_SECONDS)
                    break;

                // 读取 record_type (big-endian uint16)
                ushort recordType = ReadBigEndianUInt16(data, i + 4);

                if (recordType < MIN_RECORD_TYPE || recordType > MAX_RECORD_TYPE)
                    break;

                int recordSize = MIN_RECORD_SIZE + 2 * recordType;
                if (i + recordSize > data.Length)
                    break;

                // 解析每个点数据 (big-endian uint16)
                int pointsStart = i + 6;
                for (int p = 0; p < recordType; p++)
                {
                    ushort rawValue = ReadBigEndianUInt16(data, pointsStart + p * 2);
                    int pointId = rawValue & 0xFF;
                    int stateByte = (rawValue >> 8) & 0xFF;

                    // 按点号过滤
                    if (pointIdsOfInterest == null || pointIdsOfInterest.Contains(pointId))
                    {
                        events.Add(new DigitEvent
                        {
                            Timestamp = ts,
                            PointId = pointId,
                            StateByte = stateByte
                        });
                    }
                }

                i += recordSize;
            }

            return events;
        }

        /// <summary>
        /// 解析目录下所有 Digit(*).dat 文件，构建按时间戳升序排列的合并时间线。
        /// </summary>
        /// <param name="digitDir">Digit(*).dat 所在目录</param>
        /// <param name="pointIdsOfInterest">关心的点号集合（null=保留全部）</param>
        /// <returns>排序后的开关量事件列表</returns>
        public List<DigitEvent> BuildTimeline(string digitDir, HashSet<int> pointIdsOfInterest = null)
        {
            var allEvents = new List<DigitEvent>();

            if (string.IsNullOrEmpty(digitDir) || !Directory.Exists(digitDir))
                return allEvents;

            string[] files;
            try
            {
                files = Directory.GetFiles(digitDir, "Digit*.dat", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return allEvents;
            }

            // 按文件名排序，保证稳定的处理顺序
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                try
                {
                    var events = ParseFile(file, pointIdsOfInterest);
                    allEvents.AddRange(events);
                }
                catch
                {
                    // 跳过损坏的文件
                }
            }

            // 按时间戳升序排列
            allEvents.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            return allEvents;
        }

        // ── 内部工具方法 ──

        /// <summary>
        /// 从指定偏移开始扫描，找到第一条满足验证条件的数据记录起始位置。
        /// </summary>
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
                    return i;
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
