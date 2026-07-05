using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SwitchMonitor.Common;
using SwitchMonitor.Diagnosis;
using DiagnosisEngine;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 10: 诊断引擎 TDD 测试。
    /// 测试 DiagnosisEngine 的规则加载、5 条诊断规则执行、JSON 配置解析。
    /// </summary>
    public class Slice10Tests
    {
        static int passed = 0;
        static int failed = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 10: 诊断引擎 测试 ===");
            Console.WriteLine();

            // ---- 配置模型测试 ----
            TestRuleConfigDeserialization();

            // ---- 引擎初始化测试 ----
            TestEngineInitialize();
            TestEngineThrowsWhenNotInitialized();
            TestEngineThrowsOnNullData();

            // ---- 规则 1: 转换时间异常 ----
            TestConversionTimeNormal();
            TestConversionTimeWarning();
            TestConversionTimeAlarm();

            // ---- 规则 2: 采样数异常 ----
            TestSampleCountNormal();
            TestSampleCountAlarm();

            // ---- 规则 3: 解锁段峰值异常 ----
            TestUnlockPeakNormal();
            TestUnlockPeakWarning();
            TestUnlockPeakAlarm();

            // ---- 规则 4: 转换段稳态异常 ----
            TestConversionSteadyNormal();
            TestConversionSteadyWarning();

            // ---- 规则 5: 锁闭段峰值异常 ----
            TestLockPeakNormal();
            TestLockPeakWarning();
            TestLockPeakAlarm();

            // ---- 集成测试 ----
            TestDisabledRuleNotExecuted();
            TestAllRulesOnNormalData();
            TestMultipleRulesTriggered();
            TestEmptySamplesHandling();

            Console.WriteLine();
            Console.WriteLine("=== Slice 10 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // TDD Cycle 1: RuleConfig JSON 反序列化
        // ================================================================

        /// <summary>
        /// Cycle 1: JSON 配置文件正确反序列化为 RuleConfig 列表。
        /// </summary>
        static void TestRuleConfigDeserialization()
        {
            Console.WriteLine("--- Cycle 1: RuleConfig JSON 反序列化 ---");
            try
            {
                string json = @"{
                    ""rules"": [
                        {
                            ""name"": ""conversion_time"",
                            ""displayName"": ""转换时间异常"",
                            ""enabled"": true,
                            ""type"": ""threshold"",
                            ""parameters"": {
                                ""referenceSeconds"": 5.8,
                                ""warningDeviation"": 0.5,
                                ""alarmDeviation"": 1.5
                            }
                        },
                        {
                            ""name"": ""unlock_peak"",
                            ""displayName"": ""解锁段峰值异常"",
                            ""enabled"": false,
                            ""type"": ""morphology"",
                            ""parameters"": {
                                ""referenceValue"": 3.5,
                                ""warningRatio"": 1.3,
                                ""alarmRatio"": 1.5
                            }
                        }
                    ]
                }";

                var config = RuleConfigCollection.LoadFromJson(json);
                Assert(config != null, "反序列化结果非 null");
                Assert(config.Rules.Count == 2, string.Format("有 2 条规则 (实际: {0})", config.Rules.Count));

                var rule1 = config.Rules[0];
                Assert(rule1.Name == "conversion_time", "规则1 Name");
                Assert(rule1.DisplayName == "转换时间异常", "规则1 DisplayName");
                Assert(rule1.Enabled == true, "规则1 Enabled");
                Assert(rule1.Type == "threshold", "规则1 Type");
                Assert(rule1.Parameters.Count == 3, string.Format("规则1 有3个参数 (实际: {0})", rule1.Parameters.Count));

                var rule2 = config.Rules[1];
                Assert(rule2.Name == "unlock_peak", "规则2 Name");
                Assert(rule2.Enabled == false, "规则2 Enabled = false");
                Assert(rule2.Type == "morphology", "规则2 Type");

                Console.WriteLine("  规则1: {0} (enabled={1}, type={2})", rule1.DisplayName, rule1.Enabled, rule1.Type);
                Console.WriteLine("  规则2: {0} (enabled={1}, type={2})", rule2.DisplayName, rule2.Enabled, rule2.Type);
                Console.WriteLine("  [PASS] Cycle 1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 2: 引擎初始化
        // ================================================================

        /// <summary>
        /// Cycle 2: Initialize() 从目录加载全部 .json 规则文件。
        /// </summary>
        static void TestEngineInitialize()
        {
            Console.WriteLine("--- Cycle 2: 引擎初始化 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory();
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 验证初始化后可以正常调用 Diagnose
                    var data = MakeNormalData();
                    var results = engine.Diagnose(data);

                    Assert(results != null, "初始化后 Diagnose 返回非 null");
                    Assert(results.Count > 0, "初始化后 Diagnose 返回至少 1 条结果");
                    Assert(results.Count == 5, string.Format("5 条规则全部执行 (实际: {0})", results.Count));

                    Console.WriteLine("  从目录加载规则: {0}", rulesDir);
                    Console.WriteLine("  返回 {0} 条诊断结果", results.Count);
                    foreach (var r in results)
                    {
                        Console.WriteLine("    [{0}] {1}: {2}", r.Level, r.RuleName, r.Description);
                    }
                    Console.WriteLine("  [PASS] Cycle 2");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 3: 未初始化时抛出异常
        // ================================================================

        /// <summary>
        /// Cycle 3: 未初始化时调用 Diagnose 抛出 InvalidOperationException。
        /// </summary>
        static void TestEngineThrowsWhenNotInitialized()
        {
            Console.WriteLine("--- Cycle 3: 未初始化时抛异常 ---");
            try
            {
                var engine = new DiagnosisEngine.DiagnosisEngine();

                bool threw = false;
                try
                {
                    engine.Diagnose(new SwitchActionData());
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert(threw, "未初始化时 Diagnose 抛出 InvalidOperationException");

                Console.WriteLine("  [PASS] Cycle 3");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 4: null 数据抛异常
        // ================================================================

        /// <summary>
        /// Cycle 4: Diagnose(null) 抛出 ArgumentNullException。
        /// </summary>
        static void TestEngineThrowsOnNullData()
        {
            Console.WriteLine("--- Cycle 4: null 数据抛异常 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory();
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    bool threw = false;
                    try
                    {
                        engine.Diagnose(null);
                    }
                    catch (ArgumentNullException)
                    {
                        threw = true;
                    }
                    Assert(threw, "null 参数时 Diagnose 抛出 ArgumentNullException");

                    Console.WriteLine("  [PASS] Cycle 4");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 5: 规则1 转换时间异常 — 正常
        // ================================================================

        /// <summary>
        /// Cycle 5: 转换时间与参考值偏差 ≤ 0.5s → 正常。
        /// </summary>
        static void TestConversionTimeNormal()
        {
            Console.WriteLine("--- Cycle 5: 转换时间正常 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("conversion_time");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 150 samples / 25 Hz = 6.0s → 偏差 0.2s ≤ 0.5s → 正常
                    var data = MakeDataWithSamples(150, 25, 2.5f);
                    var results = engine.Diagnose(data);

                    var ctResult = results.Find(r => r.RuleName == "转换时间异常");
                    Assert(ctResult != null, "包含转换时间异常规则结果");
                    Assert(ctResult.Level == DiagnosisLevel.Normal,
                        string.Format("偏差 0.2s → 正常 (actual: {0})", ctResult.Level));

                    Console.WriteLine("  转换时间={0:F2}s, 参考=5.80s, 偏差={1:F2}s → {2}",
                        150f / 25f, 150f / 25f - 5.8f, ctResult.Level);
                    Console.WriteLine("  [PASS] Cycle 5");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 6: 规则1 转换时间异常 — 预警
        // ================================================================

        /// <summary>
        /// Cycle 6: 转换时间偏差 0.5~1.5s → 预警。
        /// </summary>
        static void TestConversionTimeWarning()
        {
            Console.WriteLine("--- Cycle 6: 转换时间预警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("conversion_time");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 170 samples / 25 Hz = 6.8s → 偏差 1.0s → 预警
                    var data = MakeDataWithSamples(170, 25, 2.5f);
                    var results = engine.Diagnose(data);

                    var ctResult = results.Find(r => r.RuleName == "转换时间异常");
                    Assert(ctResult != null, "包含转换时间异常规则结果");
                    Assert(ctResult.Level == DiagnosisLevel.Warning,
                        string.Format("偏差 1.0s → 预警 (actual: {0})", ctResult.Level));

                    Console.WriteLine("  转换时间={0:F2}s, 参考=5.80s, 偏差={1:F2}s → {2}",
                        170f / 25f, 170f / 25f - 5.8f, ctResult.Level);
                    Console.WriteLine("  [PASS] Cycle 6");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 7: 规则1 转换时间异常 — 报警
        // ================================================================

        /// <summary>
        /// Cycle 7: 转换时间偏差 > 1.5s → 报警。
        /// </summary>
        static void TestConversionTimeAlarm()
        {
            Console.WriteLine("--- Cycle 7: 转换时间报警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("conversion_time");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 200 samples / 25 Hz = 8.0s → 偏差 2.2s > 1.5s → 报警
                    var data = MakeDataWithSamples(200, 25, 2.5f);
                    var results = engine.Diagnose(data);

                    var ctResult = results.Find(r => r.RuleName == "转换时间异常");
                    Assert(ctResult != null, "包含转换时间异常规则结果");
                    Assert(ctResult.Level == DiagnosisLevel.Alarm,
                        string.Format("偏差 2.2s → 报警 (actual: {0})", ctResult.Level));

                    Console.WriteLine("  转换时间={0:F2}s, 参考=5.80s, 偏差={1:F2}s → {2}",
                        200f / 25f, 200f / 25f - 5.8f, ctResult.Level);
                    Console.WriteLine("  [PASS] Cycle 7");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 8: 规则2 采样数异常 — 正常
        // ================================================================

        /// <summary>
        /// Cycle 8: 采样数 ≥ 参考最小值的 80% → 正常。
        /// </summary>
        static void TestSampleCountNormal()
        {
            Console.WriteLine("--- Cycle 8: 采样数正常 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("sample_count");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 参考最小值 150, 80% = 120
                    // 构造: 130 samples → ≥ 120 → 正常
                    var data = MakeDataWithSamples(130, 25, 2.5f);
                    var results = engine.Diagnose(data);

                    var scResult = results.Find(r => r.RuleName == "采样数异常");
                    Assert(scResult != null, "包含采样数异常规则结果");
                    Assert(scResult.Level == DiagnosisLevel.Normal,
                        string.Format("采样数 130 ≥ 120 → 正常 (actual: {0})", scResult.Level));

                    Console.WriteLine("  采样数={0}, 参考最小值={1}, 80%阈值={2} → {3}",
                        130, 150, 120, scResult.Level);
                    Console.WriteLine("  [PASS] Cycle 8");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 9: 规则2 采样数异常 — 报警
        // ================================================================

        /// <summary>
        /// Cycle 9: 采样数 < 参考最小值的 80% → 报警。
        /// </summary>
        static void TestSampleCountAlarm()
        {
            Console.WriteLine("--- Cycle 9: 采样数报警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("sample_count");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 参考最小值 150, 80% = 120
                    // 构造: 100 samples → < 120 → 报警
                    var data = MakeDataWithSamples(100, 25, 2.5f);
                    var results = engine.Diagnose(data);

                    var scResult = results.Find(r => r.RuleName == "采样数异常");
                    Assert(scResult != null, "包含采样数异常规则结果");
                    Assert(scResult.Level == DiagnosisLevel.Alarm,
                        string.Format("采样数 100 < 120 → 报警 (actual: {0})", scResult.Level));

                    Console.WriteLine("  采样数={0}, 参考最小值={1}, 80%阈值={2} → {3}",
                        100, 150, 120, scResult.Level);
                    Console.WriteLine("  [PASS] Cycle 9");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 10: 规则3 解锁段峰值异常 — 正常
        // ================================================================

        /// <summary>
        /// Cycle 10: 解锁段峰值 ≤ 参考值的 1.3 倍 → 正常。
        /// </summary>
        static void TestUnlockPeakNormal()
        {
            Console.WriteLine("--- Cycle 10: 解锁段峰值正常 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("unlock_peak");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 200 采样点，前 15% (30 点) 峰值 = 4.0A
                    // 参考值 = 3.5A, warningRatio = 1.3 → 阈值 = 4.55A
                    // 4.0 ≤ 4.55 → 正常
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 4.0f, midValue: 2.5f, lockPeak: 3.0f);
                    var results = engine.Diagnose(data);

                    var upResult = results.Find(r => r.RuleName == "解锁段峰值异常");
                    Assert(upResult != null, "包含解锁段峰值异常规则结果");
                    Assert(upResult.Level == DiagnosisLevel.Normal,
                        string.Format("解锁峰值 4.0A ≤ 4.55A → 正常 (actual: {0})", upResult.Level));

                    Console.WriteLine("  解锁峰值={0:F2}A, 参考={1:F2}A, 预警阈值={2:F2}A → {3}",
                        4.0f, 3.5f, 3.5f * 1.3f, upResult.Level);
                    Console.WriteLine("  [PASS] Cycle 10");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 11: 规则3 解锁段峰值异常 — 预警
        // ================================================================

        /// <summary>
        /// Cycle 11: 解锁段峰值超出参考值 30% → 预警。
        /// </summary>
        static void TestUnlockPeakWarning()
        {
            Console.WriteLine("--- Cycle 11: 解锁段峰值预警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("unlock_peak");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 参考值 = 3.5A, warningRatio = 1.3 → 阈值 = 4.55A
                    // alarmRatio = 1.5 → 报警阈值 = 5.25A
                    // 构造: 解锁峰值 = 5.0A → 超过 1.3x 但不到 1.5x → 预警
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 5.0f, midValue: 2.5f, lockPeak: 3.0f);
                    var results = engine.Diagnose(data);

                    var upResult = results.Find(r => r.RuleName == "解锁段峰值异常");
                    Assert(upResult != null, "包含解锁段峰值异常规则结果");
                    Assert(upResult.Level == DiagnosisLevel.Warning,
                        string.Format("解锁峰值 5.0A → 预警 (actual: {0})", upResult.Level));
                    Assert(upResult.Description.Contains("密贴过紧"),
                        string.Format("描述包含'密贴过紧' (actual: {0})", upResult.Description));

                    Console.WriteLine("  解锁峰值={0:F2}A, 预警阈值={1:F2}A, 报警阈值={2:F2}A → {3}",
                        5.0f, 4.55f, 5.25f, upResult.Level);
                    Console.WriteLine("  描述: {0}", upResult.Description);
                    Console.WriteLine("  [PASS] Cycle 11");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 12: 规则3 解锁段峰值异常 — 报警
        // ================================================================

        /// <summary>
        /// Cycle 12: 解锁段峰值超出参考值 50% → 报警。
        /// </summary>
        static void TestUnlockPeakAlarm()
        {
            Console.WriteLine("--- Cycle 12: 解锁段峰值报警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("unlock_peak");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 参考值 = 3.5A, alarmRatio = 1.5 → 报警阈值 = 5.25A
                    // 构造: 解锁峰值 = 6.0A → 超过 1.5x → 报警
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 6.0f, midValue: 2.5f, lockPeak: 3.0f);
                    var results = engine.Diagnose(data);

                    var upResult = results.Find(r => r.RuleName == "解锁段峰值异常");
                    Assert(upResult != null, "包含解锁段峰值异常规则结果");
                    Assert(upResult.Level == DiagnosisLevel.Alarm,
                        string.Format("解锁峰值 6.0A → 报警 (actual: {0})", upResult.Level));

                    Console.WriteLine("  解锁峰值={0:F2}A, 报警阈值={1:F2}A, ratio={2:F2} → {3}",
                        6.0f, 5.25f, 6.0f / 3.5f, upResult.Level);
                    Console.WriteLine("  [PASS] Cycle 12");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 13: 规则4 转换段稳态异常 — 正常
        // ================================================================

        /// <summary>
        /// Cycle 13: 转换段平均值 ≤ 参考值的 1.3 倍 → 正常。
        /// </summary>
        static void TestConversionSteadyNormal()
        {
            Console.WriteLine("--- Cycle 13: 转换段稳态正常 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("conversion_steady");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 200 采样点，中间 20%~80% (40~160) 平均值 = 2.5A
                    // 参考值 = 2.8A, warningRatio = 1.3 → 阈值 = 3.64A
                    // 2.5 ≤ 3.64 → 正常
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 5.0f, midValue: 2.5f, lockPeak: 4.0f);
                    var results = engine.Diagnose(data);

                    var csResult = results.Find(r => r.RuleName == "转换段稳态异常");
                    Assert(csResult != null, "包含转换段稳态异常规则结果");
                    Assert(csResult.Level == DiagnosisLevel.Normal,
                        string.Format("转换段均值 2.5A ≤ 3.64A → 正常 (actual: {0})", csResult.Level));

                    Console.WriteLine("  转换段均值={0:F2}A, 参考={1:F2}A, 预警阈值={2:F2}A → {3}",
                        2.5f, 2.8f, 2.8f * 1.3f, csResult.Level);
                    Console.WriteLine("  [PASS] Cycle 13");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 14: 规则4 转换段稳态异常 — 预警
        // ================================================================

        /// <summary>
        /// Cycle 14: 转换段平均值超出参考值 30% → 预警。
        /// </summary>
        static void TestConversionSteadyWarning()
        {
            Console.WriteLine("--- Cycle 14: 转换段稳态预警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("conversion_steady");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 参考值 = 2.8A, warningRatio = 1.3 → 阈值 = 3.64A
                    // 构造: midValue = 4.0A → > 3.64A → 预警
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 5.0f, midValue: 4.0f, lockPeak: 4.0f);
                    var results = engine.Diagnose(data);

                    var csResult = results.Find(r => r.RuleName == "转换段稳态异常");
                    Assert(csResult != null, "包含转换段稳态异常规则结果");
                    Assert(csResult.Level == DiagnosisLevel.Warning,
                        string.Format("转换段均值 4.0A > 3.64A → 预警 (actual: {0})", csResult.Level));
                    Assert(csResult.Description.Contains("滑床板缺油或卡阻"),
                        string.Format("描述包含'滑床板缺油或卡阻' (actual: {0})", csResult.Description));

                    Console.WriteLine("  转换段均值={0:F2}A, 预警阈值={1:F2}A → {2}",
                        4.0f, 3.64f, csResult.Level);
                    Console.WriteLine("  描述: {0}", csResult.Description);
                    Console.WriteLine("  [PASS] Cycle 14");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 15: 规则5 锁闭段峰值异常 — 正常
        // ================================================================

        /// <summary>
        /// Cycle 15: 锁闭段峰值 ≤ 参考值的 1.3 倍 → 正常。
        /// </summary>
        static void TestLockPeakNormal()
        {
            Console.WriteLine("--- Cycle 15: 锁闭段峰值正常 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("lock_peak");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 200 采样点，后 15% (170~199) 峰值 = 3.5A
                    // 参考值 = 3.2A, warningRatio = 1.3 → 阈值 = 4.16A
                    // 3.5 ≤ 4.16 → 正常
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 5.0f, midValue: 2.5f, lockPeak: 3.5f);
                    var results = engine.Diagnose(data);

                    var lpResult = results.Find(r => r.RuleName == "锁闭段峰值异常");
                    Assert(lpResult != null, "包含锁闭段峰值异常规则结果");
                    Assert(lpResult.Level == DiagnosisLevel.Normal,
                        string.Format("锁闭峰值 3.5A ≤ 4.16A → 正常 (actual: {0})", lpResult.Level));

                    Console.WriteLine("  锁闭峰值={0:F2}A, 参考={1:F2}A, 预警阈值={2:F2}A → {3}",
                        3.5f, 3.2f, 3.2f * 1.3f, lpResult.Level);
                    Console.WriteLine("  [PASS] Cycle 15");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 16: 规则5 锁闭段峰值异常 — 预警
        // ================================================================

        /// <summary>
        /// Cycle 16: 锁闭段峰值超出参考值 30% → 预警。
        /// </summary>
        static void TestLockPeakWarning()
        {
            Console.WriteLine("--- Cycle 16: 锁闭段峰值预警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("lock_peak");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 参考值 = 3.2A, warningRatio = 1.3 → 阈值 = 4.16A
                    // alarmRatio = 1.5 → 报警阈值 = 4.8A
                    // 构造: 锁闭峰值 = 4.5A → 超过 1.3x 但不到 1.5x → 预警
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 6.0f, midValue: 2.5f, lockPeak: 4.5f);
                    var results = engine.Diagnose(data);

                    var lpResult = results.Find(r => r.RuleName == "锁闭段峰值异常");
                    Assert(lpResult != null, "包含锁闭段峰值异常规则结果");
                    Assert(lpResult.Level == DiagnosisLevel.Warning,
                        string.Format("锁闭峰值 4.5A → 预警 (actual: {0})", lpResult.Level));
                    Assert(lpResult.Description.Contains("密贴调整过紧"),
                        string.Format("描述包含'密贴调整过紧' (actual: {0})", lpResult.Description));

                    Console.WriteLine("  锁闭峰值={0:F2}A, 预警阈值={1:F2}A, 报警阈值={2:F2}A → {3}",
                        4.5f, 4.16f, 4.8f, lpResult.Level);
                    Console.WriteLine("  描述: {0}", lpResult.Description);
                    Console.WriteLine("  [PASS] Cycle 16");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 17: 规则5 锁闭段峰值异常 — 报警
        // ================================================================

        /// <summary>
        /// Cycle 17: 锁闭段峰值超出参考值 50% → 报警。
        /// </summary>
        static void TestLockPeakAlarm()
        {
            Console.WriteLine("--- Cycle 17: 锁闭段峰值报警 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory("lock_peak");
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 参考值 = 3.2A, alarmRatio = 1.5 → 报警阈值 = 4.8A
                    // 构造: 锁闭峰值 = 5.5A → 超过 1.5x → 报警
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 6.0f, midValue: 2.5f, lockPeak: 5.5f);
                    var results = engine.Diagnose(data);

                    var lpResult = results.Find(r => r.RuleName == "锁闭段峰值异常");
                    Assert(lpResult != null, "包含锁闭段峰值异常规则结果");
                    Assert(lpResult.Level == DiagnosisLevel.Alarm,
                        string.Format("锁闭峰值 5.5A → 报警 (actual: {0})", lpResult.Level));

                    Console.WriteLine("  锁闭峰值={0:F2}A, 报警阈值={1:F2}A, ratio={2:F2} → {3}",
                        5.5f, 4.8f, 5.5f / 3.2f, lpResult.Level);
                    Console.WriteLine("  [PASS] Cycle 17");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 18: disabled 规则不执行
        // ================================================================

        /// <summary>
        /// Cycle 18: enabled=false 的规则不执行，不出现在结果中。
        /// </summary>
        static void TestDisabledRuleNotExecuted()
        {
            Console.WriteLine("--- Cycle 18: disabled 规则不执行 ---");
            try
            {
                // 创建只有一条规则且 disabled 的配置
                string rulesDir = Path.Combine(Path.GetTempPath(), "slice10_test_disabled_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(rulesDir);
                try
                {
                    string json = @"{
                        ""rules"": [
                            {
                                ""name"": ""conversion_time"",
                                ""displayName"": ""转换时间异常"",
                                ""enabled"": false,
                                ""type"": ""threshold"",
                                ""parameters"": {
                                    ""referenceSeconds"": 5.8,
                                    ""warningDeviation"": 0.5,
                                    ""alarmDeviation"": 1.5
                                }
                            },
                            {
                                ""name"": ""sample_count"",
                                ""displayName"": ""采样数异常"",
                                ""enabled"": true,
                                ""type"": ""threshold"",
                                ""parameters"": {
                                    ""referenceMinCount"": 150
                                }
                            }
                        ]
                    }";
                    File.WriteAllText(Path.Combine(rulesDir, "test_rules.json"), json, Encoding.UTF8);

                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 200 采样点 → 转换时间偏差大（按理应报警），但规则 disabled
                    // 采样数 200 ≥ 120 → 正常
                    var data = MakeDataWithSamples(200, 25, 2.5f);
                    var results = engine.Diagnose(data);

                    // disabled 规则不应出现在结果中
                    var ctResult = results.Find(r => r.RuleName == "转换时间异常");
                    Assert(ctResult == null, "disabled 规则不出现在结果中");

                    // 只有 enabled 的规则
                    Assert(results.Count == 1, string.Format("只有 1 条结果 (实际: {0})", results.Count));
                    Assert(results[0].RuleName == "采样数异常", "唯一结果是采样数异常");

                    Console.WriteLine("  disabled 规则被跳过, 仅 {0} 条结果", results.Count);
                    Console.WriteLine("  [PASS] Cycle 18");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 19: 正常曲线全部规则返回正常
        // ================================================================

        /// <summary>
        /// Cycle 19: 构造"正常"曲线数据 → 所有规则返回正常。
        /// </summary>
        static void TestAllRulesOnNormalData()
        {
            Console.WriteLine("--- Cycle 19: 正常曲线全部正常 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory(); // 全部5条规则
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造"标准正常"曲线: 145 采样点, 25Hz → 5.8s (正好等于参考值)
                    // 各段峰值都在正常范围内
                    var data = MakeDataWithUnlockPeak(145, 25, unlockPeak: 3.0f, midValue: 2.5f, lockPeak: 2.8f);
                    var results = engine.Diagnose(data);

                    Assert(results.Count == 5, string.Format("5 条规则 (实际: {0})", results.Count));

                    int normalCount = 0;
                    foreach (var r in results)
                    {
                        if (r.Level == DiagnosisLevel.Normal) normalCount++;
                        Console.WriteLine("  [{0}] {1}: {2}", r.Level, r.RuleName, r.Description);
                    }

                    Assert(normalCount == 5,
                        string.Format("全部 5 条正常 (实际: {0})", normalCount));

                    Console.WriteLine("  [PASS] Cycle 19");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 20: 多条规则同时触发
        // ================================================================

        /// <summary>
        /// Cycle 20: 一条异常曲线触发多条规则。
        /// </summary>
        static void TestMultipleRulesTriggered()
        {
            Console.WriteLine("--- Cycle 20: 多条规则同时触发 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory();
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    // 构造: 200 采样点(=8.0s, 偏差2.2s→报警),
                    // 解锁峰值 6.0A (ratio 1.71→报警),
                    // 转换段均值 4.0A (ratio 1.43→预警),
                    // 锁闭峰值 5.5A (ratio 1.72→报警)
                    var data = MakeDataWithUnlockPeak(200, 25, unlockPeak: 6.0f, midValue: 4.0f, lockPeak: 5.5f);
                    var results = engine.Diagnose(data);

                    Assert(results.Count == 5, "5 条规则全部执行");

                    // 统计各级别数量
                    int normalCount = 0, warningCount = 0, alarmCount = 0;
                    foreach (var r in results)
                    {
                        switch (r.Level)
                        {
                            case "正常": normalCount++; break;
                            case "预警": warningCount++; break;
                            case "报警": alarmCount++; break;
                        }
                        Console.WriteLine("  [{0}] {1}: {2} (异常值={3}, 参考值={4})",
                            r.Level, r.RuleName, r.Description, r.AbnormalValue, r.ReferenceValue);
                    }

                    Assert(alarmCount >= 3,
                        string.Format("至少 3 条报警 (实际: {0})", alarmCount));
                    Assert(warningCount >= 1,
                        string.Format("至少 1 条预警 (实际: {0})", warningCount));

                    Console.WriteLine("  正常:{0} 预警:{1} 报警:{2}", normalCount, warningCount, alarmCount);
                    Console.WriteLine("  [PASS] Cycle 20");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 21: 空采样数据处理
        // ================================================================

        /// <summary>
        /// Cycle 21: 空 Samples 列表不抛异常，返回安全默认值。
        /// </summary>
        static void TestEmptySamplesHandling()
        {
            Console.WriteLine("--- Cycle 21: 空采样数据处理 ---");
            try
            {
                string rulesDir = CreateTempRulesDirectory();
                try
                {
                    var engine = new DiagnosisEngine.DiagnosisEngine();
                    engine.Initialize(rulesDir);

                    var data = new SwitchActionData
                    {
                        StationName = "测试站",
                        SwitchId = "SW_01",
                        SampleRate = 25,
                        Samples = new List<SamplePoint>() // 空的
                    };

                    // 不应抛异常
                    var results = engine.Diagnose(data);
                    Assert(results != null, "空数据时返回非 null");
                    Assert(results.Count > 0, "空数据时仍有结果");

                    Console.WriteLine("  空采样 → 返回 {0} 条结果（正常/默认值）", results.Count);
                    Console.WriteLine("  [PASS] Cycle 21");
                    passed++;
                }
                finally
                {
                    CleanupTempDirectory(rulesDir);
                }
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

        /// <summary>
        /// 创建包含指定规则的临时目录。
        /// 不传参数则创建全部 5 条规则的配置。
        /// </summary>
        static string CreateTempRulesDirectory(params string[] ruleNames)
        {
            string dir = Path.Combine(Path.GetTempPath(), "slice10_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);

            var allRules = new List<object>();

            if (ruleNames == null || ruleNames.Length == 0 || Array.IndexOf(ruleNames, "conversion_time") >= 0)
            {
                allRules.Add(new
                {
                    name = "conversion_time",
                    displayName = "转换时间异常",
                    enabled = true,
                    type = "threshold",
                    parameters = new { referenceSeconds = 5.8, warningDeviation = 0.5, alarmDeviation = 1.5 }
                });
            }
            if (ruleNames == null || ruleNames.Length == 0 || Array.IndexOf(ruleNames, "sample_count") >= 0)
            {
                allRules.Add(new
                {
                    name = "sample_count",
                    displayName = "采样数异常",
                    enabled = true,
                    type = "threshold",
                    parameters = new { referenceMinCount = 150 }
                });
            }
            if (ruleNames == null || ruleNames.Length == 0 || Array.IndexOf(ruleNames, "unlock_peak") >= 0)
            {
                allRules.Add(new
                {
                    name = "unlock_peak",
                    displayName = "解锁段峰值异常",
                    enabled = true,
                    type = "morphology",
                    parameters = new { referenceValue = 3.5, warningRatio = 1.3, alarmRatio = 1.5 }
                });
            }
            if (ruleNames == null || ruleNames.Length == 0 || Array.IndexOf(ruleNames, "conversion_steady") >= 0)
            {
                allRules.Add(new
                {
                    name = "conversion_steady",
                    displayName = "转换段稳态异常",
                    enabled = true,
                    type = "morphology",
                    parameters = new { referenceValue = 2.8, warningRatio = 1.3 }
                });
            }
            if (ruleNames == null || ruleNames.Length == 0 || Array.IndexOf(ruleNames, "lock_peak") >= 0)
            {
                allRules.Add(new
                {
                    name = "lock_peak",
                    displayName = "锁闭段峰值异常",
                    enabled = true,
                    type = "morphology",
                    parameters = new { referenceValue = 3.2, warningRatio = 1.3, alarmRatio = 1.5 }
                });
            }

            var config = new { rules = allRules };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(config,
                Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(Path.Combine(dir, "default_rules.json"), json, Encoding.UTF8);

            return dir;
        }

        static void CleanupTempDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { /* 忽略清理失败 */ }
        }

        /// <summary>
        /// 创建标准 SwitchActionData：
        /// sampleCount 个采样点，Phase "A"，Current = baseValue。
        /// </summary>
        static SwitchActionData MakeDataWithSamples(int sampleCount, int sampleRate, float currentValue)
        {
            var data = new SwitchActionData
            {
                StationName = "测试站",
                SwitchId = "SW_01",
                StartTime = 1700000000L,
                EndTime = 1700000000L + (long)(sampleCount / (float)sampleRate),
                Direction = "定位→反位",
                SampleRate = sampleRate,
                SampleCount = sampleCount,
                Samples = new List<SamplePoint>()
            };

            for (int i = 0; i < sampleCount; i++)
            {
                data.Samples.Add(new SamplePoint
                {
                    Index = i,
                    Timestamp = data.StartTime + (long)(i / (float)sampleRate),
                    Phase = "A",
                    Current = currentValue,
                    Voltage = 380f,
                    Power = currentValue * 380f
                });
            }

            return data;
        }

        /// <summary>
        /// 创建普通的正常数据（145 采样点, 25Hz → 5.8s → 正好匹配参考值）。
        /// </summary>
        static SwitchActionData MakeNormalData()
        {
            return MakeDataWithSamples(145, 25, 2.5f);
        }

        /// <summary>
        /// 创建具有不同段特征的道岔动作数据。
        /// sampleCount 总采样点数, sampleRate 采样率,
        /// unlockPeak 前15%的峰值, midValue 中间段的恒定值, lockPeak 后15%的峰值。
        /// </summary>
        static SwitchActionData MakeDataWithUnlockPeak(int sampleCount, int sampleRate,
            float unlockPeak, float midValue, float lockPeak)
        {
            var data = new SwitchActionData
            {
                StationName = "测试站",
                SwitchId = "SW_01",
                StartTime = 1700000000L,
                EndTime = 1700000000L + (long)(sampleCount / (float)sampleRate),
                Direction = "定位→反位",
                SampleRate = sampleRate,
                SampleCount = sampleCount,
                Samples = new List<SamplePoint>()
            };

            int unlockEnd = Math.Max(1, (int)(sampleCount * 0.15));
            int lockStart = Math.Max(unlockEnd + 1, (int)(sampleCount * 0.85));

            for (int i = 0; i < sampleCount; i++)
            {
                float current;
                if (i < unlockEnd)
                {
                    // 解锁段：从 0 渐变到 unlockPeak
                    float t = (float)i / unlockEnd;
                    current = unlockPeak * t;
                }
                else if (i >= lockStart)
                {
                    // 锁闭段：从 midValue 渐变到 lockPeak
                    float t = (float)(i - lockStart) / (sampleCount - lockStart);
                    current = midValue + (lockPeak - midValue) * t;
                }
                else
                {
                    // 转换段：恒定为 midValue
                    current = midValue;
                }

                data.Samples.Add(new SamplePoint
                {
                    Index = i,
                    Timestamp = data.StartTime + (long)(i / (float)sampleRate),
                    Phase = "A",
                    Current = current,
                    Voltage = 380f,
                    Power = current * 380f
                });
            }

            return data;
        }
    }
}
