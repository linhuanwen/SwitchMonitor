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
    /// D6 Trend Analysis + Reference Curve Comparison 测试套件
    /// 验证: features.json 列式存储 / T1 渐变劣化 / 参考曲线生成 / P1 逐点对比 / UI 叠加数据
    /// </summary>
    public static class D6Tests
    {
        public static void Run()
        {
            // ═══ Slice 1: features.json 列式存储 ═══
            TestRunner.Test("features.json 列式格式序列化", Test_FeaturesJson_ColumnarFormat);
            TestRunner.Test("features.json 列式格式反序列化", Test_FeaturesJson_Deserialize);
            TestRunner.Test("FeaturesStore 追加写入 + 读取", Test_FeaturesStore_AppendAndRead);
            TestRunner.Test("FeaturesStore 回填工具", Test_FeaturesStore_Backfill);

            // ═══ Slice 2: T1 趋势分析 ═══
            TestRunner.Test("T1 合成递增数据触发预警", Test_T1_SyntheticTrend_Triggers);
            TestRunner.Test("T1 平稳数据不触发", Test_T1_StableData_NoTrigger);
            TestRunner.Test("T1 天数不足不触发", Test_T1_InsufficientDays_NoTrigger);
            TestRunner.Test("T1 ruleId=T1 出现在诊断结果中", Test_T1_RuleId_Format);

            // ═══ Slice 3: 参考曲线生成 ═══
            TestRunner.Test("ReferenceCurveBuilder 生成参考曲线", Test_ReferenceCurve_Generate);
            TestRunner.Test("参考曲线 alignIndex 为 spikeIndex 中位数", Test_ReferenceCurve_AlignIndex);
            TestRunner.Test("ReferenceCurveStore 读写 JSON", Test_ReferenceCurveStore_Roundtrip);

            // ═══ Slice 4: P1 逐点对比 ═══
            TestRunner.Test("P1 超时曲线触发预警", Test_P1_TimeoutTriggers);
            TestRunner.Test("P1 正常曲线不触发", Test_P1_NormalNoTrigger);
            TestRunner.Test("P1 ruleId=P1 出现在诊断结果中", Test_P1_RuleId_Format);
            TestRunner.Test("P1 areaDiffRatio 和 maxAbsDev 计算正确", Test_P1_Metrics_Calculation);

            // ═══ Slice 5: thresholds.json T1/P1 节 ═══
            TestRunner.Test("ThresholdStore 包含 T1 默认阈值", Test_Thresholds_T1_Defaults);
            TestRunner.Test("ThresholdStore 包含 P1 默认阈值", Test_Thresholds_P1_Defaults);
            TestRunner.Test("T1/P1 阈值可从 JSON 加载", Test_Thresholds_T1P1_FromJson);

            // ═══ Slice 6: UI 参考曲线叠加数据 ═══
            TestRunner.Test("参考曲线 JSON 格式供前端使用", Test_ReferenceCurve_UI_JsonFormat);
            TestRunner.Test("chartData 含 refCurve 字段", Test_ChartData_RefCurve);
        }

        // ──────────────────────────────────────────────
        // Slice 1: features.json 列式存储
        // ──────────────────────────────────────────────

        static void Test_FeaturesJson_ColumnarFormat()
        {
            var store = new FeaturesStore();
            store.Columns = new List<string> { "timestamp", "durationSec", "spikePeak", "unlockMean", "convMean", "lockMean", "tailMean" };
            store.Rows = new List<List<double>>();
            store.Rows.Add(new List<double> { 1770922311, 11.76, 3.392, 0.309, 0.308, 0.208 });
            store.Rows.Add(new List<double> { 1770771323, 8.56, 3.294, 0.317, 0.254, 0.202 });

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(store);

            TestRunner.AssertTrue(json.Contains("\"Columns\""), "含 Columns 键");
            TestRunner.AssertTrue(json.Contains("\"Rows\""), "含 Rows 键");
            TestRunner.AssertTrue(json.Contains("\"timestamp\""), "含 timestamp 列名");
            TestRunner.AssertTrue(json.Contains("1770922311"), "含 row 值");
            TestRunner.AssertFalse(json.Contains("\"timestamp\":1770922311"), "列式不含逐行键名");
        }

        static void Test_FeaturesJson_Deserialize()
        {
            string json = @"{""columns"":[""timestamp"",""durationSec"",""spikePeak"",""unlockMean"",""convMean"",""lockMean"",""tailMean""],""rows"":[[1770922311,11.76,3.392,0.309,0.308,0.308,0.208],[1770771323,8.56,3.294,0.317,0.254,0.239,0.202]]}";

            var serializer = new JavaScriptSerializer();
            var store = serializer.Deserialize<FeaturesStore>(json);

            TestRunner.AssertNotNull(store, "反序列化非null");
            TestRunner.AssertEqual(7, store.Columns.Count, "7 列");
            TestRunner.AssertEqual(2, store.Rows.Count, "2 行");
            TestRunner.AssertEqual("convMean", store.Columns[4], "第5列=convMean");
            TestRunner.AssertEqual(0.308, store.Rows[0][4], 0.001, "第一行 convMean=0.308");
        }

        static void Test_FeaturesStore_AppendAndRead()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string switchId = "1-J";
                var writer = new FeaturesStore();
                writer.Columns = new List<string> { "timestamp", "durationSec", "spikePeak", "convMean", "tailMean" };

                writer.Rows.Add(new List<double> { 100, 11.76, 3.392, 0.308, 0.208 });
                writer.Rows.Add(new List<double> { 200, 11.80, 3.400, 0.310, 0.210 });
                FeaturesStore.Save(tempDir, switchId, writer);

                FeaturesStore.Append(tempDir, switchId, 300, 11.72, 3.235, 0.307, 0.301, 0.200, 0.213);

                string filePath = Path.Combine(tempDir, switchId, "features.json");
                var loaded = FeaturesStore.Load(filePath);
                TestRunner.AssertNotNull(loaded, "加载非null");
                TestRunner.AssertEqual(3, loaded.Rows.Count, "追加后共3行");
                TestRunner.AssertEqual(300.0, loaded.Rows[2][0], 0.1, "第3行 timestamp=300");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_FeaturesStore_Backfill()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string parsedDir = Path.Combine(tempDir, "parsed_data");
                var im = new IndexManager(parsedDir);
                im.Initialize();

                var events = new List<SwitchEvent>
                {
                    new SwitchEvent
                    {
                        Timestamp = 1770922311, DateTimeStr = "2026-02-13 02:51:51",
                        Power = MakePowerCurve(3.392, 0.308, 300),
                        SampleCount = 300, SampleInterval = 0.04, Duration = 11.76
                    },
                    new SwitchEvent
                    {
                        Timestamp = 1770922400, DateTimeStr = "2026-02-13 06:16:30",
                        Power = MakePowerCurve(3.400, 0.310, 298),
                        SampleCount = 298, SampleInterval = 0.04, Duration = 11.80
                    }
                };
                im.SaveDayData("1-J", "2026-02-13", events);

                int backfilled = FeaturesStore.BackfillWithDir(im, "1-J", parsedDir);
                TestRunner.AssertEqual(2, backfilled, "回填行数=2");

                string featuresPath = Path.Combine(parsedDir, "1-J", "features.json");
                TestRunner.AssertFileExists(featuresPath, "features.json 已生成");

                var loaded = FeaturesStore.Load(featuresPath);
                TestRunner.AssertEqual(7, loaded.Columns.Count, "7 列");
                TestRunner.AssertEqual(2, loaded.Rows.Count, "2 行");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 2: T1 趋势分析
        // ──────────────────────────────────────────────

        static void Test_T1_SyntheticTrend_Triggers()
        {
            var rows = new List<List<double>>();
            long baseTs = 1770000000;
            double baseConv = 0.300;
            double targetConv = 0.360;
            int totalDays = 10;
            int eventsPerDay = 5;

            for (int day = 0; day < totalDays; day++)
            {
                double dayConv = baseConv + (targetConv - baseConv) * day / (totalDays - 1);
                for (int e = 0; e < eventsPerDay; e++)
                {
                    long ts = baseTs + day * 86400 + e * 300;
                    double noise = (e - 2) * 0.002;
                    rows.Add(new List<double> { (double)ts, 11.76, 3.392, 0.309, dayConv + noise, 0.200, 0.208 });
                }
            }

            var store = new FeaturesStore
            {
                Columns = new List<string> { "timestamp", "durationSec", "spikePeak", "unlockMean", "convMean", "lockMean", "tailMean" },
                Rows = rows
            };

            var result = TrendAnalyzer.AnalyzeT1(store, 0.300, 0.15, 7);
            TestRunner.AssertNotNull(result, "T1 应触发");
            TestRunner.AssertTrue(result != null && result.RuleId == "T1", "ruleId=T1");
            TestRunner.AssertTrue(result != null && result.Level == "预警", "级别=预警");
            TestRunner.AssertTrue(result != null && result.Description.Contains("持续上升"), "描述含'持续上升'");
        }

        static void Test_T1_StableData_NoTrigger()
        {
            var rows = new List<List<double>>();
            long baseTs = 1770000000;
            double baseConv = 0.300;
            int totalDays = 10;
            int eventsPerDay = 5;
            var rng = new Random(42);

            for (int day = 0; day < totalDays; day++)
            {
                for (int e = 0; e < eventsPerDay; e++)
                {
                    long ts = baseTs + day * 86400 + e * 300;
                    double noise = (rng.NextDouble() - 0.5) * 0.02;
                    rows.Add(new List<double> { (double)ts, 11.76, 3.392, 0.309, baseConv + noise, 0.200, 0.208 });
                }
            }

            var store = new FeaturesStore
            {
                Columns = new List<string> { "timestamp", "durationSec", "spikePeak", "unlockMean", "convMean", "lockMean", "tailMean" },
                Rows = rows
            };

            var result = TrendAnalyzer.AnalyzeT1(store, 0.300, 0.15, 7);
            TestRunner.AssertTrue(result == null, "平稳数据 T1 不触发");
        }

        static void Test_T1_InsufficientDays_NoTrigger()
        {
            var rows = new List<List<double>>();
            long baseTs = 1770000000;

            for (int day = 0; day < 5; day++)
            {
                for (int e = 0; e < 3; e++)
                {
                    long ts = baseTs + day * 86400 + e * 300;
                    double conv = 0.300 + day * 0.015;
                    rows.Add(new List<double> { (double)ts, 11.76, 3.392, 0.309, conv, 0.200, 0.208 });
                }
            }

            var store = new FeaturesStore
            {
                Columns = new List<string> { "timestamp", "durationSec", "spikePeak", "unlockMean", "convMean", "lockMean", "tailMean" },
                Rows = rows
            };

            var result = TrendAnalyzer.AnalyzeT1(store, 0.300, 0.15, 7);
            TestRunner.AssertTrue(result == null, "不足7天不触发");
        }

        static void Test_T1_RuleId_Format()
        {
            var result = new DiagnosisResult
            {
                RuleId = "T1",
                RuleName = "渐变劣化预警",
                Level = "预警",
                Description = "转换段功率呈持续上升趋势（最近7天），convMean从0.301升至0.360（+19.7%），建议检查滑床板润滑",
                Value = 0.360,
                Reference = 0.301
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(result);

            TestRunner.AssertTrue(json.Contains("\"T1\""), "含 ruleId T1");
            TestRunner.AssertTrue(json.Contains("渐变劣化预警"), "含规则名");
            TestRunner.AssertTrue(json.Contains("持续上升"), "含趋势描述");
            TestRunner.AssertTrue(json.Contains("滑床板润滑"), "含建议措施");
        }

        // ──────────────────────────────────────────────
        // Slice 3: 参考曲线生成
        // ──────────────────────────────────────────────

        static void Test_ReferenceCurve_Generate()
        {
            var normalCurves = new List<List<double>>();
            for (int i = 0; i < 10; i++)
            {
                normalCurves.Add(MakeNormalCurve(spikeIndex: 6, convMean: 0.301, tailMean: 0.213, length: 300));
            }

            var refCurve = ReferenceCurveBuilder.Build(normalCurves, sampleInterval: 0.04);

            TestRunner.AssertNotNull(refCurve, "参考曲线非null");
            TestRunner.AssertEqual("1-J", refCurve.SwitchId, "switchId 传递正确");
            TestRunner.AssertEqual(0.04, refCurve.SampleInterval, 0.001, "sampleInterval=0.04");
            TestRunner.AssertEqual(6, refCurve.AlignIndex, "alignIndex=6（spikeIndex 中位数）");
            TestRunner.AssertTrue(refCurve.Values.Count >= 250 && refCurve.Values.Count <= 310,
                "参考曲线长度 ≈ 300");

            double convSum = 0;
            int convCount = 0;
            int convStart = refCurve.AlignIndex + 20;
            int convEnd = refCurve.Values.Count - 40;
            if (convEnd > convStart)
            {
                for (int i = convStart; i < convEnd && i < refCurve.Values.Count; i++)
                {
                    convSum += refCurve.Values[i];
                    convCount++;
                }
            }
            if (convCount > 0)
            {
                double convMean = convSum / convCount;
                TestRunner.AssertEqual(0.301, convMean, 0.03, "参考曲线转换段均值≈0.301");
            }
        }

        static void Test_ReferenceCurve_AlignIndex()
        {
            var normalCurves = new List<List<double>>();
            int[] spikeIndices = { 5, 6, 6, 7, 8 };
            foreach (var si in spikeIndices)
            {
                normalCurves.Add(MakeNormalCurve(spikeIndex: si, convMean: 0.301, tailMean: 0.213, length: 300));
            }

            var refCurve = ReferenceCurveBuilder.Build(normalCurves, sampleInterval: 0.04);
            TestRunner.AssertEqual(6, refCurve.AlignIndex, "alignIndex=中位数6");
        }

        static void Test_ReferenceCurveStore_Roundtrip()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string dir = Path.Combine(tempDir, "reference_curves");
                Directory.CreateDirectory(dir);

                var curve = new ReferenceCurve
                {
                    SwitchId = "1-J",
                    SampleInterval = 0.04,
                    AlignIndex = 6,
                    Values = new List<double> { 0.0, 0.14, 0.24, 3.235, 0.350, 0.307, 0.301, 0.300, 0.299 },
                    ComputedAt = "2026-07-08 10:00:00"
                };

                ReferenceCurveStore.Save(dir, curve);

                string expectedPath = Path.Combine(dir, "1-1.json");
                TestRunner.AssertFileExists(expectedPath, "参考曲线文件存在");

                var loaded = ReferenceCurveStore.Load(expectedPath);
                TestRunner.AssertNotNull(loaded, "加载非null");
                TestRunner.AssertEqual("1-J", loaded.SwitchId, "switchId 一致");
                TestRunner.AssertEqual(6, loaded.AlignIndex, "alignIndex 一致");
                TestRunner.AssertEqual(9, loaded.Values.Count, "values 长度一致");
                TestRunner.AssertEqual(3.235, loaded.Values[3], 0.001, "value[3]=3.235");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 4: P1 逐点对比
        // ──────────────────────────────────────────────

        static void Test_P1_TimeoutTriggers()
        {
            var refCurve = MakeNormalCurve(spikeIndex: 6, convMean: 0.301, tailMean: 0.213, length: 300);
            var currentCurve = MakeNormalCurve(spikeIndex: 7, convMean: 0.545, tailMean: 0.706, length: 790);

            var result = ProfileComparer.CompareP1(currentCurve, refCurve, 7, 6, 0.301, 0.04);

            TestRunner.AssertNotNull(result, "P1 应触发");
            TestRunner.AssertTrue(result != null && result.RuleId == "P1", "ruleId=P1");
            TestRunner.AssertTrue(result != null && result.Level == "预警", "级别=预警");
            TestRunner.AssertTrue(result != null && result.Value > 0.25, "areaDiffRatio > 0.25");
        }

        static void Test_P1_NormalNoTrigger()
        {
            var refCurve = MakeNormalCurve(spikeIndex: 6, convMean: 0.301, tailMean: 0.213, length: 300);
            var currentCurve = MakeNormalCurve(spikeIndex: 6, convMean: 0.308, tailMean: 0.208, length: 300);

            var result = ProfileComparer.CompareP1(currentCurve, refCurve, 6, 6, 0.301, 0.04);

            if (result != null)
            {
                TestRunner.AssertTrue(result.Value < 0.25, "正常曲线 areaDiffRatio < 0.25");
            }
        }

        static void Test_P1_RuleId_Format()
        {
            var result = new DiagnosisResult
            {
                RuleId = "P1",
                RuleName = "曲线形态偏离",
                Level = "预警",
                Description = "曲线形态偏离参考，areaDiffRatio=0.35 maxAbsDev=0.480kW，参考convMean=0.301kW",
                Value = 0.35,
                Reference = 0.301
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(result);

            TestRunner.AssertTrue(json.Contains("\"P1\""), "含 ruleId P1");
            TestRunner.AssertTrue(json.Contains("曲线形态偏离"), "含规则名");
            TestRunner.AssertTrue(json.Contains("areaDiffRatio"), "含面积比指标");
            TestRunner.AssertTrue(json.Contains("maxAbsDev"), "含最大偏差指标");
        }

        static void Test_P1_Metrics_Calculation()
        {
            var refCurve = new List<double> { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0 };
            var curCurve = new List<double> { 0.0, 1.0, 4.0, 9.0, 4.0, 5.0 };

            var threshold = new RuleThreshold
            {
                enabled = true,
                level = "预警",
                areaDiffRatioThreshold = 0.25,
                maxAbsDevRatio = 1.0
            };

            using (var ms = new System.IO.MemoryStream())
            {
                var result = ProfileComparer.CompareP1WithThreshold(curCurve, refCurve, 0, 0, 5.0, threshold);

                TestRunner.AssertNotNull(result, "手工构造曲线触发 P1");
                if (result != null)
                {
                    // |差|面积=8, 参考面积=15, ratio=8/15≈0.533
                    TestRunner.AssertEqual(0.533, result.Value, 0.06, "areaDiffRatio≈0.533");
                    TestRunner.AssertTrue(result.Description.Contains("0.533"), "描述含 areaDiffRatio");
                }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 5: thresholds.json T1/P1 节
        // ──────────────────────────────────────────────

        static void Test_Thresholds_T1_Defaults()
        {
            var store = ThresholdStore.CreateDefaults();
            TestRunner.AssertNotNull(store, "ThresholdStore 非null");

            var t1 = store.Get("T1");
            TestRunner.AssertNotNull(t1, "T1 阈值存在");
            if (t1 != null)
            {
                TestRunner.AssertTrue(t1.enabled, "T1 默认启用");
                TestRunner.AssertEqual("预警", t1.level, "T1 级别=预警");
                TestRunner.AssertEqual(0.15, t1.trendRatio, 0.001, "T1 trendRatio=0.15");
                TestRunner.AssertEqual(7, t1.trendDays, "T1 trendDays=7");
            }
        }

        static void Test_Thresholds_P1_Defaults()
        {
            var store = ThresholdStore.CreateDefaults();
            var p1 = store.Get("P1");
            TestRunner.AssertNotNull(p1, "P1 阈值存在");
            if (p1 != null)
            {
                TestRunner.AssertTrue(p1.enabled, "P1 默认启用");
                TestRunner.AssertEqual("预警", p1.level, "P1 级别=预警");
                TestRunner.AssertEqual(0.25, p1.areaDiffRatioThreshold, 0.001, "P1 areaDiffRatio=0.25");
                TestRunner.AssertEqual(1.0, p1.maxAbsDevRatio, 0.001, "P1 maxAbsDevRatio=1.0");
            }
        }

        static void Test_Thresholds_T1P1_FromJson()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string thresholdsJson = @"{
                    ""version"": 2,
                    ""rules"": {
                        ""R1"": { ""enabled"": true, ""level"": ""故障"", ""durOverRefSeconds"": 3.0 },
                        ""T1"": { ""enabled"": true, ""level"": ""预警"", ""trendRatio"": 0.20, ""trendDays"": 10 },
                        ""P1"": { ""enabled"": false, ""level"": ""报警"", ""areaDiffRatioThreshold"": 0.30, ""maxAbsDevRatio"": 1.5 }
                    }
                }";

                string path = Path.Combine(tempDir, "thresholds.json");
                File.WriteAllText(path, thresholdsJson, Encoding.UTF8);

                var store = ThresholdStore.Load(path);

                var t1 = store.Get("T1");
                TestRunner.AssertNotNull(t1, "T1 从 JSON 加载");
                if (t1 != null)
                {
                    TestRunner.AssertTrue(t1.enabled, "T1 启用");
                    TestRunner.AssertEqual(0.20, t1.trendRatio, 0.001, "T1 trendRatio=0.20");
                    TestRunner.AssertEqual(10, t1.trendDays, "T1 trendDays=10");
                }

                var p1 = store.Get("P1");
                TestRunner.AssertNotNull(p1, "P1 从 JSON 加载");
                if (p1 != null)
                {
                    TestRunner.AssertFalse(p1.enabled, "P1 禁用");
                    TestRunner.AssertEqual("报警", p1.level, "P1 级别=报警");
                    TestRunner.AssertEqual(0.30, p1.areaDiffRatioThreshold, 0.001, "P1 areaDiffRatio=0.30");
                    TestRunner.AssertEqual(1.5, p1.maxAbsDevRatio, 0.001, "P1 maxAbsDevRatio=1.5");
                }

                var r1 = store.Get("R1");
                TestRunner.AssertNotNull(r1, "R1 仍然存在");
                if (r1 != null)
                {
                    TestRunner.AssertTrue(r1.enabled, "R1 启用");
                    TestRunner.AssertEqual("故障", r1.level, "R1 级别=故障");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 6: UI 参考曲线叠加数据
        // ──────────────────────────────────────────────

        static void Test_ReferenceCurve_UI_JsonFormat()
        {
            var refCurve = new ReferenceCurve
            {
                SwitchId = "1-J",
                SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double> { 0.0, 0.14, 0.24, 3.235, 0.350, 0.307, 0.301, 0.300, 0.299 },
                ComputedAt = "2026-07-08 10:00:00"
            };

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(refCurve);

            TestRunner.AssertTrue(json.Contains("\"SwitchId\""), "含 SwitchId");
            TestRunner.AssertTrue(json.Contains("\"SampleInterval\""), "含 SampleInterval");
            TestRunner.AssertTrue(json.Contains("\"AlignIndex\""), "含 AlignIndex");
            TestRunner.AssertTrue(json.Contains("\"Values\""), "含 Values 数组");
        }

        static void Test_ChartData_RefCurve()
        {
            var refCurve = new ReferenceCurve
            {
                SwitchId = "1-J",
                SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double> { 0.0, 0.14, 3.235, 0.301, 0.300 }
            };

            var serializer = new JavaScriptSerializer();
            var chartData = new
            {
                switchId = "1-J",
                currentEvent = new { timestamp = 1770922311L, direction = "定位↔反位", duration = 11.76 },
                refCurve = refCurve
            };

            string json = serializer.Serialize(chartData);
            TestRunner.AssertTrue(json.Contains("\"refCurve\""), "chartData 含 refCurve");
            TestRunner.AssertTrue(json.Contains("\"Values\""), "含 Values");
        }

        // ──────────────────────────────────────────────
        // 测试辅助方法
        // ──────────────────────────────────────────────

        private static List<double> MakeNormalCurve(int spikeIndex, double convMean, double tailMean, int length)
        {
            var curve = new List<double>();
            var rng = new Random(spikeIndex * 100 + (int)(convMean * 1000));

            for (int i = 0; i < length; i++)
            {
                if (i < 4)
                    curve.Add(0.0);
                else if (i == spikeIndex)
                    curve.Add(3.3 + rng.NextDouble() * 0.3);
                else if (i < spikeIndex + 5)
                    curve.Add(0.4 + rng.NextDouble() * 0.3);
                else if (i > length - 30)
                {
                    if (i > length - 22 && i < length - 2)
                        curve.Add(tailMean + (rng.NextDouble() - 0.5) * 0.02);
                    else if (i > length - 28 && i <= length - 23)
                        curve.Add(0.16 + rng.NextDouble() * 0.03);
                    else
                        curve.Add(tailMean + (rng.NextDouble() - 0.5) * 0.05);
                }
                else
                    curve.Add(convMean + (rng.NextDouble() - 0.5) * 0.04);
            }

            return curve;
        }

        private static List<double[]> MakePowerCurve(double spikePeak, double convMean, int length)
        {
            var power = new List<double[]>();
            var rng = new Random((int)(spikePeak * 1000));

            for (int i = 0; i < length; i++)
            {
                double t = i * 0.04;
                double v;
                if (i < 4)
                    v = 0.0;
                else if (i == 6)
                    v = spikePeak;
                else if (i < 11)
                    v = 0.35 + rng.NextDouble() * 0.1;
                else if (i > length - 30)
                    v = 0.21 + (rng.NextDouble() - 0.5) * 0.04;
                else
                    v = convMean + (rng.NextDouble() - 0.5) * 0.04;

                power.Add(new double[] { Math.Round(t, 3), Math.Round(v, 3) });
            }

            return power;
        }
    }
}
