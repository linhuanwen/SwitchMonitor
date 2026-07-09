using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// D4 Pipeline Integration 测试套件
    /// 验证: EventDiagnosis 存储 / alarms_index / diag.log / DiagnosisRunner / DiagnoseHook
    /// </summary>
    public static class D4Tests
    {
        public static void Run()
        {
            // ═══ Slice 1: EventDiagnosis POCO 序列化 ═══
            TestRunner.Test("EventDiagnosis 序列化为 JSON", Test_EventDiagnosis_Serialize);
            TestRunner.Test("EventDiagnosis 从 JSON 反序列化", Test_EventDiagnosis_Deserialize);
            TestRunner.Test("正常事件 results 为空列表", Test_EventDiagnosis_NormalEvent);

            // ═══ Slice 2: IndexManager 诊断存储 ═══
            TestRunner.Test("SaveDayDiagnosis 写入 .diag.json", Test_SaveDayDiagnosis_WritesFile);
            TestRunner.Test("LoadDayDiagnosis 读取 .diag.json", Test_LoadDayDiagnosis_ReadsFile);
            TestRunner.Test("LoadDayDiagnosis 文件缺失返回空列表", Test_LoadDayDiagnosis_MissingFile);
            TestRunner.Test("SaveDayDiagnosis 覆盖更新", Test_SaveDayDiagnosis_Overwrite);
            TestRunner.Test("alarms_index 写入并读取", Test_AlarmsIndex_ReadWrite);
            TestRunner.Test("alarms_index 三个键恒输出含0", Test_AlarmsIndex_ZeroKeys);
            TestRunner.Test("alarms_index 无异常日期不写入", Test_AlarmsIndex_NoNormalDates);

            // ═══ Slice 3: diag.log ═══
            TestRunner.Test("LogDiagnosis 写入 diag.log", Test_LogDiagnosis_Writes);
            TestRunner.Test("LogDiagnosis 包含时间戳和内容", Test_LogDiagnosis_Format);

            // ═══ Slice 4: DiagnosisRunner ═══
            TestRunner.Test("DiagnosisRunner.Run 正常事件", Test_DiagnosisRunner_NormalEvent);
            TestRunner.Test("DiagnosisRunner.Run 故障事件", Test_DiagnosisRunner_FaultEvent);

            // ═══ Slice 5: DataPipeline.DiagnoseHook ═══
            TestRunner.Test("DiagnoseHook 在 SaveDayData 后被调用", Test_DiagnoseHook_Called);
            TestRunner.Test("DiagnoseHook=null 时不调用", Test_DiagnoseHook_NullSafe);

            // ═══ Slice 6: AppConfig.Diagnosis ═══
            TestRunner.Test("AppConfig.Diagnosis 默认启用", Test_DiagnosisConfig_Defaults);
            TestRunner.Test("config.json 读取 diagnosis 节", Test_DiagnosisConfig_FromJson);
        }

        // ──────────────────────────────────────────────
        // Slice 1: EventDiagnosis POCO
        // ──────────────────────────────────────────────

        static void Test_EventDiagnosis_Serialize()
        {
            var diag = new EventDiagnosis
            {
                Timestamp = 1769618597,
                Level = "故障",
                Results = new List<DiagnosisItem>
                {
                    new DiagnosisItem
                    {
                        RuleId = "R1", RuleName = "动作超时/未完成", Level = "故障",
                        Description = "动作时长31.36s，超过参考11.72s+3.0s",
                        Value = 31.36, Reference = 11.72
                    }
                }
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(diag);

            // JavaScriptSerializer 使用 PascalCase（与现有 SwitchEvent 序列化一致）
            TestRunner.AssertTrue(json.Contains("\"Timestamp\""), "含 Timestamp");
            TestRunner.AssertTrue(json.Contains("1769618597"), "含 timestamp 值");
            TestRunner.AssertTrue(json.Contains("\"Level\""), "含 Level");
            TestRunner.AssertTrue(json.Contains("\"故障\""), "含 level 值");
            TestRunner.AssertTrue(json.Contains("\"Results\""), "含 Results");
            TestRunner.AssertTrue(json.Contains("\"RuleId\""), "含 RuleId");
            TestRunner.AssertTrue(json.Contains("\"R1\""), "含 R1");
        }

        static void Test_EventDiagnosis_Deserialize()
        {
            string json = @"[{""timestamp"":1769618597,""level"":""故障"",""results"":[{""ruleId"":""R1"",""ruleName"":""动作超时/未完成"",""level"":""故障"",""description"":""动作时长31.36s"",""value"":31.36,""reference"":11.72}]}]";

            var serializer = new JavaScriptSerializer();
            var list = serializer.Deserialize<List<EventDiagnosis>>(json);

            TestRunner.AssertNotNull(list, "反序列化结果");
            TestRunner.AssertEqual(1, list.Count, "事件数");
            TestRunner.AssertEqual(1769618597L, list[0].Timestamp, "Timestamp");
            TestRunner.AssertEqual("故障", list[0].Level, "Level");
            TestRunner.AssertEqual(1, list[0].Results.Count, "Results 数");
            TestRunner.AssertEqual("R1", list[0].Results[0].RuleId, "RuleId");
            TestRunner.AssertEqual(31.36, list[0].Results[0].Value, 0.001, "Value");
        }

        static void Test_EventDiagnosis_NormalEvent()
        {
            var diag = new EventDiagnosis
            {
                Timestamp = 1770922311,
                Level = "正常",
                Results = new List<DiagnosisItem>()
            };

            TestRunner.AssertEqual("正常", diag.Level, "正常事件 level");
            TestRunner.AssertEqual(0, diag.Results.Count, "正常事件 results 为空");
        }

        // ──────────────────────────────────────────────
        // Slice 2: IndexManager 诊断存储
        // ──────────────────────────────────────────────

        static void Test_SaveDayDiagnosis_WritesFile()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                var diagnoses = new List<EventDiagnosis>
                {
                    new EventDiagnosis { Timestamp = 1770922311, Level = "正常", Results = new List<DiagnosisItem>() },
                    new EventDiagnosis { Timestamp = 1769618597, Level = "故障",
                        Results = new List<DiagnosisItem> {
                            new DiagnosisItem { RuleId = "R1", RuleName = "动作超时/未完成", Level = "故障",
                                Description = "测试", Value = 31.36, Reference = 11.72 }
                        }
                    }
                };

                im.SaveDayDiagnosis("4-1", "2026-01-29", diagnoses);

                string expectedPath = Path.Combine(tempDir, "4-1", "2026-01-29.diag.json");
                TestRunner.AssertFileExists(expectedPath, ".diag.json 文件");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_LoadDayDiagnosis_ReadsFile()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                var diagnoses = new List<EventDiagnosis>
                {
                    new EventDiagnosis { Timestamp = 1770922311, Level = "正常", Results = new List<DiagnosisItem>() },
                    new EventDiagnosis { Timestamp = 1769618597, Level = "故障",
                        Results = new List<DiagnosisItem> {
                            new DiagnosisItem { RuleId = "R1", RuleName = "动作超时/未完成", Level = "故障",
                                Description = "测试", Value = 31.36, Reference = 11.72 }
                        }
                    }
                };

                im.SaveDayDiagnosis("4-1", "2026-01-29", diagnoses);

                var loaded = im.LoadDayDiagnosis("4-1", "2026-01-29");

                TestRunner.AssertEqual(2, loaded.Count, "加载事件数");
                TestRunner.AssertEqual(1770922311L, loaded[0].Timestamp, "事件1 timestamp");
                TestRunner.AssertEqual("正常", loaded[0].Level, "事件1 level");
                TestRunner.AssertEqual(1769618597L, loaded[1].Timestamp, "事件2 timestamp");
                TestRunner.AssertEqual("故障", loaded[1].Level, "事件2 level");
                TestRunner.AssertEqual(1, loaded[1].Results.Count, "事件2 results 数");
                TestRunner.AssertEqual("R1", loaded[1].Results[0].RuleId, "事件2 ruleId");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_LoadDayDiagnosis_MissingFile()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                var loaded = im.LoadDayDiagnosis("1-1", "2099-01-01");
                TestRunner.AssertNotNull(loaded, "缺失文件应返回空列表非null");
                TestRunner.AssertEqual(0, loaded.Count, "缺失文件返回空列表");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_SaveDayDiagnosis_Overwrite()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                // 第一次写入
                var first = new List<EventDiagnosis> {
                    new EventDiagnosis { Timestamp = 1, Level = "正常", Results = new List<DiagnosisItem>() }
                };
                im.SaveDayDiagnosis("1-1", "2026-01-01", first);

                // 第二次写入（覆盖）
                var second = new List<EventDiagnosis> {
                    new EventDiagnosis { Timestamp = 2, Level = "故障",
                        Results = new List<DiagnosisItem> {
                            new DiagnosisItem { RuleId = "R1", RuleName = "动作超时", Level = "故障",
                                Description = "x", Value = 10, Reference = 5 }
                        }
                    }
                };
                im.SaveDayDiagnosis("1-1", "2026-01-01", second);

                var loaded = im.LoadDayDiagnosis("1-1", "2026-01-01");
                TestRunner.AssertEqual(1, loaded.Count, "覆盖后只有新数据");
                TestRunner.AssertEqual(2L, loaded[0].Timestamp, "覆盖后 timestamp");
                TestRunner.AssertEqual("故障", loaded[0].Level, "覆盖后 level");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_AlarmsIndex_ReadWrite()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                // 写入一次诊断，触发 alarms_index 更新
                var diagnoses = new List<EventDiagnosis>
                {
                    new EventDiagnosis { Timestamp = 1, Level = "预警", Results = new List<DiagnosisItem>() },
                    new EventDiagnosis { Timestamp = 2, Level = "故障", Results = new List<DiagnosisItem>() },
                    new EventDiagnosis { Timestamp = 3, Level = "故障", Results = new List<DiagnosisItem>() },
                };
                im.SaveDayDiagnosis("4-1", "2026-01-29", diagnoses);

                var index = im.LoadAlarmsIndex();
                TestRunner.AssertNotNull(index, "alarms_index 非null");
                TestRunner.AssertTrue(index.ContainsKey("4-1"), "含 4-1");
                TestRunner.AssertTrue(index["4-1"].ContainsKey("2026-01-29"), "含日期 2026-01-29");

                var counts = index["4-1"]["2026-01-29"];
                TestRunner.AssertTrue(counts.ContainsKey("预警"), "含 预警 键");
                TestRunner.AssertTrue(counts.ContainsKey("报警"), "含 报警 键");
                TestRunner.AssertTrue(counts.ContainsKey("故障"), "含 故障 键");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_AlarmsIndex_ZeroKeys()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                // 仅有正常事件
                var diagnoses = new List<EventDiagnosis>
                {
                    new EventDiagnosis { Timestamp = 1, Level = "正常", Results = new List<DiagnosisItem>() },
                };
                im.SaveDayDiagnosis("1-1", "2026-02-13", diagnoses);

                var index = im.LoadAlarmsIndex();
                // 全是正常事件 → 该日期不应出现
                TestRunner.AssertFalse(index.ContainsKey("1-1") && index["1-1"].ContainsKey("2026-02-13"),
                    "全正常日期不在 alarms_index 中");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_AlarmsIndex_NoNormalDates()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                // 混合：有正常 + 预警
                var diagnoses = new List<EventDiagnosis>
                {
                    new EventDiagnosis { Timestamp = 1, Level = "正常", Results = new List<DiagnosisItem>() },
                    new EventDiagnosis { Timestamp = 2, Level = "预警", Results = new List<DiagnosisItem>() },
                };
                im.SaveDayDiagnosis("1-1", "2026-02-13", diagnoses);

                var index = im.LoadAlarmsIndex();
                TestRunner.AssertTrue(index.ContainsKey("1-1"), "含 1-1");
                TestRunner.AssertTrue(index["1-1"].ContainsKey("2026-02-13"), "含日期");

                var counts = index["1-1"]["2026-02-13"];
                // 三个键恒输出含0
                TestRunner.AssertEqual(1, counts["预警"], "预警=1");
                TestRunner.AssertEqual(0, counts["报警"], "报警=0");
                TestRunner.AssertEqual(0, counts["故障"], "故障=0");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 3: diag.log
        // ──────────────────────────────────────────────

        static void Test_LogDiagnosis_Writes()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string originalDir = Logger.LogDir;
                Logger.LogDir = tempDir;

                Logger.LogDiagnosis("测试诊断日志条目");

                string logPath = Path.Combine(tempDir, "diag.log");
                TestRunner.AssertFileExists(logPath, "diag.log 文件存在");

                string content = File.ReadAllText(logPath, Encoding.UTF8);
                TestRunner.AssertTrue(content.Contains("测试诊断日志条目"), "日志内容正确");

                Logger.LogDir = originalDir;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_LogDiagnosis_Format()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string originalDir = Logger.LogDir;
                Logger.LogDir = tempDir;

                Logger.LogDiagnosis("switchId=4-1 eventTs=1769618597 → 故障");

                string logPath = Path.Combine(tempDir, "diag.log");
                string content = File.ReadAllText(logPath, Encoding.UTF8);

                // 每行应以时间戳开头 [yyyy-MM-dd HH:mm:ss]
                TestRunner.AssertTrue(content.StartsWith("["), "以 [ 开头");
                TestRunner.AssertTrue(content.Contains("] switchId=4-1"), "含 switchId");
                TestRunner.AssertTrue(content.Contains("→ 故障"), "含诊断结论");

                Logger.LogDir = originalDir;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 4: DiagnosisRunner
        // ──────────────────────────────────────────────

        /// <summary>
        /// 测试用诊断引擎：始终返回正常
        /// </summary>
        class StubNormalEngine : IDiagnosisEngine
        {
            public void Initialize(string rulesDir) { }
            public List<DiagnosisResult> Diagnose(string switchId, CurveFeatures features)
            {
                return new List<DiagnosisResult>(); // 正常
            }
        }

        /// <summary>
        /// 测试用诊断引擎：始终返回 R1 故障
        /// </summary>
        class StubFaultEngine : IDiagnosisEngine
        {
            public void Initialize(string rulesDir) { }
            public List<DiagnosisResult> Diagnose(string switchId, CurveFeatures features)
            {
                return new List<DiagnosisResult>
                {
                    new DiagnosisResult
                    {
                        RuleId = "R1", RuleName = "动作超时/未完成", Level = DiagnosisLevel.Fault,
                        Description = "动作时长31.36s，超过参考11.72s+3.0s",
                        Value = 31.36, Reference = 11.72
                    }
                };
            }
        }

        static void Test_DiagnosisRunner_NormalEvent()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string originalDir = Logger.LogDir;
                Logger.LogDir = tempDir;

                var engine = new StubNormalEngine();
                var evt = new SwitchEvent
                {
                    Timestamp = 1770922311,
                    DateTimeStr = "2026-02-13 02:51:51",
                    Power = new List<double[]>
                    {
                        new double[] { 0.00, 0.500 },
                        new double[] { 0.04, 0.800 },
                        new double[] { 0.08, 1.200 },
                        new double[] { 0.12, 0.600 }
                    }
                };

                var result = DiagnosisRunner.Run(engine, "1-1", evt);

                TestRunner.AssertNotNull(result, "返回非null");
                TestRunner.AssertEqual(1770922311L, result.Timestamp, "Timestamp");
                TestRunner.AssertEqual("正常", result.Level, "综合级别=正常");
                TestRunner.AssertEqual(0, result.Results.Count, "无命中规则");

                Logger.LogDir = originalDir;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_DiagnosisRunner_FaultEvent()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string originalDir = Logger.LogDir;
                Logger.LogDir = tempDir;

                var engine = new StubFaultEngine();
                var evt = new SwitchEvent
                {
                    Timestamp = 1769618597,
                    DateTimeStr = "2026-01-29 00:43:17",
                    Power = new List<double[]>
                    {
                        new double[] { 0.00, 4.353 },
                        new double[] { 0.04, 0.545 }
                    }
                };

                var result = DiagnosisRunner.Run(engine, "4-1", evt);

                TestRunner.AssertNotNull(result, "返回非null");
                TestRunner.AssertEqual(1769618597L, result.Timestamp, "Timestamp");
                TestRunner.AssertEqual("故障", result.Level, "综合级别=故障");
                TestRunner.AssertEqual(1, result.Results.Count, "1条命中规则");
                TestRunner.AssertEqual("R1", result.Results[0].RuleId, "命中 R1");

                // 验证 diag.log 被写入
                string logPath = Path.Combine(tempDir, "diag.log");
                TestRunner.AssertFileExists(logPath, "diag.log 已写入");

                Logger.LogDir = originalDir;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 5: DataPipeline.DiagnoseHook
        // ──────────────────────────────────────────────

        static void Test_DiagnoseHook_Called()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                // 创建最小 CSV 数据（单事件）
                string csvDir = Path.Combine(tempDir, "csv");
                Directory.CreateDirectory(csvDir);

                // 文件0: SwitchCurve(0).csv — 配对中的第一个（A/B相电流）
                string csv0 = Path.Combine(csvDir, "SwitchCurve(0).csv");
                File.WriteAllText(csv0,
                    "timestamp,datetime,phase,s0,s1,s2,s3,s4,s5,s6,s7,s8,s9,s10,s11,s12,s13,s14,s15\n" +
                    "1770922311,2026-02-13 02:51:51,16777216,0.5,0.6,0.7,0.8,0.9,1.0,1.1,1.2,1.1,1.0,0.9,0.8,0.7,0.6,0.5,0.4\n",
                    Encoding.UTF8);

                // 文件3: SwitchCurve(3).csv — 功率
                string csv3 = Path.Combine(csvDir, "SwitchCurve(3).csv");
                File.WriteAllText(csv3,
                    "timestamp,datetime,phase,s0,s1,s2,s3,s4,s5,s6,s7,s8,s9,s10,s11,s12,s13,s14,s15\n" +
                    "1770922311,2026-02-13 02:51:51,0,0.500,0.800,1.200,1.000,0.800,0.600,0.500,0.400,0.350,0.300,0.280,0.260,0.250,0.240,0.230,0.220\n",
                    Encoding.UTF8);

                var config = new AppConfig
                {
                    SwitchGroups = new List<SwitchGroup>
                    {
                        new SwitchGroup { Id = "1-1", Label = "1-1", DataFileIndex = 0 }
                    },
                    ParsedDataDir = "parsed_data"
                };

                string parsedDir = Path.Combine(tempDir, "parsed_data");
                Directory.CreateDirectory(parsedDir);

                // 更改工作目录为 tempDir（DataPipeline 相对路径依赖）
                var im = new IndexManager(parsedDir);
                im.Initialize();

                var pipeline = new DataPipeline(config, im);

                int hookCallCount = 0;
                string lastSwitchId = null;
                EventDiagnosis lastResult = null;

                pipeline.DiagnoseHook = (switchId, evt) =>
                {
                    hookCallCount++;
                    lastSwitchId = switchId;
                    lastResult = new EventDiagnosis
                    {
                        Timestamp = evt.Timestamp,
                        Level = "正常",
                        Results = new List<DiagnosisItem>()
                    };
                    return lastResult;
                };

                pipeline.ImportAll(csvDir);

                TestRunner.AssertEqual(1, hookCallCount, "DiagnoseHook 被调用1次");
                TestRunner.AssertEqual("1-1", lastSwitchId, "switchId 正确");
                TestRunner.AssertNotNull(lastResult, "返回 EventDiagnosis 非null");

                // 验证 .diag.json 被生成
                string diagPath = Path.Combine(parsedDir, "1-1", "2026-02-13.diag.json");
                TestRunner.AssertFileExists(diagPath, ".diag.json 由 pipeline 生成");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_DiagnoseHook_NullSafe()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string csvDir = Path.Combine(tempDir, "csv");
                Directory.CreateDirectory(csvDir);

                string csv0 = Path.Combine(csvDir, "SwitchCurve(0).csv");
                File.WriteAllText(csv0,
                    "timestamp,datetime,phase,s0,s1,s2,s3,s4,s5\n" +
                    "1770922311,2026-02-13 02:51:51,16777216,0.5,0.6,0.7,0.8,0.9,1.0\n",
                    Encoding.UTF8);

                var config = new AppConfig
                {
                    SwitchGroups = new List<SwitchGroup>
                    {
                        new SwitchGroup { Id = "1-1", Label = "1-1", DataFileIndex = 0 }
                    },
                    ParsedDataDir = "parsed_data"
                };

                string parsedDir = Path.Combine(tempDir, "parsed_data");
                var im = new IndexManager(parsedDir);
                im.Initialize();

                var pipeline = new DataPipeline(config, im);
                // 不设置 DiagnoseHook → 应为 null

                // 不应抛异常
                pipeline.ImportAll(csvDir);

                // .diag.json 不应生成（没有 hook）
                string diagPath = Path.Combine(parsedDir, "1-1", "2026-02-13.diag.json");
                TestRunner.AssertFalse(File.Exists(diagPath), "无 hook 时不生成 .diag.json");

                TestRunner.AssertTrue(pipeline.TotalEventsImported > 0, "导入正常完成");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 6: AppConfig.Diagnosis
        // ──────────────────────────────────────────────

        static void Test_DiagnosisConfig_Defaults()
        {
            var config = new AppConfig();
            TestRunner.AssertNotNull(config.Diagnosis, "Diagnosis 节非null");
            TestRunner.AssertTrue(config.Diagnosis.Enabled, "默认启用");
            TestRunner.AssertEqual("Rules", config.Diagnosis.RulesDir, "默认 Rules 目录");
        }

        static void Test_DiagnosisConfig_FromJson()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                string json = @"{
                    ""SwitchGroups"": [],
                    ""DataSourceDir"": ""./data"",
                    ""ParsedDataDir"": ""./parsed_data"",
                    ""ScanInterval"": 5,
                    ""diagnosis"": {
                        ""enabled"": false,
                        ""rulesDir"": ""MyRules""
                    }
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);
                TestRunner.AssertNotNull(config.Diagnosis, "Diagnosis 节被解析");
                TestRunner.AssertFalse(config.Diagnosis.Enabled, "enabled=false");
                TestRunner.AssertEqual("MyRules", config.Diagnosis.RulesDir, "自定义 rulesDir");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
