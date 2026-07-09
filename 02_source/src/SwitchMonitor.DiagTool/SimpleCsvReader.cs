using System;
using System.Collections.Generic;
using System.IO;

namespace SwitchMonitor.DiagTool
{
    /// <summary>
    /// 独立简易 CSV 读取器，供 selftest 使用。
    /// 不依赖 Data 项目的 CsvDataReader（语义不同，那包含相位配对逻辑）。
    /// 每行格式：timestamp,datetime,phase,s0,s1,... 尾部空列截断。
    /// </summary>
    internal class SimpleCsvReader
    {
        /// <summary>
        /// 读取功率 CSV 文件的所有行。
        /// 返回列表，每项为 (timestamp: int, datetime: string, values: List&lt;double&gt;)。
        /// </summary>
        public static List<CsvRow> ReadPowerCsv(string filePath)
        {
            var rows = new List<CsvRow>();
            using (var reader = new StreamReader(filePath, System.Text.Encoding.UTF8))
            {
                // 跳过表头
                string header = reader.ReadLine();
                if (header == null) return rows;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var row = ParseLine(line);
                    if (row != null)
                    {
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        private static CsvRow ParseLine(string line)
        {
            var parts = SplitCsv(line);
            if (parts.Length < 4) return null;

            int timestamp;
            if (!int.TryParse(parts[0], out timestamp)) return null;

            var values = new List<double>();
            for (int i = 3; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) break;
                double v;
                if (double.TryParse(parts[i],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out v))
                {
                    values.Add(v);
                }
                else
                {
                    break;
                }
            }

            return new CsvRow
            {
                Timestamp = timestamp,
                DateTimeStr = parts[1],
                Values = values
            };
        }

        private static string[] SplitCsv(string line)
        {
            // 简单逗号分割（功率 CSV 不含引号包裹的字段）
            return line.Split(',');
        }
    }

    internal class CsvRow
    {
        public int Timestamp;
        public string DateTimeStr;
        public List<double> Values;
    }
}
