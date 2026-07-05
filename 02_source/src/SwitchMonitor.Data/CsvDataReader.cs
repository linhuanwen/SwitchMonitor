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
    }

    /// <summary>
    /// CSM2010 CSV 文件读取器
    /// 格式: timestamp,datetime,phase,s0,s1,...,s789
    /// 相位值: 0=功率, 16777216=A相电流, 33554432=B相电流, 50331648/50332416=C相电流
    /// </summary>
    internal class CsvDataReader
    {
        // 相位常量
        public const int PHASE_POWER = 0;
        public const int PHASE_A = 16777216;        // 0x01000000 A相电流
        public const int PHASE_B = 33554432;        // 0x02000000 B相电流
        public const int PHASE_C_IDEAL = 50331648;  // 0x03000000 C相电流（A+B）
        public const int PHASE_C_ACTUAL = 50332416; // 实际数据中出现的C相值

        /// <summary>
        /// 读取一个 CSV 文件的所有行
        /// </summary>
        public List<CsvRow> ReadFile(string filePath)
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

                    CsvRow row = ParseLine(line);
                    if (row != null && row.SampleCount > 0)
                        rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// 解析一行 CSV 数据
        /// </summary>
        private CsvRow ParseLine(string line)
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
                SampleCount = count
            };
        }

        /// <summary>
        /// 判断相位是否为 A相电流
        /// </summary>
        public static bool IsPhaseA(int phase)
        {
            return phase == PHASE_A;
        }

        /// <summary>
        /// 判断相位是否为 B相电流
        /// </summary>
        public static bool IsPhaseB(int phase)
        {
            return phase == PHASE_B;
        }

        /// <summary>
        /// 判断相位是否为 C相电流（含实际值 50332416）
        /// </summary>
        public static bool IsPhaseC(int phase)
        {
            return phase == PHASE_C_IDEAL || phase == PHASE_C_ACTUAL;
        }

        /// <summary>
        /// 判断相位是否为功率
        /// </summary>
        public static bool IsPhasePower(int phase)
        {
            return phase == PHASE_POWER;
        }
    }
}
