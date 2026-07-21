# SwitchMonitor — 道岔监测系统 V3.0

铁路信号集中监测（CSM）道岔动作曲线分析与诊断工具。从 CSM 站机软件生成的二进制 `.dat` 文件中解析道岔动作电流/功率曲线，提供曲线展示、方向判定、参考基线管理和规则驱动的多级诊断。

- **技术栈**: C# .NET Framework 4.0, WinForms, WebBrowser + Highcharts 2.2.1, JSON
- **目标平台**: Windows XP SP3（研华 610H 工控机）
- **部署模式**: xcopy 免安装，与既有 CSM 软件零侵入并存

> ⚠️ **XP 兼容性**: WebBrowser 方案已做 IE8 兼容处理（Highcharts VML 降级、旧 JS API），但尚未在 XP 工控机上实测。如遇问题，可回退至 `08_archive_gdi/`（纯 GDI+ 自绘，100% XP 兼容）。

---

## 目录结构

```
SwitchMonitor/
├── 01_docs/                     # 需求、方案、分析、issue 跟踪
│   ├── PRD.md                   # 产品需求文档
│   ├── diagnosis/               # 功率曲线报警分析模块设计（CONTEXT + D1-D7 issues）
│   └── issues/                  # 主线开发任务拆分（Slice 01-09）
│
├── 02_source/                   # 源代码
│   ├── src/                     # C# 解决方案（6 个项目）
│   │   ├── SwitchMonitor.sln
│   │   ├── SwitchMonitor.UI/         # WinForms 主程序（5 个 .cs）
│   │   ├── SwitchMonitor.Data/       # 数据访问层（8 个 .cs）
│   │   ├── SwitchMonitor.Diagnosis/  # 诊断引擎（19 个 .cs）
│   │   ├── SwitchMonitor.DiagTool/   # CLI 诊断工具
│   │   ├── SwitchMonitor.Tools/      # CLI 导入工具
│   │   └── SwitchMonitor.Tests/      # 测试套件（D4-D7）
│   └── tools/                   # Python 数据勘探/导入脚本（10 个）
│
├── 03_raw_data/                 # 原始监测数据
│   ├── sanshuibei/              # CSM2010 道岔曲线 .dat（16 个文件，8 组道岔）
│   ├── shiqi/                   # ASCII 格式监测数据
│   ├── 本地接收目录扳动/         # 开关量事件 Digit(*).dat
│   ├── 本地接收目录表示/         # 表示电压模拟量
│   ├── Station_SSB/             # 车站配置（digit.ini 等）
│   └── 本地接收目录/             # 现场 CSM 软件参考副本
│
├── 04_tests/                    # 测试脚本与验证数据
│
├── 05_production_data/          # 运行时配置与生产数据
│   ├── config.json              # 主配置（8 组道岔、阈值、颜色）
│   ├── Config/                  # switch_mapping.json + switch_digit_config.json
│   ├── Rules/                   # 诊断规则引擎配置
│   │   ├── default_rules.json   # 规则定义（R1-R8 + CR001）
│   │   ├── thresholds.json      # 阈值参数
│   │   ├── baselines.json       # 功率基线（分 switchId|方向）
│   │   ├── current_baselines.json # 电流基线
│   │   ├── provenance_index.json  # 基线来源索引
│   │   ├── reference_curves/    # 参考曲线文件
│   │   └── knowledge_base/      # 知识库（规则 CR001 + 文献条目 KB001-KB002）
│   └── parsed_data/             # 解析后的日 JSON（8 组道岔 × 约 180 天）
│       ├── 1-J/  1-X/           # 1#道岔 尖轨/心轨
│       ├── 2-J/  2-X/           # 2#道岔 尖轨/心轨
│       ├── 3-J/  3-X/           # 3#道岔 尖轨/心轨
│       └── 4-J/  4-X/           # 4#道岔 尖轨/心轨
│
├── 06_deploy/                   # 部署产出与文档
│
├── 07_logs/                     # 运行时日志
│
└── 08_archive_gdi/              # 旧版 GDI+ 自绘方案（完整 6 项目，XP 备用）
    └── README_archive.md
```

---

## 解决方案架构

```
SwitchMonitor.UI (WinForms EXE — WebBrowser + Highcharts 图表)
    ├── SwitchMonitor.Data        (CSM2010 解析、Digit 开关量、文件索引、配置管理)
    ├── SwitchMonitor.Diagnosis   (特征提取、基线构建、规则引擎、趋势分析)
    └── System.Web.Extensions     (JavaScriptSerializer — 零第三方依赖)

SwitchMonitor.DiagTool (CLI — 离线诊断/基线/自检)
    ├── SwitchMonitor.Data
    └── SwitchMonitor.Diagnosis

SwitchMonitor.Tools (CLI — CSV 导入)
    └── SwitchMonitor.Data

SwitchMonitor.Tests (NUnit 测试套件)
    ├── D4Tests … D7Tests
    └── TestRunner (自实现断言)
```

**总计**: 6 个 C# 项目，约 40 个 `.cs` 源文件，**运行时零第三方 NuGet 依赖**。

---

## 数据流

```
CSM2010 二进制 .dat (SwitchCurve(N).dat)
        ↓  [parse_csm2010.py / C# DataPipeline]
按时间戳合并 A/B/C 电流 + 功率相位
        ↓
开关量 Digit(*).dat + digit.ini 点号配置
        ↓  [DirectionResolver: DB/FB 状态机]
定位→反位 / 反位→定位 判定
        ↓
parsed_data/{switchId}/{YYYY-MM-DD}.json   ← 每日动作事件
        ↓
特征提取  →  features.json + current_features.json
        ↓
基线构建  →  baselines.json + current_baselines.json
        ↓
规则引擎  →  {YYYY-MM-DD}.diag.json   ← 诊断结果
        ↓
MainForm C# → Highcharts 前端渲染（2×2 图表 + 曲线详情弹窗）
```

---

## 道岔命名规范（V2.0）

| 编号 | 含义 | 说明 |
|------|------|------|
| `1-J` | 1号道岔**尖**轨 | J = 尖轨 (Jian gui / Switch Point Rail) |
| `1-X` | 1号道岔**心**轨 | X = 心轨 (Xin gui / Frog / Crossing Nose) |
| `2-J` | 2号道岔尖轨 | |
| `2-X` | 2号道岔心轨 | |
| `3-J` | 3号道岔尖轨 | |
| `3-X` | 3号道岔心轨 | |
| `4-J` | 4号道岔尖轨 | |
| `4-X` | 4号道岔心轨 | |

命名来源：CSM 站机 `digit.ini` 配置文件，与监测终端命名完全一致。

---

## 功能状态

### ✅ 已实现

| 模块 | 功能 | 说明 |
|------|------|------|
| **数据解析** | CSM2010 二进制道岔曲线解析 | 16 个 .dat 文件 → 8 组道岔（电流 A/B/C + 功率 4 相位合并） |
| | Digit 开关量解析 | 从 Digit(*).dat + digit.ini 提取 DB/FB 状态判定动作方向 |
| | 多格式支持 | CSM2010 .dat / shiqi ASCII / 本地接收目录 |
| **数据管理** | 按日 JSON 存储 | `parsed_data/{switchId}/{YYYY-MM-DD}.json` |
| | 增量索引 | `index.json` 快速日期定位 |
| | 文件监听导入 | 菜单「数据→导入源数据」全量/增量导入 |
| **曲线展示** | 2×2 Highcharts 图表 | 电流 A/B/C 三相 + 功率曲线，独立 Y 轴 |
| | 曲线缩放/平移 | Highcharts zoom 交互 |
| | 参考曲线叠加 | 按 switchId 加载参考曲线到图表对比 |
| | 三级日历选日 | 年→月→日 三级日期选择器 |
| | 曲线详情弹窗 | 双击曲线打开 ChartDetailForm 大图 |
| | 动作列表 | 侧边栏展示当天所有动作事件，点击切换 |
| **诊断功能** | 特征提取 (D1) | 功率特征 (Duration/Spike/StepRatio 等) + 电流特征 (三相同时刻点) |
| | 基线构建 (D2-D3) | 按 switchId|方向 分离基线（功率 + 电流），中位数统计 |
| | 诊断规则引擎 (D4) | JSON 驱动规则：R1 超时、R2 缩短、R3 波动、R4-R5 过流、R6 台阶、R7-R8 新增 |
| | 四级告警 | 正常 → 预警 → 报警 → 故障 |
| | 知识库规则 CR001 | 基于尾部平台检测的二极管击穿诊断（含 EMD/1D-CNN/LSTM 文献支撑） |
| | 趋势分析 (D6) | FeaturesStore 时间序列趋势检测 |
| | 电流基线扩展 (D7) | CurrentFeaturesStore + CurrentBaseline 独立电流维度基线 |
| | 诊断 UI | 工具菜单 → 诊断参数设置（DiagParamForm） |
| | 重跑诊断 | 工具菜单 → 重新诊断当前数据 |
| | 设定基准曲线 | 工具菜单 → 一键重建全量基线（分方向） |
| **CLI 工具** | DiagTool (6 命令) | `selftest` 金标准自检 / `baseline` 基线生成 / `dryrun` 规则演习 / `trend` 趋势 / `refcurve` 参考曲线 / `profilecheck` 剖面检查 |
| | ImportRunner | 独立 CSV 导入工具 |
| **Python 工具** | 10 个脚本 | 数据解析（CSM2010/shiqi/digit）、全量导入、基线参考实现、诊断演习 |
| **配置** | JSON 驱动 | 道岔组、阈值、颜色、UI 参数、诊断规则全部 JSON 可配置 |
| **日志** | 运行日志 | `07_logs/` 滚动日志记录 |

### ⚠️ 部分完成

| 模块 | 状态 | 详情 |
|------|------|------|
| **导出功能** | 前端按钮就绪，C# 端未实现 | HTML 中 `exportPng()`/`exportCsv()` 调用 `window.external.ExportPng/Csv`，但 MainForm 无对应方法，点击静默无效果 |
| **全链路交互** | 大体实现，缺防抖 | 曲线加载在 UI 线程同步执行，快速切换时有卡顿 |
| **增量文件扫描** | 手动导入替代 | `scanInterval` 配置存在但未驱动自动扫描 |

### ❌ 未实现

| 功能 | 关联 Slice | 说明 |
|------|-----------|------|
| 报警阈值配置 UI | Slice 08 | 阈值当前只能手改 `config.json` 后重启 |
| 道岔名称映射 UI | Slice 09 | `Config/switch_mapping.json` 为旧版遗留，主线无加载代码 |
| 增量自动采集 | Slice 03 | 由手动"导入源数据"替代 |

---

## 快速开始

### 编译

```bash
# 要求: .NET Framework 4.0 SDK + Visual Studio 2010+
cd 02_source/src
msbuild SwitchMonitor.sln /p:Configuration=Release /p:Platform=x86
```

或使用 `dotnet build`（.NET SDK 6+ 带 Framework 4.0 兼容）：

```bash
dotnet build 02_source/src/SwitchMonitor.sln
```

### 运行

1. 确保运行目录有 `config.json` 和 `parsed_data/`
2. 修改 `config.json` 中 `dataSourceDir` 和 `parsedDataDir` 为实际路径
3. 运行 `SwitchMonitor.exe`

### 数据准备

```bash
# 1. 解析 CSM2010 .dat → 分日 JSON
python 02_source/tools/import_all_data.py

# 2. 解析 digit.ini → 开关量点号配置
python 02_source/tools/parse_digit_ini.py

# 3. 生成功率基线
cd 02_source/src/SwitchMonitor.DiagTool/bin/Debug/net40
SwitchMonitor.DiagTool.exe baseline ../../../05_production_data/parsed_data

# 4. 规则演习（dryrun）
SwitchMonitor.DiagTool.exe dryrun ../../../05_production_data/parsed_data ../../../05_production_data/Rules
```

### XP 部署

1. 将编译输出复制到工控机
2. 双击 `ie8_fix.reg` 导入 IE8 模拟注册表
3. 修改 `config.json` 路径
4. 运行 `SwitchMonitor.exe`

---

## 诊断规则一览

| 规则 | 名称 | 严重级别 | 触发条件 |
|------|------|---------|---------|
| R1 | 动作超时 | 🔴 故障 | 时长超过基线 3s 以上 |
| R2 | 动作缩短 | 🟠 报警 | 时长低于基线 60% |
| R3 | 曲线波动 | 🟡 预警 | 最大偏离超过基线 0.5s 的窗口 |
| R4 | 启动电流过流 | 🟠 报警 | 启动峰值超过基线 30% |
| R5 | 转换电流过流 | 🟠 报警 | 转换均值超过基线 30% |
| R6 | 台阶异常 | 🟡 预警 | 台阶比超过 [0.67, 1.5] 范围 |
| R7 | 锁定电流异常 | 🟠 报警 | 锁定段均值偏离基线 |
| R8 | 尾部电流异常 | 🟡 预警 | 尾部均值偏离基线 |
| CR001 | 二极管击穿 | 🔴 故障 | 尾部平台检测 + 三相电流关系 |

---

## 相关文档

- [PRD 产品需求文档](01_docs/PRD.md)
- [诊断模块设计](01_docs/diagnosis/三模块诊断架构设计.md)
- [诊断上下文规格](01_docs/diagnosis/CONTEXT.md)
- [方案B 技术设计](01_docs/方案B_CSharp_WinForms_方案.md)
- [部署说明](06_deploy/部署说明.md)
- [旧 GDI+ 版归档](08_archive_gdi/README_archive.md)

---

<p align="center"><b>SwitchMonitor V3.0</b> — 道岔监测系统 · 2026-07</p>
