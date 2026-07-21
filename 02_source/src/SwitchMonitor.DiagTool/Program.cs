using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;

namespace SwitchMonitor.DiagTool
{
    class Program
    {
        // 功率文件 → switchId 映射
        private static readonly Dictionary<int, string> PowerFileMap = new Dictionary<int, string>
        {
            {3, "1-J"}, {7, "1-X"}, {11, "3-J"}, {15, "3-X"},
            {19, "2-J"}, {23, "2-X"}, {27, "4-J"}, {31, "4-X"}
        };

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 2;
            }

            string command = args[0].ToLowerInvariant();
            string dir = args[1];

            switch (command)
            {
                case "selftest":
                    return RunSelftest(dir);
                case "baseline":
                {
                    return RunBaselineCommand(dir, args);
                }
                case "dryrun":
                {
                    string rulesDir = args.Length > 2 ? args[2] : Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".", "Rules");
                    return RunDryrun(dir, rulesDir);
                }
                case "trend":
                {
                    string switchId = args.Length > 2 ? args[2] : null;
                    return RunTrend(dir, switchId);
                }
                case "refcurve":
                {
                    string outputDir = args.Length > 2 ? args[2] : Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".", "Rules", "reference_curves");
                    return RunRefCurve(dir, outputDir);
                }
                case "profilecheck":
                {
                    string rulesDir = args.Length > 2 ? args[2] : Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".", "Rules");
                    return RunProfileCheck(dir, rulesDir);
                }
                default:
                    Console.Error.WriteLine("未知子命令: " + args[0]);
                    PrintUsage();
                    return 2;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("用法: DiagTool.exe <子命令> <参数>");
            Console.WriteLine();
            Console.WriteLine("子命令:");
            Console.WriteLine("  selftest     <sanshuibei_csv目录>    运行金标准夹具自检（特征 + 诊断）");
            Console.WriteLine("  baseline     <sanshuibei_csv目录> [输出路径] [--current|--all]");
            Console.WriteLine("               从CSV生成功率基线；--current 从parsed_data生成电流基线；--all 同时生成两者");
            Console.WriteLine("  dryrun       <sanshuibei_csv目录> [Rules目录]  全量规则演习，打印触发矩阵");
            Console.WriteLine("  trend        <parsed_data目录> [switchId]  T1 趋势分析（指定道岔或全部）");
            Console.WriteLine("  refcurve     <sanshuibei_csv目录> [输出目录]  生成逐点参考曲线");
            Console.WriteLine("  profilecheck <parsed_data目录> [Rules目录]  P1 逐点形态对比 dryrun");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  DiagTool.exe selftest D:\\data\\sanshuibei_csv");
            Console.WriteLine("  DiagTool.exe baseline D:\\data\\sanshuibei_csv");
            Console.WriteLine("  DiagTool.exe baseline D:\\data\\parsed_data Rules\\current_baselines.json --current");
            Console.WriteLine("  DiagTool.exe baseline D:\\data\\parsed_data Rules --all");
            Console.WriteLine("  DiagTool.exe dryrun D:\\data\\sanshuibei_csv Rules");
            Console.WriteLine("  DiagTool.exe trend parsed_data 1-1");
            Console.WriteLine("  DiagTool.exe refcurve D:\\data\\sanshuibei_csv");
            Console.WriteLine("  DiagTool.exe profilecheck parsed_data Rules");
        }

        // ═══════════════════════════════════════════════════════════════
        // selftest — 金标准夹具特征自检 + 诊断断言
        // ═══════════════════════════════════════════════════════════════

        static int RunSelftest(string csvDir)
        {
            if (!Directory.Exists(csvDir))
            {
                Console.Error.WriteLine("错误: 目录不存在: " + csvDir);
                PrintUsage();
                return 2;
            }

            Console.WriteLine("=== D1 FeatureExtractor 金标准自检 ===");
            Console.WriteLine("数据目录: " + csvDir);
            Console.WriteLine();

            bool allPass = true;

            // 四个金标准夹具定义
            var fixtures = new GoldenFixture[]
            {
                new GoldenFixture
                {
                    Label = "夹具A 正常J曲线",
                    FileName = "SwitchCurve(3).csv",
                    Timestamp = 1770922311,
                    SwitchId = "1-J",
                    Expected = new CurveFeatures
                    {
                        SampleCount = 300, IsFullWindow = false, IsValid = true,
                        ActiveEnd = 293, DurationSec = 11.76,
                        SpikePeak = 3.392, SpikeIndex = 6,
                        UnlockEnd = 111, LockStart = 238,
                        UnlockMean = 0.286, ConvMean = 0.319, ConvMax = 0.353,
                        StepRatio = 0.922, LockMean = 0.353, TailMean = 0.251
                    },
                    // 诊断期望：正常（空列表）
                    ExpectedDiag = new List<string>()
                },
                new GoldenFixture
                {
                    Label = "夹具B 正常X曲线",
                    FileName = "SwitchCurve(7).csv",
                    Timestamp = 1770771323,
                    SwitchId = "1-X",
                    Expected = new CurveFeatures
                    {
                        SampleCount = 220, IsFullWindow = false, IsValid = true,
                        ActiveEnd = 213, DurationSec = 8.56,
                        SpikePeak = 3.294, SpikeIndex = 6,
                        UnlockEnd = 104, LockStart = 160,
                        UnlockMean = 0.254, ConvMean = 0.267, ConvMax = 0.275,
                        StepRatio = 0.971, LockMean = 0.251, TailMean = 0.217
                    },
                    // 诊断期望：正常（空列表）
                    ExpectedDiag = new List<string>()
                },
                new GoldenFixture
                {
                    Label = "夹具C 超时/卡阻",
                    FileName = "SwitchCurve(27).csv",
                    Timestamp = 1769618597,
                    SwitchId = "4-J",
                    Expected = new CurveFeatures
                    {
                        SampleCount = 790, IsFullWindow = true, IsValid = true,
                        ActiveEnd = 783, DurationSec = 31.36,
                        SpikePeak = 4.353, SpikeIndex = 7,
                        UnlockEnd = 320, LockStart = 513,
                        UnlockMean = 0.572, ConvMean = 0.286, ConvMax = 0.627,
                        StepRatio = 1.185, LockMean = 0.704, TailMean = 0.706
                    },
                    // 诊断期望：恰好 [R1 故障]
                    ExpectedDiag = new List<string> { "R1" }
                },
                new GoldenFixture
                {
                    Label = "夹具D 动作夭折",
                    FileName = "SwitchCurve(31).csv",
                    Timestamp = 1773938685,
                    SwitchId = "4-X",
                    Expected = new CurveFeatures
                    {
                        SampleCount = 27, IsFullWindow = false, IsValid = true,
                        ActiveEnd = 20, DurationSec = 0.84,
                        SpikePeak = 3.373, SpikeIndex = 5,
                        UnlockEnd = 11, LockStart = -1,
                        UnlockMean = 0.216, ConvMean = 0.216, ConvMax = 0.216,
                        StepRatio = 1.0, LockMean = 0.0, TailMean = 0.0
                    },
                    // 诊断期望：恰好 [R2 报警]
                    ExpectedDiag = new List<string> { "R2" }
                }
            };

            // ── 阶段 1: 特征提取 ──
            foreach (var fixture in fixtures)
            {
                Console.WriteLine("--- " + fixture.Label + " ---");
                Console.WriteLine("  文件: " + fixture.FileName + "  timestamp=" + fixture.Timestamp);

                string filePath = Path.Combine(csvDir, fixture.FileName);
                if (!File.Exists(filePath))
                {
                    Console.WriteLine("  [FAIL] 文件不存在: " + filePath);
                    allPass = false;
                    continue;
                }

                var rows = SimpleCsvReader.ReadPowerCsv(filePath);
                CsvRow match = null;
                foreach (var row in rows)
                {
                    if (row.Timestamp == fixture.Timestamp)
                    {
                        match = row;
                        break;
                    }
                }

                if (match == null)
                {
                    Console.WriteLine("  [FAIL] 未找到 timestamp=" + fixture.Timestamp + " 的行");
                    allPass = false;
                    continue;
                }

                var actual = FeatureExtractor.Extract(match.Values);
                fixture.ActualFeatures = actual; // 保存供诊断阶段使用
                bool fixturePass = true;

                fixturePass &= CheckField("SampleCount",  actual.SampleCount,  fixture.Expected.SampleCount,  false, ref allPass);
                fixturePass &= CheckField("IsFullWindow", actual.IsFullWindow, fixture.Expected.IsFullWindow, false, ref allPass);
                fixturePass &= CheckField("IsValid",      actual.IsValid,      fixture.Expected.IsValid,      false, ref allPass);
                fixturePass &= CheckField("ActiveEnd",    actual.ActiveEnd,    fixture.Expected.ActiveEnd,    false, ref allPass);
                fixturePass &= CheckField("DurationSec",  actual.DurationSec,  fixture.Expected.DurationSec,  true,  ref allPass);
                fixturePass &= CheckField("SpikePeak",    actual.SpikePeak,    fixture.Expected.SpikePeak,    true,  ref allPass);
                fixturePass &= CheckField("SpikeIndex",   actual.SpikeIndex,   fixture.Expected.SpikeIndex,   false, ref allPass);
                fixturePass &= CheckField("UnlockEnd",    actual.UnlockEnd,   fixture.Expected.UnlockEnd,    false, ref allPass);
                fixturePass &= CheckField("LockStart",    actual.LockStart,   fixture.Expected.LockStart,    false, ref allPass);
                fixturePass &= CheckField("UnlockMean",   actual.UnlockMean,   fixture.Expected.UnlockMean,   true,  ref allPass);
                fixturePass &= CheckField("ConvMean",     actual.ConvMean,     fixture.Expected.ConvMean,     true,  ref allPass);
                fixturePass &= CheckField("ConvMax",      actual.ConvMax,      fixture.Expected.ConvMax,      true,  ref allPass);
                fixturePass &= CheckField("StepRatio",    actual.StepRatio,    fixture.Expected.StepRatio,    true,  ref allPass);
                fixturePass &= CheckField("LockMean",     actual.LockMean,     fixture.Expected.LockMean,     true,  ref allPass);
                fixturePass &= CheckField("TailMean",     actual.TailMean,     fixture.Expected.TailMean,     true,  ref allPass);

                if (fixturePass)
                {
                    Console.WriteLine("  => 特征全部 PASS");
                }
                Console.WriteLine();
            }

            // ── 阶段 2: 诊断逻辑验证 ──
            Console.WriteLine("=== D3 DiagnosisEngine 诊断逻辑验证 ===");
            Console.WriteLine();

            // 构建基线（从 CSV 数据）
            var baselineStore = BuildBaselinesFromCsv(csvDir);
            if (baselineStore == null || baselineStore.Switches == null || baselineStore.Switches.Count == 0)
            {
                Console.WriteLine("[警告] 无法从 CSV 数据构建基线，跳过诊断验证");
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("已从 CSV 数据构建 {0} 台道岔基线", baselineStore.Switches.Count);
                Console.WriteLine();

                // 初始化诊断引擎（使用内置默认阈值 + CSV 构建的基线）
                var engine = new DiagnosisEngine();
                // 直接设置基线（通过反射/内部方法），因为 Initialize 需要目录
                // 方案：使用临时目录存放 baselines.json
                string tempDir = Path.Combine(Path.GetTempPath(), "DiagTool_selftest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);
                try
                {
                    baselineStore.Save(Path.Combine(tempDir, "baselines.json"));
                    // 写入内置默认 thresholds.json
                    string thresholdsJson = @"{""version"":1,""rules"":{""R1"":{""enabled"":true,""level"":""故障"",""durOverRefSeconds"":3.0},""R2"":{""enabled"":true,""level"":""报警"",""durUnderRefRatio"":0.6},""R3"":{""enabled"":true,""level"":""预警"",""maxDeviationSeconds"":0.5},""R4"":{""enabled"":true,""level"":""预警"",""overRefRatio"":1.3},""R5"":{""enabled"":true,""level"":""预警"",""overRefRatio"":1.3},""R6"":{""enabled"":true,""level"":""报警"",""maxStepRatio"":1.5,""minStepRatio"":0.67},""R7"":{""enabled"":true,""level"":""预警"",""overRefRatio"":1.3},""R8"":{""enabled"":true,""level"":""预警"",""deviationRatio"":0.3},""R9"":{""enabled"":true,""level"":""预警"",""deviationRatio"":0.3}}}";
                    File.WriteAllText(Path.Combine(tempDir, "thresholds.json"), thresholdsJson, System.Text.Encoding.UTF8);

                    engine.Initialize(tempDir);

                    foreach (var fixture in fixtures)
                    {
                        if (fixture.ActualFeatures == null || !fixture.ActualFeatures.IsValid)
                        {
                            // 夹具 D 的 tailMean 期望也可能是有效的
                            if (fixture.ActualFeatures == null)
                            {
                                Console.WriteLine("  [SKIP] " + fixture.Label + " — 特征提取失败");
                                continue;
                            }
                        }

                        Console.WriteLine("--- " + fixture.Label + " (switchId=" + fixture.SwitchId + ") ---");

                        var results = engine.Diagnose(fixture.SwitchId, fixture.ActualFeatures);
                        var hitIds = new List<string>();
                        foreach (var r in results)
                        {
                            hitIds.Add(r.RuleId);
                        }

                        string overallLevel = DiagnosisAggregator.OverallLevel(results);
                        Console.WriteLine("  命中规则: " + (hitIds.Count > 0 ? string.Join(", ", hitIds) : "(无)"));
                        Console.WriteLine("  综合级别: " + overallLevel);

                        foreach (var r in results)
                        {
                            Console.WriteLine("  " + r.RuleId + " [" + r.Level + "]: " + r.Description);
                        }

                        // 断言：命中规则集合必须与期望一致
                        bool diagPass = true;
                        if (hitIds.Count != fixture.ExpectedDiag.Count)
                        {
                            diagPass = false;
                        }
                        else
                        {
                            for (int i = 0; i < hitIds.Count; i++)
                            {
                                if (hitIds[i] != fixture.ExpectedDiag[i])
                                {
                                    diagPass = false;
                                    break;
                                }
                            }
                        }

                        if (diagPass)
                        {
                            Console.WriteLine("  => 诊断 PASS (期望: " +
                                (fixture.ExpectedDiag.Count > 0 ? string.Join(", ", fixture.ExpectedDiag) : "正常") + ")");
                        }
                        else
                        {
                            Console.WriteLine("  [FAIL] 诊断: 期望=" +
                                (fixture.ExpectedDiag.Count > 0 ? string.Join(", ", fixture.ExpectedDiag) : "正常") +
                                " 实际=" + (hitIds.Count > 0 ? string.Join(", ", hitIds) : "正常"));
                            allPass = false;
                        }
                        Console.WriteLine();
                    }
                }
                finally
                {
                    // 清理临时文件
                    try
                    {
                        File.Delete(Path.Combine(tempDir, "baselines.json"));
                        File.Delete(Path.Combine(tempDir, "thresholds.json"));
                        Directory.Delete(tempDir);
                    }
                    catch { }
                }
            }

            // 边界用例：空数组
            Console.WriteLine("--- 边界用例: 空数组 ---");
            var emptyFeatures = FeatureExtractor.Extract(new double[0]);
            Console.WriteLine("  IsValid=" + emptyFeatures.IsValid + " (期望=false)");
            if (!emptyFeatures.IsValid)
                Console.WriteLine("  => PASS");
            else
            {
                Console.WriteLine("  => FAIL");
                allPass = false;
            }
            Console.WriteLine();

            // 边界用例：全零数组
            Console.WriteLine("--- 边界用例: 全零数组 ---");
            var zeroFeatures = FeatureExtractor.Extract(new double[] { 0.0, 0.0, 0.0 });
            Console.WriteLine("  IsValid=" + zeroFeatures.IsValid + " (期望=false)");
            if (!zeroFeatures.IsValid)
                Console.WriteLine("  => PASS");
            else
            {
                Console.WriteLine("  => FAIL");
                allPass = false;
            }
            Console.WriteLine();

            // 边界用例：Extract(SwitchEvent) 从 [t,v] 对中抽取 v 列
            Console.WriteLine("--- 边界用例: Extract(SwitchEvent) ---");
            var evt = new SwitchEvent
            {
                Timestamp = 1234567890,
                DateTimeStr = "2026-01-01 00:00:00",
                Power = new List<double[]>
                {
                    new double[] { 0.00, 0.500 },
                    new double[] { 0.04, 0.800 },
                    new double[] { 0.08, 1.200 },
                    new double[] { 0.12, 0.600 },
                    new double[] { 0.16, 0.300 },
                    new double[] { 0.20, 0.200 },
                    new double[] { 0.24, 0.150 },
                    new double[] { 0.28, 0.100 },
                    new double[] { 0.32, 0.080 },
                    new double[] { 0.36, 0.050 },
                    new double[] { 0.40, 0.030 },
                    new double[] { 0.44, 0.020 },
                    new double[] { 0.48, 0.010 },
                    new double[] { 0.52, 0.005 },
                    new double[] { 0.56, 0.003 },
                    new double[] { 0.60, 0.001 },
                }
            };
            var evtFeatures = FeatureExtractor.Extract(evt);
            bool evtPass = true;
            evtPass &= CheckField("IsValid",   evtFeatures.IsValid,   true,  false, ref allPass);
            evtPass &= CheckField("SampleCount", evtFeatures.SampleCount, 16, false, ref allPass);
            evtPass &= CheckField("SpikePeak", evtFeatures.SpikePeak, 1.2,   true,  ref allPass);
            evtPass &= CheckField("SpikeIndex", evtFeatures.SpikeIndex, 2,   false, ref allPass);
            if (evtPass)
                Console.WriteLine("  => PASS");
            Console.WriteLine();

            // 边界用例：SwitchEvent.Power 为空
            Console.WriteLine("--- 边界用例: Power=null ---");
            var nullPowerFeatures = FeatureExtractor.Extract(new SwitchEvent());
            Console.WriteLine("  IsValid=" + nullPowerFeatures.IsValid + " (期望=false)");
            if (!nullPowerFeatures.IsValid)
                Console.WriteLine("  => PASS");
            else
            {
                Console.WriteLine("  => FAIL");
                allPass = false;
            }
            Console.WriteLine();

            Console.WriteLine("=== 自检完成 ===");
            int exitCode = allPass ? 0 : 1;
            Console.WriteLine("退出码: " + exitCode + " (" + (allPass ? "全部通过" : "存在失败") + ")");
            return exitCode;
        }

        // ═══════════════════════════════════════════════════════════════
        // baseline — 基线生成（含 --current / --all）
        // ═══════════════════════════════════════════════════════════════

        static int RunBaselineCommand(string dir, string[] args)
        {
            // 解析标志
            bool currentOnly = false;
            bool all = false;
            string outputPath = null;

            for (int i = 2; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                if (arg == "--current")
                    currentOnly = true;
                else if (arg == "--all")
                    all = true;
                else
                    outputPath = args[i];
            }

            if (currentOnly && all)
            {
                Console.Error.WriteLine("错误: --current 与 --all 不能同时使用");
                PrintUsage();
                return 2;
            }

            if (currentOnly)
            {
                if (outputPath == null)
                {
                    string parentDir = Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".";
                    outputPath = Path.Combine(parentDir, "Rules", "current_baselines.json");
                }
                return RunCurrentBaseline(dir, outputPath);
            }

            if (all)
            {
                if (outputPath == null)
                {
                    string parentDir = Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".";
                    outputPath = Path.Combine(parentDir, "Rules");
                }
                string powerOutput = Path.Combine(outputPath, "baselines.json");
                string currentOutput = Path.Combine(outputPath, "current_baselines.json");

                // 功率基线（从 parsed_data 构建）
                int powerResult = RunBaselineFromParsedData(dir, powerOutput);
                Console.WriteLine();

                // 电流基线
                int currentResult = RunCurrentBaseline(dir, currentOutput);

                return (powerResult == 0 && currentResult == 0) ? 0 : 1;
            }

            // 默认：功率基线（从 CSV）
            if (outputPath == null)
            {
                string parentDir = Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".";
                outputPath = Path.Combine(parentDir, "Rules", "baselines.json");
            }
            return RunBaseline(dir, outputPath);
        }

        static int RunBaseline(string csvDir, string outputPath)
        {
            if (!Directory.Exists(csvDir))
            {
                Console.Error.WriteLine("错误: CSV 数据目录不存在: " + csvDir);
                return 2;
            }

            Console.WriteLine("=== D2 BaselineBuilder 基线生成 ===");
            Console.WriteLine("CSV 数据目录: " + Path.GetFullPath(csvDir));
            Console.WriteLine("输出路径: " + Path.GetFullPath(outputPath));
            Console.WriteLine();

            var store = BuildBaselinesFromCsv(csvDir);

            if (store == null || store.Switches == null || store.Switches.Count == 0)
            {
                Console.WriteLine("警告: 没有道岔满足基线条件，未生成 baselines.json");
                return 0;
            }

            store.ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 打印表头
            Console.WriteLine("{0,-6} {1,5} {2,8} {3,8} {4,8} {5,8} {6,8} {7,8}",
                "道岔", "样本", "时长s", "峰值", "解锁", "转换", "锁闭", "缓放");
            Console.WriteLine(new string('-', 60));

            foreach (var kvp in store.Switches)
            {
                var baseline = kvp.Value;
                Console.WriteLine("{0,-6} {1,5} {2,8:0.00} {3,8:0.000} {4,8:0.000} {5,8:0.000} {6,8:0.000} {7,8:0.000}",
                    kvp.Key, baseline.SampleCount,
                    baseline.RefDurationSec, baseline.RefSpikePeak,
                    baseline.RefUnlockMean, baseline.RefConvMean,
                    baseline.RefLockMean, baseline.RefTailMean);
            }

            Console.WriteLine();

            store.Save(outputPath);
            Console.WriteLine("已保存 {0} 台道岔的基线到: {1}", store.Switches.Count, Path.GetFullPath(outputPath));
            Console.WriteLine();
            Console.WriteLine("=== 基线生成完成 ===");
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // baseline --current — 电流基线生成
        // ═══════════════════════════════════════════════════════════════

        static int RunCurrentBaseline(string parsedDataDir, string outputPath)
        {
            if (!Directory.Exists(parsedDataDir))
            {
                Console.Error.WriteLine("错误: parsed_data 目录不存在: " + parsedDataDir);
                return 2;
            }

            Console.WriteLine("=== D7 CurrentBaselineBuilder 电流基线生成 ===");
            Console.WriteLine("parsed_data 目录: " + Path.GetFullPath(parsedDataDir));
            Console.WriteLine("输出路径: " + Path.GetFullPath(outputPath));
            Console.WriteLine();

            var im = new IndexManager(parsedDataDir);
            im.Initialize();

            var switchIds = im.GetAllSwitchIds();
            if (switchIds == null || switchIds.Count == 0)
            {
                Console.WriteLine("警告: parsed_data 中未找到道岔数据");
                return 0;
            }

            var store = new CurrentBaselineStore();
            store.Switches = new Dictionary<string, CurrentBaseline>();

            foreach (var switchId in switchIds)
            {
                var allFeatures = new List<CurrentFeatures>();
                string dateFrom = null;
                string dateTo = null;

                // 先加载功率诊断结果，构建"正常"时间戳集合
                var normalTimestamps = LoadNormalTimestamps(parsedDataDir, switchId);

                // 优先从 current_features.json 读取
                string featuresPath = Path.Combine(parsedDataDir, switchId, "current_features.json");
                if (File.Exists(featuresPath))
                {
                    var cfs = CurrentFeaturesStore.Load(featuresPath);
                    if (cfs != null && cfs.Rows != null && cfs.Rows.Count > 0)
                    {
                        // 从列式存储重建 CurrentFeatures 列表
                        int tsIdx = cfs.ColumnIndex("timestamp");
                        int durIdx = cfs.ColumnIndex("durationSec");
                        int unbalIdx = cfs.ColumnIndex("maxUnbalanceRatio");
                        int spaIdx = cfs.ColumnIndex("spikePeakA"); int siaIdx = cfs.ColumnIndex("spikeIndexA");
                        int ulaIdx = cfs.ColumnIndex("unlockMeanA"); int cmaIdx = cfs.ColumnIndex("convMeanA");
                        int lmaIdx = cfs.ColumnIndex("lockMeanA"); int tmaIdx = cfs.ColumnIndex("tailMeanA");
                        int spbIdx = cfs.ColumnIndex("spikePeakB"); int sibIdx = cfs.ColumnIndex("spikeIndexB");
                        int ulbIdx = cfs.ColumnIndex("unlockMeanB"); int cmbIdx = cfs.ColumnIndex("convMeanB");
                        int lmbIdx = cfs.ColumnIndex("lockMeanB"); int tmbIdx = cfs.ColumnIndex("tailMeanB");
                        int spcIdx = cfs.ColumnIndex("spikePeakC"); int sicIdx = cfs.ColumnIndex("spikeIndexC");
                        int ulcIdx = cfs.ColumnIndex("unlockMeanC"); int cmcIdx = cfs.ColumnIndex("convMeanC");
                        int lmcIdx = cfs.ColumnIndex("lockMeanC"); int tmcIdx = cfs.ColumnIndex("tailMeanC");
                        int validIdx = cfs.ColumnIndex("isValid");
                        int fwIdx = cfs.ColumnIndex("isFullWindow");
                        int scIdx = cfs.ColumnIndex("sampleCount");
                        int aeIdx = cfs.ColumnIndex("activeEnd");

                        foreach (var row in cfs.Rows)
                        {
                            // 功率诊断筛选：有诊断数据时仅保留功率诊断为"正常"的事件
                            if (normalTimestamps != null && tsIdx >= 0)
                            {
                                long rowTs = (long)row[tsIdx];
                                if (!normalTimestamps.Contains(rowTs))
                                    continue;
                            }

                            // 从列式存储重建 CurrentFeatures
                            var f = new CurrentFeatures
                            {
                                IsValid = validIdx >= 0 ? row[validIdx] > 0.5 : true,
                                IsFullWindow = fwIdx >= 0 ? row[fwIdx] > 0.5 : false,
                                DurationSec = row[durIdx],
                                MaxUnbalanceRatio = row[unbalIdx],
                                SpikePeakA = row[spaIdx], SpikeIndexA = (int)row[siaIdx],
                                UnlockMeanA = row[ulaIdx], ConvMeanA = row[cmaIdx],
                                LockMeanA = row[lmaIdx], TailMeanA = row[tmaIdx],
                                SpikePeakB = row[spbIdx], SpikeIndexB = (int)row[sibIdx],
                                UnlockMeanB = row[ulbIdx], ConvMeanB = row[cmbIdx],
                                LockMeanB = row[lmbIdx], TailMeanB = row[tmbIdx],
                                SpikePeakC = row[spcIdx], SpikeIndexC = (int)row[sicIdx],
                                UnlockMeanC = row[ulcIdx], ConvMeanC = row[cmcIdx],
                                LockMeanC = row[lmcIdx], TailMeanC = row[tmcIdx],
                                SampleCount = scIdx >= 0 ? (int)row[scIdx] : 300,
                                ActiveEnd = aeIdx >= 0 ? (int)row[aeIdx] : 293
                            };
                            allFeatures.Add(f);
                        }

                        // 从 row 的 timestamp 列确定日期范围
                        if (tsIdx >= 0 && cfs.Rows.Count > 0)
                        {
                            long firstTs = (long)cfs.Rows[0][tsIdx];
                            long lastTs = (long)cfs.Rows[cfs.Rows.Count - 1][tsIdx];
                            dateFrom = UnixTimestampToDate(firstTs);
                            dateTo = UnixTimestampToDate(lastTs);
                        }
                    }
                }

                // 如果 current_features.json 不存在，从日 JSON 实时提取
                if (allFeatures.Count == 0)
                {
                    var dates = im.GetDates(switchId);
                    if (dates != null && dates.Count > 0)
                    {
                        dates.Sort();
                        dateFrom = dates[0];
                        dateTo = dates[dates.Count - 1];

                        foreach (var date in dates)
                        {
                            var events = im.LoadDayData(switchId, date);
                            foreach (var evt in events)
                            {
                                // 功率诊断筛选：有诊断数据时仅保留功率诊断为"正常"的事件
                                if (normalTimestamps != null && !normalTimestamps.Contains(evt.Timestamp))
                                    continue;

                                var cf = CurrentFeatureExtractor.Extract(evt);
                                allFeatures.Add(cf);
                            }
                        }
                    }
                }

                if (allFeatures.Count == 0)
                {
                    Console.WriteLine("{0,-6} 无电流数据或功率诊断正常样本，跳过", switchId);
                    continue;
                }

                // 按两个方向分别生成基线（对齐功率基线 16 条目格式）
                var directions = new[] { "定位→反位", "反位→定位" };
                foreach (var dir in directions)
                {
                    var baseline = CurrentBaselineBuilder.Build(allFeatures, 30, dir);
                    if (baseline != null)
                    {
                        baseline.DateFrom = dateFrom;
                        baseline.DateTo = dateTo;
                        string key = CurrentBaselineStore.MakeKey(switchId, dir);
                        store.Switches[key] = baseline;
                    }
                }

                if (!store.Switches.Keys.Any(k => k.StartsWith(switchId + "|")))
                {
                    Console.WriteLine("{0,-6} 正常样本不足30（双向均失败），跳过", switchId);
                }
            }

            if (store.Switches.Count == 0)
            {
                Console.WriteLine("警告: 没有道岔满足电流基线条件，未生成 current_baselines.json");
                return 0;
            }

            store.ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 打印表头（分相显示）
            Console.WriteLine("{0,-6} {1,5} | {2,8} {3,8} {4,8} {5,8} {6,8} | {7,8} {8,8} {9,8}",
                "道岔", "样本", "A-峰值", "A-解锁", "A-转换", "A-锁闭", "A-缓放",
                "B-转换", "C-转换", "时长s");
            Console.WriteLine(new string('-', 100));

            foreach (var kvp in store.Switches)
            {
                var bl = kvp.Value;
                Console.WriteLine("{0,-6} {1,5} | {2,8:0.000} {3,8:0.000} {4,8:0.000} {5,8:0.000} {6,8:0.000} | {7,8:0.000} {8,8:0.000} | {9,8:0.00}",
                    kvp.Key, bl.SampleCount,
                    bl.RefSpikePeakA, bl.RefUnlockMeanA, bl.RefConvMeanA,
                    bl.RefLockMeanA, bl.RefTailMeanA,
                    bl.RefConvMeanB, bl.RefConvMeanC,
                    bl.RefDurationSec);
            }

            Console.WriteLine();

            try
            {
                store.Save(outputPath);
                Console.WriteLine("已保存 {0} 台道岔的电流基线到: {1}", store.Switches.Count, Path.GetFullPath(outputPath));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: 保存电流基线失败: " + ex.Message);
                return 1;
            }
            Console.WriteLine();
            Console.WriteLine("=== 电流基线生成完成 ===");
            return 0;
        }

        /// <summary>
        /// 加载功率诊断结果为"正常"的事件时间戳集合。
        /// 读取 .diag.json 文件，返回所有 Level=="正常" 或 Results 为空的 timestamp。
        /// 没有诊断数据时返回 null（表示无需筛选，全部通过）。
        /// </summary>
        private static HashSet<long> LoadNormalTimestamps(string parsedDataDir, string switchId)
        {
            string switchDir = Path.Combine(parsedDataDir, switchId);
            if (!Directory.Exists(switchDir))
                return null;

            var normalTimestamps = new HashSet<long>();
            bool anyDiagFound = false;

            foreach (var file in Directory.GetFiles(switchDir, "*.diag.json"))
            {
                anyDiagFound = true;
                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    serializer.MaxJsonLength = int.MaxValue;
                    var diagnoses = serializer.Deserialize<List<EventDiagnosis>>(json);
                    if (diagnoses != null)
                    {
                        foreach (var d in diagnoses)
                        {
                            if (d.Level == "正常" || (d.Results != null && d.Results.Count == 0))
                            {
                                normalTimestamps.Add(d.Timestamp);
                            }
                        }
                    }
                }
                catch
                {
                    // 损坏的 .diag.json 跳过
                }
            }

            // 没有诊断数据 → 返回 null（调用方不筛选）
            return anyDiagFound ? normalTimestamps : null;
        }

        /// <summary>
        /// 从 parsed_data 目录生成功率基线（用于 --all 模式）。
        /// </summary>
        static int RunBaselineFromParsedData(string parsedDataDir, string outputPath)
        {
            if (!Directory.Exists(parsedDataDir))
            {
                Console.Error.WriteLine("错误: parsed_data 目录不存在: " + parsedDataDir);
                return 2;
            }

            Console.WriteLine("=== D2 BaselineBuilder 功率基线生成（从 parsed_data）===");
            Console.WriteLine("parsed_data 目录: " + Path.GetFullPath(parsedDataDir));
            Console.WriteLine("输出路径: " + Path.GetFullPath(outputPath));
            Console.WriteLine();

            var im = new IndexManager(parsedDataDir);
            im.Initialize();

            var switchIds = im.GetAllSwitchIds();
            if (switchIds == null || switchIds.Count == 0)
            {
                Console.WriteLine("警告: parsed_data 中未找到道岔数据");
                return 0;
            }

            var store = new BaselineStore();
            store.Switches = new Dictionary<string, SwitchBaseline>();

            foreach (var switchId in switchIds)
            {
                var allFeatures = new List<CurveFeatures>();
                var allDates = new List<string>();
                var dates = im.GetDates(switchId);

                foreach (var date in dates)
                {
                    allDates.Add(date);
                    var events = im.LoadDayData(switchId, date);
                    foreach (var evt in events)
                    {
                        var features = FeatureExtractor.Extract(evt);
                        allFeatures.Add(features);
                    }
                }

                string dateFrom = null;
                string dateTo = null;
                if (allDates.Count > 0)
                {
                    allDates.Sort();
                    dateFrom = allDates[0];
                    dateTo = allDates[allDates.Count - 1];
                }

                var baseline = BaselineBuilder.Build(allFeatures, 30);
                if (baseline != null)
                {
                    baseline.DateFrom = dateFrom;
                    baseline.DateTo = dateTo;
                    store.Switches[switchId] = baseline;
                }
                else
                {
                    Console.WriteLine("{0,-6} 正常样本不足30，跳过", switchId);
                }
            }

            if (store.Switches.Count == 0)
            {
                Console.WriteLine("警告: 没有道岔满足基线条件");
                return 0;
            }

            store.ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            Console.WriteLine("{0,-6} {1,5} {2,8} {3,8} {4,8} {5,8} {6,8} {7,8}",
                "道岔", "样本", "时长s", "峰值", "解锁", "转换", "锁闭", "缓放");
            Console.WriteLine(new string('-', 60));

            foreach (var kvp in store.Switches)
            {
                var bl = kvp.Value;
                Console.WriteLine("{0,-6} {1,5} {2,8:0.00} {3,8:0.000} {4,8:0.000} {5,8:0.000} {6,8:0.000} {7,8:0.000}",
                    kvp.Key, bl.SampleCount,
                    bl.RefDurationSec, bl.RefSpikePeak,
                    bl.RefUnlockMean, bl.RefConvMean,
                    bl.RefLockMean, bl.RefTailMean);
            }

            Console.WriteLine();
            store.Save(outputPath);
            Console.WriteLine("已保存 {0} 台道岔的基线到: {1}", store.Switches.Count, Path.GetFullPath(outputPath));
            Console.WriteLine("=== 功率基线生成完成 ===");
            return 0;
        }

        /// <summary>Unix 时间戳转日期字符串 "yyyy-MM-dd"</summary>
        private static string UnixTimestampToDate(long ts)
        {
            try
            {
                DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                dt = dt.AddSeconds(ts).ToLocalTime();
                return dt.ToString("yyyy-MM-dd");
            }
            catch
            {
                return "";
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // dryrun — 全量规则演习
        // ═══════════════════════════════════════════════════════════════

        static int RunDryrun(string csvDir, string rulesDir)
        {
            if (!Directory.Exists(csvDir))
            {
                Console.Error.WriteLine("错误: CSV 数据目录不存在: " + csvDir);
                return 2;
            }

            Console.WriteLine("=== D3 DiagnosisEngine 全量规则演习 (dryrun) ===");
            Console.WriteLine("CSV 数据目录: " + Path.GetFullPath(csvDir));
            Console.WriteLine("Rules 目录: " + Path.GetFullPath(rulesDir));
            Console.WriteLine();

            // 确保 Rules 目录存在（否则引擎会用内置默认值）
            if (!Directory.Exists(rulesDir))
            {
                Directory.CreateDirectory(rulesDir);
            }

            // 如果没有 thresholds.json，写入默认模板
            string thresholdsPath = Path.Combine(rulesDir, "thresholds.json");
            if (!File.Exists(thresholdsPath))
            {
                Console.WriteLine("[信息] thresholds.json 不存在，将使用内置默认阈值");
            }

            // 如果没有 baselines.json，先从 CSV 构建
            string baselinesPath = Path.Combine(rulesDir, "baselines.json");
            if (!File.Exists(baselinesPath))
            {
                Console.WriteLine("[信息] baselines.json 不存在，先从 CSV 构建...");
                var store = BuildBaselinesFromCsv(csvDir);
                if (store != null && store.Switches != null && store.Switches.Count > 0)
                {
                    store.ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    store.Save(baselinesPath);
                    Console.WriteLine("  已生成 {0} 台道岔基线", store.Switches.Count);
                }
                else
                {
                    Console.WriteLine("  [警告] 无法构建基线，dryrun 仅执行 R0/R1 硬规则");
                }
                Console.WriteLine();
            }

            // 初始化引擎
            var engine = new DiagnosisEngine();
            engine.Initialize(rulesDir);

            // 规则 ID 列表（R0-R8）
            string[] ruleIds = { "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8" };

            // 累计统计
            var grandCounts = new Dictionary<string, int>();
            foreach (var rid in ruleIds)
                grandCounts[rid] = 0;
            int totalEvents = 0;
            int alarmedEvents = 0;

            // 打印表头
            Console.WriteLine("{0,-6} {1,5} | {2,4} {3,4} {4,4} {5,4} {6,4} {7,4} {8,4} {9,4} {10,4} | 触发率",
                "道岔", "事件", "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8");
            Console.WriteLine(new string('-', 85));

            foreach (var kvp in PowerFileMap)
            {
                int fileNum = kvp.Key;
                string switchId = kvp.Value;
                string fileName = string.Format("SwitchCurve({0}).csv", fileNum);
                string filePath = Path.Combine(csvDir, fileName);

                if (!File.Exists(filePath))
                {
                    Console.WriteLine("{0,-6} [警告] 文件不存在: {1}", switchId, fileName);
                    continue;
                }

                var rows = SimpleCsvReader.ReadPowerCsv(filePath);
                var counts = new Dictionary<string, int>();
                foreach (var rid in ruleIds)
                    counts[rid] = 0;
                int switchFired = 0;

                foreach (var row in rows)
                {
                    var features = FeatureExtractor.Extract(row.Values);
                    var results = engine.Diagnose(switchId, features);
                    foreach (var r in results)
                    {
                        if (counts.ContainsKey(r.RuleId))
                            counts[r.RuleId]++;
                    }
                    if (results.Count > 0)
                        switchFired++;
                }

                foreach (var rid in ruleIds)
                    grandCounts[rid] += counts[rid];
                totalEvents += rows.Count;
                alarmedEvents += switchFired;

                double rate = rows.Count > 0 ? switchFired / (double)rows.Count * 100.0 : 0.0;
                Console.WriteLine("{0,-6} {1,5} | {2,4} {3,4} {4,4} {5,4} {6,4} {7,4} {8,4} {9,4} {10,4} | {11,5:F2}%",
                    switchId, rows.Count,
                    counts["R0"], counts["R1"], counts["R2"],
                    counts["R3"], counts["R4"], counts["R5"],
                    counts["R6"], counts["R7"], counts["R8"],
                    rate);
            }

            Console.WriteLine(new string('-', 85));
            double totalRate = totalEvents > 0 ? alarmedEvents / (double)totalEvents * 100.0 : 0.0;
            Console.WriteLine("{0,-6} {1,5} | {2,4} {3,4} {4,4} {5,4} {6,4} {7,4} {8,4} {9,4} {10,4} | {11,5:F2}%",
                "合计", totalEvents,
                grandCounts["R0"], grandCounts["R1"], grandCounts["R2"],
                grandCounts["R3"], grandCounts["R4"], grandCounts["R5"],
                grandCounts["R6"], grandCounts["R7"], grandCounts["R8"],
                totalRate);

            Console.WriteLine();
            Console.WriteLine("触发汇总: " + string.Join(", ",
                Array.ConvertAll(ruleIds, rid => rid + "=" + grandCounts[rid])));
            Console.WriteLine("总事件: {0}, 触发: {1} ({2:F2}%)", totalEvents, alarmedEvents, totalRate);
            Console.WriteLine();
            Console.WriteLine("=== dryrun 完成 ===");
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // trend — T1 渐变劣化趋势分析
        // ═══════════════════════════════════════════════════════════════

        static int RunTrend(string parsedDataDir, string switchId)
        {
            if (!Directory.Exists(parsedDataDir))
            {
                Console.Error.WriteLine("错误: parsed_data 目录不存在: " + parsedDataDir);
                return 2;
            }

            Console.WriteLine("=== D6 T1 渐变劣化趋势分析 ===");
            Console.WriteLine("parsed_data 目录: " + Path.GetFullPath(parsedDataDir));
            Console.WriteLine();

            // 确定要分析的道岔
            List<string> switchIds;
            if (!string.IsNullOrEmpty(switchId))
            {
                switchIds = new List<string> { switchId };
            }
            else
            {
                // 扫描 parsed_data 目录下的子目录作为 switchId
                switchIds = new List<string>();
                foreach (var subDir in Directory.GetDirectories(parsedDataDir))
                {
                    string name = Path.GetFileName(subDir);
                    if (name.Contains("-"))
                        switchIds.Add(name);
                }
            }

            if (switchIds.Count == 0)
            {
                Console.WriteLine("未找到道岔数据");
                return 0;
            }

            int totalTriggers = 0;
            foreach (var sid in switchIds)
            {
                string featuresPath = Path.Combine(parsedDataDir, sid, "features.json");
                if (!File.Exists(featuresPath))
                {
                    Console.WriteLine("{0,-6} 无 features.json，跳过", sid);
                    continue;
                }

                var store = FeaturesStore.Load(featuresPath);
                if (store == null || store.Rows == null || store.Rows.Count == 0)
                {
                    Console.WriteLine("{0,-6} features.json 为空", sid);
                    continue;
                }

                // 尝试加载基线获取参考值
                double refConvMean = 0.301;    // fallback
                double refDurSec = 11.72;      // fallback
                string rulesDir = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(parsedDataDir)) ?? ".", "Rules");
                string baselinesPath = Path.Combine(rulesDir, "baselines.json");
                if (File.Exists(baselinesPath))
                {
                    var bs = BaselineStore.Load(baselinesPath);
                    SwitchBaseline baseline;
                    if (bs.Switches != null && bs.Switches.TryGetValue(sid, out baseline))
                    {
                        refConvMean = baseline.RefConvMean;
                        refDurSec = baseline.RefDurationSec;
                    }
                }

                // 执行趋势分析
                var convResult = TrendAnalyzer.AnalyzeT1(store, refConvMean, 0.15, 7, "convMean");
                var durResult = TrendAnalyzer.AnalyzeT1(store, refDurSec, 0.15, 7, "durationSec");

                Console.Write("{0,-6} {1} 条特征 → ", sid, store.Rows.Count);

                if (convResult != null || durResult != null)
                {
                    Console.WriteLine("触发!");
                    if (convResult != null)
                    {
                        Console.WriteLine("  [T1] " + convResult.Description);
                        totalTriggers++;
                    }
                    if (durResult != null)
                    {
                        Console.WriteLine("  [T1] " + durResult.Description);
                        totalTriggers++;
                    }
                }
                else
                {
                    Console.WriteLine("无趋势异常");
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== 趋势分析完成: {0} 条触发 ===", totalTriggers);
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // refcurve — 逐点参考曲线生成
        // ═══════════════════════════════════════════════════════════════

        static int RunRefCurve(string csvDir, string outputDir)
        {
            if (!Directory.Exists(csvDir))
            {
                Console.Error.WriteLine("错误: CSV 数据目录不存在: " + csvDir);
                return 2;
            }

            Console.WriteLine("=== D6 ReferenceCurveBuilder 参考曲线生成 ===");
            Console.WriteLine("CSV 数据目录: " + Path.GetFullPath(csvDir));
            Console.WriteLine("输出目录: " + Path.GetFullPath(outputDir));
            Console.WriteLine();

            // 确保输出目录存在
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            foreach (var kvp in PowerFileMap)
            {
                int fileNum = kvp.Key;
                string switchId = kvp.Value;
                string fileName = string.Format("SwitchCurve({0}).csv", fileNum);
                string filePath = Path.Combine(csvDir, fileName);

                if (!File.Exists(filePath))
                {
                    Console.WriteLine("{0,-6} [警告] 文件不存在: {1}", switchId, fileName);
                    continue;
                }

                var rows = SimpleCsvReader.ReadPowerCsv(filePath);

                // 筛选正常曲线（排除 isFullWindow 和超时/夭折）
                var normalCurves = new List<List<double>>();
                foreach (var row in rows)
                {
                    var features = FeatureExtractor.Extract(row.Values);
                    if (features.IsValid && !features.IsFullWindow && features.DurationSec >= 2.4)
                    {
                        normalCurves.Add(row.Values);
                    }
                }

                if (normalCurves.Count < 30)
                {
                    Console.WriteLine("{0,-6} 正常样本不足 ({1}<30)", switchId, normalCurves.Count);
                    continue;
                }

                var refCurve = ReferenceCurveBuilder.Build(normalCurves, 0.04, switchId);
                if (refCurve != null)
                {
                    ReferenceCurveStore.Save(outputDir, refCurve);
                    Console.WriteLine("{0,-6} 正常样本={1,4}  参考曲线长度={2,4}  alignIndex={3}",
                        switchId, normalCurves.Count, refCurve.Values.Count, refCurve.AlignIndex);
                }
                else
                {
                    Console.WriteLine("{0,-6} 生成失败", switchId);
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== 参考曲线生成完成 ===");
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // profilecheck — P1 逐点形态对比 dryrun
        // ═══════════════════════════════════════════════════════════════

        static int RunProfileCheck(string parsedDataDir, string rulesDir)
        {
            if (!Directory.Exists(parsedDataDir))
            {
                Console.Error.WriteLine("错误: parsed_data 目录不存在: " + parsedDataDir);
                return 2;
            }

            Console.WriteLine("=== D6 P1 逐点形态对比 dryrun ===");
            Console.WriteLine("parsed_data 目录: " + Path.GetFullPath(parsedDataDir));
            Console.WriteLine("Rules 目录: " + Path.GetFullPath(rulesDir));
            Console.WriteLine();

            // 加载所有参考曲线
            string refDir = Path.Combine(rulesDir, "reference_curves");
            var refCurves = ReferenceCurveStore.LoadAll(refDir);

            if (refCurves.Count == 0)
            {
                Console.WriteLine("[警告] 无参考曲线，请先运行 refcurve 子命令生成");
                return 0;
            }

            Console.WriteLine("已加载 {0} 条参考曲线", refCurves.Count);
            Console.WriteLine();

            // 加载 P1 阈值
            string thresholdsPath = Path.Combine(rulesDir, "thresholds.json");
            var thresholdStore = ThresholdStore.Load(thresholdsPath);
            var p1Threshold = thresholdStore.Get("P1");
            if (p1Threshold == null)
            {
                p1Threshold = new RuleThreshold { enabled = true, level = "预警", areaDiffRatioThreshold = 0.25, maxAbsDevRatio = 1.0 };
            }

            // 加载基线获取 refConvMean
            string baselinesPath = Path.Combine(rulesDir, "baselines.json");
            var baselineStore = BaselineStore.Load(baselinesPath);

            int totalChecked = 0;
            int p1Triggered = 0;

            Console.WriteLine("{0,-6} {1,5} | {2,6} | {3,6}", "道岔", "事件", "触发P1", "触发率");
            Console.WriteLine(new string('-', 40));

            foreach (var kvp in refCurves)
            {
                string switchId = kvp.Key;
                var refCurve = kvp.Value;

                double refConvMean = 0.301;
                if (baselineStore.Switches != null && baselineStore.Switches.ContainsKey(switchId))
                {
                    refConvMean = baselineStore.Switches[switchId].RefConvMean;
                }

                // 遍历 parsed_data 中该道岔的所有日数据
                string switchDir = Path.Combine(parsedDataDir, switchId);
                if (!Directory.Exists(switchDir)) continue;

                int switchChecked = 0;
                int switchTriggered = 0;

                foreach (var file in Directory.GetFiles(switchDir, "*.json"))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".diag.json") || fileName == "features.json")
                        continue;

                    var events = new IndexManager(parsedDataDir).LoadDayData(switchId,
                        fileName.Replace(".json", ""));
                    foreach (var evt in events)
                    {
                        var features = FeatureExtractor.Extract(evt);
                        if (!features.IsValid || features.IsFullWindow) continue;

                        var result = ProfileComparer.CompareP1WithThreshold(
                            features.RawValues, refCurve.Values,
                            features.SpikeIndex, refCurve.AlignIndex,
                            refConvMean, p1Threshold);

                        switchChecked++;
                        if (result != null) switchTriggered++;
                    }
                }

                totalChecked += switchChecked;
                p1Triggered += switchTriggered;

                double rate = switchChecked > 0 ? switchTriggered / (double)switchChecked * 100.0 : 0.0;
                Console.WriteLine("{0,-6} {1,5} | {2,6} | {3,5:F2}%",
                    switchId, switchChecked, switchTriggered, rate);
            }

            Console.WriteLine(new string('-', 40));
            double totalRate = totalChecked > 0 ? p1Triggered / (double)totalChecked * 100.0 : 0.0;
            Console.WriteLine("{0,-6} {1,5} | {2,6} | {3,5:F2}%",
                "合计", totalChecked, p1Triggered, totalRate);

            Console.WriteLine();
            Console.WriteLine("=== P1 dryrun 完成: {0}/{1} 触发 ({2:F2}%) ===",
                p1Triggered, totalChecked, totalRate);

            // 检查误报率
            if (totalRate > 1.0)
            {
                Console.WriteLine("[注意] P1 误报率 > 1%，建议调整阈值");
            }

            return 0;
        }

        // ═══════════════════════════════════════════════════════════════
        // 辅助方法
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 从 CSV 目录构建所有道岔的基线。
        /// </summary>
        private static BaselineStore BuildBaselinesFromCsv(string csvDir)
        {
            var store = new BaselineStore();
            store.Switches = new Dictionary<string, SwitchBaseline>();

            foreach (var kvp in PowerFileMap)
            {
                int fileNum = kvp.Key;
                string switchId = kvp.Value;
                string fileName = string.Format("SwitchCurve({0}).csv", fileNum);
                string filePath = Path.Combine(csvDir, fileName);

                if (!File.Exists(filePath))
                    continue;

                var rows = SimpleCsvReader.ReadPowerCsv(filePath);
                var allFeatures = new List<CurveFeatures>();
                var allDates = new List<string>();

                foreach (var row in rows)
                {
                    if (row.DateTimeStr != null && row.DateTimeStr.Length >= 10)
                    {
                        string date = row.DateTimeStr.Substring(0, 10);
                        allDates.Add(date);
                    }
                    var features = FeatureExtractor.Extract(row.Values);
                    allFeatures.Add(features);
                }

                string dateFrom = null;
                string dateTo = null;
                if (allDates.Count > 0)
                {
                    allDates.Sort();
                    dateFrom = allDates[0];
                    dateTo = allDates[allDates.Count - 1];
                }

                var baseline = BaselineBuilder.Build(allFeatures, 30);
                if (baseline != null)
                {
                    baseline.DateFrom = dateFrom;
                    baseline.DateTo = dateTo;
                    store.Switches[switchId] = baseline;
                }
            }

            return store;
        }

        static bool CheckField(string name, int actual, int expected, bool isDouble, ref bool allPass)
        {
            bool pass = actual == expected;
            if (!pass)
                Console.WriteLine("  [FAIL] " + name + ": 实际=" + actual + " 期望=" + expected);
            return pass;
        }

        static bool CheckField(string name, double actual, double expected, bool isDouble, ref bool allPass)
        {
            bool pass = Math.Abs(actual - expected) < 0.002;
            if (!pass)
                Console.WriteLine("  [FAIL] " + name + ": 实际=" + actual + " 期望=" + expected);
            return pass;
        }

        static bool CheckField(string name, bool actual, bool expected, bool isDouble, ref bool allPass)
        {
            bool pass = actual == expected;
            if (!pass)
                Console.WriteLine("  [FAIL] " + name + ": 实际=" + actual + " 期望=" + expected);
            return pass;
        }

        class GoldenFixture
        {
            public string Label;
            public string FileName;
            public int Timestamp;
            public string SwitchId;
            public CurveFeatures Expected;
            public CurveFeatures ActualFeatures; // 由 selftest 阶段1 填充
            public List<string> ExpectedDiag;     // 期望命中的规则 ID 列表
        }
    }
}
