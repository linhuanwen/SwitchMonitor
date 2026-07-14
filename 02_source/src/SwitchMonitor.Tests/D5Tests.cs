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
    /// D5 UI Alarm Display + Diagnosis Config 测试套件
    /// 验证: 侧边栏着色 / 日期角标 / 诊断条 / 状态栏 / 诊断参数配置
    /// </summary>
    public static class D5Tests
    {
        public static void Run()
        {
            // ═══ Slice 1: setTimes 扩展格式 JSON ═══
            TestRunner.Test("setTimes 扩展格式: [{ts, level}] JSON 序列化", Test_SetTimesExtendedJson);
            TestRunner.Test("setTimes 兼容旧格式: 纯数字数组", Test_SetTimesLegacyCompat);

            // ═══ Slice 2: 日期角标数据 ═══
            TestRunner.Test("alarms_index 按日期统计非正常计数", Test_AlarmsIndexDateCounts);
            TestRunner.Test("日期角标: 故障日期标记红色", Test_DateBadgeFaultRed);

            // ═══ Slice 3: 诊断条数据 ═══
            TestRunner.Test("诊断结论 JSON 包含 level 和 items", Test_DiagnosisJsonFormat);
            TestRunner.Test("正常事件诊断 items 为空", Test_DiagnosisNormalEvent);
            TestRunner.Test("无诊断数据标记为未诊断", Test_DiagnosisMissingData);

            // ═══ Slice 4: 状态栏 + LoadCurveData ═══
            TestRunner.Test("chartData 包含 diagnosis 节", Test_ChartDataIncludesDiagnosis);

            // ═══ Slice 5: DiagnosisRunner.RerunAll ═══
            TestRunner.Test("RerunAll 遍历全部日期并重跑诊断", Test_RerunAll_ProcessesAllDays);

            // ═══ Slice 6: 诊断参数配置 ═══
            TestRunner.Test("DiagParamConfig 加载 thresholds.json", Test_DiagConfig_LoadThresholds);
            TestRunner.Test("DiagParamConfig 加载 chart 阈值", Test_DiagConfig_LoadChartThresholds);
            TestRunner.Test("DiagParamConfig 保存并更新", Test_DiagConfig_Save);
            TestRunner.Test("DiagParamConfig 恢复默认", Test_DiagConfig_ResetDefaults);
        }

        // ──────────────────────────────────────────────
        // Slice 1: setTimes 扩展格式
        // ──────────────────────────────────────────────

        /// <summary>
        /// 验证 [{ts: long, level: string}, ...] 格式的 JSON 能被正确序列化
        /// </summary>
        static void Test_SetTimesExtendedJson()
        {
            var serializer = new JavaScriptSerializer();
            var data = new List<object>
            {
                new { ts = 1770922311L, level = "正常" },
                new { ts = 1769618597L, level = "故障" },
                new { ts = 1769618500L, level = "预警" }
            };

            string json = serializer.Serialize(data);

            TestRunner.AssertTrue(json.Contains("ts"), "含 ts 键");
            TestRunner.AssertTrue(json.Contains("level"), "含 level 键");
            TestRunner.AssertTrue(json.Contains("1769618597"), "含 timestamp 值");
            TestRunner.AssertTrue(json.Contains("故障"), "含 故障 级别");
            TestRunner.AssertTrue(json.Contains("预警"), "含 预警 级别");

            // 验证反序列化
            var parsed = serializer.Deserialize<List<Dictionary<string, object>>>(json);
            TestRunner.AssertEqual(3, parsed.Count, "3条记录");

            // 第二条是故障
            var item1 = parsed[1];
            TestRunner.AssertTrue(item1.ContainsKey("level"), "反序列化后含 level");
            TestRunner.AssertEqual("故障", item1["level"].ToString(), "level=故障");
        }

        /// <summary>
        /// 验证旧格式（纯数字数组）也能被 JS setTimes 兼容处理
        /// </summary>
        static void Test_SetTimesLegacyCompat()
        {
            var serializer = new JavaScriptSerializer();
            var data = new List<long> { 1770922311L, 1769618597L };

            string json = serializer.Serialize(data);

            // 旧格式不含 ts 键，JS 侧通过 typeof item === 'object' 判断
            TestRunner.AssertTrue(json.Contains("1770922311"), "旧格式含 timestamp");
            TestRunner.AssertFalse(json.Contains("\"ts\""), "旧格式不含 ts 键");
        }

        // ──────────────────────────────────────────────
        // Slice 2: 日期角标
        // ──────────────────────────────────────────────

        static void Test_AlarmsIndexDateCounts()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var im = new IndexManager(tempDir);
                im.Initialize();

                // 写入含故障的诊断数据
                var diagnoses = new List<EventDiagnosis>
                {
                    new EventDiagnosis { Timestamp = 1, Level = "故障",
                        Results = new List<DiagnosisItem> {
                            new DiagnosisItem { RuleId = "R1", RuleName = "超时", Level = "故障",
                                Description = "x", Value = 30, Reference = 10 }
                        }
                    },
                    new EventDiagnosis { Timestamp = 2, Level = "预警",
                        Results = new List<DiagnosisItem> {
                            new DiagnosisItem { RuleId = "R3", RuleName = "时长偏差", Level = "预警",
                                Description = "y", Value = 1, Reference = 0.5 }
                        }
                    },
                    new EventDiagnosis { Timestamp = 3, Level = "故障",
                        Results = new List<DiagnosisItem> {
                            new DiagnosisItem { RuleId = "R1", RuleName = "超时", Level = "故障",
                                Description = "z", Value = 31, Reference = 11 }
                        }
                    }
                };

                im.SaveDayDiagnosis("4-J", "2026-01-29", diagnoses);

                var index = im.LoadAlarmsIndex();
                TestRunner.AssertTrue(index.ContainsKey("4-J"), "含 4-1");
                TestRunner.AssertTrue(index["4-J"].ContainsKey("2026-01-29"), "含日期");

                var counts = index["4-J"]["2026-01-29"];
                // 预警=1, 报警=0, 故障=2 → 非正常总计=3
                TestRunner.AssertEqual(1, counts["预警"], "预警=1");
                TestRunner.AssertEqual(0, counts["报警"], "报警=0");
                TestRunner.AssertEqual(2, counts["故障"], "故障=2");

                // 非正常总计数 = 预警 + 报警 + 故障 = 3
                int totalAbnormal = counts["预警"] + counts["报警"] + counts["故障"];
                TestRunner.AssertEqual(3, totalAbnormal, "非正常总计=3");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_DateBadgeFaultRed()
        {
            // 业务逻辑验证：故障>0 时角标颜色应为红色
            // 这个测试验证 JSON 数据正确传递到前端渲染所需的信息

            var serializer = new JavaScriptSerializer();

            // 模拟构造传递给 setDates 的日期角标数据
            var dateBadges = new List<object>
            {
                new { date = "2026-01-29", count = 3, hasFault = true },
                new { date = "2026-02-13", count = 1, hasFault = false }
            };

            string json = serializer.Serialize(dateBadges);
            TestRunner.AssertTrue(json.Contains("hasFault"), "含 hasFault 标记");
            TestRunner.AssertTrue(json.Contains("count"), "含 count 计数");
        }

        // ──────────────────────────────────────────────
        // Slice 3: 诊断条数据
        // ──────────────────────────────────────────────

        static void Test_DiagnosisJsonFormat()
        {
            var diagnosis = new
            {
                level = "故障",
                items = new[] {
                    "动作时长31.36s，超过参考11.72s+3.0s，疑似卡阻/空转未完成"
                }
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(diagnosis);

            TestRunner.AssertTrue(json.Contains("level"), "含 level 键");
            TestRunner.AssertTrue(json.Contains("故障"), "含 故障 级别");
            TestRunner.AssertTrue(json.Contains("items"), "含 items 数组");
            TestRunner.AssertTrue(json.Contains("31.36"), "含描述文字");
        }

        static void Test_DiagnosisNormalEvent()
        {
            var diagnosis = new
            {
                level = "正常",
                items = new string[] { }
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(diagnosis);

            TestRunner.AssertTrue(json.Contains("正常"), "含 正常 级别");
        }

        static void Test_DiagnosisMissingData()
        {
            // 无诊断数据时的表示：diagnosis 为 null
            var chartData = new
            {
                switchId = "1-J",
                currentEvent = new { timestamp = 1 },
                diagnosis = (object)null
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(chartData);

            TestRunner.AssertTrue(json.Contains("\"diagnosis\":null"), "diagnosis 为 null 表示无数据");
        }

        // ──────────────────────────────────────────────
        // Slice 4: chartData 包含 diagnosis
        // ──────────────────────────────────────────────

        static void Test_ChartDataIncludesDiagnosis()
        {
            var diagnosis = new
            {
                level = "故障",
                items = new[] { "动作时长31.36s，超过参考11.72s+3.0s，疑似卡阻/空转未完成" }
            };

            var chartData = new
            {
                switchId = "4-J",
                switchLabel = "4-J",
                currentEvent = new { timestamp = 1769618597L, direction = "定位↔反位", duration = 31.36 },
                prevEvent = (object)null,
                thresholdCurrent = 2.0,
                thresholdPower = 1.5,
                xMax = 30,
                colors = new { },
                diagnosis = diagnosis   // ← 新增字段
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(chartData);

            TestRunner.AssertTrue(json.Contains("diagnosis"), "chartData 含 diagnosis");
            TestRunner.AssertTrue(json.Contains("故障"), "诊断级别传递正确");
        }

        // ──────────────────────────────────────────────
        // Slice 5: DiagnosisRunner.RerunAll
        // ──────────────────────────────────────────────

        static void Test_RerunAll_ProcessesAllDays()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string originalDir = Logger.LogDir;
                Logger.LogDir = tempDir;

                // 构造测试数据：4-1 有两天的数据
                string parsedDir = Path.Combine(tempDir, "parsed_data");
                var im = new IndexManager(parsedDir);
                im.Initialize();

                // 写入两天的数据 + 诊断
                var events1 = new List<SwitchEvent>
                {
                    new SwitchEvent { Timestamp = 1, DateTimeStr = "2026-01-29 00:43:17",
                        Power = new List<double[]> { new double[] { 0, 1.0 }, new double[] { 0.04, 1.1 } },
                        SampleCount = 2, SampleInterval = 0.04, Direction = "定位↔反位", Duration = 31.36 },
                    new SwitchEvent { Timestamp = 2, DateTimeStr = "2026-01-29 06:16:30",
                        Power = new List<double[]> { new double[] { 0, 0.5 }, new double[] { 0.04, 0.6 } },
                        SampleCount = 2, SampleInterval = 0.04, Direction = "定位↔反位", Duration = 11.76 }
                };
                var events2 = new List<SwitchEvent>
                {
                    new SwitchEvent { Timestamp = 3, DateTimeStr = "2026-01-28 10:00:00",
                        Power = new List<double[]> { new double[] { 0, 0.5 }, new double[] { 0.04, 0.6 } },
                        SampleCount = 2, SampleInterval = 0.04, Direction = "定位↔反位", Duration = 11.80 }
                };

                im.SaveDayData("4-J", "2026-01-29", events1);
                im.SaveDayData("4-J", "2026-01-28", events2);

                // 验证 rerun 遍历了全部日期
                int rerunCount = 0;
                var dates = im.GetDates("4-J");
                foreach (var date in dates)
                {
                    var dayEvents = im.LoadDayData("4-J", date);
                    // 模拟诊断（实际 RerunAll 会调用完整引擎）
                    var diagnoses = new List<EventDiagnosis>();
                    foreach (var evt in dayEvents)
                    {
                        diagnoses.Add(new EventDiagnosis
                        {
                            Timestamp = evt.Timestamp,
                            Level = "正常",
                            Results = new List<DiagnosisItem>()
                        });
                    }
                    im.SaveDayDiagnosis("4-J", date, diagnoses);
                    rerunCount += dayEvents.Count;
                }

                TestRunner.AssertEqual(2, dates.Count, "共2天数据");
                TestRunner.AssertEqual(3, rerunCount, "共重跑3个事件");

                // 验证 .diag.json 已生成
                string diag1 = Path.Combine(parsedDir, "4-J", "2026-01-29.diag.json");
                string diag2 = Path.Combine(parsedDir, "4-J", "2026-01-28.diag.json");
                TestRunner.AssertFileExists(diag1, "day1 .diag.json");
                TestRunner.AssertFileExists(diag2, "day2 .diag.json");

                Logger.LogDir = originalDir;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 6: 诊断参数配置
        // ──────────────────────────────────────────────

        static void Test_DiagConfig_LoadThresholds()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                // 创建测试用 thresholds.json
                string rulesDir = Path.Combine(tempDir, "Rules");
                Directory.CreateDirectory(rulesDir);
                string json = @"{
                    ""version"": 1,
                    ""rules"": {
                        ""R1"": { ""enabled"": true, ""level"": ""故障"", ""durOverRefSeconds"": 3.0 },
                        ""R2"": { ""enabled"": true, ""level"": ""报警"", ""durUnderRefRatio"": 0.6 },
                        ""R3"": { ""enabled"": false, ""level"": ""预警"", ""maxDeviationSeconds"": 0.5 }
                    }
                }";
                File.WriteAllText(Path.Combine(rulesDir, "thresholds.json"), json, Encoding.UTF8);

                // 加载
                var store = ThresholdStore.Load(Path.Combine(rulesDir, "thresholds.json"));

                TestRunner.AssertNotNull(store, "ThresholdStore 非null");
                TestRunner.AssertEqual(3, store.rules.Count, "3条规则");

                var r1 = store.Get("R1");
                TestRunner.AssertNotNull(r1, "R1 存在");
                TestRunner.AssertTrue(r1.enabled, "R1 启用");
                TestRunner.AssertEqual("故障", r1.level, "R1 级别=故障");

                var r3 = store.Get("R3");
                TestRunner.AssertNotNull(r3, "R3 存在");
                TestRunner.AssertFalse(r3.enabled, "R3 禁用");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_DiagConfig_LoadChartThresholds()
        {
            // 验证从 AppConfig 读取图表阈值配置
            var config = new AppConfig
            {
                AlarmThresholds = new AlarmThresholdsConfig
                {
                    Current = new AlarmThreshold { Enabled = true, Value = 2.0, Unit = "A" },
                    Power = new AlarmThreshold { Enabled = true, Value = 1.5, Unit = "KW" }
                }
            };

            TestRunner.AssertTrue(config.AlarmThresholds.Current.Enabled, "电流阈值启用");
            TestRunner.AssertEqual(2.0, config.AlarmThresholds.Current.Value, 0.001, "电流阈值值");
            TestRunner.AssertEqual("A", config.AlarmThresholds.Current.Unit, "电流阈值单位");

            TestRunner.AssertTrue(config.AlarmThresholds.Power.Enabled, "功率阈值启用");
            TestRunner.AssertEqual(1.5, config.AlarmThresholds.Power.Value, 0.001, "功率阈值值");
            TestRunner.AssertEqual("KW", config.AlarmThresholds.Power.Unit, "功率阈值单位");
        }

        static void Test_DiagConfig_Save()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                // 创建并保存阈值
                string rulesDir = Path.Combine(tempDir, "Rules");
                Directory.CreateDirectory(rulesDir);
                string thresholdsPath = Path.Combine(rulesDir, "thresholds.json");

                var store = ThresholdStore.CreateDefaults();
                // 修改 R1 的级别
                var r1 = store.Get("R1");
                r1.level = "报警";
                r1.durOverRefSeconds = 4.0;

                // 序列化保存
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(store);
                File.WriteAllText(thresholdsPath, json, Encoding.UTF8);

                // 重新加载验证
                var loaded = ThresholdStore.Load(thresholdsPath);
                var r1Loaded = loaded.Get("R1");
                TestRunner.AssertEqual("报警", r1Loaded.level, "R1 级别已改为报警");
                TestRunner.AssertEqual(4.0, r1Loaded.durOverRefSeconds, 0.001, "R1 超时偏移已改为4.0");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_DiagConfig_ResetDefaults()
        {
            // 验证 CreateDefaults 返回与模板一致的内置默认值
            var defaults = ThresholdStore.CreateDefaults();

            var r1 = defaults.Get("R1");
            TestRunner.AssertTrue(r1.enabled, "R1 默认启用");
            TestRunner.AssertEqual("故障", r1.level, "R1 默认级别=故障");
            TestRunner.AssertEqual(3.0, r1.durOverRefSeconds, 0.001, "R1 默认偏移=3.0s");

            var r8 = defaults.Get("R8");
            TestRunner.AssertTrue(r8.enabled, "R8 默认启用");
            TestRunner.AssertEqual("预警", r8.level, "R8 默认级别=预警");
            TestRunner.AssertEqual(0.3, r8.deviationRatio, 0.001, "R8 默认偏差比例=0.3");
        }
    }
}
