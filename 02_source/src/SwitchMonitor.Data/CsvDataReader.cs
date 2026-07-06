using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// CSM2010 CSV 一行原始数据
    /// </summary>
    public class CsvRow
    {
        public long Timestamp;
        public string DateTimeStr;
        public int Phase;
        public double[] Samples;
        public int SampleCount;
        /// <summary>相位类型: 0=功率, 1=A相, 2=B相, 3=C相</summary>
        public int PhaseType;
    }

    /// <summary>
    /// CSM2010 CSV 文件读取器
    /// 格式: timestamp,datetime,phase,s0,s1,...,s789
    ///
    /// 相位编码规则（每个转辙机组使用 2 个配对文件 N 和 N+3）：
    /// - 第一个文件 (索引 N): 相位高字节 = N + offset
    ///   offset 0=C相电流, 1=A相电流, 2=B相电流
    /// - 第二个文件 (索引 N+3): 包含功率
    /// </summary>
    internal class CsvDataReader
    {
        /// <summary>
        /// 读取一个 CSV 文件的所有行，并根据文件索引计算相位类型
        /// </summary>
        /// <param name="filePath">CSV 文件路径</param>
        /// <param name="fileIndex">文件索引号 (0-31)</param>
        /// <param name="isSecondFile">是否为配对中的第二个文件（包含C相）</param>
        public List<CsvRow> ReadFile(string filePath, int fileIndex, bool isSecondFile)
        {
            var rows = new List<CsvRow>(1000);

            using (var reader = new StreamReader(filePath, Encoding.UTF8))
            {
                // 跳过标题行
                string header = reader.ReadLine();
                if (header == null)
                    return rows;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    CsvRow row = ParseLine(line, fileIndex, isSecondFile);
                    if (row != null && row.SampleCount > 0)
                        rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// 根据文件索引和原始相位值计算相位类型
        /// </summary>
        private static int ComputePhaseType(int phase, int fileIndex, bool isSecondFile)
        {
            if (isSecondFile)
                return 0; // 第二个文件是功率

            int highByte = (phase >> 24) & 0xFF;
            int offset = highByte - fileIndex;
            // offset 0=C相电流, 1=A相电流, 2=B相电流
            if (offset == 0) return 3; // C相电流
            return offset; // 1=A相, 2=B相
        }

        /// <summary>
        /// 解析一行 CSV 数据
        /// </summary>
        private CsvRow ParseLine(string line, int fileIndex, bool isSecondFile)
        {
            string[] parts = line.Split(',');
            if (parts.Length < 4)
                return null;

            // 列0: Unix 时间戳（秒）
            long timestamp;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp))
                return null;

            // 列1: 可读时间 "yyyy-MM-dd HH:mm:ss"
            string dateTimeStr = parts[1];

            // 列2: 相位值
            int phase;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out phase))
                return null;

            // 列3~792: 采样值 s0..s789（尾部可能为空）
            // 先统计有效采样数（以第一个空字符串为界）
            int sampleStart = 3;
            int sampleEnd = parts.Length;
            for (int i = sampleStart; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                {
                    sampleEnd = i;
                    break;
                }
            }

            int count = sampleEnd - sampleStart;
            if (count == 0)
                return null;

            var samples = new double[count];
            for (int i = 0; i < count; i++)
            {
                double val;
                if (!double.TryParse(parts[sampleStart + i], NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                {
                    // 解析失败则截断到此位置
                    var truncated = new double[i];
                    Array.Copy(samples, truncated, i);
                    samples = truncated;
                    count = i;
                    break;
                }
                samples[i] = val;
            }

            if (count == 0)
                return null;

            return new CsvRow
            {
                Timestamp = timestamp,
                DateTimeStr = dateTimeStr,
                Phase = phase,
                Samples = samples,
                SampleCount = count,
                PhaseType = ComputePhaseType(phase, fileIndex, isSecondFile)
            };
        }

    }
}
