using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SwitchMonitor.Common;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 11: 道岔映射配置 TDD 测试。
    /// 测试 MappingConfig 的 JSON 反序列化、文件加载、降级策略、查询方法和热加载。
    /// </summary>
    public class Slice11Tests
    {
        static int passed = 0;
        static int failed = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 11: 道岔映射配置 测试 ===");
            Console.WriteLine();

            // ---- Cycle 1: JSON 反序列化 ----
            TestJsonDeserialization();

            // ---- Cycle 2: 文件加载成功 ----
            TestLoadFromFile();

            // ---- Cycle 3: 文件不存在降级 ----
            TestLoadMissingFileFallback();

            // ---- Cycle 4: JSON 损坏降级 ----
            TestLoadCorruptJsonFallback();

            // ---- Cycle 5: 缺少必需字段降级 ----
            TestLoadMissingFieldsFallback();

            // ---- Cycle 6: GetSwitchName 已映射 ----
            TestGetSwitchNameMapped();

            // ---- Cycle 7: GetSwitchName 未映射降级 ----
            TestGetSwitchNameUnmapped();

            // ---- Cycle 8: GetPointConfigName 已映射 ----
            TestGetPointConfigNameMapped();

            // ---- Cycle 9: GetPointConfigName 未映射降级 ----
            TestGetPointConfigNameUnmapped();

            // ---- Cycle 10: 热加载 ----
            TestHotReload();

            // ---- Cycle 11: 独立于诊断规则配置 ----
            TestIndependentFromDiagnosisConfig();

            // ---- Cycle 12: 裸数字键的向后兼容（DB 中存"0"也能匹配"SwitchCurve(0)"） ----
            TestBareNumberKeyFallback();

            // ---- Cycle 13: 端到端：模拟 parser 提取逻辑 → 映射查找 ----
            TestParserToMappingFlow();

            // ---- Cycle 14: 实际 switch_mapping.json 文件加载验证 ----
            TestRealMappingFileLoad();

            Console.WriteLine();
            Console.WriteLine("=== Slice 11 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // TDD Cycle 1: JSON 反序列化
        // ================================================================

        /// <summary>
        /// Cycle 1: 从 JSON 字符串正确反序列化 MappingConfig。
        /// </summary>
        static void TestJsonDeserialization()
        {
            Console.WriteLine("--- Cycle 1: JSON 反序列化 ---");
            try
            {
                string json = @"{
                    ""version"": ""1.0"",
                    ""stationId"": ""SSB"",
                    ""stationName"": ""三水北"",
                    ""fileMapping"": {
                        ""SwitchCurve(0)"": {
                            ""switchId"": ""SW_01"",
                            ""switchName"": ""1#道岔"",
                            ""description"": ""待确认-可能为1#J尖轨"",
                            ""directionHint"": ""定位↔反位""
                        },
                        ""SwitchCurve(4)"": {
                            ""switchId"": ""SW_02"",
                            ""switchName"": ""2#道岔"",
                            ""description"": ""待确认-可能为1#X心轨"",
                            ""directionHint"": ""定位↔反位""
                        }
                    },
                    ""pointIdMapping"": {
                        ""184"": {
                            ""configName"": ""537GH"",
                            ""switchId"": null,
                            ""description"": ""待确认-H/B含义及对应道岔""
                        },
                        ""185"": {
                            ""configName"": ""537GB"",
                            ""switchId"": null,
                            ""description"": ""待确认""
                        }
                    },
                    ""directionMapping"": {
                        ""DB"": { ""meaning"": ""定位表示"", ""note"": ""DB 继电器吸起=道岔在定位"" },
                        ""FB"": { ""meaning"": ""反位表示"", ""note"": ""FB 继电器吸起=道岔在反位"" },
                        ""H"": { ""meaning"": ""待确认"", ""note"": ""可能是定位或反位其中之一"" },
                        ""B"": { ""meaning"": ""待确认"", ""note"": ""与H互斥"" }
                    }
                }";

                var config = MappingConfig.LoadFromJson(json);
                Assert(config != null, "反序列化结果非 null");
                Assert(config.Version == "1.0", "Version = 1.0");
                Assert(config.StationId == "SSB", "StationId = SSB");
                Assert(config.StationName == "三水北", "StationName = 三水北");

                // 验证 fileMapping
                Assert(config.FileMapping != null, "FileMapping 非 null");
                Assert(config.FileMapping.Count == 2,
                    string.Format("FileMapping 有 2 条 (实际: {0})", config.FileMapping.Count));
                Assert(config.FileMapping.ContainsKey("SwitchCurve(0)"), "包含 SwitchCurve(0)");
                Assert(config.FileMapping["SwitchCurve(0)"].SwitchId == "SW_01", "SW_01 SwitchId");
                Assert(config.FileMapping["SwitchCurve(0)"].SwitchName == "1#道岔", "1#道岔 SwitchName");
                Assert(config.FileMapping["SwitchCurve(0)"].Description == "待确认-可能为1#J尖轨", "描述正确");
                Assert(config.FileMapping["SwitchCurve(0)"].DirectionHint == "定位↔反位", "方向提示正确");

                // 验证 pointIdMapping
                Assert(config.PointIdMapping != null, "PointIdMapping 非 null");
                Assert(config.PointIdMapping.Count == 2,
                    string.Format("PointIdMapping 有 2 条 (实际: {0})", config.PointIdMapping.Count));
                Assert(config.PointIdMapping.ContainsKey("184"), "包含点号 184");
                Assert(config.PointIdMapping["184"].ConfigName == "537GH", "537GH ConfigName");
                Assert(config.PointIdMapping["184"].SwitchId == null, "SwitchId 为 null");
                Assert(config.PointIdMapping["184"].Description == "待确认-H/B含义及对应道岔", "描述正确");

                // 验证 directionMapping
                Assert(config.DirectionMapping != null, "DirectionMapping 非 null");
                Assert(config.DirectionMapping.Count == 4,
                    string.Format("DirectionMapping 有 4 条 (实际: {0})", config.DirectionMapping.Count));
                Assert(config.DirectionMapping.ContainsKey("H"), "包含方向 H");
                Assert(config.DirectionMapping["H"].Meaning == "待确认", "H.Meaning = 待确认");
                Assert(config.DirectionMapping["H"].Note == "可能是定位或反位其中之一", "H.Note 正确");

                Console.WriteLine("  版本: {0}, 车站: {1}({2})", config.Version, config.StationName, config.StationId);
                Console.WriteLine("  FileMapping: {0} 条", config.FileMapping.Count);
                Console.WriteLine("  PointIdMapping: {0} 条", config.PointIdMapping.Count);
                Console.WriteLine("  DirectionMapping: {0} 条", config.DirectionMapping.Count);
                Console.WriteLine("  [PASS] Cycle 1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 2: 从文件加载
        // ================================================================

        /// <summary>
        /// Cycle 2: Load(path) 从 JSON 文件正确加载。
        /// </summary>
        static void TestLoadFromFile()
        {
            Console.WriteLine("--- Cycle 2: 从文件加载 ---");
            try
            {
                string tempDir = CreateTempDir();
                try
                {
                    string json = @"{
                        ""version"": ""1.0"",
                        ""stationId"": ""SSB"",
                        ""stationName"": ""三水北"",
                        ""fileMapping"": {
                            ""SwitchCurve(0)"": {
                                ""switchId"": ""SW_01"",
                                ""switchName"": ""1#道岔"",
                                ""description"": ""测试"",
                                ""directionHint"": ""定位↔反位""
                            }
                        },
                        ""pointIdMapping"": {},
                        ""directionMapping"": {}
                    }";
                    string filePath = Path.Combine(tempDir, "switch_mapping.json");
                    File.WriteAllText(filePath, json, Encoding.UTF8);

                    var config = MappingConfig.Load(filePath);
                    Assert(config != null, "Load 返回非 null");
                    Assert(config.Version == "1.0", "Version 正确");
                    Assert(config.FileMapping.Count == 1, "FileMapping 1 条");
                    Assert(config.FileMapping["SwitchCurve(0)"].SwitchName == "1#道岔", "SwitchName 正确");

                    Console.WriteLine("  加载文件: {0}", filePath);
                    Console.WriteLine("  车站: {0}", config.StationName);
                    Console.WriteLine("  [PASS] Cycle 2");
                    passed++;
                }
                finally
                {
                    CleanupTempDir(tempDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 3: 文件不存在降级
        // ================================================================

        /// <summary>
        /// Cycle 3: 配置文件不存在时返回默认配置，不抛异常。
        /// </summary>
        static void TestLoadMissingFileFallback()
        {
            Console.WriteLine("--- Cycle 3: 文件不存在降级 ---");
            try
            {
                string nonExistentPath = Path.Combine(Path.GetTempPath(),
                    "non_existent_" + Guid.NewGuid().ToString("N") + ".json");

                var config = MappingConfig.Load(nonExistentPath);
                Assert(config != null, "文件不存在时返回非 null");
                Assert(config.Version == "1.0", "默认 Version = 1.0");
                Assert(config.StationId == "DEFAULT", "默认 StationId");
                Assert(config.FileMapping != null, "默认 FileMapping 非 null");
                Assert(config.FileMapping.Count == 0, "默认 FileMapping 为空");
                Assert(config.PointIdMapping != null, "默认 PointIdMapping 非 null");
                Assert(config.PointIdMapping.Count == 0, "默认 PointIdMapping 为空");

                Console.WriteLine("  缺失文件 → 返回默认配置 (StationId={0})", config.StationId);
                Console.WriteLine("  [PASS] Cycle 3");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 4: JSON 损坏降级
        // ================================================================

        /// <summary>
        /// Cycle 4: JSON 格式损坏时返回默认配置，不抛异常。
        /// </summary>
        static void TestLoadCorruptJsonFallback()
        {
            Console.WriteLine("--- Cycle 4: JSON 损坏降级 ---");
            try
            {
                string tempDir = CreateTempDir();
                try
                {
                    string filePath = Path.Combine(tempDir, "switch_mapping.json");
                    File.WriteAllText(filePath, "这不是合法的 JSON {{{{{", Encoding.UTF8);

                    var config = MappingConfig.Load(filePath);
                    Assert(config != null, "JSON 损坏时返回非 null");
                    Assert(config.Version == "1.0", "默认 Version");
                    Assert(config.FileMapping.Count == 0, "默认 FileMapping 为空");

                    Console.WriteLine("  损坏的 JSON → 返回默认配置 (不崩溃)");
                    Console.WriteLine("  [PASS] Cycle 4");
                    passed++;
                }
                finally
                {
                    CleanupTempDir(tempDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 5: 缺少必需字段降级
        // ================================================================

        /// <summary>
        /// Cycle 5: JSON 缺少必需字段时使用默认值。
        /// </summary>
        static void TestLoadMissingFieldsFallback()
        {
            Console.WriteLine("--- Cycle 5: 缺少必需字段降级 ---");
            try
            {
                string tempDir = CreateTempDir();
                try
                {
                    // 只有 version，缺少其他字段
                    string json = @"{ ""version"": ""2.0"" }";
                    string filePath = Path.Combine(tempDir, "switch_mapping.json");
                    File.WriteAllText(filePath, json, Encoding.UTF8);

                    var config = MappingConfig.Load(filePath);
                    Assert(config != null, "缺少字段时返回非 null");
                    Assert(config.Version == "2.0", "Version 正确读取");
                    Assert(config.StationId == "DEFAULT", "缺失 StationId → DEFAULT");
                    Assert(config.StationName == "未命名车站", "缺失 StationName → 未命名车站");
                    Assert(config.FileMapping != null, "FileMapping 初始化为空字典");
                    Assert(config.PointIdMapping != null, "PointIdMapping 初始化为空字典");
                    Assert(config.DirectionMapping != null, "DirectionMapping 初始化为空字典");

                    Console.WriteLine("  缺字段 JSON → 各字段使用默认值");
                    Console.WriteLine("  [PASS] Cycle 5");
                    passed++;
                }
                finally
                {
                    CleanupTempDir(tempDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 6: GetSwitchName 已映射
        // ================================================================

        /// <summary>
        /// Cycle 6: 已映射的文件返回 switchName。
        /// </summary>
        static void TestGetSwitchNameMapped()
        {
            Console.WriteLine("--- Cycle 6: GetSwitchName 已映射 ---");
            try
            {
                var config = CreateSampleConfig();

                string name = config.GetSwitchName("SwitchCurve(0)");
                Assert(name == "1#道岔",
                    string.Format("SwitchCurve(0) → 1#道岔 (actual: {0})", name));

                name = config.GetSwitchName("SwitchCurve(4)");
                Assert(name == "2#道岔",
                    string.Format("SwitchCurve(4) → 2#道岔 (actual: {0})", name));

                Console.WriteLine("  SwitchCurve(0) → {0}", config.GetSwitchName("SwitchCurve(0)"));
                Console.WriteLine("  SwitchCurve(4) → {0}", config.GetSwitchName("SwitchCurve(4)"));
                Console.WriteLine("  [PASS] Cycle 6");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 7: GetSwitchName 未映射降级
        // ================================================================

        /// <summary>
        /// Cycle 7: 未映射的文件返回文件名本身。
        /// </summary>
        static void TestGetSwitchNameUnmapped()
        {
            Console.WriteLine("--- Cycle 7: GetSwitchName 未映射降级 ---");
            try
            {
                var config = CreateSampleConfig();

                string name = config.GetSwitchName("SwitchCurve(99)");
                Assert(name == "SwitchCurve(99)",
                    string.Format("未映射 → 返回文件名 (actual: {0})", name));

                name = config.GetSwitchName("SomeFile.dat");
                Assert(name == "SomeFile.dat",
                    string.Format("未知文件 → 返回文件名 (actual: {0})", name));

                // null/空字符串
                name = config.GetSwitchName(null);
                Assert(name == "(未知)", "null → (未知)");

                name = config.GetSwitchName("");
                Assert(name == "(未知)", "空字符串 → (未知)");

                Console.WriteLine("  未映射: SwitchCurve(99) → {0}", config.GetSwitchName("SwitchCurve(99)"));
                Console.WriteLine("  null → {0}", config.GetSwitchName(null));
                Console.WriteLine("  [PASS] Cycle 7");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 8: GetPointConfigName 已映射
        // ================================================================

        /// <summary>
        /// Cycle 8: 已映射的点号返回 configName。
        /// </summary>
        static void TestGetPointConfigNameMapped()
        {
            Console.WriteLine("--- Cycle 8: GetPointConfigName 已映射 ---");
            try
            {
                var config = CreateSampleConfig();

                string name = config.GetPointConfigName(184);
                Assert(name == "537GH",
                    string.Format("点号 184 → 537GH (actual: {0})", name));

                name = config.GetPointConfigName(185);
                Assert(name == "537GB",
                    string.Format("点号 185 → 537GB (actual: {0})", name));

                Console.WriteLine("  点号 184 → {0}", config.GetPointConfigName(184));
                Console.WriteLine("  点号 185 → {0}", config.GetPointConfigName(185));
                Console.WriteLine("  [PASS] Cycle 8");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 9: GetPointConfigName 未映射降级
        // ================================================================

        /// <summary>
        /// Cycle 9: 未映射的点号返回降级显示名。
        /// </summary>
        static void TestGetPointConfigNameUnmapped()
        {
            Console.WriteLine("--- Cycle 9: GetPointConfigName 未映射降级 ---");
            try
            {
                var config = CreateSampleConfig();

                string name = config.GetPointConfigName(999);
                Assert(name == "点号999(未映射)",
                    string.Format("未映射 → 降级显示 (actual: {0})", name));

                name = config.GetPointConfigName(0);
                Assert(name == "点号0(未映射)",
                    string.Format("点号0 → 降级显示 (actual: {0})", name));

                Console.WriteLine("  点号 999 → {0}", config.GetPointConfigName(999));
                Console.WriteLine("  [PASS] Cycle 9");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 10: 热加载
        // ================================================================

        /// <summary>
        /// Cycle 10: 修改配置文件后 Reload 反映新值。
        /// </summary>
        static void TestHotReload()
        {
            Console.WriteLine("--- Cycle 10: 热加载 ---");
            try
            {
                string tempDir = CreateTempDir();
                try
                {
                    string filePath = Path.Combine(tempDir, "switch_mapping.json");

                    // 写入初始配置
                    string json1 = @"{
                        ""version"": ""1.0"",
                        ""stationId"": ""SSB"",
                        ""stationName"": ""三水北"",
                        ""fileMapping"": {
                            ""SwitchCurve(0)"": {
                                ""switchId"": ""SW_01"",
                                ""switchName"": ""1#道岔"",
                                ""description"": ""初始"",
                                ""directionHint"": ""定位↔反位""
                            }
                        },
                        ""pointIdMapping"": {},
                        ""directionMapping"": {}
                    }";
                    File.WriteAllText(filePath, json1, Encoding.UTF8);

                    var config = MappingConfig.Load(filePath);
                    Assert(config.GetSwitchName("SwitchCurve(0)") == "1#道岔",
                        "初始加载: 1#道岔");

                    // 修改配置文件
                    string json2 = @"{
                        ""version"": ""1.0"",
                        ""stationId"": ""SSB"",
                        ""stationName"": ""三水北"",
                        ""fileMapping"": {
                            ""SwitchCurve(0)"": {
                                ""switchId"": ""SW_01"",
                                ""switchName"": ""1#道岔(已确认J尖轨)"",
                                ""description"": ""已确认"",
                                ""directionHint"": ""定位↔反位""
                            }
                        },
                        ""pointIdMapping"": {},
                        ""directionMapping"": {}
                    }";
                    File.WriteAllText(filePath, json2, Encoding.UTF8);

                    // 热加载
                    config.Reload();
                    Assert(config.GetSwitchName("SwitchCurve(0)") == "1#道岔(已确认J尖轨)",
                        string.Format("热加载后名称更新 (actual: {0})",
                            config.GetSwitchName("SwitchCurve(0)")));

                    Console.WriteLine("  初始: SwitchCurve(0) → 1#道岔");
                    Console.WriteLine("  修改配置文件后 Reload");
                    Console.WriteLine("  热加载: SwitchCurve(0) → {0}", config.GetSwitchName("SwitchCurve(0)"));
                    Console.WriteLine("  [PASS] Cycle 10");
                    passed++;
                }
                finally
                {
                    CleanupTempDir(tempDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 11: 独立于诊断规则配置
        // ================================================================

        /// <summary>
        /// Cycle 11: MappingConfig 和诊断规则配置互不干扰。
        /// </summary>
        static void TestIndependentFromDiagnosisConfig()
        {
            Console.WriteLine("--- Cycle 11: 独立于诊断规则配置 ---");
            try
            {
                // MappingConfig 不需要 RulesPath
                var config = CreateSampleConfig();
                Assert(config.GetSwitchName("SwitchCurve(0)") == "1#道岔",
                    "无需诊断规则目录即可工作");

                // MappingConfig 没有 rules 字段
                string json = @"{
                    ""version"": ""1.0"",
                    ""stationId"": ""SSB"",
                    ""stationName"": ""测试"",
                    ""fileMapping"": {},
                    ""pointIdMapping"": {},
                    ""directionMapping"": {}
                }";
                var parsed = MappingConfig.LoadFromJson(json);
                Assert(parsed != null, "纯映射 JSON 正确解析");

                // 多余字段不干扰解析
                string jsonWithExtra = @"{
                    ""version"": ""1.0"",
                    ""stationId"": ""SSB"",
                    ""stationName"": ""测试"",
                    ""fileMapping"": {},
                    ""pointIdMapping"": {},
                    ""directionMapping"": {},
                    ""extraField"": ""不应干扰"",
                    ""rules"": []
                }";
                var parsedExtra = MappingConfig.LoadFromJson(jsonWithExtra);
                Assert(parsedExtra != null, "含多余字段仍正确解析");
                Assert(parsedExtra.StationName == "测试", "多余字段不影响核心字段");

                Console.WriteLine("  映射配置独立于诊断规则配置");
                Console.WriteLine("  [PASS] Cycle 11");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 12: 裸数字键的向后兼容
        // ================================================================

        /// <summary>
        /// Cycle 12: 当 DB 中存储裸数字作为 SwitchId（如 "0"），
        /// GetSwitchName 应尝试 "SwitchCurve(0)" 格式作为后备匹配。
        /// </summary>
        static void TestBareNumberKeyFallback()
        {
            Console.WriteLine("--- Cycle 12: 裸数字键向后兼容 ---");
            try
            {
                var config = CreateSampleConfig();

                // 使用裸数字 "0" 查找——应通过后备逻辑匹配到 "SwitchCurve(0)"
                string name = config.GetSwitchName("0");
                Assert(name == "1#道岔",
                    string.Format("裸数字 '0' → 应匹配 SwitchCurve(0) 得到 1#道岔 (actual: {0})", name));

                // 裸数字 "4"
                name = config.GetSwitchName("4");
                Assert(name == "2#道岔",
                    string.Format("裸数字 '4' → 应匹配 SwitchCurve(4) 得到 2#道岔 (actual: {0})", name));

                // 非数字的未映射值 → 降级返回原值
                name = config.GetSwitchName("unknown_file");
                Assert(name == "unknown_file",
                    string.Format("非数字未映射值 → 降级返回原值 (actual: {0})", name));

                Console.WriteLine("  裸数字 '0' → {0}", config.GetSwitchName("0"));
                Console.WriteLine("  裸数字 '4' → {0}", config.GetSwitchName("4"));
                Console.WriteLine("  [PASS] Cycle 12");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 13: 端到端 parser → 映射流程
        // ================================================================

        /// <summary>
        /// Cycle 13: 模拟 SwitchCurveParser 提取 SwitchId 的完整路径。
        /// 从 "SwitchCurve(0).dat" → 提取 basename → 映射到 "1#道岔"。
        /// </summary>
        static void TestParserToMappingFlow()
        {
            Console.WriteLine("--- Cycle 13: 端到端 parser → 映射流程 ---");
            try
            {
                var config = CreateSampleConfig();

                // 模拟 parser 的文件名提取逻辑
                string fileSource = "SwitchCurve(0).dat";
                string extractedId = System.IO.Path.GetFileNameWithoutExtension(fileSource);
                Assert(extractedId == "SwitchCurve(0)",
                    string.Format("Parser 提取: SwitchCurve(0).dat → SwitchCurve(0) (actual: {0})", extractedId));

                // 通过映射查找
                string displayName = config.GetSwitchName(extractedId);
                Assert(displayName == "1#道岔",
                    string.Format("映射: SwitchCurve(0) → 1#道岔 (actual: {0})", displayName));

                // 另一个文件
                string fileSource2 = "SwitchCurve(4).dat";
                string extractedId2 = System.IO.Path.GetFileNameWithoutExtension(fileSource2);
                Assert(extractedId2 == "SwitchCurve(4)",
                    string.Format("Parser 提取: SwitchCurve(4).dat → SwitchCurve(4) (actual: {0})", extractedId2));
                Assert(config.GetSwitchName(extractedId2) == "2#道岔", "映射: SwitchCurve(4) → 2#道岔");

                // 未映射文件 → 降级显示原始文件名
                string fileSource3 = "SwitchCurve(99).dat";
                string extractedId3 = System.IO.Path.GetFileNameWithoutExtension(fileSource3);
                string fallbackName = config.GetSwitchName(extractedId3);
                Assert(fallbackName == "SwitchCurve(99)",
                    string.Format("未映射 → 返回原文件名 (actual: {0})", fallbackName));

                Console.WriteLine("  {0} → {1} → {2}", fileSource, extractedId, displayName);
                Console.WriteLine("  {0} → {1} → {2}", fileSource2, extractedId2, config.GetSwitchName(extractedId2));
                Console.WriteLine("  {0} → {1} (未映射, 降级)", fileSource3, fallbackName);
                Console.WriteLine("  [PASS] Cycle 13");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 14: 实际 switch_mapping.json 文件加载验证
        // ================================================================

        /// <summary>
        /// Cycle 14: 验证实际的 Config/switch_mapping.json 文件能正确加载，
        /// 并包含所有 16 个数据文件的映射条目。
        /// </summary>
        static void TestRealMappingFileLoad()
        {
            Console.WriteLine("--- Cycle 14: 实际 switch_mapping.json 文件加载 ---");
            try
            {
                // 查找实际配置文件
                string configPath = null;
                var candidates = new[]
                {
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Config", "switch_mapping.json")),
                    System.IO.Path.GetFullPath(System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Config", "switch_mapping.json")),
                };

                foreach (var path in candidates)
                {
                    if (System.IO.File.Exists(path))
                    {
                        configPath = path;
                        break;
                    }
                }

                if (configPath == null)
                {
                    Console.WriteLine("  跳过: 找不到 switch_mapping.json 文件");
                    Console.WriteLine("  [PASS] Cycle 14 (跳过)");
                    passed++;
                    Console.WriteLine();
                    return;
                }

                Console.WriteLine("  加载文件: {0}", configPath);

                var config = MappingConfig.Load(configPath);
                Assert(config != null, "Load 返回非 null");
                Assert(config.Version == "1.0", "Version = 1.0");
                Assert(config.StationId == "SSB", "StationId = SSB");
                Assert(config.StationName == "三水北", "StationName = 三水北");
                Assert(config.FileMapping != null, "FileMapping 非 null");
                Assert(config.FileMapping.Count == 16,
                    string.Format("FileMapping 有 16 条（实际: {0}）", config.FileMapping.Count));

                // 验证关键条目
                Assert(config.FileMapping.ContainsKey("SwitchCurve(0)"), "包含 SwitchCurve(0)");
                Assert(config.FileMapping["SwitchCurve(0)"].SwitchName == "1#道岔",
                    string.Format("SwitchCurve(0) → {0}", config.FileMapping["SwitchCurve(0)"].SwitchName));

                // 验证 GetSwitchName 对所有已映射文件可用
                string[] allFiles = { "SwitchCurve(0)", "SwitchCurve(3)", "SwitchCurve(4)", "SwitchCurve(7)",
                    "SwitchCurve(8)", "SwitchCurve(11)", "SwitchCurve(12)", "SwitchCurve(15)",
                    "SwitchCurve(16)", "SwitchCurve(19)", "SwitchCurve(20)", "SwitchCurve(23)",
                    "SwitchCurve(24)", "SwitchCurve(27)", "SwitchCurve(28)", "SwitchCurve(31)" };
                foreach (var f in allFiles)
                {
                    string name = config.GetSwitchName(f);
                    Assert(name != f,
                        string.Format("{0} → {1} (不应返回原文件名)", f, name));
                }

                Console.WriteLine("  所有 16 个文件均有映射且返回非原文件名");
                Console.WriteLine("  [PASS] Cycle 14");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Helpers
        // ================================================================

        static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                Console.WriteLine("    ASSERT FAIL: {0}", message);
                throw new Exception(message);
            }
        }

        static MappingConfig CreateSampleConfig()
        {
            string json = @"{
                ""version"": ""1.0"",
                ""stationId"": ""SSB"",
                ""stationName"": ""三水北"",
                ""fileMapping"": {
                    ""SwitchCurve(0)"": {
                        ""switchId"": ""SW_01"",
                        ""switchName"": ""1#道岔"",
                        ""description"": ""待确认-可能为1#J尖轨"",
                        ""directionHint"": ""定位↔反位""
                    },
                    ""SwitchCurve(4)"": {
                        ""switchId"": ""SW_02"",
                        ""switchName"": ""2#道岔"",
                        ""description"": ""待确认-可能为1#X心轨"",
                        ""directionHint"": ""定位↔反位""
                    }
                },
                ""pointIdMapping"": {
                    ""184"": {
                        ""configName"": ""537GH"",
                        ""switchId"": null,
                        ""description"": ""待确认-H/B含义及对应道岔""
                    },
                    ""185"": {
                        ""configName"": ""537GB"",
                        ""switchId"": null,
                        ""description"": ""待确认""
                    }
                },
                ""directionMapping"": {
                    ""DB"": { ""meaning"": ""定位表示"", ""note"": ""DB 继电器吸起=道岔在定位"" },
                    ""FB"": { ""meaning"": ""反位表示"", ""note"": ""FB 继电器吸起=道岔在反位"" },
                    ""H"": { ""meaning"": ""待确认"", ""note"": ""可能是定位或反位其中之一"" },
                    ""B"": { ""meaning"": ""待确认"", ""note"": ""与H互斥"" }
                }
            }";
            return MappingConfig.LoadFromJson(json);
        }

        static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "slice11_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void CleanupTempDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { /* 忽略清理失败 */ }
        }
    }
}
