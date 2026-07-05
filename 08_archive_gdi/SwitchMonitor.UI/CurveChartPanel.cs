using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 曲线图表自定义面板。
    /// 使用 GDI+ 绘制三相电流/电压/功率曲线，支持缩放/拖拽/框选交互。
    /// </summary>
    public class CurveChartPanel : Panel
    {
        // ---- 颜色定义 ----
        private static readonly Color PhaseAColor = Color.FromArgb(220, 50, 50);   // 红
        private static readonly Color PhaseBColor = Color.FromArgb(50, 150, 50);   // 绿
        private static readonly Color PhaseCColor = Color.FromArgb(50, 100, 220);   // 蓝
        private static readonly Color PowerColor = Color.FromArgb(230, 150, 30);    // 橙
        private static readonly Color GridColor = Color.FromArgb(220, 220, 220);    // 浅灰
        private static readonly Color AxisColor = Color.FromArgb(80, 80, 80);       // 深灰
        private static readonly Color BgColor = Color.White;
        private static readonly Color SelectRectColor = Color.FromArgb(80, 100, 180, 255); // 半透明蓝
        private static readonly Color RefCurveColor = Color.FromArgb(128, 128, 128, 128);  // 灰色半透明（参考曲线）
        private static readonly Color RefLabelColor = Color.FromArgb(128, 128, 128);       // 灰色标签

        // ---- 数据 ----
        private List<float> _phaseA;
        private List<float> _phaseB;
        private List<float> _phaseC;

        // ---- 参考曲线数据 ----
        private List<float> _refPhaseA;
        private List<float> _refPhaseB;
        private List<float> _refPhaseC;
        private string _refLabel;       // 参考曲线标签文字
        private bool _hasReferenceCurve;

        // ---- 阈值线数据 ----
        private float? _thresholdValue;        // 阈值线 Y 值（null 表示不显示）
        private Color _thresholdColor = Color.Red;
        private DashStyle _thresholdLineStyle = DashStyle.Dash;
        private string _thresholdLabel;        // 阈值标签文字

        // ---- 边距 ----
        private const int MarginLeft = 60;
        private const int MarginRight = 20;
        private const int MarginTop = 20;
        private const int MarginBottom = 40;

        // ---- 视口状态 ----
        private float _viewLeft;      // 可见 X 范围起始（采样序号）
        private float _viewRight;     // 可见 X 范围结束（采样序号）
        private float _viewTop;       // 可见 Y 范围上限（数值）
        private float _viewBottom;    // 可见 Y 范围下限（数值）

        // 初始全数据范围（用于复位）
        private float _origViewLeft;
        private float _origViewRight;
        private float _origViewTop;
        private float _origViewBottom;
        private bool _viewportInitialized;

        // ---- 鼠标交互状态 ----
        private bool _isDragging;
        private bool _isRectZooming;  // Ctrl+拖拽 框选模式
        private Point _dragStart;
        private Point _dragCurrent;
        private float _dragStartViewLeft;
        private float _dragStartViewRight;
        private float _dragStartViewTop;
        private float _dragStartViewBottom;

        // ---- 系列显隐控制 ----
        private bool _phaseAVisible = true;
        private bool _phaseBVisible = true;
        private bool _phaseCVisible = true;
        private bool _thresholdVisible = true;
        private bool _yAxisFixed = false;

        // ---- 边界状态 ----
        private string _emptyStateMessage = "请选择转辙机查看曲线";
        private bool _isLoading = false;
        private string _errorMessage = null;

        // ---- 导出事件 ----
        /// <summary>用户请求导出 PNG 图片</summary>
        public event EventHandler ExportImageRequested;
        /// <summary>用户请求导出 CSV 数据</summary>
        public event EventHandler ExportCsvRequested;

        // ---- 视口同步事件 ----
        /// <summary>视口变化时触发，用于同步状态时间线</summary>
        public event Action<float, float, int> ViewportChanged;  // viewLeft, viewRight, maxSampleCount

        // ---- 缩放约束 ----
        private const float ZoomFactor = 0.2f;          // 每次缩放 20%
        private const float MinXRange = 0.5f;            // 最小 X 范围（采样点数）
        private const float MinYRange = 0.001f;          // 最小 Y 范围
        private const float MaxZoomFactor = 20f;         // 最大放大倍数

        /// <summary>Y 轴标签文字</summary>
        public string YAxisLabel { get; set; } = "电流 (A)";

        /// <summary>是否有曲线数据可供导出</summary>
        public bool HasData
        {
            get
            {
                return (_phaseA != null && _phaseA.Count > 0)
                    || (_phaseB != null && _phaseB.Count > 0)
                    || (_phaseC != null && _phaseC.Count > 0);
            }
        }

        // ---- 系列显隐属性 ----

        /// <summary>A相曲线是否可见</summary>
        public bool PhaseAVisible { get { return _phaseAVisible; } }

        /// <summary>B相曲线是否可见</summary>
        public bool PhaseBVisible { get { return _phaseBVisible; } }

        /// <summary>C相曲线是否可见</summary>
        public bool PhaseCVisible { get { return _phaseCVisible; } }

        /// <summary>阈值线是否可见（不影响阈值值本身）</summary>
        public bool ThresholdVisible { get { return _thresholdVisible; } }

        /// <summary>Y轴是否固定量程（true=保持，false=自适应）</summary>
        public bool YAxisFixed { get { return _yAxisFixed; } }

        /// <summary>
        /// 设置各相曲线的可见性。只触发重绘，不重置视口。
        /// </summary>
        public void SetPhaseVisibility(bool a, bool b, bool c)
        {
            _phaseAVisible = a;
            _phaseBVisible = b;
            _phaseCVisible = c;
            Invalidate();
        }

        /// <summary>
        /// 设置阈值线可见性。隐藏阈值线时不改变阈值值本身。
        /// </summary>
        public void SetThresholdVisible(bool visible)
        {
            _thresholdVisible = visible;
            Invalidate();
        }

        /// <summary>
        /// 设置 Y 轴量程固定模式。
        /// true = 保持量程不随缩放/数据变化; false = 自适应当前可见数据范围。
        /// </summary>
        public void SetYAxisFixed(bool fixedRange)
        {
            _yAxisFixed = fixedRange;
            if (!fixedRange && _viewportInitialized)
            {
                // 从固定模式切回自适应时，重新计算 Y 范围
                AdjustYRangeToVisibleData();
            }
            Invalidate();
        }

        // ---- 边界状态属性 ----

        /// <summary>空状态提示文字（无数据时显示）</summary>
        public string EmptyStateMessage
        {
            get { return _emptyStateMessage; }
            set { _emptyStateMessage = value; Invalidate(); }
        }

        /// <summary>是否正在加载数据</summary>
        public bool IsLoading
        {
            get { return _isLoading; }
            set { _isLoading = value; Invalidate(); }
        }

        /// <summary>错误消息（非 null 时显示错误提示）</summary>
        public string ErrorMessage
        {
            get { return _errorMessage; }
            set { _errorMessage = value; Invalidate(); }
        }

        // ---- 公开视口属性（供测试使用） ----

        /// <summary>当前可见 X 轴范围起始（采样序号）</summary>
        public float ViewLeft { get { return _viewLeft; } }

        /// <summary>当前可见 X 轴范围结束（采样序号）</summary>
        public float ViewRight { get { return _viewRight; } }

        /// <summary>当前可见 Y 轴范围上限（数值）</summary>
        public float ViewTop { get { return _viewTop; } }

        /// <summary>当前可见 Y 轴范围下限（数值）</summary>
        public float ViewBottom { get { return _viewBottom; } }

        public CurveChartPanel()
        {
            this.DoubleBuffered = true;
            this.ResizeRedraw = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer |
                          ControlStyles.Selectable, true); // Selectable 用于接收鼠标事件
        }

        /// <summary>
        /// 设置三相曲线数据。传入 null 表示无数据。
        /// 每次设置新数据时自动重置视口。
        /// </summary>
        public void SetSamples(List<float> phaseA, List<float> phaseB, List<float> phaseC)
        {
            _phaseA = phaseA;
            _phaseB = phaseB;
            _phaseC = phaseC;

            // 立即计算视口（不依赖 OnPaint 执行，方便测试和行为一致性）
            InitializeViewport();
            Invalidate();
        }

        /// <summary>
        /// 设置参考曲线叠加数据。传入 null 表示清除。
        /// </summary>
        /// <param name="phaseA">A相参考数据</param>
        /// <param name="phaseB">B相参考数据</param>
        /// <param name="phaseC">C相参考数据</param>
        /// <param name="label">参考曲线标签（如 "参考曲线设定于 2026-06-15 (检修后)"）</param>
        public void SetReferenceSamples(List<float> phaseA, List<float> phaseB, List<float> phaseC, string label)
        {
            _refPhaseA = phaseA;
            _refPhaseB = phaseB;
            _refPhaseC = phaseC;
            _refLabel = label;
            _hasReferenceCurve = phaseA != null || phaseB != null || phaseC != null;

            // 有参考曲线时重新计算 Y 轴范围以包含参考数据
            if (_hasReferenceCurve && _viewportInitialized)
            {
                ExpandYRangeForReference();
            }
            Invalidate();
        }

        /// <summary>
        /// 清除参考曲线叠加。
        /// </summary>
        public void ClearReferenceSamples()
        {
            _refPhaseA = null;
            _refPhaseB = null;
            _refPhaseC = null;
            _refLabel = null;
            _hasReferenceCurve = false;
            Invalidate();
        }

        /// <summary>是否有参考曲线数据</summary>
        public bool HasReferenceCurve
        {
            get { return _hasReferenceCurve; }
        }

        // ================================================================
        // 阈值线
        // ================================================================

        /// <summary>
        /// 设置阈值线。
        /// </summary>
        /// <param name="value">阈值线的 Y 值</param>
        /// <param name="color">线条颜色</param>
        /// <param name="lineStyle">线条样式</param>
        public void SetThresholdLine(float value, Color color, System.Drawing.Drawing2D.DashStyle lineStyle)
        {
            _thresholdValue = value;
            _thresholdColor = color;
            _thresholdLineStyle = lineStyle;
            _thresholdLabel = string.Format("报警上限 {0:F1}", value);
            Invalidate();
        }

        /// <summary>
        /// 清除阈值线。
        /// </summary>
        public void ClearThresholdLines()
        {
            _thresholdValue = null;
            _thresholdLabel = null;
            Invalidate();
        }

        /// <summary>是否有阈值线</summary>
        public bool HasThresholdLine
        {
            get { return _thresholdValue.HasValue; }
        }

        /// <summary>阈值线 Y 值</summary>
        public float? ThresholdValue
        {
            get { return _thresholdValue; }
        }

        /// <summary>恢复初始全数据视图</summary>
        public void ResetView()
        {
            if (!_viewportInitialized)
                return;

            _viewLeft = _origViewLeft;
            _viewRight = _origViewRight;
            _viewTop = _origViewTop;
            _viewBottom = _origViewBottom;
            FireViewportChanged();
            Invalidate();
        }

        /// <summary>
        /// 缩放到曲线的指定比例区域（用于诊断结果点击导航）。
        /// </summary>
        /// <param name="startRatio">起始比例 0.0~1.0</param>
        /// <param name="endRatio">结束比例 0.0~1.0</param>
        public void ZoomToSegment(float startRatio, float endRatio)
        {
            if (!_viewportInitialized)
                return;

            float totalRange = _origViewRight - _origViewLeft;
            float newLeft = _origViewLeft + totalRange * startRatio;
            float newRight = _origViewLeft + totalRange * endRatio;

            // 边界保护
            if (newRight <= newLeft + 0.5f)
                newRight = newLeft + 0.5f;

            _viewLeft = newLeft;
            _viewRight = newRight;

            // Y 轴保持原始范围
            _viewTop = _origViewTop;
            _viewBottom = _origViewBottom;

            FireViewportChanged();
            Invalidate();
        }

        /// <summary>
        /// 获取当前数据的最大采样数（用于视口同步）。
        /// </summary>
        public int MaxSampleCount
        {
            get
            {
                int max = 0;
                if (_phaseA != null && _phaseA.Count > max) max = _phaseA.Count;
                if (_phaseB != null && _phaseB.Count > max) max = _phaseB.Count;
                if (_phaseC != null && _phaseC.Count > max) max = _phaseC.Count;
                return max;
            }
        }

        // ================================================================
        // 测试辅助方法 — 模拟鼠标事件（internal 供测试项目通过 InternalsVisibleTo 访问）
        // ================================================================

        /// <summary>模拟鼠标移动</summary>
        public void SimulateMouseMove(int x, int y)
        {
            var args = new MouseEventArgs(MouseButtons.None, 0, x, y, 0);
            OnMouseMove(args);
        }

        /// <summary>模拟鼠标滚轮</summary>
        public void SimulateMouseWheel(int x, int y, int delta)
        {
            var args = new MouseEventArgs(MouseButtons.None, 0, x, y, delta);
            OnMouseWheel(args);
        }

        /// <summary>模拟鼠标按下</summary>
        public void SimulateMouseDown(int x, int y, MouseButtons button, bool ctrlHeld = false)
        {
            // 设置 Control 键状态
            if (ctrlHeld)
            {
                _simulateCtrlKey = true;
            }
            var args = new MouseEventArgs(button, 1, x, y, 0);
            OnMouseDown(args);
        }

        /// <summary>模拟鼠标释放</summary>
        public void SimulateMouseUp(int x, int y, MouseButtons button)
        {
            var args = new MouseEventArgs(button, 1, x, y, 0);
            OnMouseUp(args);
        }

        private bool _simulateCtrlKey;

        // ================================================================
        // 坐标转换辅助
        // ================================================================

        /// <summary>获取绘图区域矩形</summary>
        private Rectangle GetPlotArea()
        {
            int left = MarginLeft;
            int right = Width - MarginRight;
            int top = MarginTop;
            int bottom = Height - MarginBottom;
            return new Rectangle(left, top, right - left, bottom - top);
        }

        /// <summary>数据坐标 → 屏幕 X</summary>
        private float DataXToScreen(float dataX, Rectangle plot)
        {
            float xRange = _viewRight - _viewLeft;
            if (xRange < 0.001f) xRange = 1.0f;
            return plot.Left + (dataX - _viewLeft) / xRange * plot.Width;
        }

        /// <summary>屏幕 X → 数据坐标</summary>
        private float ScreenXToData(int screenX, Rectangle plot)
        {
            return _viewLeft + (float)(screenX - plot.Left) / plot.Width * (_viewRight - _viewLeft);
        }

        /// <summary>数据坐标 → 屏幕 Y</summary>
        private float DataYToScreen(float dataY, Rectangle plot)
        {
            float yRange = _viewTop - _viewBottom;
            if (yRange < 0.001f) yRange = 1.0f;
            return plot.Bottom - (dataY - _viewBottom) / yRange * plot.Height;
        }

        /// <summary>屏幕 Y → 数据坐标</summary>
        private float ScreenYToData(int screenY, Rectangle plot)
        {
            return _viewTop - (float)(screenY - plot.Top) / plot.Height * (_viewTop - _viewBottom);
        }

        // ================================================================
        // 鼠标事件
        // ================================================================

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            if (!_viewportInitialized) return;

            var plot = GetPlotArea();
            if (plot.Width <= 0 || plot.Height <= 0) return;

            // 以鼠标位置为中心缩放
            float mouseDataX = ScreenXToData(e.X, plot);
            float mouseDataY = ScreenYToData(e.Y, plot);

            // 缩放因子：向上=放大（缩小视口），向下=缩小（扩大视口）
            float factor = e.Delta > 0 ? (1f - ZoomFactor) : (1f + ZoomFactor);

            float newXRange = (_viewRight - _viewLeft) * factor;
            float newYRange = (_viewTop - _viewBottom) * factor;

            // 边界检查：不超出全数据范围
            float origXRange = _origViewRight - _origViewLeft;
            float origYRange = _origViewTop - _origViewBottom;

            // 最小范围约束
            if (newXRange < MinXRange) newXRange = MinXRange;
            if (newYRange < MinYRange) newYRange = MinYRange;

            // 最大范围约束（不超出初始全数据范围）
            if (newXRange > origXRange * 1.05f)
            {
                newXRange = origXRange;
            }
            if (newYRange > origYRange * 1.05f)
            {
                newYRange = origYRange;
            }

            // 以鼠标位置为中心计算新视口
            float ratioX = (float)(e.X - plot.Left) / plot.Width;
            float ratioY = (float)(plot.Bottom - e.Y) / plot.Height;

            float newLeft = mouseDataX - newXRange * ratioX;
            float newRight = newLeft + newXRange;
            float newBottom = mouseDataY - newYRange * ratioY;
            float newTop = newBottom + newYRange;

            // 边界裁剪：如果新视口超出数据范围，平移回来
            if (newLeft < _origViewLeft - origXRange * 0.1f)
            {
                float shift = _origViewLeft - origXRange * 0.1f - newLeft;
                newLeft += shift;
                newRight += shift;
            }
            if (newRight > _origViewRight + origXRange * 0.1f)
            {
                float shift = newRight - (_origViewRight + origXRange * 0.1f);
                newLeft -= shift;
                newRight -= shift;
            }

            // Y 轴自适应当前可见 X 范围
            if (newBottom < _origViewBottom - origYRange * 0.2f)
            {
                float shift = _origViewBottom - origYRange * 0.2f - newBottom;
                newBottom += shift;
                newTop += shift;
            }
            if (newTop > _origViewTop + origYRange * 0.2f)
            {
                float shift = newTop - (_origViewTop + origYRange * 0.2f);
                newBottom -= shift;
                newTop -= shift;
            }

            _viewLeft = newLeft;
            _viewRight = newRight;

            // Y轴固定模式下不改变 Y 范围
            if (!_yAxisFixed)
            {
                _viewBottom = newBottom;
                _viewTop = newTop;

                // 放大时 Y 轴自适应当前可见 X 范围内的数据
                if (factor < 1.0f)
                {
                    AdjustYRangeToVisibleData();
                }
            }

            FireViewportChanged();
            Invalidate();
        }

        /// <summary>触发视口变化事件</summary>
        private void FireViewportChanged()
        {
            if (_viewportInitialized && ViewportChanged != null)
            {
                ViewportChanged(_viewLeft, _viewRight, GetMaxSampleCount());
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (!_viewportInitialized) return;

            // 右键 → 弹出上下文菜单
            if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu(e.Location);
                return;
            }

            // 左键
            if (e.Button == MouseButtons.Left)
            {
                _dragStart = e.Location;
                _dragCurrent = e.Location;
                _dragStartViewLeft = _viewLeft;
                _dragStartViewRight = _viewRight;
                _dragStartViewTop = _viewTop;
                _dragStartViewBottom = _viewBottom;

                // Ctrl+左键 → 框选放大；纯左键 → 平移
                if (_simulateCtrlKey || (ModifierKeys & Keys.Control) != 0)
                {
                    _isRectZooming = true;
                }
                else
                {
                    _isDragging = true;
                    this.Cursor = Cursors.Hand;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isDragging)
            {
                // 平移模式
                var plot = GetPlotArea();
                if (plot.Width <= 0 || plot.Height <= 0) return;

                float deltaDataX = ScreenXToData(_dragStart.X, plot) - ScreenXToData(e.X, plot);
                float deltaDataY = ScreenYToData(e.Y, plot) - ScreenYToData(_dragStart.Y, plot);

                _viewLeft = _dragStartViewLeft + deltaDataX;
                _viewRight = _dragStartViewRight + deltaDataX;

                // Y轴固定模式下不改变 Y 范围
                if (!_yAxisFixed)
                {
                    _viewBottom = _dragStartViewBottom + deltaDataY;
                    _viewTop = _dragStartViewTop + deltaDataY;
                }

                Invalidate();
            }
            else if (_isRectZooming)
            {
                // 框选模式：记录当前位置用于绘制预览矩形
                _dragCurrent = e.Location;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_isDragging)
            {
                _isDragging = false;
                this.Cursor = Cursors.Default;
                FireViewportChanged();
            }
            else if (_isRectZooming)
            {
                _isRectZooming = false;
                this.Cursor = Cursors.Default;

                // 完成框选放大
                if (Math.Abs(_dragCurrent.X - _dragStart.X) > 5 &&
                    Math.Abs(_dragCurrent.Y - _dragStart.Y) > 5)
                {
                    ApplyRectangleZoom();
                }
            }
        }

        /// <summary>将框选矩形区域放大到整个图表区</summary>
        private void ApplyRectangleZoom()
        {
            var plot = GetPlotArea();
            if (plot.Width <= 0 || plot.Height <= 0) return;

            float x1 = ScreenXToData(Math.Min(_dragStart.X, _dragCurrent.X), plot);
            float x2 = ScreenXToData(Math.Max(_dragStart.X, _dragCurrent.X), plot);
            float y1 = ScreenYToData(Math.Max(_dragStart.Y, _dragCurrent.Y), plot);
            float y2 = ScreenYToData(Math.Min(_dragStart.Y, _dragCurrent.Y), plot);

            if (Math.Abs(x2 - x1) < MinXRange || Math.Abs(y1 - y2) < MinYRange)
                return;

            _viewLeft = x1;
            _viewRight = x2;

            // Y轴固定模式下不改变 Y 范围
            if (!_yAxisFixed)
            {
                _viewBottom = y2;
                _viewTop = y1;
            }

            FireViewportChanged();
            Invalidate();
        }

        /// <summary>弹出右键上下文菜单</summary>
        private void ShowContextMenu(Point location)
        {
            var menu = new ContextMenuStrip();

            // 导出 PNG 图片
            var exportImageItem = new ToolStripMenuItem("导出图片 (PNG)...");
            exportImageItem.Click += (s, e) => ExportImageRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exportImageItem);

            // 导出 CSV 数据
            var exportCsvItem = new ToolStripMenuItem("导出数据 (CSV)...");
            exportCsvItem.Click += (s, e) => ExportCsvRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exportCsvItem);

            menu.Items.Add(new ToolStripSeparator());

            // 复位
            var resetItem = new ToolStripMenuItem("复位");
            resetItem.Click += (s, e) => ResetView();
            menu.Items.Add(resetItem);

            menu.Closed += (s, e) => ((ContextMenuStrip)s).Dispose();
            menu.Show(this, location);
        }

        // ================================================================
        // 视口初始化
        // ================================================================

        /// <summary>根据当前数据初始化视口（显示全部数据 + 10% padding）</summary>
        private void InitializeViewport()
        {
            // 合并所有数据求全局范围
            List<float> allValues = new List<float>();
            if (_phaseA != null) allValues.AddRange(_phaseA);
            if (_phaseB != null) allValues.AddRange(_phaseB);
            if (_phaseC != null) allValues.AddRange(_phaseC);

            int maxSampleCount = 0;
            if (_phaseA != null) maxSampleCount = Math.Max(maxSampleCount, _phaseA.Count);
            if (_phaseB != null) maxSampleCount = Math.Max(maxSampleCount, _phaseB.Count);
            if (_phaseC != null) maxSampleCount = Math.Max(maxSampleCount, _phaseC.Count);

            if (allValues.Count == 0 || maxSampleCount == 0)
            {
                _viewLeft = 0;
                _viewRight = 1;
                _viewBottom = 0;
                _viewTop = 1;
                _viewportInitialized = false;
                return;
            }

            // X 范围：0 到 sampleCount-1
            _viewLeft = 0;
            _viewRight = maxSampleCount - 1;

            // Y 范围：最小到最大 + 10% padding
            float yMin = float.MaxValue;
            float yMax = float.MinValue;
            foreach (float v in allValues)
            {
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }

            float margin = (yMax - yMin) * 0.1f;
            if (margin < 0.01f) margin = 0.5f;
            _viewBottom = yMin - margin;
            _viewTop = yMax + margin;

            // 如果 0 在范围内，向下扩展到 0
            if (_viewBottom > 0) _viewBottom = 0;

            // 保存初始状态用于复位
            _origViewLeft = _viewLeft;
            _origViewRight = _viewRight;
            _origViewTop = _viewTop;
            _origViewBottom = _viewBottom;
            _viewportInitialized = true;
        }

        /// <summary>有参考曲线时扩展 Y 轴范围以包含参考数据</summary>
        private void ExpandYRangeForReference()
        {
            float yMin = _viewBottom;
            float yMax = _viewTop;
            bool expanded = false;

            foreach (var data in new[] { _refPhaseA, _refPhaseB, _refPhaseC })
            {
                if (data == null) continue;
                for (int i = 0; i < data.Count; i++)
                {
                    float v = data[i];
                    if (v < yMin) { yMin = v; expanded = true; }
                    if (v > yMax) { yMax = v; expanded = true; }
                }
            }

            if (expanded)
            {
                float margin = (yMax - yMin) * 0.05f;
                if (margin < 0.01f) margin = 0.1f;
                _viewBottom = yMin - margin;
                _viewTop = yMax + margin;

                if (_viewBottom > 0) _viewBottom = 0;
                if (_viewTop - _viewBottom < MinYRange)
                    _viewTop = _viewBottom + MinYRange;

                // 更新原始边界以包含参考数据
                _origViewBottom = Math.Min(_origViewBottom, _viewBottom);
                _origViewTop = Math.Max(_origViewTop, _viewTop);
            }
        }

        /// <summary>放大时 Y 轴自适应当前可见 X 范围内的数据</summary>
        private void AdjustYRangeToVisibleData()
        {
            // 收集当前可见 X 范围内的数据值
            float yMin = float.MaxValue;
            float yMax = float.MinValue;
            bool found = false;

            int iStart = Math.Max(0, (int)_viewLeft);
            int iEnd = Math.Min(GetMaxSampleCount() - 1, (int)Math.Ceiling(_viewRight));

            foreach (var data in new[] { _phaseA, _phaseB, _phaseC })
            {
                if (data == null) continue;
                for (int i = Math.Max(0, iStart); i <= Math.Min(data.Count - 1, iEnd); i++)
                {
                    float v = data[i];
                    if (v < yMin) yMin = v;
                    if (v > yMax) yMax = v;
                    found = true;
                }
            }

            if (!found) return;

            float margin = (yMax - yMin) * 0.05f;
            if (margin < 0.01f) margin = 0.1f;
            _viewBottom = yMin - margin;
            _viewTop = yMax + margin;

            if (_viewBottom > 0) _viewBottom = 0;
            if (_viewTop - _viewBottom < MinYRange)
                _viewTop = _viewBottom + MinYRange;
        }

        private int GetMaxSampleCount()
        {
            int max = 0;
            if (_phaseA != null) max = Math.Max(max, _phaseA.Count);
            if (_phaseB != null) max = Math.Max(max, _phaseB.Count);
            if (_phaseC != null) max = Math.Max(max, _phaseC.Count);
            return max;
        }

        // ================================================================
        // 绘制
        // ================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var plot = GetPlotArea();
            if (plot.Width <= 0 || plot.Height <= 0)
                return;

            // 首次绘制时初始化视口
            if (!_viewportInitialized)
                InitializeViewport();

            // 如果仍无数据，显示空状态
            List<float> allValues = new List<float>();
            if (_phaseA != null) allValues.AddRange(_phaseA);
            if (_phaseB != null) allValues.AddRange(_phaseB);
            if (_phaseC != null) allValues.AddRange(_phaseC);
            int maxSampleCount = GetMaxSampleCount();

            if (allValues.Count == 0 || maxSampleCount == 0 || !_viewportInitialized)
            {
                DrawEmptyState(g);
                return;
            }

            // 背景
            g.Clear(BgColor);

            float yMin = _viewBottom;
            float yMax = _viewTop;
            float yRange = yMax - yMin;
            if (yRange < MinYRange) yRange = MinYRange;

            // 降采样
            int downSampleFactor = 1;
            int visiblePointCount = (int)(_viewRight - _viewLeft) + 1;
            if (visiblePointCount > 500 && visiblePointCount > plot.Width * 2)
            {
                downSampleFactor = visiblePointCount / plot.Width + 1;
            }

            // === 绘制网格线 ===
            DrawGrid(g, plot, yMin, yMax, yRange);

            // === 绘制坐标轴 ===
            DrawAxes(g, plot, yMin, yMax, yRange, maxSampleCount);

            // === 绘制曲线（遵照样例显隐标志） ===
            if (_phaseAVisible) DrawCurve(g, _phaseA, PhaseAColor, plot, yMin, yRange, downSampleFactor);
            if (_phaseBVisible) DrawCurve(g, _phaseB, PhaseBColor, plot, yMin, yRange, downSampleFactor);
            if (_phaseCVisible) DrawCurve(g, _phaseC, PhaseCColor, plot, yMin, yRange, downSampleFactor);

            // === 绘制参考曲线（虚线半透明叠加） ===
            if (_hasReferenceCurve)
            {
                int refDownSample = downSampleFactor;
                // 参考曲线可能采样点数不同，重新估算降采样因子
                int refMaxCount = 0;
                if (_refPhaseA != null) refMaxCount = Math.Max(refMaxCount, _refPhaseA.Count);
                if (_refPhaseB != null) refMaxCount = Math.Max(refMaxCount, _refPhaseB.Count);
                if (_refPhaseC != null) refMaxCount = Math.Max(refMaxCount, _refPhaseC.Count);
                if (refMaxCount > 500 && refMaxCount > plot.Width * 2)
                    refDownSample = refMaxCount / plot.Width + 1;

                DrawReferenceCurve(g, _refPhaseA, plot, yMin, yRange, refDownSample);
                DrawReferenceCurve(g, _refPhaseB, plot, yMin, yRange, refDownSample);
                DrawReferenceCurve(g, _refPhaseC, plot, yMin, yRange, refDownSample);

                // 绘制参考曲线设定时间标签
                if (!string.IsNullOrEmpty(_refLabel))
                {
                    DrawReferenceLabel(g, _refLabel);
                }
            }

            // === 绘制阈值线 ===
            if (_thresholdValue.HasValue && _thresholdVisible)
            {
                DrawThresholdLine(g, plot, _thresholdValue.Value, yMin, yRange);
            }

            // === 绘制框选预览矩形 ===
            if (_isRectZooming)
            {
                DrawSelectionRect(g);
            }

            // === 绘制图例 ===
            DrawLegend(g);
        }

        private void DrawGrid(Graphics g, Rectangle plot, float yMin, float yMax, float yRange)
        {
            using (var gridPen = new Pen(GridColor, 1) { DashStyle = DashStyle.Dot })
            {
                // 垂直网格线
                int xGridCount = 5;
                for (int i = 0; i <= xGridCount; i++)
                {
                    float x = plot.Left + (float)i / xGridCount * plot.Width;
                    g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                }
            }
        }

        private void DrawAxes(Graphics g, Rectangle plot, float yMin, float yMax, float yRange, int maxSampleCount)
        {
            using (var axisPen = new Pen(AxisColor, 1))
            using (var font = new Font("宋体", 9))
            using (var fontSm = new Font("宋体", 8))
            {
                // X 轴
                g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
                // Y 轴
                g.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);

                // X 轴标签（当前可见范围内的采样序号）
                int labelCount = Math.Min(5, (int)(_viewRight - _viewLeft) + 1);
                if (labelCount < 2) labelCount = 2;
                for (int i = 0; i <= labelCount; i++)
                {
                    int sampleIdx = (int)(_viewLeft + (float)i / labelCount * (_viewRight - _viewLeft));
                    sampleIdx = Math.Max(0, Math.Min(maxSampleCount - 1, sampleIdx));
                    float x = plot.Left + (float)i / labelCount * plot.Width;
                    g.DrawString(sampleIdx.ToString(), fontSm, Brushes.Black,
                        x - 15, plot.Bottom + 4);
                }

                // Y 轴标签（Nice Numbers 自适应）
                int yLabelCount = 5;
                for (int i = 0; i <= yLabelCount; i++)
                {
                    float val = yMin + (float)i / yLabelCount * yRange;
                    float y = plot.Bottom - (val - yMin) / yRange * plot.Height;
                    string label = val >= 100 ? val.ToString("F0") :
                                   val >= 10 ? val.ToString("F1") : val.ToString("F2");
                    g.DrawString(label, fontSm, Brushes.Black,
                        2, y - 8);

                    // 网格线
                    using (var gridPen = new Pen(GridColor, 1) { DashStyle = DashStyle.Dot })
                    {
                        g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    }
                }
            }
        }

        private void DrawCurve(Graphics g, List<float> data, Color color,
            Rectangle plot, float yMin, float yRange, int downSample)
        {
            if (data == null || data.Count < 2)
                return;

            using (var pen = new Pen(color, 1.5f))
            {
                int step = Math.Max(1, downSample);
                PointF prev = PointF.Empty;
                bool first = true;

                for (int i = 0; i < data.Count; i += step)
                {
                    // 跳过不在视口范围内的点
                    if (i < (int)_viewLeft - 1 || i > (int)_viewRight + 1)
                    {
                        first = true;
                        continue;
                    }

                    float x = DataXToScreen(i, plot);
                    float val = data[i];
                    float y = DataYToScreen(val, plot);
                    y = Math.Max(plot.Top, Math.Min(plot.Bottom, y));

                    if (!first)
                    {
                        g.DrawLine(pen, prev, new PointF(x, y));
                    }
                    prev = new PointF(x, y);
                    first = false;
                }
            }
        }

        /// <summary>绘制参考曲线（虚线 + 半透明灰色）</summary>
        private void DrawReferenceCurve(Graphics g, List<float> data,
            Rectangle plot, float yMin, float yRange, int downSample)
        {
            if (data == null || data.Count < 2)
                return;

            using (var pen = new Pen(RefCurveColor, 1.5f))
            {
                pen.DashStyle = DashStyle.Dash;
                int step = Math.Max(1, downSample);
                PointF prev = PointF.Empty;
                bool first = true;

                // 参考曲线的 X 轴按比例缩放到当前视图
                float refPointCount = data.Count;

                for (int i = 0; i < data.Count; i += step)
                {
                    // 参考曲线 X 坐标按比例映射
                    float normalizedX = (float)i / refPointCount;
                    float dataX = _viewLeft + normalizedX * (_viewRight - _viewLeft);

                    // 跳过不在视口范围内的点
                    if (dataX < _viewLeft - 1 || dataX > _viewRight + 1)
                    {
                        first = true;
                        continue;
                    }

                    float x = DataXToScreen(dataX, plot);
                    float val = data[i];
                    float y = DataYToScreen(val, plot);
                    y = Math.Max(plot.Top, Math.Min(plot.Bottom, y));

                    if (!first)
                    {
                        g.DrawLine(pen, prev, new PointF(x, y));
                    }
                    prev = new PointF(x, y);
                    first = false;
                }
            }
        }

        /// <summary>绘制阈值线（红色虚线 + 标签）</summary>
        private void DrawThresholdLine(Graphics g, Rectangle plot, float value, float yMin, float yRange)
        {
            float y = DataYToScreen(value, plot);
            if (y < plot.Top || y > plot.Bottom)
                return;

            using (var pen = new Pen(_thresholdColor, 1.5f))
            {
                pen.DashStyle = _thresholdLineStyle;
                g.DrawLine(pen, plot.Left, y, plot.Right, y);
            }

            // 绘制阈值标签
            if (!string.IsNullOrEmpty(_thresholdLabel))
            {
                using (var font = new Font("宋体", 8))
                using (var brush = new SolidBrush(_thresholdColor))
                {
                    var size = g.MeasureString(_thresholdLabel, font);
                    g.DrawString(_thresholdLabel, font, brush,
                        plot.Right - size.Width - 4, y - size.Height - 2);
                }
            }
        }

        /// <summary>绘制参考曲线设定时间标签（图表右上角）</summary>
        private void DrawReferenceLabel(Graphics g, string label)
        {
            using (var font = new Font("宋体", 9))
            using (var brush = new SolidBrush(RefLabelColor))
            {
                var size = g.MeasureString(label, font);
                float x = Width - MarginRight - size.Width;
                float y = MarginTop;

                // 如果已经有图例在右上角，把标签放在左上角
                if (x < Width - MarginRight - 100 + 14 + 18)
                {
                    x = MarginLeft;
                    y = MarginTop;
                }

                g.DrawString(label, font, brush, x, y);
            }
        }

        /// <summary>绘制框选半透明蓝色矩形预览</summary>
        private void DrawSelectionRect(Graphics g)
        {
            int x = Math.Min(_dragStart.X, _dragCurrent.X);
            int y = Math.Min(_dragStart.Y, _dragCurrent.Y);
            int w = Math.Abs(_dragCurrent.X - _dragStart.X);
            int h = Math.Abs(_dragCurrent.Y - _dragStart.Y);

            if (w < 3 || h < 3) return;

            using (var fillBrush = new SolidBrush(SelectRectColor))
            using (var borderPen = new Pen(Color.FromArgb(180, 50, 100, 220), 1))
            {
                g.FillRectangle(fillBrush, x, y, w, h);
                g.DrawRectangle(borderPen, x, y, w, h);
            }
        }

        // ================================================================
        // 边界状态渲染
        // ================================================================

        /// <summary>
        /// 绘制空状态 / 加载状态 / 错误状态。
        /// 优先级: 错误 > 加载 > 空状态。
        /// </summary>
        private void DrawEmptyState(Graphics g)
        {
            g.Clear(BgColor);

            string text;
            Color textColor;
            FontStyle fontStyle;

            if (!string.IsNullOrEmpty(_errorMessage))
            {
                // 错误状态：红色文字
                text = "⚠ " + _errorMessage;
                textColor = Color.FromArgb(200, 50, 50);
                fontStyle = FontStyle.Regular;
            }
            else if (_isLoading)
            {
                // 加载状态：灰色 + 动画提示
                text = "⏳ 正在加载数据...";
                textColor = Color.Gray;
                fontStyle = FontStyle.Italic;
            }
            else
            {
                // 空状态：灰色提示
                text = _emptyStateMessage ?? "暂无数据";
                textColor = Color.Gray;
                fontStyle = FontStyle.Regular;
            }

            using (var font = new Font("宋体", 14, fontStyle))
            using (var brush = new SolidBrush(textColor))
            {
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, brush,
                    (Width - size.Width) / 2, (Height - size.Height) / 2);
            }
        }

        private void DrawLegend(Graphics g)
        {
            using (var font = new Font("宋体", 9))
            {
                float legendX = Width - MarginRight - 100;
                float legendY = MarginTop;

                if (_phaseAVisible) DrawLegendItem(g, font, PhaseAColor, "A相", legendX, ref legendY);
                if (_phaseBVisible) DrawLegendItem(g, font, PhaseBColor, "B相", legendX, ref legendY);
                if (_phaseCVisible) DrawLegendItem(g, font, PhaseCColor, "C相", legendX, ref legendY);

                if (_hasReferenceCurve)
                {
                    DrawLegendItem(g, font, RefLabelColor, "参考曲线", legendX, ref legendY);
                }
            }
        }

        private void DrawLegendItem(Graphics g, Font font, Color color, string label,
            float x, ref float y)
        {
            using (var brush = new SolidBrush(color))
            using (var textBrush = new SolidBrush(Color.Black))
            {
                g.FillRectangle(brush, x, y, 14, 10);
                g.DrawString(label, font, textBrush, x + 18, y - 1);
                y += 16;
            }
        }
    }
}
