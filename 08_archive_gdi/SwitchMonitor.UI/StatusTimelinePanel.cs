using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using SwitchMonitor.Common;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 状态时间线面板——在曲线图表下方显示开关量状态变化事件。
    /// 水平轴与曲线图表的 X 轴对齐（相同的时间范围）。
    ///
    /// 布局:
    /// ┌────────────────────────────────────────┐
    /// │  状态: ████░░░░████████░░░░░████████░░░  │
    /// │  点号: [▼ 184 537GH]  [▼ 185 537GB]     │
    /// └────────────────────────────────────────┘
    /// </summary>
    public class StatusTimelinePanel : Panel
    {
        // ---- 颜色定义 ----
        private static readonly Color BgColor = Color.FromArgb(250, 250, 250);
        private static readonly Color BorderColor = Color.FromArgb(200, 200, 200);
        private static readonly Color TextColor = Color.FromArgb(80, 80, 80);
        private static readonly Color NoDataColor = Color.FromArgb(180, 180, 180);

        // 默认 state_byte 颜色映射
        private static readonly Dictionary<int, Color> DefaultStateColors = new Dictionary<int, Color>
        {
            { 0x00, Color.FromArgb(220, 60, 60) },    // 红色 — 落下
            { 0x2F, Color.FromArgb(50, 180, 50) },    // 绿色 — 吸起
            { 0x05, Color.FromArgb(220, 150, 30) },   // 橙色
            { 0x47, Color.FromArgb(50, 100, 220) },   // 蓝色
            { 0x85, Color.FromArgb(150, 50, 200) },   // 紫色
            { 0x46, Color.FromArgb(0, 150, 180) },    // 青色
            { 0x48, Color.FromArgb(200, 130, 0) },    // 棕色
            { 0x4E, Color.FromArgb(180, 180, 40) },   // 黄绿
            { 0xC9, Color.FromArgb(200, 100, 150) },  // 粉红
            { 0xC8, Color.FromArgb(100, 150, 100) },  // 暗绿
            { 0xC6, Color.FromArgb(100, 200, 200) },  // 亮青
            { 0xC2, Color.FromArgb(200, 200, 100) },  // 亮黄
            { 0x80, Color.FromArgb(150, 150, 150) },  // 灰色
        };
        private static readonly Color DefaultEventColor = Color.FromArgb(100, 100, 100);

        // ---- 布局常量 ----
        private const int TimelineTop = 22;     // 事件条区域顶部
        private const int TimelineHeight = 16;  // 事件条高度
        private const int PointBarHeight = 4;   // 每个点号的色条高度
        private const int PointBarSpacing = 2;  // 色条间距
        private const int MarginLeft = 60;      // 与 CurveChartPanel 对齐
        private const int MarginRight = 20;
        private const int MarginBottom = 4;

        // ---- 数据 ----
        private List<StatusEvent> _allEvents;
        private List<int> _selectedPointIds;
        private List<int> _availablePointIds;

        // ---- 时间窗口 ----
        private long _windowStartTime;
        private long _windowEndTime;

        // ---- 视口状态（与曲线图表同步） ----
        private float _viewLeft;   // 可见 X 范围起始（时间比例 0~1）
        private float _viewRight;  // 可见 X 范围结束（时间比例 0~1）

        // ---- 鼠标悬浮 ----
        private StatusEvent _hoveredEvent;
        private Point _mousePos;
        private ToolTip _toolTip;

        // ---- 下拉框 ----
        private ComboBox _pointCombo;

        // ---- 映射配置 ----
        private MappingConfig _mappingConfig;

        /// <summary>当用户选择不同的点号时触发</summary>
        public event Action<List<int>> SelectedPointsChanged;

        /// <summary>当用户鼠标悬浮事件时触发（用于状态栏显示）</summary>
        public event Action<string> HoverInfoChanged;

        public StatusTimelinePanel()
        {
            this.DoubleBuffered = true;
            this.BackColor = BgColor;

            _allEvents = new List<StatusEvent>();
            _selectedPointIds = new List<int>();
            _availablePointIds = new List<int>();

            _toolTip = new ToolTip
            {
                AutoPopDelay = 5000,
                InitialDelay = 300,
                ReshowDelay = 100,
                ShowAlways = true,
            };

            // 创建点号下拉框（必须在设置 Height 之前，否则 OnResize → PositionComboBox 会 NRE）
            _pointCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("宋体", 8f),
                Width = 130,
                Visible = false,
            };
            _pointCombo.SelectedIndexChanged += OnPointComboChanged;
            this.Controls.Add(_pointCombo);

            this.MinimumSize = new Size(0, 60);
            this.Height = 60;

            this.MouseMove += OnMouseMove;
            this.MouseLeave += OnMouseLeave;
        }

        // ================================================================
        // 公开方法
        // ================================================================

        /// <summary>
        /// 设置当前时间窗口的状态事件数据。
        /// </summary>
        /// <param name="events">从 StatusEvents 表查询的事件列表</param>
        /// <param name="windowStartTime">动作开始时间 (Unix timestamp)</param>
        /// <param name="windowEndTime">动作结束时间 (Unix timestamp)</param>
        public void SetEvents(List<StatusEvent> events, long windowStartTime, long windowEndTime)
        {
            _allEvents = events ?? new List<StatusEvent>();
            _windowStartTime = windowStartTime;
            _windowEndTime = windowEndTime;

            // 统计可用点号（按出现频率排序）
            _availablePointIds = _allEvents
                .GroupBy(e => e.PointId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();

            // 默认选择频率最高的前几个点号（最多 8 个）
            if (_availablePointIds.Count > 0)
            {
                _selectedPointIds = _availablePointIds.Take(Math.Min(8, _availablePointIds.Count)).ToList();
            }
            else
            {
                _selectedPointIds.Clear();
            }

            UpdatePointCombo();
            Invalidate();
        }

        /// <summary>
        /// 与曲线图表同步视口（时间比例）。
        /// 当用户缩放/平移曲线图表时调用此方法。
        /// </summary>
        /// <param name="viewLeftFraction">可见 X 范围起始（0~1 比例）</param>
        /// <param name="viewRightFraction">可见 X 范围结束（0~1 比例）</param>
        public void SyncViewport(float viewLeftFraction, float viewRightFraction)
        {
            _viewLeft = Math.Max(0, Math.Min(1, viewLeftFraction));
            _viewRight = Math.Max(0, Math.Min(1, viewRightFraction));
            Invalidate();
        }

        /// <summary>
        /// 清除所有数据
        /// </summary>
        public void Clear()
        {
            _allEvents.Clear();
            _selectedPointIds.Clear();
            _availablePointIds.Clear();
            _viewLeft = 0;
            _viewRight = 1;
            _windowStartTime = 0;
            _windowEndTime = 0;
            UpdatePointCombo();
            Invalidate();
        }

        /// <summary>是否有数据</summary>
        public bool HasData => _allEvents.Count > 0;

        /// <summary>当前选中的点号列表</summary>
        public List<int> SelectedPointIds => new List<int>(_selectedPointIds);

        // ================================================================
        // 布局：调整下拉框位置
        // ================================================================

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PositionComboBox();
        }

        private void PositionComboBox()
        {
            int timelineBottom = TimelineTop + TimelineHeight;
            int pointBarAreaTop = timelineBottom + PointBarSpacing;
            _pointCombo.Location = new Point(MarginLeft, pointBarAreaTop);
        }

        // ================================================================
        // 坐标转换
        // ================================================================

        private Rectangle GetTimelineArea()
        {
            int left = MarginLeft;
            int right = Width - MarginRight;
            return new Rectangle(left, TimelineTop, right - left, TimelineHeight);
        }

        /// <summary>Unix 时间戳 → 屏幕 X</summary>
        private float TimeToScreenX(long timestamp, Rectangle area)
        {
            long totalDuration = _windowEndTime - _windowStartTime;
            if (totalDuration <= 0) totalDuration = 1;

            long visibleStart = _windowStartTime + (long)(_viewLeft * totalDuration);
            long visibleEnd = _windowStartTime + (long)(_viewRight * totalDuration);
            long visibleDuration = visibleEnd - visibleStart;
            if (visibleDuration <= 0) visibleDuration = 1;

            float fraction = (float)(timestamp - visibleStart) / visibleDuration;
            return area.Left + fraction * area.Width;
        }

        // ================================================================
        // 绘制
        // ================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var area = GetTimelineArea();
            if (area.Width <= 0) return;

            // 背景
            using (var bgBrush = new SolidBrush(BgColor))
            {
                g.FillRectangle(bgBrush, ClientRectangle);
            }

            // 顶部分隔线
            using (var borderPen = new Pen(BorderColor, 1))
            {
                g.DrawLine(borderPen, area.Left, TimelineTop - 2,
                    area.Right, TimelineTop - 2);
            }

            // 标签
            using (var font = new Font("宋体", 8f))
            using (var textBrush = new SolidBrush(TextColor))
            {
                g.DrawString("状态:", font, textBrush, 4, TimelineTop + 1);
            }

            if (_allEvents.Count == 0 || _selectedPointIds.Count == 0 ||
                _windowStartTime >= _windowEndTime)
            {
                DrawNoData(g, area);
                return;
            }

            // 筛选选中点号的事件
            var selectedSet = new HashSet<int>(_selectedPointIds);
            var filteredEvents = _allEvents
                .Where(ev => selectedSet.Contains(ev.PointId))
                .OrderBy(ev => ev.Timestamp)
                .ToList();

            if (filteredEvents.Count == 0)
            {
                DrawNoData(g, area);
                return;
            }

            // 为每个选中的点号分配一行（垂直堆叠）
            int pointCount = _selectedPointIds.Count;
            int barHeight = Math.Max(2, (TimelineHeight - (pointCount - 1) * PointBarSpacing) / pointCount);

            for (int pi = 0; pi < pointCount; pi++)
            {
                int pointId = _selectedPointIds[pi];
                int yOffset = TimelineTop + pi * (barHeight + PointBarSpacing);

                var pointEvents = filteredEvents
                    .Where(ev => ev.PointId == pointId)
                    .ToList();

                if (pointEvents.Count == 0) continue;

                // 绘制该点号的事件条
                DrawPointEvents(g, pointEvents, area, yOffset, barHeight);
            }

            // 绘制悬浮提示的高亮
            if (_hoveredEvent != null)
            {
                float hx = TimeToScreenX(_hoveredEvent.Timestamp, area);
                using (var highlightPen = new Pen(Color.Black, 2))
                {
                    g.DrawLine(highlightPen, hx, TimelineTop, hx, TimelineTop + TimelineHeight);
                }
            }
        }

        private void DrawPointEvents(Graphics g, List<StatusEvent> events,
            Rectangle area, int yOffset, int barHeight)
        {
            if (events.Count == 0) return;

            // 方案：每个事件绘制一条竖线标记
            // 在密集区域合并绘制
            float lastX = float.MinValue;
            const float minSpacing = 2f;

            foreach (var evt in events)
            {
                float x = TimeToScreenX(evt.Timestamp, area);

                // 跳过屏幕外的点
                if (x < area.Left - 5 || x > area.Right + 5)
                    continue;

                // 如果过于密集，跳过（由后面的点覆盖）
                if (x - lastX < minSpacing)
                    continue;

                lastX = x;

                Color color = GetStateColor(evt.StateByte);
                using (var pen = new Pen(color, 1.5f))
                {
                    g.DrawLine(pen, x, yOffset, x, yOffset + barHeight);
                }

                // 在事件点画一个小菱形标记
                int markSize = 2;
                using (var markBrush = new SolidBrush(color))
                {
                    g.FillRectangle(markBrush,
                        x - markSize, yOffset + barHeight / 2 - markSize,
                        markSize * 2, markSize * 2);
                }
            }
        }

        private void DrawNoData(Graphics g, Rectangle area)
        {
            using (var font = new Font("宋体", 8f))
            using (var brush = new SolidBrush(NoDataColor))
            {
                string text = "无开关量数据";
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, brush,
                    area.Left + (area.Width - size.Width) / 2,
                    TimelineTop + (TimelineHeight - size.Height) / 2);
            }
        }

        // ================================================================
        // 颜色映射
        // ================================================================

        private Color GetStateColor(int stateByte)
        {
            if (DefaultStateColors.TryGetValue(stateByte, out Color color))
                return color;
            return DefaultEventColor;
        }

        // ================================================================
        // 点号选择
        // ================================================================

        private void UpdatePointCombo()
        {
            _pointCombo.Items.Clear();
            foreach (int ptId in _availablePointIds)
            {
                string label = GetPointLabel(ptId);
                _pointCombo.Items.Add(label);
            }

            if (_availablePointIds.Count > 0)
            {
                _pointCombo.Visible = true;
                _pointCombo.SelectedIndex = _availablePointIds.IndexOf(_selectedPointIds.FirstOrDefault());
                if (_pointCombo.SelectedIndex < 0)
                    _pointCombo.SelectedIndex = 0;
            }
            else
            {
                _pointCombo.Visible = false;
            }
        }

        /// <summary>
        /// 获取点号的显示标签（优先使用映射配置）。
        /// </summary>
        private string GetPointLabel(int ptId)
        {
            if (_mappingConfig != null)
                return _mappingConfig.GetPointDisplayLabel(ptId);
            return string.Format("点号 {0}", ptId);
        }

        /// <summary>
        /// 刷新点号标签（由 MainForm 在热加载配置后调用）。
        /// </summary>
        public void RefreshPointLabels(MappingConfig mappingConfig)
        {
            _mappingConfig = mappingConfig;
            UpdatePointCombo();
            Invalidate();
        }

        private void OnPointComboChanged(object sender, EventArgs e)
        {
            if (_pointCombo.SelectedIndex < 0 ||
                _pointCombo.SelectedIndex >= _availablePointIds.Count)
                return;

            int selectedPoint = _availablePointIds[_pointCombo.SelectedIndex];

            // Ctrl+点击: 切换（toggle）该点号
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (_selectedPointIds.Contains(selectedPoint))
                {
                    if (_selectedPointIds.Count > 1)
                        _selectedPointIds.Remove(selectedPoint);
                }
                else
                {
                    _selectedPointIds.Add(selectedPoint);
                }
            }
            else
            {
                // 单选模式：只显示选中的点号
                _selectedPointIds = new List<int> { selectedPoint };
            }

            Invalidate();
            SelectedPointsChanged?.Invoke(_selectedPointIds);
        }

        // ================================================================
        // 鼠标交互
        // ================================================================

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            _mousePos = e.Location;

            if (_allEvents.Count == 0 || _selectedPointIds.Count == 0)
            {
                _hoveredEvent = null;
                return;
            }

            var area = GetTimelineArea();
            if (!area.Contains(e.Location))
            {
                _hoveredEvent = null;
                Invalidate();
                return;
            }

            // 找到最近的悬浮事件
            var selectedSet = new HashSet<int>(_selectedPointIds);
            var candidates = _allEvents
                .Where(ev => selectedSet.Contains(ev.PointId))
                .ToList();

            StatusEvent closest = null;
            float closestDist = 10f; // 最大命中距离（像素）

            foreach (var evt in candidates)
            {
                float sx = TimeToScreenX(evt.Timestamp, area);
                float dist = Math.Abs(sx - e.X);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = evt;
                }
            }

            if (closest != _hoveredEvent)
            {
                _hoveredEvent = closest;
                Invalidate();

                if (_hoveredEvent != null)
                {
                    string info = string.Format("时间: {0:yyyy-MM-dd HH:mm:ss} / 点号: {1} / 状态: 0x{2:X2}",
                        DateTimeHelper.FromUnixTimestamp(_hoveredEvent.Timestamp),
                        _hoveredEvent.PointId,
                        _hoveredEvent.StateByte);
                    HoverInfoChanged?.Invoke(info);
                }
                else
                {
                    HoverInfoChanged?.Invoke(null);
                }
            }
        }

        private void OnMouseLeave(object sender, EventArgs e)
        {
            _hoveredEvent = null;
            HoverInfoChanged?.Invoke(null);
            Invalidate();
        }
    }
}
