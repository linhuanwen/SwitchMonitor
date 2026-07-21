using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SwitchMonitor.Station
{
    /// <summary>
    /// CSM2010 CSV 一行原始数据（与 SwitchMonitor.Data.CsvRow 平行，供 Station 模块独立使用）
    /// </summary>
    public class StationCsvRow
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
    /// 统一曲线文件读取器 — 读取 SwitchCurve(N).csv。
    ///
    /// CSV 格式（与三水北一致）：
    ///   timestamp,datetime,phase,s0,s1,...,s789
    ///
    /// 相位编码：
    ///   - 电流文件 (dataFileIndex N): phase 高位字节 = N + offset
    ///     offset 0=A相电流, 1=B相电流, 2=C相电流
    ///   - 功率文件 (dataFileIndex N+3): 始终为功率
    /// </summary>
    public static class CurveFileReader
    {
        /// <summary>
        /// 读取单个 SwitchCurve(N).csv 文件
        /// </summary>
        /// <param name="filePath">CSV 文件绝对路径</param>
        /// <param name="fileIndex">文件索引号 N</param>
        /// <param name="isPowerFile">是否功率文件（true → PhaseType 强制为 0）</param>
        /// <returns>行数据列表</returns>
        public static List<StationCsvRow> ReadFile(string filePath, int fileIndex, bool isPowerFile)
        {
            var rows = new List<StationCsvRow>(1000);

            if (!File.Exists(filePath))
                return rows;

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

                    var row = ParseLine(line, fileIndex, isPowerFile);
                    if (row != null && row.SampleCount > 0)
                        rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// 读取一个转辙机组的全部数据（电流文件 N + 功率文件 N+3）
        /// </summary>
        /// <param name="dataDir">站点 CSV 数据目录</param>
        /// <param name="group">转辙机组定义</param>
        /// <returns>合并的行数据列表</returns>
        public static List<StationCsvRow> ReadSwitchGroup(string dataDir, SwitchGroupDef group)
        {
            int idxCurrent = group.DataFileIndex;
            int idxPower = group.PowerFileIndex;

            var allRows = new List<StationCsvRow>(2000);

            // 电流文件 (A/B/C 三相)
            string currentFile = Path.Combine(dataDir, string.Format("SwitchCurve({0}).csv", idxCurrent));
            if (File.Exists(currentFile))
            {
                var rows = ReadFile(currentFile, idxCurrent, false);
                allRows.AddRange(rows);
            }

            // 功率文件
            string powerFile = Path.Combine(dataDir, string.Format("SwitchCurve({0}).csv", idxPower));
            if (File.Exists(powerFile))
            {
                var rows = ReadFile(powerFile, idxPower, true);
                allRows.AddRange(rows);
            }

            return allRows;
        }

        /// <summary>
        /// 根据文件索引和原始相位值计算相位类型
        /// </summary>
        private static int ComputePhaseType(int phase, int fileIndex, bool isPowerFile)
        {
            if (isPowerFile)
                return 0; // 功率

            int highByte = (phase >> 24) & 0xFF;
            int offset = highByte - fileIndex;

            // offset 0=A相, 1=B相, 2=C相
            if (offset >= 0 && offset <= 2)
                return offset + 1; // 0→1(A), 1→2(B), 2→3(C)

            return offset; // 异常值原样返回
        }

        /// <summary>
        /// 解析一行 CSV
        /// </summary>
        private static StationCsvRow ParseLine(string line, int fileIndex, bool isPowerFile)
        {
            string[] parts = line.Split(',');
            if (parts.Length < 4)
                return null;

            // 列0: Unix 时间戳
            long timestamp;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out timestamp))
                return null;

            // 列1: 可读时间
            string dateTimeStr = parts[1];

            // 列2: 相位值
            int phase;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out phase))
                return null;

            // 列3+: 采样值 s0..sN（以第一个空字符串为界）
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
                    // 解析失败则截断
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

            return new StationCsvRow
            {
                Timestamp = timestamp,
                DateTimeStr = dateTimeStr,
                Phase = phase,
                Samples = samples,
                SampleCount = count,
                PhaseType = ComputePhaseType(phase, fileIndex, isPowerFile)
            };
        }
    }
}
