# Slice D5: UI 报警展示 + 诊断参数配置（合并 Slice 08 静态阈值）

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖链**: D1 → D2 → D3 → D4 → **D5**
> **前置阅读**: [PRD §4.6/§4.8](../PRD.md) — UI 展示规格与诊断参数配置；[IE8 兼容性测试结论](../PRD.md#53-ie8-兼容性测试)
> **合并说明**: 原 Slice 08（报警阈值配置模块）的阈值管理功能并入本 slice 的"诊断参数设置"对话框

## Type

AFK

## Blocked by

D4（诊断结果存储）

## What to build

四项交付：(1) 侧边栏时间列表按级别着色 + 日期角标；(2) 图表页顶部诊断结论条；
(3) 状态栏级别摘要；(4) **诊断参数配置对话框**（合并原 Slice 08 的阈值管理）。

全部 HTML/JS 必须 **ES5 + IE8 兼容**（var/function、无 CSS3、参照现有 sidebar.html/charts.html 写法）。
IE8 模式下 WebBrowser 控件设置 `ScriptErrorsSuppressed = true`（生产环境），并在所有 HTML 页面
`<script>` 最开头加 `console` polyfill：

```javascript
window.console = window.console || { log: function(){}, warn: function(){}, error: function(){} };
```

### 1. 侧边栏时间列表着色 + 日期角标

- `MainForm.OnDateSelected` 里在取时间戳列表的同时 `LoadDayDiagnosis(switchId, date)`，
  构造 `[{ts, level}, ...]` JSON 传给 `setTimes`（sidebar.html 的 `setTimes` 签名扩展，
  兼容旧格式：元素为纯数字时按"正常"处理）
- 时间列表项按级别显示颜色：
  正常=默认灰白(`#aaa`)、预警=`#FFD54F`、报警=`#FF9800`、故障=`#FF4444`；
  级别非正常的项在时间后追加级别字（如 `00:43:17 故障`），左边框着色
- 日期下拉项追加异常角标：读 alarms_index，非正常计数 >0 的日期显示
  `2026-01-29 (2)` 样式，故障>0 用红色、否则橙色

### 2. 图表页诊断结论条（charts.html）

- 顶部新增一个横条区域 `#diagBar`（图表网格上方，高约 28px）
- `MainForm.LoadCurveData` 把当前事件的诊断结果并入 `chartData`：
  `diagnosis: { level: "故障", items: ["动作时长31.36s，超过参考…", ...] }`
- JS 渲染：
  - 正常 → 绿色圆点 + "诊断正常"
  - 非正常 → 级别色背景条 + 中文结论文字（多条用 `；` 拼接，超长横向截断并 title 提示）
  - 无诊断数据（.diag.json 缺失）→ 灰色"未诊断"
- 渲染纯 DOM 操作（innerHTML 拼字符串），不用 Highcharts
- 级别→颜色映射放 `config.json` 的 `chartColors` 节新增键（`levelWarning/levelAlarm/levelFault`），
  JS 从 chartData.colors 取——沿用现有配色注入通道

### 3. 状态栏

- `UpdateStatusBar` 追加当前事件级别：`1-1 | 2026-01-29 | 00:43:17 | 故障`

### 4. 诊断参数配置对话框（合并 Slice 08）

"工具"菜单 → "诊断参数设置(&P)" → 弹出模态 WinForms `Form`（**原生控件，不用 HTML**，保证稳定性）：

```
┌─ 诊断参数设置 ──────────────────────────────────────────┐
│                                                          │
│  ┌─ 规则启停与级别 ─────────────────────────────────┐   │
│  │                                                  │   │
│  │  [✓] R1 动作超时/未完成    级别: [▼ 故障]        │   │
│  │      超时偏移: [3.0  ] 秒                        │   │
│  │                                                  │   │
│  │  [✓] R2 动作夭折           级别: [▼ 报警]        │   │
│  │      时长比下限: [0.60]                           │   │
│  │                                                  │   │
│  │  [✓] R3 动作时长偏差       级别: [▼ 预警]        │   │
│  │      最大偏差: [0.5  ] 秒                        │   │
│  │                                                  │   │
│  │  [✓] R4 启动峰值偏高       级别: [▼ 预警]        │   │
│  │  [✓] R5 转换段功率偏高     级别: [▼ 预警]        │   │
│  │  [✓] R7 解锁段偏高         级别: [▼ 预警]        │   │
│  │      上限倍率: [1.30]                            │   │
│  │                                                  │   │
│  │  [✓] R6 转换段台阶突变     级别: [▼ 报警]        │   │
│  │      上限: [1.50]  下限: [0.67]                  │   │
│  │                                                  │   │
│  │  [✓] R8 缓放段异常         级别: [▼ 预警]        │   │
│  │      偏差比例: [0.30]                            │   │
│  └──────────────────────────────────────────────────┘   │
│                                                          │
│  ┌─ 图表阈值线（曲线展示层，不影响诊断规则）────────┐   │
│  │                                                  │   │
│  │  电流曲线  [✓] 启用报警上限  [2.0  ] A           │   │
│  │  功率曲线  [✓] 启用报警上限  [1.5  ] KW          │   │
│  │  颜色: [■ 红色]  线型: [▼ 虚线]                  │   │
│  └──────────────────────────────────────────────────┘   │
│                                                          │
│  ┌─ 操作 ──────────────────────────────────────────┐   │
│  │  [保存并重跑诊断]  [仅保存]  [恢复默认]  [取消]  │   │
│  └──────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────┘
```

**对话框设计要点**：

- **诊断规则区**（上半）：编辑 `thresholds.json` 中每条规则的 `enabled`、`level`、阈值参数。
  R4/R5/R7 共用同一 `overRefRatio` 参数因此在 UI 上归为一组；R0 不显示（恒启用）。
- **图表阈值线区**（下半）：沿用原 Slice 08 的电流/功率阈值配置。此部分控制的是 `yAxis.plotLines`
  在图表上的显示位置——它和诊断引擎的规则阈值是**两个独立的概念**。图表阈值线走
  `config.json → JS updateThreshold()` 通道；诊断规则阈值走 `thresholds.json → DiagnosisEngine`
  通道。在一窗口中管理避免用户困惑"两个阈值设置"。
- **操作按钮**：
  - "保存并重跑诊断"：写回 JSON + 更新引擎配置 + 触发 `DiagnosisRunner.RerunAll`（BackgroundWorker，完成后刷新当前视图）。这是阈值调优的核心工作流。
  - "仅保存"：写回 JSON + 更新引擎配置，不重跑。图表阈值线用 `InvokeScript("updateThreshold", ...)` 实时刷新。
  - "恢复默认"：重置为代码内置默认值
  - "取消"：关闭窗口不保存

**实现分工**：
- 对话框 UI：WinForms `Form` + 标准控件（CheckBox, ComboBox, NumericUpDown, Button）
- 加载：构造时读 `thresholds.json` + `config.json` 的 `alarmThresholds` 节
- 保存：更新 `ConfigManager` + 序列化
- 重跑诊断：调用 `DiagnosisRunner.RerunAll(indexManager, engine)`（同 D4 的 `rerun` 逻辑），
  `DiagnosisRunner` 放在 Diagnosis 项目以便 DiagTool 复用

### 5. 菜单

- "工具"菜单加"重新诊断当前数据(&D)"：对全部 parsed_data 重跑诊断（不重新导 CSV），
  用 BackgroundWorker，完成后刷新当前视图

## Acceptance criteria

### 诊断展示

- [ ] 导入含异常的数据后：4-1 选 2026-01-29，时间列表中超时动作显示红色"故障"标记
- [ ] 点击该事件，诊断条显示红底结论文字，内容与 .diag.json 的 description 一致
- [ ] 正常事件显示"诊断正常"绿点；旧数据（无 .diag.json）显示"未诊断"，不报 JS 错误
- [ ] 日期下拉中 2026-01-29 带红色 `(n)` 角标，n = 该日非正常事件数
- [ ] 时间列表着色后，原有选中高亮、点击切换、降序排列行为不回归
- [ ] 状态栏显示当前事件级别

### IE8 兼容性

- [ ] 全部页面在 WebBrowser + IE8 模式下无脚本错误弹窗（`ScriptErrorsSuppressed=false` 测试后
      恢复为 `true`）
- [ ] 所有 HTML 页面包含 `console` polyfill（`<script>` 最开头）
- [ ] 窗口缩放时诊断条不遮挡图表、不出滚动条

### 诊断参数配置

- [ ] "工具"菜单可打开"诊断参数设置"对话框
- [ ] 每条规则的启用/级别/阈值参数可编辑
- [ ] "保存并重跑诊断"后 `thresholds.json` + `config.json` 更新，诊断结果刷新
- [ ] "仅保存"后图表阈值线实时更新（无需重新加载图表），诊断规则下次导入生效
- [ ] "恢复默认"重置为内置默认值
- [ ] 取消不保存修改
- [ ] 程序重启后上次配置的规则阈值和图表阈值均生效
- [ ] 输入非法值（非数字、负数、超出范围）有提示而不崩溃

## Further notes

- sidebar.html 的 `setTimes` 兼容策略：`typeof item === 'object' ? item.ts : item`，
  避免 D4 未跑或旧 parsed_data 时列表挂掉
- 诊断条渲染纯 DOM 操作（innerHTML 拼字符串），不要用 Highcharts
- "重新诊断"功能实现上 = 遍历 index，对每天 LoadDayData → DiagnoseHook 同款组合 →
  SaveDayDiagnosis；代码放 Diagnosis 项目的 `DiagnosisRunner.RerunAll(indexManager, engine)`
  以便 DiagTool 也能复用（`DiagTool.exe rerun <parsed_data> <Rules>`）
- **图表阈值线 vs 诊断规则阈值**：两个概念务必在 UI 和文档中区分清楚。图表线控制视觉参考线的位置；
  诊断规则阈值控制报警判定逻辑。PRD §4.8 有详细说明
- **IE8 已知注意事项**：`console` 对象在未开 F12 时不存在 → polyfill；`ScriptErrorsSuppressed=true`
  抑制无害的脚本噪音；WebBrowser 需设置 `FEATURE_BROWSER_EMULATION=8888`
