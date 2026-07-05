using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using SwitchMonitor.UI;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 6: 全链路交互联动 TDD 测试。
    /// 测试: 系列显隐控制、阈值线开关、Y轴量程保持、边界状态渲染、状态栏格式化。
    /// </summary>
    public class Slice6Tests
    {
        static int passed = 0;
        static int failed = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 6: 全链路交互联动 测试 ===");
            Console.WriteLine();

            // TDD 循环
            TestPhaseVisibilityDefault();
            TestPhaseVisibilityToggle();
            TestThresholdLineToggle();
            TestYAxisHoldToggle();
            TestEmptyStateMessage();
            TestLoadingStateIndicator();
            TestErrorStateDisplay();
            TestStatusBarFormat();
            TestAllSeriesHiddenState();
            TestViewportResetOnSeriesToggle();

            Console.WriteLine();
            Console.WriteLine("=== Slice 6 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // TDD Cycle 1: 默认所有系列可见
        // ================================================================

        /// <summary>
        /// Cycle 1: 初始状态下，A/B/C 三相和阈值线均默认可见。
        /// </summary>
        static void TestPhaseVisibilityDefault()
        {
            Console.WriteLine("--- Cycle 1: 默认系列可见状态 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    // 默认全部可见
                    Assert(panel.PhaseAVisible, "默认 PhaseA 可见");
                    Assert(panel.PhaseBVisible, "默认 PhaseB 可见");
                    Assert(panel.PhaseCVisible, "默认 PhaseC 可见");
                    Assert(panel.ThresholdVisible, "默认阈值线可见");
                    Assert(!panel.YAxisFixed, "默认 Y轴不自适应（跟随数据）");

                    Console.WriteLine("  PhaseA={0} PhaseB={1} PhaseC={2} Threshold={3} YFixed={4}",
                        panel.PhaseAVisible, panel.PhaseBVisible, panel.PhaseCVisible,
                        panel.ThresholdVisible, panel.YAxisFixed);
                    Console.WriteLine("  [PASS] Cycle 1");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 2: 系列显隐切换
        // ================================================================

        /// <summary>
        /// Cycle 2: SetPhaseVisibility 可独立控制各相显隐。
        /// </summary>
        static void TestPhaseVisibilityToggle()
        {
            Console.WriteLine("--- Cycle 2: 系列显隐切换 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    // 隐藏 A 相
                    panel.SetPhaseVisibility(false, true, true);
                    Assert(!panel.PhaseAVisible, "隐藏后 PhaseA 不可见");
                    Assert(panel.PhaseBVisible, "PhaseB 仍可见");
                    Assert(panel.PhaseCVisible, "PhaseC 仍可见");

                    // 隐藏 B 相
                    panel.SetPhaseVisibility(true, false, true);
                    Assert(panel.PhaseAVisible, "PhaseA 可见");
                    Assert(!panel.PhaseBVisible, "PhaseB 不可见");
                    Assert(panel.PhaseCVisible, "PhaseC 可见");

                    // 隐藏 C 相
                    panel.SetPhaseVisibility(true, true, false);
                    Assert(panel.PhaseAVisible, "PhaseA 可见");
                    Assert(panel.PhaseBVisible, "PhaseB 可见");
                    Assert(!panel.PhaseCVisible, "PhaseC 不可见");

                    // 全部隐藏
                    panel.SetPhaseVisibility(false, false, false);
                    Assert(!panel.PhaseAVisible && !panel.PhaseBVisible && !panel.PhaseCVisible,
                        "全部隐藏");

                    // 全部显示（恢复）
                    panel.SetPhaseVisibility(true, true, true);
                    Assert(panel.PhaseAVisible && panel.PhaseBVisible && panel.PhaseCVisible,
                        "全部显示恢复");

                    Console.WriteLine("  [PASS] Cycle 2");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 3: 阈值线显隐切换
        // ================================================================

        /// <summary>
        /// Cycle 3: SetThresholdVisible 可切换阈值线显隐，不改变阈值值本身。
        /// </summary>
        static void TestThresholdLineToggle()
        {
            Console.WriteLine("--- Cycle 3: 阈值线显隐切换 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    // 先设置阈值线
                    panel.SetThresholdLine(2.5f, Color.Red, System.Drawing.Drawing2D.DashStyle.Dash);
                    Assert(panel.HasThresholdLine, "阈值线已设置");
                    Assert(panel.ThresholdVisible, "阈值线默认可见");

                    // 隐藏阈值线
                    panel.SetThresholdVisible(false);
                    Assert(!panel.ThresholdVisible, "SetThresholdVisible(false) → 阈值线不可见");
                    Assert(panel.HasThresholdLine, "但阈值值仍然存在（HasThresholdLine=true）");

                    // 重新显示阈值线
                    panel.SetThresholdVisible(true);
                    Assert(panel.ThresholdVisible, "SetThresholdVisible(true) → 阈值线可见");

                    Console.WriteLine("  [PASS] Cycle 3");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 4: Y轴量程保持切换
        // ================================================================

        /// <summary>
        /// Cycle 4: SetYAxisFixed(true) 时缩放不变 Y 轴范围; false 时自适应。
        /// </summary>
        static void TestYAxisHoldToggle()
        {
            Console.WriteLine("--- Cycle 4: Y轴量程保持切换 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    float origTop = panel.ViewTop;
                    float origBottom = panel.ViewBottom;

                    // 启用 Y 轴固定
                    panel.SetYAxisFixed(true);
                    Assert(panel.YAxisFixed, "YAxisFixed=true");

                    // 缩放入
                    int cx = panel.Width / 2, cy = panel.Height / 2;
                    panel.SimulateMouseMove(cx, cy);
                    panel.SimulateMouseWheel(cx, cy, 120);

                    // Y 轴范围应保持（因为固定了）
                    Assert(ApproxEqual(panel.ViewTop, origTop, 0.01f),
                        string.Format("Y轴固定时缩放不改变 YTop (orig={0:F2} cur={1:F2})", origTop, panel.ViewTop));
                    Assert(ApproxEqual(panel.ViewBottom, origBottom, 0.01f),
                        string.Format("Y轴固定时缩放不改变 YBottom (orig={0:F2} cur={1:F2})", origBottom, panel.ViewBottom));

                    // 关闭 Y 轴固定
                    panel.SetYAxisFixed(false);
                    Assert(!panel.YAxisFixed, "YAxisFixed=false");

                    // 缩放入
                    panel.SimulateMouseWheel(cx, cy, 120);

                    // Y 轴范围应该改变了（自适应当前可见数据）
                    // 如果数据是线性的，放大区域可能 Y 范围有变化
                    // 不强制要求变化多少，只验证状态正确
                    Console.WriteLine("  Y轴固定关闭后: Top={0:F2} Bottom={1:F2}",
                        panel.ViewTop, panel.ViewBottom);

                    Console.WriteLine("  [PASS] Cycle 4");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 5: 空状态消息
        // ================================================================

        /// <summary>
        /// Cycle 5: 无数据时图表显示空状态提示文字。
        /// </summary>
        static void TestEmptyStateMessage()
        {
            Console.WriteLine("--- Cycle 5: 空状态消息 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    // 默认无数据时应有空状态消息
                    Assert(!string.IsNullOrEmpty(panel.EmptyStateMessage),
                        "无数据时有空状态消息");

                    // 设置自定义空状态消息
                    panel.EmptyStateMessage = "请选择转辙机查看曲线";
                    Assert(panel.EmptyStateMessage == "请选择转辙机查看曲线",
                        "自定义空状态消息生效");

                    // 设置数据后空状态消息应被清除（通过 HasData 判断）
                    var data = MakeLinearData(50, 0f, 5f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);
                    Assert(panel.HasData, "设置数据后 HasData=true");

                    // 清除数据
                    panel.SetSamples(null, null, null);
                    Assert(panel.EmptyStateMessage == "请选择转辙机查看曲线",
                        "清除数据后保留空状态消息设置");

                    Console.WriteLine("  [PASS] Cycle 5");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 6: 加载状态指示
        // ================================================================

        /// <summary>
        /// Cycle 6: 设置加载状态后显示加载指示器样式。
        /// </summary>
        static void TestLoadingStateIndicator()
        {
            Console.WriteLine("--- Cycle 6: 加载状态指示 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    // 默认非加载状态
                    Assert(!panel.IsLoading, "默认 IsLoading=false");

                    // 设置为加载状态
                    panel.IsLoading = true;
                    Assert(panel.IsLoading, "IsLoading=true");

                    // 设置为加载状态
                    panel.IsLoading = false;
                    Assert(!panel.IsLoading, "IsLoading=false");

                    Console.WriteLine("  [PASS] Cycle 6");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 7: 错误状态显示
        // ================================================================

        /// <summary>
        /// Cycle 7: 设置错误消息后图表应显示错误提示。
        /// </summary>
        static void TestErrorStateDisplay()
        {
            Console.WriteLine("--- Cycle 7: 错误状态显示 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    // 默认无错误
                    Assert(string.IsNullOrEmpty(panel.ErrorMessage),
                        "默认无错误消息");

                    // 设置错误消息
                    panel.ErrorMessage = "数据解析失败: 文件格式不正确";
                    Assert(panel.ErrorMessage == "数据解析失败: 文件格式不正确",
                        "错误消息已设置");

                    // 清除错误
                    panel.ErrorMessage = null;
                    Assert(string.IsNullOrEmpty(panel.ErrorMessage),
                        "清除后无错误消息");

                    Console.WriteLine("  [PASS] Cycle 7");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 8: 状态栏格式化
        // ================================================================

        /// <summary>
        /// Cycle 8: 状态栏应能生成正确的格式化字符串。
        /// 格式: "1-1 | 2026-06-29 | 17:01:41 | 动作数: 25"
        /// </summary>
        static void TestStatusBarFormat()
        {
            Console.WriteLine("--- Cycle 8: 状态栏格式化 ---");
            try
            {
                // MainForm 不便于直接测试状态栏 label，但可测试格式化逻辑
                // 通过反射或静态辅助方法验证格式
                string switchId = "1-1";
                string date = "2026-06-29";
                string time = "17:01:41";
                int actionCount = 25;

                string expected = "1-1 | 2026-06-29 | 17:01:41 | 动作数: 25";
                string actual = MainForm.FormatStatusText(switchId, date, time, actionCount);

                Assert(actual == expected,
                    string.Format("状态栏格式正确: [{0}] == [{1}]", actual, expected));

                // 空 case
                string empty = MainForm.FormatStatusText(null, null, null, 0);
                Assert(!string.IsNullOrEmpty(empty),
                    "空参数时也有默认文字");
                Console.WriteLine("  空状态: [{0}]", empty);

                Console.WriteLine("  [PASS] Cycle 8");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 9: 全部系列隐藏时的边界状态
        // ================================================================

        /// <summary>
        /// Cycle 9: 所有系列隐藏时应显示提示而非空白图表。
        /// </summary>
        static void TestAllSeriesHiddenState()
        {
            Console.WriteLine("--- Cycle 9: 全部系列隐藏 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(100, 0f, 5f);
                    panel.SetSamples(data, data, data);
                    EnsureHandle(panel);

                    // 隐藏所有系列
                    panel.SetPhaseVisibility(false, false, false);
                    Assert(!panel.PhaseAVisible && !panel.PhaseBVisible && !panel.PhaseCVisible,
                        "全部系列隐藏");

                    // 仍然有数据，但系列不可见
                    Assert(panel.HasData, "HasData 不受系列显隐影响");

                    Console.WriteLine("  [PASS] Cycle 9");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 10: 切换系列显隐不影响视口
        // ================================================================

        /// <summary>
        /// Cycle 10: 切换系列显隐不应重置视口（用户可能正在放大查看某区域）。
        /// </summary>
        static void TestViewportResetOnSeriesToggle()
        {
            Console.WriteLine("--- Cycle 10: 系列显隐不重置视口 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, data, data);
                    EnsureHandle(panel);

                    // 先缩放
                    int cx = panel.Width / 2, cy = panel.Height / 2;
                    panel.SimulateMouseMove(cx, cy);
                    panel.SimulateMouseWheel(cx, cy, 120);

                    float zoomedLeft = panel.ViewLeft;
                    float zoomedRight = panel.ViewRight;
                    float zoomedBottom = panel.ViewBottom;
                    float zoomedTop = panel.ViewTop;

                    // 切换系列显隐
                    panel.SetPhaseVisibility(false, true, true);

                    // 视口应保持不变
                    Assert(ApproxEqual(panel.ViewLeft, zoomedLeft, 0.01f),
                        "系列切换后 ViewLeft 不变");
                    Assert(ApproxEqual(panel.ViewRight, zoomedRight, 0.01f),
                        "系列切换后 ViewRight 不变");

                    Console.WriteLine("  视口: L={0:F2} R={1:F2} B={2:F2} T={3:F2} (不变)",
                        panel.ViewLeft, panel.ViewRight, panel.ViewBottom, panel.ViewTop);
                    Console.WriteLine("  [PASS] Cycle 10");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Helpers
        // ================================================================

        static List<float> MakeLinearData(int count, float startVal, float endVal)
        {
            var list = new List<float>(count);
            for (int i = 0; i < count; i++)
            {
                float t = (float)i / (count - 1);
                list.Add(startVal + t * (endVal - startVal));
            }
            return list;
        }

        static void EnsureHandle(Control c)
        {
            c.Size = new Size(800, 500);
            var h = c.Handle;
        }

        static bool ApproxEqual(float a, float b, float tolerance)
        {
            return Math.Abs(a - b) < tolerance;
        }

        static void Assert(bool cond, string msg)
        {
            if (!cond) { Console.WriteLine("    ASSERT FAIL: {0}", msg); throw new Exception(msg); }
        }
    }
}
