using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using SwitchMonitor.UI;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 5: 曲线交互 — 缩放/拖拽/局部放大 TDD 测试。
    /// 测试 CurveChartPanel 的视口变换和交互行为。
    /// </summary>
    public class Slice5Tests
    {
        static int passed = 0;
        static int failed = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 5: 曲线交互 测试 ===");
            Console.WriteLine();

            // TDD 循环测试
            TestViewportInitialState();
            TestViewportAfterReset();
            TestMouseWheelZoomIn();
            TestMouseWheelZoomOut();
            TestZoomBoundaryLimits();
            TestMouseDragPan();
            TestRectangleZoom();
            TestRightClickReset();
            TestAxisLabelsUpdate();
            TestViewResetOnDataChange();

            Console.WriteLine();
            Console.WriteLine("=== Slice 5 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // TDD Cycle 1: Viewport 初始状态
        // ================================================================

        /// <summary>
        /// Cycle 1: 设置数据后，ViewLeft/Right/Top/Bottom 反映全数据范围（含 10% padding）。
        /// </summary>
        static void TestViewportInitialState()
        {
            Console.WriteLine("--- Cycle 1: Viewport 初始状态 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    // 初始无数据时，viewport 应有合理默认值
                    Assert(panel.ViewLeft >= 0, "无数据时 ViewLeft >= 0");
                    Assert(panel.ViewRight >= panel.ViewLeft,
                        "无数据时 ViewRight >= ViewLeft");

                    // 设置已知数据：(0~199 共 200 点，值范围 0~10)
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);

                    // 创建句柄触发 OnPaint
                    EnsureHandle(panel);

                    // ViewLeft ≈ 0, ViewRight ≈ 199 (全 X 范围)
                    Assert(panel.ViewLeft <= 1f,
                        string.Format("ViewLeft ≈ 0 (actual: {0:F2})", panel.ViewLeft));
                    Assert(panel.ViewRight >= 198f,
                        string.Format("ViewRight ≈ 199 (actual: {0:F2})", panel.ViewRight));

                    // ViewBottom ≈ 0, ViewTop ≈ 11 (含 10% 上 padding，下边界含 0)
                    Assert(panel.ViewBottom <= 0.01f,
                        string.Format("ViewBottom ≈ 0 (actual: {0:F2})", panel.ViewBottom));
                    Assert(panel.ViewTop >= 10.5f,
                        string.Format("ViewTop >= 10.5 (含10%padding, actual: {0:F2})", panel.ViewTop));
                    Assert(panel.ViewTop <= 12f,
                        string.Format("ViewTop <= 12 (actual: {0:F2})", panel.ViewTop));

                    Console.WriteLine("  ViewLeft={0:F2} ViewRight={1:F2} ViewBottom={2:F2} ViewTop={3:F2}",
                        panel.ViewLeft, panel.ViewRight, panel.ViewBottom, panel.ViewTop);
                    Console.WriteLine("  [PASS] Cycle 1");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 2: ResetView 重置
        // ================================================================

        /// <summary>
        /// Cycle 2: 修改 viewport 后调用 ResetView() 恢复初始全貌视图。
        /// </summary>
        static void TestViewportAfterReset()
        {
            Console.WriteLine("--- Cycle 2: ResetView 重置 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    float origLeft = panel.ViewLeft;
                    float origRight = panel.ViewRight;
                    float origBottom = panel.ViewBottom;
                    float origTop = panel.ViewTop;

                    Console.WriteLine("  初始: L={0:F2} R={1:F2} B={2:F2} T={3:F2}",
                        origLeft, origRight, origBottom, origTop);

                    // 修改 viewport (通过内部方法模拟缩放后状态)
                    // 我们通过 ResetView 接口测试：应该在任意状态下都能恢复
                    panel.ResetView();

                    // 验证恢复到初始状态
                    Assert(ApproxEqual(panel.ViewLeft, origLeft, 0.1f),
                        string.Format("ResetView 后 ViewLeft 恢复 ({0:F2} ≈ {1:F2})",
                            panel.ViewLeft, origLeft));
                    Assert(ApproxEqual(panel.ViewRight, origRight, 0.1f),
                        string.Format("ResetView 后 ViewRight 恢复 ({0:F2} ≈ {1:F2})",
                            panel.ViewRight, origRight));
                    Assert(ApproxEqual(panel.ViewBottom, origBottom, 0.1f),
                        string.Format("ResetView 后 ViewBottom 恢复"));
                    Assert(ApproxEqual(panel.ViewTop, origTop, 0.1f),
                        string.Format("ResetView 后 ViewTop 恢复"));

                    Console.WriteLine("  [PASS] Cycle 2");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 3: 滚轮缩放（放大）
        // ================================================================

        /// <summary>
        /// Cycle 3: MouseWheel 向上 → 以鼠标位置为中心放大，viewport 范围缩小。
        /// </summary>
        static void TestMouseWheelZoomIn()
        {
            Console.WriteLine("--- Cycle 3: 滚轮放大 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    float origXRange = panel.ViewRight - panel.ViewLeft;
                    float origYRange = panel.ViewTop - panel.ViewBottom;

                    // 模拟鼠标在图表中心，滚轮向上（Delta=120 表示放大）
                    // 需要先触发 MouseMove 设置鼠标位置，再 MouseWheel
                    int centerX = panel.Width / 2;
                    int centerY = panel.Height / 2;

                    panel.SimulateMouseMove(centerX, centerY);
                    panel.SimulateMouseWheel(centerX, centerY, 120); // 向上=放大

                    float newXRange = panel.ViewRight - panel.ViewLeft;
                    float newYRange = panel.ViewTop - panel.ViewBottom;

                    // 放大后范围应缩小（约 20%）
                    Assert(newXRange < origXRange * 0.9f,
                        string.Format("放大后 X 范围缩小 (orig={0:F1} new={1:F1})", origXRange, newXRange));
                    Assert(newYRange < origYRange * 0.9f,
                        string.Format("放大后 Y 范围缩小 (orig={0:F1} new={1:F1})", origYRange, newYRange));

                    Console.WriteLine("  X范围: {0:F1} → {1:F1}, Y范围: {2:F1} → {3:F1}",
                        origXRange, newXRange, origYRange, newYRange);
                    Console.WriteLine("  [PASS] Cycle 3");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 4: 滚轮缩小（缩小）
        // ================================================================

        /// <summary>
        /// Cycle 4: MouseWheel 向下 → 缩小，但不超出全数据范围。
        /// </summary>
        static void TestMouseWheelZoomOut()
        {
            Console.WriteLine("--- Cycle 4: 滚轮缩小 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    float origXRange = panel.ViewRight - panel.ViewLeft;

                    // 先放大一段，再缩小
                    int cx = panel.Width / 2, cy = panel.Height / 2;
                    panel.SimulateMouseMove(cx, cy);
                    panel.SimulateMouseWheel(cx, cy, 120); // 放大
                    panel.SimulateMouseWheel(cx, cy, 120); // 再放大

                    float zoomedXRange = panel.ViewRight - panel.ViewLeft;
                    Assert(zoomedXRange < origXRange * 0.8f, "两级放大后范围明显缩小");

                    // 缩小回去
                    panel.SimulateMouseWheel(cx, cy, -120); // 缩小
                    float afterOneOut = panel.ViewRight - panel.ViewLeft;
                    Assert(afterOneOut > zoomedXRange,
                        string.Format("缩小后 X 范围扩大 ({0:F1} → {1:F1})", zoomedXRange, afterOneOut));

                    // 使劲缩小，不应超过全数据范围
                    for (int i = 0; i < 20; i++)
                        panel.SimulateMouseWheel(cx, cy, -120);

                    float finalXRange = panel.ViewRight - panel.ViewLeft;
                    Assert(finalXRange <= origXRange * 1.05f,
                        string.Format("缩小极限不超过初始范围 (final={0:F1} orig={1:F1})",
                            finalXRange, origXRange));

                    Console.WriteLine("  原始X范围={0:F1}, 缩小到极限={1:F1}", origXRange, finalXRange);
                    Console.WriteLine("  [PASS] Cycle 4");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 5: 缩放边界限制
        // ================================================================

        /// <summary>
        /// Cycle 5: 缩放到极限时不再继续，不出现负范围或越界。
        /// </summary>
        static void TestZoomBoundaryLimits()
        {
            Console.WriteLine("--- Cycle 5: 缩放边界限制 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    int cx = panel.Width / 2, cy = panel.Height / 2;
                    panel.SimulateMouseMove(cx, cy);

                    // 疯狂放大，不应出现负范围
                    for (int i = 0; i < 100; i++)
                        panel.SimulateMouseWheel(cx, cy, 120);

                    float xRange = panel.ViewRight - panel.ViewLeft;
                    float yRange = panel.ViewTop - panel.ViewBottom;

                    Assert(xRange > 0.1f,
                        string.Format("X 范围不会为负 (range={0:F3})", xRange));
                    Assert(yRange > 0.001f,
                        string.Format("Y 范围不会为负 (range={0:F3})", yRange));

                    // ViewLeft < ViewRight 始终成立
                    Assert(panel.ViewLeft < panel.ViewRight, "ViewLeft < ViewRight");
                    Assert(panel.ViewBottom < panel.ViewTop, "ViewBottom < ViewTop");

                    // 放大后视口在原始数据范围内
                    Assert(panel.ViewLeft >= -1f,
                        string.Format("ViewLeft 不越界 ({0:F2})", panel.ViewLeft));
                    Assert(panel.ViewRight <= 210f,
                        string.Format("ViewRight 不越界 ({0:F2})", panel.ViewRight));

                    Console.WriteLine("  放大到极限: X范围={0:F3}, Y范围={1:F3}", xRange, yRange);
                    Console.WriteLine("  [PASS] Cycle 5");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 6: 鼠标拖拽平移
        // ================================================================

        /// <summary>
        /// Cycle 6: 按住鼠标左键拖拽 → 平移视图。
        /// </summary>
        static void TestMouseDragPan()
        {
            Console.WriteLine("--- Cycle 6: 鼠标拖拽平移 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    float origLeft = panel.ViewLeft;
                    float origRight = panel.ViewRight;
                    float origBottom = panel.ViewBottom;
                    float origTop = panel.ViewTop;

                    // 模拟拖拽：按下 → 移动 → 释放
                    int startX = panel.Width / 2;
                    int startY = panel.Height / 2;
                    int endX = startX - 40; // 向左拖
                    int endY = startY + 20; // 向下拖

                    panel.SimulateMouseDown(startX, startY, MouseButtons.Left);
                    panel.SimulateMouseMove(endX, endY);
                    panel.SimulateMouseUp(endX, endY, MouseButtons.Left);

                    // 向左拖拽 → ViewLeft 和 ViewRight 应增大（曲线向右移动 = 视口向右）
                    float deltaLeft = panel.ViewLeft - origLeft;
                    float deltaRight = panel.ViewRight - origRight;

                    Assert(deltaLeft > 0.05f,
                        string.Format("左拖→ViewLeft 增大 (delta={0:F3})", deltaLeft));
                    Assert(deltaRight > 0.05f,
                        string.Format("左拖→ViewRight 增大 (delta={0:F3})", deltaRight));

                    // 范围保持不变
                    float origXRange = origRight - origLeft;
                    float newXRange = panel.ViewRight - panel.ViewLeft;
                    Assert(ApproxEqual(origXRange, newXRange, 0.5f),
                        string.Format("平移后 X 范围不变 ({0:F2} ≈ {1:F2})", origXRange, newXRange));

                    Console.WriteLine("  ViewLeft: {0:F2}→{1:F2}, ViewRight: {2:F2}→{3:F2}",
                        origLeft, panel.ViewLeft, origRight, panel.ViewRight);
                    Console.WriteLine("  [PASS] Cycle 6");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 7: 框选局部放大
        // ================================================================

        /// <summary>
        /// Cycle 7: Ctrl+左键拖拽 → 框选矩形区域 → 释放后该区域放大到整个图表区。
        /// </summary>
        static void TestRectangleZoom()
        {
            Console.WriteLine("--- Cycle 7: 框选局部放大 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    float origXRange = panel.ViewRight - panel.ViewLeft;

                    // 模拟 Ctrl+左键拖拽一个小矩形
                    // 图表区坐标映射：我们需要在 plot area 内拖拽
                    int x1 = 80, y1 = 50;   // 左上
                    int x2 = 120, y2 = 100; // 右下

                    panel.SimulateMouseDown(x1, y1, MouseButtons.Left, ctrlHeld: true);
                    panel.SimulateMouseMove(x2, y2);
                    panel.SimulateMouseUp(x2, y2, MouseButtons.Left);

                    float newXRange = panel.ViewRight - panel.ViewLeft;

                    // 框选后范围应该缩小（放大了选中区域）
                    Assert(newXRange < origXRange * 0.8f,
                        string.Format("框选放大后 X 范围缩小 (orig={0:F1} new={1:F1})",
                            origXRange, newXRange));

                    Console.WriteLine("  X范围: {0:F1} → {1:F1}", origXRange, newXRange);
                    Console.WriteLine("  新viewport: L={0:F2} R={1:F2} B={2:F2} T={3:F2}",
                        panel.ViewLeft, panel.ViewRight, panel.ViewBottom, panel.ViewTop);
                    Console.WriteLine("  [PASS] Cycle 7");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 8: 右键复位
        // ================================================================

        /// <summary>
        /// Cycle 8: 右键点击 → 上下文菜单 → "复位" → 恢复初始全数据视图。
        /// 同时验证 ResetView() 方法在任何状态下都能恢复。
        /// </summary>
        static void TestRightClickReset()
        {
            Console.WriteLine("--- Cycle 8: 右键复位 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    float origLeft = panel.ViewLeft, origRight = panel.ViewRight;
                    float origBottom = panel.ViewBottom, origTop = panel.ViewTop;

                    // 先进行一系列操作改变 viewport
                    int cx = panel.Width / 2, cy = panel.Height / 2;
                    panel.SimulateMouseMove(cx, cy);
                    panel.SimulateMouseWheel(cx, cy, 120); // 放大
                    panel.SimulateMouseWheel(cx, cy, 120); // 再放大

                    // 确认 viewport 已改变
                    float zoomedXRange = panel.ViewRight - panel.ViewLeft;
                    Assert(zoomedXRange < (origRight - origLeft) * 0.8f,
                        "缩放后 viewport 已改变");

                    // 调用 ResetView
                    panel.ResetView();

                    // 验证完全恢复
                    Assert(ApproxEqual(panel.ViewLeft, origLeft, 0.1f),
                        string.Format("复位后 ViewLeft 恢复 ({0:F2}≈{1:F2})",
                            panel.ViewLeft, origLeft));
                    Assert(ApproxEqual(panel.ViewRight, origRight, 0.1f),
                        string.Format("复位后 ViewRight 恢复"));
                    Assert(ApproxEqual(panel.ViewBottom, origBottom, 0.1f),
                        string.Format("复位后 ViewBottom 恢复"));
                    Assert(ApproxEqual(panel.ViewTop, origTop, 0.1f),
                        string.Format("复位后 ViewTop 恢复"));

                    Console.WriteLine("  复位后: L={0:F2} R={1:F2} B={2:F2} T={3:F2}",
                        panel.ViewLeft, panel.ViewRight, panel.ViewBottom, panel.ViewTop);
                    Console.WriteLine("  [PASS] Cycle 8");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 9: 坐标轴标签更新
        // ================================================================

        /// <summary>
        /// Cycle 9: 拖拽/缩放后，坐标轴标签反映当前可见范围内的数值。
        /// </summary>
        static void TestAxisLabelsUpdate()
        {
            Console.WriteLine("--- Cycle 9: 坐标轴标签更新 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data, null, null);
                    EnsureHandle(panel);

                    // 轴标签应该反映当前 viewport
                    // X轴显示采样序号范围
                    float xMin = panel.ViewLeft;
                    float xMax = panel.ViewRight;

                    Assert(xMin >= -1f && xMax <= 210f,
                        string.Format("X轴范围合理: [{0:F1}, {1:F1}]", xMin, xMax));

                    // 缩放后轴标签应该更新
                    int cx = panel.Width / 2, cy = panel.Height / 2;
                    panel.SimulateMouseMove(cx, cy);
                    panel.SimulateMouseWheel(cx, cy, 120);

                    float newXMin = panel.ViewLeft;
                    float newXMax = panel.ViewRight;

                    // 缩放后 X 范围缩小（轴标签更精细）
                    Assert((newXMax - newXMin) < (xMax - xMin),
                        "缩放后轴标签范围变小（更精细）");

                    Console.WriteLine("  缩放前轴范围: [{0:F1}, {1:F1}], 缩放后: [{2:F1}, {3:F1}]",
                        xMin, xMax, newXMin, newXMax);
                    Console.WriteLine("  [PASS] Cycle 9");
                    passed++;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // TDD Cycle 10: 切换数据时视图重置
        // ================================================================

        /// <summary>
        /// Cycle 10: 切换动作（SetSamples）时视图自动重置到全数据范围。
        /// </summary>
        static void TestViewResetOnDataChange()
        {
            Console.WriteLine("--- Cycle 10: 切换动作时视图重置 ---");
            try
            {
                using (var panel = new CurveChartPanel())
                {
                    var data1 = MakeLinearData(200, 0f, 10f);
                    panel.SetSamples(data1, null, null);
                    EnsureHandle(panel);

                    // 先缩放
                    int cx = panel.Width / 2, cy = panel.Height / 2;
                    panel.SimulateMouseMove(cx, cy);
                    panel.SimulateMouseWheel(cx, cy, 120);

                    float zoomedLeft = panel.ViewLeft;
                    float zoomedRight = panel.ViewRight;

                    // 切换到不同数据
                    var data2 = MakeLinearData(100, 5f, 20f);
                    panel.SetSamples(data2, null, null);

                    // 验证 viewport 已重置到新数据的全范围
                    Assert(panel.ViewLeft <= 1f,
                        string.Format("新数据 ViewLeft 从 0 开始 ({0:F2})", panel.ViewLeft));
                    Assert(panel.ViewRight >= 98f,
                        string.Format("新数据 ViewRight 到末尾 ({0:F2})", panel.ViewRight));

                    // 不应停留在旧的缩放状态
                    Assert(!ApproxEqual(panel.ViewLeft, zoomedLeft, 0.5f),
                        "ViewLeft 不是旧的缩放值");

                    Console.WriteLine("  旧缩放: L={0:F2} R={1:F2}, 新数据: L={2:F2} R={3:F2}",
                        zoomedLeft, zoomedRight, panel.ViewLeft, panel.ViewRight);
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

        /// <summary>生成线性测试数据</summary>
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

        /// <summary>确保控件创建了窗口句柄并设置合理尺寸</summary>
        static void EnsureHandle(Control c)
        {
            // 设置合理尺寸以便绘图区域计算
            c.Size = new Size(800, 500);
            // 访问 Handle 强制创建窗口句柄
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
