using System;
using System.Collections.Generic;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// CSM2010 格式的 SwitchCurve(*).dat 二进制文件解析器。
    /// 将 Python 参考实现 parse_csm2010.py 翻译为 C#。
    /// </summary>
    public class SwitchCurveParser
    {
        // CSM2010 二进制格式常量
        private const int BLOCK_SIZE = 4014;
        private const int HEADER_SIZE = 42;
        private const int DATA_START = 100032;

        // 验证边界
        private const long MIN_TIMESTAMP = 1_500_000_000;
        private const long MAX_TIMESTAMP = 2_000_000_000;
        private const int EXPECTED_SAMPLE_RATE = 25;
        private const int MIN_SAMPLE_COUNT = 10;
        private const int MAX_SAMPLE_COUNT = 2000;

        /// <summary>解析过程中遇到的警告和错误</summary>
        public List<string> Errors { get; private set; }

        public SwitchCurveParser()
        {
            Errors = new List<string>();
        }

        /// <summary>
        /// 解析 SwitchCurve(*).dat 文件的全部字节内容。
        /// 返回按 timestamp 分组的道岔动作数据列表。
        /// </summary>
        /// <param name="fileData">文件的完整字节数组</param>
        /// <param name="fileSource">来源文件名（用于数据库记录）</param>
        /// <returns>解析出的道岔动作数据列表</returns>
        public List<SwitchActionData> Parse(byte[] fileData, string fileSource)
        {
            Errors.Clear();
            var events = new List<RawBlock>();

            if (fileData == null || fileData.Length <= DATA_START)
            {
                Errors.Add(string.Format("文件 {0}: 数据不足，总长 {1} 字节 < 数据起点 {2}",
                    fileSource, fileData != null ? fileData.Length : 0, DATA_START));
                return new List<SwitchActionData>();
            }

            int off = DATA_START;
            while (off + BLOCK_SIZE <= fileData.Length)
            {
                // 解析块头 (42 字节)
                uint ts = BitConverter.ToUInt32(fileData, off);
                uint flags = BitConverter.ToUInt32(fileData, off + 4);
                ushort sampleRate = BitConverter.ToUInt16(fileData, off + 10);
                ushort sampleCount = BitConverter.ToUInt16(fileData, off + 12);

                // 验证
                if (ts < MIN_TIMESTAMP || ts > MAX_TIMESTAMP ||
                    sampleRate != EXPECTED_SAMPLE_RATE ||
                    sampleCount <= MIN_SAMPLE_COUNT || sampleCount >= MAX_SAMPLE_COUNT)
                {
                    // 无效块，跳过
                    off += BLOCK_SIZE;
                    continue;
                }

                // 解析采样值（float32 LE 数组，在块头之后）
                int nFloats = (BLOCK_SIZE - HEADER_SIZE) / 4;
                var samples = new float[sampleCount];
                int sampleDataStart = off + HEADER_SIZE;

                for (int i = 0; i < sampleCount && i < nFloats; i++)
                {
                    samples[i] = BitConverter.ToSingle(fileData, sampleDataStart + i * 4);
                }

                events.Add(new RawBlock
                {
                    Timestamp = ts,
                    Flags = flags,
                    SampleRate = sampleRate,
                    SampleCount = sampleCount,
                    Samples = samples
                });

                off += BLOCK_SIZE;
            }

            if (events.Count == 0)
            {
                Errors.Add(string.Format("文件 {0}: 未找到有效数据块", fileSource));
                return new List<SwitchActionData>();
            }

            // 按 timestamp 分组——同 timestamp 的数据块属于同一次动作的不同相
            var groups = new Dictionary<long, List<RawBlock>>();
            foreach (var e in events)
            {
                if (!groups.ContainsKey(e.Timestamp))
                    groups[e.Timestamp] = new List<RawBlock>();
                groups[e.Timestamp].Add(e);
            }

            var actions = new List<SwitchActionData>();
            foreach (var kvp in groups)
            {
                var blocks = kvp.Value;
                // SampleCount 取各组中最大值（不同 phase 可能有略微不同的采样数）
                int maxSampleCount = blocks[0].SampleCount;
                foreach (var b in blocks)
                {
                    if (b.SampleCount > maxSampleCount)
                        maxSampleCount = b.SampleCount;
                }

                var action = new SwitchActionData
                {
                    FileSource = fileSource,
                    SwitchId = ExtractSwitchId(fileSource),
                    StartTime = kvp.Key,
                    SampleRate = blocks[0].SampleRate,
                    PhaseCount = blocks.Count,
                    SampleCount = maxSampleCount,
                    Direction = "未知",
                    Samples = new List<SamplePoint>()
                };

                // 计算 EndTime: StartTime + (SampleCount / SampleRate)
                action.EndTime = action.StartTime + (long)(maxSampleCount / (double)action.SampleRate);

                // 展开每个相的数据块为采样点
                char phaseLabel = 'A';
                foreach (var block in blocks)
                {
                    string phase = GetPhaseLabel(block.Flags, phaseLabel);

                    for (int i = 0; i < block.SampleCount && i < block.Samples.Length; i++)
                    {
                        // 计算该采样点的时间戳
                        long sampleTs = action.StartTime + (long)(i * 1000.0 / action.SampleRate);

                        var point = new SamplePoint
                        {
                            Index = i,
                            Timestamp = sampleTs,
                            Phase = phase,
                            RawValue = block.Samples[i], // 当前无法区分 I/U/P，先存入 RawValue
                        };
                        action.Samples.Add(point);
                    }

                    phaseLabel++;
                }

                // 按 Phase 和 Index 排序
                action.Samples.Sort((a, b) =>
                {
                    int cmp = string.Compare(a.Phase, b.Phase, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                    return a.Index.CompareTo(b.Index);
                });

                actions.Add(action);
            }

            return actions;
        }

        /// <summary>
        /// 便捷方法：从文件路径读取字节并解析。
        /// 用于 FileWatcherService 的委托注入（Func&lt;string, List&lt;SwitchActionData&gt;&gt;）。
        /// </summary>
        /// <param name="filePath">文件完整路径</param>
        /// <returns>解析出的道岔动作数据列表</returns>
        public List<SwitchActionData> ParseFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException("filePath");

            byte[] fileData = System.IO.File.ReadAllBytes(filePath);
            string fileName = System.IO.Path.GetFileName(filePath);
            return Parse(fileData, fileName);
        }

        /// <summary>
        /// 从文件路径中提取道岔标识。
        /// 返回完整的不含扩展名文件名（如 "SwitchCurve(0).dat" → "SwitchCurve(0)"），
        /// 以便直接匹配 switch_mapping.json 中的 fileMapping 键。
        /// </summary>
        private static string ExtractSwitchId(string fileSource)
        {
            if (string.IsNullOrEmpty(fileSource))
                return "unknown";

            // 返回完整的不含扩展名文件名（如 "SwitchCurve(0)"）
            // 这样可以直接匹配 switch_mapping.json 中的 fileMapping 键
            return System.IO.Path.GetFileNameWithoutExtension(fileSource);
        }

        /// <summary>
        /// 根据 flags 值确定相别标签
        /// </summary>
        private static string GetPhaseLabel(uint flags, char fallbackLabel)
        {
            // flags byte3 编码: 文件索引+offset（offset 0=A相, 1=B相, 2=C相, 依据 DC.ini）
            switch (flags)
            {
                case 16777216: return "B";  // byte3=1 → offset=1 → B相
                case 33554432: return "C";  // byte3=2 → offset=2 → C相
                case 50331648: return "A";  // byte3=3 → offset=0 → A相
                default:
                    // 使用回退标签（A, B, C, ...）
                    return fallbackLabel.ToString();
            }
        }

        private class RawBlock
        {
            public uint Timestamp;
            public uint Flags;
            public ushort SampleRate;
            public ushort SampleCount;
            public float[] Samples;
        }
    }
}
