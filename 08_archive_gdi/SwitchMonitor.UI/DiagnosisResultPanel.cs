using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SwitchMonitor.Common;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 诊断结果面板。显示诊断引擎输出的分级结果列表。
    /// 每条结果：颜色圆点 + 级别标签 + 描述。
    /// 底部显示统计汇总（正常/预警/报警/故障 各数量）。
    /// 点击结果项可触发区域导航事件。
    /// </summary>
    public class DiagnosisResultPanel : Panel
    {
        // ---- 数据 ----
        private List<DiagnosisResult> _results;

        // ---- 布局常量 ----
        private const int ItemHeight = 28;
        private const int LeftPadding = 12;
        private const int DotRadius = 5;
        private const int DotLeft = 14;
        private const int SummaryHeight = 32;
        private const int TitleBarHeight = 26;

        // ---- 颜色 ----
        private static readonly Color NormalColor = Color.FromArgb(34, 139, 34);    // 绿色
        private static readonly Color WarningColor = Color.FromArgb(218, 165, 32);  // 金黄色
        private static readonly Color AlarmColor = Color.FromArgb(220, 20, 60);      // 红色
        private static readonly Color FaultColor = Color.FromArgb(180, 0, 0);        // 深红色
        private static readonly Color BgColor = Color.FromArgb(250, 250, 250);

        // ---- 事件 ----
        /// <summary>用户点击某条诊断结果时触发。参数为被点击结果的索引。</summary>
        public event Action<int> DiagnosisItemClicked;

        public DiagnosisResultPanel()
        {
            this.DoubleBuffered = true;
            this.BackColor = BgColor;
            this.MinimumSize = new Size(200, 100);

            this.MouseClick += OnPanelMouseClick;
            this.MouseMove += OnPanelMouseMove;
            this.Paint += OnPanelPaint;
        }

        /// <summary>
        /// 设置诊断结果并刷新面板。
        /// </summary>
        public void SetResults(List<DiagnosisResult> results)
        {
            _results = results;
            this.Invalidate();
        }

        /// <summary>
        /// 获取当前显示的结果。
        /// </summary>
        public List<DiagnosisResult> GetResults()
        {
            return _results;
        }

        /// <summary>
        /// 清除所有结果。
        /// </summary>
        public void ClearResults()
        {
            _results = null;
            this.Invalidate();
        }

        private void OnPanelPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int y = 0;

            // 标题栏
            using (var titleFont = new Font("宋体", 10f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(Color.FromArgb(60, 60, 60)))
            using (var bgBrush = new SolidBrush(Color.FromArgb(235, 235, 235)))
            {
                g.FillRectangle(bgBrush, 0, y, this.Width, TitleBarHeight);
                g.DrawString("诊断结果", titleFont, titleBrush, LeftPadding, y + 5);
            }
            y += TitleBarHeight;

            // 分隔线
            using (var linePen = new Pen(Color.FromArgb(200, 200, 200)))
            {
                g.DrawLine(linePen, 0, y, this.Width, y);
            }

            if (_results == null || _results.Count == 0)
            {
                // 无数据
                using (var font = new Font("宋体", 9f))
                using (var brush = new SolidBrush(Color.Gray))
                {
                    g.DrawString("(无诊断数据 — 请选择一条动作记录)", font, brush,
                        LeftPadding, y + 10);
                }
                return;
            }

            // 绘制每条结果
            using (var itemFont = new Font("宋体", 9f))
            using (var normalBrush = new SolidBrush(NormalColor))
            using (var warningBrush = new SolidBrush(WarningColor))
            using (var alarmBrush = new SolidBrush(AlarmColor))
            using (var faultBrush = new SolidBrush(FaultColor))
            using (var textBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            using (var bgBrush = new SolidBrush(Color.White))
            {
                for (int i = 0; i < _results.Count; i++)
                {
                    var result = _results[i];
                    int itemY = y + 2;

                    // 交替背景色
                    if (i % 2 == 0)
                    {
                        g.FillRectangle(bgBrush, 0, itemY - 1, this.Width, ItemHeight);
                    }

                    // 色点
                    Brush dotBrush = GetLevelBrush(result.Level, normalBrush, warningBrush, alarmBrush, faultBrush);
                    int dotY = itemY + ItemHeight / 2 - DotRadius;
                    g.FillEllipse(dotBrush, DotLeft, dotY, DotRadius * 2, DotRadius * 2);

                    // 级别标签
                    int textX = DotLeft + DotRadius * 2 + 8;
                    g.DrawString(result.Level, itemFont, dotBrush, textX, itemY + 4);

                    // 描述
                    int descX = textX + 40;
                    string desc = result.Description ?? "";
                    g.DrawString(desc, itemFont, textBrush, descX, itemY + 4);

                    y += ItemHeight;
                }
            }

            // 统计汇总
            y += 4;
            using (var linePen = new Pen(Color.FromArgb(200, 200, 200)))
            {
                g.DrawLine(linePen, 0, y, this.Width, y);
            }

            int normalCount = 0, warningCount = 0, alarmCount = 0, faultCount = 0;
            foreach (var r in _results)
            {
                switch (r.Level)
                {
                    case "正常": normalCount++; break;
                    case "预警": warningCount++; break;
                    case "报警": alarmCount++; break;
                    case "故障": faultCount++; break;
                }
            }

            using (var summaryFont = new Font("宋体", 8.5f))
            using (var summaryBrush = new SolidBrush(Color.FromArgb(80, 80, 80)))
            {
                string summary = string.Format("正常: {0}条  预警: {1}条  报警: {2}条  故障: {3}条",
                    normalCount, warningCount, alarmCount, faultCount);
                g.DrawString(summary, summaryFont, summaryBrush, LeftPadding, y + 6);
            }

            // 调整面板高度
            int neededHeight = y + SummaryHeight;
            if (this.Height != neededHeight && neededHeight > 60)
            {
                this.Height = neededHeight;
            }
        }

        private Brush GetLevelBrush(string level,
            Brush normalBrush, Brush warningBrush, Brush alarmBrush, Brush faultBrush)
        {
            switch (level)
            {
                case "正常": return normalBrush;
                case "预警": return warningBrush;
                case "报警": return alarmBrush;
                case "故障": return faultBrush;
                default: return normalBrush;
            }
        }

        /// <summary>
        /// 鼠标悬停时改变光标为手型（在有结果项的区域）。
        /// </summary>
        private void OnPanelMouseMove(object sender, MouseEventArgs e)
        {
            if (_results == null || _results.Count == 0)
            {
                this.Cursor = Cursors.Default;
                return;
            }

            int itemIndex = GetItemIndexAtY(e.Y);
            if (itemIndex >= 0 && itemIndex < _results.Count)
            {
                this.Cursor = Cursors.Hand;
            }
            else
            {
                this.Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// 点击结果项时触发 DiagnosisItemClicked 事件。
        /// </summary>
        private void OnPanelMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_results == null || _results.Count == 0) return;

            int itemIndex = GetItemIndexAtY(e.Y);
            if (itemIndex >= 0 && itemIndex < _results.Count)
            {
                DiagnosisItemClicked?.Invoke(itemIndex);
            }
        }

        /// <summary>
        /// 根据鼠标 Y 坐标计算命中的结果项索引。
        /// </summary>
        private int GetItemIndexAtY(int mouseY)
        {
            if (_results == null) return -1;

            int y = TitleBarHeight + 1; // 标题栏 + 分隔线
            for (int i = 0; i < _results.Count; i++)
            {
                if (mouseY >= y && mouseY < y + ItemHeight)
                    return i;
                y += ItemHeight;
            }
            return -1;
        }
    }
}
