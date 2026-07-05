using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SwitchMonitor.Common;
using SwitchMonitor.UI;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 7: 导出图片 + CSV TDD 测试。
    /// 测试 ExportService 的 CSV 格式生成、文件名生成和 PNG 导出。
    /// CSV 格式要求 (per issue spec):
    ///   Time(s),CurrentA(A),CurrentB(A),CurrentC(A),Power(KW)
    ///   宽格式：每行一个采样点，三相电流 + 功率在同一行。
    /// </summary>
    public class Slice7Tests
    {
        static int passed = 0;
        static int failed = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 7: 导出图片 + CSV 测试 ===");
            Console.WriteLine();

            // TDD 循环测试
            TestCsvHeaderFormat();
            TestCsvDataRowFormat();
            TestCsvMultiPhaseData();
            TestCsvEmptyData();
            TestCsvUtf8Bom();
            TestCsvTimeCalculation();
            TestCsvPowerInKw();
            TestDefaultFileName();
            TestPngExportCreatesFile();
            TestPngExportCorrectDimensions();
            TestExportDisabledWhenNoData();

            Console.WriteLine();
            Console.WriteLine("=== Slice 7 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // TDD Cycle 1: CSV 表头格式（宽格式，中文表头）
        // ================================================================

        /// <summary>
        /// Cycle 1: 生成的 CSV 第一行是正确的中文表头——宽格式。
        /// 表头: Time(s),CurrentA(A),CurrentB(A),CurrentC(A),Power(KW)
        /// </summary>
        static void TestCsvHeaderFormat()
        {
            Console.WriteLine("--- Cycle 1: CSV 表头格式 (宽格式) ---");
            try
            {
                var samples = MakeWideSampleData();
                var action = MakeActionRecord();
                string csv = ExportService.GenerateCsvContent(samples, action);

                Assert(csv != null, "CSV 内容不为 null");
                Assert(csv.Length > 0, "CSV 内容不为空");

                string[] lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                Assert(lines.Length >= 1, "至少包含表头行");

                string header = lines[0];
                Assert(header.Contains("Time(s)"), "表头包含 Time(s)");
                Assert(header.Contains("CurrentA(A)"), "表头包含 CurrentA(A)");
                Assert(header.Contains("CurrentB(A)"), "表头包含 CurrentB(A)");
                Assert(header.Contains("CurrentC(A)"), "表头包含 CurrentC(A)");
                Assert(header.Contains("Power(KW)"), "表头包含 Power(KW)");

                // 不应包含旧格式的字段
                Assert(!header.Contains("SampleIndex"), "表头不含旧字段 SampleIndex");
                Assert(!header.Contains("Timestamp"), "表头不含旧字段 Timestamp");
                Assert(!header.Contains("Phase"), "表头不含旧字段 Phase");
                Assert(!header.Contains("Voltage"), "表头不含 Voltage");

                Console.WriteLine("  表头: {0}", header);
                Console.WriteLine("  [PASS] Cycle 1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 2: CSV 数据行格式（宽格式，3 位小数）
        // ================================================================

        /// <summary>
        /// Cycle 2: 数据行是宽格式，每行一个采样点，包含 A/B/C 三相电流和功率。
        /// </summary>
        static void TestCsvDataRowFormat()
        {
            Console.WriteLine("--- Cycle 2: CSV 数据行格式 (宽格式) ---");
            try
            {
                // 模拟 2 个采样点，每个点有 A/B/C 三相数据
                var samples = new List<CurveSampleRecord>
                {
                    // SampleIndex 0: A/B/C 三相
                    new CurveSampleRecord { SampleIndex = 0, Phase = "A", Current = 5.647f, Power = 3020.0f },
                    new CurveSampleRecord { SampleIndex = 0, Phase = "B", Current = 5.529f, Power = 3020.0f },
                    new CurveSampleRecord { SampleIndex = 0, Phase = "C", Current = 2.078f, Power = 3020.0f },
                    // SampleIndex 1: A/B/C 三相
                    new CurveSampleRecord { SampleIndex = 1, Phase = "A", Current = 1.451f, Power = 294.0f },
                    new CurveSampleRecord { SampleIndex = 1, Phase = "B", Current = 1.451f, Power = 294.0f },
                    new CurveSampleRecord { SampleIndex = 1, Phase = "C", Current = 1.490f, Power = 294.0f },
                };
                var action = MakeActionRecord(); // SampleRate = 25
                string csv = ExportService.GenerateCsvContent(samples, action);

                string[] lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                Assert(lines.Length == 3, string.Format("表头 + 2 数据行 = 3 行 (实际: {0})", lines.Length));

                // 第一行数据: Time(s)=0.000, CurrentA=5.647, CurrentB=5.529, CurrentC=2.078, Power=3.020
                string[] fields0 = lines[1].Split(',');
                Assert(fields0.Length == 5, string.Format("5 个字段 (实际: {0})", fields0.Length));
                Assert(fields0[0] == "0.000", string.Format("Time = 0.000 (actual: {0})", fields0[0]));
                Assert(fields0[1] == "5.647", string.Format("CurrentA = 5.647 (actual: {0})", fields0[1]));
                Assert(fields0[2] == "5.529", string.Format("CurrentB = 5.529 (actual: {0})", fields0[2]));
                Assert(fields0[3] == "2.078", string.Format("CurrentC = 2.078 (actual: {0})", fields0[3]));
                Assert(fields0[4] == "3.020", string.Format("Power = 3.020 KW (actual: {0})", fields0[4]));

                // 第二行数据: Time(s)=0.040, CurrentA=1.451, CurrentB=1.451, CurrentC=1.490, Power=0.294
                string[] fields1 = lines[2].Split(',');
                Assert(fields1[0] == "0.040", string.Format("Time = 0.040 (actual: {0})", fields1[0]));
                Assert(fields1[1] == "1.451", string.Format("CurrentA = 1.451 (actual: {0})", fields1[1]));
                Assert(fields1[4] == "0.294", string.Format("Power = 0.294 KW (actual: {0})", fields1[4]));

                Console.WriteLine("  行1: {0}", lines[1]);
                Console.WriteLine("  行2: {0}", lines[2]);
                Console.WriteLine("  [PASS] Cycle 2");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 3: 多相数据正确分组
        // ================================================================

        /// <summary>
        /// Cycle 3: 三相数据按 SampleIndex 正确分组到同一行。
        /// </summary>
        static void TestCsvMultiPhaseData()
        {
            Console.WriteLine("--- Cycle 3: 多相数据分组 ---");
            try
            {
                var samples = MakeWideMultiPhaseSamples();
                var action = MakeActionRecord();
                string csv = ExportService.GenerateCsvContent(samples, action);

                string[] lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                // 5 个采样点 → 表头 + 5 数据行 = 6
                Assert(lines.Length == 6, string.Format("表头 + 5 数据行 = 6 (实际: {0})", lines.Length));

                // 每个数据行应有 5 个字段
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] fields = lines[i].Split(',');
                    Assert(fields.Length == 5, string.Format("行 {0}: 5 个字段 (实际: {0})", i, fields.Length));

                    // 验证三相电流都存在（非空）
                    Assert(!string.IsNullOrEmpty(fields[1]), "CurrentA 不为空");
                    Assert(!string.IsNullOrEmpty(fields[2]), "CurrentB 不为空");
                    Assert(!string.IsNullOrEmpty(fields[3]), "CurrentC 不为空");
                }

                Console.WriteLine("  总行数: {0} (含表头)", lines.Length);
                Console.WriteLine("  [PASS] Cycle 3");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 4: 空数据处理
        // ================================================================

        /// <summary>
        /// Cycle 4: 无数据时 GenerateCsvContent 返回仅表头的 CSV。
        /// </summary>
        static void TestCsvEmptyData()
        {
            Console.WriteLine("--- Cycle 4: 空数据处理 ---");
            try
            {
                var action = MakeActionRecord();

                // null 列表 → 返回仅表头
                string csvNull = ExportService.GenerateCsvContent(null, action);
                Assert(csvNull != null, "null 列表返回非 null");
                string[] linesNull = csvNull.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                Assert(linesNull.Length == 1, string.Format("null 时仅表头 (实际: {0} 行)", linesNull.Length));
                Assert(linesNull[0].Contains("Time(s)"), "表头正确");

                // 空列表 → 返回仅表头
                string csvEmpty = ExportService.GenerateCsvContent(new List<CurveSampleRecord>(), action);
                Assert(csvEmpty != null, "空列表返回非 null");
                string[] linesEmpty = csvEmpty.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                Assert(linesEmpty.Length == 1, string.Format("空列表时仅表头 (实际: {0} 行)", linesEmpty.Length));

                Console.WriteLine("  [PASS] Cycle 4");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 5: UTF-8 BOM 编码
        // ================================================================

        /// <summary>
        /// Cycle 5: 写入文件的 CSV 使用 UTF-8 with BOM 编码
        /// （确保 Excel 正确识别中文列名）。
        /// </summary>
        static void TestCsvUtf8Bom()
        {
            Console.WriteLine("--- Cycle 5: UTF-8 BOM 编码 ---");
            try
            {
                var samples = MakeWideSampleData();
                var action = MakeActionRecord();

                string tmpPath = Path.Combine(Path.GetTempPath(), "slice7_test_utf8bom.csv");
                try
                {
                    ExportService.ExportCsvToFile(samples, action, tmpPath);

                    byte[] fileBytes = File.ReadAllBytes(tmpPath);
                    Assert(fileBytes.Length >= 3, "文件至少 3 字节");

                    // UTF-8 BOM = 0xEF, 0xBB, 0xBF
                    Assert(fileBytes[0] == 0xEF, string.Format("BOM 第1字节 = 0xEF (actual: 0x{0:X2})", fileBytes[0]));
                    Assert(fileBytes[1] == 0xBB, string.Format("BOM 第2字节 = 0xBB (actual: 0x{0:X2})", fileBytes[1]));
                    Assert(fileBytes[2] == 0xBF, string.Format("BOM 第3字节 = 0xBF (actual: 0x{0:X2})", fileBytes[2]));

                    // 验证新表头可以正确读取
                    string content = File.ReadAllText(tmpPath, Encoding.UTF8);
                    Assert(content.Contains("Time(s)"), "UTF-8 读取后包含 Time(s)");
                    Assert(content.Contains("CurrentA(A)"), "UTF-8 读取后包含 CurrentA(A)");
                    Assert(content.Contains("Power(KW)"), "UTF-8 读取后包含 Power(KW)");

                    Console.WriteLine("  BOM: 0x{0:X2} 0x{1:X2} 0x{2:X2}", fileBytes[0], fileBytes[1], fileBytes[2]);
                    Console.WriteLine("  文件大小: {0} 字节", fileBytes.Length);
                    Console.WriteLine("  [PASS] Cycle 5");
                    passed++;
                }
                finally
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 6: 时间列计算 (SampleIndex / SampleRate)
        // ================================================================

        /// <summary>
        /// Cycle 6: Time(s) = SampleIndex / SampleRate，保留 3 位小数。
        /// </summary>
        static void TestCsvTimeCalculation()
        {
            Console.WriteLine("--- Cycle 6: 时间列计算 ---");
            try
            {
                var samples = new List<CurveSampleRecord>
                {
                    new CurveSampleRecord { SampleIndex = 0, Phase = "A", Current = 1.0f, Power = 220f },
                    new CurveSampleRecord { SampleIndex = 0, Phase = "B", Current = 1.0f, Power = 220f },
                    new CurveSampleRecord { SampleIndex = 0, Phase = "C", Current = 1.0f, Power = 220f },
                    new CurveSampleRecord { SampleIndex = 25, Phase = "A", Current = 2.0f, Power = 440f },
                    new CurveSampleRecord { SampleIndex = 25, Phase = "B", Current = 2.0f, Power = 440f },
                    new CurveSampleRecord { SampleIndex = 25, Phase = "C", Current = 2.0f, Power = 440f },
                    new CurveSampleRecord { SampleIndex = 50, Phase = "A", Current = 3.0f, Power = 660f },
                    new CurveSampleRecord { SampleIndex = 50, Phase = "B", Current = 3.0f, Power = 660f },
                    new CurveSampleRecord { SampleIndex = 50, Phase = "C", Current = 3.0f, Power = 660f },
                };
                var action = MakeActionRecord(); // SampleRate = 25

                string csv = ExportService.GenerateCsvContent(samples, action);
                string[] lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                // 3 个采样点 → 表头 + 3 行
                Assert(lines.Length == 4, string.Format("4 行 (实际: {0})", lines.Length));

                // Index 0: Time = 0/25 = 0.000
                Assert(lines[1].Split(',')[0] == "0.000", "SampleIndex 0 -> Time 0.000");
                // Index 25: Time = 25/25 = 1.000
                Assert(lines[2].Split(',')[0] == "1.000", "SampleIndex 25 -> Time 1.000");
                // Index 50: Time = 50/25 = 2.000
                Assert(lines[3].Split(',')[0] == "2.000", "SampleIndex 50 -> Time 2.000");

                Console.WriteLine("  行1 Time: {0}", lines[1].Split(',')[0]);
                Console.WriteLine("  行2 Time: {0}", lines[2].Split(',')[0]);
                Console.WriteLine("  行3 Time: {0}", lines[3].Split(',')[0]);
                Console.WriteLine("  [PASS] Cycle 6");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 7: 功率列单位为 KW
        // ================================================================

        /// <summary>
        /// Cycle 7: Power 列以 KW 为单位（原始数据为 W，需除以 1000）。
        /// </summary>
        static void TestCsvPowerInKw()
        {
            Console.WriteLine("--- Cycle 7: 功率列 KW 单位 ---");
            try
            {
                var samples = new List<CurveSampleRecord>
                {
                    new CurveSampleRecord { SampleIndex = 0, Phase = "A", Current = 5.0f, Power = 1500.0f },
                    new CurveSampleRecord { SampleIndex = 0, Phase = "B", Current = 5.0f, Power = 1500.0f },
                    new CurveSampleRecord { SampleIndex = 0, Phase = "C", Current = 5.0f, Power = 1500.0f },
                    new CurveSampleRecord { SampleIndex = 1, Phase = "A", Current = 5.0f, Power = 0.0f },
                    new CurveSampleRecord { SampleIndex = 1, Phase = "B", Current = 5.0f, Power = 0.0f },
                    new CurveSampleRecord { SampleIndex = 1, Phase = "C", Current = 5.0f, Power = 0.0f },
                    new CurveSampleRecord { SampleIndex = 2, Phase = "A", Current = 5.0f, Power = 1234.567f },
                    new CurveSampleRecord { SampleIndex = 2, Phase = "B", Current = 5.0f, Power = 1234.567f },
                    new CurveSampleRecord { SampleIndex = 2, Phase = "C", Current = 5.0f, Power = 1234.567f },
                };
                var action = MakeActionRecord();

                string csv = ExportService.GenerateCsvContent(samples, action);
                string[] lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                // 1500W → 1.500 KW
                Assert(lines[1].Split(',')[4] == "1.500", string.Format("1500W → 1.500 KW (actual: {0})", lines[1].Split(',')[4]));
                // 0W → 0.000 KW
                Assert(lines[2].Split(',')[4] == "0.000", string.Format("0W → 0.000 KW (actual: {0})", lines[2].Split(',')[4]));
                // 1234.567W → 1.235 KW (3 位小数四舍五入)
                Assert(lines[3].Split(',')[4] == "1.235", string.Format("1234.567W → 1.235 KW (actual: {0})", lines[3].Split(',')[4]));

                Console.WriteLine("  1500W → {0} KW", lines[1].Split(',')[4]);
                Console.WriteLine("  0W → {0} KW", lines[2].Split(',')[4]);
                Console.WriteLine("  1234.567W → {0} KW", lines[3].Split(',')[4]);
                Console.WriteLine("  [PASS] Cycle 7");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 8: 默认文件名生成
        // ================================================================

        /// <summary>
        /// Cycle 8: 根据动作记录生成正确的默认文件名。
        /// 格式: {道岔ID}_{时间}_{后缀}.{扩展名}
        /// </summary>
        static void TestDefaultFileName()
        {
            Console.WriteLine("--- Cycle 8: 默认文件名生成 ---");
            try
            {
                var action = new SwitchActionRecord
                {
                    Id = 1,
                    SwitchId = "SW_01",
                    StartTime = 1776243701,
                    Direction = "定位→反位",
                    SampleCount = 200,
                };

                string pngName = ExportService.GenerateDefaultFileName(action, "曲线", "png");
                Assert(pngName != null, "PNG 文件名不为 null");
                Assert(pngName.Contains("SW_01"), "文件名包含道岔ID");
                Assert(pngName.EndsWith("_曲线.png"), "文件名以 _曲线.png 结尾");
                Assert(!pngName.Contains(":"), "文件名不含冒号（Windows 非法字符）");
                Assert(pngName.Contains("20260415"), "文件名包含日期 20260415");

                Console.WriteLine("  PNG 默认文件名: {0}", pngName);

                string csvName = ExportService.GenerateDefaultFileName(action, "数据", "csv");
                Assert(csvName.EndsWith("_数据.csv"), "CSV 文件名以 _数据.csv 结尾");

                Console.WriteLine("  CSV 默认文件名: {0}", csvName);
                Console.WriteLine("  [PASS] Cycle 8");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 9: PNG 导出创建文件
        // ================================================================

        static void TestPngExportCreatesFile()
        {
            Console.WriteLine("--- Cycle 9: PNG 导出创建文件 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    panel.Size = new System.Drawing.Size(800, 500);
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, data, data);
                    var h = panel.Handle;

                    string tmpPath = Path.Combine(Path.GetTempPath(), "slice7_test_export.png");
                    try
                    {
                        var action = MakeActionRecord();
                        ExportService.ExportChartToPng(panel, action, tmpPath);

                        Assert(File.Exists(tmpPath), "PNG 文件已创建");
                        var fileInfo = new FileInfo(tmpPath);
                        Assert(fileInfo.Length > 0, string.Format("文件大小 > 0 (actual: {0} 字节)", fileInfo.Length));
                        Assert(fileInfo.Length >= 1000, string.Format("文件大小合理 >= 1KB (actual: {0})", fileInfo.Length));

                        Console.WriteLine("  PNG 文件大小: {0} 字节 ({1:F1} KB)", fileInfo.Length, fileInfo.Length / 1024.0);
                        Console.WriteLine("  [PASS] Cycle 9");
                        passed++;
                    }
                    finally
                    {
                        if (File.Exists(tmpPath)) File.Delete(tmpPath);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 10: PNG 导出尺寸正确
        // ================================================================

        static void TestPngExportCorrectDimensions()
        {
            Console.WriteLine("--- Cycle 10: PNG 导出尺寸正确 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    panel.Size = new System.Drawing.Size(800, 500);
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    var h = panel.Handle;

                    string tmpPath = Path.Combine(Path.GetTempPath(), "slice7_test_dims.png");
                    try
                    {
                        var action = MakeActionRecord();
                        ExportService.ExportChartToPng(panel, action, tmpPath);

                        using (var img = System.Drawing.Image.FromFile(tmpPath))
                        {
                            Assert(img.Width == 800, string.Format("宽度 = 800 (actual: {0})", img.Width));
                            Assert(img.Height == 500, string.Format("高度 = 500 (actual: {0})", img.Height));
                        }

                        Console.WriteLine("  导出尺寸: 800x500");
                        Console.WriteLine("  [PASS] Cycle 10");
                        passed++;
                    }
                    finally
                    {
                        if (File.Exists(tmpPath)) File.Delete(tmpPath);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 11: 无数据时导出按钮应灰显
        // ================================================================

        static void TestExportDisabledWhenNoData()
        {
            Console.WriteLine("--- Cycle 11: 无数据时导出禁用 ---");
            try
            {
                Assert(!ExportService.HasExportableData(null), "null 列表 → 不可导出");
                Assert(!ExportService.HasExportableData(new List<CurveSampleRecord>()), "空列表 → 不可导出");
                Assert(ExportService.HasExportableData(MakeWideSampleData()), "有数据 → 可导出");

                Console.WriteLine("  [PASS] Cycle 11");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Helpers
        // ================================================================

        static SwitchActionRecord MakeActionRecord()
        {
            return new SwitchActionRecord
            {
                Id = 1,
                SwitchId = "SW_01",
                StartTime = 1776243701,
                Direction = "定位→反位",
                SampleCount = 3,
                PhaseCount = 3,
                SampleRate = 25,
            };
        }

        /// <summary>生成宽格式测试数据：3 个采样点 × 3 相 = 9 条记录</summary>
        static List<CurveSampleRecord> MakeWideSampleData()
        {
            return new List<CurveSampleRecord>
            {
                new CurveSampleRecord { SampleIndex = 0, Phase = "A", Current = 5.647f, Power = 3020.0f },
                new CurveSampleRecord { SampleIndex = 0, Phase = "B", Current = 5.529f, Power = 3020.0f },
                new CurveSampleRecord { SampleIndex = 0, Phase = "C", Current = 2.078f, Power = 3020.0f },
                new CurveSampleRecord { SampleIndex = 1, Phase = "A", Current = 1.451f, Power = 294.0f },
                new CurveSampleRecord { SampleIndex = 1, Phase = "B", Current = 1.451f, Power = 294.0f },
                new CurveSampleRecord { SampleIndex = 1, Phase = "C", Current = 1.490f, Power = 294.0f },
                new CurveSampleRecord { SampleIndex = 2, Phase = "A", Current = 2.103f, Power = 461.7f },
                new CurveSampleRecord { SampleIndex = 2, Phase = "B", Current = 2.103f, Power = 461.7f },
                new CurveSampleRecord { SampleIndex = 2, Phase = "C", Current = 2.103f, Power = 461.7f },
            };
        }

        /// <summary>生成 5 个采样点的多相测试数据</summary>
        static List<CurveSampleRecord> MakeWideMultiPhaseSamples()
        {
            var list = new List<CurveSampleRecord>();
            foreach (var phase in new[] { "A", "B", "C" })
            {
                for (int i = 0; i < 5; i++)
                {
                    list.Add(new CurveSampleRecord
                    {
                        SampleIndex = i,
                        Phase = phase,
                        Current = 1.0f + i * 0.5f + (phase[0] - 'A') * 0.3f,
                        Power = 220f * (1.0f + i * 0.5f)
                    });
                }
            }
            return list;
        }

        static List<float> MakeLinearData(int count, float startVal, float endVal)
        {
            var list = new List<float>(count);
            for (int i = 0; i < count; i++)
            {
                float t = (float)i / (count - 1);
                list.Add(startVal + t * (endVal - startVal));
            }
            return list;
        }

        static void Assert(bool cond, string msg)
        {
            if (!cond) { Console.WriteLine("    ASSERT FAIL: {0}", msg); throw new Exception(msg); }
        }
    }
}
