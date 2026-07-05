# 道岔监测系统 (SwitchMonitor)

铁路信号集中监测（CSM）道岔动作曲线显示与异常诊断系统。

- **技术栈**: C# .NET Framework 4.0, WinForms, WebBrowser + Highcharts, JSON
- **目标平台**: Windows XP (研华610H工控机)
- **目标车站**: 三水北 (SSB), 广佛肇城际铁路

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
│   ├── Rules/                 # 诊断规则配置
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
- [项目分析总结](01_docs/项目分析总结.md)
- [部署说明](06_deploy/部署说明.md)
- [旧 GDI+ 版归档说明](08_archive_gdi/README_archive.md)
