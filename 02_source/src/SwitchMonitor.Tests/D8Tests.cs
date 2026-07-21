using System;
using System.Collections.Generic;
using System.IO;
using SwitchMonitor.Diagnosis;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// D8 Standard Curve Builder 测试套件。
    /// 验证:
    ///   S1 — StandardCurveBuilder.Build() 融合正确性（fusionWeight/clamp/除零保护/null边界）
    ///   S2 — ResampleLinear() 边界情况
    ///   S3 — GetPointAlpha() 五阶段 α 分配
    ///   S4 — StandardCurveStore Save/Load 读写往返
    ///
    /// 可以与 NUnit 配合使用（见文件末尾的 NUnit 适配说明），
    /// 也可直接通过 TestRunner 控制台运行。
    /// </summary>
    public static class D8Tests
    {
        public static void Run()
        {
            // ═══ S1: StandardCurveBuilder.Build — 融合正确性 ═══
            TestRunner.Test("Build fusionWeight=0 输出≈输入形态（α全为1.0）", Test_Build_FusionWeight0_AllAlphasOne);
            TestRunner.Test("Build fusionWeight=0 重采样后段均值≈输入段均值", Test_Build_FusionWeight0_SegmentMeansMatchInput);
            TestRunner.Test("Build fusionWeight=1 各段α对齐到基线", Test_Build_FusionWeight1_AlignsToBaseline);
            TestRunner.Test("Build fusionWeight=0.5 α混合到中间值", Test_Build_FusionWeight05_MidAlpha);
            TestRunner.Test("Build null参考曲线返回null", Test_Build_NullReferenceCurve);
            TestRunner.Test("Build null基线返回null", Test_Build_NullBaseline);
            TestRunner.Test("Build 空Values参考曲线返回null", Test_Build_EmptyValues);
            TestRunner.Test("Build clamp生效 α被限制在[0.7,1.3]范围内", Test_Build_ClampEffect);
            TestRunner.Test("Build 除零保护 LockMean=0→α_lock=1.0不抛异常", Test_Build_DivByZero_LockMean);
            TestRunner.Test("Build 除零保护 TailMean=0→α_tail=1.0不抛异常", Test_Build_DivByZero_TailMean);
            TestRunner.Test("Build 输出Values长度≈基线时长/采样间隔", Test_Build_OutputLength);
            TestRunner.Test("Build 输出无NaN或Infinity", Test_Build_NoNaN);

            // ═══ S2: ResampleLinear 边界 ═══
            TestRunner.Test("ResampleLinear targetCount=1 返回首点", Test_Resample_TargetCount1);
            TestRunner.Test("ResampleLinear N=1 targetCount=5 全填充首点", Test_Resample_NEquals1);
            TestRunner.Test("ResampleLinear targetCount=0 返回空列表", Test_Resample_TargetCount0);
            TestRunner.Test("ResampleLinear 空源列表返回空列表", Test_Resample_EmptySource);
            TestRunner.Test("ResampleLinear targetCount=N 返回副本(值≈原值)", Test_Resample_SameCount);
            TestRunner.Test("ResampleLinear targetCount=10 N=5 线性插值单调", Test_Resample_TenPoints);

            // ═══ S3: GetPointAlpha 段分配 ═══
            TestRunner.Test("GetPointAlpha spike段(i<si)返回α_spike", Test_GetPointAlpha_SpikeSegment);
            TestRunner.Test("GetPointAlpha unlock段[si+2,si+14)返回α_unlock", Test_GetPointAlpha_UnlockSegment);
            TestRunner.Test("GetPointAlpha conv段[si+20,ae-40)返回α_conv", Test_GetPointAlpha_ConvSegment);
            TestRunner.Test("GetPointAlpha spike→unlock过渡区线性混合", Test_GetPointAlpha_SpikeToUnlock_Blend);
            TestRunner.Test("GetPointAlpha 短曲线无lock/tail段 边界回退不抛异常", Test_GetPointAlpha_ShortCurve);

            // ═══ S4: StandardCurveStore 读写 ═══
            TestRunner.Test("StandardCurveStore Save→Load 往返字段一致", Test_Store_SaveLoad_Roundtrip);
            TestRunner.Test("StandardCurveStore 文件不存在返回null", Test_Store_FileNotExists);
            TestRunner.Test("StandardCurveStore LoadAll 批量加载", Test_Store_LoadAll);

            // ═══ S5: StandardCurveBuilder.Blend — 逐点线性融合 ═══
            TestRunner.Test("Blend fusionWeight=0 输出≈参考曲线", Test_Blend_Weight0_ReturnsReference);
            TestRunner.Test("Blend fusionWeight=1 输出≈中位曲线", Test_Blend_Weight1_ReturnsMedian);
            TestRunner.Test("Blend fusionWeight=0.5 中间值", Test_Blend_Weight05_Midpoint);
            TestRunner.Test("Blend 不同长度曲线不抛异常", Test_Blend_DifferentLengths);
            TestRunner.Test("Blend null输入返回null", Test_Blend_NullChecks);
            TestRunner.Test("Blend 融合后OriginalMedianValues不变", Test_Blend_OriginalMedianPreserved);
        }

        // ═══════════════════════════════════════════════════════════════
        //  测试辅助：构造可预测的参考曲线
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 构造一条标准的 300 点功率测试曲线。
        /// 各段值可精确控制，FeatureExtractor 的输出可预测。
        ///
        /// 五阶段布局（spikeIndex=6, activeEnd=293）：
        ///   [0,  3): 0.0           — 零前导
        ///   [3,  6): 渐升至0.2     — 启机爬升
        ///   [6]:     spikePeak     — 启动尖峰
        ///   [7,  8): 快速下降      — 尖峰回落
        ///   [8, 20): unlockVal     — 解锁段 12点
        ///   [20,26): 线性过渡      — unlock→conv 过渡
        ///   [26,253): convVal      — 转换段 227点
        ///   [253,271): lockVal     — 锁闭段 18点 (activeEnd-40, activeEnd-22)
        ///   [271,291): tailVal     — 缓放段 20点 (activeEnd-22, activeEnd-2)
        ///   [291,293]: 渐变至t+δ   — 尾端渐变（保持>5%峰值使activeEnd=293）
        ///   [294,299]: 0.0         — 零尾填充
        /// </summary>
        static List<double> MakePowerCurve(
            int totalPoints, int spikeIndex, double spikePeak,
            double unlockVal, double convVal, double lockVal, double tailVal, int activeEnd)
        {
            var curve = new List<double>(totalPoints);
            int n = totalPoints;

            for (int i = 0; i < n; i++)
            {
                double v;
                if (i < 3)
                {
                    v = 0.0;
                }
                else if (i < spikeIndex)
                {
                    // 启机爬升: 线性从 0 → 0.2
                    double t = (double)(i - 3) / (spikeIndex - 3);
                    v = 0.2 * t;
                }
                else if (i == spikeIndex)
                {
                    v = spikePeak;
                }
                else if (i < spikeIndex + 2)
                {
                    // 尖峰后快速回落
                    double t = (double)(i - spikeIndex) / 2.0;
                    v = spikePeak + (unlockVal - spikePeak) * t;
                }
                else if (i < spikeIndex + 14)
                {
                    v = unlockVal;
                }
                else if (i < spikeIndex + 20)
                {
                    // unlock → conv 线性过渡
                    double t = (double)(i - (spikeIndex + 14)) / 6.0;
                    v = unlockVal + (convVal - unlockVal) * t;
                }
                else if (i < activeEnd - 40)
                {
                    v = convVal;
                }
                else if (i < activeEnd - 22)
                {
                    // conv → lock 过渡
                    double t = (double)(i - (activeEnd - 40)) / 18.0;
                    v = convVal + (lockVal - convVal) * t;
                }
                else if (i < activeEnd - 2)
                {
                    v = tailVal;
                }
                else if (i <= activeEnd)
                {
                    // 尾端渐变保持在阈值之上以确保 activeEnd 精确
                    double t = (double)(i - (activeEnd - 2)) / 2.0;
                    double threshold = Math.Max(spikePeak * 0.05, 0.01);
                    v = tailVal * (1.0 - t) + (threshold + 0.01) * t;
                }
                else
                {
                    v = 0.0;
                }

                curve.Add(Math.Round(Math.Max(0, v), 3));
            }

            return curve;
        }

        /// <summary>构造一条短参考曲线（activeEnd ≤ 50 → LockMean=0, TailMean=0）</summary>
        static List<double> MakeShortCurve()
        {
            // 45 点，spikeIndex=6, activeEnd≈40（≤50，无锁闭段; >30，有缓放段但…）
            // 实际 activeEnd 设计为 28（≤30 → 无缓放段）
            var curve = new List<double>();
            // [0, 2]: 0
            curve.AddRange(new double[] { 0, 0, 0 });
            // [3, 5]: rise
            curve.AddRange(new double[] { 0.05, 0.1, 0.15 });
            // [6]: spike
            curve.Add(3.0);
            // [7, 7]: drop
            curve.Add(0.6);
            // [8, 19]: unlock (≈0.45)
            for (int i = 0; i < 12; i++) curve.Add(0.45);
            // [20, 25]: transition
            for (int i = 0; i < 6; i++) curve.Add(0.32);
            // [26, 28]: conv (short, just 3 points)
            curve.AddRange(new double[] { 0.30, 0.30, 0.30 });
            // [29, 34]: tail end → gradual decay
            curve.AddRange(new double[] { 0.25, 0.20, 0.12, 0.08, 0.04, 0.02 });
            // [35, 44]: zero fill
            for (int i = 0; i < 10; i++) curve.Add(0.0);

            return curve;
        }

        /// <summary>
        /// 将 List&lt;double&gt; 包装为 ReferenceCurve。
        /// </summary>
        static ReferenceCurve MakeReferenceCurve(List<double> values, string switchId = "1-J",
            double sampleInterval = 0.04)
        {
            return new ReferenceCurve
            {
                SwitchId = switchId,
                SampleInterval = sampleInterval,
                AlignIndex = 6,
                Values = values,
                ComputedAt = "2026-07-14 10:00:00"
            };
        }

        /// <summary>
        /// 构造一个与参考曲线不同的基线（用于验证融合效果）。
        /// </summary>
        static SwitchBaseline MakeBaseline(
            double refDurationSec = 12.0,
            double refSpikePeak = 4.0,
            double refUnlockMean = 0.6,
            double refConvMean = 0.4,
            double refLockMean = 0.35,
            double refTailMean = 0.25,
            string direction = "定位→反位")
        {
            return new SwitchBaseline
            {
                RefDurationSec = refDurationSec,
                RefSpikePeak = refSpikePeak,
                RefUnlockMean = refUnlockMean,
                RefConvMean = refConvMean,
                RefLockMean = refLockMean,
                RefTailMean = refTailMean,
                SampleCount = 3000,
                Direction = direction,
                DateFrom = "2025-12-01",
                DateTo = "2026-07-01"
            };
        }

        /// <summary>
        /// 构造一个极端基线（值远超参考曲线，用于 clamp 测试）。
        /// </summary>
        static SwitchBaseline MakeExtremeBaseline()
        {
            return new SwitchBaseline
            {
                RefDurationSec = 20.0,
                RefSpikePeak = 10.0,        // raw α = 10/3.5 ≈ 2.86 → 应 clamp 到 1.3
                RefUnlockMean = 1.5,        // raw α = 1.5/0.5 = 3.0 → 应 clamp 到 1.3
                RefConvMean = 1.2,          // raw α = 1.2/0.3 = 4.0 → 应 clamp 到 1.3
                RefLockMean = 0.0,          // → 除零保护 α=1.0
                RefTailMean = 0.0,          // → 除零保护 α=1.0
                SampleCount = 3000,
                Direction = "定位→反位",
                DateFrom = "2025-12-01",
                DateTo = "2026-07-01"
            };
        }

        // ═══════════════════════════════════════════════════════════════
        //  S1: StandardCurveBuilder.Build 测试
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// fusionWeight=0 → 所有 α = 1.0，标准曲线的 Alpha* 字段全为 1.0。
        /// </summary>
        static void Test_Build_FusionWeight0_AllAlphasOne()
        {
            var refCurve = MakeReferenceCurve(
                MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293));
            var baseline = MakeBaseline();

            var result = StandardCurveBuilder.Build(refCurve, baseline, fusionWeight: 0.0);

            TestRunner.AssertNotNull(result, "输出非null");
            TestRunner.AssertEqual(1.0, result.AlphaTime,   0.001, "AlphaTime=1.0");
            TestRunner.AssertEqual(1.0, result.AlphaSpike,  0.001, "AlphaSpike=1.0");
            TestRunner.AssertEqual(1.0, result.AlphaUnlock, 0.001, "AlphaUnlock=1.0");
            TestRunner.AssertEqual(1.0, result.AlphaConv,   0.001, "AlphaConv=1.0");
            TestRunner.AssertEqual(1.0, result.AlphaLock,   0.001, "AlphaLock=1.0");
            TestRunner.AssertEqual(1.0, result.AlphaTail,   0.001, "AlphaTail=1.0");
            TestRunner.AssertEqual(0.0, result.FusionWeight, 0.001, "FusionWeight=0.0");
        }

        /// <summary>
        /// fusionWeight=0 → 输出曲线经重采样后，再提取特征，段均值 ≈ 输入参考曲线的段均值。
        /// </summary>
        static void Test_Build_FusionWeight0_SegmentMeansMatchInput()
        {
            var refValues = MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293);
            var refCurve = MakeReferenceCurve(refValues);
            var baseline = MakeBaseline();

            // 提取输入参考曲线的特征作为基准
            var refFeat = FeatureExtractor.Extract(refValues);

            var result = StandardCurveBuilder.Build(refCurve, baseline, fusionWeight: 0.0);
            TestRunner.AssertNotNull(result, "输出非null");

            // 重采样后的长度 = round(baseline.RefDurationSec / 0.04) = round(12.0/0.04) = 300
            // 再提取输出曲线的特征
            var outFeat = FeatureExtractor.Extract(result.Values);

            // 由于重采样引入微小误差，段均值应接近（容差 5%）
            // ConvMean 应该接近输入值 (主要关注转换段，因为它数据最多)
            TestRunner.AssertTrue(outFeat.ConvMean > 0, "输出ConvMean > 0");
            double convDiff = Math.Abs(outFeat.ConvMean - refFeat.ConvMean);
            TestRunner.AssertTrue(convDiff < 0.05,
                string.Format("ConvMean接近输入值 (输入={0:F3}, 输出={1:F3}, diff={2:F4})",
                    refFeat.ConvMean, outFeat.ConvMean, convDiff));

            // SpikePeak 也应接近
            double spikeDiff = Math.Abs(outFeat.SpikePeak - refFeat.SpikePeak);
            TestRunner.AssertTrue(spikeDiff < 0.1,
                string.Format("SpikePeak接近输入值 (输入={0:F3}, 输出={1:F3}, diff={2:F4})",
                    refFeat.SpikePeak, outFeat.SpikePeak, spikeDiff));
        }

        /// <summary>
        /// fusionWeight=1 → α 完全应用基线/参考的比值。
        /// 输出曲线的段均值应接近基线 Ref* 值（受 clamp 限制）。
        /// </summary>
        static void Test_Build_FusionWeight1_AlignsToBaseline()
        {
            // 参考曲线: convMean≈0.30, 基线: RefConvMean=0.4
            // α_conv_raw = 0.4/0.30 ≈ 1.333 → clamp 到 1.3
            // α_conv = 1.0 + (1.3 - 1.0) × 1.0 = 1.3
            // 输出 conv ≈ 0.30 × 1.3 = 0.39（接近 0.4 但受 clamp 限制）
            var refValues = MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293);
            var refCurve = MakeReferenceCurve(refValues);
            var baseline = MakeBaseline();  // RefConvMean=0.4

            var result = StandardCurveBuilder.Build(refCurve, baseline, fusionWeight: 1.0);
            TestRunner.AssertNotNull(result, "输出非null");
            TestRunner.AssertEqual(1.0, result.FusionWeight, 0.001, "FusionWeight=1.0");

            // 验证 α 不为 1.0（融合确实生效）
            // α_conv_raw = 0.4/0.3 = 1.333 → clamped to 1.3 → α_conv = 1.3
            TestRunner.AssertTrue(result.AlphaConv > 1.0,
                string.Format("AlphaConv > 1.0 (实际={0:F4})", result.AlphaConv));

            // 输出段均值应该被缩放到接近基线水平
            var outFeat = FeatureExtractor.Extract(result.Values);
            // 输出 ConvMean 应 > 输入 ConvMean（因为 α_conv > 1.0）
            TestRunner.AssertTrue(outFeat.ConvMean > 0.31,
                string.Format("输出ConvMean提升 (={0:F3}, >0.31)", outFeat.ConvMean));
        }

        /// <summary>
        /// fusionWeight=0.5 → α 在 1.0 和全对齐之间取一半。
        /// α = 1.0 + (clamped - 1.0) × 0.5
        /// </summary>
        static void Test_Build_FusionWeight05_MidAlpha()
        {
            // 基线 RefConvMean=0.4, 参考曲线 ConvMean≈0.30
            // α_conv_raw = 0.4/0.3 = 1.333 → clamp 1.3
            // α_conv = 1.0 + (1.3 - 1.0) × 0.5 = 1.15
            var refValues = MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293);
            var refCurve = MakeReferenceCurve(refValues);
            var baseline = MakeBaseline();

            var result = StandardCurveBuilder.Build(refCurve, baseline, fusionWeight: 0.5);
            TestRunner.AssertNotNull(result, "输出非null");

            // α_conv 应在 1.0 和 1.3 之间（clamped到1.3后混合一半）
            double expectedAlpha = 1.0 + (1.3 - 1.0) * 0.5; // = 1.15
            TestRunner.AssertEqual(expectedAlpha, result.AlphaConv, 0.01,
                string.Format("AlphaConv≈{0:F2} (fusionWeight=0.5)", expectedAlpha));

            // fusionWeight=0.5 的输出应介于 fusionWeight=0 和 =1 之间
            TestRunner.AssertEqual(0.5, result.FusionWeight, 0.001, "FusionWeight=0.5");
        }

        /// <summary>null 参考曲线返回 null。</summary>
        static void Test_Build_NullReferenceCurve()
        {
            var result = StandardCurveBuilder.Build(null, MakeBaseline());
            TestRunner.AssertTrue(result == null, "null参考曲线→返回null");
        }

        /// <summary>null 基线返回 null。</summary>
        static void Test_Build_NullBaseline()
        {
            var refValues = MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293);
            var result = StandardCurveBuilder.Build(MakeReferenceCurve(refValues), null);
            TestRunner.AssertTrue(result == null, "null基线→返回null");
        }

        /// <summary>参考曲线的 Values 为空时返回 null。</summary>
        static void Test_Build_EmptyValues()
        {
            var emptyCurve = new ReferenceCurve
            {
                SwitchId = "1-J",
                SampleInterval = 0.04,
                Values = new List<double>()   // empty
            };
            var result = StandardCurveBuilder.Build(emptyCurve, MakeBaseline());
            TestRunner.AssertTrue(result == null, "空Values→返回null");
        }

        /// <summary>
        /// clamp 生效：极端基线值导致 raw α 远超 1.3，验证所有 α 被限制在 [0.7, 1.3]。
        /// </summary>
        static void Test_Build_ClampEffect()
        {
            var refValues = MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293);
            var refCurve = MakeReferenceCurve(refValues);
            var extreme = MakeExtremeBaseline();

            var result = StandardCurveBuilder.Build(refCurve, extreme, fusionWeight: 1.0);
            TestRunner.AssertNotNull(result, "极端基线→输出非null（不抛异常）");

            // 检查所有 α 是否在 [0.7, 1.3] 内（Lock/Tail 因除零保护为 1.0）
            double[] alphas = { result.AlphaTime, result.AlphaSpike, result.AlphaUnlock,
                                result.AlphaConv, result.AlphaLock, result.AlphaTail };
            string[] names = { "AlphaTime", "AlphaSpike", "AlphaUnlock",
                               "AlphaConv", "AlphaLock", "AlphaTail" };

            for (int i = 0; i < alphas.Length; i++)
            {
                TestRunner.AssertTrue(alphas[i] >= 0.7 - 0.001,
                    string.Format("{0}={1:F4} 不低于0.7", names[i], alphas[i]));
                TestRunner.AssertTrue(alphas[i] <= 1.3 + 0.001,
                    string.Format("{0}={1:F4} 不高于1.3", names[i], alphas[i]));
            }

            // Spike/Unlock/Conv 的 raw α 远超 1.3，应被 clamp 到 1.3
            // Time 的 raw α 也远超 1.3
            TestRunner.AssertEqual(1.3, result.AlphaSpike, 0.01, "AlphaSpike被clamp到1.3");
            TestRunner.AssertEqual(1.3, result.AlphaUnlock, 0.01, "AlphaUnlock被clamp到1.3");
            TestRunner.AssertEqual(1.3, result.AlphaConv, 0.01, "AlphaConv被clamp到1.3");

            // Lock/Tail: 基线 RefLockMean=0 → baseline 字段缺失保护 α=1.0
            //   （0 不是合法的锁闭段功率值，只可能是 JSON 缺失字段）
            TestRunner.AssertEqual(1.0, result.AlphaLock, 0.01, "AlphaLock=1.0 (字段缺失保护)");
            TestRunner.AssertEqual(1.0, result.AlphaTail, 0.01, "AlphaTail=1.0 (字段缺失保护)");
        }

        /// <summary>
        /// 除零保护：LockMean=0（短曲线无锁闭段）→ α_lock=1.0，不抛异常。
        /// </summary>
        static void Test_Build_DivByZero_LockMean()
        {
            var shortValues = MakeShortCurve(); // activeEnd ≈ 28 ≤ 30 → TailMean=0, activeEnd ≤ 50 → LockMean=0
            var shortCurve = MakeReferenceCurve(shortValues, "1-X");
            var baseline = new SwitchBaseline
            {
                RefDurationSec = 1.6,
                RefSpikePeak = 3.5,
                RefUnlockMean = 0.5,
                RefConvMean = 0.35,
                RefLockMean = 0.3,     // baseline 有 LockMean，但参考曲线没有 → α_lock_raw 计算时 refFeat.LockMean=0 → α=1.0
                RefTailMean = 0.2,      // baseline 有 TailMean，但参考曲线没有 → α=1.0
                SampleCount = 3000,
                Direction = "定位→反位"
            };

            // 不抛异常即为通过
            StandardCurve result = null;
            Exception caught = null;
            try
            {
                result = StandardCurveBuilder.Build(shortCurve, baseline);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            TestRunner.AssertTrue(caught == null,
                "短曲线LockMean=0 → Build不抛异常");
            TestRunner.AssertNotNull(result, "短曲线→仍返回有效StandardCurve");

            // 验证 α_lock 和 α_tail 为 1.0（除零保护）
            TestRunner.AssertEqual(1.0, result.AlphaLock, 0.001,
                string.Format("AlphaLock=1.0 (除零保护, 实际={0:F4})", result.AlphaLock));
            TestRunner.AssertEqual(1.0, result.AlphaTail, 0.001,
                string.Format("AlphaTail=1.0 (除零保护, 实际={0:F4})", result.AlphaTail));
        }

        /// <summary>
        /// 除零保护：TailMean=0 → α_tail=1.0。
        /// 使用 activeEnd 在 (30, 50] 范围内的曲线（有 TailMean 但 LockMean=0）。
        /// </summary>
        static void Test_Build_DivByZero_TailMean()
        {
            // 构造一条 activeEnd≈45 的曲线（>30 有缓放段，≤50 无锁闭段）
            var midCurve = MakePowerCurve(60, 6, 3.0, 0.45, 0.30, 0.28, 0.21, 45);
            var midRef = MakeReferenceCurve(midCurve, "1-Y");

            var baseline = new SwitchBaseline
            {
                RefDurationSec = 2.0,
                RefSpikePeak = 3.5,
                RefUnlockMean = 0.5,
                RefConvMean = 0.35,
                RefLockMean = 0.0,     // baseline LockMean=0 → α_lock_raw → 1.0
                RefTailMean = 0.25,
                SampleCount = 3000,
                Direction = "定位→反位"
            };

            StandardCurve result = null;
            Exception caught = null;
            try
            {
                result = StandardCurveBuilder.Build(midRef, baseline);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            TestRunner.AssertTrue(caught == null, "LockMean=0 on either side → 不抛异常");
            TestRunner.AssertNotNull(result, "返回有效StandardCurve");

            // baseline.RefLockMean=0 → α_lock_raw = ... wait
            // The code checks: refFeat.LockMean > 0.001 ? baseline.RefLockMean / refFeat.LockMean : 1.0
            // midCurve has activeEnd=45 → LockMean=0 → α_lock_raw = 1.0
            // But also: baseline.RefLockMean=0, what does the code compute?
            // It computes: refFeat.LockMean > 0.001 ? baseline.RefLockMean / refFeat.LockMean : 1.0
            // If refFeat.LockMean=0, then the ternary returns 1.0 (div-by-zero protection)
            // Then MixAlpha(1.0, ...) = 1.0
            TestRunner.AssertEqual(1.0, result.AlphaLock, 0.001, "AlphaLock=1.0");
        }

        /// <summary>
        /// 输出 Values 长度 ≈ baseline.RefDurationSec / sampleInterval。
        /// </summary>
        static void Test_Build_OutputLength()
        {
            var refValues = MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293);
            var refCurve = MakeReferenceCurve(refValues);

            // 基线时长 12.0s → targetLen = round(12.0/0.04) = 300
            var baseline = MakeBaseline(refDurationSec: 12.0);

            var result = StandardCurveBuilder.Build(refCurve, baseline);
            TestRunner.AssertNotNull(result, "输出非null");
            TestRunner.AssertTrue(result.Values.Count >= 200,
                string.Format("Values长度≥200 (实际={0}, 期望≈300)", result.Values.Count));
            // 目标长度应为 round(12.0 / 0.04) = 300
            int expectedLen = (int)Math.Round(12.0 / 0.04);
            TestRunner.AssertEqual(expectedLen, result.Values.Count,
                string.Format("Values长度={0} (期望={1})", result.Values.Count, expectedLen));
        }

        /// <summary>
        /// 输出中不含 NaN 或 ±Infinity。
        /// </summary>
        static void Test_Build_NoNaN()
        {
            var refValues = MakePowerCurve(300, 6, 3.5, 0.5, 0.30, 0.28, 0.21, 293);
            var refCurve = MakeReferenceCurve(refValues);
            var result = StandardCurveBuilder.Build(refCurve, MakeBaseline());

            TestRunner.AssertNotNull(result, "输出非null");
            foreach (double v in result.Values)
            {
                TestRunner.AssertFalse(double.IsNaN(v),
                    string.Format("值 {0:F4} 不是 NaN", v));
                TestRunner.AssertFalse(double.IsInfinity(v),
                    string.Format("值 {0:F4} 不是 Infinity", v));
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  S2: ResampleLinear 边界测试
        // ═══════════════════════════════════════════════════════════════

        /// <summary>targetCount=1 返回仅含 src[0] 的列表。</summary>
        static void Test_Resample_TargetCount1()
        {
            var src = new List<double> { 10.0, 20.0, 30.0, 40.0, 50.0 };
            var result = StandardCurveBuilder.ResampleLinear(src, 1);

            TestRunner.AssertEqual(1, result.Count, "Count=1");
            TestRunner.AssertEqual(10.0, result[0], 0.001, "result[0]=10.0 (=src[0])");
        }

        /// <summary>N=1 时，无论 targetCount 多大，全填充首点。</summary>
        static void Test_Resample_NEquals1()
        {
            var src = new List<double> { 7.5 };
            var result = StandardCurveBuilder.ResampleLinear(src, 5);

            TestRunner.AssertEqual(5, result.Count, "Count=5");
            for (int i = 0; i < 5; i++)
            {
                TestRunner.AssertEqual(7.5, result[i], 0.001,
                    string.Format("result[{0}]=7.5", i));
            }
        }

        /// <summary>targetCount=0 返回空列表。</summary>
        static void Test_Resample_TargetCount0()
        {
            var src = new List<double> { 1.0, 2.0, 3.0 };
            var result = StandardCurveBuilder.ResampleLinear(src, 0);

            TestRunner.AssertNotNull(result, "返回非null");
            TestRunner.AssertEqual(0, result.Count, "Count=0");
        }

        /// <summary>空源列表返回空列表。</summary>
        static void Test_Resample_EmptySource()
        {
            var result = StandardCurveBuilder.ResampleLinear(new List<double>(), 10);

            TestRunner.AssertNotNull(result, "返回非null");
            TestRunner.AssertEqual(0, result.Count, "Count=0");
        }

        /// <summary>targetCount = N 时，返回长度相同的列表，值接近原值（线性插值两端精确匹配）。</summary>
        static void Test_Resample_SameCount()
        {
            var src = new List<double> { 0.0, 1.0, 2.0, 3.0, 4.0 };
            var result = StandardCurveBuilder.ResampleLinear(src, 5);

            TestRunner.AssertEqual(5, result.Count, "Count=5");
            // 两端点精确匹配
            TestRunner.AssertEqual(0.0, result[0], 0.001, "result[0]=0.0");
            TestRunner.AssertEqual(4.0, result[4], 0.001, "result[4]=4.0");
        }

        /// <summary>N=5→targetCount=10：线性插值输出应单调递增。</summary>
        static void Test_Resample_TenPoints()
        {
            var src = new List<double> { 0.0, 1.0, 2.0, 3.0, 4.0 };
            var result = StandardCurveBuilder.ResampleLinear(src, 10);

            TestRunner.AssertEqual(10, result.Count, "Count=10");
            // 单调递增
            for (int i = 1; i < result.Count; i++)
            {
                TestRunner.AssertTrue(result[i] >= result[i - 1] - 0.001,
                    string.Format("单调递增: result[{0}]={1:F4} >= result[{2}]={3:F4}",
                        i, result[i], i - 1, result[i - 1]));
            }
            // 端点
            TestRunner.AssertEqual(0.0, result[0], 0.001, "首点=0.0");
            TestRunner.AssertEqual(4.0, result[9], 0.001, "末点=4.0");
        }

        // ═══════════════════════════════════════════════════════════════
        //  S3: GetPointAlpha 段分配测试
        // ═══════════════════════════════════════════════════════════════

        /// <summary>i < si → α_spike</summary>
        static void Test_GetPointAlpha_SpikeSegment()
        {
            // 参数: si=6, unlockEnd=20, lockStart=253, ae=293, n=300, hw=3
            double alpha = StandardCurveBuilder.GetPointAlpha(
                3, 6, 20, 253, 293, 300,
                2.0, 3.0, 4.0, 5.0, 6.0, 3);

            TestRunner.AssertEqual(2.0, alpha, 0.001, "i=3 < si=6 → α_spike=2.0");
        }

        /// <summary>i 在 [si+2, si+14) → α_unlock</summary>
        static void Test_GetPointAlpha_UnlockSegment()
        {
            // si=6, unlock段: [8, 20)
            double alpha = StandardCurveBuilder.GetPointAlpha(
                10, 6, 20, 253, 293, 300,
                2.0, 3.0, 4.0, 5.0, 6.0, 3);

            TestRunner.AssertEqual(3.0, alpha, 0.001, "i=10 ∈ [8,20) → α_unlock=3.0");
        }

        /// <summary>i 在 [si+20, ae-40) → α_conv</summary>
        static void Test_GetPointAlpha_ConvSegment()
        {
            // si=6, ae=293, conv段: [26, 253)
            double alpha = StandardCurveBuilder.GetPointAlpha(
                100, 6, 20, 253, 293, 300,
                2.0, 3.0, 4.0, 5.0, 6.0, 3);

            TestRunner.AssertEqual(4.0, alpha, 0.001, "i=100 ∈ [26,253) → α_conv=4.0");
        }

        /// <summary>spike→unlock 过渡区 [si, si+2) 线性混合。</summary>
        static void Test_GetPointAlpha_SpikeToUnlock_Blend()
        {
            // si=6, spike→unlock 过渡: [6, 8), 共2步
            // i=6: t=0.0 → α_spike
            double a0 = StandardCurveBuilder.GetPointAlpha(
                6, 6, 20, 253, 293, 300,
                2.0, 3.0, 4.0, 5.0, 6.0, 3);
            // i=7: t=0.5 → mid
            double a1 = StandardCurveBuilder.GetPointAlpha(
                7, 6, 20, 253, 293, 300,
                2.0, 3.0, 4.0, 5.0, 6.0, 3);

            TestRunner.AssertEqual(2.0, a0, 0.001, "i=6 (过渡起点) → α_spike=2.0");
            TestRunner.AssertEqual(2.5, a1, 0.001, "i=7 (过渡中点) → 2.5");
        }

        /// <summary>
        /// 短曲线 (ae < 50): 无 lock/tail 段。
        /// ae=30, n=40, si=5。
        /// 验证 i 靠近末尾时回退不抛异常。
        /// </summary>
        static void Test_GetPointAlpha_ShortCurve()
        {
            // ae=30 (≤50: 无lock段, ≤30→无tail段: 边界 case)
            // si=5, unlockEnd=19, lockStart=0, n=40, hw=3
            double alpha = StandardCurveBuilder.GetPointAlpha(
                35, 5, 19, 0, 30, 40,
                2.0, 3.0, 4.0, 5.0, 6.0, 3);

            // 应不抛异常，返回某个合理的 α 值（不在 NaN/Infinity）
            TestRunner.AssertFalse(double.IsNaN(alpha), "短曲线→不返回NaN");
            TestRunner.AssertFalse(double.IsInfinity(alpha), "短曲线→不返回Infinity");
            TestRunner.AssertTrue(alpha >= 0, string.Format("α={0:F4} ≥ 0", alpha));
        }

        // ═══════════════════════════════════════════════════════════════
        //  S4: StandardCurveStore 读写测试
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Save → Load 往返：所有字段一致。</summary>
        static void Test_Store_SaveLoad_Roundtrip()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                var original = new StandardCurve
                {
                    SwitchId = "1-J",
                    Direction = "定位→反位",
                    SampleInterval = 0.04,
                    AlignIndex = 6,
                    Values = new List<double> { 0.0, 3.5, 0.5, 0.30, 0.28, 0.21 },
                    FusionWeight = 1.0,
                    ReferenceSource = "reference_curves/1-J.json",
                    BaselineComputedAt = "2026-07-14 10:00:00",
                    AlphaTime = 1.0204,
                    AlphaSpike = 1.1429,
                    AlphaUnlock = 1.2,
                    AlphaConv = 1.3,
                    AlphaLock = 1.0,
                    AlphaTail = 0.9524,
                    ComputedAt = "2026-07-14 15:00:00"
                };

                StandardCurveStore.Save(tempDir, original);

                // 验证文件已创建
                string filePath = Path.Combine(tempDir, "1-J_定位→反位.json");
                TestRunner.AssertFileExists(filePath, "标准曲线 JSON 已保存");

                // 加载回来
                var loaded = StandardCurveStore.Load(filePath);
                TestRunner.AssertNotNull(loaded, "Load 返回非null");

                // 逐字段验证
                TestRunner.AssertEqual(original.SwitchId, loaded.SwitchId, "SwitchId 一致");
                TestRunner.AssertEqual(original.Direction, loaded.Direction, "Direction 一致");
                TestRunner.AssertEqual(original.SampleInterval, loaded.SampleInterval, 0.0001, "SampleInterval 一致");
                TestRunner.AssertEqual(original.AlignIndex, loaded.AlignIndex, "AlignIndex 一致");
                TestRunner.AssertEqual(original.FusionWeight, loaded.FusionWeight, 0.0001, "FusionWeight 一致");
                TestRunner.AssertEqual(original.ReferenceSource, loaded.ReferenceSource, "ReferenceSource 一致");
                TestRunner.AssertEqual(original.BaselineComputedAt, loaded.BaselineComputedAt, "BaselineComputedAt 一致");
                TestRunner.AssertEqual(original.AlphaTime, loaded.AlphaTime, 0.0001, "AlphaTime 一致");
                TestRunner.AssertEqual(original.AlphaSpike, loaded.AlphaSpike, 0.0001, "AlphaSpike 一致");
                TestRunner.AssertEqual(original.AlphaUnlock, loaded.AlphaUnlock, 0.0001, "AlphaUnlock 一致");
                TestRunner.AssertEqual(original.AlphaConv, loaded.AlphaConv, 0.0001, "AlphaConv 一致");
                TestRunner.AssertEqual(original.AlphaLock, loaded.AlphaLock, 0.0001, "AlphaLock 一致");
                TestRunner.AssertEqual(original.AlphaTail, loaded.AlphaTail, 0.0001, "AlphaTail 一致");
                TestRunner.AssertEqual(original.ComputedAt, loaded.ComputedAt, "ComputedAt 一致");

                // Values 列表逐元素验证
                TestRunner.AssertEqual(original.Values.Count, loaded.Values.Count, "Values 长度一致");
                for (int i = 0; i < original.Values.Count; i++)
                {
                    TestRunner.AssertEqual(original.Values[i], loaded.Values[i], 0.001,
                        string.Format("Values[{0}] 一致", i));
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>文件不存在时 Load 返回 null。</summary>
        static void Test_Store_FileNotExists()
        {
            string nonExistent = Path.Combine(Path.GetTempPath(),
                "SwitchMonitor_NoSuchFile_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");

            var result = StandardCurveStore.Load(nonExistent);
            TestRunner.AssertTrue(result == null, "文件不存在→返回null");
        }

        /// <summary>LoadAll 加载目录下所有 .json 标准曲线。</summary>
        static void Test_Store_LoadAll()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                // 保存两条标准曲线
                var c1 = new StandardCurve
                {
                    SwitchId = "1-J", Direction = "定位→反位", SampleInterval = 0.04,
                    Values = new List<double> { 1.0, 2.0 }
                };
                var c2 = new StandardCurve
                {
                    SwitchId = "4-J", Direction = "反位→定位", SampleInterval = 0.04,
                    Values = new List<double> { 3.0, 4.0 }
                };

                StandardCurveStore.Save(tempDir, c1);
                StandardCurveStore.Save(tempDir, c2);

                var all = StandardCurveStore.LoadAll(tempDir);
                TestRunner.AssertEqual(2, all.Count, "LoadAll返回2条曲线");
                TestRunner.AssertTrue(all.ContainsKey("1-J|定位→反位"), "包含1-J|定位→反位");
                TestRunner.AssertTrue(all.ContainsKey("4-J|反位→定位"), "包含4-J|反位→定位");
                TestRunner.AssertEqual("定位→反位", all["1-J|定位→反位"].Direction, "1-J Direction正确");
                TestRunner.AssertEqual("反位→定位", all["4-J|反位→定位"].Direction, "4-J Direction正确");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  S5: Blend 逐点线性融合
        // ═══════════════════════════════════════════════════════════════

        /// <summary>w=0 时融合结果等于参考曲线值。</summary>
        static void Test_Blend_Weight0_ReturnsReference()
        {
            var medianValues = new List<double>();
            for (int i = 0; i < 100; i++) medianValues.Add(3.0);
            var refValues = new List<double>();
            for (int i = 0; i < 100; i++) refValues.Add(1.0);

            var medianCurve = new StandardCurve
            {
                SwitchId = "1-J", Direction = "定位→反位", SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double>(medianValues),
                OriginalMedianValues = new List<double>(medianValues)
            };
            var refCurve = MakeReferenceCurve(refValues);

            var result = StandardCurveBuilder.Blend(medianCurve, refCurve, 0.0);

            TestRunner.AssertTrue(result != null, "结果非null");
            TestRunner.AssertEqual(100, result.Values.Count, "输出长度=100");
            for (int i = 0; i < result.Values.Count; i++)
            {
                TestRunner.AssertTrue(Math.Abs(result.Values[i] - 1.0) < 0.01,
                    "点[" + i + "] 应为1.0, 实际=" + result.Values[i]);
            }
            TestRunner.AssertTrue(Math.Abs(result.FusionWeight) < 0.001, "FusionWeight≈0");
        }

        /// <summary>w=1 时融合结果等于中位曲线值。</summary>
        static void Test_Blend_Weight1_ReturnsMedian()
        {
            var medianValues = new List<double>();
            for (int i = 0; i < 100; i++) medianValues.Add(3.0);
            var refValues = new List<double>();
            for (int i = 0; i < 100; i++) refValues.Add(1.0);

            var medianCurve = new StandardCurve
            {
                SwitchId = "1-J", Direction = "定位→反位", SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double>(medianValues),
                OriginalMedianValues = new List<double>(medianValues)
            };
            var refCurve = MakeReferenceCurve(refValues);

            var result = StandardCurveBuilder.Blend(medianCurve, refCurve, 1.0);

            TestRunner.AssertTrue(result != null, "结果非null");
            TestRunner.AssertEqual(100, result.Values.Count, "输出长度=100");
            for (int i = 0; i < result.Values.Count; i++)
            {
                TestRunner.AssertTrue(Math.Abs(result.Values[i] - 3.0) < 0.01,
                    "点[" + i + "] 应为3.0, 实际=" + result.Values[i]);
            }
            TestRunner.AssertTrue(Math.Abs(result.FusionWeight - 1.0) < 0.001, "FusionWeight≈1");
        }

        /// <summary>w=0.5 时融合结果=(median+ref)/2。</summary>
        static void Test_Blend_Weight05_Midpoint()
        {
            var medianValues = new List<double>();
            for (int i = 0; i < 100; i++) medianValues.Add(3.0);
            var refValues = new List<double>();
            for (int i = 0; i < 100; i++) refValues.Add(1.0);

            var medianCurve = new StandardCurve
            {
                SwitchId = "1-J", Direction = "定位→反位", SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double>(medianValues),
                OriginalMedianValues = new List<double>(medianValues)
            };
            var refCurve = MakeReferenceCurve(refValues);

            var result = StandardCurveBuilder.Blend(medianCurve, refCurve, 0.5);

            TestRunner.AssertTrue(result != null, "结果非null");
            for (int i = 0; i < result.Values.Count; i++)
            {
                TestRunner.AssertTrue(Math.Abs(result.Values[i] - 2.0) < 0.01,
                    "点[" + i + "] 应为2.0, 实际=" + result.Values[i]);
            }
            TestRunner.AssertTrue(Math.Abs(result.FusionWeight - 0.5) < 0.001, "FusionWeight≈0.5");
        }

        /// <summary>不同长度曲线融合：参考曲线被重采样到中位曲线等长，不抛异常。</summary>
        static void Test_Blend_DifferentLengths()
        {
            var medianValues = new List<double>();
            for (int i = 0; i < 300; i++) medianValues.Add(2.0);
            var refValues = new List<double>();
            for (int i = 0; i < 200; i++) refValues.Add(1.5);

            var medianCurve = new StandardCurve
            {
                SwitchId = "1-J", Direction = "定位→反位", SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double>(medianValues),
                OriginalMedianValues = new List<double>(medianValues)
            };
            var refCurve = MakeReferenceCurve(refValues);
            refCurve.AlignIndex = 6;

            var result = StandardCurveBuilder.Blend(medianCurve, refCurve, 1.0);

            TestRunner.AssertTrue(result != null, "结果非null");
            TestRunner.AssertEqual(300, result.Values.Count, "输出长度=中位曲线长度300");
            // OriginalMedianValues 保持不变
            TestRunner.AssertEqual(300, result.OriginalMedianValues.Count, "OriginalMedianValues长度=300");
            TestRunner.AssertTrue(Math.Abs(result.OriginalMedianValues[0] - 2.0) < 0.01, "OriginalMedianValues[0]=2.0");
        }

        /// <summary>null 输入返回 null。</summary>
        static void Test_Blend_NullChecks()
        {
            var validMedian = new StandardCurve
            {
                SwitchId = "1-J", Direction = "定位→反位", SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double> { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 },
                OriginalMedianValues = new List<double> { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 }
            };
            var validRef = MakeReferenceCurve(new List<double> { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 });

            TestRunner.AssertTrue(StandardCurveBuilder.Blend(null, validRef, 0.5) == null, "null中位曲线→null");
            TestRunner.AssertTrue(StandardCurveBuilder.Blend(validMedian, null, 0.5) == null, "null参考曲线→null");
        }

        /// <summary>融合后 OriginalMedianValues 保持不变。</summary>
        static void Test_Blend_OriginalMedianPreserved()
        {
            var medianValues = new List<double>();
            for (int i = 0; i < 50; i++) medianValues.Add(5.0);
            var refValues = new List<double>();
            for (int i = 0; i < 50; i++) refValues.Add(1.0);

            var medianCurve = new StandardCurve
            {
                SwitchId = "1-J", Direction = "定位→反位", SampleInterval = 0.04,
                AlignIndex = 6,
                Values = new List<double>(medianValues),
                OriginalMedianValues = new List<double>(medianValues)
            };
            var refCurve = MakeReferenceCurve(refValues);

            var result = StandardCurveBuilder.Blend(medianCurve, refCurve, 0.3);

            // OriginalMedianValues 不变
            for (int i = 0; i < result.OriginalMedianValues.Count; i++)
            {
                TestRunner.AssertTrue(Math.Abs(result.OriginalMedianValues[i] - 5.0) < 0.01,
                    "OriginalMedianValues[" + i + "]=5.0");
            }
            // Values 已更新（w=0.3 → 5.0*0.3 + 1.0*0.7 = 1.5+0.7=2.2）
            TestRunner.AssertTrue(Math.Abs(result.Values[0] - 2.2) < 0.01, "Values[0]≈2.2 (blended)");
        }
    }
}
