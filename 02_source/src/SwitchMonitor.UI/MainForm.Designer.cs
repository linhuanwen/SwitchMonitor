namespace SwitchMonitor.UI
{
    partial class MainForm
    {
        /// <summary>
        /// 必需的设计器变量
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 清理所有正在使用的资源
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows 窗体设计器生成的代码

        /// <summary>
        /// 设计器支持所需的方法
        /// </summary>
        private void InitializeComponent()
        {
            this.splitContainer = new System.Windows.Forms.SplitContainer();
            this.sidebarBrowser = new System.Windows.Forms.WebBrowser();
            this.chartBrowser = new System.Windows.Forms.WebBrowser();
            this.statusStrip = new System.Windows.Forms.StatusStrip();
            this.statusLabel = new System.Windows.Forms.ToolStripStatusLabel();
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).BeginInit();
            this.splitContainer.Panel1.SuspendLayout();
            this.splitContainer.Panel2.SuspendLayout();
            this.splitContainer.SuspendLayout();
            this.statusStrip.SuspendLayout();
            this.SuspendLayout();
            //
            // splitContainer
            //
            this.splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer.FixedPanel = System.Windows.Forms.FixedPanel.Panel1;
            this.splitContainer.Location = new System.Drawing.Point(0, 0);
            this.splitContainer.Name = "splitContainer";
            //
            // splitContainer.Panel1
            //
            this.splitContainer.Panel1.Controls.Add(this.sidebarBrowser);
            this.splitContainer.Panel1MinSize = 180;
            //
            // splitContainer.Panel2
            //
            this.splitContainer.Panel2.Controls.Add(this.chartBrowser);
            this.splitContainer.Size = new System.Drawing.Size(1280, 900);
            this.splitContainer.SplitterDistance = 230;
            this.splitContainer.TabIndex = 0;
            //
            // sidebarBrowser
            //
            this.sidebarBrowser.AllowWebBrowserDrop = false;
            this.sidebarBrowser.Dock = System.Windows.Forms.DockStyle.Fill;
            this.sidebarBrowser.Location = new System.Drawing.Point(0, 0);
            this.sidebarBrowser.MinimumSize = new System.Drawing.Size(20, 20);
            this.sidebarBrowser.Name = "sidebarBrowser";
            this.sidebarBrowser.ScrollBarsEnabled = false;
            this.sidebarBrowser.Size = new System.Drawing.Size(230, 900);
            this.sidebarBrowser.TabIndex = 0;
            this.sidebarBrowser.WebBrowserShortcutsEnabled = false;
            this.sidebarBrowser.IsWebBrowserContextMenuEnabled = false;
            //
            // chartBrowser
            //
            this.chartBrowser.AllowWebBrowserDrop = false;
            this.chartBrowser.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chartBrowser.Location = new System.Drawing.Point(0, 0);
            this.chartBrowser.MinimumSize = new System.Drawing.Size(20, 20);
            this.chartBrowser.Name = "chartBrowser";
            this.chartBrowser.ScrollBarsEnabled = false;
            this.chartBrowser.Size = new System.Drawing.Size(1046, 900);
            this.chartBrowser.TabIndex = 0;
            this.chartBrowser.WebBrowserShortcutsEnabled = false;
            this.chartBrowser.IsWebBrowserContextMenuEnabled = false;
            //
            // statusStrip
            //
            this.statusStrip.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                this.statusLabel});
            this.statusStrip.Location = new System.Drawing.Point(0, 900);
            this.statusStrip.Name = "statusStrip";
            this.statusStrip.Size = new System.Drawing.Size(1280, 22);
            this.statusStrip.TabIndex = 1;
            //
            // statusLabel
            //
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(100, 17);
            this.statusLabel.Text = "就绪";
            //
            // notifyIcon
            //
            this.notifyIcon = new System.Windows.Forms.NotifyIcon();
            this.notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            this.notifyIcon.Visible = true;
            this.notifyIcon.Text = "道岔监控系统";
            //
            // MainForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 12F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            this.ClientSize = new System.Drawing.Size(1280, 922);
            this.Controls.Add(this.splitContainer);
            this.Controls.Add(this.statusStrip);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "道岔监控数据查看系统 V3.0";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer)).EndInit();
            this.splitContainer.Panel1.ResumeLayout(false);
            this.splitContainer.Panel2.ResumeLayout(false);
            this.splitContainer.ResumeLayout(false);
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.SplitContainer splitContainer;
        private System.Windows.Forms.WebBrowser sidebarBrowser;
        private System.Windows.Forms.WebBrowser chartBrowser;
        private System.Windows.Forms.StatusStrip statusStrip;
        private System.Windows.Forms.ToolStripStatusLabel statusLabel;
        private System.Windows.Forms.NotifyIcon notifyIcon;
    }
}
