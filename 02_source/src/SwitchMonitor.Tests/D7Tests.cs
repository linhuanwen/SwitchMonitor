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
    /// D7 Current Baseline Construction 测试套件
    /// 验证: CurrentFeatureExtractor / CurrentBaselineBuilder / CurrentBaselineStore / CurrentFeaturesStore
    /// </summary>
    public static class D7Tests
    {
        public static void Run()
        {
            // ═══ S1: CurrentFeatureExtractor 三相电流特征提取 ═══
            TestRunner.Test("CurrentFeatureExtractor 三相正常曲线提取20维特征", Test_CurrentExtract_ThreePhaseNormal);
            TestRunner.Test("CurrentFeatureExtractor ExtractPhase 单相分割", Test_CurrentExtractPhase_Segments);
            TestRunner.Test("CurrentFeatureExtractor 空数组返回 IsValid=false", Test_CurrentExtract_EmptyArray);
            TestRunner.Test("CurrentFeatureExtractor 全零数组返回 IsValid=false", Test_CurrentExtract_AllZero);
            TestRunner.Test("CurrentFeatureExtractor 缺少某相数据仍有效", Test_CurrentExtract_MissingPhase);
            TestRunner.Test("CurrentFeatureExtractor 短曲线 TailMean=0", Test_CurrentExtract_ShortCurve_TailZero);
            TestRunner.Test("CurrentFeatureExtractor FullWindow 检测", Test_CurrentExtract_FullWindow);
            TestRunner.Test("CurrentFeatureExtractor MaxUnbalanceRatio 计算", Test_CurrentExtract_UnbalanceRatio);
            TestRunner.Test("CurrentFeatureExtractor DurationSec 取三相最大值", Test_CurrentExtract_DurationSec);

            // ═══ S2: CurrentBaselineBuilder 基线构建 ═══
            TestRunner.Test("CurrentBaselineBuilder ≥30样本输出合法基线", Test_CurrentBaseline_Build30);
            TestRunner.Test("CurrentBaselineBuilder <30样本返回null", Test_CurrentBaseline_InsufficientSamples);
            TestRunner.Test("CurrentBaselineBuilder 排除 IsValid=false 的样本", Test_CurrentBaseline_ExcludesInvalid);
            TestRunner.Test("CurrentBaselineBuilder 排除 IsFullWindow=true 的样本", Test_CurrentBaseline_ExcludesFullWindow);
            TestRunner.Test("CurrentBaselineBuilder MAD过滤剔除离群值", Test_CurrentBaseline_MADFiltering);
            TestRunner.Test("CurrentBaselineBuilder 基线值精度正确", Test_CurrentBaseline_Precision);
            TestRunner.Test("CurrentBaselineBuilder null输入返回null", Test_CurrentBaseline_NullInput);
            TestRunner.Test("CurrentBaselineBuilder 空列表返回null", Test_CurrentBaseline_EmptyList);
            TestRunner.Test("CurrentBaselineBuilder 全部特征被过滤返回null", Test_CurrentBaseline_AllFiltered);
            TestRunner.Test("CurrentBaselineBuilder MAD过滤后样本不足返回null", Test_CurrentBaseline_MADRemovesAll);
            TestRunner.Test("CurrentBaselineBuilder 确定性输入验证精确基线值", Test_CurrentBaseline_DeterministicValues);

            // ═══ S3: CurrentBaselineStore 存储读写 ═══
            TestRunner.Test("CurrentBaselineStore Save+Load 可逆", Test_CurrentBaselineStore_Roundtrip);
            TestRunner.Test("CurrentBaselineStore 文件不存在返回空Store", Test_CurrentBaselineStore_FileNotExists);
            TestRunner.Test("CurrentBaselineStore JSON损坏返回空Store", Test_CurrentBaselineStore_CorruptJson);

            // ═══ S4: CurrentFeaturesStore 列式存储 ═══
            TestRunner.Test("CurrentFeaturesStore 列式格式序列化", Test_CurrentFeaturesStore_ColumnarFormat);
            TestRunner.Test("CurrentFeaturesStore 追加写入+读取", Test_CurrentFeaturesStore_AppendAndRead);
            TestRunner.Test("CurrentFeaturesStore 无效行也写入", Test_CurrentFeaturesStore_InvalidRowWritten);
            TestRunner.Test("CurrentFeaturesStore BackfillWithDir 回填", Test_CurrentFeaturesStore_Backfill);

            // ═══ S5: DiagnosisRunner 管道集成 (AC6) ═══
            TestRunner.Test("DiagnosisRunner.Run 追加电流特征到 current_features.json", Test_DiagRunner_AppendsCurrentFeatures);
            TestRunner.Test("DiagnosisRunner.Run parsedDataDir为null不抛异常", Test_DiagRunner_NullParsedDir);
            TestRunner.Test("DiagnosisRunner.Run 无效电流特征也写入", Test_DiagRunner_InvalidCurrentWritten);
        }

        // ──────────────────────────────────────────────
        // S1: CurrentFeatureExtractor
        // ──────────────────────────────────────────────

        static void Test_CurrentExtract_ThreePhaseNormal()
        {
            // 构建一条三相正常曲线：A/B/C 各 300 点，spikeIndex=6，duration≈11.76s
            var evt = MakeThreePhaseEvent(300, spikeIndex: 6, convMean: 2.8, activeEnd: 293);

            var f = CurrentFeatureExtractor.Extract(evt);

            TestRunner.AssertNotNull(f, "特征非null");
            TestRunner.AssertTrue(f.IsValid, "IsValid=true");
            TestRunner.AssertEqual(300, f.SampleCount, "SampleCount=300");
            TestRunner.AssertFalse(f.IsFullWindow, "IsFullWindow=false");

            // A 相特征
            TestRunner.AssertTrue(f.SpikePeakA > 0, "SpikePeakA > 0");
            TestRunner.AssertEqual(6, f.SpikeIndexA, "SpikeIndexA=6");
            TestRunner.AssertTrue(f.UnlockMeanA > 0, "UnlockMeanA > 0");
            TestRunner.AssertTrue(f.ConvMeanA > 0, "ConvMeanA > 0");
            TestRunner.AssertTrue(f.LockMeanA > 0, "LockMeanA > 0");
            TestRunner.AssertTrue(f.TailMeanA > 0, "TailMeanA > 0");

            // B 相非空
            TestRunner.AssertTrue(f.SpikePeakB > 0, "SpikePeakB > 0");
            // C 相非空
            TestRunner.AssertTrue(f.SpikePeakC > 0, "SpikePeakC > 0");

            // 三相汇总
            TestRunner.AssertTrue(f.DurationSec > 2.4, "DurationSec > 2.4");
            TestRunner.AssertTrue(f.MaxUnbalanceRatio >= 0, "MaxUnbalanceRatio >= 0");
        }

        static void Test_CurrentExtractPhase_Segments()
        {
            // 构造一条典型 A 相电流曲线：spikeIndex=6, activeEnd=293, convMean≈2.8
            var values = MakePhaseCurve(300, spikeIndex: 6, convMean: 2.8, activeEnd: 293);

            var f = CurrentFeatureExtractor.ExtractPhase(values);

            TestRunner.AssertTrue(f.IsValid, "IsValid=true");
            TestRunner.AssertEqual(300, f.SampleCount, "SampleCount");
            TestRunner.AssertEqual(6, f.SpikeIndexA, "SpikeIndex=6");
            TestRunner.AssertTrue(f.SpikePeakA >= 3.0, "SpikePeak >= 3.0（尖峰在索引6处）");
            TestRunner.AssertTrue(f.UnlockMeanA > 0, "UnlockMeanA > 0");
            TestRunner.AssertTrue(f.ConvMeanA > 0, "ConvMeanA > 0");
            // activeEnd=293 > 50，锁闭段应有效
            TestRunner.AssertTrue(f.LockMeanA > 0, "LockMeanA > 0（activeEnd>50）");
            // activeEnd=293 > 30，缓放段应有效
            TestRunner.AssertTrue(f.TailMeanA > 0, "TailMeanA > 0（activeEnd>30）");
        }

        static void Test_CurrentExtract_EmptyArray()
        {
            var evt = new SwitchEvent
            {
                CurrentA = new List<double[]>(),
                CurrentB = new List<double[]>(),
                CurrentC = new List<double[]>()
            };
            var f = CurrentFeatureExtractor.Extract(evt);
            TestRunner.AssertFalse(f.IsValid, "空数组 → IsValid=false");
        }

        static void Test_CurrentExtract_AllZero()
        {
            var evt = MakeThreePhaseEventWithValues(
                new double[] { 0, 0, 0, 0, 0 },
                new double[] { 0, 0, 0, 0, 0 },
                new double[] { 0, 0, 0, 0, 0 });
            var f = CurrentFeatureExtractor.Extract(evt);
            TestRunner.AssertFalse(f.IsValid, "全零 → IsValid=false");
        }

        static void Test_CurrentExtract_MissingPhase()
        {
            // B 相缺失（空列表），但 A 相和 C 相有数据
            var evt = MakeThreePhaseEventWithValues(
                new double[] { 0, 0.5, 4.0, 0.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 0.5, 0.3 },
                new double[0],
                new double[] { 0, 0.5, 4.0, 0.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 2.8, 0.5, 0.3 });
            var f = CurrentFeatureExtractor.Extract(evt);
            // 整体 IsValid 应为 true（至少有一相有效），但 B 相特征应为 0
            TestRunner.AssertTrue(f.IsValid, "部分相缺失 → IsValid=true（有有效相）");
            // B 相应为 0
            TestRunner.AssertEqual(0.0, f.SpikePeakB, 0.001, "B相缺失 → SpikePeakB=0");
        }

        static void Test_CurrentExtract_ShortCurve_TailZero()
        {
            // 短曲线：25点，activeEnd ≈ 20（≤30）
            var values = new double[] { 0, 0.5, 4.0, 0.8, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 2.5, 0.5, 0.3, 0.1, 0.01, 0, 0 };
            var f = CurrentFeatureExtractor.ExtractPhase(new List<double>(values));
            TestRunner.AssertTrue(f.IsValid, "短曲线 IsValid=true");
            TestRunner.AssertEqual(0.0, f.TailMeanA, 0.001, "activeEnd≤30 → TailMeanA=0");
            TestRunner.AssertEqual(0.0, f.LockMeanA, 0.001, "activeEnd≤50 → LockMeanA=0");
        }

        static void Test_CurrentExtract_FullWindow()
        {
            // 打满录制窗口：790点
            var evt = MakeThreePhaseEvent(790, spikeIndex: 6, convMean: 2.8, activeEnd: 783);
            var f = CurrentFeatureExtractor.Extract(evt);
            TestRunner.AssertTrue(f.IsFullWindow, "n≥780 → IsFullWindow=true");
        }

        static void Test_CurrentExtract_UnbalanceRatio()
        {
            // 构造三相不平衡：A=3.0, B=2.5, C=2.0
            var evt = MakeThreePhaseEventWithConvMean(3.0, 2.5, 2.0);

            var f = CurrentFeatureExtractor.Extract(evt);

            TestRunner.AssertTrue(f.MaxUnbalanceRatio > 0, "有差异 → MaxUnbalanceRatio > 0");
            // threePhaseMean = (3.0+2.5+2.0)/3 = 2.5
            // max deviation = max(|3.0-2.5|, |2.5-2.5|, |2.0-2.5|) = 0.5
            // ratio = 0.5/2.5 = 0.2
            TestRunner.AssertEqual(0.2, f.MaxUnbalanceRatio, 0.01, "MaxUnbalanceRatio≈0.2");
        }

        static void Test_CurrentExtract_DurationSec()
        {
            // A 相 activeEnd=200, B=250, C=220 → max=250 → duration=251×0.04=10.04
            var evt = MakeThreePhaseEventWithActiveEnds(200, 250, 220);
            var f = CurrentFeatureExtractor.Extract(evt);
            TestRunner.AssertEqual(10.04, f.DurationSec, 0.02, "DurationSec=max(activeEnd)+1×0.04");
        }

        // ──────────────────────────────────────────────
        // S2: CurrentBaselineBuilder
        // ──────────────────────────────────────────────

        static void Test_CurrentBaseline_Build30()
        {
            var features = MakeSyntheticFeatures(50, 0.001);
            var baseline = CurrentBaselineBuilder.Build(features, 30);

            TestRunner.AssertNotNull(baseline, "50条样本应输出基线");
            TestRunner.AssertTrue(baseline.SampleCount >= 30, "SampleCount ≥ 30");
            // 验证 20 维都有值
            TestRunner.AssertTrue(baseline.RefSpikePeakA > 0, "RefSpikePeakA > 0");
            TestRunner.AssertTrue(baseline.RefSpikeIndexA > 0, "RefSpikeIndexA > 0");
            TestRunner.AssertTrue(baseline.RefUnlockMeanA > 0, "RefUnlockMeanA > 0");
            TestRunner.AssertTrue(baseline.RefConvMeanA > 0, "RefConvMeanA > 0");
            TestRunner.AssertTrue(baseline.RefLockMeanA > 0, "RefLockMeanA > 0");
            TestRunner.AssertTrue(baseline.RefTailMeanA > 0, "RefTailMeanA > 0");
            // B 相
            TestRunner.AssertTrue(baseline.RefSpikePeakB > 0, "RefSpikePeakB > 0");
            // C 相
            TestRunner.AssertTrue(baseline.RefSpikePeakC > 0, "RefSpikePeakC > 0");
            // 汇总基线
            TestRunner.AssertTrue(baseline.RefDurationSec > 2.4, "RefDurationSec > 2.4");
            TestRunner.AssertTrue(baseline.RefMaxUnbalanceRatio >= 0, "RefMaxUnbalanceRatio >= 0");
        }

        static void Test_CurrentBaseline_InsufficientSamples()
        {
            var features = MakeSyntheticFeatures(20, 0.001);
            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertTrue(baseline == null, "<30条 → null");
        }

        static void Test_CurrentBaseline_ExcludesInvalid()
        {
            var features = new List<CurrentFeatures>();
            // 添加 10 条无效 + 35 条有效 = 45 条总计
            for (int i = 0; i < 10; i++)
            {
                features.Add(new CurrentFeatures { IsValid = false });
            }
            var valid = MakeSyntheticFeatures(35, 0.001);
            features.AddRange(valid);

            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertNotNull(baseline, "无效样本被排除后仍有≥30条");
        }

        static void Test_CurrentBaseline_ExcludesFullWindow()
        {
            var features = new List<CurrentFeatures>();
            // 添加 15 条 FullWindow
            var fw = MakeSyntheticFeatures(15, 0.001);
            foreach (var f in fw)
            {
                f.IsFullWindow = true;
                features.Add(f);
            }
            // 添加 35 条正常
            var normal = MakeSyntheticFeatures(35, 0.001);
            features.AddRange(normal);

            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertNotNull(baseline, "FullWindow 被排除后仍有≥30条");
        }

        static void Test_CurrentBaseline_MADFiltering()
        {
            var features = MakeSyntheticFeatures(35, 0.001);

            // 注入 5 条明显异常的样本（转换段均值偏离很大）
            for (int i = 0; i < 5; i++)
            {
                var outlier = MakeSingleFeature(2.8 * 2.0); // convMean ≈ 5.6，严重偏离
                features.Add(outlier);
            }

            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertNotNull(baseline, "MAD过滤后仍有≥30条正常样本");
            // 离群值被剔除，ConvMeanA 应接近 2.8，而非被拉高
            TestRunner.AssertEqual(2.8, baseline.RefConvMeanA, 0.3, "RefConvMeanA 未被离群值拉偏");
        }

        static void Test_CurrentBaseline_Precision()
        {
            var features = MakeSyntheticFeatures(40, 0.001);
            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertNotNull(baseline, "基线非null");

            // DurationSec 保留 2 位
            double durRounded = Math.Round(baseline.RefDurationSec, 2);
            TestRunner.AssertEqual(durRounded, baseline.RefDurationSec, 0.001, "DurationSec 保留2位");

            // SpikeIndex 应为整数
            TestRunner.AssertEqual((double)(int)baseline.RefSpikeIndexA, (double)baseline.RefSpikeIndexA, 0.001, "SpikeIndex 为整数");
        }

        static void Test_CurrentBaseline_NullInput()
        {
            var baseline = CurrentBaselineBuilder.Build(null, 30);
            TestRunner.AssertTrue(baseline == null, "null输入 → 返回null");
        }

        static void Test_CurrentBaseline_EmptyList()
        {
            var baseline = CurrentBaselineBuilder.Build(new List<CurrentFeatures>(), 30);
            TestRunner.AssertTrue(baseline == null, "空列表 → 返回null");
        }

        static void Test_CurrentBaseline_AllFiltered()
        {
            // 全部特征都不满足前置过滤条件
            var features = new List<CurrentFeatures>();
            // 10条 IsValid=false
            for (int i = 0; i < 10; i++)
                features.Add(new CurrentFeatures { IsValid = false });
            // 10条 IsFullWindow=true
            for (int i = 0; i < 10; i++)
                features.Add(new CurrentFeatures { IsValid = true, IsFullWindow = true, DurationSec = 11.72 });
            // 10条 DurationSec < 2.4
            for (int i = 0; i < 10; i++)
                features.Add(new CurrentFeatures { IsValid = true, IsFullWindow = false, DurationSec = 1.0 });

            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertTrue(baseline == null, "全部特征被前置过滤 → 返回null");
        }

        static void Test_CurrentBaseline_MADRemovesAll()
        {
            // 构造场景：5条正常样本 + 30条极端离群样本（ConvMean偏离10×）
            // 第一轮中位数被离群样本主导 → baseline_0 接近离群簇中心
            // MAD 很小（离群簇内聚）→ 5条正常样本距离超大 → 被剔除
            // 仅剩30条离群样本 → 第二轮中位数基于离群簇 → 保留数≥30
            // 验证：最终基线 RefConvMeanA 接近离群值（5.6），而非正常值（2.8）
            var features = new List<CurrentFeatures>();

            // 5条正常样本（ConvMeanA≈2.8）
            for (int i = 0; i < 5; i++)
            {
                features.Add(new CurrentFeatures
                {
                    IsValid = true, IsFullWindow = false,
                    SpikePeakA = 5.5, SpikeIndexA = 6,
                    UnlockMeanA = 3.2, ConvMeanA = 2.8,
                    LockMeanA = 1.5, TailMeanA = 1.7,
                    SpikePeakB = 5.45, SpikeIndexB = 6,
                    UnlockMeanB = 3.18, ConvMeanB = 2.78,
                    LockMeanB = 1.48, TailMeanB = 1.68,
                    SpikePeakC = 5.52, SpikeIndexC = 7,
                    UnlockMeanC = 3.22, ConvMeanC = 2.82,
                    LockMeanC = 1.52, TailMeanC = 1.72,
                    DurationSec = 11.72, MaxUnbalanceRatio = 0.03,
                    SampleCount = 300, ActiveEnd = 293
                });
            }

            // 30条离群样本（ConvMeanA≈5.6，偏差2×）
            for (int i = 0; i < 30; i++)
            {
                features.Add(new CurrentFeatures
                {
                    IsValid = true, IsFullWindow = false,
                    SpikePeakA = 5.5, SpikeIndexA = 6,
                    UnlockMeanA = 3.2, ConvMeanA = 5.6,
                    LockMeanA = 1.5, TailMeanA = 1.7,
                    SpikePeakB = 5.45, SpikeIndexB = 6,
                    UnlockMeanB = 3.18, ConvMeanB = 5.6 * 0.99,
                    LockMeanB = 1.48, TailMeanB = 1.68,
                    SpikePeakC = 5.52, SpikeIndexC = 7,
                    UnlockMeanC = 3.22, ConvMeanC = 5.6 * 1.01,
                    LockMeanC = 1.52, TailMeanC = 1.72,
                    DurationSec = 11.72, MaxUnbalanceRatio = 0.03,
                    SampleCount = 300, ActiveEnd = 293
                });
            }

            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertNotNull(baseline, "35条样本(5正常+30离群) → 基线非null");

            // 正常样本被MAD剔除，基线应反映离群簇的中心 ≈ 5.6
            TestRunner.AssertTrue(baseline.RefConvMeanA > 4.0,
                "RefConvMeanA > 4.0（离群簇主导基线，正常样本被剔除）");
            TestRunner.AssertTrue(baseline.SampleCount >= 28 && baseline.SampleCount <= 32,
                "SampleCount ≈ 30（离群簇保留，正常样本被剔除）");
        }

        static void Test_CurrentBaseline_DeterministicValues()
        {
            // 构造确定性输入（无噪声），验证基线输出精确值
            var features = new List<CurrentFeatures>();
            for (int i = 0; i < 50; i++)
            {
                features.Add(new CurrentFeatures
                {
                    IsValid = true, IsFullWindow = false,
                    // A 相
                    SpikePeakA = 5.500, SpikeIndexA = 6,
                    UnlockMeanA = 3.200, ConvMeanA = 2.800,
                    LockMeanA = 1.500, TailMeanA = 1.700,
                    // B 相
                    SpikePeakB = 5.450, SpikeIndexB = 6,
                    UnlockMeanB = 3.180, ConvMeanB = 2.780,
                    LockMeanB = 1.480, TailMeanB = 1.680,
                    // C 相
                    SpikePeakC = 5.520, SpikeIndexC = 7,
                    UnlockMeanC = 3.220, ConvMeanC = 2.820,
                    LockMeanC = 1.520, TailMeanC = 1.720,
                    // 汇总
                    DurationSec = 11.72,
                    MaxUnbalanceRatio = 0.030,
                    SampleCount = 300, ActiveEnd = 293
                });
            }

            var baseline = CurrentBaselineBuilder.Build(features, 30);
            TestRunner.AssertNotNull(baseline, "50条相同样本 → 基线非null");

            // 中位数应精确等于输入值（所有值相同）
            TestRunner.AssertEqual(5.500, baseline.RefSpikePeakA, 0.001, "RefSpikePeakA=5.500");
            TestRunner.AssertEqual(6, baseline.RefSpikeIndexA, "RefSpikeIndexA=6");
            TestRunner.AssertEqual(3.200, baseline.RefUnlockMeanA, 0.001, "RefUnlockMeanA=3.200");
            TestRunner.AssertEqual(2.800, baseline.RefConvMeanA, 0.001, "RefConvMeanA=2.800");
            TestRunner.AssertEqual(1.500, baseline.RefLockMeanA, 0.001, "RefLockMeanA=1.500");
            TestRunner.AssertEqual(1.700, baseline.RefTailMeanA, 0.001, "RefTailMeanA=1.700");

            TestRunner.AssertEqual(5.450, baseline.RefSpikePeakB, 0.001, "RefSpikePeakB=5.450");
            TestRunner.AssertEqual(5.520, baseline.RefSpikePeakC, 0.001, "RefSpikePeakC=5.520");
            TestRunner.AssertEqual(7, baseline.RefSpikeIndexC, "RefSpikeIndexC=7");

            TestRunner.AssertEqual(11.72, baseline.RefDurationSec, 0.01, "RefDurationSec=11.72");
            TestRunner.AssertEqual(0.030, baseline.RefMaxUnbalanceRatio, 0.001, "RefMaxUnbalanceRatio=0.030");

            // 50条全保留（MAD=0，距离=0 → 全部保留）
            TestRunner.AssertTrue(baseline.SampleCount >= 45, "MAD过滤后保留≥45条");
        }

        // ──────────────────────────────────────────────
        // S3: CurrentBaselineStore
        // ──────────────────────────────────────────────

        static void Test_CurrentBaselineStore_Roundtrip()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string path = Path.Combine(tempDir, "current_baselines.json");

                var store = new CurrentBaselineStore();
                store.ComputedAt = "2026-07-14 15:00:00";
                store.Switches = new Dictionary<string, CurrentBaseline>();
                store.Switches["1-J"] = new CurrentBaseline
                {
                    RefSpikePeakA = 5.500, RefSpikeIndexA = 6,
                    RefUnlockMeanA = 3.200, RefConvMeanA = 2.800,
                    RefLockMeanA = 1.500, RefTailMeanA = 1.700,
                    RefSpikePeakB = 5.450, RefSpikeIndexB = 6,
                    RefUnlockMeanB = 3.180, RefConvMeanB = 2.780,
                    RefLockMeanB = 1.480, RefTailMeanB = 1.680,
                    RefSpikePeakC = 5.520, RefSpikeIndexC = 7,
                    RefUnlockMeanC = 3.220, RefConvMeanC = 2.820,
                    RefLockMeanC = 1.520, RefTailMeanC = 1.720,
                    RefDurationSec = 11.72,
                    RefMaxUnbalanceRatio = 0.03,
                    SampleCount = 2840,
                    DateFrom = "2025-12-13",
                    DateTo = "2026-06-29"
                };

                store.Save(path);

                TestRunner.AssertFileExists(path, "current_baselines.json 已保存");

                var loaded = CurrentBaselineStore.Load(path);
                TestRunner.AssertNotNull(loaded, "加载非null");
                TestRunner.AssertEqual("2026-07-14 15:00:00", loaded.ComputedAt, "ComputedAt 一致");
                TestRunner.AssertTrue(loaded.Switches.ContainsKey("1-J"), "含 switchId 1-1");

                var bl = loaded.Switches["1-J"];
                TestRunner.AssertEqual(5.500, bl.RefSpikePeakA, 0.001, "RefSpikePeakA");
                TestRunner.AssertEqual(6, bl.RefSpikeIndexA, "RefSpikeIndexA");
                TestRunner.AssertEqual(3.200, bl.RefUnlockMeanA, 0.001, "RefUnlockMeanA");
                TestRunner.AssertEqual(2.800, bl.RefConvMeanA, 0.001, "RefConvMeanA");
                TestRunner.AssertEqual(11.72, bl.RefDurationSec, 0.01, "RefDurationSec");
                TestRunner.AssertEqual(0.03, bl.RefMaxUnbalanceRatio, 0.001, "RefMaxUnbalanceRatio");
                TestRunner.AssertEqual(2840, bl.SampleCount, "SampleCount");
                TestRunner.AssertEqual("2025-12-13", bl.DateFrom, "DateFrom");
                TestRunner.AssertEqual("2026-06-29", bl.DateTo, "DateTo");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_CurrentBaselineStore_FileNotExists()
        {
            string path = Path.Combine(TestRunner.TempDir(), "nonexistent", "current_baselines.json");
            try
            {
                var store = CurrentBaselineStore.Load(path);
                TestRunner.AssertNotNull(store, "不存在文件 → 返回空Store（非null）");
                TestRunner.AssertNotNull(store.Switches, "Switches 字典已初始化");
                TestRunner.AssertEqual(0, store.Switches.Count, "Switches 为空");
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(path), true); } catch { }
            }
        }

        static void Test_CurrentBaselineStore_CorruptJson()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string path = Path.Combine(tempDir, "current_baselines.json");
                File.WriteAllText(path, "this is not valid json {{{", Encoding.UTF8);

                var store = CurrentBaselineStore.Load(path);
                TestRunner.AssertNotNull(store, "损坏 JSON → 返回空Store（不抛异常）");
                TestRunner.AssertEqual(0, store.Switches.Count, "Switches 为空");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // S4: CurrentFeaturesStore
        // ──────────────────────────────────────────────

        static void Test_CurrentFeaturesStore_ColumnarFormat()
        {
            var store = new CurrentFeaturesStore();
            store.Columns = new List<string>(CurrentFeaturesStore.DefaultColumns);
            store.Rows = new List<List<double>>();
            store.Rows.Add(new List<double> { 1770922311, 11.76, 0.03, 5.500, 6, 3.200, 2.800, 1.500, 1.700, 5.450, 6, 3.180, 2.780, 1.480, 1.680, 5.520, 7, 3.220, 2.820, 1.520, 1.720, 1.0, 0.0, 300, 293 });

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(store);

            TestRunner.AssertTrue(json.Contains("\"Columns\""), "含 Columns 键");
            TestRunner.AssertTrue(json.Contains("\"Rows\""), "含 Rows 键");
            TestRunner.AssertTrue(json.Contains("\"timestamp\""), "含 timestamp 列名");
            TestRunner.AssertTrue(json.Contains("\"spikePeakA\""), "含 spikePeakA 列名");
            TestRunner.AssertTrue(json.Contains("\"maxUnbalanceRatio\""), "含 maxUnbalanceRatio 列名");
            TestRunner.AssertFalse(json.Contains("\"timestamp\":1770922311"), "列式不含逐行键名");
        }

        static void Test_CurrentFeaturesStore_AppendAndRead()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string switchId = "1-J";

                // 使用 Append 写入两行
                var f1 = MakeSingleFeature(2.8);
                CurrentFeaturesStore.Append(tempDir, switchId, 1770922311, f1);

                var f2 = MakeSingleFeature(2.85);
                CurrentFeaturesStore.Append(tempDir, switchId, 1770922400, f2);

                string filePath = Path.Combine(tempDir, switchId, "current_features.json");
                TestRunner.AssertFileExists(filePath, "current_features.json 已生成");

                var loaded = CurrentFeaturesStore.Load(filePath);
                TestRunner.AssertNotNull(loaded, "加载非null");
                TestRunner.AssertEqual(25, loaded.Columns.Count, "25 列（21 特征 + 4 过滤元数据）");
                TestRunner.AssertEqual(2, loaded.Rows.Count, "2 行");

                // 验证 RowFromCurrentFeatures 的列顺序
                int tsIdx = loaded.ColumnIndex("timestamp");
                TestRunner.AssertEqual(0, tsIdx, "timestamp 是第一列");
                TestRunner.AssertEqual(1770922311.0, loaded.Rows[0][tsIdx], 0.1, "第一行 timestamp");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_CurrentFeaturesStore_InvalidRowWritten()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string switchId = "1-J";

                // 写入一条无效特征
                var invalid = new CurrentFeatures { IsValid = false };
                CurrentFeaturesStore.Append(tempDir, switchId, 1770922311, invalid);

                string filePath = Path.Combine(tempDir, switchId, "current_features.json");
                var loaded = CurrentFeaturesStore.Load(filePath);
                TestRunner.AssertNotNull(loaded, "无效行也写入");
                TestRunner.AssertEqual(1, loaded.Rows.Count, "1 行");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_CurrentFeaturesStore_Backfill()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string parsedDir = Path.Combine(tempDir, "parsed_data");
                var im = new IndexManager(parsedDir);
                im.Initialize();

                var events = new List<SwitchEvent>
                {
                    MakeThreePhaseEvent(300, spikeIndex: 6, convMean: 2.8, activeEnd: 293),
                    MakeThreePhaseEvent(298, spikeIndex: 6, convMean: 2.82, activeEnd: 291)
                };
                events[0].Timestamp = 1770922311;
                events[0].DateTimeStr = "2026-02-13 02:51:51";
                events[1].Timestamp = 1770922400;
                events[1].DateTimeStr = "2026-02-13 06:16:30";
                im.SaveDayData("1-J", "2026-02-13", events);

                int backfilled = CurrentFeaturesStore.BackfillWithDir(im, "1-J", parsedDir);
                TestRunner.AssertEqual(2, backfilled, "回填行数=2");

                string featuresPath = Path.Combine(parsedDir, "1-J", "current_features.json");
                TestRunner.AssertFileExists(featuresPath, "current_features.json 已生成");

                var loaded = CurrentFeaturesStore.Load(featuresPath);
                TestRunner.AssertNotNull(loaded, "加载非null");
                TestRunner.AssertEqual(2, loaded.Rows.Count, "2 行");
                // 验证 contains expected columns
                TestRunner.AssertTrue(loaded.ColumnIndex("spikePeakA") >= 0, "含 spikePeakA 列");
                TestRunner.AssertTrue(loaded.ColumnIndex("convMeanA") >= 0, "含 convMeanA 列");
                TestRunner.AssertTrue(loaded.ColumnIndex("maxUnbalanceRatio") >= 0, "含 maxUnbalanceRatio 列");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // S5: DiagnosisRunner 管道集成 (AC6)
        // ──────────────────────────────────────────────

        /// <summary>AC6: Run() 带 parsedDataDir 时创建 current_features.json</summary>
        static void Test_DiagRunner_AppendsCurrentFeatures()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string rulesDir = Path.Combine(tempDir, "Rules");
                string parsedDir = Path.Combine(tempDir, "parsed_data");
                Directory.CreateDirectory(rulesDir);
                Directory.CreateDirectory(parsedDir);

                // 1. 准备 Rules/thresholds.json（内置默认阈值）
                string thresholdsJson = @"{""version"":1,""rules"":{""R1"":{""enabled"":true,""level"":""故障"",""durOverRefSeconds"":3.0},""R2"":{""enabled"":true,""level"":""报警"",""durUnderRefRatio"":0.6},""R3"":{""enabled"":true,""level"":""预警"",""maxDeviationSeconds"":0.5},""R4"":{""enabled"":true,""level"":""预警"",""overRefRatio"":1.3},""R5"":{""enabled"":true,""level"":""预警"",""overRefRatio"":1.3},""R6"":{""enabled"":true,""level"":""报警"",""maxStepRatio"":1.5,""minStepRatio"":0.67},""R7"":{""enabled"":true,""level"":""预警"",""overRefRatio"":1.3},""R8"":{""enabled"":true,""level"":""预警"",""deviationRatio"":0.3},""R9"":{""enabled"":false,""level"":""预警"",""deviationRatio"":0.3}}}";
                File.WriteAllText(Path.Combine(rulesDir, "thresholds.json"), thresholdsJson, Encoding.UTF8);

                // 2. 准备 Rules/baselines.json（为测试道岔 "1-J" 建基线）
                var baselineStore = new BaselineStore();
                baselineStore.ComputedAt = "2026-07-14 10:00:00";
                baselineStore.Switches = new Dictionary<string, SwitchBaseline>
                {
                    { "1-J", new SwitchBaseline
                    {
                        RefDurationSec = 11.72, RefSpikePeak = 3.235,
                        RefUnlockMean = 0.307, RefConvMean = 0.300,
                        RefLockMean = 0.300, RefTailMean = 0.213,
                        SampleCount = 100, DateFrom = "2025-12-13", DateTo = "2026-06-29"
                    }}
                };
                baselineStore.Save(Path.Combine(rulesDir, "baselines.json"));

                // 3. 初始化引擎
                var engine = new DiagnosisEngine();
                engine.Initialize(rulesDir);

                // 4. 构造 SwitchEvent（含功率 + 三相电流）
                string switchId = "1-J";
                var evt = MakeThreePhaseEvent(300, spikeIndex: 6, convMean: 0.300, activeEnd: 293);
                evt.Timestamp = 1770922311;
                evt.DateTimeStr = "2026-02-13 02:51:51";
                // 填入功率数据（模拟正常功率曲线，使功率特征 IsValid=true）
                evt.Power = MakeCurrentPairs(MakePowerCurve(300, 6, 0.300, 293));

                // 5. 执行诊断
                var result = DiagnosisRunner.Run(engine, switchId, evt, parsedDir);
                TestRunner.AssertNotNull(result, "诊断结果非null");
                TestRunner.AssertEqual("正常", result.Level, "正常曲线 → 诊断正常");

                // 6. 验证 current_features.json 已创建
                string cfPath = Path.Combine(parsedDir, switchId, "current_features.json");
                TestRunner.AssertFileExists(cfPath, "current_features.json 已创建");

                // 7. 验证内容正确
                var loaded = CurrentFeaturesStore.Load(cfPath);
                TestRunner.AssertNotNull(loaded, "加载 current_features.json 非null");
                TestRunner.AssertEqual(25, loaded.Columns.Count, "25 列");
                TestRunner.AssertEqual(1, loaded.Rows.Count, "1 行");

                // 验证 timestamp
                int tsIdx = loaded.ColumnIndex("timestamp");
                TestRunner.AssertTrue(tsIdx >= 0, "含 timestamp 列");
                TestRunner.AssertEqual(1770922311.0, loaded.Rows[0][tsIdx], 0.1, "timestamp 正确");

                // 验证关键电流特征列存在
                TestRunner.AssertTrue(loaded.ColumnIndex("spikePeakA") >= 0, "含 spikePeakA");
                TestRunner.AssertTrue(loaded.ColumnIndex("convMeanA") >= 0, "含 convMeanA");
                TestRunner.AssertTrue(loaded.ColumnIndex("maxUnbalanceRatio") >= 0, "含 maxUnbalanceRatio");
                TestRunner.AssertTrue(loaded.ColumnIndex("durationSec") >= 0, "含 durationSec");
                TestRunner.AssertTrue(loaded.ColumnIndex("isValid") >= 0, "含 isValid");

                // 验证当前行的 IsValid=1.0（有效）
                int validIdx = loaded.ColumnIndex("isValid");
                TestRunner.AssertEqual(1.0, loaded.Rows[0][validIdx], 0.01, "IsValid=1.0（三相有效）");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>AC6 边界：parsedDataDir=null 不抛异常，不创建文件</summary>
        static void Test_DiagRunner_NullParsedDir()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string rulesDir = Path.Combine(tempDir, "Rules");
                Directory.CreateDirectory(rulesDir);

                // 准备最小 Rules
                string thresholdsJson = @"{""version"":1,""rules"":{""R1"":{""enabled"":true,""level"":""故障"",""durOverRefSeconds"":3.0}}}";
                File.WriteAllText(Path.Combine(rulesDir, "thresholds.json"), thresholdsJson, Encoding.UTF8);

                var baselineStore2 = new BaselineStore();
                baselineStore2.Switches = new Dictionary<string, SwitchBaseline>
                {
                    { "1-J", new SwitchBaseline { RefDurationSec = 11.72, SampleCount = 100 } }
                };
                baselineStore2.Save(Path.Combine(rulesDir, "baselines.json"));

                var engine = new DiagnosisEngine();
                engine.Initialize(rulesDir);

                // 构造事件
                var evt = MakeThreePhaseEvent(300, spikeIndex: 6, convMean: 0.300, activeEnd: 293);
                evt.Timestamp = 1770922311;
                evt.DateTimeStr = "2026-02-13 02:51:51";
                evt.Power = MakeCurrentPairs(MakePowerCurve(300, 6, 0.300, 293));

                // parsedDataDir = null → 不抛异常
                EventDiagnosis result = null;
                Exception caught = null;
                try
                {
                    result = DiagnosisRunner.Run(engine, "1-J", evt, null);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                TestRunner.AssertTrue(caught == null, "parsedDataDir=null 不抛异常");
                TestRunner.AssertNotNull(result, "仍然返回诊断结果");
                TestRunner.AssertEqual("正常", result.Level, "诊断正常");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>AC6 边界：无效电流特征也写入（保留审计痕迹）</summary>
        static void Test_DiagRunner_InvalidCurrentWritten()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string rulesDir = Path.Combine(tempDir, "Rules");
                string parsedDir = Path.Combine(tempDir, "parsed_data");
                Directory.CreateDirectory(rulesDir);
                Directory.CreateDirectory(parsedDir);

                // 准备 Rules
                string thresholdsJson = @"{""version"":1,""rules"":{""R1"":{""enabled"":true,""level"":""故障"",""durOverRefSeconds"":3.0}}}";
                File.WriteAllText(Path.Combine(rulesDir, "thresholds.json"), thresholdsJson, Encoding.UTF8);

                var blStore = new BaselineStore();
                blStore.Switches = new Dictionary<string, SwitchBaseline>
                {
                    { "1-J", new SwitchBaseline { RefDurationSec = 11.72, SampleCount = 100 } }
                };
                blStore.Save(Path.Combine(rulesDir, "baselines.json"));

                var engine = new DiagnosisEngine();
                engine.Initialize(rulesDir);

                // 构造事件：功率有效，但电流全为空
                var evt = new SwitchEvent
                {
                    Timestamp = 1770922311,
                    DateTimeStr = "2026-02-13 02:51:51",
                    Power = MakeCurrentPairs(MakePowerCurve(300, 6, 0.300, 293)),
                    CurrentA = new List<double[]>(),
                    CurrentB = new List<double[]>(),
                    CurrentC = new List<double[]>()
                };

                // 执行诊断（不抛异常）
                EventDiagnosis result = null;
                Exception caught = null;
                try
                {
                    result = DiagnosisRunner.Run(engine, "1-J", evt, parsedDir);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }

                TestRunner.AssertTrue(caught == null, "空电流不抛异常");
                TestRunner.AssertNotNull(result, "诊断结果非null");

                // 验证 current_features.json 已写入（即使 IsValid=false）
                string cfPath = Path.Combine(parsedDir, "1-J", "current_features.json");
                TestRunner.AssertFileExists(cfPath, "电流为空 → 仍写入 current_features.json");

                var loaded = CurrentFeaturesStore.Load(cfPath);
                TestRunner.AssertNotNull(loaded, "加载非null");
                TestRunner.AssertEqual(1, loaded.Rows.Count, "1 行");

                // IsValid 应为 0.0
                int validIdx = loaded.ColumnIndex("isValid");
                TestRunner.AssertTrue(validIdx >= 0, "含 isValid 列");
                TestRunner.AssertEqual(0.0, loaded.Rows[0][validIdx], 0.01, "IsValid=0.0（审计痕迹）");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // 测试辅助方法
        // ──────────────────────────────────────────────

        /// <summary>构造三相事件（A/B/C 曲线形状相同）</summary>
        private static SwitchEvent MakeThreePhaseEvent(int length, int spikeIndex, double convMean, int activeEnd)
        {
            var evt = new SwitchEvent
            {
                Timestamp = 1770922311,
                DateTimeStr = "2026-02-13 02:51:51",
                SampleCount = length,
                SampleInterval = 0.04,
                Duration = (activeEnd + 1) * 0.04
            };

            evt.CurrentA = MakeCurrentPairs(MakePhaseCurve(length, spikeIndex, convMean, activeEnd));
            evt.CurrentB = MakeCurrentPairs(MakePhaseCurve(length, spikeIndex, convMean * 0.99, activeEnd));
            evt.CurrentC = MakeCurrentPairs(MakePhaseCurve(length, spikeIndex, convMean * 1.01, activeEnd));

            return evt;
        }

        /// <summary>构造三相事件（指定各相 activeEnd）</summary>
        private static SwitchEvent MakeThreePhaseEventWithActiveEnds(int activeEndA, int activeEndB, int activeEndC)
        {
            int length = 300;
            var evt = new SwitchEvent
            {
                Timestamp = 1770922311,
                DateTimeStr = "2026-02-13 02:51:51",
                SampleCount = length,
                SampleInterval = 0.04
            };
            evt.CurrentA = MakeCurrentPairs(MakePhaseCurve(length, 6, 2.8, activeEndA));
            evt.CurrentB = MakeCurrentPairs(MakePhaseCurve(length, 6, 2.8, activeEndB));
            evt.CurrentC = MakeCurrentPairs(MakePhaseCurve(length, 6, 2.8, activeEndC));
            return evt;
        }

        /// <summary>构造三相事件（指定各相 convMean）</summary>
        private static SwitchEvent MakeThreePhaseEventWithConvMean(double convA, double convB, double convC)
        {
            int length = 300;
            var evt = new SwitchEvent
            {
                Timestamp = 1770922311,
                DateTimeStr = "2026-02-13 02:51:51",
                SampleCount = length,
                SampleInterval = 0.04
            };
            evt.CurrentA = MakeCurrentPairs(MakePhaseCurve(length, 6, convA, 293));
            evt.CurrentB = MakeCurrentPairs(MakePhaseCurve(length, 6, convB, 293));
            evt.CurrentC = MakeCurrentPairs(MakePhaseCurve(length, 6, convC, 293));
            return evt;
        }

        /// <summary>用原始值数组构造三相事件</summary>
        private static SwitchEvent MakeThreePhaseEventWithValues(double[] a, double[] b, double[] c)
        {
            var evt = new SwitchEvent
            {
                Timestamp = 1770922311,
                DateTimeStr = "2026-02-13 02:51:51",
                SampleCount = Math.Max(a.Length, Math.Max(b.Length, c.Length)),
                SampleInterval = 0.04
            };
            evt.CurrentA = MakeCurrentPairs(new List<double>(a));
            evt.CurrentB = MakeCurrentPairs(new List<double>(b));
            evt.CurrentC = MakeCurrentPairs(new List<double>(c));
            return evt;
        }

        /// <summary>构造单相电流曲线（模拟真实道岔电流波形）</summary>
        private static List<double> MakePhaseCurve(int length, int spikeIndex, double convMean, int activeEnd)
        {
            var curve = new List<double>();
            var rng = new Random(spikeIndex * 100 + (int)(convMean * 1000));

            for (int i = 0; i < length; i++)
            {
                double v;
                if (i < 3)
                {
                    // 前 3 点：零填充（录制窗口前余量）
                    v = 0.0;
                }
                else if (i == spikeIndex)
                {
                    // 启动尖峰：约 4-6A
                    v = 4.5 + rng.NextDouble() * 1.5;
                }
                else if (i > spikeIndex && i < spikeIndex + 5)
                {
                    // 尖峰后的快速下降
                    v = 2.5 + rng.NextDouble() * 1.0;
                }
                else if (i > length - 35)
                {
                    // 尾部区域
                    if (i > activeEnd)
                    {
                        // activeEnd 之后为零填充
                        v = 0.0;
                    }
                    else if (i >= activeEnd - 22 && i < activeEnd - 2)
                    {
                        // 缓放段：低平台 ≈ 1.5-1.8A
                        v = 1.7 + (rng.NextDouble() - 0.5) * 0.1;
                    }
                    else if (i >= activeEnd - 40 && i < activeEnd - 22)
                    {
                        // 锁闭段：更低的凹口 ≈ 1.4-1.6A
                        v = 1.5 + (rng.NextDouble() - 0.5) * 0.1;
                    }
                    else
                    {
                        v = 1.7 + (rng.NextDouble() - 0.5) * 0.2;
                    }
                }
                else if (i > activeEnd)
                {
                    v = 0.0;
                }
                else
                {
                    // 转换段主体：稳定平台
                    v = convMean + (rng.NextDouble() - 0.5) * 0.2;
                }

                curve.Add(Math.Round(Math.Max(0, v), 3));
            }

            return curve;
        }

        /// <summary>将 List<double> 转为 List<double[]>（[t, v] 对）</summary>
        private static List<double[]> MakeCurrentPairs(List<double> values)
        {
            var pairs = new List<double[]>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                pairs.Add(new double[] { Math.Round(i * 0.04, 3), values[i] });
            }
            return pairs;
        }

        /// <summary>制造 n 条合成正常电流特征（三相近似相同，带微小噪声）</summary>
        private static List<CurrentFeatures> MakeSyntheticFeatures(int n, double noiseScale)
        {
            var list = new List<CurrentFeatures>();
            var rng = new Random(42);
            for (int i = 0; i < n; i++)
            {
                Func<double> noise = () => (rng.NextDouble() - 0.5) * noiseScale * 2;
                list.Add(new CurrentFeatures
                {
                    IsValid = true,
                    IsFullWindow = false,
                    // A 相
                    SpikePeakA = 5.5 + noise(),
                    SpikeIndexA = 6,
                    UnlockMeanA = 3.2 + noise(),
                    ConvMeanA = 2.8 + noise(),
                    LockMeanA = 1.5 + noise(),
                    TailMeanA = 1.7 + noise(),
                    // B 相
                    SpikePeakB = 5.45 + noise(),
                    SpikeIndexB = 6,
                    UnlockMeanB = 3.18 + noise(),
                    ConvMeanB = 2.78 + noise(),
                    LockMeanB = 1.48 + noise(),
                    TailMeanB = 1.68 + noise(),
                    // C 相
                    SpikePeakC = 5.52 + noise(),
                    SpikeIndexC = 7,
                    UnlockMeanC = 3.22 + noise(),
                    ConvMeanC = 2.82 + noise(),
                    LockMeanC = 1.52 + noise(),
                    TailMeanC = 1.72 + noise(),
                    // 汇总
                    DurationSec = 11.72 + noise() * 0.1,
                    MaxUnbalanceRatio = 0.03 + Math.Abs(noise() * 0.01),
                    // 元数据
                    SampleCount = 300,
                    ActiveEnd = 293
                });
            }
            return list;
        }

        /// <summary>制造单条合成电流特征</summary>
        private static CurrentFeatures MakeSingleFeature(double convMeanA)
        {
            return new CurrentFeatures
            {
                IsValid = true,
                IsFullWindow = false,
                SpikePeakA = 5.5, SpikeIndexA = 6,
                UnlockMeanA = 3.2, ConvMeanA = convMeanA,
                LockMeanA = 1.5, TailMeanA = 1.7,
                SpikePeakB = 5.45, SpikeIndexB = 6,
                UnlockMeanB = 3.18, ConvMeanB = convMeanA * 0.99,
                LockMeanB = 1.48, TailMeanB = 1.68,
                SpikePeakC = 5.52, SpikeIndexC = 7,
                UnlockMeanC = 3.22, ConvMeanC = convMeanA * 1.01,
                LockMeanC = 1.52, TailMeanC = 1.72,
                DurationSec = 11.72,
                MaxUnbalanceRatio = 0.03,
                SampleCount = 300,
                ActiveEnd = 293
            };
        }

        /// <summary>构造正常功率曲线（模拟真实道岔功率波形，kW）</summary>
        private static List<double> MakePowerCurve(int length, int spikeIndex, double convMean, int activeEnd)
        {
            var curve = new List<double>();
            var rng = new Random(spikeIndex * 200 + (int)(convMean * 1000));

            for (int i = 0; i < length; i++)
            {
                double v;
                if (i < 3)
                {
                    v = 0.0;
                }
                else if (i == spikeIndex)
                {
                    // 启动尖峰：约 2.5-3.5kW
                    v = 2.8 + rng.NextDouble() * 0.7;
                }
                else if (i > spikeIndex && i < spikeIndex + 5)
                {
                    // 尖峰后快速下降到解锁段
                    v = 0.28 + rng.NextDouble() * 0.1;
                }
                else if (i > length - 35)
                {
                    if (i > activeEnd)
                    {
                        v = 0.0;
                    }
                    else if (i >= activeEnd - 22 && i < activeEnd - 2)
                    {
                        // 缓放段：低平台 ≈ 0.21kW
                        v = 0.21 + (rng.NextDouble() - 0.5) * 0.02;
                    }
                    else if (i >= activeEnd - 40 && i < activeEnd - 22)
                    {
                        // 锁闭段
                        v = 0.30 + (rng.NextDouble() - 0.5) * 0.02;
                    }
                    else
                    {
                        v = 0.21 + (rng.NextDouble() - 0.5) * 0.04;
                    }
                }
                else if (i > activeEnd)
                {
                    v = 0.0;
                }
                else
                {
                    // 转换段主体：稳定平台
                    v = convMean + (rng.NextDouble() - 0.5) * 0.04;
                }

                curve.Add(Math.Round(Math.Max(0, v), 3));
            }

            return curve;
        }
    }
}
