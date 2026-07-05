using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SwitchMonitor.Data;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 主窗口 — 道岔监控数据查看系统
    /// </summary>
    [ComVisible(true)]
    public partial class MainForm : Form
    {
        private readonly AppConfig _config;
        private readonly IndexManager _indexManager;
        private readonly JavaScriptSerializer _serializer;
        private readonly DataPipeline _pipeline;
        private readonly BackgroundWorker _importWorker;
        private MenuStrip _menuStrip;
        private string _selectedSwitchId;
        private string _selectedDate;
        private long _selectedTimestamp;

        public MainForm(AppConfig config, IndexManager indexManager)
        {
            InitializeComponent();
            _config = config;
            _indexManager = indexManager;
            _serializer = new JavaScriptSerializer();
            _pipeline = new DataPipeline(config, indexManager);

            // 初始化 WebBrowser
            sidebarBrowser.ObjectForScripting = new JSBridge(this);
            chartBrowser.ObjectForScripting = new JSBridge(this);

            // IE8: 禁用脚本错误弹窗
            sidebarBrowser.ScriptErrorsSuppressed = true;
            chartBrowser.ScriptErrorsSuppressed = true;

            // 设置菜单栏
            SetupMenu();

            // 设置后台导入
            _importWorker = new BackgroundWorker();
            _importWorker.WorkerReportsProgress = true;
            _importWorker.WorkerSupportsCancellation = true;
            _importWorker.DoWork += ImportWorker_DoWork;
            _importWorker.ProgressChanged += ImportWorker_ProgressChanged;
            _importWorker.RunWorkerCompleted += ImportWorker_Completed;

            // 加载侧边栏
            LoadSidebar();
            // 加载图表页（空状态）
            LoadChartPage(null, null);
        }

        #region 菜单栏与数据导入

        /// <summary>
        /// 创建菜单栏
        /// </summary>
        private void SetupMenu()
        {
            _menuStrip = new MenuStrip();
            _menuStrip.BackColor = Color.FromArgb(30, 30, 50);
            _menuStrip.ForeColor = Color.FromArgb(200, 200, 200);
            _menuStrip.Dock = DockStyle.Top;

            var dataMenu = new ToolStripMenuItem("数据(&D)");

            var importItem = new ToolStripMenuItem("导入源数据(&I)...", null, OnImportClicked);
            importItem.ShortcutKeys = Keys.Control | Keys.I;
            dataMenu.DropDownItems.Add(importItem);

            dataMenu.DropDownItems.Add(new ToolStripSeparator());

            var refreshItem = new ToolStripMenuItem("刷新列表(&R)", null, OnRefreshClicked);
            refreshItem.ShortcutKeys = Keys.F5;
            dataMenu.DropDownItems.Add(refreshItem);

            _menuStrip.Items.Add(dataMenu);
            this.Controls.Add(_menuStrip);
            this.MainMenuStrip = _menuStrip;
        }

        private void OnImportClicked(object sender, EventArgs e)
        {
            if (_importWorker.IsBusy)
            {
                MessageBox.Show("导入正在进行中，请等待完成。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string sourceDir = _config.DataSourceDir;
            if (!string.IsNullOrEmpty(sourceDir) && !Path.IsPathRooted(sourceDir))
            {
                sourceDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, sourceDir));
            }

            // 检查源目录是否存在
            if (!Directory.Exists(sourceDir))
            {
                var browseResult = MessageBox.Show(
                    "数据源目录不存在:\n" + sourceDir + "\n\n" +
                    "CSV 源数据文件（SwitchCurve(*).csv）应放在此目录中。\n\n" +
                    "是否需要手动指定数据源目录？\n（点击[是]选择文件夹，点击[否]取消）",
                    "数据源未找到", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (browseResult == DialogResult.Yes)
                {
                    using (var dialog = new FolderBrowserDialog())
                    {
                        dialog.Description = "请选择包含 SwitchCurve(*).csv 文件的目录";
                        dialog.ShowNewFolderButton = false;
                        if (dialog.ShowDialog() == DialogResult.OK)
                        {
                            sourceDir = dialog.SelectedPath;
                        }
                        else return;
                    }
                }
                else return;
            }

            var result = MessageBox.Show(
                "将从数据源目录导入 CSV 数据。\n\n" +
                "数据源: " + sourceDir + "\n" +
                "输出目录: parsed_data\\\n\n" +
                "此操作将覆盖已有的 JSON 数据。\n确定继续？",
                "导入确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

            if (result == DialogResult.OK)
            {
                statusLabel.Text = "正在导入...";
                _importWorker.RunWorkerAsync(sourceDir);
            }
        }

        private void OnRefreshClicked(object sender, EventArgs e)
        {
            // 重新初始化索引（重新加载 index.json）
            string parsedDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.ParsedDataDir);
            _indexManager.Initialize();

            // 刷新当前选中的转辙机
            if (!string.IsNullOrEmpty(_selectedSwitchId))
            {
                OnSwitchSelected(_selectedSwitchId);
            }

            statusLabel.Text = "列表已刷新";
        }

        private void ImportWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var worker = sender as BackgroundWorker;
            string customSourceDir = e.Argument as string;

            // 订阅进度
            _pipeline.OnProgress += (msg, pct) =>
            {
                if (worker != null && worker.WorkerReportsProgress)
                    worker.ReportProgress(pct, msg);
            };

            try
            {
                _pipeline.ImportAll(customSourceDir);
            }
            catch (Exception ex)
            {
                e.Result = "导入失败: " + ex.Message;
            }
        }

        private void ImportWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string msg = e.UserState as string;
            if (!string.IsNullOrEmpty(msg))
                statusLabel.Text = msg;
        }

        private void ImportWorker_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                statusLabel.Text = "导入出错: " + e.Error.Message;
                MessageBox.Show("导入过程发生错误:\n" + e.Error.Message,
                    "导入错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (e.Result != null)
            {
                statusLabel.Text = e.Result.ToString();
                MessageBox.Show(e.Result.ToString(), "导入结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 导入成功，刷新索引
            _indexManager.Initialize();

            int count = _pipeline.TotalEventsImported;
            statusLabel.Text = string.Format("导入完成，共 {0} 个动作事件", count);

            MessageBox.Show(string.Format("数据导入完成！\n\n共导入 {0} 个动作事件。\n\n请选择转辙机查看曲线。", count),
                "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);

            // 刷新当前选中的转辙机列表
            if (!string.IsNullOrEmpty(_selectedSwitchId))
            {
                OnSwitchSelected(_selectedSwitchId);
            }
        }

        #endregion

        #region WebBrowser 加载

        /// <summary>
        /// 加载侧边栏 HTML
        /// </summary>
        private void LoadSidebar()
        {
            string html = GetEmbeddedResource("SwitchMonitor.UI.Html.sidebar.html");
            if (!string.IsNullOrEmpty(html))
            {
                html = html.Replace("{{CONFIG_JSON}}", _serializer.Serialize(_config.SwitchGroups));
                sidebarBrowser.DocumentText = html;
            }
        }

        /// <summary>
        /// 加载图表页 HTML（内联注入 JS 库）
        /// </summary>
        private void LoadChartPage(SwitchEvent currentEvent, SwitchEvent prevEvent)
        {
            string html = GetEmbeddedResource("SwitchMonitor.UI.Html.charts.html");
            if (string.IsNullOrEmpty(html))
            {
                html = "<html><body style='background:#1a1a2e;color:#888;font:12px SimSun;text-align:center;padding-top:200px;'>图表组件加载中...</body></html>";
                chartBrowser.DocumentText = html;
                return;
            }

            // 内联注入 jquery.js 和 highcharts.js
            html = InjectEmbeddedScript(html, "SwitchMonitor.UI.Js.jquery.js");
            html = InjectEmbeddedScript(html, "SwitchMonitor.UI.Js.highcharts.js");

            chartBrowser.DocumentText = html;
        }

        /// <summary>
        /// 从嵌入资源读取 HTML 内容
        /// </summary>
        private string GetEmbeddedResource(string resourceName)
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将嵌入的 JS 资源内联注入到 HTML 的 </head> 之前
        /// </summary>
        private string InjectEmbeddedScript(string html, string jsResourceName)
        {
            string js = GetEmbeddedResource(jsResourceName);
            if (string.IsNullOrEmpty(js))
                return html;

            string scriptTag = "<script type=\"text/javascript\">\n" + js + "\n</script>";

            int headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headClose >= 0)
            {
                return html.Insert(headClose, scriptTag);
            }

            // 没有 </head>，注入到 <body> 之前
            int bodyOpen = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyOpen >= 0)
            {
                bodyOpen = html.IndexOf('>', bodyOpen) + 1;
                return html.Insert(bodyOpen, scriptTag);
            }

            return html;
        }

        #endregion

        #region 事件处理（由 JSBridge HTML 调用触发）

        /// <summary>
        /// 用户选择了转辙机
        /// </summary>
        public void OnSwitchSelected(string switchId)
        {
            if (string.IsNullOrEmpty(switchId))
                return;

            _selectedSwitchId = switchId;
            _selectedTimestamp = 0;

            // 获取该转辙机的所有日期列表
            var dates = _indexManager.GetDates(switchId);

            // 通知侧边栏更新日期列表
            string datesJson = _serializer.Serialize(dates);
            InvokeSidebarScript("setDates", datesJson);

            // 默认选中最近日期
            if (dates.Count > 0)
            {
                _selectedDate = dates[0];
                InvokeSidebarScript("setSelectedDate", _selectedDate);
                OnDateSelected(_selectedDate);
            }
            else
            {
                _selectedDate = null;
                InvokeSidebarScript("setTimes", "[]");
                ClearCharts();
            }

            UpdateStatusBar();
        }

        /// <summary>
        /// 用户选择了日期
        /// </summary>
        public void OnDateSelected(string date)
        {
            if (string.IsNullOrEmpty(_selectedSwitchId) || string.IsNullOrEmpty(date))
                return;

            _selectedDate = date;

            // 获取该天的所有时间戳
            var timestamps = _indexManager.GetTimestamps(_selectedSwitchId, date);

            // 通知侧边栏更新时间列表
            string timesJson = _serializer.Serialize(timestamps);
            InvokeSidebarScript("setTimes", timesJson);

            // 自动选中最近时间
            if (timestamps.Count > 0)
            {
                _selectedTimestamp = timestamps[0];
                InvokeSidebarScript("setSelectedTime", _selectedTimestamp.ToString());
                LoadCurveData();
            }
            else
            {
                _selectedTimestamp = 0;
                ClearCharts();
            }

            UpdateStatusBar();
        }

        /// <summary>
        /// 用户选择了时间
        /// </summary>
        public void OnTimeSelected(string timestampStr)
        {
            long timestamp;
            if (!long.TryParse(timestampStr, out timestamp))
                return;

            _selectedTimestamp = timestamp;
            LoadCurveData();
            UpdateStatusBar();
        }

        /// <summary>
        /// 复选框控制系列显隐
        /// </summary>
        public void OnSeriesToggled(string seriesName, bool visible)
        {
            InvokeChartScript("toggleSeries", _serializer.Serialize(new { name = seriesName, visible = visible }));
        }

        #endregion

        #region 数据加载与图表渲染

        /// <summary>
        /// 加载当前选中时间 + 上一时间的曲线数据，推送到图表
        /// </summary>
        private void LoadCurveData()
        {
            if (string.IsNullOrEmpty(_selectedSwitchId) ||
                string.IsNullOrEmpty(_selectedDate) ||
                _selectedTimestamp == 0)
                return;

            // 加载当日数据
            var dayEvents = _indexManager.LoadDayData(_selectedSwitchId, _selectedDate);

            // 找到当前选中事件和上一时间事件
            SwitchEvent currentEvent = null;
            SwitchEvent prevEvent = null;

            for (int i = 0; i < dayEvents.Count; i++)
            {
                if (dayEvents[i].Timestamp == _selectedTimestamp)
                {
                    currentEvent = dayEvents[i];
                    // 取下一项（更早的时间）作为上一动作
                    if (i + 1 < dayEvents.Count)
                        prevEvent = dayEvents[i + 1];
                    break;
                }
            }

            if (currentEvent == null && dayEvents.Count > 0)
            {
                currentEvent = dayEvents[0];
                if (dayEvents.Count > 1)
                    prevEvent = dayEvents[1];
            }

            // 计算动态 X 轴最大值
            int xMax = _config.Ui.XAxisDefaultMax;
            double maxDuration = currentEvent != null ? currentEvent.Duration : 0;
            if (prevEvent != null && prevEvent.Duration > maxDuration)
                maxDuration = prevEvent.Duration;
            if (maxDuration > _config.Ui.XAxisDefaultMax)
                xMax = _config.Ui.XAxisExtendedMax;

            // 构建传给 JS 的数据对象
            var chartData = new
            {
                switchId = _selectedSwitchId,
                switchLabel = GetSwitchLabel(_selectedSwitchId),
                currentEvent = BuildEventJson(currentEvent),
                prevEvent = BuildEventJson(prevEvent),
                thresholdCurrent = _config.AlarmThresholds.Current.Enabled ? _config.AlarmThresholds.Current.Value : (double?)null,
                thresholdPower = _config.AlarmThresholds.Power.Enabled ? _config.AlarmThresholds.Power.Value : (double?)null,
                xMax = xMax,
                colors = _config.ChartColors
            };

            string chartDataJson = _serializer.Serialize(chartData);
            InvokeChartScript("loadChartData", chartDataJson);
        }

        /// <summary>
        /// 将 SwitchEvent 转为给 JS 使用的 JSON 对象
        /// </summary>
        private object BuildEventJson(SwitchEvent evt)
        {
            if (evt == null)
                return null;

            // 构建 [[t, v], ...] 格式的采样数组
            var currentA = new object[evt.SampleCount];
            var currentB = new object[evt.SampleCount];
            var currentC = new object[evt.SampleCount];
            var power = new object[evt.SampleCount];

            for (int i = 0; i < evt.SampleCount; i++)
            {
                double t = Math.Round(i * evt.SampleInterval, 3);
                currentA[i] = new double[] { t, i < evt.CurrentA.Count ? Math.Round(evt.CurrentA[i], 3) : 0 };
                currentB[i] = new double[] { t, i < evt.CurrentB.Count ? Math.Round(evt.CurrentB[i], 3) : 0 };
                currentC[i] = new double[] { t, i < evt.CurrentC.Count ? Math.Round(evt.CurrentC[i], 3) : 0 };
                power[i] = new double[] { t, i < evt.Power.Count ? Math.Round(evt.Power[i], 3) : 0 };
            }

            return new
            {
                timestamp = evt.Timestamp,
                datetime = evt.DateTimeStr,
                direction = evt.Direction,
                duration = evt.Duration,
                currentA = currentA,
                currentB = currentB,
                currentC = currentC,
                power = power
            };
        }

        /// <summary>
        /// 清空图表
        /// </summary>
        private void ClearCharts()
        {
            InvokeChartScript("clearCharts", null);
        }

        /// <summary>
        /// 根据 switchId 获取显示标签
        /// </summary>
        private string GetSwitchLabel(string switchId)
        {
            foreach (var sg in _config.SwitchGroups)
            {
                if (sg.Id == switchId)
                    return sg.Label;
            }
            return switchId;
        }

        #endregion

        #region WebBrowser JS 调用辅助

        /// <summary>
        /// 向侧边栏 WebBrowser 调用 JS 函数
        /// </summary>
        private void InvokeSidebarScript(string functionName, string jsonArg)
        {
            try
            {
                if (sidebarBrowser.Document != null)
                {
                    sidebarBrowser.Document.InvokeScript(functionName, new object[] { jsonArg });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("InvokeSidebarScript 错误: " + ex.Message);
            }
        }

        /// <summary>
        /// 向图表 WebBrowser 调用 JS 函数
        /// </summary>
        private void InvokeChartScript(string functionName, string jsonArg)
        {
            try
            {
                if (chartBrowser.Document != null)
                {
                    chartBrowser.Document.InvokeScript(functionName, new object[] { jsonArg });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("InvokeChartScript 错误: " + ex.Message);
            }
        }

        #endregion

        #region 状态栏

        private void UpdateStatusBar()
        {
            string text = "就绪";
            if (!string.IsNullOrEmpty(_selectedSwitchId))
            {
                text = string.Format("{0} | {1} | {2}",
                    GetSwitchLabel(_selectedSwitchId),
                    _selectedDate ?? "-",
                    _selectedTimestamp > 0 ? UnixToTimeString(_selectedTimestamp) : "-");
            }
            statusLabel.Text = text;
        }

        private string UnixToTimeString(long timestamp)
        {
            DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            dt = dt.AddSeconds(timestamp).ToLocalTime();
            return dt.ToString("HH:mm:ss");
        }

        #endregion
    }

    #region JSBridge — HTML ↔ C# 通信

    /// <summary>
    /// 暴露给 WebBrowser 中 JavaScript 调用的桥接类
    /// </summary>
    [ComVisible(true)]
    public class JSBridge
    {
        private readonly MainForm _form;

        public JSBridge(MainForm form)
        {
            _form = form;
        }

        /// <summary>
        /// HTML: window.external.SelectSwitch("1-1")
        /// </summary>
        public void SelectSwitch(string switchId)
        {
            _form.BeginInvoke(new Action(() => _form.OnSwitchSelected(switchId)));
        }

        /// <summary>
        /// HTML: window.external.SelectDate("2026-06-29")
        /// </summary>
        public void SelectDate(string date)
        {
            _form.BeginInvoke(new Action(() => _form.OnDateSelected(date)));
        }

        /// <summary>
        /// HTML: window.external.SelectTime("1776243701")
        /// </summary>
        public void SelectTime(string timestamp)
        {
            _form.BeginInvoke(new Action(() => _form.OnTimeSelected(timestamp)));
        }

        /// <summary>
        /// HTML: window.external.ToggleSeries("currentA", "true")
        /// </summary>
        public void ToggleSeries(string seriesName, string visible)
        {
            _form.BeginInvoke(new Action(() =>
                _form.OnSeriesToggled(seriesName, visible == "true")));
        }
    }

    #endregion
}
