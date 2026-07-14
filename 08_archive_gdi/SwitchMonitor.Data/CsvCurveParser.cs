using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// CSM2010 CSV 导出的 SwitchCurve(*).csv 文件解析器。
    /// CSV 格式: timestamp,datetime,phase,s0,s1,...,s789
    /// Phase 值: byte3 编码 — 文件索引+offset（offset 0=A相, 1=B相, 2=C相, 依据 DC.ini）, 功率=文件索引+3
    /// </summary>
    public class CsvCurveParser
    {
        // Phase 常量（对应文件索引 0 时的 flag 值）
        public const uint PHASE_A = 50332416;    // byte3=3 → offset=0 → A相
        public const uint PHASE_B = 16777216;    // byte3=1 → offset=1 → B相
        public const uint PHASE_C = 33554432;    // byte3=2 → offset=2 → C相
        public const uint PHASE_POWER = 0;

        /// <summary>解析过程中遇到的警告和错误</summary>
        public List<string> Errors { get; private set; }

        public CsvCurveParser()
        {
            Errors = new List<string>();
        }

        /// <summary>
        /// 解析单个 CSV 文件，返回按 timestamp 分组的行数据。
        /// </summary>
        /// <param name="filePath">CSV 文件完整路径</param>
        /// <returns>按 timestamp 分组的字典（key=timestamp, value=该时间戳的所有行）</returns>
        public Dictionary<long, List<CsvRow>> ParseFile(string filePath)
        {
            Errors.Clear();
            var groups = new Dictionary<long, List<CsvRow>>();

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Errors.Add(string.Format("文件不存在或路径为空: {0}", filePath ?? "(null)"));
                return groups;
            }

            try
            {
                int lineNo = 0;
                using (var reader = new StreamReader(filePath, Encoding.UTF8))
                {
                    // 跳过头行
                    string header = reader.ReadLine();
                    lineNo = 1;
                    if (header == null)
                    {
                        Errors.Add(string.Format("文件为空: {0}", filePath));
                        return groups;
                    }

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNo++;
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        try
                        {
                            CsvRow row = ParseLine(line, lineNo);
                            if (row != null)
                            {
                                if (!groups.ContainsKey(row.Timestamp))
                                    groups[row.Timestamp] = new List<CsvRow>();
                                groups[row.Timestamp].Add(row);
                            }
                        }
                        catch (Exception ex)
                        {
                            Errors.Add(string.Format("{0}:{1} 跳过异常行: {2}",
                                Path.GetFileName(filePath), lineNo, ex.Message));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Errors.Add(string.Format("读取文件 {0} 失败: {1}", filePath, ex.Message));
            }

            return groups;
        }

        /// <summary>
        /// 解析 CSV 中的一行数据。
        /// </summary>
        private static CsvRow ParseLine(string line, int lineNo)
        {
            string[] parts = line.Split(',');

            if (parts.Length < 4)
                throw new FormatException(string.Format("行 {0}: 字段数不足 ({1})", lineNo, parts.Length));

            // 解析 timestamp
            if (!long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp))
                throw new FormatException(string.Format("行 {0}: 无效的 timestamp '{1}'", lineNo, parts[0]));

            string datetime = parts[1].Trim();

            // 解析 phase
            if (!uint.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint phase))
                throw new FormatException(string.Format("行 {0}: 无效的 phase '{1}'", lineNo, parts[2]));

            // 解析采样值 (从索引 3 开始)
            int sampleCount = parts.Length - 3;
            var samples = new float[sampleCount];
            int validCount = 0;

            for (int i = 3; i < parts.Length; i++)
            {
                string val = parts[i].Trim();
                if (string.IsNullOrEmpty(val))
                {
                    samples[i - 3] = 0f; // 空值 → 0
                }
                else if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    samples[i - 3] = f;
                    validCount = (i - 3) + 1;
                }
                else
                {
                    samples[i - 3] = 0f;
                }
            }

            return new CsvRow
            {
                Timestamp = timestamp,
                Datetime = datetime,
                Phase = phase,
                Samples = samples,
            };
        }

        /// <summary>
        /// 根据 phase 值获取相别标签。
        /// </summary>
        public static string GetPhaseLabel(uint phase)
        {
            switch (phase)
            {
                case PHASE_A: return "A";
                case PHASE_B: return "B";
                case PHASE_C: return "C";
                case PHASE_POWER: return "P";
                default:
                    // 对未识别的 phase 值，尝试用高位字节推断
                    uint highByte = (phase >> 24) & 0xFF;
                    if (highByte == 3) return "A";  // offset=0
                    if (highByte == 1) return "B";  // offset=1 → B相
                    if (highByte == 2) return "C";  // offset=2 → C相
                    return "P"; // 默认按功率处理
            }
        }
    }

    /// <summary>
    /// CSV 中解析出的一行原始数据
    /// </summary>
    public class CsvRow
    {
        /// <summary>Unix 时间戳</summary>
        public long Timestamp;

        /// <summary>可读日期时间字符串 (yyyy-MM-dd HH:mm:ss)</summary>
        public string Datetime;

        /// <summary>原始 phase 值</summary>
        public uint Phase;

        /// <summary>采样值数组 (s0..sN)</summary>
        public float[] Samples;
    }
}
