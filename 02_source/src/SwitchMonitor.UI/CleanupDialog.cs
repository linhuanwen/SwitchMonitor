using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using SwitchMonitor.Data;
using SwitchMonitor.Storage;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 数据清理对话框 — 按站点清理 SQLite 历史数据。
    /// 站点多选 + 保留天数 + 数据量展示 + 确认执行。
    /// </summary>
    public class CleanupDialog : Form
    {
        private readonly AppConfig _config;
        private readonly IndexManager _indexManager;
        private readonly List<SiteInfoRow> _siteRows;
        private readonly BackgroundWorker _cleanupWorker;
        private Button _btnOk;
        private Button _btnCancel;
        private Label _lblSummary;
        private NumericUpDown _numDays;
        private Label _lblDays;
        private Panel _sitePanel;
        private Label _lblProgress;

        public CleanupDialog(AppConfig config, IndexManager indexManager)
        {
            _config = config;
            _indexManager = indexManager;
            _siteRows = new List<SiteInfoRow>();

            _cleanupWorker = new BackgroundWorker();
            _cleanupWorker.WorkerReportsProgress = true;
            _cleanupWorker.WorkerSupportsCancellation = true;
            _cleanupWorker.DoWork += CleanupWorker_DoWork;
            _cleanupWorker.ProgressChanged += CleanupWorker_ProgressChanged;
            _cleanupWorker.RunWorkerCompleted += CleanupWorker_Completed;

            InitializeComponent();
            LoadSiteData();
        }

        private void InitializeComponent()
        {
            this.Text = "清理历史数据";
            this.ClientSize = new Size(520, 480);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("SimSun", 10.5f);
            this.BackColor = Color.FromArgb(26, 26, 46);
            this.ForeColor = Color.FromArgb(224, 224, 224);

            // 提示文字
            var lblHint = new Label
            {
                Text = "选择要清理的站点，设置数据保留天数：",
                Location = new Point(16, 12),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200)
            };

            // 站点列表面板（可滚动）
            _sitePanel = new Panel
            {
                Location = new Point(16, 36),
                Size = new Size(488, 280),
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true
            };

            // 保留天数
            _lblDays = new Label
            {
                Text = "保留最近",
                Location = new Point(16, 328),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200)
            };

            _numDays = new NumericUpDown
            {
                Location = new Point(88, 325),
                Size = new Size(64, 22),
                Minimum = 1,
                Maximum = 3650,
                Value = _config.DataRetentionDays > 0 ? _config.DataRetentionDays : 365,
                TextAlign = HorizontalAlignment.Center
            };
            _numDays.ValueChanged += (s, e) => UpdateSummary();

            var lblDaysUnit = new Label
            {
                Text = "天",
                Location = new Point(156, 328),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200)
            };

            // 概要
            _lblSummary = new Label
            {
                Text = "",
                Location = new Point(16, 360),
                AutoSize = true,
                ForeColor = Color.FromArgb(170, 170, 170)
            };

            // 进度
            _lblProgress = new Label
            {
                Text = "",
                Location = new Point(16, 390),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 170, 0)
            };

            // 按钮
            _btnOk = new Button
            {
                Text = "确认清理",
                Location = new Point(300, 410),
                Size = new Size(90, 28),
                FlatStyle = FlatStyle.Flat
            };
            _btnOk.Click += OnOkClicked;

            _btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(400, 410),
                Size = new Size(90, 28),
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel
            };

            this.Controls.Add(lblHint);
            this.Controls.Add(_sitePanel);
            this.Controls.Add(_lblDays);
            this.Controls.Add(_numDays);
            this.Controls.Add(lblDaysUnit);
            this.Controls.Add(_lblSummary);
            this.Controls.Add(_lblProgress);
            this.Controls.Add(_btnOk);
            this.Controls.Add(_btnCancel);
            this.CancelButton = _btnCancel;
        }

        /// <summary>
        /// 加载各站点数据量、最早记录等信息
        /// </summary>
        private void LoadSiteData()
        {
            var sites = GetEffectiveSites();
            if (sites.Count == 0)
            {
                _sitePanel.Controls.Add(new Label
                {
                    Text = "没有可管理的站点。",
                    Location = new Point(8, 8),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(128, 128, 128)
                });
                _btnOk.Enabled = false;
                return;
            }

            int y = 6;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var site in sites)
            {
                // 确定数据库路径
                string dbPath = ResolveDbPath(site, baseDir);

                // 查询数据量信息
                string sizeText = "";
                string earliestText = "";
                string countText = "";
                bool hasData = false;

                if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                {
                    try
                    {
                        using (var storage = new StorageManager(dbPath))
                        {
                            int count = storage.GetEventCount();
                            long fileSize = storage.GetFileSize();
                            countText = count.ToString("N0") + " 条";
                            sizeText = FormatFileSize(fileSize);

                            // 查询最早记录
                            earliestText = GetEarliestDate(storage);
                            if (!string.IsNullOrEmpty(earliestText))
                            {
                                earliestText = "最早: " + earliestText;
                                hasData = true;
                            }
                            else
                            {
                                earliestText = "无数据";
                            }
                        }
                    }
                    catch
                    {
                        sizeText = "读取失败";
                        earliestText = "";
                    }
                }
                else
                {
                    sizeText = "数据库不存在";
                    earliestText = "";
                }

                var row = new SiteInfoRow
                {
                    SiteId = site.Id,
                    SiteName = site.Name,
                    DbPath = dbPath,
                    HasData = hasData,
                    RecordCount = countText,
                    FileSize = sizeText
                };

                // 计算最早日期
                if (hasData && !string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                {
                    try
                    {
                        using (var storage = new StorageManager(dbPath))
                        {
                            row.EarliestDate = GetEarliestDate(storage);
                        }
                    }
                    catch { }
                }

                var chk = new CheckBox
                {
                    Text = string.Format("{0} ({1}, {2})",
                        site.Name, sizeText,
                        string.IsNullOrEmpty(earliestText) ? "无数据" : earliestText),
                    Location = new Point(8, y),
                    AutoSize = true,
                    Tag = row,
                    Checked = hasData && row.RecordCount != "无数据",
                    ForeColor = hasData ? Color.FromArgb(224, 224, 224) : Color.FromArgb(100, 100, 100),
                    Enabled = hasData
                };
                chk.CheckedChanged += (s, e) => UpdateSummary();
                _sitePanel.Controls.Add(chk);
                _siteRows.Add(row);

                y += 26;
            }

            UpdateSummary();
        }

        private string ResolveDbPath(SiteConfig site, string baseDir)
        {
            // 优先用站点独立 db（parsed_data/<siteId>.db）
            string parsedDir = site.ParsedDataDir ?? _config.ParsedDataDir;
            if (!Path.IsPathRooted(parsedDir))
                parsedDir = Path.Combine(baseDir, parsedDir);

            // 多站组网模式：每站一个 .db 文件（如 parsed_data/SSB.db）
            string siteDb = Path.Combine(parsedDir, site.Id + ".db");
            if (File.Exists(siteDb))
                return siteDb;

            // 回退：单站模式 switch_events.db
            string defaultDb = Path.Combine(parsedDir, "switch_events.db");
            if (File.Exists(defaultDb))
                return defaultDb;

            // 优先返回站点 db 路径（即使不存在，供后续创建判断）
            return siteDb;
        }

        private static string GetEarliestDate(StorageManager storage)
        {
            try
            {
                using (var conn = new System.Data.SQLite.SQLiteConnection(
                    string.Format("Data Source={0};Version=3;", storage.DbPath)))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT MIN(timestamp) FROM events;";
                        object result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            long ts = Convert.ToInt64(result);
                            if (ts > 0)
                            {
                                DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                    .AddSeconds(ts).ToLocalTime();
                                return dt.ToString("yyyy-MM-dd");
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private List<SiteConfig> GetEffectiveSites()
        {
            // 总终端：使用 config.Stations
            // 班组终端/站机：使用 config.TeamStations 或 config.Sites
            if (_config.Role == "central" && _config.Stations != null && _config.Stations.Count > 0)
                return _config.Stations;

            if (_config.TeamStations != null && _config.TeamStations.Count > 0)
                return _config.TeamStations;

            if (_config.Sites != null && _config.Sites.Count > 0)
                return _config.Sites;

            return new List<SiteConfig>();
        }

        private void UpdateSummary()
        {
            int selectedCount = 0;
            long totalSize = 0;

            foreach (Control ctrl in _sitePanel.Controls)
            {
                var chk = ctrl as CheckBox;
                if (chk != null && chk.Checked)
                {
                    selectedCount++;
                    var row = chk.Tag as SiteInfoRow;
                    if (row != null && !string.IsNullOrEmpty(row.DbPath) && File.Exists(row.DbPath))
                    {
                        try
                        {
                            totalSize += new FileInfo(row.DbPath).Length;
                        }
                        catch { }
                    }
                }
            }

            if (selectedCount > 0)
            {
                // 粗略估算释放空间
                int days = (int)_numDays.Value;
                long estimatedFree = totalSize / 2; // 粗略假设清理一半数据
                _lblSummary.Text = string.Format("已选 {0} 个站点, 当前总占用 {1}, 预计释放约 {2}",
                    selectedCount, FormatFileSize(totalSize), FormatFileSize(estimatedFree));
                _btnOk.Enabled = true;
            }
            else
            {
                _lblSummary.Text = "未选择任何站点";
                _btnOk.Enabled = false;
            }
        }

        private void OnOkClicked(object sender, EventArgs e)
        {
            int days = (int)_numDays.Value;
            var selectedRows = new List<SiteInfoRow>();

            foreach (Control ctrl in _sitePanel.Controls)
            {
                var chk = ctrl as CheckBox;
                if (chk != null && chk.Checked)
                {
                    var row = chk.Tag as SiteInfoRow;
                    if (row != null)
                        selectedRows.Add(row);
                }
            }

            if (selectedRows.Count == 0)
            {
                MessageBox.Show("请至少选择一个有数据的站点。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirmMsg = string.Format(
                "确认要删除 {0} 个站点 {1} 天之前的历史数据吗？\n\n" +
                "此操作不可撤销，被删除的数据将无法恢复。\n\n" +
                "确定继续？",
                selectedRows.Count, days);

            var result = MessageBox.Show(confirmMsg, "确认清理",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
                return;

            // 禁用 UI，启动后台清理
            _btnOk.Enabled = false;
            _btnCancel.Enabled = false;
            _numDays.Enabled = false;
            foreach (Control ctrl in _sitePanel.Controls)
                ctrl.Enabled = false;

            _lblProgress.Text = "正在清理...";
            _cleanupWorker.RunWorkerAsync(new CleanupArgs
            {
                Rows = selectedRows,
                RetentionDays = days
            });
        }

        private void CleanupWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var args = (CleanupArgs)e.Argument;
            var worker = sender as BackgroundWorker;
            DateTime cutoff = DateTime.Now.AddDays(-args.RetentionDays);
            int totalDeleted = 0;
            var results = new List<CleanupSiteResult>();

            for (int i = 0; i < args.Rows.Count; i++)
            {
                var row = args.Rows[i];

                if (worker.CancellationPending)
                {
                    e.Cancel = true;
                    return;
                }

                worker.ReportProgress((i * 100) / args.Rows.Count,
                    string.Format("正在清理 {0}...", row.SiteName));

                int deleted = 0;
                try
                {
                    if (!string.IsNullOrEmpty(row.DbPath) && File.Exists(row.DbPath))
                    {
                        using (var storage = new StorageManager(row.DbPath))
                        {
                            deleted = storage.DeleteEventsOlderThan(cutoff);
                            if (deleted > 0)
                            {
                                totalDeleted += deleted;
                                storage.Vacuum();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new CleanupSiteResult
                    {
                        SiteName = row.SiteName,
                        Deleted = 0,
                        Error = ex.Message
                    });
                    continue;
                }

                results.Add(new CleanupSiteResult
                {
                    SiteName = row.SiteName,
                    Deleted = deleted,
                    Error = null
                });
            }

            worker.ReportProgress(100, "清理完成");
            e.Result = new Dictionary<string, object>
            {
                { "TotalDeleted", totalDeleted },
                { "Results", results }
            };
        }

        private void CleanupWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string msg = e.UserState as string;
            if (!string.IsNullOrEmpty(msg))
                _lblProgress.Text = msg;
        }

        private void CleanupWorker_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                _lblProgress.Text = "清理已取消";
                MessageBox.Show("数据清理已被取消。", "取消",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.DialogResult = DialogResult.Cancel;
                this.Close();
                return;
            }

            if (e.Error != null)
            {
                _lblProgress.Text = "清理出错: " + e.Error.Message;
                MessageBox.Show("数据清理过程发生错误:\n" + e.Error.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.DialogResult = DialogResult.Cancel;
                this.Close();
                return;
            }

            // dynamic 在 .NET 4.0 下可通过反射，这里直接使用 Dictionary
            var resultDict = e.Result as Dictionary<string, object>;
            int totalDeleted = 0;
            if (resultDict != null && resultDict.ContainsKey("TotalDeleted"))
                totalDeleted = Convert.ToInt32(resultDict["TotalDeleted"]);

            // 刷新 IndexManager 缓存
            try { _indexManager.Initialize(); } catch { }

            string msg = string.Format("清理完成！共删除 {0} 条历史记录。", totalDeleted);
            _lblProgress.Text = msg;
            MessageBox.Show(msg, "清理完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + "B";
            if (bytes < 1024 * 1024) return string.Format("{0:F1}KB", bytes / 1024.0);
            if (bytes < 1024 * 1024 * 1024) return string.Format("{0:F1}MB", bytes / (1024.0 * 1024));
            return string.Format("{0:F2}GB", bytes / (1024.0 * 1024 * 1024));
        }

        private class SiteInfoRow
        {
            public string SiteId;
            public string SiteName;
            public string DbPath;
            public bool HasData;
            public string RecordCount;
            public string FileSize;
            public string EarliestDate;
        }

        private class CleanupArgs
        {
            public List<SiteInfoRow> Rows;
            public int RetentionDays;
        }

        private class CleanupSiteResult
        {
            public string SiteName;
            public int Deleted;
            public string Error;
        }
    }
}
