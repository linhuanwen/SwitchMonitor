# 道岔监测系统 (SwitchMonitor)

铁路信号集中监测（CSM）道岔动作曲线显示与异常诊断系统。

- **技术栈**: C# .NET Framework 4.0, WinForms, WebBrowser + Highcharts, JSON
- **目标平台**: Windows XP (研华610H工控机)
> ⚠️ **XP 兼容性**: WebBrowser 方案已做 IE8 兼容处理（VML 降级、旧 JS API），但尚未在 XP 工控机上实测。如遇问题，可回退至 `08_archive_gdi\`（纯 GDI+ 自绘，100% XP 兼容）。

## 目录结构

```
SwitchMonitor/
│
├── 01_docs/                   # 需求、方案、分析、issue跟踪
│   ├── CONTEXT.md             # 领域术语表
│   ├── PRD.md                 # 产品需求文档
│   ├── 方案A_*.md / 方案B_*.md # 架构方案设计
│   ├── 项目分析总结.md         # 数据与软件分析总结
│   ├── issues/                # 开发任务拆分 (9个slice)
│   ├── diagnosis/             # 功率曲线报警分析模块 (CONTEXT + D1-D6 issues, 规划完成待开发)
│   └── *_报告.txt / *_说明.txt # 各类分析报告
│
├── 02_source/                 # 源代码与工具 (WebBrowser + Highcharts 版)
│   ├── src/                   # C# 解决方案 (2个项目)
│   │   ├── SwitchMonitor.sln
│   │   ├── SwitchMonitor.UI/    # WebBrowser 主程序 (HTML/JS 前端)
│   │   ├── SwitchMonitor.Data/  # CSV→JSON 数据层
│   │   └── SwitchMonitor.Tools/ # CLI 导入工具
│   └── tools/                 # Python 数据解析工具
│
├── 03_raw_data/               # 现场原始数据
│   ├── sanshuibei/            # CSM2010 道岔曲线 .dat (16个)
│   ├── shiqi/                 # ASCII 格式原始数据
│   ├── 本地接收目录表示/       # 表示电压模拟量 .dat
│   ├── 本地接收目录扳动/       # 开关量事件 .dat
│   ├── Station_SSB/           # 三水北站配置
│   └── 本地接收目录/           # 现场CSM软件参考副本
│
├── 04_tests/                  # 测试脚本与验证数据
│   ├── Data/                  # 测试 SQLite 数据库
│   ├── scripts/               # Python 测试辅助脚本
│   └── 验证对比数据/           # 解析结果交叉验证 (含 sanshuibei_csv)
│
├── 05_production_data/        # 运行时配置与数据
│   ├── Config/                # 道岔映射配置
│   ├── Rules/                 # 诊断规则配置 (现仅旧GDI版遗留文件; thresholds/baselines 待 D2/D3 生成)
│   ├── config.json            # Web 版配置 (8组道岔、图表颜色)
│   └── parsed_data/           # 已解析的日 JSON (8个月生产数据, 1,661文件)
│
├── 06_deploy/                 # 部署包与文档
│   ├── 部署说明.md
│   ├── build_out/             # 编译好的部署包
│   └── import_test/           # 独立导入工具
│
├── 07_logs/                   # 运行时日志
│
└── 08_archive_gdi/            # 旧版 GDI+ 自绘图表版本 (XP测试备用)
    └── README_archive.md      # 归档说明
```

## 解决方案架构

```
SwitchMonitor.UI (WinForms EXE, WebBrowser + Highcharts)
    └── SwitchMonitor.Data (CSV解析、JSON存储、数据索引)
```

- **2 个项目，12 个 .cs 文件，零外部依赖**
- 图表渲染：嵌入式 WebBrowser 控件 + Highcharts 2.2.1 (VML 降级支持 IE8)
- 数据存储：JSON 文件 (System.Web.Extensions JavaScriptSerializer)
- 前端：HTML/JS 嵌入式资源 (sidebar.html + charts.html)

## 数据流

```
CSM2010 CSV 文件 (SwitchCurve(N).csv)
        ↓
    CsvDataReader  (相位解析: A/B/C/Power)
        ↓
    DataPipeline   (按时间戳合并相位、分配方向)
        ↓
    IndexManager   (按日存储 JSON + 维护 index.json)
        ↓
parsed_data/{switchId}/{YYYY-MM-DD}.json
        ↓
    MainForm       (C# 加载 JSON → JS 渲染 Highcharts)
```

## 功能状态（2026-07-08 代码审核）

> 以下结论基于对 `02_source/src/` 的代码审核（issue 文档中的验收 checkbox 未随开发勾选，不反映实际进度）。

### 主线功能（01_docs/issues/ Slice 01-09）

| Slice | 功能 | 状态 |
|---|---|---|
| 01 | 项目脚手架/数据模型/配置 | ✅ 已实现（`SwitchMonitor.Tools/` 源码未纳入 .sln，无法随解决方案编译） |
| 02 | CSM2010 解析 → JSON | ✅ 已实现（坏行静默跳过，未按验收要求记警告日志） |
| 03 | 文件扫描+增量采集 | ❌ 未实现（由菜单"数据→导入源数据"手动全量导入替代；config 中 `scanInterval` 为死配置） |
| 04 | 主窗体+侧边栏 | ✅ 已实现（SplitContainer 固定 230px 替代 18%/82% 比例布局；日期选择升级为三级日历） |
| 05 | 2×2 Highcharts 图表 | ✅ 已实现（含缩放/平移/参考曲线/详情窗等超纲功能；X 轴 14/30 规则被 JS 端覆盖未生效） |
| 06 | 全链路交互联动 | ⚠️ 大体实现（曲线加载在 UI 线程同步执行，无防抖/取消机制） |
| 07 | 导出 PNG/CSV | ❌ 未实现（前端按钮调用的 `ExportPng`/`ExportCsv` 在 C# JSBridge 中不存在，点击静默无效果） |
| 08 | 报警阈值配置 UI | ❌ 未实现（无菜单入口、无对话框；JS 端 `updateThreshold` 钩子已备好但无人调用） |
| 09 | 道岔名称映射 | ❌ 未实现（`Config/switch_mapping.json` 为旧 GDI 版遗留，主线无加载代码） |

### 曲线报警功能现状

报警能力分两层，进度不同：

1. **静态阈值线（部分可用）**：图表按 `config.json` 的 `alarmThresholds` 绘制红色虚线，电流/功率独立启停、复选框控制显隐。但修改阈值只能手工编辑 `config.json` 后重启——Slice 08 的配置对话框未实现。
2. **功率曲线报警分析模块 Diagnosis（规划完成，代码未启动）**：设计文档见 `01_docs/diagnosis/CONTEXT.md`——基于 23,999 个历史动作事件实测统计，规划了特征提取、每台道岔基线、R0-R8 规则引擎（正常/预警/报警/故障四级）。Python 参考实现 `02_source/tools/diag_reference_check.py`（golden/baseline/dryrun）已可运行并产出验收基准。D1-D6 六个 slice 均未开工：`SwitchMonitor.Diagnosis`/`SwitchMonitor.DiagTool` 项目不存在，`Rules/thresholds.json`、`Rules/baselines.json`、`parsed_data/**/*.diag.json` 均未生成。注意 `Rules/default_rules.json` 是旧 GDI 版遗留（参考时长 5.8s 与现数据 8.7s/11.7s 不符），不作为新模块依据。

### 已知问题

- 导出按钮是"假按钮"（Slice 07），界面上可点击但无任何效果，验收时易误判。
- `charts.html` 遗留调试痕迹：右上角红色 RENDER 徽标、空状态"★ Build 2026-07-06-v24 ★"字样；窗口标题"v12"与 git 提交"V2.0"版本号不一致。
- 两个 csproj 的 Release OutputPath 硬编码为绝对路径 `D:\tool\SwitchMonitor\05_production_data\`。
- 工作区有未提交改动：配色主题（#0a0a1e→#3c3c3c）、参考曲线按转辙机独立存储、解析器 `HEADER_SIZE` 42→14 修正（parsed_data 已全量重新生成）、diagnosis 规划文档与参考脚本（均为未跟踪文件）。

## 快速开始

### 编译运行

1. 用 Visual Studio 2010+ 打开 `02_source/src/SwitchMonitor.sln`
2. 目标框架 `.NET Framework 4.0`，平台 `x86`
3. 生成解决方案
4. 确保运行目录有 `config.json` 和 `parsed_data/`
5. 按 F5 运行

### 直接运行（使用预编译包）

`06_deploy/build_out/` 已包含编译好的 SwitchMonitor.exe。

**XP 部署步骤**：
1. 将 `build_out/` 全部文件复制到工控机
2. 双击 `ie8_fix.reg` 导入 IE8 模拟注册表项
3. 修改 `config.json` 中的 `dataSourceDir` 和 `parsedDataDir` 路径
4. 运行 `SwitchMonitor.exe`

### 数据准备

```bash
# 1. 将 .dat 转换为 .csv
python 02_source/tools/parse_csm2010.py

# 2. 导入 CSV → JSON (也可用编译好的 ImportRunner.exe)
cd 06_deploy/import_test/
ImportRunner.exe config.json
```

## 相关文档

- [PRD 产品需求文档](01_docs/PRD.md)
- [方案B 技术设计](01_docs/方案B_CSharp_WinForms_方案.md)
- [功率曲线报警分析模块设计 (Diagnosis)](01_docs/diagnosis/CONTEXT.md)
- [项目分析总结](01_docs/项目分析总结.md)
- [部署说明](06_deploy/部署说明.md)
- [旧 GDI+ 版归档说明](08_archive_gdi/README_archive.md)
