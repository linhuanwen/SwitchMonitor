using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SwitchMonitor.Common;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 道岔监测主窗口。
    /// 左侧：动作列表（DataGridView），右侧：曲线图表（CurveChartPanel）。
    /// </summary>
    public class MainForm : Form
    {
        // ---- UI 控件 ----
        private SplitContainer _splitContainer;
        private DataGridView _actionGrid;
        private CurveChartPanel _curvePanel;
        private ToolStrip _toolStrip;
        private MenuStrip _menuStrip;
        private ToolStripButton _btnCurrent;
        private ToolStripButton _btnVoltage;
        private ToolStripButton _btnPower;
        private ToolStripButton _btnExportPng;
        private ToolStripButton _btnExportCsv;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel;
        private ContextMenuStrip _actionGridContextMenu;

        // ---- 筛选控件 ----
        private ToolStrip _filterStrip;
        private ToolStripComboBox _switchCombo;
        private DateTimePicker _dtpFrom;
        private DateTimePicker _dtpTo;
        private ToolStripButton _btnQuery;
        private ToolStripButton _btnClear;
        private Timer _debounceTimer;

        // ---- 系列显隐复选框 ----
        private ToolStripButton _chkPhaseA;
        private ToolStripButton _chkPhaseB;
        private ToolStripButton _chkPhaseC;
        private ToolStripButton _chkThreshold;
        private ToolStripButton _chkHoldRange;

        // ---- 数据 ----
        private QueryService _queryService;
        private List<SwitchActionRecord> _actions;
        private List<CurveSampleRecord> _currentSamples;
        private List<string> _allSwitchIds;
        private MappingConfig _mappingConfig;
        // switchId → displayName 映射缓存（用于下拉框反向查找）
        private Dictionary<string, string> _switchDisplayNames;

        // ---- 配置 ----
        private ConfigManager _configManager;

        // ---- 状态时间线 ----
        private StatusTimelinePanel _statusTimeline;
        private SwitchActionRecord _currentAction;  // 当前查看的动作

        // ---- 诊断引擎 ----
        private IDiagnosisEngine _diagnosisEngine;
        private DiagnosisResultPanel _diagnosisPanel;

        // ---- 当前显示模式 ----
        private enum DisplayMode { Current = 0, Voltage = 1, Power = 2 }
        private DisplayMode _mode = DisplayMode.Current;

        // ---- 异步加载取消令牌 ----
        private int _loadToken = 0;  // 每次选中动作时递增，用于取消旧加载

        /// <summary>
        /// 指定 SQLite 数据库路径和映射配置构造主窗口
        /// </summary>
        public MainForm(string dbPath, MappingConfig mappingConfig, ConfigManager configManager = null)
        {
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.log"),
                string.Format("[{0}] MainForm ctor, dbPath=[{1}], exists={2}\n",
                DateTime.Now, dbPath, System.IO.File.Exists(dbPath)));
            _queryService = new QueryService(dbPath);
            _mappingConfig = mappingConfig ?? MappingConfig.CreateDefault();
            _switchDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _configManager = configManager ?? new ConfigManager(
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "config.json")));
            InitializeComponents();
            InitializeDiagnosisEngine();
            LoadData();
        }

        /// <summary>
        /// 初始化诊断引擎，加载 Rules/ 目录下的规则配置。
        /// 如果初始化失败，诊断面板将显示错误提示但不影响主程序运行。
        /// </summary>
        private void InitializeDiagnosisEngine()
        {
            try
            {
                _diagnosisEngine = new DiagnosisEngine.DiagnosisEngine();
                string rulesPath = FindRulesPath();
                if (!string.IsNullOrEmpty(rulesPath))
                {
                    _diagnosisEngine.Initialize(rulesPath);
                    _statusLabel.Text = "诊断引擎已就绪";
                }
            }
            catch (Exception ex)
            {
                _diagnosisPanel?.SetResults(new List<DiagnosisResult>
                {
                    new DiagnosisResult
                    {
                        RuleName = "初始化失败",
                        Level = DiagnosisLevel.Normal,
                        Description = "诊断引擎初始化失败: " + ex.Message,
                    }
                });
            }
        }

        /// <summary>
        /// 查找 Rules/ 目录路径。
        /// </summary>
        private static string FindRulesPath()
        {
            var candidates = new[]
            {
                // net40 输出目录 (bin/x86/Debug/net40/) 向上 5 级到项目根
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Rules")),
                // net8.0-windows 输出目录 向上 4 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Rules")),
                // bin/Debug 向上 3 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Rules")),
                // bin 目录向上 1 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Rules")),
                // 当前目录
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rules")),
            };

            foreach (var path in candidates)
            {
                if (Directory.Exists(path))
                    return path;
            }
            return null;
        }

        private void InitializeComponents()
        {
            // 窗体属性
            this.Text = "道岔监测系统 - SwitchMonitor";
            this.Size = new Size(1200, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(800, 500);
            this.Font = new Font("宋体", 9f);

            // === 菜单栏 ===
            _menuStrip = new MenuStrip();
            var refMenu = new ToolStripMenuItem("参考曲线管理");
            refMenu.Click += OnReferenceCurveManagementClick;
            _menuStrip.Items.Add(refMenu);

            // "报警阈值设置" 菜单项
            var alarmMenu = new ToolStripMenuItem("报警阈值设置");
            alarmMenu.Click += OnAlarmThresholdClick;
            _menuStrip.Items.Add(alarmMenu);

            // "重新加载配置" 菜单项
            var reloadMenu = new ToolStripMenuItem("重新加载配置");
            reloadMenu.Click += OnReloadMappingConfigClick;
            _menuStrip.Items.Add(reloadMenu);

            // === 工具栏 ===
            _toolStrip = new ToolStrip();
            _btnCurrent = new ToolStripButton("电流", null, OnPhaseSwitchClick, "btnCurrent")
            {
                CheckOnClick = true, Checked = true
            };
            _btnVoltage = new ToolStripButton("电压", null, OnPhaseSwitchClick, "btnVoltage")
            {
                CheckOnClick = true
            };
            _btnPower = new ToolStripButton("功率", null, OnPhaseSwitchClick, "btnPower")
            {
                CheckOnClick = true
            };
            _toolStrip.Items.Add(new ToolStripLabel("显示:"));
            _toolStrip.Items.Add(_btnCurrent);
            _toolStrip.Items.Add(_btnVoltage);
            _toolStrip.Items.Add(_btnPower);
            _toolStrip.Items.Add(new ToolStripSeparator());

            // 导出按钮
            _btnExportPng = new ToolStripButton("导出图片", null, OnExportPngClick, "btnExportPng")
            {
                Enabled = false,
                ToolTipText = "将当前图表导出为 PNG 图片",
            };
            _btnExportCsv = new ToolStripButton("导出CSV", null, OnExportCsvClick, "btnExportCsv")
            {
                Enabled = false,
                ToolTipText = "将当前曲线数据导出为 CSV 文件",
            };
            _toolStrip.Items.Add(_btnExportPng);
            _toolStrip.Items.Add(_btnExportCsv);
            _toolStrip.Items.Add(new ToolStripSeparator());

            // 系列显隐复选框
            _toolStrip.Items.Add(new ToolStripLabel("系列:"));
            _chkPhaseA = new ToolStripButton("A相", null, OnSeriesCheckClick, "chkPhaseA")
            {
                CheckOnClick = true, Checked = true,
                ToolTipText = "显示/隐藏 A 相电流曲线",
            };
            _chkPhaseB = new ToolStripButton("B相", null, OnSeriesCheckClick, "chkPhaseB")
            {
                CheckOnClick = true, Checked = true,
                ToolTipText = "显示/隐藏 B 相电流曲线",
            };
            _chkPhaseC = new ToolStripButton("C相", null, OnSeriesCheckClick, "chkPhaseC")
            {
                CheckOnClick = true, Checked = true,
                ToolTipText = "显示/隐藏 C 相电流曲线",
            };
            _toolStrip.Items.Add(_chkPhaseA);
            _toolStrip.Items.Add(_chkPhaseB);
            _toolStrip.Items.Add(_chkPhaseC);
            _toolStrip.Items.Add(new ToolStripSeparator());

            _chkThreshold = new ToolStripButton("报警上限", null, OnSeriesCheckClick, "chkThreshold")
            {
                CheckOnClick = true, Checked = true,
                ToolTipText = "显示/隐藏报警上限阈值线",
            };
            _chkHoldRange = new ToolStripButton("保持量程", null, OnSeriesCheckClick, "chkHoldRange")
            {
                CheckOnClick = true, Checked = false,
                ToolTipText = "固定 Y 轴量程 / 自适应",
            };
            _toolStrip.Items.Add(_chkThreshold);
            _toolStrip.Items.Add(_chkHoldRange);
            _toolStrip.Items.Add(new ToolStripSeparator());

            _toolStrip.Items.Add(new ToolStripLabel("点击列表项查看曲线"));

            // === 筛选工具栏 ===
            _filterStrip = new ToolStrip();

            // 道岔下拉
            _filterStrip.Items.Add(new ToolStripLabel("道岔:"));
            _switchCombo = new ToolStripComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
            };
            _switchCombo.SelectedIndexChanged += OnFilterSwitchChanged;
            _filterStrip.Items.Add(_switchCombo);

            // 起始时间
            _filterStrip.Items.Add(new ToolStripLabel("  从:"));
            _dtpFrom = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd HH:mm:ss",
                Width = 150,
                MinDate = new DateTime(2000, 1, 1),
                MaxDate = new DateTime(2099, 12, 31),
            };
            var fromHost = new ToolStripControlHost(_dtpFrom) { Width = 155 };
            _filterStrip.Items.Add(fromHost);

            // 结束时间
            _filterStrip.Items.Add(new ToolStripLabel("  到:"));
            _dtpTo = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd HH:mm:ss",
                Width = 150,
                MinDate = new DateTime(2000, 1, 1),
                MaxDate = new DateTime(2099, 12, 31),
            };
            var toHost = new ToolStripControlHost(_dtpTo) { Width = 155 };
            _filterStrip.Items.Add(toHost);

            // 查询按钮
            _btnQuery = new ToolStripButton("查询", null, OnQueryClick, "btnQuery");
            _filterStrip.Items.Add(new ToolStripSeparator());
            _filterStrip.Items.Add(_btnQuery);

            // 清除筛选按钮
            _btnClear = new ToolStripButton("清除筛选", null, OnClearClick, "btnClear");
            _filterStrip.Items.Add(_btnClear);

            // 自动查询 debounce Timer (500ms)
            _debounceTimer = new Timer { Interval = 500 };
            _debounceTimer.Tick += OnDebounceTimerTick;

            // === 状态栏 ===
            _statusStrip = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("就绪 — 请选择转辙机查看曲线");
            _statusStrip.Items.Add(_statusLabel);

            // === 分栏容器 ===
            _splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
            };
            this.Load += (s, e) =>
            {
                _splitContainer.Panel1MinSize = 200;
                _splitContainer.Panel2MinSize = 200;
                _splitContainer.SplitterDistance = this.ClientSize.Width / 3;
            };

            // === 左侧：动作列表 ===
            _actionGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
            };
            _actionGrid.SelectionChanged += OnActionSelected;
            _actionGrid.CellMouseClick += OnActionGridCellMouseClick;

            // 右键菜单
            _actionGridContextMenu = new ContextMenuStrip();
            var setRefItem = new ToolStripMenuItem("设为参考曲线");
            setRefItem.Click += OnSetReferenceCurveClick;
            _actionGridContextMenu.Items.Add(setRefItem);
            var clearRefItem = new ToolStripMenuItem("清除参考曲线");
            clearRefItem.Click += OnClearReferenceCurveClick;
            _actionGridContextMenu.Items.Add(clearRefItem);
            _actionGrid.ContextMenuStrip = _actionGridContextMenu;

            // 定义列
            _actionGrid.Columns.Add("StartTimeDisplay", "时间");
            _actionGrid.Columns.Add("SwitchId", "道岔");
            _actionGrid.Columns.Add("Direction", "方向");
            _actionGrid.Columns.Add("SampleCount", "采样点数");

            _actionGrid.Columns["StartTimeDisplay"].FillWeight = 40;
            _actionGrid.Columns["SwitchId"].FillWeight = 15;
            _actionGrid.Columns["Direction"].FillWeight = 20;
            _actionGrid.Columns["SampleCount"].FillWeight = 10;

            _splitContainer.Panel1.Controls.Add(_actionGrid);

            // === 右侧：曲线图表 + 诊断面板 + 状态时间线 ===
            var rightPanel = new Panel { Dock = DockStyle.Fill };

            _curvePanel = new CurveChartPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
            };
            _curvePanel.ExportImageRequested += OnExportPngClick;
            _curvePanel.ExportCsvRequested += OnExportCsvClick;

            // 视口同步：曲线图表缩放/平移时同步状态时间线
            _curvePanel.ViewportChanged += OnCurveViewportChanged;

            // 诊断结果面板（在图表下方）
            _diagnosisPanel = new DiagnosisResultPanel
            {
                Dock = DockStyle.Bottom,
                Height = 120,
                BackColor = Color.FromArgb(250, 250, 250),
            };
            _diagnosisPanel.DiagnosisItemClicked += OnDiagnosisItemClicked;

            _statusTimeline = new StatusTimelinePanel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = Color.FromArgb(250, 250, 250),
            };
            _statusTimeline.HoverInfoChanged += OnStatusHoverChanged;
            _statusTimeline.RefreshPointLabels(_mappingConfig);

            rightPanel.Controls.Add(_curvePanel);
            rightPanel.Controls.Add(_diagnosisPanel);
            rightPanel.Controls.Add(_statusTimeline);

            _splitContainer.Panel2.Controls.Add(rightPanel);

            // === 布局 ===
            this.Controls.Add(_splitContainer);
            this.Controls.Add(_filterStrip);
            this.Controls.Add(_toolStrip);
            this.Controls.Add(_menuStrip);
            this.Controls.Add(_statusStrip);

            // Dock 顺序（先 Dock 的在上方）
            _menuStrip.Dock = DockStyle.Top;
            _toolStrip.Dock = DockStyle.Top;
            _filterStrip.Dock = DockStyle.Top;
            _splitContainer.Dock = DockStyle.Fill;
            _statusStrip.Dock = DockStyle.Bottom;

            // 确保 splitContainer 填充在 toolbar 下方
            _splitContainer.BringToFront();
        }

        private void LoadData()
        {
            try
            {
                _statusLabel.Text = "正在加载数据...";
                this.Cursor = Cursors.WaitCursor;

                // 构建 switchId → displayName 映射
                _switchDisplayNames.Clear();
                _allSwitchIds = _queryService.GetDistinctSwitchIds();
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.log"),
                    string.Format("[{0}] LoadData: GetDistinctSwitchIds returned {1} items\n",
                    DateTime.Now, _allSwitchIds.Count));
                foreach (var id in _allSwitchIds)
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.log"),
                        string.Format("[{0}] LoadData:   switchId=[{1}]\n", DateTime.Now, id));
                foreach (var id in _allSwitchIds)
                {
                    _switchDisplayNames[id] = _mappingConfig.GetSwitchName(id);
                }

                // 加载道岔列表（填充 ComboBox，显示映射后的可读名称）
                _switchCombo.Items.Clear();
                _switchCombo.Items.Add("全部");
                foreach (var id in _allSwitchIds)
                {
                    _switchCombo.Items.Add(_mappingConfig.GetSwitchName(id));
                }
                _switchCombo.SelectedIndex = 0; // 默认选"全部"

                // 设置时间选择器默认范围
                var allActions = _queryService.GetAllActions();
                if (allActions.Count > 0)
                {
                    var oldest = DateTimeHelper.FromUnixTimestamp(allActions[allActions.Count - 1].StartTime);
                    var newest = DateTimeHelper.FromUnixTimestamp(allActions[0].StartTime);
                    _dtpFrom.Value = oldest.Date;
                    _dtpTo.Value = newest.Date.AddDays(1).AddSeconds(-1);
                }

                // 加载全部数据到列表
                _actions = allActions;
                PopulateActionGrid();

                _statusLabel.Text = string.Format("已加载 {0} 条动作记录 | 车站: {1}",
                    _actions.Count, _mappingConfig.StationName);
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diag.log"),
                    string.Format("[{0}] LoadData EXCEPTION: {1}\n{2}\n",
                    DateTime.Now, ex.Message, ex.StackTrace));
                _statusLabel.Text = "加载失败: " + ex.Message;
                MessageBox.Show("加载数据失败:\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// 将 _actions 列表填充到 DataGridView
        /// </summary>
        private void PopulateActionGrid()
        {
            _actionGrid.Rows.Clear();

            if (_actions == null || _actions.Count == 0)
            {
                // 根据是否有筛选条件给出不同的提示
                if (_switchCombo.SelectedIndex > 0)
                    _statusLabel.Text = "该道岔无匹配记录";
                else
                    _statusLabel.Text = "无动作记录 — 请检查数据源或数据库";
                return;
            }

            foreach (var action in _actions)
            {
                string displayName = _mappingConfig.GetSwitchName(action.SwitchId);
                _actionGrid.Rows.Add(
                    action.StartTimeDisplay,
                    displayName,
                    action.Direction,
                    action.SampleCount
                );
            }
        }

        /// <summary>
        /// 执行筛选查询——读取控件值，调用 QueryService 并刷新列表
        /// </summary>
        private void ExecuteFilter()
        {
            try
            {
                _statusLabel.Text = "正在查询...";
                this.Cursor = Cursors.WaitCursor;

                // 解析道岔选择（从显示名反查原始 switchId）
                string switchId = null;
                if (_switchCombo.SelectedIndex > 0) // 0 = "全部"
                {
                    string displayName = _switchCombo.SelectedItem.ToString();
                    switchId = ResolveSwitchIdFromDisplay(displayName);
                }

                // 解析时间范围
                DateTime? from = null;
                DateTime? to = null;

                // 只在使用者修改了时间后才应用筛选（非默认值）
                // 如果起始时间不是当天 00:00:00，则认为用户设定了筛选
                if (_dtpFrom.Value > _dtpFrom.MinDate)
                {
                    from = _dtpFrom.Value;
                }
                if (_dtpTo.Value < _dtpTo.MaxDate.AddDays(-1))
                {
                    to = _dtpTo.Value;
                }

                _actions = _queryService.GetActions(switchId, from, to);

                // 清除当前曲线数据显示
                _currentSamples = null;
                _currentAction = null;
                _curvePanel.SetSamples(null, null, null);
                _statusTimeline.Clear();
                _diagnosisPanel.ClearResults();
                UpdateExportButtonState();

                PopulateActionGrid();

                if (_actions.Count > 0)
                {
                    string filterDesc = "";
                    if (!string.IsNullOrEmpty(switchId))
                        filterDesc += " 道岔=" + switchId;
                    if (from.HasValue || to.HasValue)
                        filterDesc += string.Format(" 时间[{0:yyyy-MM-dd HH:mm} ~ {1:yyyy-MM-dd HH:mm}]",
                            from ?? _dtpFrom.MinDate, to ?? _dtpTo.MaxDate);
                    _statusLabel.Text = string.Format("查询结果: {0} 条{1}", _actions.Count, filterDesc);
                }
                // 无结果时 PopulateActionGrid 已显示"无匹配记录"
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "查询失败: " + ex.Message;
                MessageBox.Show("查询失败:\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// 清除筛选条件，恢复显示全部数据
        /// </summary>
        private void ClearFilter()
        {
            // 重置 ComboBox
            _switchCombo.SelectedIndex = 0; // "全部"

            // 重置 DateTimePicker
            var allActions = _queryService.GetAllActions();
            if (allActions.Count > 0)
            {
                var oldest = DateTimeHelper.FromUnixTimestamp(allActions[allActions.Count - 1].StartTime);
                var newest = DateTimeHelper.FromUnixTimestamp(allActions[0].StartTime);
                _dtpFrom.Value = oldest.Date;
                _dtpTo.Value = newest.Date.AddDays(1).AddSeconds(-1);
            }

            // 重建下拉框（使用映射名称）
            RebuildSwitchCombo();

            // 重新加载全部数据
            _actions = allActions;
            _currentSamples = null;
            _currentAction = null;
            _curvePanel.SetSamples(null, null, null);
            _statusTimeline.Clear();
            _diagnosisPanel.ClearResults();
            UpdateExportButtonState();
            PopulateActionGrid();
            _statusLabel.Text = string.Format("已加载 {0} 条动作记录（筛选已清除） | 车站: {1}",
                _actions.Count, _mappingConfig.StationName);
        }

        /// <summary>
        /// 道岔切换时启动 debounce timer（500ms 后自动查询）
        /// </summary>
        private void OnFilterSwitchChanged(object sender, EventArgs e)
        {
            // 忽略初始化时的触发
            if (_switchCombo.SelectedIndex < 0)
                return;

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        /// <summary>
        /// debounce timer 到期，自动执行查询
        /// </summary>
        private void OnDebounceTimerTick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            ExecuteFilter();
        }

        /// <summary>
        /// 查询按钮点击
        /// </summary>
        private void OnQueryClick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            ExecuteFilter();
        }

        /// <summary>
        /// 清除筛选按钮点击
        /// </summary>
        private void OnClearClick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            ClearFilter();
        }

        private void OnActionSelected(object sender, EventArgs e)
        {
            if (_actionGrid.SelectedRows.Count == 0)
                return;

            int rowIndex = _actionGrid.SelectedRows[0].Index;
            if (rowIndex < 0 || rowIndex >= _actions.Count)
                return;

            var action = _actions[rowIndex];
            _currentAction = action;
            string switchDisplay = _mappingConfig.GetSwitchName(action.SwitchId);
            _statusLabel.Text = string.Format("正在加载 {0} 的曲线数据...", switchDisplay);

            // 取消上次未完成的加载（递增令牌）
            int myToken = System.Threading.Interlocked.Increment(ref _loadToken);

            // 显示加载状态
            _curvePanel.IsLoading = true;
            _curvePanel.ErrorMessage = null;

            // 异步加载数据，避免阻塞 UI
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                // 检查是否已被取消
                if (System.Threading.Interlocked.CompareExchange(ref _loadToken, 0, 0) != myToken)
                    return;

                List<CurveSampleRecord> samples = null;
                string errorMsg = null;
                try
                {
                    samples = _queryService.GetCurveSamples(action.Id);
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }

                // 回到 UI 线程更新界面
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        // 再次检查是否已被取消
                        if (System.Threading.Interlocked.CompareExchange(ref _loadToken, 0, 0) != myToken)
                            return;

                        _curvePanel.IsLoading = false;

                        if (errorMsg != null)
                        {
                            _curvePanel.ErrorMessage = "数据加载失败: " + errorMsg;
                            _statusLabel.Text = "加载曲线失败: " + errorMsg;
                            return;
                        }

                        _currentSamples = samples;
                        try
                        {
                            LoadCurveSamples();
                            UpdateExportButtonState();
                            LoadReferenceCurveForSwitch(action.SwitchId);
                            LoadStatusTimeline(action);
                            RunDiagnosis(action);

                            string date = null;
                            string time = null;
                            if (action.StartTime > 0)
                            {
                                var dt = SwitchMonitor.Common.DateTimeHelper.FromUnixTimestamp(action.StartTime);
                                date = dt.ToString("yyyy-MM-dd");
                                time = dt.ToString("HH:mm:ss");
                            }
                            _statusLabel.Text = FormatStatusText(switchDisplay, date, time, action.SampleCount)
                                + " | 显示: " + GetDisplayModeName();
                        }
                        catch (Exception innerEx)
                        {
                            _curvePanel.ErrorMessage = "渲染失败: " + innerEx.Message;
                            _statusLabel.Text = "渲染曲线失败: " + innerEx.Message;
                        }
                    }));
                }
            });
        }

        private void LoadCurveSamples()
        {
            if (_currentSamples == null || _currentSamples.Count == 0)
            {
                _curvePanel.SetSamples(null, null, null);
                _diagnosisPanel.ClearResults();
                return;
            }

            // 按相别分组的浮点值列表
            List<float> phaseA = null, phaseB = null, phaseC = null;

            switch (_mode)
            {
                case DisplayMode.Current:
                    phaseA = ExtractValues(_currentSamples, "A", s => s.Current);
                    phaseB = ExtractValues(_currentSamples, "B", s => s.Current);
                    phaseC = ExtractValues(_currentSamples, "C", s => s.Current);
                    _curvePanel.YAxisLabel = "电流 (A)";
                    break;
                case DisplayMode.Voltage:
                    phaseA = ExtractValues(_currentSamples, "A", s => s.Voltage);
                    phaseB = ExtractValues(_currentSamples, "B", s => s.Voltage);
                    phaseC = ExtractValues(_currentSamples, "C", s => s.Voltage);
                    _curvePanel.YAxisLabel = "电压 (V)";
                    break;
                case DisplayMode.Power:
                    phaseA = ExtractValues(_currentSamples, "A", s => s.Power);
                    phaseB = ExtractValues(_currentSamples, "B", s => s.Power);
                    phaseC = ExtractValues(_currentSamples, "C", s => s.Power);
                    _curvePanel.YAxisLabel = "功率 (kW)";
                    break;
            }

            _curvePanel.SetSamples(phaseA, phaseB, phaseC);
            // SetSamples 内部已调用 Invalidate()

            // 应用当前阈值配置到图表
            var thresholds = _configManager.GetAlarmThresholds();
            ApplyThresholdsToChart(thresholds);
        }

        private List<float> ExtractValues(List<CurveSampleRecord> samples, string phase,
            Func<CurveSampleRecord, float> selector)
        {
            var result = new List<float>();
            foreach (var s in samples)
            {
                if (s.Phase == phase)
                {
                    float val = selector(s);
                    // 如果主列（Current/Voltage/Power）为 0 但 RawValue 有值，回退到 RawValue
                    if (val == 0f && s.RawValue != 0f)
                        val = s.RawValue;
                    result.Add(val);
                }
            }
            return result;
        }

        private void OnPhaseSwitchClick(object sender, EventArgs e)
        {
            var btn = sender as ToolStripButton;
            if (btn == null || btn.Checked == false) return;

            // 互斥选中
            foreach (ToolStripItem item in _toolStrip.Items)
            {
                if (item is ToolStripButton b && b != btn)
                    b.Checked = false;
            }

            // 确定模式
            if (btn == _btnCurrent) _mode = DisplayMode.Current;
            else if (btn == _btnVoltage) _mode = DisplayMode.Voltage;
            else if (btn == _btnPower) _mode = DisplayMode.Power;

            LoadCurveSamples();
        }

        /// <summary>
        /// 系列显隐复选框 / 阈值线 / 量程保持 点击处理。
        /// 将工具栏复选框状态同步到曲线面板。
        /// </summary>
        private void OnSeriesCheckClick(object sender, EventArgs e)
        {
            var btn = sender as ToolStripButton;
            if (btn == null) return;

            // 同步所有复选框状态到 CurveChartPanel
            _curvePanel.SetPhaseVisibility(
                _chkPhaseA.Checked,
                _chkPhaseB.Checked,
                _chkPhaseC.Checked);
            _curvePanel.SetThresholdVisible(_chkThreshold.Checked);
            _curvePanel.SetYAxisFixed(_chkHoldRange.Checked);
        }

        private string GetDisplayModeName()
        {
            switch (_mode)
            {
                case DisplayMode.Current: return "电流";
                case DisplayMode.Voltage: return "电压";
                case DisplayMode.Power: return "功率";
                default: return "?";
            }
        }

        /// <summary>
        /// 更新导出按钮的启用状态（无数据时灰显）
        /// </summary>
        private void UpdateExportButtonState()
        {
            bool hasData = ExportService.HasExportableData(_currentSamples);
            _btnExportPng.Enabled = hasData;
            _btnExportCsv.Enabled = hasData;
        }

        /// <summary>
        /// 导出 PNG 图片按钮点击
        /// </summary>
        private void OnExportPngClick(object sender, EventArgs e)
        {
            if (!ExportService.HasExportableData(_currentSamples))
            {
                _statusLabel.Text = "无数据可导出";
                return;
            }

            // 获取当前选中的动作记录
            SwitchActionRecord currentAction = null;
            if (_actionGrid.SelectedRows.Count > 0)
            {
                int rowIndex = _actionGrid.SelectedRows[0].Index;
                if (rowIndex >= 0 && rowIndex < _actions.Count)
                    currentAction = _actions[rowIndex];
            }

            if (currentAction == null)
            {
                _statusLabel.Text = "请先选择一条动作记录";
                return;
            }

            string defaultName = ExportService.GenerateDefaultFileName(currentAction, "曲线", "png",
                _mappingConfig.GetSwitchName(currentAction.SwitchId));

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "导出曲线图片";
                sfd.Filter = "PNG 图片 (*.png)|*.png";
                sfd.DefaultExt = "png";
                sfd.FileName = defaultName;
                sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        ExportService.ExportChartToPng(_curvePanel, currentAction, sfd.FileName);
                        _statusLabel.Text = "已导出到: " + sfd.FileName;
                    }
                    catch (Exception ex)
                    {
                        _statusLabel.Text = "导出图片失败: " + ex.Message;
                    }
                }
            }
        }

        /// <summary>
        /// 导出 CSV 数据按钮点击
        /// </summary>
        private void OnExportCsvClick(object sender, EventArgs e)
        {
            if (!ExportService.HasExportableData(_currentSamples))
            {
                _statusLabel.Text = "无数据可导出";
                return;
            }

            // 获取当前选中的动作记录
            SwitchActionRecord currentAction = null;
            if (_actionGrid.SelectedRows.Count > 0)
            {
                int rowIndex = _actionGrid.SelectedRows[0].Index;
                if (rowIndex >= 0 && rowIndex < _actions.Count)
                    currentAction = _actions[rowIndex];
            }

            if (currentAction == null)
            {
                _statusLabel.Text = "请先选择一条动作记录";
                return;
            }

            string defaultName = ExportService.GenerateDefaultFileName(currentAction, "数据", "csv",
                _mappingConfig.GetSwitchName(currentAction.SwitchId));

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "导出曲线数据";
                sfd.Filter = "CSV 文件 (*.csv)|*.csv";
                sfd.DefaultExt = "csv";
                sfd.FileName = defaultName;
                sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        ExportService.ExportCsvToFile(_currentSamples, currentAction, sfd.FileName);
                        _statusLabel.Text = "已导出到: " + sfd.FileName;
                    }
                    catch (Exception ex)
                    {
                        _statusLabel.Text = "导出CSV失败: " + ex.Message;
                    }
                }
            }
        }

        // ================================================================
        // 参考曲线管理
        // ================================================================

        /// <summary>
        /// action grid 单元格右键点击 → 选中该行然后显示右键菜单
        /// </summary>
        private void OnActionGridCellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.RowIndex < _actions.Count)
            {
                _actionGrid.ClearSelection();
                _actionGrid.Rows[e.RowIndex].Selected = true;
            }
        }

        /// <summary>
        /// "设为参考曲线" 菜单项点击
        /// </summary>
        private void OnSetReferenceCurveClick(object sender, EventArgs e)
        {
            if (_actionGrid.SelectedRows.Count == 0)
                return;

            int rowIndex = _actionGrid.SelectedRows[0].Index;
            if (rowIndex < 0 || rowIndex >= _actions.Count)
                return;

            var action = _actions[rowIndex];

            // 确认对话框
            string switchDisplay = _mappingConfig.GetSwitchName(action.SwitchId);
            string msg = string.Format("是否将 {0} 于 {1} 的动作曲线设为参考曲线？",
                switchDisplay, action.StartTimeDisplay);
            var result = MessageBox.Show(this, msg, "设为参考曲线",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
                return;

            try
            {
                _queryService.SetReferenceCurve(action.SwitchId, action.Id, null);
                _statusLabel.Text = string.Format("已将 {0} 于 {1} 的曲线设为参考曲线",
                    switchDisplay, action.StartTimeDisplay);

                // 加载参考曲线叠加
                LoadReferenceCurveForSwitch(action.SwitchId);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "设置参考曲线失败: " + ex.Message;
                MessageBox.Show(this, "设置参考曲线失败:\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// "清除参考曲线" 菜单项点击
        /// </summary>
        private void OnClearReferenceCurveClick(object sender, EventArgs e)
        {
            if (_actionGrid.SelectedRows.Count == 0)
                return;

            int rowIndex = _actionGrid.SelectedRows[0].Index;
            if (rowIndex < 0 || rowIndex >= _actions.Count)
                return;

            var action = _actions[rowIndex];

            try
            {
                _queryService.ClearReferenceCurve(action.SwitchId);
                string switchDisplay = _mappingConfig.GetSwitchName(action.SwitchId);
                _statusLabel.Text = string.Format("已清除 {0} 的参考曲线", switchDisplay);

                // 清除图表上的参考曲线叠加
                _curvePanel.ClearReferenceSamples();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "清除参考曲线失败: " + ex.Message;
            }
        }

        /// <summary>
        /// "报警阈值设置" 菜单项点击 → 打开阈值配置对话框
        /// </summary>
        private void OnAlarmThresholdClick(object sender, EventArgs e)
        {
            using (var form = new AlarmThresholdForm(_configManager))
            {
                form.ThresholdsChanged += OnThresholdsChanged;
                form.ShowDialog(this);
            }
        }

        /// <summary>
        /// 阈值配置保存后 → 更新图表中的阈值线。
        /// </summary>
        private void OnThresholdsChanged(object sender, EventArgs e)
        {
            try
            {
                var thresholds = _configManager.GetAlarmThresholds();
                ApplyThresholdsToChart(thresholds);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("更新阈值线失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 将报警阈值配置应用到图表。
        /// 根据当前显示模式（电流/功率）设置对应的阈值线。
        /// </summary>
        private void ApplyThresholdsToChart(AlarmThresholdConfig thresholds)
        {
            if (thresholds == null)
            {
                _curvePanel.ClearThresholdLines();
                return;
            }

            switch (_mode)
            {
                case DisplayMode.Power:
                    if (thresholds.IsPowerThresholdActive)
                    {
                        _curvePanel.SetThresholdLine(
                            thresholds.Power.UpperLimit,
                            ParseColor(thresholds.Power.UpperColor),
                            ParseLineStyle(thresholds.Power.UpperLineStyle));
                    }
                    else
                    {
                        _curvePanel.ClearThresholdLines();
                    }
                    break;

                case DisplayMode.Current:
                case DisplayMode.Voltage:
                default:
                    if (thresholds.IsCurrentThresholdActive)
                    {
                        _curvePanel.SetThresholdLine(
                            thresholds.Current.UpperLimit,
                            ParseColor(thresholds.Current.UpperColor),
                            ParseLineStyle(thresholds.Current.UpperLineStyle));
                    }
                    else
                    {
                        _curvePanel.ClearThresholdLines();
                    }
                    break;
            }
        }

        /// <summary>
        /// 将 "#RRGGBB" 颜色字符串解析为 Color。
        /// </summary>
        private static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 7)
                return Color.Red;

            try
            {
                int r = Convert.ToInt32(hex.Substring(1, 2), 16);
                int g = Convert.ToInt32(hex.Substring(3, 2), 16);
                int b = Convert.ToInt32(hex.Substring(5, 2), 16);
                return Color.FromArgb(r, g, b);
            }
            catch
            {
                return Color.Red;
            }
        }

        /// <summary>
        /// 将线型字符串解析为 DashStyle。
        /// </summary>
        private static System.Drawing.Drawing2D.DashStyle ParseLineStyle(string style)
        {
            switch (style)
            {
                case "dash": return System.Drawing.Drawing2D.DashStyle.Dash;
                case "dot": return System.Drawing.Drawing2D.DashStyle.Dot;
                case "solid":
                default: return System.Drawing.Drawing2D.DashStyle.Solid;
            }
        }

        /// <summary>
        /// "参考曲线管理" 菜单项点击 → 打开管理窗口
        /// </summary>
        private void OnReferenceCurveManagementClick(object sender, EventArgs e)
        {
            using (var window = new ReferenceCurveManagementForm(_queryService))
            {
                window.ShowDialog(this);

                // 管理窗口关闭后，刷新当前参考曲线显示
                if (_actionGrid.SelectedRows.Count > 0)
                {
                    int rowIndex = _actionGrid.SelectedRows[0].Index;
                    if (rowIndex >= 0 && rowIndex < _actions.Count)
                    {
                        LoadReferenceCurveForSwitch(_actions[rowIndex].SwitchId);
                    }
                }
            }
        }

        /// <summary>
        /// 为指定道岔加载活跃参考曲线并叠加显示
        /// </summary>
        private void LoadReferenceCurveForSwitch(string switchId)
        {
            try
            {
                var activeRef = _queryService.GetActiveReferenceCurve(switchId);
                if (activeRef == null)
                {
                    _curvePanel.ClearReferenceSamples();
                    return;
                }

                // 从缓存获取参考曲线采样数据
                var refSamples = _queryService.GetCachedReferenceSamples(switchId);
                if (refSamples == null || refSamples.Count == 0)
                {
                    _curvePanel.ClearReferenceSamples();
                    return;
                }

                // 按相别提取当前显示模式的数据
                List<float> refA = null, refB = null, refC = null;
                switch (_mode)
                {
                    case DisplayMode.Current:
                        refA = ExtractValues(refSamples, "A", s => s.Current);
                        refB = ExtractValues(refSamples, "B", s => s.Current);
                        refC = ExtractValues(refSamples, "C", s => s.Current);
                        break;
                    case DisplayMode.Voltage:
                        refA = ExtractValues(refSamples, "A", s => s.Voltage);
                        refB = ExtractValues(refSamples, "B", s => s.Voltage);
                        refC = ExtractValues(refSamples, "C", s => s.Voltage);
                        break;
                    case DisplayMode.Power:
                        refA = ExtractValues(refSamples, "A", s => s.Power);
                        refB = ExtractValues(refSamples, "B", s => s.Power);
                        refC = ExtractValues(refSamples, "C", s => s.Power);
                        break;
                }

                // 构建参考曲线标签
                string desc = !string.IsNullOrEmpty(activeRef.Description)
                    ? " (" + activeRef.Description + ")" : "";
                string label = string.Format("参考曲线设定于 {0}{1}",
                    activeRef.SetTimeDateDisplay, desc);

                _curvePanel.SetReferenceSamples(refA, refB, refC, label);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("加载参考曲线失败: " + ex.Message);
            }
        }

        // ================================================================
        // 状态时间线
        // ================================================================

        /// <summary>
        /// 加载指定动作时间窗口内的开关量状态事件
        /// </summary>
        private void LoadStatusTimeline(SwitchActionRecord action)
        {
            try
            {
                // 扩展时间窗口：开始前 5 秒，结束后 5 秒（确保覆盖边界事件）
                long windowStart = action.StartTime - 5;
                long windowEnd = (action.EndTime > 0 ? action.EndTime : action.StartTime + action.SampleCount / action.SampleRate) + 5;

                var events = _queryService.GetStatusEvents(windowStart, windowEnd);
                _statusTimeline.SetEvents(events, windowStart, windowEnd);

                // 初始同步视口
                SyncTimelineViewport();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("加载状态时间线失败: " + ex.Message);
                _statusTimeline.Clear();
            }
        }

        /// <summary>
        /// 曲线图表视口变化 → 同步状态时间线
        /// </summary>
        private void OnCurveViewportChanged(float viewLeft, float viewRight, int maxSampleCount)
        {
            SyncTimelineViewport();
        }

        /// <summary>
        /// 将曲线图表的视口（采样序号范围）同步到状态时间线（时间比例）
        /// </summary>
        private void SyncTimelineViewport()
        {
            if (_currentAction == null || _currentAction.SampleCount <= 0)
                return;

            // 曲线图表视口在采样序号空间，转换为时间比例
            float leftFraction = _curvePanel.ViewLeft / _currentAction.SampleCount;
            float rightFraction = _curvePanel.ViewRight / _currentAction.SampleCount;

            _statusTimeline.SyncViewport(leftFraction, rightFraction);
        }

        /// <summary>
        /// 状态时间线悬浮信息 → 更新状态栏
        /// </summary>
        private void OnStatusHoverChanged(string info)
        {
            if (!string.IsNullOrEmpty(info))
            {
                _statusLabel.Text = info;
            }
            else if (_currentAction != null)
            {
                // 恢复默认状态
                string switchDisplay = _mappingConfig.GetSwitchName(_currentAction.SwitchId);
                _statusLabel.Text = string.Format("当前: {0} | {1} | {2} | {3} 采样点 | 显示: {4}",
                    switchDisplay, _currentAction.Direction, _currentAction.StartTimeDisplay,
                    _currentAction.SampleCount, GetDisplayModeName());
            }
        }

        // ================================================================
        // 诊断引擎
        // ================================================================

        /// <summary>
        /// 对当前加载的动作运行诊断引擎，结果更新到诊断面板。
        /// </summary>
        private void RunDiagnosis(SwitchActionRecord action)
        {
            if (_diagnosisEngine == null)
            {
                _diagnosisPanel.ClearResults();
                return;
            }

            try
            {
                var data = ConvertToSwitchActionData(action, _currentSamples);
                var results = _diagnosisEngine.Diagnose(data);
                _diagnosisPanel.SetResults(results);

                // 诊断出报警/故障时给出状态栏提示
                int alarmCount = 0;
                foreach (var r in results)
                {
                    if (r.Level == DiagnosisLevel.Alarm || r.Level == DiagnosisLevel.Fault)
                        alarmCount++;
                }
                if (alarmCount > 0)
                {
                    _statusLabel.Text = string.Format("{0} | 诊断: {1} 条报警!", _statusLabel.Text, alarmCount);
                }
            }
            catch (Exception ex)
            {
                _diagnosisPanel.SetResults(new List<DiagnosisResult>
                {
                    new DiagnosisResult
                    {
                        RuleName = "诊断异常",
                        Level = DiagnosisLevel.Normal,
                        Description = "诊断引擎执行异常: " + ex.Message,
                    }
                });
            }
        }

        /// <summary>
        /// 将数据库记录（SwitchActionRecord + CurveSampleRecord）转换为诊断引擎的输入格式。
        /// </summary>
        private SwitchActionData ConvertToSwitchActionData(SwitchActionRecord action, List<CurveSampleRecord> samples)
        {
            var data = new SwitchActionData
            {
                StationName = _mappingConfig.StationName,
                SwitchId = action.SwitchId,
                StartTime = action.StartTime,
                EndTime = action.EndTime,
                Direction = action.Direction,
                SampleRate = action.SampleRate > 0 ? action.SampleRate : 25,
                SampleCount = action.SampleCount,
                PhaseCount = action.PhaseCount,
                FileSource = action.FileSource,
            };

            if (samples != null)
            {
                foreach (var s in samples)
                {
                    data.Samples.Add(new SamplePoint
                    {
                        Index = s.SampleIndex,
                        Timestamp = s.Timestamp,
                        Phase = s.Phase,
                        Current = s.Current,
                        Voltage = s.Voltage,
                        Power = s.Power,
                        RawValue = s.RawValue,
                    });
                }
            }

            return data;
        }

        /// <summary>
        /// 重建 switchId → displayName 映射缓存并刷新下拉框。
        /// 如果保留当前选择项则尝试恢复，否则选中"全部"。
        /// </summary>
        private void RebuildSwitchCombo()
        {
            _switchDisplayNames.Clear();
            _allSwitchIds = _queryService.GetDistinctSwitchIds();
            foreach (var id in _allSwitchIds)
            {
                _switchDisplayNames[id] = _mappingConfig.GetSwitchName(id);
            }

            // 保存当前选择
            string currentDisplay = null;
            if (_switchCombo.SelectedIndex > 0)
                currentDisplay = _switchCombo.SelectedItem.ToString();

            _switchCombo.Items.Clear();
            _switchCombo.Items.Add("全部");
            foreach (var id in _allSwitchIds)
            {
                _switchCombo.Items.Add(_mappingConfig.GetSwitchName(id));
            }

            if (currentDisplay != null)
            {
                int idx = _switchCombo.Items.IndexOf(currentDisplay);
                _switchCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                _switchCombo.SelectedIndex = 0;
            }
        }

        // ================================================================
        // 映射配置热加载
        // ================================================================

        /// <summary>
        /// "重新加载配置" 菜单项点击 → 热加载映射配置并刷新 UI。
        /// </summary>
        private void OnReloadMappingConfigClick(object sender, EventArgs e)
        {
            try
            {
                _mappingConfig.Reload();
                _statusLabel.Text = "配置已重新加载";

                // 刷新所有使用映射配置的 UI 元素
                RefreshAllMappedDisplays();

                _statusLabel.Text = string.Format("配置已重新加载 | 车站: {0} | {1} 条动作记录",
                    _mappingConfig.StationName, _actions != null ? _actions.Count : 0);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "重新加载配置失败: " + ex.Message;
                MessageBox.Show("重新加载配置失败:\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 刷新所有受映射配置影响的 UI 显示。
        /// </summary>
        private void RefreshAllMappedDisplays()
        {
            // 重建下拉框
            RebuildSwitchCombo();

            // 刷新动作列表
            PopulateActionGrid();

            // 刷新状态时间线的点号标签
            _statusTimeline.RefreshPointLabels(_mappingConfig);

            // 刷新当前曲线标题（如果正在查看）
            if (_currentAction != null)
            {
                string switchDisplay = _mappingConfig.GetSwitchName(_currentAction.SwitchId);
                _statusLabel.Text = string.Format("当前: {0} | {1} | {2} | {3} 采样点 | 显示: {4}",
                    switchDisplay, _currentAction.Direction, _currentAction.StartTimeDisplay,
                    _currentAction.SampleCount, GetDisplayModeName());
            }

            Invalidate();
        }

        /// <summary>
        /// 从显示名反查原始 switchId（用于 ComboBox 选择 → 查询过滤）。
        /// 因为不同 switchId 可能映射为相同显示名，优先精确匹配 _switchDisplayNames。
        /// </summary>
        private string ResolveSwitchIdFromDisplay(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return null;

            foreach (var kv in _switchDisplayNames)
            {
                if (kv.Value == displayName)
                    return kv.Key;
            }

            // 降级：假设显示名就是 switchId
            return displayName;
        }

        /// <summary>
        /// 格式化状态栏文本。
        /// 格式: "1-1 | 2026-06-29 | 17:01:41 | 动作数: 25"
        /// </summary>
        /// <param name="switchId">道岔 ID（null=未选）</param>
        /// <param name="date">日期字符串</param>
        /// <param name="time">时间字符串</param>
        /// <param name="actionCount">动作数量</param>
        /// <returns>格式化后的状态文本</returns>
        public static string FormatStatusText(string switchId, string date, string time, int actionCount)
        {
            if (string.IsNullOrEmpty(switchId))
                return "就绪 — 请选择转辙机查看曲线";

            var parts = new System.Collections.Generic.List<string>();
            parts.Add(switchId);
            if (!string.IsNullOrEmpty(date)) parts.Add(date);
            if (!string.IsNullOrEmpty(time)) parts.Add(time);
            if (actionCount > 0) parts.Add("动作数: " + actionCount);

            return string.Join(" | ", parts.ToArray());
        }

        /// <summary>
        /// 诊断结果项点击 → 缩放到曲线的相关区域。
        /// </summary>
        private void OnDiagnosisItemClicked(int index)
        {
            var results = _diagnosisPanel.GetResults();
            if (results == null || index < 0 || index >= results.Count)
                return;

            var result = results[index];

            // 根据规则名称决定缩放区域
            switch (result.RuleName)
            {
                case "解锁段峰值异常":
                    // 缩放到前 15%
                    _curvePanel.ZoomToSegment(0f, 0.15f);
                    _statusLabel.Text = "已缩放到: 解锁段 (前 15%)";
                    break;

                case "转换段稳态异常":
                    // 缩放到中间 20%~80%
                    _curvePanel.ZoomToSegment(0.2f, 0.8f);
                    _statusLabel.Text = "已缩放到: 转换段 (20%~80%)";
                    break;

                case "锁闭段峰值异常":
                    // 缩放到后 15%
                    _curvePanel.ZoomToSegment(0.85f, 1.0f);
                    _statusLabel.Text = "已缩放到: 锁闭段 (后 15%)";
                    break;

                case "转换时间异常":
                case "采样数异常":
                default:
                    // 缩放到全范围
                    _curvePanel.ResetView();
                    _statusLabel.Text = "已复位全数据视图";
                    break;
            }
        }
    }
}
