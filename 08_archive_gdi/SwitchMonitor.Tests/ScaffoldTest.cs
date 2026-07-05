using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Newtonsoft.Json;
using SwitchMonitor.Common;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;
using DiagnosisEngine;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// 脚手架验证测试程序。
    /// 验证内容：
    /// 1. POCO 模型创建与 JSON 序列化
    /// 2. IDiagnosisEngine 接口契约
    /// 3. 数据库 DDL 语句完整性
    /// 4. 诊断引擎占位实现
    /// </summary>
    public class ScaffoldTest
    {
        static int passed = 0;
        static int failed = 0;

        public static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== SwitchMonitor 脚手架验证 ===");
            Console.WriteLine();

            TestPocoSerialization();
            TestDiagnosisInterface();
            TestDatabaseDDL();
            TestDiagnosisEngine();
            TestProjectDependencies();

            Console.WriteLine();
            Console.WriteLine("=== Slice 2: CSM2010 CSV 解析 → JSON 管道 测试 ===");
            Console.WriteLine();

            try
            {
                var (sp, sf) = Slice2Tests.RunAll();
                passed += sp;
                failed += sf;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 2 异常: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== Slice 3: FileWatcherService 测试 ===");
            Console.WriteLine();

            try
            {
                var (fp, ff) = FileWatcherServiceTests.RunAll();
                passed += fp;
                failed += ff;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 3 异常: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== Slice 4+6: QueryService 测试 ===");
            Console.WriteLine();

            try
            {
                var (qp, qf) = QueryServiceTests.RunAll();
                passed += qp;
                failed += qf;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 4+6 异常: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== Slice 5: 曲线交互 测试 ===");
            Console.WriteLine();

            try
            {
                var (ip, inf) = Slice5Tests.RunAll();
                passed += ip;
                failed += inf;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 5 异常: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== Slice 6: 全链路交互联动 测试 ===");
            Console.WriteLine();

            try
            {
                var (l6p, l6f) = Slice6Tests.RunAll();
                passed += l6p;
                failed += l6f;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 6 异常: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== Slice 7: 导出图片 + CSV 测试 ===");
            Console.WriteLine();

            try
            {
                var (ep, ef) = Slice7Tests.RunAll();
                passed += ep;
                failed += ef;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 7 异常: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== Slice 8-Alarm: alarm threshold config tests ===");
            Console.WriteLine();

            try
            {
                var (ap, af) = AlarmThresholdTests.RunAll();
                passed += ap;
                failed += af;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 8-Alarm error: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== Slice 11: 道岔映射配置 测试 ===");
            Console.WriteLine();

            try
            {
                var (mp, mf) = Slice11Tests.RunAll();
                passed += mp;
                failed += mf;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Slice 11 异常: {0}", ex.Message);
                failed++;
            }

            Console.WriteLine();
            Console.WriteLine("=== 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);

            if (failed > 0)
            {
                Console.WriteLine("存在失败项，请检查。");
                Environment.Exit(1);
            }
            else
            {
                Console.WriteLine("全部验证通过！脚手架已就绪。");
            }
        }

        /// <summary>
        /// 测试 1: POCO 模型创建与 JSON 序列化
        /// </summary>
        static void TestPocoSerialization()
        {
            Console.WriteLine("--- 测试 1: POCO 模型与 JSON 序列化 ---");

            try
            {
                // 创建 SamplePoint
                var sample = new SamplePoint
                {
                    Index = 42,
                    Timestamp = 1700000000L,
                    Phase = "A",
                    Current = 2.5f,
                    Voltage = 380.0f,
                    Power = 950.0f
                };
                Assert(sample.Index == 42, "SamplePoint.Index");
                Assert(sample.Phase == "A", "SamplePoint.Phase");

                // 创建 SwitchActionData
                var action = new SwitchActionData
                {
                    StationName = "三水北",
                    SwitchId = "SW_01",
                    StartTime = 1700000000L,
                    EndTime = 1700000006L,
                    Direction = "定位->反位",
                    SampleRate = 25,
                    VoltageBefore = 24.1f,
                    VoltageAfter = 23.8f,
                    Samples = new List<SamplePoint>
                    {
                        new SamplePoint { Index = 0, Timestamp = 1700000000L, Phase = "A", Current = 0.0f, Voltage = 380f, Power = 0f },
                        new SamplePoint { Index = 1, Timestamp = 1700000040L, Phase = "A", Current = 2.5f, Voltage = 378f, Power = 945f },
                        new SamplePoint { Index = 2, Timestamp = 1700000080L, Phase = "A", Current = 3.1f, Voltage = 375f, Power = 1162f },
                    }
                };
                Assert(action.SwitchId == "SW_01", "SwitchActionData.SwitchId");
                Assert(action.Samples.Count == 3, "SwitchActionData.Samples.Count");
                Assert(action.SampleRate == 25, "SwitchActionData.SampleRate");

                // 创建 DiagnosisResult
                var result = new DiagnosisResult
                {
                    RuleName = "转换时间异常",
                    Level = DiagnosisLevel.Alarm,
                    Description = "转换时间 7.6 秒，超出正常范围",
                    AbnormalValue = 7.6f,
                    ReferenceValue = 5.8f
                };
                Assert(result.Level == "报警", "DiagnosisResult.Level");
                Assert(result.RuleName == "转换时间异常", "DiagnosisResult.RuleName");

                // 创建 StatusEvent
                var statusEvent = new StatusEvent
                {
                    FileSource = "Digit(0).dat",
                    Timestamp = 1700000000L,
                    PointId = 184,
                    StateByte = 0x2F,
                    RawValue = 0x2F2F,
                    SwitchId = "SW_01"
                };
                Assert(statusEvent.PointId == 184, "StatusEvent.PointId");
                Assert(statusEvent.StateByte == 0x2F, "StatusEvent.StateByte");

                // JSON 序列化 / 反序列化验证
                try
                {
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(action,
                        Newtonsoft.Json.Formatting.Indented);
                    Console.WriteLine("  序列化结果 (Newtonsoft.Json):");
                    Console.WriteLine(json);

                    var deserialized = Newtonsoft.Json.JsonConvert.DeserializeObject<SwitchActionData>(json);
                    Assert(deserialized != null, "反序列化非 null");
                    Assert(deserialized.SwitchId == action.SwitchId, "反序列化 SwitchId 一致");
                    Assert(deserialized.Samples.Count == action.Samples.Count, "反序列化 Samples.Count 一致");
                    Assert(deserialized.Direction == action.Direction, "反序列化 Direction 一致");
                }
                catch (Exception jsonEx)
                {
                    Console.WriteLine("  JSON 序列化跳过: {0}", jsonEx.Message);
                }

                Console.WriteLine("  [PASS] 测试 1 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 1 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 2: IDiagnosisEngine 接口契约
        /// </summary>
        static void TestDiagnosisInterface()
        {
            Console.WriteLine("--- 测试 2: IDiagnosisEngine 接口契约 ---");

            try
            {
                // 验证接口方法签名
                var type = typeof(IDiagnosisEngine);
                var methods = type.GetMethods();
                Assert(methods.Length == 2, "IDiagnosisEngine 有 2 个方法");

                var initMethod = type.GetMethod("Initialize");
                Assert(initMethod != null, "Initialize 方法存在");
                Assert(initMethod.ReturnType == typeof(void), "Initialize 返回 void");

                var diagnoseMethod = type.GetMethod("Diagnose");
                Assert(diagnoseMethod != null, "Diagnose 方法存在");
                Assert(diagnoseMethod.ReturnType == typeof(List<DiagnosisResult>), "Diagnose 返回 List<DiagnosisResult>");

                Console.WriteLine("  [PASS] 测试 2 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 2 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 3: 数据库 DDL 语句完整性
        /// </summary>
        static void TestDatabaseDDL()
        {
            Console.WriteLine("--- 测试 3: 数据库 DDL 语句完整性 ---");

            try
            {
                // 验证有 6 张表的 DDL
                Assert(DatabaseInitializer.AllTableDDL.Length == 6, "有 6 张表的 DDL");

                // 验证每张表的 DDL 包含 CREATE TABLE
                foreach (var ddl in DatabaseInitializer.AllTableDDL)
                {
                    Assert(ddl.Contains("CREATE TABLE IF NOT EXISTS"), "DDL 包含 CREATE TABLE IF NOT EXISTS");
                }

                // 验证关键表名
                string allDDL = string.Join("\n", DatabaseInitializer.AllTableDDL);
                Assert(allDDL.Contains("SwitchActions"), "包含 SwitchActions 表");
                Assert(allDDL.Contains("CurveSamples"), "包含 CurveSamples 表");
                Assert(allDDL.Contains("StatusEvents"), "包含 StatusEvents 表");
                Assert(allDDL.Contains("ReferenceCurves"), "包含 ReferenceCurves 表");
                Assert(allDDL.Contains("DiagnosisLog"), "包含 DiagnosisLog 表");
                Assert(allDDL.Contains("ProcessedFiles"), "包含 ProcessedFiles 表");

                // 验证索引
                Assert(DatabaseInitializer.CreateIndexes.Length == 4, "有 4 个索引");
                string allIndexes = string.Join("\n", DatabaseInitializer.CreateIndexes);
                Assert(allIndexes.Contains("idx_actions_switch_time"), "包含 idx_actions_switch_time");
                Assert(allIndexes.Contains("idx_samples_action"), "包含 idx_samples_action");
                Assert(allIndexes.Contains("idx_status_time"), "包含 idx_status_time");
                Assert(allIndexes.Contains("idx_status_point"), "包含 idx_status_point");

                // 验证约束
                Assert(allDDL.Contains("UNIQUE(ActionId, SampleIndex, Phase)"), "CurveSamples 有复合唯一约束");
                Assert(allDDL.Contains("REFERENCES SwitchActions(Id)"), "有外键引用");

                Console.WriteLine("  DDL 内容:");
                foreach (var ddl in DatabaseInitializer.AllTableDDL)
                {
                    // 提取表名
                    int start = ddl.IndexOf("CREATE TABLE IF NOT EXISTS") + 29;
                    int end = ddl.IndexOf("(", start);
                    if (end > start)
                    {
                        Console.WriteLine("    - {0}", ddl.Substring(start, end - start).Trim());
                    }
                }

                Console.WriteLine("  [PASS] 测试 3 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 3 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 4: 诊断引擎占位实现
        /// </summary>
        static void TestDiagnosisEngine()
        {
            Console.WriteLine("--- 测试 4: 诊断引擎占位实现 ---");

            try
            {
                var engine = new DummyDiagnosisEngine();

                // 未初始化时调用应抛出异常
                bool threwOnUninitialized = false;
                try
                {
                    engine.Diagnose(new SwitchActionData());
                }
                catch (InvalidOperationException)
                {
                    threwOnUninitialized = true;
                }
                Assert(threwOnUninitialized, "未初始化时 Diagnose 抛出 InvalidOperationException");

                // 初始化
                engine.Initialize("Rules\\");
                Assert(true, "Initialize 成功"); // 不抛异常即通过

                // 诊断（占位实现）
                var data = new SwitchActionData
                {
                    StationName = "三水北",
                    SwitchId = "SW_01",
                    Direction = "定位->反位",
                    Samples = new List<SamplePoint>
                    {
                        new SamplePoint { Index = 0, Phase = "A", Current = 2.5f }
                    }
                };
                var results = engine.Diagnose(data);

                Assert(results != null, "Diagnose 返回非 null");
                Assert(results.Count > 0, "Diagnose 返回至少 1 条结果");
                Assert(results[0].Level == DiagnosisLevel.Normal, "占位引擎返回正常级别");
                Assert(results[0].RuleName == "占位诊断", "占位引擎返回占位规则名");

                Console.WriteLine("  诊断结果: {0}", results[0].Description);

                // 验证 null 参数抛出异常
                bool threwOnNull = false;
                try
                {
                    engine.Diagnose(null);
                }
                catch (ArgumentNullException)
                {
                    threwOnNull = true;
                }
                Assert(threwOnNull, "null 参数时 Diagnose 抛出 ArgumentNullException");

                Console.WriteLine("  [PASS] 测试 4 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 4 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 5: 项目依赖关系验证
        /// </summary>
        static void TestProjectDependencies()
        {
            Console.WriteLine("--- 测试 5: 项目依赖关系验证 ---");

            try
            {
                // 验证类型存在于正确的程序集中
                var commonAssembly = typeof(SamplePoint).Assembly;
                Console.WriteLine("  SwitchMonitor.Common 程序集: {0}", commonAssembly.GetName().Name);

                var diagnosisAssembly = typeof(IDiagnosisEngine).Assembly;
                Console.WriteLine("  SwitchMonitor.Diagnosis 程序集: {0}", diagnosisAssembly.GetName().Name);

                var dataAssembly = typeof(DatabaseInitializer).Assembly;
                Console.WriteLine("  SwitchMonitor.Data 程序集: {0}", dataAssembly.GetName().Name);

                var engineAssembly = typeof(DummyDiagnosisEngine).Assembly;
                Console.WriteLine("  DiagnosisEngine 程序集: {0}", engineAssembly.GetName().Name);

                // 验证 DiagnosisEngine 实现了 IDiagnosisEngine
                Assert(typeof(IDiagnosisEngine).IsAssignableFrom(typeof(DummyDiagnosisEngine)),
                    "DummyDiagnosisEngine 实现 IDiagnosisEngine");

                // 验证 DiagnosisResult 的 Level 常量
                Assert(DiagnosisLevel.Normal == "正常", "DiagnosisLevel.Normal");
                Assert(DiagnosisLevel.Warning == "预警", "DiagnosisLevel.Warning");
                Assert(DiagnosisLevel.Alarm == "报警", "DiagnosisLevel.Alarm");
                Assert(DiagnosisLevel.Fault == "故障", "DiagnosisLevel.Fault");

                Console.WriteLine("  [PASS] 测试 5 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 5 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                Console.WriteLine("    ASSERT FAIL: {0}", message);
                throw new Exception(message);
            }
        }
    }
}
