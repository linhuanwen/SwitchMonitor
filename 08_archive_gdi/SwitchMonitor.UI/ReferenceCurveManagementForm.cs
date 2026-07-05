using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 参考曲线管理窗口。
    /// 列表显示所有已设定的参考曲线，支持删除（软删除）和重新激活。
    /// </summary>
    public class ReferenceCurveManagementForm : Form
    {
        private QueryService _queryService;
        private DataGridView _grid;
        private Button _btnReactivate;
        private Button _btnDelete;
        private Button _btnClose;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel;

        private List<ReferenceCurveRecord> _records;

        public ReferenceCurveManagementForm(QueryService queryService)
        {
            if (queryService == null)
                throw new ArgumentNullException(nameof(queryService));

            _queryService = queryService;
            InitializeComponents();
            LoadData();
        }

        private void InitializeComponents()
        {
            this.Text = "参考曲线管理";
            this.Size = new Size(850, 500);
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("宋体", 9f);
            this.MinimumSize = new Size(600, 300);

            // === DataGridView ===
            _grid = new DataGridView
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

            // 定义列
            _grid.Columns.Add("SwitchId", "道岔 ID");
            _grid.Columns.Add("SetTime", "设定时间");
            _grid.Columns.Add("SourceActionTimeDisplay", "来源动作时间");
            _grid.Columns.Add("Description", "备注");
            _grid.Columns.Add("IsActive", "状态");

            _grid.Columns["SwitchId"].FillWeight = 15;
            _grid.Columns["SetTime"].FillWeight = 25;
            _grid.Columns["SourceActionTimeDisplay"].FillWeight = 25;
            _grid.Columns["Description"].FillWeight = 20;
            _grid.Columns["IsActive"].FillWeight = 15;

            // === 按钮面板 ===
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
            };

            _btnReactivate = new Button
            {
                Text = "重新激活",
                Width = 80,
                Location = new Point(12, 8),
                Enabled = false,
            };
            _btnReactivate.Click += OnReactivateClick;

            _btnDelete = new Button
            {
                Text = "删除（失效）",
                Width = 100,
                Location = new Point(100, 8),
                Enabled = false,
            };
            _btnDelete.Click += OnDeleteClick;

            _btnClose = new Button
            {
                Text = "关闭",
                Width = 60,
                Location = new Point(this.ClientSize.Width - 72, 8),
                Anchor = AnchorStyles.Right,
            };
            _btnClose.Click += (s, e) => this.Close();

            buttonPanel.Controls.Add(_btnReactivate);
            buttonPanel.Controls.Add(_btnDelete);
            buttonPanel.Controls.Add(_btnClose);

            // Grid 选中变化时更新按钮状态
            _grid.SelectionChanged += (s, e) =>
            {
                bool hasSelection = _grid.SelectedRows.Count > 0;
                _btnReactivate.Enabled = hasSelection;
                _btnDelete.Enabled = hasSelection;
            };

            // 窗体大小变化时调整关闭按钮位置
            this.Resize += (s, e) =>
            {
                _btnClose.Left = this.ClientSize.Width - 72;
            };

            // === 状态栏 ===
            _statusStrip = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("就绪");
            _statusStrip.Items.Add(_statusLabel);

            // === 布局 ===
            this.Controls.Add(_grid);
            this.Controls.Add(buttonPanel);
            this.Controls.Add(_statusStrip);
            _statusStrip.Dock = DockStyle.Bottom;
        }

        private void LoadData()
        {
            try
            {
                _records = _queryService.GetAllReferenceCurves();
                PopulateGrid();
                _statusLabel.Text = string.Format("共 {0} 条参考曲线记录", _records.Count);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "加载失败: " + ex.Message;
                MessageBox.Show(this, "加载参考曲线列表失败:\n" + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PopulateGrid()
        {
            _grid.Rows.Clear();

            if (_records == null || _records.Count == 0)
            {
                _statusLabel.Text = "暂无参考曲线记录";
                return;
            }

            foreach (var r in _records)
            {
                _grid.Rows.Add(
                    r.SwitchId,
                    r.SetTime,
                    r.SourceActionTimeDisplay,
                    r.Description ?? "",
                    r.IsActive ? "活跃" : "已失效"
                );
            }
        }

        /// <summary>"重新激活"按钮点击</summary>
        private void OnReactivateClick(object sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0)
                return;

            int rowIndex = _grid.SelectedRows[0].Index;
            if (rowIndex < 0 || rowIndex >= _records.Count)
                return;

            var record = _records[rowIndex];

            try
            {
                _queryService.ReactivateReferenceCurve(record.Id);
                _statusLabel.Text = string.Format("已重新激活 {0} 的参考曲线 (设定于 {1})",
                    record.SwitchId, record.SetTimeDateDisplay);

                // 刷新列表
                LoadData();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "重新激活失败: " + ex.Message;
                MessageBox.Show(this, "重新激活失败:\n" + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>"删除"按钮点击（软删除：IsActive = 0）</summary>
        private void OnDeleteClick(object sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0)
                return;

            int rowIndex = _grid.SelectedRows[0].Index;
            if (rowIndex < 0 || rowIndex >= _records.Count)
                return;

            var record = _records[rowIndex];

            try
            {
                _queryService.DeleteReferenceCurve(record.Id);
                _statusLabel.Text = string.Format("已删除 {0} 的参考曲线 (设定于 {1})",
                    record.SwitchId, record.SetTimeDateDisplay);

                // 清除缓存
                _queryService.InvalidateReferenceCache(record.SwitchId);

                // 刷新列表
                LoadData();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "删除失败: " + ex.Message;
                MessageBox.Show(this, "删除失败:\n" + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
