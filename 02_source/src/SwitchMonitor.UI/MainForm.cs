using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;
using SwitchMonitor.Network;
using SwitchMonitor.Storage;

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
        private ToolStripButton _btnExportPng;
        private string _selectedSwitchId;
        private string _selectedDate;
        private long _selectedTimestamp;
        private DiagnosisEngine _diagnosisEngine;
        private SiteConfig _selectedSite;
        private JSBridge _jsBridge;  // 持有引用，DocumentText 后需重新绑定
        private volatile bool _isSwitchingSite;  // 防止并发的站点切换

        // N01-4: 网络层集成
        private NetworkConfig _networkConfig;
        private StationMonitor _stationMonitor;
        private DataCatcher _dataCatcher;
        private CatchupState _catchupState;

        public MainForm(AppConfig config, IndexManager indexManager)
        {
            InitializeComponent();
            _config = config;

            // N01-4: 窗口关闭时清理网络资源
            this.FormClosing += (s, e) =>
            {
                try
                {
                    if (_stationMonitor != null) { _stationMonitor.Stop(); _stationMonitor.Dispose(); }
                    if (_dataCatcher != null) { _dataCatcher.Dispose(); }
                    if (_catchupState != null) _catchupState.Save();
                    if (notifyIcon != null) { notifyIcon.Visible = false; notifyIcon.Dispose(); }
                }
                catch { }
            };
            _indexManager = indexManager;
            _serializer = new JavaScriptSerializer();
            _pipeline = new DataPipeline(config, indexManager);

            // ── 初始化当前站点 ──
            _selectedSite = FindSiteById(config.SelectedSiteId);

            // 同步全局配置的 DataSourceDir / SwitchGroups（DataPipeline 使用时才能取到正确的组）
            if (_selectedSite != null)
            {
                if (!string.IsNullOrEmpty(_selectedSite.DataSourceDir))
                    _config.DataSourceDir = _selectedSite.DataSourceDir;
                if (_selectedSite.SwitchGroups != null && _selectedSite.SwitchGroups.Count > 0)
                    _config.SwitchGroups = _selectedSite.SwitchGroups;
            }

            // ── D4: 装配诊断管道 ──
            try
            {
                _pipeline.DiagnoseHook = DiagnosisRunner.CreateHook(config.Diagnosis, _indexManager.ParsedDataDir);
                _diagnosisEngine = DiagnosisRunner.LastEngine;
                if (_pipeline.DiagnoseHook != null)
                    Logger.Info("诊断引擎已装配");
                else
                    Logger.Info("诊断已禁用 (diagnosis.enabled=false)");
            }
            catch (Exception ex)
            {
                Logger.Error("诊断引擎初始化失败，诊断已禁用", ex);
            }

            // 初始化 WebBrowser
            _jsBridge = new JSBridge(this);
            sidebarBrowser.ObjectForScripting = _jsBridge;
            chartBrowser.ObjectForScripting = _jsBridge;

            // 每次 DocumentText 导航完成后重新绑定 ObjectForScripting（COM 绑定可能在导航中丢失）
            sidebarBrowser.DocumentCompleted += (s, e) =>
            {
                sidebarBrowser.ObjectForScripting = _jsBridge;
            };
            chartBrowser.DocumentCompleted += (s, e) =>
            {
                chartBrowser.ObjectForScripting = _jsBridge;
            };

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

            // N01-4: 启动站机监控和补拉器
            InitializeNetwork();

            // 记录启动日志
            Logger.Info("SwitchMonitor 启动");
            Logger.Info("配置路径: " + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
            Logger.Info("数据目录: " + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.ParsedDataDir));
            Logger.Info("转辙机数量: " + config.SwitchGroups.Count);
        }

        #region N01-4 网络集成

        /// <summary>
        /// 初始化站机监控（探测）和补拉器（自动/手动）。
        /// </summary>
        private void InitializeNetwork()
        {
            try
            {
                // 构建 NetworkConfig
                _networkConfig = BuildNetworkConfig();

                // 没有配置远程站点则不启动网络层
                if (_networkConfig.Stations == null || _networkConfig.Stations.Count == 0)
                {
                    Logger.Info("未配置远程站点，网络监控未启动");
                    return;
                }

                // CatchupState 持久化文件
                string statePath = Path.Combine(_indexManager.ParsedDataDir, "catchup_state.json");
                _catchupState = CatchupState.Load(statePath);

                // 站机监控（定期探测）
                _stationMonitor = new StationMonitor(_networkConfig, _catchupState);
                _stationMonitor.StationStateChanged += OnStationStateChanged;
                _stationMonitor.Start();
                Logger.Info(string.Format("站机监控已启动: {0} 个站点, 探测间隔 {1}ms",
                    _networkConfig.Stations.Count, _networkConfig.ProbeIntervalMs));

                // 补拉器
                _dataCatcher = new DataCatcher(_networkConfig, _catchupState);
                _dataCatcher.ProgressChanged += OnCatchupProgressChanged;

                // 首次推送状态到侧边栏
                PushStationStatuses();
            }
            catch (Exception ex)
            {
                Logger.Error("网络层初始化失败，离线功能不可用", ex);
            }
        }

        /// <summary>
        /// 从 AppConfig 构建 NetworkConfig。
        /// </summary>
        private NetworkConfig BuildNetworkConfig()
        {
            var nc = new NetworkConfig
            {
                ListenPort = _config.ListenPort,
                ParsedDataDir = _config.ParsedDataDir,
                ProbeIntervalMs = 120000,  // 2 分钟
                HttpTimeoutMs = 10000,     // 10 秒
                OfflineThreshold = 2
            };

            // 解析站点列表
            var sites = GetEffectiveSites();
            foreach (var site in sites)
            {
                // 只有配置了 Ip 的站点才参与网络通信
                if (string.IsNullOrEmpty(site.Ip))
                    continue;

                nc.Stations.Add(new StationInfo
                {
                    Id = site.Id,
                    Name = site.Name,
                    Ip = site.Ip,
                    Port = site.Port > 0 ? site.Port : 9000,
                    DbPath = ResolveStationDbPath(site)
                });
            }

            return nc;
        }

        private string ResolveStationDbPath(SiteConfig site)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string parsedDir = site.ParsedDataDir ?? _config.ParsedDataDir;
            if (!Path.IsPathRooted(parsedDir))
                parsedDir = Path.Combine(baseDir, parsedDir);
            return Path.Combine(parsedDir, site.Id + ".db");
        }

        /// <summary>
        /// 站点状态变更处理：更新侧边栏指示灯 + 弹气泡告警。
        /// </summary>
        private void OnStationStateChanged(object sender, StationStateChangedEventArgs e)
        {
            // 更新侧边栏状态
            this.BeginInvoke(new Action(() =>
            {
                PushStationStatuses();

                // 离线告警：弹气泡（仅当变为离线时）
                if (e.NewStatus == StationStatus.Offline)
                {
                    string title = e.StationName + " 通信中断";
                    string text = string.Format("{0}站（{1}）连续探测失败，请检查网络连接。",
                        e.StationName, e.StationId);
                    ShowBalloonTip(title, text, ToolTipIcon.Warning);

                    Logger.Warning(string.Format("站点离线: {0} ({1})", e.StationName, e.StationId));
                }
                else if (e.NewStatus == StationStatus.Online && e.OldStatus == StationStatus.Offline)
                {
                    // 离线恢复 → 自动补拉由 DataCatcher 监听完成
                    string title = e.StationName + " 已恢复";
                    string text = string.Format("{0}站通信已恢复，正在自动补拉数据...",
                        e.StationName);
                    ShowBalloonTip(title, text, ToolTipIcon.Info);

                    Logger.Info(string.Format("站点恢复: {0} ({1}) → 触发自动补拉", e.StationName, e.StationId));
                }
            }));
        }

        /// <summary>
        /// 补拉进度事件处理：更新状态栏文本。
        /// </summary>
        private void OnCatchupProgressChanged(object sender, CatchupProgressEventArgs e)
        {
            this.BeginInvoke(new Action(() =>
            {
                if (e.IsError)
                {
                    statusLabel.Text = string.Format("补拉 {0} 失败: {1}", e.StationName, e.ErrorMessage);
                    Logger.Error(string.Format("补拉失败 {0}: {1}", e.StationName, e.ErrorMessage));
                    // 恢复侧边栏按钮
                    InvokeSidebarScript("onCatchupComplete", "\"" + e.StationId + "\"");
                    return;
                }

                if (e.IsComplete)
                {
                    statusLabel.Text = string.Format("补拉 {0} 完成，共 {1} 条",
                        e.StationName, e.ReceivedCount);
                    Logger.Info(string.Format("补拉完成 {0}: {1} 条", e.StationName, e.ReceivedCount));

                    // 刷新 IndexManager 索引缓存
                    try { _indexManager.Initialize(); } catch { }

                    // 恢复侧边栏按钮
                    InvokeSidebarScript("onCatchupComplete", "\"" + e.StationId + "\"");

                    // 刷新当前转辙机数据
                    if (!string.IsNullOrEmpty(_selectedSwitchId))
                    {
                        try { OnSwitchSelected(_selectedSwitchId); } catch { }
                    }
                }
                else
                {
                    statusLabel.Text = string.Format("正在补拉 {0}…已拉 {1} 条",
                        e.StationName, e.ReceivedCount);
                }
            }));
        }

        /// <summary>
        /// 手动触发补拉（由侧边栏"补拉"按钮调用）。
        /// </summary>
        public void CatchupData(string siteId)
        {
            if (_dataCatcher == null || _networkConfig == null)
            {
                MessageBox.Show("网络功能未初始化。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var station = _networkConfig.Stations.Find(s => s.Id == siteId);
            if (station == null)
            {
                MessageBox.Show("未找到站点: " + siteId, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            statusLabel.Text = string.Format("开始补拉 {0}...", station.Name);
            _dataCatcher.CatchupAsync(station);
        }

        /// <summary>
        /// 将所有站点状态推送到侧边栏 JS。
        /// </summary>
        private void PushStationStatuses()
        {
            if (_stationMonitor == null) return;

            var statuses = _stationMonitor.GetAllStatuses();
            var dict = new Dictionary<string, string>();
            foreach (var kv in statuses)
            {
                dict[kv.Key] = kv.Value.ToString().ToLower();
            }

            // 补全所有已配置站点的状态（未探测到的标记为 unknown）
            var sites = GetEffectiveSites();
            foreach (var site in sites)
            {
                if (!dict.ContainsKey(site.Id))
                    dict[site.Id] = "unknown";
            }

            string json = _serializer.Serialize(dict);
            InvokeSidebarScript("updateSiteStatuses", json);
        }

        /// <summary>
        /// 气泡提示。XP 兼容。
        /// </summary>
        private void ShowBalloonTip(string title, string text, ToolTipIcon icon)
        {
            try
            {
                if (notifyIcon != null)
                {
                    notifyIcon.BalloonTipTitle = title;
                    notifyIcon.BalloonTipText = text;
                    notifyIcon.BalloonTipIcon = icon;
                    notifyIcon.ShowBalloonTip(3000); // 3 秒
                }
            }
            catch { /* 气泡失败不阻塞 */ }
        }

        /// <summary>
        /// 工具 → 清理历史数据。
        /// </summary>
        public void OnCleanupClicked(object sender, EventArgs e)
        {
            try
            {
                using (var dialog = new CleanupDialog(_config, _indexManager))
                {
                    dialog.ShowDialog(this);

                    // 如果清理完成，刷新数据
                    if (dialog.DialogResult == DialogResult.OK)
                    {
                        _indexManager.Initialize();
                        if (!string.IsNullOrEmpty(_selectedSwitchId))
                        {
                            OnSwitchSelected(_selectedSwitchId);
                        }
                        statusLabel.Text = "数据清理完成";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开清理对话框失败: " + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private List<SiteConfig> GetEffectiveSites()
        {
            if (_config.Role == "central" && _config.Stations != null && _config.Stations.Count > 0)
                return _config.Stations;

            if (_config.TeamStations != null && _config.TeamStations.Count > 0)
                return _config.TeamStations;

            if (_config.Sites != null && _config.Sites.Count > 0)
                return _config.Sites;

            return new List<SiteConfig>();
        }

        #endregion

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

            // === 工具菜单 ===
            var toolMenu = new ToolStripMenuItem("工具(&T)");

            var diagParamItem = new ToolStripMenuItem("诊断参数设置(&P)...", null, OnDiagParamClicked);
            toolMenu.DropDownItems.Add(diagParamItem);

            var baselineItem = new ToolStripMenuItem("设定基准曲线(&B)...", null, OnBaselineSettingClicked);
            toolMenu.DropDownItems.Add(baselineItem);

            var rerunDiagItem = new ToolStripMenuItem("重新诊断当前数据(&D)", null, OnRerunDiagClicked);
            toolMenu.DropDownItems.Add(rerunDiagItem);

            toolMenu.DropDownItems.Add(new ToolStripSeparator());

            var viewLogItem = new ToolStripMenuItem("查看日志(&L)...", null, OnViewLogClicked);
            viewLogItem.ShortcutKeys = Keys.Control | Keys.L;
            toolMenu.DropDownItems.Add(viewLogItem);

            var openLogDirItem = new ToolStripMenuItem("打开日志目录(&O)...", null, OnOpenLogDirClicked);
            toolMenu.DropDownItems.Add(openLogDirItem);

            toolMenu.DropDownItems.Add(new ToolStripSeparator());

            // N01-4: 清理历史数据（仅总终端显示）
            if (_config.Role == "central")
            {
                var cleanupItem = new ToolStripMenuItem("清理历史数据(&C)...", null, OnCleanupClicked);
                toolMenu.DropDownItems.Add(cleanupItem);
            }

            _menuStrip.Items.Add(toolMenu);

            // 导出按钮（右对齐，与菜单同行）
            _btnExportPng = new ToolStripButton("导出图片", null, OnExportPngClick, "btnExportPng");
            _btnExportPng.Enabled = false;
            _btnExportPng.ToolTipText = "将当前图表导出为 PNG 图片";
            _btnExportPng.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _btnExportPng.Image = SystemIcons.Application.ToBitmap();
            _btnExportPng.Alignment = ToolStripItemAlignment.Right;
            _menuStrip.Items.Add(_btnExportPng);

            this.Controls.Add(_menuStrip);
            this.MainMenuStrip = _menuStrip;
        }

        /// <summary>
        /// 启用/禁用导出按钮
        /// </summary>
        private void SetExportButtonEnabled(bool enabled)
        {
            if (_btnExportPng != null)
            {
                // 需要在 UI 线程上调用
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => _btnExportPng.Enabled = enabled));
                }
                else
                {
                    _btnExportPng.Enabled = enabled;
                }
            }
        }

        /// <summary>
        /// 导出图表为 PNG 图片
        /// </summary>
        private void OnExportPngClick(object sender, EventArgs e)
        {
            try
            {
                // 生成默认文件名
                string defaultName = GenerateExportFileName();
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = "导出图表为 PNG 图片";
                    sfd.Filter = "PNG 图片 (*.png)|*.png";
                    sfd.DefaultExt = "png";
                    sfd.FileName = defaultName;
                    sfd.OverwritePrompt = true;

                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        ExportWebBrowserToPng(sfd.FileName);
                        statusLabel.Text = "图片已导出: " + Path.GetFileName(sfd.FileName);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("导出图片失败", ex);
                MessageBox.Show("导出图片失败:\n" + ex.Message, "导出错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 生成默认导出文件名
        /// </summary>
        private string GenerateExportFileName()
        {
            string switchPart = string.IsNullOrEmpty(_selectedSwitchId) ? "Unknown" : _selectedSwitchId;
            // 清理文件名中不合法的字符
            foreach (var c in Path.GetInvalidFileNameChars())
                switchPart = switchPart.Replace(c, '_');

            string dateStr = string.IsNullOrEmpty(_selectedDate) ? "" : _selectedDate.Replace("-", "");
            string timeStr = DateTime.Now.ToString("HHmmss");

            return string.Format("{0}_{1}_{2}_曲线.png", switchPart, dateStr, timeStr);
        }

        /// <summary>
        /// 将 WebBrowser 图表内容导出为 PNG 文件
        /// 使用 IViewObject COM 接口捕获 WebBrowser 渲染内容
        /// </summary>
        private void ExportWebBrowserToPng(string filePath)
        {
            int width = chartBrowser.ClientSize.Width;
            int height = chartBrowser.ClientSize.Height;

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("图表区域尺寸无效");

            using (var bmp = new Bitmap(width, height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // 首先尝试通过 ActiveX 实例的 IViewObject 捕获
                        bool captured = false;
                        try
                        {
                            var viewObj = chartBrowser.ActiveXInstance as IViewObject;
                            if (viewObj != null)
                            {
                                var rc = new COMRECT { left = 0, top = 0, right = width, bottom = height };
                                int hr = viewObj.Draw(1, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    hdc, ref rc, IntPtr.Zero, IntPtr.Zero, 0);
                                captured = (hr == 0);  // S_OK
                            }
                        }
                        catch { }

                        // 如果 IViewObject 失败，回退到屏幕截图
                        if (!captured)
                        {
                            Point screenPos = chartBrowser.PointToScreen(Point.Empty);
                            g.CopyFromScreen(screenPos.X, screenPos.Y, 0, 0,
                                new Size(width, height), CopyPixelOperation.SourceCopy);
                        }
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
                bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>
        /// 根据 siteId 查找站点配置，找不到则返回 null
        /// </summary>
        private SiteConfig FindSiteById(string siteId)
        {
            if (_config.Sites == null || string.IsNullOrEmpty(siteId))
                return null;
            foreach (var site in _config.Sites)
            {
                if (site.Id == siteId)
                    return site;
            }
            return null;
        }

        /// <summary>
        /// 获取当前站点的转辙机组（站点无覆盖时回退到全局 SwitchGroups）
        /// </summary>
        private List<SwitchGroup> GetCurrentSwitchGroups()
        {
            if (_selectedSite != null && _selectedSite.SwitchGroups != null && _selectedSite.SwitchGroups.Count > 0)
                return _selectedSite.SwitchGroups;
            return _config.SwitchGroups;
        }

        /// <summary>
        /// 侧边栏站点下拉框切换 → 切换数据目录、刷新转辙机列表
        /// </summary>
        public void OnSiteSelected(string siteId)
        {
            if (string.IsNullOrEmpty(siteId))
                return;

            var newSite = FindSiteById(siteId);
            if (newSite == null)
                return;

            if (_selectedSite != null && _selectedSite.Id == newSite.Id)
                return; // 未变化

            // 防止并发切换
            if (_isSwitchingSite)
                return;

            _selectedSite = newSite;
            _config.SelectedSiteId = newSite.Id;

            // 解析数据目录路径
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string newParsedDir = newSite.ParsedDataDir ?? _config.ParsedDataDir;
            if (!Path.IsPathRooted(newParsedDir))
                newParsedDir = Path.Combine(baseDir, newParsedDir);

            // 更新全局数据源目录和转辙机组（轻量操作，同步执行）
            if (!string.IsNullOrEmpty(newSite.DataSourceDir))
                _config.DataSourceDir = newSite.DataSourceDir;
            if (newSite.SwitchGroups != null && newSite.SwitchGroups.Count > 0)
                _config.SwitchGroups = newSite.SwitchGroups;

            // 持久化选择（轻量操作）
            try { ConfigManager.SaveConfig(Path.Combine(baseDir, "config.json")); }
            catch { /* 保存失败不阻塞 */ }

            // 清空当前选择和图表（UI 操作，同步）
            _selectedSwitchId = null;
            _selectedDate = null;
            _selectedTimestamp = 0;
            ClearCharts();
            statusLabel.Text = "正在切换到 " + newSite.Name + "...";

            _isSwitchingSite = true;
            var siteName = newSite.Name;  // 捕获给回调用

            // 后台线程执行耗时操作：切换数据目录 + 迁移 + 重建索引 + 重载基线
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _indexManager.ChangeDataDir(newParsedDir);

                    // 重新加载新站点的基线和标准曲线
                    if (_diagnosisEngine != null)
                    {
                        try
                        {
                            _diagnosisEngine.ReloadBaselines(newParsedDir);
                            Logger.Info("已重新加载站点基线: " + newParsedDir);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning("重载站点基线失败: " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("站点切换后台操作失败: " + ex.Message);
                }

                // 回到 UI 线程刷新界面
                this.BeginInvoke(new Action(() =>
                {
                    LoadSidebar();
                    statusLabel.Text = "已切换到: " + siteName;
                    Logger.Info("切换站点: " + siteName + " | 数据目录: " + newParsedDir);
                    _isSwitchingSite = false;
                }));
            });
        }

        private void OnImportClicked(object sender, EventArgs e)
        {
            if (_importWorker.IsBusy)
            {
                MessageBox.Show("导入正在进行中，请等待完成。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 优先使用站点专属数据源目录，回退到全局配置
            string sourceDir = _selectedSite != null && !string.IsNullOrEmpty(_selectedSite.DataSourceDir)
                ? _selectedSite.DataSourceDir
                : _config.DataSourceDir;
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

        private void OnViewLogClicked(object sender, EventArgs e)
        {
            try
            {
                string logPath = Logger.TodayLogPath;
                if (File.Exists(logPath))
                {
                    Process.Start("notepad.exe", logPath);
                }
                else
                {
                    MessageBox.Show("今天的日志文件尚未生成。\n\n日志路径: " + logPath,
                        "查看日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开日志失败: " + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenLogDirClicked(object sender, EventArgs e)
        {
            try
            {
                string logDir = Logger.LogDir;
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);
                Process.Start("explorer.exe", logDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开日志目录失败: " + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 工具 → 诊断参数设置 → 弹出 DiagParamForm 对话框
        /// </summary>
        private void OnDiagParamClicked(object sender, EventArgs e)
        {
            try
            {
                using (var form = new DiagParamForm(_config, _indexManager))
                {
                    DialogResult result = form.ShowDialog(this);
                    if (result == DialogResult.OK)
                    {
                        // 保存 + 重跑诊断
                        statusLabel.Text = "诊断参数已保存，正在重跑诊断...";
                        RunDiagnosisRerun();
                    }
                    else if (result == DialogResult.Yes)
                    {
                        // 仅保存，刷新图表阈值线
                        statusLabel.Text = "诊断参数已保存";
                        // 实时刷新图表阈值线
                        string thresholdJson = _serializer.Serialize(new
                        {
                            current = _config.AlarmThresholds.Current.Enabled ? _config.AlarmThresholds.Current.Value : (double?)null,
                            power = _config.AlarmThresholds.Power.Enabled ? _config.AlarmThresholds.Power.Value : (double?)null
                        });
                        InvokeChartScript("updateThreshold", thresholdJson);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开诊断参数设置失败: " + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 工具 → 设定基准曲线 → 弹出方向提示 → 转辙机选择 → 触发基线重建
        /// </summary>
        private void OnBaselineSettingClicked(object sender, EventArgs e)
        {
            string message =
                "基准曲线按动作方向分为两类：\n\n" +
                "  ● 定位→反位：道岔从定位扳向反位的基准曲线\n" +
                "  ● 反位→定位：道岔从反位扳向定位的基准曲线\n\n" +
                "系统会根据每次动作的方向自动选用对应的基准曲线进行诊断。\n\n" +
                "点击「确定」后将弹出转辙机选择框，可只重建选定转辙机的基线。\n" +
                "此操作可能需要数分钟，完成后将自动刷新当前视图。";

            var result = MessageBox.Show(message, "设定基准曲线",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

            if (result != DialogResult.OK)
                return;

            if (_importWorker.IsBusy)
            {
                MessageBox.Show("导入任务正在进行中，请等待完成后再设定基准曲线。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 弹出转辙机选择对话框
            List<string> selectedIds = ShowSwitchSelectDialog("选择要建基线的转辙机");
            if (selectedIds == null)
                return; // 用户取消

            statusLabel.Text = string.Format("正在重建基准曲线 ({0} 台转辙机)...", selectedIds.Count);
            var capturedIds = selectedIds;
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.WorkerSupportsCancellation = true;

            worker.DoWork += (s, args) =>
            {
                try
                {
                    string rulesDir = _config.Diagnosis.RulesDir;
                    if (!Path.IsPathRooted(rulesDir))
                        rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);

                    var resultInfo = BuildAllBaselines(rulesDir, capturedIds);
                    args.Result = resultInfo;
                }
                catch (Exception ex)
                {
                    args.Result = "失败: " + ex.Message;
                }
            };

            worker.RunWorkerCompleted += (s, args) =>
            {
                string resultInfo = args.Result as string;
                if (resultInfo != null && resultInfo.StartsWith("失败"))
                {
                    statusLabel.Text = "基准曲线重建失败";
                    MessageBox.Show("基准曲线重建失败:\n" + resultInfo,
                        "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    statusLabel.Text = "基准曲线重建完成";
                    MessageBox.Show("基准曲线重建完成！\n\n" + resultInfo,
                        "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // 刷新当前视图以加载新的分方向基线
                    if (!string.IsNullOrEmpty(_selectedSwitchId) &&
                        !string.IsNullOrEmpty(_selectedDate) &&
                        _selectedTimestamp > 0)
                    {
                        LoadCurveData();
                    }
                }
            };

            worker.RunWorkerAsync();
        }

        /// <summary>
        /// 重建基线（分方向）。
        /// 读取 features.json 和 current_features.json，按方向过滤后构建基线，
        /// 写入 baselines.json 和 current_baselines.json。
        /// </summary>
        /// <param name="rulesDir">诊断规则目录</param>
        /// <param name="selectedSwitchIds">指定转辙机列表，传 null 或空则跑全站</param>
        /// <returns>结果摘要字符串</returns>
        private string BuildAllBaselines(string rulesDir, List<string> selectedSwitchIds = null)
        {
            // 使用当前站点的 ParsedDataDir（而非全局 _config.ParsedDataDir），
            // 否则在番禺站等非默认站点构建基线时会读取错误的目录。
            string parsedDataDir = _indexManager.ParsedDataDir;
            string[] directions = { BaselineStore.DirNormalToReverse, BaselineStore.DirReverseToNormal };

            // 确定要处理的转辙机列表
            List<string> targetSwitchIds = (selectedSwitchIds != null && selectedSwitchIds.Count > 0)
                ? selectedSwitchIds
                : _indexManager.GetAllSwitchIds();

            // ── 功率基线 ──
            var powerBaselines = new BaselineStore();
            int powerOk = 0, powerSkipped = 0;
            foreach (var sid in targetSwitchIds)
            {
                // 读取该道岔的全部 features.json 数据；缺失则从原始数据提取并回填
                List<CurveFeatures> allFeats = LoadAllPowerFeatures(sid, parsedDataDir);
                if (allFeats.Count == 0)
                {
                    Logger.Info(string.Format("features.json 缺失，从原始数据提取: {0}", sid));
                    FeaturesStore.BackfillWithDir(_indexManager, sid, parsedDataDir);
                    allFeats = LoadAllPowerFeatures(sid, parsedDataDir);
                }
                if (allFeats.Count == 0)
                    continue;

                foreach (var dir in directions)
                {
                    var bl = BaselineBuilder.Build(allFeats, 30, dir);
                    if (bl != null)
                    {
                        powerBaselines.Switches[BaselineStore.MakeKey(sid, dir)] = bl;
                        powerOk++;

                        // D5.5: 从历史原始功率数据生成 spike 对齐的逐点中位标准曲线
                        // 多重过滤: ①时长±15%基线 ②R4-R9诊断预筛选 ③IQR统计离群剔除
                        try
                        {
                            // ── Pass 1: 收集候选曲线 + 基线诊断预筛选 ──
                            var candidates = new List<CurveFeatures>();
                            var candidatePowerVals = new List<List<double>>();
                            foreach (var date in _indexManager.GetDates(sid))
                            {
                                var dayEvents = _indexManager.LoadDayData(sid, date);
                                foreach (var evt in dayEvents)
                                {
                                    // 方向过滤：未知方向视为匹配所有方向（与 BaselineBuilder Fix A 一致）
                                    if (evt.Direction != null && evt.Direction != "未知" && evt.Direction != dir) continue;
                                    if (evt.Power == null || evt.Power.Count < 10) continue;

                                    var feats = FeatureExtractor.Extract(evt);
                                    if (!feats.IsValid || feats.IsFullWindow || feats.DurationSec < 2.4) continue;
                                    if (Math.Abs(feats.DurationSec - bl.RefDurationSec) >= bl.RefDurationSec * 0.15) continue;

                                    // 基线参考值诊断预筛选（与 DiagnosisEngine R4-R9 阈值一致）
                                    if (feats.SpikePeak > bl.RefSpikePeak * 1.3) continue;   // R4: 启动峰值偏高
                                    if (feats.ConvMean > bl.RefConvMean * 1.3) continue;     // R5: 转换段偏高
                                    if (feats.UnlockMean > bl.RefUnlockMean * 1.3) continue; // R7: 解锁段偏高
                                    if (bl.RefTailMean > 0.001 && Math.Abs(feats.TailMean - bl.RefTailMean) / bl.RefTailMean > 0.3) continue; // R8
                                    if (bl.RefLockMean > 0.001 && Math.Abs(feats.LockMean - bl.RefLockMean) / bl.RefLockMean > 0.3) continue; // R9
                                    if (feats.StepRatio > 1.5 || feats.StepRatio < 0.67) continue; // R6: 台阶突变

                                    var powerVals = new List<double>(evt.Power.Count);
                                    foreach (var pair in evt.Power)
                                    {
                                        if (pair != null && pair.Length >= 2)
                                            powerVals.Add(Math.Round(pair[1], 3));
                                        else
                                            powerVals.Add(0.0);
                                    }
                                    candidates.Add(feats);
                                    candidatePowerVals.Add(powerVals);
                                }
                            }

                            int rawCount = candidatePowerVals.Count;

                            // ── Pass 2: IQR 统计离群过滤（6 维特征） ──
                            var filteredPowerCurves = FilterByIQR(candidates, candidatePowerVals);

                            int filteredOut = rawCount - filteredPowerCurves.Count;
                            if (filteredOut > 0)
                            {
                                Logger.Info(string.Format("IQR 过滤 {0} {1}: 剔除 {2} 条离群曲线, 保留 {3} 条",
                                    sid, dir, filteredOut, filteredPowerCurves.Count));
                            }

                            var sc = MedianCurveBuilder.Build(filteredPowerCurves, sid, dir);
                            if (sc != null)
                            {
                                string scDir = Path.Combine(parsedDataDir, "standard_curves");
                                StandardCurveStore.Save(scDir, sc);
                                Logger.Info(string.Format("标准曲线已生成: {0} {1} ({2} 条曲线 → {3} 个点)",
                                    sid, dir, filteredPowerCurves.Count, sc.Values.Count));

                                // D8.5: 若旧标准曲线有融合权重，自动恢复融合
                                double oldWeight = sc.FusionWeight;
                                if (oldWeight > 0.001)
                                {
                                    string refDir2 = Path.Combine(rulesDir, "reference_curves");
                                    string refFileName2 = ReferenceCurveStore.MakeFileName(sid, dir);
                                    string refPath2 = Path.Combine(refDir2, refFileName2);
                                    if (File.Exists(refPath2))
                                    {
                                        var refCurve = ReferenceCurveStore.Load(refPath2);
                                        if (refCurve != null)
                                        {
                                            var blended = StandardCurveBuilder.Blend(sc, refCurve, oldWeight);
                                            if (blended != null)
                                            {
                                                StandardCurveStore.Save(scDir, blended);
                                                Logger.Info(string.Format("BuildAllBaselines: 已恢复融合 {0} {1} w={2:F2}",
                                                    sid, dir, oldWeight));
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Logger.Info(string.Format("标准曲线跳过 {0} {1}: 有效曲线不足 (原始{2}条)",
                                    sid, dir, rawCount));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning(string.Format("标准曲线生成失败 {0} {1}: {2}", sid, dir, ex.Message));
                        }
                    }
                    else
                    {
                        powerSkipped++;
                        Logger.Info(string.Format("功率基线 {0}|{1} 样本不足，跳过", sid, dir));
                    }
                }
            }
            powerBaselines.ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string powerPath = Path.Combine(parsedDataDir, "baselines.json");
            powerBaselines.Save(powerPath);

            // ── 电流基线 ──
            var currentBaselines = new CurrentBaselineStore();
            int currentOk = 0, currentSkipped = 0;
            foreach (var sid in targetSwitchIds)
            {
                List<CurrentFeatures> allCurrentFeats = LoadAllCurrentFeatures(sid, parsedDataDir);
                if (allCurrentFeats.Count == 0)
                {
                    Logger.Info(string.Format("current_features.json 缺失，从原始数据提取: {0}", sid));
                    CurrentFeaturesStore.BackfillWithDir(_indexManager, sid, parsedDataDir);
                    allCurrentFeats = LoadAllCurrentFeatures(sid, parsedDataDir);
                }
                if (allCurrentFeats.Count == 0)
                    continue;

                foreach (var dir in directions)
                {
                    var cbl = CurrentBaselineBuilder.Build(allCurrentFeats, 30, dir);
                    if (cbl != null)
                    {
                        currentBaselines.Switches[CurrentBaselineStore.MakeKey(sid, dir)] = cbl;
                        currentOk++;
                    }
                    else
                    {
                        currentSkipped++;
                        Logger.Info(string.Format("电流基线 {0}|{1} 样本不足，跳过", sid, dir));
                    }
                }
            }
            currentBaselines.ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string currentPath = Path.Combine(parsedDataDir, "current_baselines.json");
            currentBaselines.Save(currentPath);

            string summary = string.Format(
                "功率基线: {0} 条成功, {1} 条样本不足\n" +
                "电流基线: {2} 条成功, {3} 条样本不足\n\n" +
                "已保存到:\n{4}\n{5}",
                powerOk, powerSkipped, currentOk, currentSkipped,
                powerPath, currentPath);

            Logger.Info("基准曲线重建完成: " + summary.Replace("\n", " | "));
            return summary;
        }

        /// <summary>
        /// 从 parsed_data 读取某道岔的全部功率特征（合并各日期的 features.json）
        /// </summary>
        private static List<CurveFeatures> LoadAllPowerFeatures(string switchId, string parsedDataDir)
        {
            var result = new List<CurveFeatures>();
            try
            {
                string featuresPath = Path.Combine(parsedDataDir, switchId, "features.json");
                if (File.Exists(featuresPath))
                {
                    var store = FeaturesStore.Load(featuresPath);
                    if (store != null && store.Rows != null && store.Columns != null)
                    {
                        int durIdx = store.ColumnIndex("durationSec");
                        int spikeIdx = store.ColumnIndex("spikePeak");
                        int unlockIdx = store.ColumnIndex("unlockMean");
                        int convIdx = store.ColumnIndex("convMean");
                        int lockIdx = store.ColumnIndex("lockMean");
                        int tailIdx = store.ColumnIndex("tailMean");
                        int dirIdx = store.ColumnIndex("direction");

                        foreach (var row in store.Rows)
                        {
                            if (row == null || row.Count == 0) continue;
                            var f = new CurveFeatures
                            {
                                DurationSec = durIdx >= 0 && durIdx < row.Count ? row[durIdx] : 0,
                                SpikePeak = spikeIdx >= 0 && spikeIdx < row.Count ? row[spikeIdx] : 0,
                                UnlockMean = unlockIdx >= 0 && unlockIdx < row.Count ? row[unlockIdx] : 0,
                                ConvMean = convIdx >= 0 && convIdx < row.Count ? row[convIdx] : 0,
                                LockMean = lockIdx >= 0 && lockIdx < row.Count ? row[lockIdx] : 0,
                                TailMean = tailIdx >= 0 && tailIdx < row.Count ? row[tailIdx] : 0,
                                Direction = dirIdx >= 0 && dirIdx < row.Count
                                    ? FeaturesStore.DecodeDirection(row[dirIdx]) : null,
                                IsValid = true,
                                IsFullWindow = false // features.json 不存此字段，默认 false
                            };
                            result.Add(f);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("LoadAllPowerFeatures 失败 switchId=" + switchId + ": " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// 从 parsed_data 读取某道岔的全部电流特征（合并各日期的 current_features.json）
        /// </summary>
        private static List<CurrentFeatures> LoadAllCurrentFeatures(string switchId, string parsedDataDir)
        {
            var result = new List<CurrentFeatures>();
            try
            {
                string featuresPath = Path.Combine(parsedDataDir, switchId, "current_features.json");
                if (File.Exists(featuresPath))
                {
                    var store = CurrentFeaturesStore.Load(featuresPath);
                    if (store != null && store.Rows != null && store.Columns != null)
                    {
                        int durIdx = store.ColumnIndex("durationSec");
                        int dirIdx = store.ColumnIndex("direction");
                        int spikeIdxA = store.ColumnIndex("spikeIndexA");
                        int spikePeakA = store.ColumnIndex("spikePeakA");
                        int unlockA = store.ColumnIndex("unlockMeanA");
                        int convA = store.ColumnIndex("convMeanA");
                        int lockA = store.ColumnIndex("lockMeanA");
                        int tailA = store.ColumnIndex("tailMeanA");
                        int spikePeakB = store.ColumnIndex("spikePeakB");
                        int spikeIdxB = store.ColumnIndex("spikeIndexB");
                        int unlockB = store.ColumnIndex("unlockMeanB");
                        int convB = store.ColumnIndex("convMeanB");
                        int lockB = store.ColumnIndex("lockMeanB");
                        int tailB = store.ColumnIndex("tailMeanB");
                        int spikePeakC = store.ColumnIndex("spikePeakC");
                        int spikeIdxC = store.ColumnIndex("spikeIndexC");
                        int unlockC = store.ColumnIndex("unlockMeanC");
                        int convC = store.ColumnIndex("convMeanC");
                        int lockC = store.ColumnIndex("lockMeanC");
                        int tailC = store.ColumnIndex("tailMeanC");
                        int maxUnbIdx = store.ColumnIndex("maxUnbalanceRatio");
                        int isValidIdx = store.ColumnIndex("isValid");
                        int isFullIdx = store.ColumnIndex("isFullWindow");

                        foreach (var row in store.Rows)
                        {
                            if (row == null || row.Count == 0) continue;
                            var f = new CurrentFeatures
                            {
                                DurationSec = durIdx >= 0 && durIdx < row.Count ? row[durIdx] : 0,
                                Direction = dirIdx >= 0 && dirIdx < row.Count
                                    ? FeaturesStore.DecodeDirection(row[dirIdx]) : null,
                                SpikeIndexA = spikeIdxA >= 0 && spikeIdxA < row.Count ? (int)Math.Round(row[spikeIdxA]) : 0,
                                SpikePeakA = spikePeakA >= 0 && spikePeakA < row.Count ? row[spikePeakA] : 0,
                                UnlockMeanA = unlockA >= 0 && unlockA < row.Count ? row[unlockA] : 0,
                                ConvMeanA = convA >= 0 && convA < row.Count ? row[convA] : 0,
                                LockMeanA = lockA >= 0 && lockA < row.Count ? row[lockA] : 0,
                                TailMeanA = tailA >= 0 && tailA < row.Count ? row[tailA] : 0,
                                SpikePeakB = spikePeakB >= 0 && spikePeakB < row.Count ? row[spikePeakB] : 0,
                                SpikeIndexB = spikeIdxB >= 0 && spikeIdxB < row.Count ? (int)Math.Round(row[spikeIdxB]) : 0,
                                UnlockMeanB = unlockB >= 0 && unlockB < row.Count ? row[unlockB] : 0,
                                ConvMeanB = convB >= 0 && convB < row.Count ? row[convB] : 0,
                                LockMeanB = lockB >= 0 && lockB < row.Count ? row[lockB] : 0,
                                TailMeanB = tailB >= 0 && tailB < row.Count ? row[tailB] : 0,
                                SpikePeakC = spikePeakC >= 0 && spikePeakC < row.Count ? row[spikePeakC] : 0,
                                SpikeIndexC = spikeIdxC >= 0 && spikeIdxC < row.Count ? (int)Math.Round(row[spikeIdxC]) : 0,
                                UnlockMeanC = unlockC >= 0 && unlockC < row.Count ? row[unlockC] : 0,
                                ConvMeanC = convC >= 0 && convC < row.Count ? row[convC] : 0,
                                LockMeanC = lockC >= 0 && lockC < row.Count ? row[lockC] : 0,
                                TailMeanC = tailC >= 0 && tailC < row.Count ? row[tailC] : 0,
                                MaxUnbalanceRatio = maxUnbIdx >= 0 && maxUnbIdx < row.Count ? row[maxUnbIdx] : 0,
                                IsValid = isValidIdx >= 0 && isValidIdx < row.Count ? row[isValidIdx] > 0.5 : true,
                                IsFullWindow = isFullIdx >= 0 && isFullIdx < row.Count ? row[isFullIdx] > 0.5 : false,
                            };
                            result.Add(f);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("LoadAllCurrentFeatures 失败 switchId=" + switchId + ": " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// IQR 统计离群过滤：对 6 维特征逐维计算四分位距，
        /// 在 >=2 维上为离群值的曲线被剔除。
        /// 样本 < 30 时不过滤（样本不足 IQR 不可靠）。
        /// </summary>
        private static List<List<double>> FilterByIQR(List<CurveFeatures> feats, List<List<double>> powerValues)
        {
            if (feats == null || feats.Count < 30) return powerValues;
            int n = feats.Count;

            // 6 维特征: SpikePeak, UnlockMean, ConvMean, LockMean, TailMean, DurationSec
            var dims = new Func<CurveFeatures, double>[]
            {
                f => f.SpikePeak,
                f => f.UnlockMean,
                f => f.ConvMean,
                f => f.LockMean,
                f => f.TailMean,
                f => f.DurationSec
            };

            int[] outlierCounts = new int[n];

            foreach (var getter in dims)
            {
                // 提取该维所有值并排序
                var values = new double[n];
                for (int i = 0; i < n; i++)
                    values[i] = getter(feats[i]);
                Array.Sort(values);

                // Q1, Q3, IQR
                double q1 = values[n / 4];
                double q3 = values[3 * n / 4];
                double iqr = q3 - q1;
                if (iqr < 0.001) continue; // 所有值几乎相同，跳过该维

                double lower = q1 - 1.5 * iqr;
                double upper = q3 + 1.5 * iqr;

                for (int i = 0; i < n; i++)
                {
                    double v = getter(feats[i]);
                    if (v < lower || v > upper)
                        outlierCounts[i]++;
                }
            }

            // 保留在 <=1 维上为离群值的曲线
            var result = new List<List<double>>();
            for (int i = 0; i < n; i++)
            {
                if (outlierCounts[i] <= 1)
                    result.Add(powerValues[i]);
            }

            return result;
        }

        /// <summary>
        /// 工具 → 重新诊断当前数据
        /// </summary>
        private void OnRerunDiagClicked(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "将对已导入的道岔动作数据重新运行诊断引擎。\n\n" +
                "此操作不重新导入 CSV，仅重跑诊断规则。\n" +
                "点击「确定」后将弹出转辙机选择框，可只诊断选定转辙机。\n" +
                "完成后将更新诊断条、时间列表着色和日期角标。\n\n确定继续？",
                "重新诊断", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

            if (result == DialogResult.OK)
            {
                // 弹出转辙机选择对话框
                List<string> selectedIds = ShowSwitchSelectDialog("选择要重跑诊断的转辙机");
                if (selectedIds == null)
                    return; // 用户取消

                statusLabel.Text = string.Format("正在重跑诊断 ({0} 台转辙机)...", selectedIds.Count);
                RunDiagnosisRerun(selectedIds);
            }
        }

        /// <summary>
        /// 弹出转辙机选择对话框，返回用户勾选的 ID 列表；取消则返回 null。
        /// </summary>
        private List<string> ShowSwitchSelectDialog(string title)
        {
            var allIds = _indexManager.GetAllSwitchIds();
            if (allIds.Count == 0)
            {
                MessageBox.Show("当前站点无已导入的转辙机数据。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            using (var dialog = new SwitchSelectDialog(allIds, title))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    return dialog.SelectedSwitchIds;
                return null;
            }
        }

        /// <summary>
        /// 后台重跑诊断（BackgroundWorker）
        /// </summary>
        private void RunDiagnosisRerun(List<string> selectedSwitchIds = null)
        {
            if (_importWorker.IsBusy)
            {
                MessageBox.Show("导入任务正在进行中，请等待完成后再重跑诊断。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var capturedIds = selectedSwitchIds; // 捕获到闭包
            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.WorkerSupportsCancellation = true;

            worker.DoWork += (s, args) =>
            {
                try
                {
                    string rulesDir = _config.Diagnosis.RulesDir;
                    if (!Path.IsPathRooted(rulesDir))
                        rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);
                    string stationDir = _indexManager.ParsedDataDir;

                    var engine = new DiagnosisEngine();
                    engine.Initialize(rulesDir, stationDir);
                    engine.SetParsedDataDir(stationDir);

                    // 重建索引以获取最新数据
                    _indexManager.Initialize();

                    if (capturedIds != null && capturedIds.Count > 0)
                        DiagnosisRunner.RerunSelected(_indexManager, engine, capturedIds);
                    else
                        DiagnosisRunner.RerunAll(_indexManager, engine);
                    args.Result = "OK";
                }
                catch (Exception ex)
                {
                    args.Result = "失败: " + ex.Message;
                }
            };

            worker.RunWorkerCompleted += (s, args) =>
            {
                string result = args.Result as string;
                if (result == "OK")
                {
                    statusLabel.Text = "诊断重跑完成";
                    MessageBox.Show("诊断重跑完成！\n\n请重新选择日期查看更新后的诊断结果。",
                        "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // 刷新当前视图
                    if (!string.IsNullOrEmpty(_selectedSwitchId))
                    {
                        // 重新加载 alarms_index 角标
                        try
                        {
                            var alarmsIndex = _indexManager.LoadAlarmsIndex();
                            Dictionary<string, Dictionary<string, int>> switchAlarms = null;
                            if (alarmsIndex.ContainsKey(_selectedSwitchId))
                                switchAlarms = alarmsIndex[_selectedSwitchId];
                            string alarmsJson = _serializer.Serialize(switchAlarms);
                            InvokeSidebarScript("setAlarmBadges", alarmsJson);
                        }
                        catch { }

                        // 重载当前日期的时间列表 + 曲线
                        if (!string.IsNullOrEmpty(_selectedDate))
                        {
                            OnDateSelected(_selectedDate);
                        }
                    }
                }
                else
                {
                    statusLabel.Text = "诊断重跑失败";
                    MessageBox.Show("诊断重跑失败:\n" + result,
                        "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            worker.RunWorkerAsync();
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

            // 启用批量缓冲：导入期间 features.json / current_features.json 只写内存，
            // 消除逐条 Append 的 O(n²) 文件 I/O（每条事件读→反序列化→追加→序列化→写整个 JSON）
            FeaturesStore.BatchMode = true;

            try
            {
                _pipeline.ImportAll(customSourceDir);

                // 导入完成后刷新批量缓冲
                string parsedDir = _indexManager.ParsedDataDir;
                FeaturesStore.FlushBatch(parsedDir);
                CurrentFeaturesStore.FlushBatch(parsedDir);
            }
            catch (Exception ex)
            {
                e.Result = "导入失败: " + ex.Message;
            }
            finally
            {
                FeaturesStore.BatchMode = false;
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
                var groups = GetCurrentSwitchGroups();
                var sites = _config.Sites ?? new List<SiteConfig>();
                string selectedSiteId = _selectedSite != null ? _selectedSite.Id : (_config.SelectedSiteId ?? "");
                html = html.Replace("{{SWITCH_GROUPS_JSON}}", _serializer.Serialize(groups));
                html = html.Replace("{{SITES_JSON}}", _serializer.Serialize(sites));
                html = html.Replace("{{SELECTED_SITE_ID}}", selectedSiteId);
                sidebarBrowser.DocumentText = html;
                sidebarBrowser.ObjectForScripting = _jsBridge;  // 导航后重新绑定 COM
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
                chartBrowser.ObjectForScripting = _jsBridge;
                return;
            }

            // 内联注入 jquery.js 和 highcharts.js
            html = InjectEmbeddedScript(html, "SwitchMonitor.UI.Js.jquery.js");
            html = InjectEmbeddedScript(html, "SwitchMonitor.UI.Js.highcharts.js");

            chartBrowser.DocumentText = html;
            chartBrowser.ObjectForScripting = _jsBridge;  // 导航后重新绑定 COM
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

            Logger.Info("选择转辙机: " + switchId);

            // 获取该转辙机的所有日期列表
            var dates = _indexManager.GetDates(switchId);

            Logger.Info(string.Format("转辙机 {0} 共 {1} 个日期", switchId, dates.Count));

            // 通知侧边栏更新日期下拉框
            string datesJson = _serializer.Serialize(dates);
            InvokeSidebarScript("setDates", datesJson);

            // D5: 加载 alarms_index，传递日期角标数据
            try
            {
                var alarmsIndex = _indexManager.LoadAlarmsIndex();
                Dictionary<string, Dictionary<string, int>> switchAlarms = null;
                if (alarmsIndex.ContainsKey(switchId))
                    switchAlarms = alarmsIndex[switchId];
                string alarmsJson = _serializer.Serialize(switchAlarms);
                InvokeSidebarScript("setAlarmBadges", alarmsJson);
            }
            catch
            {
                // alarms_index 缺失时不报错，仅不显示角标
                InvokeSidebarScript("setAlarmBadges", "null");
            }

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

            Logger.Info(string.Format("选择日期: {0} / {1}", _selectedSwitchId, date));

            // 获取该天的所有时间戳
            var timestamps = _indexManager.GetTimestamps(_selectedSwitchId, date);

            Logger.Info(string.Format("日期 {0} 共 {1} 条动作记录", date, timestamps.Count));

            // D5: 加载诊断结果，构造 [{ts, level}, ...] 扩展格式传给 setTimes
            var diagnoses = _indexManager.LoadDayDiagnosis(_selectedSwitchId, date);
            var diagByTs = new Dictionary<long, string>();
            foreach (var d in diagnoses)
            {
                diagByTs[d.Timestamp] = d.Level;
            }

            var timesWithLevel = new List<object>();
            foreach (var ts in timestamps)
            {
                string level;
                if (diagByTs.TryGetValue(ts, out level))
                    timesWithLevel.Add(new { ts = ts, level = level });
                else
                    timesWithLevel.Add(new { ts = ts, level = "正常" });
            }

            string timesJson = _serializer.Serialize(timesWithLevel);
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

            Logger.Info(string.Format("选择时间: {0} / {1} / {2}",
                _selectedSwitchId, _selectedDate, UnixToTimeString(timestamp)));

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

        /// <summary>
        /// 双击图表 → 打开独立曲线详情窗口
        /// </summary>
        public void OnOpenChartDetail(string chartKey, string dataJson)
        {
            try
            {
                // 准备 chart_detail.html（内联注入 JS 库）
                string detailHtml = GetEmbeddedResource("SwitchMonitor.UI.Html.chart_detail.html");
                if (string.IsNullOrEmpty(detailHtml))
                {
                    MessageBox.Show("无法加载图表详情页面。", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                detailHtml = InjectEmbeddedScript(detailHtml, "SwitchMonitor.UI.Js.jquery.js");
                detailHtml = InjectEmbeddedScript(detailHtml, "SwitchMonitor.UI.Js.highcharts.js");

                var detailForm = new ChartDetailForm(dataJson, detailHtml);
                detailForm.Show(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OnOpenChartDetail 错误: " + ex.Message);
            }
        }

        /// <summary>
        /// D8: 保存人工参考曲线。若标准曲线已存在则自动融合。
        /// </summary>
        public void OnSetReferenceCurve(string switchId, string powerJson, string direction, double fusionWeight = 1.0)
        {
            try
            {
                // 1. 解析 power 值
                var serializer = new JavaScriptSerializer();
                var powerValues = serializer.Deserialize<List<double>>(powerJson);
                if (powerValues == null || powerValues.Count < 10)
                {
                    Logger.Warning("OnSetReferenceCurve: power 数据无效 switchId=" + switchId);
                    return;
                }

                string rulesDir = _config.Diagnosis.RulesDir;
                if (!Path.IsPathRooted(rulesDir))
                    rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);

                double sampleInterval = 0.04;

                // 2. 构建 ReferenceCurve 并保存（按方向命名文件）
                var feat = FeatureExtractor.Extract(powerValues);
                var refCurve = new ReferenceCurve
                {
                    SwitchId = switchId,
                    Direction = direction,
                    SampleInterval = sampleInterval,
                    AlignIndex = feat.IsValid ? feat.SpikeIndex : 6,
                    Values = powerValues,
                    ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Source = "manual"
                };

                string refDir = Path.Combine(rulesDir, "reference_curves");
                if (!Directory.Exists(refDir))
                    Directory.CreateDirectory(refDir);
                string refFileName = ReferenceCurveStore.MakeFileName(switchId, direction);
                string refPath = Path.Combine(refDir, refFileName);
                File.WriteAllText(refPath, serializer.Serialize(refCurve), Encoding.UTF8);
                Logger.Info(string.Format("参考曲线已保存: {0}", refPath));

                // 3. 若标准曲线已存在，自动融合
                string scDir = Path.Combine(_indexManager.ParsedDataDir, "standard_curves");
                string scFileName = StandardCurveStore.MakeFileName(switchId, direction);
                string scPath = Path.Combine(scDir, scFileName);
                if (File.Exists(scPath))
                {
                    var medianCurve = StandardCurveStore.Load(scPath);
                    if (medianCurve != null && medianCurve.Values != null && medianCurve.Values.Count > 0)
                    {
                        double w = Math.Max(0.0, Math.Min(1.0, fusionWeight));
                        var blended = StandardCurveBuilder.Blend(medianCurve, refCurve, w);
                        if (blended != null)
                        {
                            StandardCurveStore.Save(scDir, blended);
                            _diagnosisEngine?.UpdateStandardCurve(switchId, direction, blended);
                            Logger.Info(string.Format("OnSetReferenceCurve: 已自动融合 w={0:F2}", w));
                        }
                    }
                }

                // 4. 刷新图表
                LoadCurveData();
            }
            catch (Exception ex)
            {
                Logger.Error("OnSetReferenceCurve 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 取消人工设定的参考曲线。
        /// 删除 reference_curves/ 文件后，标准曲线恢复为纯中位值。
        /// </summary>
        public void OnCancelReferenceCurve(string switchId, string direction, string powerJson, double fusionWeight = 1.0)
        {
            try
            {
                string rulesDir = _config.Diagnosis.RulesDir;
                if (!Path.IsPathRooted(rulesDir))
                    rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);

                // 1. 删除人工参考曲线文件
                string refDir = Path.Combine(rulesDir, "reference_curves");
                string refFileName = ReferenceCurveStore.MakeFileName(switchId, direction);
                string refPath = Path.Combine(refDir, refFileName);
                if (File.Exists(refPath))
                {
                    // 只删除人工设定的参考曲线
                    try
                    {
                        var serializer = new JavaScriptSerializer();
                        var dict = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(refPath, Encoding.UTF8));
                        object src;
                        if (dict != null && dict.TryGetValue("Source", out src) && src != null && src.ToString() == "manual")
                        {
                            File.Delete(refPath);
                            Logger.Info(string.Format("人工参考曲线已删除: {0}", refPath));
                        }
                    }
                    catch { /* 解析失败则安全跳过 */ }
                }

                // 2. 恢复标准曲线为纯中位
                string scDir = Path.Combine(_indexManager.ParsedDataDir, "standard_curves");
                string scFileName = StandardCurveStore.MakeFileName(switchId, direction);
                string scPath = Path.Combine(scDir, scFileName);
                if (File.Exists(scPath))
                {
                    var sc = StandardCurveStore.Load(scPath);
                    if (sc != null && sc.OriginalMedianValues != null && sc.OriginalMedianValues.Count > 0)
                    {
                        sc.Values = new List<double>(sc.OriginalMedianValues);
                        sc.FusionWeight = 0.0;
                        StandardCurveStore.Save(scDir, sc);
                        _diagnosisEngine?.UpdateStandardCurve(switchId, direction, sc);
                        Logger.Info(string.Format("标准曲线已恢复为纯中位: {0} {1}", switchId, direction));
                    }
                }

                // 3. 刷新图表
                LoadCurveData();
            }
            catch (Exception ex)
            {
                Logger.Error("OnCancelReferenceCurve 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// HTML: window.external.SetFusionWeight("1-J", "定位→反位", 0.5)
        /// 从磁盘加载中位标准曲线与人工参考曲线，按融合权重逐点混合后保存。
        /// </summary>
        public void OnSetFusionWeight(string switchId, string direction, double fusionWeight)
        {
            try
            {
                string rulesDir = _config.Diagnosis.RulesDir;
                if (!Path.IsPathRooted(rulesDir))
                    rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);

                // 1. 加载标准曲线
                string scDir = Path.Combine(_indexManager.ParsedDataDir, "standard_curves");
                string scFileName = StandardCurveStore.MakeFileName(switchId, direction);
                string scPath = Path.Combine(scDir, scFileName);
                var medianCurve = StandardCurveStore.Load(scPath);
                if (medianCurve == null || medianCurve.Values == null || medianCurve.Values.Count == 0)
                {
                    Logger.Info(string.Format("OnSetFusionWeight: 无标准曲线可融合, {0} {1}", switchId, direction));
                    LoadCurveData();
                    return;
                }

                // 2. 加载参考曲线
                string refDir = Path.Combine(rulesDir, "reference_curves");
                string refFileName = ReferenceCurveStore.MakeFileName(switchId, direction);
                string refPath = Path.Combine(refDir, refFileName);
                if (!File.Exists(refPath))
                {
                    string oldPath = Path.Combine(refDir, switchId + ".json");
                    if (File.Exists(oldPath))
                        refPath = oldPath;
                }
                var refCurve = ReferenceCurveStore.Load(refPath);
                if (refCurve == null || refCurve.Values == null || refCurve.Values.Count == 0)
                {
                    Logger.Info(string.Format("OnSetFusionWeight: 无参考曲线可融合, {0} {1}", switchId, direction));
                    LoadCurveData();
                    return;
                }

                // 3. 融合
                double w = Math.Max(0.0, Math.Min(1.0, fusionWeight));
                var blended = StandardCurveBuilder.Blend(medianCurve, refCurve, w);
                if (blended == null)
                {
                    Logger.Warning(string.Format("OnSetFusionWeight: 融合失败, {0} {1}", switchId, direction));
                    return;
                }

                // 4. 保存
                StandardCurveStore.Save(scDir, blended);
                Logger.Info(string.Format("融合曲线已保存: {0} {1} w={2:F2}", switchId, direction, w));

                // 5. 热更新引擎缓存
                _diagnosisEngine?.UpdateStandardCurve(switchId, direction, blended);

                // 6. 刷新前端
                LoadCurveData();
            }
            catch (Exception ex)
            {
                Logger.Error("OnSetFusionWeight 失败: " + ex.Message);
            }
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

            // D5: 加载当前事件的诊断结论
            object diagnosis = null;
            try
            {
                var dayDiagnoses = _indexManager.LoadDayDiagnosis(_selectedSwitchId, _selectedDate);
                EventDiagnosis currentDiag = null;
                foreach (var d in dayDiagnoses)
                {
                    if (d.Timestamp == _selectedTimestamp)
                    {
                        currentDiag = d;
                        break;
                    }
                }

                if (currentDiag != null && currentDiag.Results != null && currentDiag.Results.Count > 0)
                {
                    var items = new List<string>();
                    foreach (var r in currentDiag.Results)
                    {
                        items.Add(r.Description);
                    }
                    diagnosis = new
                    {
                        level = currentDiag.Level,
                        items = items.ToArray()
                    };
                }
                else if (currentDiag != null)
                {
                    // 诊断数据存在但无命中规则 → 正常
                    diagnosis = new
                    {
                        level = currentDiag.Level,
                        items = new string[0]
                    };
                }
                // 否则 diagnosis 保持 null（.diag.json 缺失 → 前端显示"未诊断"）
            }
            catch
            {
                // 诊断数据缺失时不报错
                diagnosis = null;
            }

            // D5.5: 标准曲线现在在 BuildAllBaselines 中生成（spike 对齐逐点中位数），
            // 不再按需从单条事件生成。

            // D6: 加载该道岔各方向的参考曲线（用于图表叠加，按方向匹配）
            object refCurve = null;      // 兼容旧版：currentEvent 方向的参考曲线
            var refCurves = new List<object>();  // 所有方向的参考曲线数组
            object baseline = null;
            try
            {
                string[] allDirections = { "定位→反位", "反位→定位" };
                string refDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.Diagnosis.RulesDir, "reference_curves");
                foreach (var dir in allDirections)
                {
                    string refFileName = ReferenceCurveStore.MakeFileName(_selectedSwitchId, dir);
                    string refPath = Path.Combine(refDir, refFileName);
                    // 降级：如果按方向找不到，尝试无方向旧格式
                    if (!File.Exists(refPath))
                    {
                        string oldPath = Path.Combine(refDir, _selectedSwitchId + ".json");
                        if (File.Exists(oldPath))
                            refPath = oldPath;
                    }
                    if (File.Exists(refPath))
                    {
                        var loadedRef = ReferenceCurveStore.Load(refPath);
                        if (loadedRef != null && loadedRef.Values != null && loadedRef.Values.Count > 0)
                        {
                            var refPairs = new List<object>();
                            double interval = loadedRef.SampleInterval > 0 ? loadedRef.SampleInterval : 0.04;
                            for (int i = 0; i < loadedRef.Values.Count; i++)
                            {
                                double t = Math.Round(i * interval, 3);
                                refPairs.Add(new double[] { t, loadedRef.Values[i] });
                            }
                            var refObj = new
                            {
                                switchId = loadedRef.SwitchId,
                                direction = loadedRef.Direction ?? dir,
                                alignIndex = loadedRef.AlignIndex,
                                values = refPairs
                            };
                            refCurves.Add(refObj);
                            // 保持 refCurve 兼容（取 currentEvent 方向或第一个）
                            string currentDir = currentEvent != null ? currentEvent.Direction : null;
                            if ((loadedRef.Direction ?? dir) == currentDir)
                                refCurve = refObj;
                        }
                    }
                }
                if (refCurve == null && refCurves.Count > 0)
                    refCurve = refCurves[0];
            }
            catch
            {
                // 参考曲线加载失败不中断主流程
                refCurve = null;
            }

            // D6b: 加载电流参考曲线（分相，从 Rules/current_reference_curves/）
            var currentRefCurves = new List<object>();
            try
            {
                string[] allDirections = { "定位→反位", "反位→定位" };
                string currentRefDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.Diagnosis.RulesDir, "current_reference_curves");
                if (Directory.Exists(currentRefDir))
                {
                    // 按 direction 分组，每组合并 A/B/C 三相
                    foreach (var dir in allDirections)
                    {
                        var phaseData = new Dictionary<string, List<object>>();  // "currentA" → [[t,v],...]
                        bool hasAny = false;
                        foreach (var phase in new[] { "A", "B", "C" })
                        {
                            string fileName = PhaseCurrentReferenceCurveStore.MakeFileName(_selectedSwitchId, dir, phase);
                            string filePath = Path.Combine(currentRefDir, fileName);
                            if (!File.Exists(filePath)) continue;

                            var pcRef = PhaseCurrentReferenceCurveStore.Load(filePath);
                            if (pcRef == null || pcRef.Values == null || pcRef.Values.Count == 0) continue;

                            double interval = pcRef.SampleInterval > 0 ? pcRef.SampleInterval : 0.04;
                            var pairs = new List<object>();
                            for (int i = 0; i < pcRef.Values.Count; i++)
                            {
                                double t = Math.Round(i * interval, 3);
                                pairs.Add(new double[] { t, pcRef.Values[i] });
                            }
                            phaseData["current" + phase] = pairs;
                            hasAny = true;
                        }
                        if (!hasAny) continue;

                        currentRefCurves.Add(new
                        {
                            switchId = _selectedSwitchId,
                            direction = dir,
                            currentA = phaseData.ContainsKey("currentA") ? phaseData["currentA"] : null,
                            currentB = phaseData.ContainsKey("currentB") ? phaseData["currentB"] : null,
                            currentC = phaseData.ContainsKey("currentC") ? phaseData["currentC"] : null
                        });
                    }
                }
            }
            catch
            {
                // 电流参考曲线加载失败不中断主流程
            }

            // D8: 加载该道岔各方向的标准曲线（参考曲线 + 基线融合）
            object standardCurve = null;      // 兼容旧版
            var standardCurves = new List<object>();  // 所有方向的标准曲线数组
            try
            {
                string[] allDirections = { "定位→反位", "反位→定位" };
                string scDir = Path.Combine(_indexManager.ParsedDataDir, "standard_curves");
                foreach (var dir in allDirections)
                {
                    string scFileName = StandardCurveStore.MakeFileName(_selectedSwitchId, dir);
                    string scPath = Path.Combine(scDir, scFileName);
                    if (File.Exists(scPath))
                    {
                        var loadedSc = StandardCurveStore.Load(scPath);
                        if (loadedSc != null && loadedSc.Values != null && loadedSc.Values.Count > 0)
                        {
                            var scPairs = new List<object>();
                            double scInterval = loadedSc.SampleInterval > 0 ? loadedSc.SampleInterval : 0.04;
                            for (int i = 0; i < loadedSc.Values.Count; i++)
                            {
                                double t = Math.Round(i * scInterval, 3);
                                scPairs.Add(new double[] { t, loadedSc.Values[i] });
                            }
                            var scObj = new
                            {
                                switchId = loadedSc.SwitchId,
                                direction = loadedSc.Direction ?? dir,
                                alignIndex = loadedSc.AlignIndex,
                                fusionWeight = loadedSc.FusionWeight,
                                values = scPairs
                            };
                            standardCurves.Add(scObj);
                            // 保持 standardCurve 兼容
                            string currentDir = currentEvent != null ? currentEvent.Direction : null;
                            if ((loadedSc.Direction ?? dir) == currentDir)
                                standardCurve = scObj;
                        }
                    }
                }
                if (standardCurve == null && standardCurves.Count > 0)
                    standardCurve = standardCurves[0];
            }
            catch
            {
                // 标准曲线加载失败不中断主流程
                standardCurve = null;
            }

            // D8b: 加载电流标准曲线（分相，从 parsed_data/current_standard_curves/）
            var currentStandardCurves = new List<object>();
            try
            {
                string[] allDirections2 = { "定位→反位", "反位→定位" };
                string currentScDir = Path.Combine(_indexManager.ParsedDataDir, "current_standard_curves");
                if (Directory.Exists(currentScDir))
                {
                    foreach (var dir in allDirections2)
                    {
                        var phaseData = new Dictionary<string, List<object>>();
                        bool hasAny = false;
                        foreach (var phase in new[] { "A", "B", "C" })
                        {
                            string fileName = PhaseCurrentStandardCurveStore.MakeFileName(_selectedSwitchId, dir, phase);
                            string filePath = Path.Combine(currentScDir, fileName);
                            if (!File.Exists(filePath)) continue;

                            var pcSc = PhaseCurrentStandardCurveStore.Load(filePath);
                            if (pcSc == null || pcSc.Values == null || pcSc.Values.Count == 0) continue;

                            double interval = pcSc.SampleInterval > 0 ? pcSc.SampleInterval : 0.04;
                            var pairs = new List<object>();
                            for (int i = 0; i < pcSc.Values.Count; i++)
                            {
                                double t = Math.Round(i * interval, 3);
                                pairs.Add(new double[] { t, pcSc.Values[i] });
                            }
                            phaseData["current" + phase] = pairs;
                            hasAny = true;
                        }
                        if (!hasAny) continue;

                        currentStandardCurves.Add(new
                        {
                            switchId = _selectedSwitchId,
                            direction = dir,
                            currentA = phaseData.ContainsKey("currentA") ? phaseData["currentA"] : null,
                            currentB = phaseData.ContainsKey("currentB") ? phaseData["currentB"] : null,
                            currentC = phaseData.ContainsKey("currentC") ? phaseData["currentC"] : null
                        });
                    }
                }
            }
            catch
            {
                // 电流标准曲线加载失败不中断主流程
            }

            // D7: 加载该道岔的诊断基线（用于图表叠加显示），按方向查找
            try
            {
                string baselinePath = Path.Combine(_indexManager.ParsedDataDir, "baselines.json");
                if (File.Exists(baselinePath))
                {
                    var store = BaselineStore.Load(baselinePath);
                    SwitchBaseline bl;
                    // 优先按方向精确查找，降级到无方向旧格式，再降级到任意方向
                    string currentDir = currentEvent != null ? currentEvent.Direction : null;
                    if (!store.Switches.TryGetValue(BaselineStore.MakeKey(_selectedSwitchId, currentDir), out bl))
                    {
                        // 降级：尝试无方向旧 key
                        if (!store.Switches.TryGetValue(_selectedSwitchId, out bl))
                        {
                            // 再降级：方向未知/不匹配时，取该道岔的任意一条基线
                            string prefix = _selectedSwitchId + "|";
                            foreach (var kv in store.Switches)
                            {
                                if (kv.Key == _selectedSwitchId || kv.Key.StartsWith(prefix))
                                {
                                    bl = kv.Value;
                                    break;
                                }
                            }
                        }
                    }
                    if (bl != null)
                    {
                        baseline = new
                        {
                            refDurationSec = bl.RefDurationSec,
                            refSpikePeak = bl.RefSpikePeak,
                            refUnlockMean = bl.RefUnlockMean,
                            refConvMean = bl.RefConvMean,
                            refLockMean = bl.RefLockMean,
                            refTailMean = bl.RefTailMean,
                            sampleCount = bl.SampleCount
                        };
                    }
                }
            }
            catch
            {
                // 基线加载失败不中断主流程
                baseline = null;
            }

            // D7: 加载电流诊断基线（用于电流曲线图表叠加显示），按方向查找
            object currentBaseline = null;
            try
            {
                string currentBaselinePath = Path.Combine(_indexManager.ParsedDataDir, "current_baselines.json");
                if (File.Exists(currentBaselinePath))
                {
                    var cStore = CurrentBaselineStore.Load(currentBaselinePath);
                    CurrentBaseline cbl;
                    // 优先按方向精确查找，降级到无方向旧格式，再降级到任意方向
                    string currentDir = currentEvent != null ? currentEvent.Direction : null;
                    if (!cStore.Switches.TryGetValue(CurrentBaselineStore.MakeKey(_selectedSwitchId, currentDir), out cbl))
                    {
                        if (!cStore.Switches.TryGetValue(_selectedSwitchId, out cbl))
                        {
                            // 再降级：方向未知/不匹配时，取该道岔的任意一条基线
                            string prefix = _selectedSwitchId + "|";
                            foreach (var kv in cStore.Switches)
                            {
                                if (kv.Key == _selectedSwitchId || kv.Key.StartsWith(prefix))
                                {
                                    cbl = kv.Value;
                                    break;
                                }
                            }
                        }
                    }
                    if (cbl != null)
                    {
                        currentBaseline = new
                        {
                            refSpikePeakA = cbl.RefSpikePeakA,
                            refSpikeIndexA = cbl.RefSpikeIndexA,
                            refUnlockMeanA = cbl.RefUnlockMeanA,
                            refConvMeanA = cbl.RefConvMeanA,
                            refLockMeanA = cbl.RefLockMeanA,
                            refTailMeanA = cbl.RefTailMeanA,
                            refSpikePeakB = cbl.RefSpikePeakB,
                            refSpikeIndexB = cbl.RefSpikeIndexB,
                            refUnlockMeanB = cbl.RefUnlockMeanB,
                            refConvMeanB = cbl.RefConvMeanB,
                            refLockMeanB = cbl.RefLockMeanB,
                            refTailMeanB = cbl.RefTailMeanB,
                            refSpikePeakC = cbl.RefSpikePeakC,
                            refSpikeIndexC = cbl.RefSpikeIndexC,
                            refUnlockMeanC = cbl.RefUnlockMeanC,
                            refConvMeanC = cbl.RefConvMeanC,
                            refLockMeanC = cbl.RefLockMeanC,
                            refTailMeanC = cbl.RefTailMeanC,
                            refDurationSec = cbl.RefDurationSec,
                            refMaxUnbalanceRatio = cbl.RefMaxUnbalanceRatio,
                            sampleCount = cbl.SampleCount
                        };
                    }
                }
            }
            catch
            {
                // 电流基线加载失败不中断主流程
                currentBaseline = null;
            }

            // D8: 检查当前转辙机两个方向的参考曲线/标准曲线设定状态
            object refCurveStatus = null;
            try
            {
                string rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.Diagnosis.RulesDir);
                string refDir = Path.Combine(rulesDir, "reference_curves");
                string scDir = Path.Combine(_indexManager.ParsedDataDir, "standard_curves");
                string currentRefDir = Path.Combine(rulesDir, "current_reference_curves");
                string currentScDir = Path.Combine(_indexManager.ParsedDataDir, "current_standard_curves");
                string[] directions = { "定位→反位", "反位→定位" };
                var serializer = new JavaScriptSerializer();
                var items = new List<object>();
                foreach (var dir in directions)
                {
                    string refFileName = ReferenceCurveStore.MakeFileName(_selectedSwitchId, dir);
                    string scFileName = StandardCurveStore.MakeFileName(_selectedSwitchId, dir);
                    string refPath = Path.Combine(refDir, refFileName);
                    string refSource = "";
                    if (File.Exists(refPath))
                    {
                        try
                        {
                            var dict = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(refPath, Encoding.UTF8));
                            object src;
                            if (dict != null && dict.TryGetValue("Source", out src) && src != null)
                                refSource = src.ToString();
                        }
                        catch { /* 解析失败不影响主流程 */ }
                    }
                    // 检查电流参考曲线是否存在（任一相有即算有）
                    bool hasCurrentReference = false;
                    foreach (var ph in new[] { "A", "B", "C" })
                    {
                        if (File.Exists(Path.Combine(currentRefDir,
                            PhaseCurrentReferenceCurveStore.MakeFileName(_selectedSwitchId, dir, ph))))
                        { hasCurrentReference = true; break; }
                    }
                    // 检查电流标准曲线是否存在
                    bool hasCurrentStandard = false;
                    foreach (var ph in new[] { "A", "B", "C" })
                    {
                        if (File.Exists(Path.Combine(currentScDir,
                            PhaseCurrentStandardCurveStore.MakeFileName(_selectedSwitchId, dir, ph))))
                        { hasCurrentStandard = true; break; }
                    }
                    items.Add(new
                    {
                        direction = dir,
                        hasReference = File.Exists(refPath),
                        refSource = refSource,
                        hasStandard = File.Exists(Path.Combine(scDir, scFileName)),
                        hasCurrentReference = hasCurrentReference,
                        hasCurrentStandard = hasCurrentStandard
                    });
                }
                refCurveStatus = new { directions = items.ToArray() };
            }
            catch { /* 检查失败不影响主流程 */ }

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
                colors = _config.ChartColors,
                diagnosis = diagnosis,
                refCurve = refCurve,
                standardCurve = standardCurve,
                refCurves = refCurves,           // 所有方向的参考曲线数组
                standardCurves = standardCurves, // 所有方向的标准曲线数组
                currentRefCurves = currentRefCurves,           // 电流分相参考曲线
                currentStandardCurves = currentStandardCurves, // 电流分相标准曲线
                refCurveStatus = refCurveStatus,
                baseline = baseline,
                currentBaseline = currentBaseline
            };

            string chartDataJson = _serializer.Serialize(chartData);
            InvokeChartScript("loadChartData", chartDataJson);

            // 有数据时启用导出按钮
            SetExportButtonEnabled(true);

            Logger.Info(string.Format("加载曲线: {0} / {1} / {2} (当前:{3}, 上一:{4})",
                _selectedSwitchId, _selectedDate, UnixToTimeString(_selectedTimestamp),
                currentEvent != null ? currentEvent.Direction : "无",
                prevEvent != null ? prevEvent.Direction : "无"));
        }

        /// <summary>
        /// 将 SwitchEvent 转为给 JS 使用的 JSON 对象
        /// </summary>
        private object BuildEventJson(SwitchEvent evt)
        {
            if (evt == null)
                return null;

            // 构建 [[t, v], ...] 格式的采样数组（CurrentA 已是 double[] {t, v} 对）
            var currentA = new object[evt.SampleCount];
            var currentB = new object[evt.SampleCount];
            var currentC = new object[evt.SampleCount];
            var power = new object[evt.SampleCount];

            for (int i = 0; i < evt.SampleCount; i++)
            {
                double t = Math.Round(i * evt.SampleInterval, 3);
                currentA[i] = i < evt.CurrentA.Count ? evt.CurrentA[i] : (object)new double[] { t, 0 };
                currentB[i] = i < evt.CurrentB.Count ? evt.CurrentB[i] : (object)new double[] { t, 0 };
                currentC[i] = i < evt.CurrentC.Count ? evt.CurrentC[i] : (object)new double[] { t, 0 };
                power[i] = i < evt.Power.Count ? evt.Power[i] : (object)new double[] { t, 0 };
            }

            // 计算阶段边界时间（供前端基线分段显示）
            object phases = null;
            try
            {
                var feats = FeatureExtractor.Extract(evt);
                if (feats.IsValid)
                {
                    int n = feats.SampleCount;
                    double interval = evt.SampleInterval;
                    int si = feats.SpikeIndex;
                    int ae = feats.ActiveEnd;

                    // 尖峰：spikeIndex 附近放宽 ±2 采样点，截止到解锁段起点
                    double spikeStart = Math.Round(Math.Max(0, si - 2) * interval, 3);
                    double spikeEnd = Math.Round((si + 2) * interval, 3);

                    // 解锁段：[si+2, min(si+14, n))
                    int ulEnd = Math.Min(si + 14, n);
                    double unlockStart = Math.Round((si + 2) * interval, 3);
                    double unlockEnd = Math.Round(ulEnd * interval, 3);

                    // 转换段：首选 [si+20, ae-40)，退化为 [si+2, ae)
                    int convStart, convEnd;
                    if (ae - 40 > si + 20)
                    {
                        convStart = si + 20;
                        convEnd = ae - 40;
                    }
                    else
                    {
                        convStart = si + 2;
                        convEnd = ae;
                    }
                    if (convStart >= convEnd)
                    {
                        convStart = 0;
                        convEnd = ae + 1;
                    }
                    double convStartSec = Math.Round(convStart * interval, 3);
                    double convEndSec = Math.Round(Math.Min(convEnd, n) * interval, 3);

                    // 锁闭段：[ae-40, ae-22)，ae≤50 时无锁闭段
                    double lockStartSec = 0.0, lockEndSec = 0.0;
                    if (ae > 50)
                    {
                        int lockStart = ae - 40;
                        if (lockStart < 0) lockStart = 0;
                        int lockEnd = ae - 22;
                        lockStartSec = Math.Round(lockStart * interval, 3);
                        lockEndSec = Math.Round(lockEnd * interval, 3);
                    }

                    // 缓放段：[ae-22, ae-2)，ae≤30 时无缓放段
                    double tailStartSec = 0.0, tailEndSec = 0.0;
                    if (ae > 30)
                    {
                        int tailStart = Math.Max(0, ae - 22);
                        int tailEnd = ae - 2;
                        tailStartSec = Math.Round(tailStart * interval, 3);
                        tailEndSec = Math.Round(tailEnd * interval, 3);
                    }

                    phases = new
                    {
                        spikeStartSec = spikeStart,
                        spikeEndSec = spikeEnd,
                        unlockStartSec = unlockStart,
                        unlockEndSec = unlockEnd,
                        convStartSec = convStartSec,
                        convEndSec = convEndSec,
                        lockStartSec = lockStartSec,
                        lockEndSec = lockEndSec,
                        tailStartSec = tailStartSec,
                        tailEndSec = tailEndSec
                    };
                }
            }
            catch
            {
                phases = null;
            }

            // D7: 计算电流曲线每相阶段边界（供前端电流基线分段显示）
            object currentPhases = null;
            try
            {
                // 从 evt 的原始 [t,v] 对中提取 v 列
                var rawA = new List<double>();
                var rawB = new List<double>();
                var rawC = new List<double>();
                foreach (var pair in evt.CurrentA)
                    rawA.Add(pair != null && pair.Length >= 2 ? pair[1] : 0.0);
                foreach (var pair in evt.CurrentB)
                    rawB.Add(pair != null && pair.Length >= 2 ? pair[1] : 0.0);
                foreach (var pair in evt.CurrentC)
                    rawC.Add(pair != null && pair.Length >= 2 ? pair[1] : 0.0);

                var cfA = CurrentFeatureExtractor.ExtractPhase(rawA);
                var cfB = CurrentFeatureExtractor.ExtractPhase(rawB);
                var cfC = CurrentFeatureExtractor.ExtractPhase(rawC);

                double interval = evt.SampleInterval;
                int n = evt.SampleCount;

                currentPhases = new
                {
                    A = BuildCurrentPhaseSegments(cfA.SpikeIndexA, cfA.UnlockEndA, cfA.LockStartA, cfA.ActiveEnd, interval, n),
                    B = BuildCurrentPhaseSegments(cfB.SpikeIndexA, cfB.UnlockEndA, cfB.LockStartA, cfB.ActiveEnd, interval, n),
                    C = BuildCurrentPhaseSegments(cfC.SpikeIndexA, cfC.UnlockEndA, cfC.LockStartA, cfC.ActiveEnd, interval, n)
                };
            }
            catch
            {
                currentPhases = null;
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
                power = power,
                phases = phases,
                currentPhases = currentPhases
            };
        }

        /// <summary>
        /// 清空图表
        /// </summary>
        private void ClearCharts()
        {
            InvokeChartScript("clearCharts", null);

            // 清空图表时禁用导出按钮
            SetExportButtonEnabled(false);
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

        /// <summary>
        /// 使用 FeatureExtractor 检测的物理边界索引计算阶段边界时间。
        /// </summary>
        private static object BuildCurrentPhaseSegments(int spikeIndex, int unlockEnd, int lockStart,
            int activeEnd, double interval, int n)
        {
            // ① 尖峰段：[0, spikeIndex+2]
            double spikeEnd = Math.Round(Math.Min(spikeIndex + 2, n) * interval, 3);

            // ② 解锁段：[spikeIndex+2, unlockEnd]（物理边界）
            int ulEnd = Math.Max(spikeIndex + 2, unlockEnd > spikeIndex ? unlockEnd : Math.Min(spikeIndex + 14, n));
            double unlockStart = Math.Round((spikeIndex + 2) * interval, 3);
            double unlockEndSec = Math.Round(Math.Min(ulEnd, n) * interval, 3);

            // ③ 转换段：[unlockEnd+1, lockStart)（物理边界：解锁终点→密贴拐点）
            int convStart = (unlockEnd > spikeIndex) ? unlockEnd + 1 : spikeIndex + 14;
            int convEnd = lockStart > convStart ? lockStart : activeEnd;
            double convStartSec = Math.Round(convStart * interval, 3);
            double convEndSec = Math.Round(Math.Min(convEnd, n) * interval, 3);

            // ④ 锁闭段：[lockStart, activeEnd-22)（密贴拐点→预估锁闭终点）
            double lockStartSec = 0.0, lockEndSec = 0.0;
            if (lockStart > 0 && lockStart < activeEnd && activeEnd > 50)
            {
                lockStartSec = Math.Round(lockStart * interval, 3);
                lockEndSec = Math.Round(Math.Min(activeEnd - 22, n) * interval, 3);
            }

            // ⑤ 缓放段：[activeEnd-22, activeEnd-2)
            double tailStartSec = 0.0, tailEndSec = 0.0;
            if (activeEnd > 30)
            {
                int tailStart = Math.Max(0, activeEnd - 22);
                int tailEnd = activeEnd - 2;
                tailStartSec = Math.Round(tailStart * interval, 3);
                tailEndSec = Math.Round(tailEnd * interval, 3);
            }

            return new
            {
                spikeStartSec = 0.0,
                spikeEndSec = spikeEnd,
                unlockStartSec = unlockStart,
                unlockEndSec = unlockEndSec,
                convStartSec = convStartSec,
                convEndSec = convEndSec,
                lockStartSec = lockStartSec,
                lockEndSec = lockEndSec,
                tailStartSec = tailStartSec,
                tailEndSec = tailEndSec
            };
        }

        #endregion

        #region WebBrowser JS 调用辅助

        /// <summary>
        /// 向侧边栏 WebBrowser 调用 JS 函数
        /// 用 InvokeScript 直接将 JSON 字符串作为 COM 参数传递
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
                Logger.Error("InvokeSidebarScript 错误: " + functionName + " | " + ex.Message);
            }
        }

        /// <summary>
        /// 向图表 WebBrowser 调用 JS 函数
        /// 用 InvokeScript 直接将 JSON 字符串作为 COM 参数传递
        /// </summary>
        private void InvokeChartScript(string functionName, string jsonArg)
        {
            try
            {
                if (chartBrowser.Document != null)
                {
                    int dataLen = jsonArg != null ? jsonArg.Length : 0;
                    Logger.Info(string.Format("InvokeChartScript: {0}, 数据长度={1}, ReadyState={2}",
                        functionName, dataLen, chartBrowser.ReadyState));
                    chartBrowser.Document.InvokeScript(functionName, new object[] { jsonArg });
                    Logger.Info("InvokeChartScript 完成: " + functionName);
                }
                else
                {
                    Logger.Error("InvokeChartScript 失败: Document 为 null, functionName=" + functionName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("InvokeChartScript 错误: " + functionName + " | " + ex.Message);
            }
        }

        #endregion

        #region 状态栏

        private void UpdateStatusBar()
        {
            string text = "就绪";
            if (!string.IsNullOrEmpty(_selectedSwitchId))
            {
                string timeStr = _selectedTimestamp > 0 ? UnixToTimeString(_selectedTimestamp) : "-";
                text = string.Format("{0} | {1} | {2}",
                    GetSwitchLabel(_selectedSwitchId),
                    _selectedDate ?? "-",
                    timeStr);

                // D5: 追加当前事件诊断级别
                if (_selectedTimestamp > 0 && !string.IsNullOrEmpty(_selectedDate))
                {
                    try
                    {
                        var dayDiagnoses = _indexManager.LoadDayDiagnosis(_selectedSwitchId, _selectedDate);
                        foreach (var d in dayDiagnoses)
                        {
                            if (d.Timestamp == _selectedTimestamp)
                            {
                                if (d.Level != "正常")
                                    text += " | " + d.Level;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // 诊断数据缺失时不报错
                    }
                }
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
        /// HTML: window.external.SelectSite("sanshuibei")
        /// </summary>
        public void SelectSite(string siteId)
        {
            _form.BeginInvoke(new Action(() => _form.OnSiteSelected(siteId)));
        }

        /// <summary>
        /// HTML: window.external.SelectSwitch("1-J")
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

        /// <summary>
        /// HTML: window.external.OpenChartDetail("chart1", "{...}")
        /// </summary>
        public void OpenChartDetail(string chartKey, string dataJson)
        {
            _form.BeginInvoke(new Action(() =>
                _form.OnOpenChartDetail(chartKey, dataJson)));
        }

        /// <summary>
        /// HTML: window.external.SetReferenceCurve("1-J", "[3.39,3.25,...]", "定位→反位", 0.5)
        /// 保存参考曲线并自动生成标准曲线（使用指定的融合权重）。
        /// </summary>
        public void SetReferenceCurve(string switchId, string powerJson, string direction, double fusionWeight)
        {
            _form.BeginInvoke(new Action(() =>
                _form.OnSetReferenceCurve(switchId, powerJson, direction, fusionWeight)));
        }

        /// <summary>
        /// HTML: window.external.CancelReferenceCurve("1-J", "定位→反位", "[3.39,3.25,...]", 0.5)
        /// 取消人工设定的参考曲线：仅删除人工参考曲线文件，然后基于当前事件功率+基线重建标准曲线。
        /// </summary>
        public void CancelReferenceCurve(string switchId, string direction, string powerJson, double fusionWeight)
        {
            _form.BeginInvoke(new Action(() =>
                _form.OnCancelReferenceCurve(switchId, direction, powerJson, fusionWeight)));
        }

        /// <summary>
        /// HTML: window.external.SetFusionWeight("1-J", "定位→反位", 0.5)
        /// 仅调整融合权重，使用已有参考曲线文件重建标准曲线。
        /// </summary>
        public void SetFusionWeight(string switchId, string direction, double weight)
        {
            _form.BeginInvoke(new Action(() =>
                _form.OnSetFusionWeight(switchId, direction, weight)));
        }

        /// <summary>
        /// HTML: window.external.CatchupData("SSB")
        /// N01-4: 手动补拉数据。
        /// </summary>
        public void CatchupData(string siteId)
        {
            _form.BeginInvoke(new Action(() =>
                _form.CatchupData(siteId)));
        }

        /// <summary>
        /// HTML: window.external.OpenCleanupDialog()
        /// N01-4: 打开数据清理对话框。
        /// </summary>
        public void OpenCleanupDialog()
        {
            _form.BeginInvoke(new Action(() =>
                _form.OnCleanupClicked(null, EventArgs.Empty)));
        }
    }

    #endregion

    #region 辅助类 — 深色工具栏渲染 / COM 截图接口

    /// <summary>
    /// 深色主题的 ToolStrip 渲染器
    /// </summary>
    internal class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer()
            : base(new DarkProfessionalColorTable())
        {
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // 不绘制边框，与深色背景融为一体
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(200, 200, 200);
            base.OnRenderArrow(e);
        }
    }

    /// <summary>
    /// 深色主题的颜色表
    /// </summary>
    internal class DarkProfessionalColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Color.FromArgb(36, 36, 56);
        public override Color ToolStripGradientMiddle => Color.FromArgb(36, 36, 56);
        public override Color ToolStripGradientEnd => Color.FromArgb(36, 36, 56);
        public override Color MenuStripGradientBegin => Color.FromArgb(30, 30, 50);
        public override Color MenuStripGradientEnd => Color.FromArgb(30, 30, 50);
        public override Color MenuItemSelected => Color.FromArgb(80, 80, 120);
        public override Color MenuItemBorder => Color.FromArgb(60, 60, 90);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(60, 60, 100);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(60, 60, 100);
        public override Color ButtonSelectedGradientBegin => Color.FromArgb(80, 80, 120);
        public override Color ButtonSelectedGradientMiddle => Color.FromArgb(80, 80, 120);
        public override Color ButtonSelectedGradientEnd => Color.FromArgb(80, 80, 120);
        public override Color ButtonPressedGradientBegin => Color.FromArgb(60, 60, 100);
        public override Color ButtonPressedGradientMiddle => Color.FromArgb(60, 60, 100);
        public override Color ButtonPressedGradientEnd => Color.FromArgb(60, 60, 100);
        public override Color ButtonCheckedGradientBegin => Color.FromArgb(100, 100, 140);
        public override Color ButtonCheckedGradientMiddle => Color.FromArgb(100, 100, 140);
        public override Color ButtonCheckedGradientEnd => Color.FromArgb(100, 100, 140);
        public override Color SeparatorDark => Color.FromArgb(60, 60, 90);
        public override Color SeparatorLight => Color.FromArgb(80, 80, 110);
        public override Color ImageMarginGradientBegin => Color.FromArgb(36, 36, 56);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(36, 36, 56);
        public override Color ImageMarginGradientEnd => Color.FromArgb(36, 36, 56);
    }

    /// <summary>
    /// IViewObject COM 接口 — 用于捕获 WebBrowser ActiveX 控件渲染内容
    /// </summary>
    [ComImport]
    [Guid("0000010D-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IViewObject
    {
        [PreserveSig]
        int Draw(
            [MarshalAs(UnmanagedType.U4)] int dwDrawAspect,
            int lindex,
            IntPtr pvAspect,
            [In] IntPtr ptd,
            IntPtr hdcTargetDev,
            IntPtr hdcDraw,
            [In] ref COMRECT lprcBounds,
            [In] IntPtr lprcWBounds,
            IntPtr pfnContinue,
            int dwContinue);

        int GetColorSet(int dwDrawAspect, int lindex, IntPtr pvAspect,
            [In] IntPtr ptd, IntPtr hdcTargetDev, out IntPtr ppColorSet);

        int Freeze(int dwDrawAspect, int lindex, IntPtr pvAspect, out IntPtr pdwFreeze);

        int Unfreeze(int dwFreeze);

        void SetAdvise(int aspects, int advf,
            [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IAdviseSink pAdvSink);

        void GetAdvise(out int pAspects, out int pAdvf,
            [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IAdviseSink pAdvSink);
    }

    /// <summary>
    /// COM 矩形结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct COMRECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    #endregion
}
