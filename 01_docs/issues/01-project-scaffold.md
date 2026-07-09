# Slice 1: 项目脚手架 + 数据模型 + 配置加载

> **状态（2026-07-08 代码审核）**: ✅ 已实现。偏差：采样属性为 `List<double[]>`（[t,v] 对）而非 `List<double>`；`SwitchMonitor.Tools/` 仅有游离源码 `ImportTool.cs`，无 .csproj、未纳入 .sln。

## Type

AFK

## Blocked by

None — 可立即开始

## What to build

创建 VS2010 解决方案 `SwitchMonitor.sln`，包含 UI 和 Data 两个项目及依赖关系，定义数据模型 POCO、配置文件加载、中间数据目录初始化。

### 项目结构

```
SwitchMonitor.sln
├── SwitchMonitor.UI              # WinForms 主程序 (.exe)
│   ├── Program.cs                # 入口
│   ├── MainForm.cs               # 主窗口 (TableLayoutPanel)
│   ├── Html/                     # 内嵌 WebBrowser 的 HTML 资源
│   └── Js/                       # jQuery + Highcharts（从项目已有素材复制）
└── SwitchMonitor.Data            # 数据层 (.dll)
    ├── CurveData.cs              # 数据模型 POCO
    ├── ConfigManager.cs          # JSON 配置加载
    └── IndexManager.cs           # 中间数据索引维护
```

依赖方向：UI → Data

### 公共数据模型 (SwitchMonitor.Data)

- `SwitchEvent`: 一次道岔动作的完整数据
  - `long Timestamp`, `string DateTimeStr`, `string Direction`
  - `double Duration`, `double SampleInterval`
  - `List<double> CurrentA`, `List<double> CurrentB`, `List<double> CurrentC`, `List<double> Power`
  - `int SampleCount` — 采样点数
- `DayIndex`: 某转辙机某天的所有动作时间戳列表
  - `string SwitchId`, `string Date`, `List<long> Timestamps`
- `SwitchGroup`: 转辙机组配置项
  - `string Id`, `string Label`, `int DataFileIndex`
- `AlarmThreshold`: 阈值配置
  - `bool Enabled`, `double Value`, `string Unit`

### 配置文件

`config.json`（放在 .exe 同目录）：
- `switchGroups[]` — 转辙机组列表
- `dataSourceDir` — 原始 .dat 目录
- `parsedDataDir` — 中间 JSON 目录
- `scanInterval` — 扫描间隔（秒）
- `alarmThresholds.current/power` — 报警阈值
- `chartColors.*` — 图表配色
- `ui.*` — UI 参数

### 目录初始化

程序启动时确保 `parsedDataDir` 存在，不存在则自动创建。

### 引用的现有素材

从 `本地接收目录/DataFile/Station_ZQZ/js/` 复制到项目 `Js/` 目录（作为嵌入资源随 exe 分发）：
- `jquery.js` (jQuery 1.x)
- `highcharts.js` (Highcharts 2.2.1)

## Acceptance criteria

- [ ] 解决方案包含 2 个项目，编译无错误
- [ ] 项目引用关系正确（UI 引用 Data）
- [ ] 所有 POCO 类定义完整，属性类型正确
- [ ] `ConfigManager.LoadConfig()` 能正确解析 `config.json`
- [ ] 程序启动时自动创建 `parsed_data/` 目录
- [ ] jQuery + Highcharts 作为嵌入资源嵌入到 UI 项目中

## Further notes

- 目标框架：`.NET Framework 4.0`
- 平台目标：x86（兼容 XP 工控机）
- JSON 序列化使用 `Newtonsoft.Json`（.NET 4.0 兼容版本）或内置 `JavaScriptSerializer`
- 配置文件编码：UTF-8 without BOM
