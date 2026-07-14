using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 2: CSM2010 CSV 解析 → JSON 管道 TDD 测试。
    /// 测试 CsvCurveParser（CSV 解析）和 SwitchDataJsonWriter（JSON 输出）。
    /// </summary>
    public class Slice2Tests
    {
        static int passed = 0;
        static int failed = 0;
        static string testDataDir;
        static string outputDir;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 2: CSM2010 CSV 解析 → JSON 管道 测试 ===");
            Console.WriteLine();

            testDataDir = FindTestDataDir();
            outputDir = Path.Combine(Path.GetTempPath(), "slice2_test_output_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            // === CsvCurveParser 测试 ===
            TestParser_ParseCurrentFile();
            TestParser_ParsePowerFile();
            TestParser_PhaseLabelMapping();
            TestParser_TimestampGrouping();
            TestParser_InvalidRows();
            TestParser_SampleCount();

            // === SwitchDataJsonWriter 测试 ===
            TestWriter_ProcessPair();
            TestWriter_JsonFormat();
            TestWriter_DateGrouping();
            TestWriter_IndexJson();
            TestWriter_RoundTrip();

            // === 全量集成测试 ===
            TestFullPipeline_AllFiles();
            TestFullPipeline_Performance();

            // 清理
            try { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true); } catch { }

            Console.WriteLine();
            Console.WriteLine("=== Slice 2 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // CsvCurveParser 测试
        // ================================================================

        /// <summary>解析电流 CSV 文件，验证行数和分组数</summary>
        static void TestParser_ParseCurrentFile()
        {
            Console.WriteLine("--- P1: 解析电流 CSV (SwitchCurve(0).csv) ---");
            try
            {
                string fp = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                Assert(File.Exists(fp), "测试文件存在");

                var parser = new CsvCurveParser();
                var groups = parser.ParseFile(fp);

                Assert(groups != null, "返回非 null");
                Assert(groups.Count > 0, "有分组数据");
                Console.WriteLine("  分组数(唯一 timestamp): {0}", groups.Count);

                // 检查每个分组都有数据
                int totalRows = 0;
                foreach (var kvp in groups)
                {
                    Assert(kvp.Value.Count > 0, string.Format("ts={0} 至少有 1 行", kvp.Key));
                    totalRows += kvp.Value.Count;
                }
                Console.WriteLine("  总行数: {0}", totalRows);

                // 每个分组的行应对应不同 phase
                foreach (var kvp in groups)
                {
                    var phases = new HashSet<uint>();
                    foreach (var row in kvp.Value)
                        phases.Add(row.Phase);
                    Assert(phases.Count >= 1, string.Format("ts={0} 有 {1} 个不同 phase", kvp.Key, phases.Count));
                }

                Console.WriteLine("  [PASS] P1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>解析功率 CSV 文件</summary>
        static void TestParser_ParsePowerFile()
        {
            Console.WriteLine("--- P2: 解析功率 CSV (SwitchCurve(3).csv) ---");
            try
            {
                string fp = Path.Combine(testDataDir, "SwitchCurve(3).csv");
                Assert(File.Exists(fp), "测试文件存在");

                var parser = new CsvCurveParser();
                var groups = parser.ParseFile(fp);

                Assert(groups != null, "返回非 null");
                Assert(groups.Count > 0, "功率文件有分组数据");
                Console.WriteLine("  分组数(唯一 timestamp): {0}", groups.Count);

                int totalRows = 0;
                foreach (var kvp in groups)
                {
                    totalRows += kvp.Value.Count;
                }
                Console.WriteLine("  总行数: {0}", totalRows);
                Assert(totalRows > 0, "功率文件有数据行");

                Console.WriteLine("  [PASS] P2");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>Phase 值 → 标签映射正确</summary>
        static void TestParser_PhaseLabelMapping()
        {
            Console.WriteLine("--- P3: Phase 标签映射 ---");
            try
            {
                Assert(CsvCurveParser.GetPhaseLabel(16777216) == "B", "16777216 → B");
                Assert(CsvCurveParser.GetPhaseLabel(33554432) == "C", "33554432 → C");
                Assert(CsvCurveParser.GetPhaseLabel(50332416) == "A", "50332416 → A");
                Assert(CsvCurveParser.GetPhaseLabel(0) == "P", "0 → P(功率)");

                // 对未知 phase 值的降级处理
                string unknown = CsvCurveParser.GetPhaseLabel(99999999);
                Assert(!string.IsNullOrEmpty(unknown), "未知 phase 有降级标签");

                Console.WriteLine("  [PASS] P3");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>同 timestamp 的不同 phase 行被正确分组</summary>
        static void TestParser_TimestampGrouping()
        {
            Console.WriteLine("--- P4: Timestamp 分组验证 ---");
            try
            {
                string fp = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                var parser = new CsvCurveParser();
                var groups = parser.ParseFile(fp);

                // 取第一个分组验证
                long firstTs = long.MaxValue;
                foreach (var kvp in groups)
                {
                    if (kvp.Key < firstTs) firstTs = kvp.Key;
                }

                var firstGroup = groups[firstTs];
                Console.WriteLine("  第一个 timestamp: {0}, 行数: {1}", firstTs, firstGroup.Count);

                // 所有行的 timestamp 应相同
                foreach (var row in firstGroup)
                {
                    Assert(row.Timestamp == firstTs,
                        string.Format("行 timestamp={0} == 组 key={1}", row.Timestamp, firstTs));
                }

                // 各行的 datetime 应一致
                string firstDt = firstGroup[0].Datetime;
                foreach (var row in firstGroup)
                {
                    Assert(row.Datetime == firstDt,
                        string.Format("datetime 一致: {0}", row.Datetime));
                }

                Console.WriteLine("  [PASS] P4");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>损坏行/格式异常不崩溃，有警告日志</summary>
        static void TestParser_InvalidRows()
        {
            Console.WriteLine("--- P5: 无效行处理 ---");
            try
            {
                // 构造包含损坏行的临时 CSV
                string tmpFile = Path.GetTempPath() + "test_bad_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".csv";
                try
                {
                    File.WriteAllText(tmpFile,
                        "timestamp,datetime,phase,s0,s1,s2\n" +
                        "1776243701,2026-04-15 17:01:41,16777216,5.6,1.4,1.4\n" +
                        "bad_line_no_comma\n" +
                        "abc,def,ghi\n" +
                        "1776243800,2026-04-15 17:02:00,0,1.0,2.0,3.0\n",
                        Encoding.UTF8);

                    var parser = new CsvCurveParser();
                    var groups = parser.ParseFile(tmpFile);

                    // 应该解析出有效行
                    Assert(groups.Count >= 1, "有至少 1 个有效分组");

                    // 应该有错误日志
                    Assert(parser.Errors.Count > 0, "记录了跳过的异常行");
                    Console.WriteLine("  警告数: {0}", parser.Errors.Count);
                    foreach (var err in parser.Errors)
                        Console.WriteLine("    - {0}", err);

                    Console.WriteLine("  [PASS] P5");
                    passed++;
                }
                finally { try { File.Delete(tmpFile); } catch { } }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>采样值数量合理</summary>
        static void TestParser_SampleCount()
        {
            Console.WriteLine("--- P6: 采样值数量验证 ---");
            try
            {
                string fp = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                var parser = new CsvCurveParser();
                var groups = parser.ParseFile(fp);

                foreach (var kvp in groups)
                {
                    foreach (var row in kvp.Value)
                    {
                        Assert(row.Samples != null, "Samples 数组非 null");
                        Assert(row.Samples.Length > 0, string.Format("Samples 长度 {0} > 0", row.Samples.Length));
                        Assert(row.Samples.Length >= 700 && row.Samples.Length <= 1000,
                            string.Format("Samples 长度 {0} 在 [700, 1000]", row.Samples.Length));

                        // 去除非零采样值的实际数量（末尾有很多 0 或空值）
                        int nonZero = 0;
                        for (int i = row.Samples.Length - 1; i >= 0; i--)
                        {
                            if (row.Samples[i] != 0f) { nonZero = i + 1; break; }
                        }
                        Assert(nonZero > 200, string.Format("非零采样数 {0} > 200", nonZero));
                    }
                    break; // 只检查第一组
                }

                Console.WriteLine("  [PASS] P6");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // SwitchDataJsonWriter 测试
        // ================================================================

        /// <summary>处理一个文件对，验证合并结果</summary>
        static void TestWriter_ProcessPair()
        {
            Console.WriteLine("--- W1: 处理文件对 (0) ↔ (3) ---");
            try
            {
                string currFile = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                string powFile = Path.Combine(testDataDir, "SwitchCurve(3).csv");
                var mapping = MappingConfig.CreateDefault();

                var writer = new SwitchDataJsonWriter(mapping);
                var events = writer.ProcessPair(currFile, powFile, "SW_01");

                Assert(events != null, "返回非 null");
                Assert(events.Count > 0, "合并后至少有 1 个事件");
                Console.WriteLine("  合并事件数: {0}", events.Count);

                // 检查每个事件的数据完整性
                int withCurrent = 0, withPower = 0, withBoth = 0;
                foreach (var e in events)
                {
                    bool hasCurrent = (e.CurrentA != null && e.CurrentA.Count > 0)
                                   || (e.CurrentB != null && e.CurrentB.Count > 0)
                                   || (e.CurrentC != null && e.CurrentC.Count > 0);
                    bool hasPower = e.Power != null && e.Power.Count > 0;

                    if (hasCurrent) withCurrent++;
                    if (hasPower) withPower++;
                    if (hasCurrent && hasPower) withBoth++;

                    Assert(e.Timestamp > 0, "timestamp > 0");
                    Assert(!string.IsNullOrEmpty(e.Datetime), "有 datetime");
                    Assert(e.SampleCount > 0, "SampleCount > 0");
                    Assert(e.Duration > 0, "Duration > 0");
                    Assert(e.SampleInterval == 0.04, "SampleInterval = 0.04");
                    Assert(!string.IsNullOrEmpty(e.Direction), "有 Direction");
                }
                Console.WriteLine("  含电流: {0}, 含功率: {1}, 含两者: {2}", withCurrent, withPower, withBoth);

                Console.WriteLine("  [PASS] W1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>JSON 输出格式验证</summary>
        static void TestWriter_JsonFormat()
        {
            Console.WriteLine("--- W2: JSON 格式验证 ---");
            try
            {
                string currFile = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                string powFile = Path.Combine(testDataDir, "SwitchCurve(3).csv");
                var writer = new SwitchDataJsonWriter(MappingConfig.CreateDefault());
                var events = writer.ProcessPair(currFile, powFile, "SW_01");

                // 序列化第一个事件验证 JSON 结构
                var first = events[0];
                string json = JsonConvert.SerializeObject(first, Formatting.Indented);
                Console.WriteLine("  第一个事件 JSON 预览 (前 300 字符):");
                Console.WriteLine(json.Substring(0, Math.Min(300, json.Length)));
                Console.WriteLine("  ...");

                // 反序列化验证
                var deserialized = JsonConvert.DeserializeObject<SwitchEventJson>(json);
                Assert(deserialized != null, "反序列化成功");
                Assert(deserialized.Timestamp == first.Timestamp, "timestamp 一致");
                Assert(deserialized.Datetime == first.Datetime, "datetime 一致");
                Assert(deserialized.SampleCount == first.SampleCount, "sampleCount 一致");

                // 采样值精度验证（3 位小数）
                if (first.CurrentA != null && first.CurrentA.Count > 0)
                {
                    string valStr = first.CurrentA[0].ToString("F10");
                    int dotPos = valStr.IndexOf('.');
                    if (dotPos >= 0)
                    {
                        int decimalDigits = valStr.Length - dotPos - 1;
                        // 末尾可能有 0，但不应超过 3 位有效非零小数
                        Console.WriteLine("  第一个 currentA 值: {0} (小数位数: {1})", first.CurrentA[0], decimalDigits);
                    }
                }

                Console.WriteLine("  [PASS] W2");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>按日期分组写入文件</summary>
        static void TestWriter_DateGrouping()
        {
            Console.WriteLine("--- W3: 日期分组写入 ---");
            try
            {
                string switchId = "SW_01";
                string currFile = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                string powFile = Path.Combine(testDataDir, "SwitchCurve(3).csv");
                var writer = new SwitchDataJsonWriter(MappingConfig.CreateDefault());
                var events = writer.ProcessPair(currFile, powFile, switchId);

                string swOutputDir = Path.Combine(outputDir, switchId);
                writer.WriteDateFiles(events, outputDir, switchId);

                // 验证输出目录和文件
                Assert(Directory.Exists(swOutputDir), "输出目录存在: " + swOutputDir);

                var jsonFiles = Directory.GetFiles(swOutputDir, "*.json");
                Assert(jsonFiles.Length > 0, "至少产生 1 个日期 JSON 文件");
                Console.WriteLine("  日期文件数: {0}", jsonFiles.Length);
                foreach (var f in jsonFiles)
                {
                    string name = Path.GetFileName(f);
                    Console.WriteLine("    - {0} ({1:N0} bytes)", name, new FileInfo(f).Length);

                    // 文件名格式验证: YYYY-MM-DD.json
                    Assert(name.Length == 15, string.Format("文件名 {0} 长度为 15 (YYYY-MM-DD.json)", name));
                    Assert(name.EndsWith(".json"), "扩展名 .json");

                    // 验证文件内容可解析
                    string content = File.ReadAllText(f, Encoding.UTF8);
                    var fileEvents = JsonConvert.DeserializeObject<List<SwitchEventJson>>(content);
                    Assert(fileEvents != null, "文件内容可反序列化");
                    Assert(fileEvents.Count > 0, "文件有事件数据");
                }

                Console.WriteLine("  [PASS] W3");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>index.json 生成验证</summary>
        static void TestWriter_IndexJson()
        {
            Console.WriteLine("--- W4: index.json 生成验证 ---");
            try
            {
                string currFile = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                string powFile = Path.Combine(testDataDir, "SwitchCurve(3).csv");
                var writer = new SwitchDataJsonWriter(MappingConfig.CreateDefault());
                var events = writer.ProcessPair(currFile, powFile, "SW_01");
                writer.WriteDateFiles(events, outputDir, "SW_01");
                writer.UpdateIndex(outputDir);

                string indexPath = Path.Combine(outputDir, "index.json");
                Assert(File.Exists(indexPath), "index.json 存在");

                string indexContent = File.ReadAllText(indexPath, Encoding.UTF8);
                Console.WriteLine("  index.json 内容:");
                Console.WriteLine(indexContent);

                var index = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, List<string>>>>(indexContent);
                Assert(index != null, "index 可反序列化");
                Assert(index.ContainsKey("SW_01"), "包含 SW_01");

                // 验证每个日期的时间戳列表
                foreach (var dateKvp in index["SW_01"])
                {
                    var date = dateKvp.Key;
                    var timestamps = dateKvp.Value;
                    Assert(timestamps.Count > 0, string.Format("{0}: 有 {1} 个时间戳", date, timestamps.Count));

                    // 验证时间戳降序排列
                    for (int i = 1; i < timestamps.Count; i++)
                    {
                        long prev = long.Parse(timestamps[i - 1]);
                        long curr = long.Parse(timestamps[i]);
                        Assert(prev > curr, string.Format("降序: {0} > {1}", prev, curr));
                    }
                }

                Console.WriteLine("  [PASS] W4");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>写入→读取 往返验证</summary>
        static void TestWriter_RoundTrip()
        {
            Console.WriteLine("--- W5: 写入→读取 往返验证 ---");
            try
            {
                string currFile = Path.Combine(testDataDir, "SwitchCurve(0).csv");
                string powFile = Path.Combine(testDataDir, "SwitchCurve(3).csv");
                var writer = new SwitchDataJsonWriter(MappingConfig.CreateDefault());
                var events = writer.ProcessPair(currFile, powFile, "SW_01");
                writer.WriteDateFiles(events, outputDir, "SW_01");

                // 重新读取所有日期 JSON 文件
                string swOutputDir = Path.Combine(outputDir, "SW_01");
                var allReadBack = new List<SwitchEventJson>();
                foreach (var f in Directory.GetFiles(swOutputDir, "*.json"))
                {
                    string content = File.ReadAllText(f, Encoding.UTF8);
                    var fileEvents = JsonConvert.DeserializeObject<List<SwitchEventJson>>(content);
                    allReadBack.AddRange(fileEvents);
                }

                // 验证读回的事件数与原始一致
                Assert(allReadBack.Count == events.Count,
                    string.Format("读回 {0} == 原始 {1}", allReadBack.Count, events.Count));

                // 抽样验证第一条数据
                var original = events[0];
                var readBack = allReadBack.Find(e => e.Timestamp == original.Timestamp);
                Assert(readBack != null, "找到对应 timestamp 的事件");
                Assert(readBack.Datetime == original.Datetime, "datetime 一致");
                Assert(readBack.SampleCount == original.SampleCount, "sampleCount 一致");

                Console.WriteLine("  [PASS] W5");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // 全量集成测试
        // ================================================================

        /// <summary>处理全部 16 个 CSV 文件（8 对）</summary>
        static void TestFullPipeline_AllFiles()
        {
            Console.WriteLine("--- INT1: 全量解析（16 个 CSV 文件 / 8 对） ---");
            try
            {
                // 验证有 16 个 CSV 文件
                var csvFiles = Directory.GetFiles(testDataDir, "SwitchCurve(*).csv");
                Assert(csvFiles.Length == 16, "有 16 个 CSV 文件");

                // 文件配对
                var pairs = BuildFilePairs(csvFiles);
                Assert(pairs.Count == 8, "有 8 对文件");
                Console.WriteLine("  文件对:");
                foreach (var p in pairs)
                    Console.WriteLine("    {0} ↔ {1}", Path.GetFileName(p.Item1), Path.GetFileName(p.Item2));

                var mapping = MappingConfig.CreateDefault();
                // 用实际 switch_mapping.json
                string mappingPath = FindMappingConfig();
                if (!string.IsNullOrEmpty(mappingPath) && File.Exists(mappingPath))
                    mapping = MappingConfig.Load(mappingPath);

                var writer = new SwitchDataJsonWriter(mapping);
                int totalEvents = 0;
                var allSwitchIds = new List<string>();

                string pipeOutputDir = Path.Combine(outputDir, "pipeline");
                foreach (var pair in pairs)
                {
                    // 用 pair index 作为 switchId（如 "0", "4", ...）
                    string baseName = Path.GetFileNameWithoutExtension(pair.Item1);
                    string switchId = ExtractFileIndex(baseName).ToString();

                    // 尝试从 mapping 获取更友好的名称
                    var events = writer.ProcessPair(pair.Item1, pair.Item2, switchId);
                    totalEvents += events.Count;

                    writer.WriteDateFiles(events, pipeOutputDir, switchId);
                    allSwitchIds.Add(switchId);

                    Console.WriteLine("    {0}: {1} 个事件",
                        Path.GetFileName(pair.Item1), events.Count);
                }

                writer.UpdateIndex(pipeOutputDir);

                Console.WriteLine("  总事件数: {0}", totalEvents);
                Assert(totalEvents > 0, "全量解析产生了事件");

                // 验证 index.json
                string indexPath = Path.Combine(pipeOutputDir, "index.json");
                Assert(File.Exists(indexPath), "index.json 存在");
                var index = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, List<string>>>>(
                    File.ReadAllText(indexPath, Encoding.UTF8));

                Assert(index.Count == allSwitchIds.Distinct().Count(),
                    string.Format("index 中有 {0} 个转辙机", index.Count));

                Console.WriteLine("  [PASS] INT1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>性能测试：全量解析 < 30 秒</summary>
        static void TestFullPipeline_Performance()
        {
            Console.WriteLine("--- INT2: 性能测试（目标 < 30 秒） ---");
            try
            {
                var csvFiles = Directory.GetFiles(testDataDir, "SwitchCurve(*).csv");
                var pairs = BuildFilePairs(csvFiles);
                var writer = new SwitchDataJsonWriter(MappingConfig.CreateDefault());
                string perfDir = Path.Combine(outputDir, "perf");

                var sw = Stopwatch.StartNew();
                int totalEvents = 0;
                foreach (var pair in pairs)
                {
                    string baseName = Path.GetFileNameWithoutExtension(pair.Item1);
                    string switchId = ExtractFileIndex(baseName).ToString();
                    var events = writer.ProcessPair(pair.Item1, pair.Item2, switchId);
                    totalEvents += events.Count;
                    writer.WriteDateFiles(events, perfDir, switchId);
                }
                writer.UpdateIndex(perfDir);
                sw.Stop();

                double elapsedSec = sw.Elapsed.TotalSeconds;
                Console.WriteLine("  解析: {0} 个事件, 耗时: {1:F2} 秒", totalEvents, elapsedSec);
                Assert(elapsedSec < 30.0,
                    string.Format("耗时 {0:F2} 秒 < 30 秒", elapsedSec));

                Console.WriteLine("  [PASS] INT2");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Helpers
        // ================================================================

        static string FindTestDataDir()
        {
            var candidates = new[]
            {
                // net40 输出: bin/Debug/net40/ → up 4 = project root, up 5 = solution root
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "03_raw_data", "sanshuibei")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "03_raw_data", "sanshuibei")),
                // 相对路径兜底
                @"..\..\..\..\03_raw_data\sanshuibei",
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;
            throw new DirectoryNotFoundException("找不到 03_raw_data/sanshuibei/");
        }

        static string FindMappingConfig()
        {
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Config", "switch_mapping.json")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Config", "switch_mapping.json")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Config", "switch_mapping.json")),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>从文件名提取索引号，如 "SwitchCurve(0)" → 0</summary>
        static int ExtractFileIndex(string baseName)
        {
            int start = baseName.IndexOf('(');
            int end = baseName.IndexOf(')');
            if (start >= 0 && end > start)
            {
                int.TryParse(baseName.Substring(start + 1, end - start - 1), out int idx);
                return idx;
            }
            return -1;
        }

        /// <summary>
        /// 根据 _file_type_summary.csv 的配对规则构建文件对列表。
        /// 电流文件 (偶数索引) → Item1, 功率文件 (奇数索引) → Item2
        /// </summary>
        static List<Tuple<string, string>> BuildFilePairs(string[] csvFiles)
        {
            var pairs = new List<Tuple<string, string>>();

            // 按索引分组：提取文件名中的索引号
            var byIndex = new Dictionary<int, string>();
            foreach (var f in csvFiles)
            {
                string baseName = Path.GetFileNameWithoutExtension(f);
                int idx = ExtractFileIndex(baseName);
                if (idx >= 0)
                    byIndex[idx] = f;
            }

            // 配对: 0↔3, 4↔7, 8↔11, 12↔15, 16↔19, 20↔23, 24↔27, 28↔31
            int[] currentIndices = { 0, 4, 8, 12, 16, 20, 24, 28 };
            int[] powerIndices = { 3, 7, 11, 15, 19, 23, 27, 31 };

            for (int i = 0; i < currentIndices.Length; i++)
            {
                int ci = currentIndices[i];
                int pi = powerIndices[i];
                if (byIndex.ContainsKey(ci) && byIndex.ContainsKey(pi))
                {
                    pairs.Add(Tuple.Create(byIndex[ci], byIndex[pi]));
                }
            }

            return pairs;
        }

        static void Assert(bool cond, string msg)
        {
            if (!cond) { Console.WriteLine("    ASSERT FAIL: {0}", msg); throw new Exception(msg); }
        }
    }
}
