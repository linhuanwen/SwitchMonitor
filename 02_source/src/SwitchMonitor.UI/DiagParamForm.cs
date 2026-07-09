using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// D5 诊断参数配置对话框（WinForms 原生控件，不依赖 HTML）。
    /// 合并原 Slice 08 的静态阈值管理 + diagnosis thresholds.json 编辑。
    /// </summary>
    public partial class DiagParamForm : Form
    {
        private readonly AppConfig _config;
        private readonly IndexManager _indexManager;
        private readonly JavaScriptSerializer _serializer;
        private ThresholdStore _thresholds;

        // 规则控件映射
        private readonly Dictionary<string, CheckBox> _chkEnabled = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, ComboBox> _cmbLevel = new Dictionary<string, ComboBox>();
        private readonly Dictionary<string, Control> _paramControls = new Dictionary<string, Control>();

        // 图表阈值控件
        private CheckBox _chkCurrentEnabled;
        private NumericUpDown _numCurrentValue;
        private CheckBox _chkPowerEnabled;
        private NumericUpDown _numPowerValue;

        // R4/R5/R7 共用 overRefRatio 的 NumericUpDown（共享引用）
        private NumericUpDown _numOverRefRatio;

        public DiagParamForm(AppConfig config, IndexManager indexManager)
        {
            _config = config;
            _indexManager = indexManager;
            _serializer = new JavaScriptSerializer();
            InitializeComponent();
            LoadConfig();
        }

        private void InitializeComponent()
        {
            this.Text = "诊断参数设置";
            this.ClientSize = new Size(520, 720);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("SimSun", 9F);

            int y = 10;

            // ── 规则启停与级别 ──
            var rulesGroup = new GroupBox
            {
                Text = "规则启停与级别",
                Location = new Point(10, y),
                Size = new Size(498, 380)
            };
            this.Controls.Add(rulesGroup);

            int ry = 20;
            // R1: 动作超时/未完成
            ry = AddRuleRow(rulesGroup, ry, "R1", "动作超时/未完成", 0, true,
                new[] { new ParamSpec("超时偏移:", "durOverRefSeconds", 0.5m, 20.0m, 3.0m, 1, "秒") });

            // R2: 动作夭折
            ry = AddRuleRow(rulesGroup, ry, "R2", "动作夭折", 0, false,
                new[] { new ParamSpec("时长比下限:", "durUnderRefRatio", 0.10m, 0.99m, 0.60m, 2, "") });

            // R3: 动作时长偏差
            ry = AddRuleRow(rulesGroup, ry, "R3", "动作时长偏差", 0, false,
                new[] { new ParamSpec("最大偏差:", "maxDeviationSeconds", 0.1m, 10.0m, 0.5m, 1, "秒") });

            // R4: 启动峰值偏高
            ry = AddRuleRow(rulesGroup, ry, "R4", "启动峰值偏高", 0, false, null);
            // 保存 _numOverRefRatio 引用（R4/R5/R7 共用）
            ry = AddRuleRow(rulesGroup, ry, "R5", "转换段功率偏高", 0, false, null);
            ry = AddRuleRow(rulesGroup, ry, "R7", "解锁段偏高", 0, false, null);

            // R4/R5/R7 共用参数行 (overRefRatio)
            var lblOverRef = new Label
            {
                Text = "  ↳ R4/R5/R7 上限倍率:",
                Location = new Point(20, ry),
                Size = new Size(140, 24),
                TextAlign = ContentAlignment.MiddleRight
            };
            rulesGroup.Controls.Add(lblOverRef);

            _numOverRefRatio = new NumericUpDown
            {
                Location = new Point(164, ry),
                Size = new Size(64, 24),
                Minimum = 1.00m,
                Maximum = 5.00m,
                DecimalPlaces = 2,
                Increment = 0.05m,
                Value = 1.30m
            };
            rulesGroup.Controls.Add(_numOverRefRatio);

            var lblOverRefUnit = new Label
            {
                Text = "",
                Location = new Point(232, ry),
                Size = new Size(40, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            rulesGroup.Controls.Add(lblOverRefUnit);
            ry += 28;

            // R6: 转换段台阶突变
            ry = AddRuleRow(rulesGroup, ry, "R6", "转换段台阶突变", 0, false,
                new[] {
                    new ParamSpec("上限:", "maxStepRatio", 1.00m, 5.00m, 1.50m, 2, ""),
                    new ParamSpec("下限:", "minStepRatio", 0.10m, 1.00m, 0.67m, 2, "")
                });

            // R8: 缓放段异常
            ry = AddRuleRow(rulesGroup, ry, "R8", "缓放段异常", 0, false,
                new[] { new ParamSpec("偏差比例:", "deviationRatio", 0.05m, 0.95m, 0.30m, 2, "") });

            // 分隔线
            ry += 4;
            var sep = new Label
            {
                Text = "──────────────── 趋势与形态 ────────────────",
                Location = new Point(12, ry),
                Size = new Size(400, 20),
                ForeColor = Color.FromArgb(150, 150, 200)
            };
            rulesGroup.Controls.Add(sep);
            ry += 24;

            // T1: 趋势分析
            ry = AddRuleRow(rulesGroup, ry, "T1", "趋势渐增（近期 Mean 高于历史基线）", 0, false,
                new[] {
                    new ParamSpec("趋势比例:", "trendRatio", 0.02m, 0.50m, 0.15m, 2, ""),
                    new ParamSpec("趋势天数:", "trendDays", 1.0m, 90.0m, 7.0m, 0, "天")
                });

            // P1: 形态比对
            ry = AddRuleRow(rulesGroup, ry, "P1", "形态偏离（面积差 + 逐点偏差）", 0, false,
                new[] {
                    new ParamSpec("面积差比阈值:", "areaDiffRatioThreshold", 0.05m, 0.80m, 0.25m, 2, ""),
                    new ParamSpec("最大逐点偏差比:", "maxAbsDevRatio", 0.5m, 5.0m, 1.0m, 2, "")
                });

            // ★ 先更新 GroupBox 高度，再用它计算后续控件位置
            rulesGroup.Height = ry + 10;
            y += rulesGroup.Height + 8;

            // ── 图表阈值线 ──
            var chartGroup = new GroupBox
            {
                Text = "图表阈值线（曲线展示层，不影响诊断规则）",
                Location = new Point(10, y),
                Size = new Size(498, 120)
            };
            this.Controls.Add(chartGroup);
            y += chartGroup.Height + 8;

            int cy = 22;

            // 电流曲线
            var lblCurrent = new Label
            {
                Text = "电流曲线",
                Location = new Point(14, cy + 2),
                Size = new Size(60, 24),
                TextAlign = ContentAlignment.MiddleRight
            };
            chartGroup.Controls.Add(lblCurrent);

            _chkCurrentEnabled = new CheckBox
            {
                Text = "启用报警上限",
                Location = new Point(80, cy + 2),
                Size = new Size(110, 24)
            };
            chartGroup.Controls.Add(_chkCurrentEnabled);

            _numCurrentValue = new NumericUpDown
            {
                Location = new Point(200, cy),
                Size = new Size(64, 24),
                Minimum = 0.1m,
                Maximum = 100.0m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 2.0m
            };
            chartGroup.Controls.Add(_numCurrentValue);

            var lblCurrentUnit = new Label
            {
                Text = "A",
                Location = new Point(268, cy + 2),
                Size = new Size(20, 24)
            };
            chartGroup.Controls.Add(lblCurrentUnit);
            cy += 32;

            // 功率曲线
            var lblPower = new Label
            {
                Text = "功率曲线",
                Location = new Point(14, cy + 2),
                Size = new Size(60, 24),
                TextAlign = ContentAlignment.MiddleRight
            };
            chartGroup.Controls.Add(lblPower);

            _chkPowerEnabled = new CheckBox
            {
                Text = "启用报警上限",
                Location = new Point(80, cy + 2),
                Size = new Size(110, 24)
            };
            chartGroup.Controls.Add(_chkPowerEnabled);

            _numPowerValue = new NumericUpDown
            {
                Location = new Point(200, cy),
                Size = new Size(64, 24),
                Minimum = 0.1m,
                Maximum = 100.0m,
                DecimalPlaces = 1,
                Increment = 0.1m,
                Value = 1.5m
            };
            chartGroup.Controls.Add(_numPowerValue);

            var lblPowerUnit = new Label
            {
                Text = "KW",
                Location = new Point(268, cy + 2),
                Size = new Size(30, 24)
            };
            chartGroup.Controls.Add(lblPowerUnit);

            // ── 操作按钮 ──
            var btnPanel = new Panel
            {
                Location = new Point(10, y),
                Size = new Size(498, 38)
            };
            this.Controls.Add(btnPanel);

            int btnX = 6;
            var btnSaveRerun = new Button
            {
                Text = "保存并重跑诊断",
                Location = new Point(btnX, 6),
                Size = new Size(120, 26),
                DialogResult = DialogResult.OK
            };
            btnPanel.Controls.Add(btnSaveRerun);
            this.AcceptButton = btnSaveRerun;

            btnX += 126;
            var btnSave = new Button
            {
                Text = "仅保存",
                Location = new Point(btnX, 6),
                Size = new Size(72, 26),
                DialogResult = DialogResult.Yes
            };
            btnPanel.Controls.Add(btnSave);

            btnX += 78;
            var btnReset = new Button
            {
                Text = "恢复默认",
                Location = new Point(btnX, 6),
                Size = new Size(80, 26)
            };
            btnReset.Click += OnResetDefaults;
            btnPanel.Controls.Add(btnReset);

            btnX += 86;
            var btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(btnX, 6),
                Size = new Size(60, 26),
                DialogResult = DialogResult.Cancel
            };
            btnPanel.Controls.Add(btnCancel);
            this.CancelButton = btnCancel;

            y += 48;
            this.ClientSize = new Size(520, y);
        }

        /// <summary>
        /// 添加单条规则行：启用 checkbox + 级别 combobox + 可选参数
        /// </summary>
        private int AddRuleRow(GroupBox parent, int y, string ruleId, string ruleName,
            int indent, bool indentFirst, ParamSpec[] specs)
        {
            int xBase = 12 + indent * 16;
            bool isShared = (ruleId == "R5" || ruleId == "R7"); // R4/R5/R7 共用参数

            var chk = new CheckBox
            {
                Text = ruleId + " " + ruleName,
                Location = new Point(xBase, y + 2),
                Size = new Size(220, 24),
                Checked = true
            };
            parent.Controls.Add(chk);
            _chkEnabled[ruleId] = chk;
            chk.CheckedChanged += (s, e) => UpdateParamControls(ruleId);

            var cmb = new ComboBox
            {
                Location = new Point(xBase + 230, y + 1),
                Size = new Size(68, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Items = { "故障", "报警", "预警" }
            };
            cmb.SelectedIndex = 2; // 默认预警
            parent.Controls.Add(cmb);
            _cmbLevel[ruleId] = cmb;

            int ry = y + 28;

            if (specs != null)
            {
                foreach (var spec in specs)
                {
                    var lbl = new Label
                    {
                        Text = spec.Label,
                        Location = new Point(xBase + 16, ry + 2),
                        Size = new Size(isShared ? 120 : 76, 24),
                        TextAlign = ContentAlignment.MiddleRight
                    };
                    parent.Controls.Add(lbl);

                    var nud = new NumericUpDown
                    {
                        Location = new Point(xBase + (isShared ? 140 : 96), ry),
                        Size = new Size(64, 24),
                        Minimum = spec.Minimum,
                        Maximum = spec.Maximum,
                        DecimalPlaces = spec.DecimalPlaces,
                        Increment = spec.Increment ?? (spec.DecimalPlaces > 1 ? 0.05m : 0.1m),
                        Value = spec.DefaultValue
                    };
                    parent.Controls.Add(nud);
                    _paramControls[ruleId + "_" + spec.Key] = nud;

                    if (!string.IsNullOrEmpty(spec.Unit))
                    {
                        var lblUnit = new Label
                        {
                            Text = spec.Unit,
                            Location = new Point(xBase + (isShared ? 208 : 164), ry + 2),
                            Size = new Size(30, 24)
                        };
                        parent.Controls.Add(lblUnit);
                    }

                    ry += 28;
                }
            }

            return ry;
        }

        /// <summary>
        /// 启用状态变化时联动参数控件
        /// </summary>
        private void UpdateParamControls(string ruleId)
        {
            bool enabled = _chkEnabled.ContainsKey(ruleId) && _chkEnabled[ruleId].Checked;
            foreach (var kv in _paramControls)
            {
                if (kv.Key.StartsWith(ruleId + "_"))
                    kv.Value.Enabled = enabled;
            }
            // R4/R5/R7 共用 _numOverRefRatio
            if (ruleId == "R4" || ruleId == "R5" || ruleId == "R7")
            {
                if (_numOverRefRatio != null)
                {
                    bool anyChecked = (_chkEnabled.ContainsKey("R4") && _chkEnabled["R4"].Checked)
                        || (_chkEnabled.ContainsKey("R5") && _chkEnabled["R5"].Checked)
                        || (_chkEnabled.ContainsKey("R7") && _chkEnabled["R7"].Checked);
                    _numOverRefRatio.Enabled = anyChecked;
                }
            }
        }

        /// <summary>
        /// 从 thresholds.json + config.json 加载当前配置
        /// </summary>
        private void LoadConfig()
        {
            // 加载 thresholds.json
            string rulesDir = _config.Diagnosis.RulesDir;
            if (!Path.IsPathRooted(rulesDir))
                rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);

            string thresholdsPath = Path.Combine(rulesDir, "thresholds.json");
            _thresholds = ThresholdStore.Load(thresholdsPath);

            // 填充规则控件
            foreach (var kv in _thresholds.rules)
            {
                string id = kv.Key;
                var rule = kv.Value;

                if (_chkEnabled.ContainsKey(id))
                {
                    _chkEnabled[id].Checked = rule.enabled;
                }
                if (_cmbLevel.ContainsKey(id))
                {
                    for (int i = 0; i < _cmbLevel[id].Items.Count; i++)
                    {
                        if ((string)_cmbLevel[id].Items[i] == rule.level)
                        {
                            _cmbLevel[id].SelectedIndex = i;
                            break;
                        }
                    }
                }

                // 填充参数控件
                FillParamValue(id, "durOverRefSeconds", (decimal)rule.durOverRefSeconds);
                FillParamValue(id, "durUnderRefRatio", (decimal)rule.durUnderRefRatio);
                FillParamValue(id, "maxDeviationSeconds", (decimal)rule.maxDeviationSeconds);
                FillParamValue(id, "maxStepRatio", (decimal)rule.maxStepRatio);
                FillParamValue(id, "minStepRatio", (decimal)rule.minStepRatio);
                FillParamValue(id, "deviationRatio", (decimal)rule.deviationRatio);
                FillParamValue(id, "trendRatio", (decimal)rule.trendRatio);
                FillParamValue(id, "trendDays", (decimal)rule.trendDays);
                FillParamValue(id, "areaDiffRatioThreshold", (decimal)rule.areaDiffRatioThreshold);
                FillParamValue(id, "maxAbsDevRatio", (decimal)rule.maxAbsDevRatio);

                // R4/R5/R7 共用 overRefRatio — 从 R4 读取
                if (id == "R4" && _numOverRefRatio != null)
                {
                    _numOverRefRatio.Value = (decimal)rule.overRefRatio;
                }
            }

            // 填充图表阈值
            if (_config.AlarmThresholds != null)
            {
                if (_config.AlarmThresholds.Current != null)
                {
                    _chkCurrentEnabled.Checked = _config.AlarmThresholds.Current.Enabled;
                    _numCurrentValue.Value = (decimal)_config.AlarmThresholds.Current.Value;
                }
                if (_config.AlarmThresholds.Power != null)
                {
                    _chkPowerEnabled.Checked = _config.AlarmThresholds.Power.Enabled;
                    _numPowerValue.Value = (decimal)_config.AlarmThresholds.Power.Value;
                }
            }

            // 初始化参数控件启用状态
            foreach (var ruleId in _thresholds.rules.Keys)
            {
                UpdateParamControls(ruleId);
            }
        }

        private void FillParamValue(string ruleId, string key, decimal value)
        {
            string controlKey = ruleId + "_" + key;
            if (_paramControls.ContainsKey(controlKey) && _paramControls[controlKey] is NumericUpDown)
            {
                ((NumericUpDown)_paramControls[controlKey]).Value = value;
            }
        }

        /// <summary>
        /// 控件的规则阈值写回到 ThresholdStore
        /// </summary>
        private void SaveToThresholds()
        {
            foreach (var kv in _thresholds.rules)
            {
                string id = kv.Key;
                var rule = kv.Value;

                if (_chkEnabled.ContainsKey(id))
                    rule.enabled = _chkEnabled[id].Checked;
                if (_cmbLevel.ContainsKey(id))
                    rule.level = (string)_cmbLevel[id].SelectedItem;

                // 参数
                rule.durOverRefSeconds = (double)GetParamValue(id, "durOverRefSeconds", (decimal)rule.durOverRefSeconds);
                rule.durUnderRefRatio = (double)GetParamValue(id, "durUnderRefRatio", (decimal)rule.durUnderRefRatio);
                rule.maxDeviationSeconds = (double)GetParamValue(id, "maxDeviationSeconds", (decimal)rule.maxDeviationSeconds);
                rule.maxStepRatio = (double)GetParamValue(id, "maxStepRatio", (decimal)rule.maxStepRatio);
                rule.minStepRatio = (double)GetParamValue(id, "minStepRatio", (decimal)rule.minStepRatio);
                rule.deviationRatio = (double)GetParamValue(id, "deviationRatio", (decimal)rule.deviationRatio);
                rule.trendRatio = (double)GetParamValue(id, "trendRatio", (decimal)rule.trendRatio);
                rule.trendDays = (int)(double)GetParamValue(id, "trendDays", (decimal)rule.trendDays);
                rule.areaDiffRatioThreshold = (double)GetParamValue(id, "areaDiffRatioThreshold", (decimal)rule.areaDiffRatioThreshold);
                rule.maxAbsDevRatio = (double)GetParamValue(id, "maxAbsDevRatio", (decimal)rule.maxAbsDevRatio);

                // R4/R5/R7 共用 overRefRatio
                if ((id == "R4" || id == "R5" || id == "R7") && _numOverRefRatio != null)
                {
                    rule.overRefRatio = (double)_numOverRefRatio.Value;
                }
            }
        }

        private decimal GetParamValue(string ruleId, string key, decimal defaultValue)
        {
            string controlKey = ruleId + "_" + key;
            if (_paramControls.ContainsKey(controlKey) && _paramControls[controlKey] is NumericUpDown)
            {
                return ((NumericUpDown)_paramControls[controlKey]).Value;
            }
            return defaultValue;
        }

        /// <summary>
        /// 保存 thresholds.json + config.json
        /// </summary>
        private void SaveToDisk()
        {
            string rulesDir = _config.Diagnosis.RulesDir;
            if (!Path.IsPathRooted(rulesDir))
                rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rulesDir);

            if (!Directory.Exists(rulesDir))
                Directory.CreateDirectory(rulesDir);

            string thresholdsPath = Path.Combine(rulesDir, "thresholds.json");
            string json = _serializer.Serialize(_thresholds);
            File.WriteAllText(thresholdsPath, json, Encoding.UTF8);

            // 更新 config.json 中图表阈值
            if (_config.AlarmThresholds != null)
            {
                _config.AlarmThresholds.Current.Enabled = _chkCurrentEnabled.Checked;
                _config.AlarmThresholds.Current.Value = (double)_numCurrentValue.Value;
                _config.AlarmThresholds.Power.Enabled = _chkPowerEnabled.Checked;
                _config.AlarmThresholds.Power.Value = (double)_numPowerValue.Value;
            }

            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            ConfigManager.SaveConfig(configPath);
        }

        /// <summary>
        /// 恢复默认按钮
        /// </summary>
        private void OnResetDefaults(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "确定要恢复所有诊断参数为内置默认值吗？\n此操作将覆盖当前配置。",
                "确认恢复默认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);

            if (result != DialogResult.OK)
                return;

            _thresholds = ThresholdStore.CreateDefaults();
            LoadConfig();

            // 重置图表阈值
            _chkCurrentEnabled.Checked = true;
            _numCurrentValue.Value = 2.0m;
            _chkPowerEnabled.Checked = true;
            _numPowerValue.Value = 1.5m;
        }

        /// <summary>
        /// 关闭时保存（如果未取消）
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (this.DialogResult == DialogResult.OK || this.DialogResult == DialogResult.Yes)
            {
                SaveToThresholds();
                try
                {
                    SaveToDisk();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("保存配置失败:\n" + ex.Message,
                        "保存错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            base.OnFormClosing(e);
        }

        /// <summary>
        /// 参数规格辅助结构
        /// </summary>
        private class ParamSpec
        {
            public string Label;
            public string Key;
            public decimal Minimum;
            public decimal Maximum;
            public decimal DefaultValue;
            public int DecimalPlaces;
            public string Unit;
            public decimal? Increment { get; set; }

            public ParamSpec(string label, string key, decimal min, decimal max, decimal def, int dec, string unit, decimal? inc = null)
            {
                Label = label;
                Key = key;
                Minimum = min;
                Maximum = max;
                DefaultValue = def;
                DecimalPlaces = dec;
                Unit = unit;
                Increment = inc;
            }
        }
    }
}
