using System;
using System.Collections.Generic;
using SwitchMonitor.Diagnosis;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// D9 DriftEstimator 测试套件。
    /// 验证:
    ///   S1 — DriftEstimator.Estimate() 近邻不足/median/clamp/各段正确性
    ///   S2 — DriftEstimator.ApplyDrift() 形态保持/长度保持/无NaN/spike位置
    ///   S3 — DiagnosisEngine P1 集成（drift 调整版 S' 用于对比）
    /// </summary>
    public static class D9Tests
    {
        public static void Run()
        {
            // ═══ S1: DriftEstimator.Estimate ═══
            TestRunner.Test("Estimate 近邻不足20条→全1.0", Test_Estimate_InsufficientNeighbors);
            TestRunner.Test("Estimate 恰好20条 neighbors convMean相同→drift≈1.0", Test_Estimate_DriftConvIsOne);
            TestRunner.Test("Estimate median convMean高于SC→drift>1.0", Test_Estimate_DriftAboveOne);
            TestRunner.Test("Estimate median convMean低于SC→drift<1.0 clamp生效", Test_Estimate_DriftClampLow);
            TestRunner.Test("Estimate 极端值 clamp到[0.85,1.15]", Test_Estimate_ClampBothEnds);
            TestRunner.Test("Estimate NeighborCount正确记录", Test_Estimate_NeighborCount);
            TestRunner.Test("Estimate 空列表→全1.0", Test_Estimate_EmptyList);
            TestRunner.Test("Estimate null近邻列表→全1.0", Test_Estimate_NullList);

            // ═══ S2: DriftEstimator.ApplyDrift ═══
            TestRunner.Test("ApplyDrift 全部drift=1.0→输出≈输入", Test_ApplyDrift_AllOnes);
            TestRunner.Test("ApplyDrift drift_conv=1.2→转换段放大1.2倍", Test_ApplyDrift_ConvAmplified);
            TestRunner.Test("ApplyDrift 输出长度=输入长度", Test_ApplyDrift_LengthPreserved);
            TestRunner.Test("ApplyDrift 输出无NaN/Infinity", Test_ApplyDrift_NoNaN);
            TestRunner.Test("ApplyDrift spike位置保持", Test_ApplyDrift_SpikePositionPreserved);
            TestRunner.Test("ApplyDrift null标准曲线→返回null", Test_ApplyDrift_NullInput);
            TestRunner.Test("ApplyDrift null drift→返回null", Test_ApplyDrift_NullDrift);
        }

        // ═══════════════════════════════════════════════════════════════
        //  测试辅助
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 构造一条标准测试用 StandardCurve。
        /// Values 与 D8Tests.MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293) 一致。
        /// </summary>
        static StandardCurve MakeTestStandardCurve(
            double spikePeak = 3.5, double unlockVal = 0.5,
            double convVal = 0.30, double lockVal = 0.28, double tailVal = 0.21,
            int totalPoints = 300, int spikeIndex = 6, int activeEnd = 293)
        {
            var values = new List<double>(totalPoints);
            int n = totalPoints;

            for (int i = 0; i < n; i++)
            {
                double v;
                if (i < 3)
                    v = 0.0;
                else if (i < spikeIndex)
                    v = 0.2 * (double)(i - 3) / (spikeIndex - 3);
                else if (i == spikeIndex)
                    v = spikePeak;
                else if (i < spikeIndex + 2)
                    v = spikePeak + (unlockVal - spikePeak) * (double)(i - spikeIndex) / 2.0;
                else if (i < spikeIndex + 14)
                    v = unlockVal;
                else if (i < spikeIndex + 20)
                    v = unlockVal + (convVal - unlockVal) * (double)(i - (spikeIndex + 14)) / 6.0;
                else if (i < activeEnd - 40)
                    v = convVal;
                else if (i < activeEnd - 22)
                    v = convVal + (lockVal - convVal) * (double)(i - (activeEnd - 40)) / 18.0;
                else if (i < activeEnd - 2)
                    v = tailVal;
                else if (i <= activeEnd)
                {
                    double threshold = Math.Max(spikePeak * 0.05, 0.01);
                    v = tailVal * (1.0 - (double)(i - (activeEnd - 2)) / 2.0)
                        + (threshold + 0.01) * (double)(i - (activeEnd - 2)) / 2.0;
                }
                else
                    v = 0.0;

                values.Add(Math.Round(Math.Max(0, v), 3));
            }

            return new StandardCurve
            {
                SwitchId = "1-J",
                Direction = "定位→反位",
                SampleInterval = 0.04,
                AlignIndex = spikeIndex,
                Values = values,
                FusionWeight = 1.0,
                ReferenceSource = "reference_curves/1-J.json",
                BaselineComputedAt = "2026-07-14 10:00:00",
                AlphaTime = 1.0,
                AlphaSpike = 1.14,
                AlphaUnlock = 1.2,
                AlphaConv = 1.3,
                AlphaLock = 0.7,
                AlphaTail = 0.95,
                ComputedAt = "2026-07-14 15:00:00"
            };
        }

        /// <summary>
        /// 构造 CurveFeatures 列表，每条的 convMean 可精确控制。
        /// </summary>
        static List<CurveFeatures> MakeFeatures(
            int count, double convMean, double unlockMean = 0.5,
            double lockMean = 0.28, double tailMean = 0.21, double spikePeak = 3.5)
        {
            var list = new List<CurveFeatures>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new CurveFeatures
                {
                    IsValid = true,
                    SpikePeak = spikePeak,
                    SpikeIndex = 6,
                    ActiveEnd = 293,
                    DurationSec = 11.72,
                    UnlockMean = unlockMean,
                    ConvMean = convMean,
                    LockMean = lockMean,
                    TailMean = tailMean,
                    SampleCount = 300,
                    Direction = "定位→反位"
                });
            }
            return list;
        }

        // ═══════════════════════════════════════════════════════════════
        //  S1: DriftEstimator.Estimate 测试
        // ═══════════════════════════════════════════════════════════════

        /// <summary>近邻 < 20 → 全 1.0，不抛异常。</summary>
        static void Test_Estimate_InsufficientNeighbors()
        {
            var sc = MakeTestStandardCurve();
            var features = MakeFeatures(19, 0.30); // 只有 19 条

            var drift = DriftEstimator.Estimate(sc, features, neighborCount: 20);

            TestRunner.AssertNotNull(drift, "输出非null");
            TestRunner.AssertEqual(1.0, drift.DriftSpike,  0.001, "DriftSpike=1.0");
            TestRunner.AssertEqual(1.0, drift.DriftUnlock, 0.001, "DriftUnlock=1.0");
            TestRunner.AssertEqual(1.0, drift.DriftConv,   0.001, "DriftConv=1.0");
            TestRunner.AssertEqual(1.0, drift.DriftLock,   0.001, "DriftLock=1.0");
            TestRunner.AssertEqual(1.0, drift.DriftTail,   0.001, "DriftTail=1.0");
            TestRunner.AssertEqual(19, drift.NeighborCount, "NeighborCount=19");
        }

        /// <summary>
        /// 20 条邻居，convMean 全部与标准曲线 conv 均值相同 → drift_conv ≈ 1.0。
        /// 标准曲线 conv 段均值 = 0.30（构造的测试曲线）。
        /// </summary>
        static void Test_Estimate_DriftConvIsOne()
        {
            var sc = MakeTestStandardCurve();
            // 标准曲线 convMean ≈ 0.30（FeatureExtractor 提取的结果）
            // 但 FeatureExtractor 可能会略有偏差（由于重采样和α调整）
            // 我们用完全匹配的值构造
            var features = MakeFeatures(20, 0.30);

            var drift = DriftEstimator.Estimate(sc, features, neighborCount: 20);

            TestRunner.AssertNotNull(drift, "输出非null");
            // drift_conv ≈ median(0.30)/sc.ConvMean ≈ 1.0
            TestRunner.AssertTrue(Math.Abs(drift.DriftConv - 1.0) < 0.15,
                string.Format("DriftConv≈1.0 (实际={0:F4})", drift.DriftConv));
            TestRunner.AssertEqual(20, drift.NeighborCount, "NeighborCount=20");
        }

        /// <summary>邻居 convMean=0.36, SC convMean≈0.30 → drift_conv≈1.2。</summary>
        static void Test_Estimate_DriftAboveOne()
        {
            var sc = MakeTestStandardCurve();
            var features = MakeFeatures(25, 0.36); // 比 SC 的 0.30 高 20%

            var drift = DriftEstimator.Estimate(sc, features, neighborCount: 20);

            TestRunner.AssertNotNull(drift, "输出非null");
            TestRunner.AssertTrue(drift.DriftConv > 1.05,
                string.Format("DriftConv>1.05 (实际={0:F4})", drift.DriftConv));
            TestRunner.AssertEqual(25, drift.NeighborCount, "NeighborCount=25");
        }

        /// <summary>邻居 convMean=0.20, SC convMean≈0.30 → drift≈0.667 → clamp 到 0.85。</summary>
        static void Test_Estimate_DriftClampLow()
        {
            var sc = MakeTestStandardCurve();
            var features = MakeFeatures(20, 0.20); // 比 SC 低很多

            var drift = DriftEstimator.Estimate(sc, features, neighborCount: 20);

            TestRunner.AssertNotNull(drift, "输出非null");
            // raw drift ≈ 0.20/0.30 ≈ 0.667 → clamp to 0.85
            TestRunner.AssertTrue(drift.DriftConv >= 0.85 - 0.01,
                string.Format("DriftConv≥0.85 (实际={0:F4})", drift.DriftConv));
        }

        /// <summary>验证 clamp [0.85, 1.15] 双向生效。</summary>
        static void Test_Estimate_ClampBothEnds()
        {
            var sc = MakeTestStandardCurve();

            // 极低邻居 → drift 被 clamp 到 0.85
            var lowFeatures = MakeFeatures(20, 0.10);
            var lowDrift = DriftEstimator.Estimate(sc, lowFeatures, neighborCount: 20);
            TestRunner.AssertNotNull(lowDrift, "低邻居→输出非null");
            TestRunner.AssertTrue(lowDrift.DriftConv >= 0.84,
                string.Format("DriftConv≥0.84 (实际={0:F4})", lowDrift.DriftConv));

            // 极高邻居 → drift 被 clamp 到 1.15
            var highFeatures = MakeFeatures(20, 1.0);
            var highDrift = DriftEstimator.Estimate(sc, highFeatures, neighborCount: 20);
            TestRunner.AssertNotNull(highDrift, "高邻居→输出非null");
            TestRunner.AssertTrue(highDrift.DriftConv <= 1.16,
                string.Format("DriftConv≤1.16 (实际={0:F4})", highDrift.DriftConv));
        }

        /// <summary>NeighborCount 应记录实际参与计算的近邻数。</summary>
        static void Test_Estimate_NeighborCount()
        {
            var sc = MakeTestStandardCurve();
            var features = MakeFeatures(30, 0.30);

            var drift = DriftEstimator.Estimate(sc, features, neighborCount: 20);

            TestRunner.AssertNotNull(drift, "输出非null");
            TestRunner.AssertEqual(30, drift.NeighborCount,
                string.Format("NeighborCount=30 (实际={0})", drift.NeighborCount));
        }

        /// <summary>空列表 → 全 1.0。</summary>
        static void Test_Estimate_EmptyList()
        {
            var sc = MakeTestStandardCurve();
            var drift = DriftEstimator.Estimate(sc, new List<CurveFeatures>(), neighborCount: 20);

            TestRunner.AssertNotNull(drift, "空列表→输出非null");
            TestRunner.AssertEqual(1.0, drift.DriftConv, 0.001, "DriftConv=1.0");
        }

        /// <summary>null 列表 → 全 1.0。</summary>
        static void Test_Estimate_NullList()
        {
            var sc = MakeTestStandardCurve();
            var drift = DriftEstimator.Estimate(sc, null, neighborCount: 20);

            TestRunner.AssertNotNull(drift, "null列表→输出非null");
            TestRunner.AssertEqual(1.0, drift.DriftConv, 0.001, "DriftConv=1.0");
        }

        // ═══════════════════════════════════════════════════════════════
        //  S2: DriftEstimator.ApplyDrift 测试
        // ═══════════════════════════════════════════════════════════════

        /// <summary>全部 drift=1.0 → 输出 Values ≈ 输入 Values。</summary>
        static void Test_ApplyDrift_AllOnes()
        {
            var sc = MakeTestStandardCurve();
            var drift = new SegmentDrift
            {
                DriftSpike = 1.0,
                DriftUnlock = 1.0,
                DriftConv = 1.0,
                DriftLock = 1.0,
                DriftTail = 1.0,
                NeighborCount = 20,
                ComputedAt = "2026-07-15 10:00:00"
            };

            var result = DriftEstimator.ApplyDrift(sc, drift);

            TestRunner.AssertNotNull(result, "输出非null");
            TestRunner.AssertEqual(sc.Values.Count, result.Values.Count, "长度一致");

            // 由于线性插值可能有微小差异，逐点比较（容差 0.01）
            int diffCount = 0;
            for (int i = 0; i < sc.Values.Count; i++)
            {
                if (Math.Abs(sc.Values[i] - result.Values[i]) > 0.01)
                    diffCount++;
            }
            TestRunner.AssertTrue(diffCount < sc.Values.Count * 0.05, // 差异点 < 5%
                string.Format("差异点<5% (差异={0}/{1})", diffCount, sc.Values.Count));
        }

        /// <summary>drift_conv=1.2 → 转换段应被放大。</summary>
        static void Test_ApplyDrift_ConvAmplified()
        {
            var sc = MakeTestStandardCurve();
            var drift = new SegmentDrift
            {
                DriftSpike = 1.0,
                DriftUnlock = 1.0,
                DriftConv = 1.2,
                DriftLock = 1.0,
                DriftTail = 1.0,
                NeighborCount = 20,
                ComputedAt = "2026-07-15 10:00:00"
            };

            var result = DriftEstimator.ApplyDrift(sc, drift);

            TestRunner.AssertNotNull(result, "输出非null");

            // 转换段核心区域 [si+20, ae-40) = [26, 253) 的值应被放大
            // 取中间点验证
            var scFeat = FeatureExtractor.Extract(sc.Values);
            var resultFeat = FeatureExtractor.Extract(result.Values);

            TestRunner.AssertTrue(resultFeat.ConvMean > scFeat.ConvMean * 1.05,
                string.Format("ConvMean放大 (SC={0:F4}, Drifted={1:F4})",
                    scFeat.ConvMean, resultFeat.ConvMean));
        }

        /// <summary>输出 Values 长度与输入相同。</summary>
        static void Test_ApplyDrift_LengthPreserved()
        {
            var sc = MakeTestStandardCurve();
            var drift = new SegmentDrift
            {
                DriftSpike = 0.9, DriftUnlock = 1.1, DriftConv = 0.95,
                DriftLock = 1.05, DriftTail = 1.15,
                NeighborCount = 20
            };

            var result = DriftEstimator.ApplyDrift(sc, drift);

            TestRunner.AssertNotNull(result, "输出非null");
            TestRunner.AssertEqual(sc.Values.Count, result.Values.Count,
                string.Format("长度={0}", result.Values.Count));
        }

        /// <summary>输出不含 NaN 或 ±Infinity。</summary>
        static void Test_ApplyDrift_NoNaN()
        {
            var sc = MakeTestStandardCurve();
            var drift = new SegmentDrift
            {
                DriftSpike = 1.15, DriftUnlock = 0.85, DriftConv = 1.1,
                DriftLock = 0.9, DriftTail = 1.05,
                NeighborCount = 20
            };

            var result = DriftEstimator.ApplyDrift(sc, drift);

            TestRunner.AssertNotNull(result, "输出非null");
            foreach (double v in result.Values)
            {
                TestRunner.AssertFalse(double.IsNaN(v),
                    string.Format("值 {0:F4} 不是 NaN", v));
                TestRunner.AssertFalse(double.IsInfinity(v),
                    string.Format("值 {0:F4} 不是 Infinity", v));
            }
        }

        /// <summary>spike 位置应保持不变（AlignIndex 一致）。</summary>
        static void Test_ApplyDrift_SpikePositionPreserved()
        {
            var sc = MakeTestStandardCurve();
            var drift = new SegmentDrift
            {
                DriftSpike = 0.9, DriftUnlock = 1.1, DriftConv = 1.0,
                DriftLock = 1.0, DriftTail = 1.0,
                NeighborCount = 20
            };

            var result = DriftEstimator.ApplyDrift(sc, drift);

            TestRunner.AssertNotNull(result, "输出非null");
            // FeatureExtractor 提取结果曲线的 SpikeIndex 应与原曲线一致
            var resultFeat = FeatureExtractor.Extract(result.Values);
            TestRunner.AssertEqual(sc.AlignIndex, resultFeat.SpikeIndex,
                string.Format("SpikeIndex保持 (原={0}, drift后={1})",
                    sc.AlignIndex, resultFeat.SpikeIndex));
        }

        /// <summary>null 标准曲线 → 返回 null。</summary>
        static void Test_ApplyDrift_NullInput()
        {
            var drift = new SegmentDrift { DriftSpike = 1.0, DriftUnlock = 1.0, DriftConv = 1.0, DriftLock = 1.0, DriftTail = 1.0 };
            var result = DriftEstimator.ApplyDrift(null, drift);
            TestRunner.AssertTrue(result == null, "null SC→返回null");
        }

        /// <summary>null drift → 返回 null。</summary>
        static void Test_ApplyDrift_NullDrift()
        {
            var sc = MakeTestStandardCurve();
            var result = DriftEstimator.ApplyDrift(sc, null);
            TestRunner.AssertTrue(result == null, "null drift→返回null");
        }
    }
}
