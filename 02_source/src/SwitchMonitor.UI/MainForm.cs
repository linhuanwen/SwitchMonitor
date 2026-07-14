using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;

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

            // ── D4: 装配诊断管道 ──
            try
            {
                _pipeline.DiagnoseHook = DiagnosisRunner.CreateHook(config.Diagnosis, config.ParsedDataDir);
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

            // 记录启动日志
            Logger.Info("SwitchMonitor 启动");
            Logger.Info("配置路径: " + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
            Logger.Info("数据目录: " + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.ParsedDataDir));
            Logger.Info("转辙机数量: " + config.SwitchGroups.Count);
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

            _menuStrip.Items.Add(toolMenu);
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
        /// 工具 → 设定基准曲线 → 弹出方向提示并触发基线重建
        /// </summary>
        private void OnBaselineSettingClicked(object sender, EventArgs e)
        {
            string message =
                "基准曲线按动作方向分为两类：\n\n" +
                "  ● 定位→反位：道岔从定位扳向反位的基准曲线\n" +
                "  ● 反位→定位：道岔从反位扳向定位的基准曲线\n\n" +
                "系统会根据每次动作的方向自动选用对应的基准曲线进行诊断。\n\n" +
                "点击「确定」将使用当前已导入数据重建全量基准曲线（含两个方向）。\n" +
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

            statusLabel.Text = "正在重建基准曲线（分方向）...";
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

                    var resultInfo = BuildAllBaselines(rulesDir);
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
        /// 重建全量基线（分方向）：遍历所有 switchId × {定位→反位, 反位→定位}
        /// 读取 features.json 和 current_features.json，按方向过滤后构建基线，
        /// 写入 baselines.json 和 current_baselines.json。
        /// </summary>
        /// <returns>结果摘要字符串</returns>
        private string BuildAllBaselines(string rulesDir)
        {
            string parsedDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.ParsedDataDir);
            string[] directions = { BaselineStore.DirNormalToReverse, BaselineStore.DirReverseToNormal };

            // ── 功率基线 ──
            var powerBaselines = new BaselineStore();
            int powerOk = 0, powerSkipped = 0;
            foreach (var sid in _indexManager.GetAllSwitchIds())
            {
                // 读取该道岔的全部 features.json 数据
                List<CurveFeatures> allFeats = LoadAllPowerFeatures(sid, parsedDataDir);
                if (allFeats.Count == 0)
                    continue;

                foreach (var dir in directions)
                {
                    var bl = BaselineBuilder.Build(allFeats, 30, dir);
                    if (bl != null)
                    {
                        powerBaselines.Switches[BaselineStore.MakeKey(sid, dir)] = bl;
                        powerOk++;
                    }
                    else
                    {
                        powerSkipped++;
                        Logger.Info(string.Format("功率基线 {0}|{1} 样本不足，跳过", sid, dir));
                    }
                }
            }
            powerBaselines.ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string powerPath = Path.Combine(rulesDir, "baselines.json");
            powerBaselines.Save(powerPath);

            // ── 电流基线 ──
            var currentBaselines = new CurrentBaselineStore();
            int currentOk = 0, currentSkipped = 0;
            foreach (var sid in _indexManager.GetAllSwitchIds())
            {
                List<CurrentFeatures> allCurrentFeats = LoadAllCurrentFeatures(sid, parsedDataDir);
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
            string currentPath = Path.Combine(rulesDir, "current_baselines.json");
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
        /// 工具 → 重新诊断当前数据
        /// </summary>
        private void OnRerunDiagClicked(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "将对全部已导入的道岔动作数据重新运行诊断引擎。\n\n" +
                "此操作不重新导入 CSV，仅重跑诊断规则。\n" +
                "完成后将更新诊断条、时间列表着色和日期角标。\n\n确定继续？",
                "重新诊断", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

            if (result == DialogResult.OK)
            {
                statusLabel.Text = "正在重跑诊断...";
                RunDiagnosisRerun();
            }
        }

        /// <summary>
        /// 后台重跑诊断（BackgroundWorker）
        /// </summary>
        private void RunDiagnosisRerun()
        {
            if (_importWorker.IsBusy)
            {
                MessageBox.Show("导入任务正在进行中，请等待完成后再重跑诊断。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

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

                    var engine = new DiagnosisEngine();
                    engine.Initialize(rulesDir);
                    engine.SetParsedDataDir(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.ParsedDataDir));

                    // 重建索引以获取最新数据
                    _indexManager.Initialize();

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

            // D6: 加载该道岔的参考曲线（用于图表叠加）
            object refCurve = null;
            object baseline = null;
            try
            {
                string refDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.Diagnosis.RulesDir, "reference_curves");
                string refPath = Path.Combine(refDir, _selectedSwitchId + ".json");
                if (File.Exists(refPath))
                {
                    var loadedRef = ReferenceCurveStore.Load(refPath);
                    if (loadedRef != null && loadedRef.Values != null && loadedRef.Values.Count > 0)
                    {
                        // 转换为 [t, v] 对格式供前端渲染
                        var refPairs = new List<object>();
                        double interval = loadedRef.SampleInterval > 0 ? loadedRef.SampleInterval : 0.04;
                        for (int i = 0; i < loadedRef.Values.Count; i++)
                        {
                            double t = Math.Round(i * interval, 3);
                            refPairs.Add(new double[] { t, loadedRef.Values[i] });
                        }
                        refCurve = new
                        {
                            switchId = loadedRef.SwitchId,
                            alignIndex = loadedRef.AlignIndex,
                            values = refPairs
                        };
                    }
                }
            }
            catch
            {
                // 参考曲线加载失败不中断主流程
                refCurve = null;
            }

            // D7: 加载该道岔的诊断基线（用于图表叠加显示），按方向查找
            try
            {
                string baselinePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    _config.Diagnosis.RulesDir, "baselines.json");
                if (File.Exists(baselinePath))
                {
                    var store = BaselineStore.Load(baselinePath);
                    SwitchBaseline bl;
                    // 优先按方向精确查找，降级到无方向旧格式
                    string currentDir = currentEvent != null ? currentEvent.Direction : null;
                    if (!store.Switches.TryGetValue(BaselineStore.MakeKey(_selectedSwitchId, currentDir), out bl))
                    {
                        // 降级：尝试无方向旧 key
                        store.Switches.TryGetValue(_selectedSwitchId, out bl);
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
                string currentBaselinePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    _config.Diagnosis.RulesDir, "current_baselines.json");
                if (File.Exists(currentBaselinePath))
                {
                    var cStore = CurrentBaselineStore.Load(currentBaselinePath);
                    CurrentBaseline cbl;
                    // 优先按方向精确查找，降级到无方向旧格式
                    string currentDir = currentEvent != null ? currentEvent.Direction : null;
                    if (!cStore.Switches.TryGetValue(CurrentBaselineStore.MakeKey(_selectedSwitchId, currentDir), out cbl))
                    {
                        cStore.Switches.TryGetValue(_selectedSwitchId, out cbl);
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
                baseline = baseline,
                currentBaseline = currentBaseline
            };

            string chartDataJson = _serializer.Serialize(chartData);
            InvokeChartScript("loadChartData", chartDataJson);

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
                    A = BuildCurrentPhaseSegments(cfA.SpikeIndexA, cfA.ActiveEnd, interval, n),
                    B = BuildCurrentPhaseSegments(cfB.SpikeIndexA, cfB.ActiveEnd, interval, n),
                    C = BuildCurrentPhaseSegments(cfC.SpikeIndexA, cfC.ActiveEnd, interval, n)
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
        /// D7: 根据电流曲线的 spikeIndex 和 activeEnd 计算阶段边界时间。
        /// 与 CurrentFeatureExtractor.ExtractPhaseInternal 的五阶段分割一致。
        /// </summary>
        private static object BuildCurrentPhaseSegments(int spikeIndex, int activeEnd, double interval, int n)
        {
            // ① 尖峰段：[0, spikeIndex+2]
            double spikeEnd = Math.Round(Math.Min(spikeIndex + 2, n) * interval, 3);

            // ② 解锁段：[spikeIndex+2, spikeIndex+14)
            int ulEnd = Math.Min(spikeIndex + 14, n);
            double unlockStart = Math.Round((spikeIndex + 2) * interval, 3);
            double unlockEnd = Math.Round(ulEnd * interval, 3);

            // ③ 转换段：首选 [spikeIndex+20, activeEnd-40)，退化 [spikeIndex+2, activeEnd)，再退化 [0, activeEnd]
            int convStart, convEnd;
            if (activeEnd - 40 > spikeIndex + 20)
            {
                convStart = spikeIndex + 20;
                convEnd = activeEnd - 40;
            }
            else
            {
                convStart = spikeIndex + 2;
                convEnd = activeEnd;
            }
            if (convStart >= convEnd)
            {
                convStart = 0;
                convEnd = activeEnd + 1;
            }
            double convStartSec = Math.Round(convStart * interval, 3);
            double convEndSec = Math.Round(Math.Min(convEnd, n) * interval, 3);

            // ④ 锁闭段：[activeEnd-40, activeEnd-22)，activeEnd≤50 时无锁闭段
            double lockStartSec = 0.0, lockEndSec = 0.0;
            if (activeEnd > 50)
            {
                int lockStart = Math.Max(0, activeEnd - 40);
                int lockEnd = activeEnd - 22;
                lockStartSec = Math.Round(lockStart * interval, 3);
                lockEndSec = Math.Round(lockEnd * interval, 3);
            }

            // ⑤ 缓放段：[activeEnd-22, activeEnd-2)，activeEnd≤30 时无缓放段
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
                unlockEndSec = unlockEnd,
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
    }

    #endregion
}
