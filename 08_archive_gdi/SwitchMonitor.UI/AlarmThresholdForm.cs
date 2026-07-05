using System;
using System.Drawing;
using System.Windows.Forms;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 报警阈值设置对话框。
    /// 允许用户修改电流和功率的报警上限值，配置持久化到 config.json。
    /// </summary>
    public class AlarmThresholdForm : Form
    {
        // ---- 配置 ----
        private ConfigManager _configManager;
        private AlarmThresholdConfig _originalConfig;  // 用于"取消"时恢复
        private AlarmThresholdConfig _editingConfig;   // 当前编辑中的配置

        // ---- 电流曲线控件 ----
        private GroupBox _groupCurrent;
        private CheckBox _chkCurrentEnabled;
        private Label _lblCurrentUpper;
        private TextBox _txtCurrentUpper;
        private Label _lblCurrentUnit;
        private Label _lblCurrentColor;
        private ComboBox _cboCurrentColor;
        private Label _lblCurrentLineStyle;
        private ComboBox _cboCurrentLineStyle;
        // 预留下限
        private CheckBox _chkCurrentLowerEnabled;
        private Label _lblCurrentLower;
        private TextBox _txtCurrentLower;
        private Label _lblCurrentLowerUnit;
        private Label _lblCurrentLowerReserved;

        // ---- 功率曲线控件 ----
        private GroupBox _groupPower;
        private CheckBox _chkPowerEnabled;
        private Label _lblPowerUpper;
        private TextBox _txtPowerUpper;
        private Label _lblPowerUnit;
        private Label _lblPowerColor;
        private ComboBox _cboPowerColor;
        private Label _lblPowerLineStyle;
        private ComboBox _cboPowerLineStyle;
        // 预留下限
        private CheckBox _chkPowerLowerEnabled;
        private Label _lblPowerLower;
        private TextBox _txtPowerLower;
        private Label _lblPowerLowerUnit;
        private Label _lblPowerLowerReserved;

        // ---- 按钮 ----
        private Button _btnSave;
        private Button _btnCancel;

        /// <summary>
        /// 保存成功后触发，用于通知 MainForm 更新图表阈值线。
        /// </summary>
        public event EventHandler ThresholdsChanged;

        /// <summary>
        /// 获取当前生效的报警阈值配置（供 MainForm 读取）。
        /// </summary>
        public AlarmThresholdConfig CurrentThresholds
        {
            get { return _configManager.GetAlarmThresholds(); }
        }

        public AlarmThresholdForm(ConfigManager configManager)
        {
            if (configManager == null)
                throw new ArgumentNullException("configManager");

            _configManager = configManager;
            _originalConfig = configManager.GetAlarmThresholds().Clone();
            _editingConfig = _originalConfig.Clone();

            InitializeComponents();
            LoadConfigToUI();
        }

        private void InitializeComponents()
        {
            this.Text = "报警阈值设置";
            this.Size = new Size(420, 520);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("宋体", 9f);

            int y = 12;
            const int labelWidth = 80;
            const int controlLeft = 95;
            const int controlWidth = 80;

            // === 电流曲线分组 ===
            _groupCurrent = new GroupBox
            {
                Text = "电流曲线",
                Location = new Point(12, y),
                Size = new Size(380, 195),
            };

            int gy = 20;

            // 启用复选框
            _chkCurrentEnabled = new CheckBox
            {
                Text = "启用报警上限",
                Location = new Point(12, gy),
                AutoSize = true,
            };
            _chkCurrentEnabled.CheckedChanged += OnCurrentEnabledChanged;

            gy += 28;

            // 上限值
            _lblCurrentUpper = new Label
            {
                Text = "上限值:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
            };
            _txtCurrentUpper = new TextBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
            };
            _lblCurrentUnit = new Label
            {
                Text = "A",
                Location = new Point(controlLeft + controlWidth + 6, gy + 3),
                AutoSize = true,
            };

            gy += 30;

            // 颜色
            _lblCurrentColor = new Label
            {
                Text = "颜色:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
            };
            _cboCurrentColor = new ComboBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _cboCurrentColor.Items.AddRange(new object[] { "红色", "橙色", "黄色", "绿色", "蓝色", "紫色", "黑色" });
            _cboCurrentColor.Tag = new string[] { "#FF0000", "#FFA500", "#FFFF00", "#008000", "#0000FF", "#800080", "#000000" };

            gy += 30;

            // 线型
            _lblCurrentLineStyle = new Label
            {
                Text = "线型:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
            };
            _cboCurrentLineStyle = new ComboBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _cboCurrentLineStyle.Items.AddRange(new object[] { "虚线", "实线", "点线" });
            _cboCurrentLineStyle.Tag = new string[] { "dash", "solid", "dot" };

            gy += 34;

            // 预留下限（灰显）
            _chkCurrentLowerEnabled = new CheckBox
            {
                Text = "启用报警下限",
                Location = new Point(12, gy),
                AutoSize = true,
                Enabled = false,
            };
            _lblCurrentLowerReserved = new Label
            {
                Text = "(预留功能)",
                Location = new Point(140, gy + 1),
                AutoSize = true,
                ForeColor = Color.Gray,
            };

            gy += 26;

            _lblCurrentLower = new Label
            {
                Text = "下限值:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
                ForeColor = Color.Gray,
            };
            _txtCurrentLower = new TextBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
                Enabled = false,
                Text = "0.0",
            };
            _lblCurrentLowerUnit = new Label
            {
                Text = "A",
                Location = new Point(controlLeft + controlWidth + 6, gy + 3),
                AutoSize = true,
                ForeColor = Color.Gray,
            };

            _groupCurrent.Controls.Add(_chkCurrentEnabled);
            _groupCurrent.Controls.Add(_lblCurrentUpper);
            _groupCurrent.Controls.Add(_txtCurrentUpper);
            _groupCurrent.Controls.Add(_lblCurrentUnit);
            _groupCurrent.Controls.Add(_lblCurrentColor);
            _groupCurrent.Controls.Add(_cboCurrentColor);
            _groupCurrent.Controls.Add(_lblCurrentLineStyle);
            _groupCurrent.Controls.Add(_cboCurrentLineStyle);
            _groupCurrent.Controls.Add(_chkCurrentLowerEnabled);
            _groupCurrent.Controls.Add(_lblCurrentLowerReserved);
            _groupCurrent.Controls.Add(_lblCurrentLower);
            _groupCurrent.Controls.Add(_txtCurrentLower);
            _groupCurrent.Controls.Add(_lblCurrentLowerUnit);

            y += 200;

            // === 功率曲线分组 ===
            _groupPower = new GroupBox
            {
                Text = "功率曲线",
                Location = new Point(12, y),
                Size = new Size(380, 195),
            };

            gy = 20;

            _chkPowerEnabled = new CheckBox
            {
                Text = "启用报警上限",
                Location = new Point(12, gy),
                AutoSize = true,
            };
            _chkPowerEnabled.CheckedChanged += OnPowerEnabledChanged;

            gy += 28;

            _lblPowerUpper = new Label
            {
                Text = "上限值:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
            };
            _txtPowerUpper = new TextBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
            };
            _lblPowerUnit = new Label
            {
                Text = "kW",
                Location = new Point(controlLeft + controlWidth + 6, gy + 3),
                AutoSize = true,
            };

            gy += 30;

            _lblPowerColor = new Label
            {
                Text = "颜色:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
            };
            _cboPowerColor = new ComboBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _cboPowerColor.Items.AddRange(new object[] { "红色", "橙色", "黄色", "绿色", "蓝色", "紫色", "黑色" });
            _cboPowerColor.Tag = new string[] { "#FF0000", "#FFA500", "#FFFF00", "#008000", "#0000FF", "#800080", "#000000" };

            gy += 30;

            _lblPowerLineStyle = new Label
            {
                Text = "线型:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
            };
            _cboPowerLineStyle = new ComboBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _cboPowerLineStyle.Items.AddRange(new object[] { "虚线", "实线", "点线" });
            _cboPowerLineStyle.Tag = new string[] { "dash", "solid", "dot" };

            gy += 34;

            // 预留下限
            _chkPowerLowerEnabled = new CheckBox
            {
                Text = "启用报警下限",
                Location = new Point(12, gy),
                AutoSize = true,
                Enabled = false,
            };
            _lblPowerLowerReserved = new Label
            {
                Text = "(预留功能)",
                Location = new Point(140, gy + 1),
                AutoSize = true,
                ForeColor = Color.Gray,
            };

            gy += 26;

            _lblPowerLower = new Label
            {
                Text = "下限值:",
                Location = new Point(24, gy + 3),
                AutoSize = true,
                ForeColor = Color.Gray,
            };
            _txtPowerLower = new TextBox
            {
                Location = new Point(controlLeft, gy),
                Width = controlWidth,
                Enabled = false,
                Text = "0.0",
            };
            _lblPowerLowerUnit = new Label
            {
                Text = "kW",
                Location = new Point(controlLeft + controlWidth + 6, gy + 3),
                AutoSize = true,
                ForeColor = Color.Gray,
            };

            _groupPower.Controls.Add(_chkPowerEnabled);
            _groupPower.Controls.Add(_lblPowerUpper);
            _groupPower.Controls.Add(_txtPowerUpper);
            _groupPower.Controls.Add(_lblPowerUnit);
            _groupPower.Controls.Add(_lblPowerColor);
            _groupPower.Controls.Add(_cboPowerColor);
            _groupPower.Controls.Add(_lblPowerLineStyle);
            _groupPower.Controls.Add(_cboPowerLineStyle);
            _groupPower.Controls.Add(_chkPowerLowerEnabled);
            _groupPower.Controls.Add(_lblPowerLowerReserved);
            _groupPower.Controls.Add(_lblPowerLower);
            _groupPower.Controls.Add(_txtPowerLower);
            _groupPower.Controls.Add(_lblPowerLowerUnit);

            y += 200;

            // === 按钮 ===
            _btnSave = new Button
            {
                Text = "保存",
                Location = new Point(200, y + 6),
                Size = new Size(80, 28),
            };
            _btnSave.Click += OnSaveClick;

            _btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(292, y + 6),
                Size = new Size(80, 28),
            };
            _btnCancel.Click += OnCancelClick;

            this.Controls.Add(_groupCurrent);
            this.Controls.Add(_groupPower);
            this.Controls.Add(_btnSave);
            this.Controls.Add(_btnCancel);

            this.AcceptButton = _btnSave;
            this.CancelButton = _btnCancel;
        }

        /// <summary>
        /// 将配置加载到 UI 控件。
        /// </summary>
        private void LoadConfigToUI()
        {
            // 电流
            _chkCurrentEnabled.Checked = _editingConfig.Current.Enabled;
            _txtCurrentUpper.Text = _editingConfig.Current.UpperLimit.ToString("F1");
            SetComboByTag(_cboCurrentColor, _editingConfig.Current.UpperColor);
            SetComboByTag(_cboCurrentLineStyle, _editingConfig.Current.UpperLineStyle);

            // 功率
            _chkPowerEnabled.Checked = _editingConfig.Power.Enabled;
            _txtPowerUpper.Text = _editingConfig.Power.UpperLimit.ToString("F1");
            SetComboByTag(_cboPowerColor, _editingConfig.Power.UpperColor);
            SetComboByTag(_cboPowerLineStyle, _editingConfig.Power.UpperLineStyle);

            UpdateControlStates();
        }

        /// <summary>
        /// 根据 Tag 数组值选择 ComboBox 项。
        /// </summary>
        private void SetComboByTag(ComboBox combo, string tagValue)
        {
            if (combo.Tag is string[] tags)
            {
                for (int i = 0; i < tags.Length && i < combo.Items.Count; i++)
                {
                    if (tags[i] == tagValue)
                    {
                        combo.SelectedIndex = i;
                        return;
                    }
                }
            }
            // 降级
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        /// <summary>
        /// 从 ComboBox 获取当前选中的 Tag 值。
        /// </summary>
        private string GetSelectedTag(ComboBox combo)
        {
            if (combo.SelectedIndex >= 0 && combo.Tag is string[] tags)
            {
                if (combo.SelectedIndex < tags.Length)
                    return tags[combo.SelectedIndex];
            }
            return null;
        }

        /// <summary>
        /// 根据启用状态更新控件可用性。
        /// </summary>
        private void UpdateControlStates()
        {
            bool currentOn = _chkCurrentEnabled.Checked;
            _txtCurrentUpper.Enabled = currentOn;
            _cboCurrentColor.Enabled = currentOn;
            _cboCurrentLineStyle.Enabled = currentOn;

            bool powerOn = _chkPowerEnabled.Checked;
            _txtPowerUpper.Enabled = powerOn;
            _cboPowerColor.Enabled = powerOn;
            _cboPowerLineStyle.Enabled = powerOn;
        }

        private void OnCurrentEnabledChanged(object sender, EventArgs e)
        {
            UpdateControlStates();
        }

        private void OnPowerEnabledChanged(object sender, EventArgs e)
        {
            UpdateControlStates();
        }

        /// <summary>
        /// 从 UI 控件收集当前编辑的配置。
        /// </summary>
        private void CollectUItoConfig()
        {
            _editingConfig.Current.Enabled = _chkCurrentEnabled.Checked;
            float currentVal;
            if (float.TryParse(_txtCurrentUpper.Text.Trim(), out currentVal))
                _editingConfig.Current.UpperLimit = currentVal;
            _editingConfig.Current.UpperColor = GetSelectedTag(_cboCurrentColor) ?? "#FF0000";
            _editingConfig.Current.UpperLineStyle = GetSelectedTag(_cboCurrentLineStyle) ?? "dash";

            _editingConfig.Power.Enabled = _chkPowerEnabled.Checked;
            float powerVal;
            if (float.TryParse(_txtPowerUpper.Text.Trim(), out powerVal))
                _editingConfig.Power.UpperLimit = powerVal;
            _editingConfig.Power.UpperColor = GetSelectedTag(_cboPowerColor) ?? "#FF0000";
            _editingConfig.Power.UpperLineStyle = GetSelectedTag(_cboPowerLineStyle) ?? "dash";
        }

        /// <summary>
        /// 保存按钮点击 → 验证输入 → 写入配置 → 触发更新事件。
        /// </summary>
        private void OnSaveClick(object sender, EventArgs e)
        {
            // 验证电流阈值
            string currentErr = ThresholdValidator.ValidateUpperLimit(_txtCurrentUpper.Text.Trim());
            if (currentErr != null)
            {
                MessageBox.Show(this, "电流阈值: " + currentErr, "输入错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtCurrentUpper.Focus();
                _txtCurrentUpper.SelectAll();
                return;
            }

            // 验证功率阈值
            string powerErr = ThresholdValidator.ValidateUpperLimit(_txtPowerUpper.Text.Trim());
            if (powerErr != null)
            {
                MessageBox.Show(this, "功率阈值: " + powerErr, "输入错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _txtPowerUpper.Focus();
                _txtPowerUpper.SelectAll();
                return;
            }

            // 收集 UI 数据到配置对象
            CollectUItoConfig();

            // 持久化到 config.json
            try
            {
                _configManager.SaveAlarmThresholds(_editingConfig);
                _originalConfig = _editingConfig.Clone();

                // 通知 MainForm 更新图表
                if (ThresholdsChanged != null)
                    ThresholdsChanged(this, EventArgs.Empty);

                this.DialogResult = DialogResult.OK;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存配置失败:\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 取消按钮点击 → 丢弃修改 → 关闭窗口。
        /// </summary>
        private void OnCancelClick(object sender, EventArgs e)
        {
            // 不保存，直接关闭
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }
    }
}
