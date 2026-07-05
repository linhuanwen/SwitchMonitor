using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;
using SwitchMonitor.Common;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 导出服务：将图表导出为 PNG 图片，将曲线数据导出为 CSV 文件。
    /// </summary>
    public static class ExportService
    {
        /// <summary>
        /// 生成 CSV 内容字符串（不含 BOM，用于测试和预览）。
        /// 输出宽格式：每行一个采样点，列 = Time(s),CurrentA(A),CurrentB(A),CurrentC(A),Power(KW)。
        /// Time = SampleIndex / SampleRate，Power 从 W 转换为 KW。
        /// </summary>
        /// <param name="samples">曲线采样数据（长格式，含 A/B/C 三相），为 null 或空时仅返回表头</param>
        /// <param name="action">当前动作记录（提供 SampleRate 用于时间计算）</param>
        /// <returns>CSV 格式的完整字符串</returns>
        public static string GenerateCsvContent(List<CurveSampleRecord> samples, SwitchActionRecord action)
        {
            var sb = new StringBuilder();

            // 表头（宽格式，按 issue 规格）
            sb.AppendLine("Time(s),CurrentA(A),CurrentB(A),CurrentC(A),Power(KW)");

            if (samples != null && samples.Count > 0)
            {
                // 按 SampleIndex 分组，构建宽格式行
                var grouped = GroupSamplesByIndex(samples);
                int sampleRate = (action != null && action.SampleRate > 0) ? action.SampleRate : 25;

                foreach (var kvp in grouped)
                {
                    int sampleIndex = kvp.Key;
                    var row = kvp.Value;

                    // Time(s) = SampleIndex / SampleRate
                    float timeSeconds = (float)sampleIndex / sampleRate;

                    // 提取 A/B/C 相电流（缺失相填 0）
                    float currentA = row.ContainsKey("A") ? row["A"] : 0f;
                    float currentB = row.ContainsKey("B") ? row["B"] : 0f;
                    float currentC = row.ContainsKey("C") ? row["C"] : 0f;

                    // 功率取任意相的值（三相功率相同），转换为 KW
                    float powerW = 0f;
                    foreach (var phase in new[] { "A", "B", "C" })
                    {
                        if (row.ContainsKey(phase + "_P"))
                        {
                            powerW = row[phase + "_P"];
                            break;
                        }
                    }
                    float powerKW = powerW / 1000f;

                    sb.AppendFormat("{0:F3},{1:F3},{2:F3},{3:F3},{4:F3}",
                        timeSeconds, currentA, currentB, currentC, powerKW);
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将长格式采样列表按 SampleIndex 分组，返回字典：
        /// Key = SampleIndex, Value = { "A": currentA, "B": currentB, "C": currentC, "A_P": power, ... }
        /// </summary>
        private static SortedDictionary<int, Dictionary<string, float>> GroupSamplesByIndex(
            List<CurveSampleRecord> samples)
        {
            var grouped = new SortedDictionary<int, Dictionary<string, float>>();

            foreach (var s in samples)
            {
                if (!grouped.ContainsKey(s.SampleIndex))
                {
                    grouped[s.SampleIndex] = new Dictionary<string, float>();
                }

                var row = grouped[s.SampleIndex];

                // 电流值按相别存储
                if (!string.IsNullOrEmpty(s.Phase) && s.Phase.Length == 1)
                {
                    row[s.Phase] = s.Current;
                    // 功率值（取第一次出现的值，各相相同）
                    string powerKey = s.Phase + "_P";
                    if (!row.ContainsKey(powerKey))
                    {
                        row[powerKey] = s.Power;
                    }
                }
            }

            return grouped;
        }

        /// <summary>
        /// 将 CSV 内容写入文件（UTF-8 with BOM 编码，确保 Excel 正确识别中文）。
        /// </summary>
        /// <param name="samples">曲线采样数据</param>
        /// <param name="action">当前动作记录</param>
        /// <param name="filePath">目标文件路径</param>
        public static void ExportCsvToFile(List<CurveSampleRecord> samples, SwitchActionRecord action, string filePath)
        {
            string content = GenerateCsvContent(samples, action);

            // UTF-8 with BOM: 先写 BOM，再写 UTF-8 内容
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.Write(content);
            }
        }

        /// <summary>
        /// 将图表 Panel 绘制到 Bitmap 并保存为 PNG 文件。
        /// 使用 Control.DrawToBitmap() 方法。
        /// </summary>
        /// <param name="chartPanel">图表面板控件</param>
        /// <param name="action">当前动作记录（用于生成默认文件名）</param>
        /// <param name="filePath">目标文件路径</param>
        public static void ExportChartToPng(Control chartPanel, SwitchActionRecord action, string filePath)
        {
            if (chartPanel == null)
                throw new ArgumentNullException(nameof(chartPanel));

            using (var bitmap = new Bitmap(chartPanel.Width, chartPanel.Height))
            {
                chartPanel.DrawToBitmap(bitmap, new Rectangle(0, 0, chartPanel.Width, chartPanel.Height));
                bitmap.Save(filePath, ImageFormat.Png);
            }
        }

        /// <summary>
        /// 生成默认保存文件名。
        /// 格式: {道岔显示名}_{时间}_{后缀}.{扩展名}
        /// 如: 1#道岔_20260415_170141_曲线.png
        /// </summary>
        /// <param name="action">动作记录</param>
        /// <param name="suffix">后缀标识（如 "曲线"、"数据"）</param>
        /// <param name="extension">文件扩展名（不含点，如 "png"、"csv"）</param>
        /// <param name="switchDisplayName">道岔显示名（可选，为 null 时使用 SwitchId）</param>
        /// <returns>不含路径的默认文件名</returns>
        public static string GenerateDefaultFileName(SwitchActionRecord action, string suffix, string extension,
            string switchDisplayName = null)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            string switchPart;
            if (!string.IsNullOrEmpty(switchDisplayName))
            {
                switchPart = switchDisplayName;
            }
            else
            {
                switchPart = string.IsNullOrEmpty(action.SwitchId) ? "Unknown" : action.SwitchId;
            }

            // 清理文件名中不合法的字符
            switchPart = switchPart.Replace("\\", "_").Replace("/", "_")
                                   .Replace(":", "_").Replace("*", "_")
                                   .Replace("?", "_").Replace("\"", "_")
                                   .Replace("<", "_").Replace(">", "_")
                                   .Replace("|", "_");

            // 时间戳转本地时间，格式 yyyyMMdd_HHmmss（不含冒号，Windows 合法）
            string timeStr;
            try
            {
                var dt = DateTimeHelper.FromUnixTimestamp(action.StartTime);
                timeStr = dt.ToString("yyyyMMdd_HHmmss");
            }
            catch
            {
                timeStr = action.StartTime.ToString();
            }

            return string.Format("{0}_{1}_{2}.{3}", switchPart, timeStr, suffix, extension);
        }

        /// <summary>
        /// 判断是否有可导出的数据。
        /// </summary>
        /// <param name="samples">曲线采样数据列表</param>
        /// <returns>有数据可导出时返回 true</returns>
        public static bool HasExportableData(List<CurveSampleRecord> samples)
        {
            return samples != null && samples.Count > 0;
        }
    }
}
