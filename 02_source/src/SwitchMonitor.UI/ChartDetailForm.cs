using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 曲线详情独立窗口 — 全屏单图表，支持自由缩放
    /// </summary>
    public class ChartDetailForm : Form
    {
        private WebBrowser _browser;
        private readonly string _chartDataJson;
        private readonly string _htmlContent;

        /// <summary>
        /// 创建曲线详情窗口
        /// </summary>
        /// <param name="chartDataJson">图表数据 JSON（包含 series、标题、颜色等）</param>
        /// <param name="htmlContent">已注入 JS 库的完整 HTML 内容</param>
        public ChartDetailForm(string chartDataJson, string htmlContent)
        {
            _chartDataJson = chartDataJson;
            _htmlContent = htmlContent;

            // 设置 WebBrowser
            _browser = new WebBrowser();
            _browser.Dock = DockStyle.Fill;
            _browser.ObjectForScripting = new DetailJSBridge(this);
            _browser.ScriptErrorsSuppressed = true;
            _browser.ScrollBarsEnabled = false;
            _browser.IsWebBrowserContextMenuEnabled = false;
            _browser.WebBrowserShortcutsEnabled = false;
            _browser.DocumentCompleted += Browser_DocumentCompleted;
            this.Controls.Add(_browser);

            // 加载 HTML
            if (!string.IsNullOrEmpty(_htmlContent))
            {
                _browser.DocumentText = _htmlContent;
            }
        }

        private void Browser_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            if (_browser.ReadyState == WebBrowserReadyState.Complete)
            {
                // 调用 JS 初始化图表
                try
                {
                    _browser.Document.InvokeScript("initChart", new object[] { _chartDataJson });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ChartDetailForm initChart 错误: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 关闭窗口（由 JS 调用）
        /// </summary>
        public void CloseWindow()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => this.Close()));
            }
            else
            {
                this.Close();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_browser != null)
                {
                    _browser.DocumentCompleted -= Browser_DocumentCompleted;
                    _browser.Dispose();
                    _browser = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 曲线详情窗口的 JS 桥接类
    /// </summary>
    [ComVisible(true)]
    public class DetailJSBridge
    {
        private readonly ChartDetailForm _form;

        public DetailJSBridge(ChartDetailForm form)
        {
            _form = form;
        }

        /// <summary>
        /// HTML: window.external.CloseWindow()
        /// </summary>
        public void CloseWindow()
        {
            _form.CloseWindow();
        }
    }
}
