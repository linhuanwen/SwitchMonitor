using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 转辙机选择对话框 — 允许用户勾选要单独处理的转辙机号，
    /// 避免每次基线/诊断都跑全站数据。
    /// </summary>
    public class SwitchSelectDialog : Form
    {
        private readonly List<string> _allSwitchIds;
        private CheckedListBox _listBox;
        private Button _btnOk;
        private Button _btnCancel;
        private Button _btnSelectAll;
        private Button _btnDeselectAll;
        private Label _lblHint;
        private Label _lblCount;

        /// <summary>用户最终勾选的转辙机 ID 列表</summary>
        public List<string> SelectedSwitchIds { get; private set; }

        public SwitchSelectDialog(List<string> allSwitchIds, string title = "选择转辙机")
        {
            _allSwitchIds = allSwitchIds ?? new List<string>();
            SelectedSwitchIds = new List<string>();

            this.Text = title;
            this.ClientSize = new Size(380, 420);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("SimSun", 10.5f);
            this.BackColor = Color.FromArgb(26, 26, 46);
            this.ForeColor = Color.FromArgb(224, 224, 224);

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // 提示文字
            _lblHint = new Label
            {
                Text = "勾选需要处理的转辙机号：",
                Location = new Point(16, 12),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200)
            };

            // 转辙机号勾选列表
            _listBox = new CheckedListBox
            {
                Location = new Point(16, 36),
                Size = new Size(346, 280),
                BackColor = Color.FromArgb(40, 40, 60),
                ForeColor = Color.FromArgb(224, 224, 224),
                BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true,
                IntegralHeight = false
            };
            foreach (var sid in _allSwitchIds)
            {
                _listBox.Items.Add(sid, true); // 默认全选
            }

            // 事件绑在添加项目之后，避免构造期间 Add(checked:true) 触发 ItemCheck
            // 时控件句柄尚未创建导致 BeginInvoke 抛异常
            _listBox.ItemCheck += OnItemCheck;

            // 全选 / 取消全选
            _btnSelectAll = new Button
            {
                Text = "全选",
                Location = new Point(16, 324),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat
            };
            _btnSelectAll.Click += (s, e) =>
            {
                for (int i = 0; i < _listBox.Items.Count; i++)
                    _listBox.SetItemChecked(i, true);
            };

            _btnDeselectAll = new Button
            {
                Text = "取消全选",
                Location = new Point(104, 324),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat
            };
            _btnDeselectAll.Click += (s, e) =>
            {
                for (int i = 0; i < _listBox.Items.Count; i++)
                    _listBox.SetItemChecked(i, false);
            };

            // 已选计数
            _lblCount = new Label
            {
                Text = "",
                Location = new Point(200, 328),
                AutoSize = true,
                ForeColor = Color.FromArgb(170, 170, 170)
            };
            UpdateCountLabel();

            // 确认 / 取消
            _btnOk = new Button
            {
                Text = "确定",
                Location = new Point(200, 360),
                Size = new Size(76, 28),
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK
            };
            _btnOk.Click += OnOkClicked;

            _btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(286, 360),
                Size = new Size(76, 28),
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel
            };

            this.Controls.Add(_lblHint);
            this.Controls.Add(_listBox);
            this.Controls.Add(_btnSelectAll);
            this.Controls.Add(_btnDeselectAll);
            this.Controls.Add(_lblCount);
            this.Controls.Add(_btnOk);
            this.Controls.Add(_btnCancel);
            this.AcceptButton = _btnOk;
            this.CancelButton = _btnCancel;
        }

        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            // 延迟更新计数（ItemCheck 在值变更前触发，需 BeginInvoke）
            if (this.IsHandleCreated)
                this.BeginInvoke((Action)UpdateCountLabel);
        }

        private void UpdateCountLabel()
        {
            int count = _listBox.CheckedIndices.Count;
            _lblCount.Text = string.Format("已选 {0}/{1}", count, _allSwitchIds.Count);
        }

        private void OnOkClicked(object sender, EventArgs e)
        {
            SelectedSwitchIds.Clear();
            foreach (var item in _listBox.CheckedItems)
            {
                SelectedSwitchIds.Add(item.ToString());
            }

            if (SelectedSwitchIds.Count == 0)
            {
                MessageBox.Show("请至少选择一个转辙机。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.DialogResult = DialogResult.None;
            }
        }
    }
}
