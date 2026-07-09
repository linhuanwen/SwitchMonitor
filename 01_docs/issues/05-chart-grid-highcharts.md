# Slice 5: 2×2 图表网格 (Highcharts)

> **状态（2026-07-08 代码审核）**: ✅ 已实现。偏差：上排=当前事件/下排=上一次（与规格相反，功能等价）；背景/网格线颜色由 config 驱动（现 #3c3c3c/#6a6a6a）；X 轴 14/30 规则被 JS 端 `Math.ceil(maxDuration)` 覆盖未生效。超纲：滚轮缩放、拖拽平移、参考曲线对比、双击详情窗。遗留调试痕迹：RENDER 徽标、Build 版本字样。

## Type

AFK

## Blocked by

Slice 4（MainForm + WebBrowser 侧边栏）+ Slice 2（有数据可渲染）

## What to build

在右侧图表区用 WebBrowser 加载 `charts.html`，嵌入 4 个 Highcharts 实例渲染 2×2 网格。C# 端通过 `InvokeScript` 注入曲线数据，实现深色主题、tooltip、图例切换、红色阈值虚线、动态 X 轴。

### 2×2 图表布局

```
┌─────────────────┬─────────────────┐
│  左上: 电流曲线   │  右上: 功率曲线   │
│  (上一时间)       │  (上一时间)       │
│  Y轴: A          │  Y轴: KW        │
├─────────────────┼─────────────────┤
│  左下: 电流曲线   │  右下: 功率曲线   │
│  (当前选中)       │  (当前选中)       │
│  Y轴: A          │  Y轴: KW        │
└─────────────────┴─────────────────┘
```

- 上一时间 = 时间列表中选中项的上一项（更早的一条记录）
- 4 个图表共用一个时间轴概念，但独立创建 Highcharts 实例
- 每个图表是 `charts.html` 中的一个 `<div>`

### Highcharts 配置要点

每个电流图表配置：
- `chart.type`: `'spline'`（平滑曲线）
- `chart.animation`: `false`（VML 下关闭动画保证流畅）
- `chart.backgroundColor`: `'#1a1a2e'`
- `xAxis.min/max`: 0 / 14（默认），当数据中 X 值超过 14 时扩展到 30
- `yAxis.plotLines`: 红色虚线（dashStyle: 'dash'），值为 `alarmThresholds.current.value`
- `marker.enabled`: `false`（790 个点不显示标记）
- `plotOptions.spline.lineWidth`: 1.5
- `tooltip.shared`: `true` — 同时显示 A/B/C 三相值
- `tooltip.formatter`: 自定义格式化：显示时间(秒) + 各相电流(A)
- `legend`: 底部水平排列，文字颜色 `#888`，点击图例切换系列显隐

功率图表同上，但 Y 轴单位为 KW，仅一个系列。

### 系列配色

- A 相电流: `#FF4444`（红）
- B 相电流: `#44FF44`（绿）
- C 相电流: `#4488FF`（蓝）
- 功率: `#FFAA00`（橙）
- 阈值线: `#FF0000`（红虚线）

### C# → JS 数据注入

```csharp
// MainForm 调用
webBrowser.Document.InvokeScript("loadChartData", new object[] { chartDataJson });
```

`chartDataJson` 结构：
```json
{
  "title": "1-1 道岔动作电流曲线",
  "currentEvent": {
    "timestamp": 1776243701,
    "datetime": "2026-04-15 17:01:41",
    "direction": "定位↔反位",
    "currentA": [[0, 5.6], [0.04, 1.4], ...],
    "currentB": [[0, 5.5], [0.04, 1.4], ...],
    "currentC": [[0, 2.1], [0.04, 1.5], ...],
    "power": [[0, 3.0], [0.04, 0.3], ...]
  },
  "prevEvent": { /* 同上结构，上一时间的动作 */ },
  "thresholdCurrent": 2.0,
  "thresholdPower": 1.5
}
```

### 动态 X 轴

- 默认 X 轴最大 14 秒
- 如果任一 event 的 duration > 14s → X 轴最大扩展到 30s
- 判断逻辑在 C# 端完成，作为参数传入 `xMax`

## Acceptance criteria

- [ ] 4 个图表在 2×2 网格中正确渲染
- [ ] 深色背景 `#1a1a2e`，网格线 `#333355`
- [ ] 电流图显示 A/B/C 三相 3 条曲线，分色正确
- [ ] 功率图显示 1 条功率曲线
- [ ] 鼠标悬停显示多系列 tooltip
- [ ] 红色阈值虚线在设定值处横穿图表
- [ ] 点击图例项 → 对应系列显隐切换
- [ ] X 轴 ≤ 14s 时显示 0-14，超过时扩展到 30
- [ ] 图表区随窗口缩放自适应
- [ ] IE8 VML 模式无报错（在 XP 上验证）
- [ ] 无数据时图表区显示空状态提示

## Further notes

- Highcharts 2.2.1 在 JS 端创建实例：`new Highcharts.Chart({...})`
- 数据数组格式：`[[x1, y1], [x2, y2], ...]`（790 个点）
- UI 中复选框的状态同步通过额外的 C#→JS 调用实现
- 如果 VML 渲染 790 点卡顿，前端做 stride-2 降采样到 395 点
- 参考文献：既有项目 `samplevbar.html` 的 Highcharts 用法
