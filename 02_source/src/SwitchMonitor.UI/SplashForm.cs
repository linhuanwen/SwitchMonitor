using System;
using System.Drawing;
using System.Windows.Forms;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 启动加载窗口 — 在数据迁移/索引初始化期间显示，避免用户看到"无响应"。
    /// 运行在独立 STA 线程，通过 BeginInvoke 接收主线程的状态更新。
    /// </summary>
    public class SplashForm : Form
    {
        private readonly Label _lblTitle;
        private readonly Label _lblSubtitle;
        private readonly Label _lblStatus;
        private readonly ProgressBar _progressBar;

        public SplashForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(400, 140);
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.BackColor = Color.FromArgb(26, 26, 46);
            this.ForeColor = Color.FromArgb(224, 224, 224);

            // 标题
            _lblTitle = new Label
            {
                Text = "道岔监测系统",
                Font = new Font("SimSun", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 200, 255),
                AutoSize = true,
                Location = new Point(0, 16)
            };
            CenterLabel(_lblTitle);

            // 副标题
            _lblSubtitle = new Label
            {
                Text = "正在加载数据，请稍候...",
                Font = new Font("SimSun", 10f),
                ForeColor = Color.FromArgb(200, 200, 200),
                AutoSize = true,
                Location = new Point(0, 44)
            };
            CenterLabel(_lblSubtitle);

            // 进度条（不确定模式）
            _progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Size = new Size(300, 22),
                Location = new Point(0, 74)
            };
            CenterControl(_progressBar);

            // 状态文字
            _lblStatus = new Label
            {
                Text = "",
                Font = new Font("SimSun", 9f),
                ForeColor = Color.FromArgb(150, 150, 150),
                AutoSize = true,
                Location = new Point(0, 104)
            };
            CenterLabel(_lblStatus);

            this.Controls.Add(_lblTitle);
            this.Controls.Add(_lblSubtitle);
            this.Controls.Add(_progressBar);
            this.Controls.Add(_lblStatus);
        }

        /// <summary>
        /// 更新底部状态文字（线程安全）。
        /// </summary>
        public void UpdateStatus(string text)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<string>(UpdateStatus), text);
                return;
            }
            _lblStatus.Text = text;
            CenterLabel(_lblStatus);
        }

        private void CenterLabel(Label lbl)
        {
            lbl.Left = (this.ClientSize.Width - lbl.PreferredWidth) / 2;
        }

        private void CenterControl(Control ctrl)
        {
            ctrl.Left = (this.ClientSize.Width - ctrl.Width) / 2;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 强制重绘，避免窗口出现空白
            this.Refresh();
        }
    }
}
