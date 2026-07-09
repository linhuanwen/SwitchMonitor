# CLAUDE.md — 道岔监测系统 (SwitchMonitor)

> AI 辅助开发上下文文档。每次会话开始时读取此文件以了解项目概况。

## 项目概要

铁路信号集中监测系统（CSM）道岔动作曲线分析与诊断工具。从 CSM 站机软件生成的二进制 `.dat` 文件中解析道岔动作电流/电压/功率曲线，提供曲线展示、参考曲线管理和可配置的异常诊断引擎。

- **目标平台**: 研华 610H 工控机, Windows XP, .NET Framework 4.0
- **部署模式**: xcopy 部署，与既有 CSM 软件零侵入并存

## 目录结构

```
SwitchMonitor/
├── CLAUDE.md                  # 本文件
├── 01_docs/                   # 项目文档（PRD、方案、分析报告）
│   └── issues/                # 开发任务/issues
├── 02_source/
│   ├── tools/                 # Python 解析/导入工具脚本
│   │   ├── parse_csm2010.py       # CSM2010 道岔曲线二进制解析
│   │   ├── parse_shiqi.py         # shiqi ASCII 数据解析
│   │   ├── parse_local_receive.py # Digit + DCBSDYAnalog 解析
│   │   └── import_real_data.py    # 真实数据 → SQLite 导入
│   └── src/                   # C# .NET 4.0 WinForms 源码
│       ├── SwitchMonitor.sln
│       ├── SwitchMonitor.UI/      # WinForms 主程序
│       ├── SwitchMonitor.Data/    # 数据访问层（解析、配置、管道）
│       └── SwitchMonitor.Tools/   # 导入工具
├── 03_raw_data/               # 原始二进制/ASCII 监测数据
│   ├── sanshuibei/            # CSM2010 道岔曲线（16 个 .dat，8 组道岔）
│   └── shiqi/                 # ASCII 监测数据（.elc/.vol/.pow/.factor/.rst）
├── 04_tests/
│   ├── scripts/               # 测试脚本（create_test_db.py, write_tests.py）
│   ├── Data/                  # 测试 SQLite 数据库
│   └── generated/             # write_tests.py 生成的 C# 测试代码
├── 05_production_data/
│   ├── Config/switch_mapping.json  # 道岔 → 文件索引映射
│   └── Rules/default_rules.json    # 诊断规则配置
├── 06_deploy/                  # 部署产出
│   ├── build_out/              # 解析后的 JSON 数据 + IE8 注册表修复
│   ├── import_test/            # ImportRunner 导入工具 + config.json
│   └── 部署说明.md
├── 07_logs/                    # 运行日志（含历史崩溃记录）
└── 08_archive_gdi/             # GDI+ 完整版归档（含全部 6 个 C# 项目）
    ├── SwitchMonitor.UI/       # GDI+ 自绘曲线版本
    ├── SwitchMonitor.Data/     # SQLite + 二进制解析完整实现
    ├── SwitchMonitor.Common/   # 公共 POCO 模型 + 配置类
    ├── SwitchMonitor.Diagnosis/ # IDiagnosisEngine 接口
    ├── SwitchMonitor.Tests/    # NUnit 测试套件
    └── DiagnosisEngine/        # 诊断引擎实现

## 技术栈

| 层面 | 选型 | 说明 |
|---|---|---|
| 语言 | C# 4.0 + Python 3 | C# 主程序，Python 用于数据勘探/导入 |
| 框架 | .NET Framework 4.0 | XP 兼容上限 |
| 界面 | WinForms + WebBrowser + Highcharts 2.x | IE8 VML 降级渲染 |
| 数据存储 | SQLite (System.Data.SQLite) | 免安装单文件 |
| 配置格式 | JSON (JavaScriptSerializer) | 内置，无需第三方库 |
| 图表库 | Highcharts 2.2.1 | 项目已有，位于 `02_source/src/SwitchMonitor.UI/Js/` |

## 关键数据格式

### CSM2010 道岔曲线 (SwitchCurve*.dat)
- 魔数: `CSM2010\x00`
- 24 字节索引记录 → 数据块（4B timestamp + 4B flags + 4B sample_count + 采样值）
- flags: `16777216`=A相, `33554432`=B相, `50332416`=C相, `0`=功率
- 采样率: 25 Hz，每相约 150-790 个采样点

### Digit 开关量 (Digit*.dat)
- 变长记录: 4B timestamp LE + 2B record_type BE + N×2B point_data BE
- point_data: 高字节=state_byte (0x2f/0x00), 低字节=point_id

### shiqi ASCII 数据
- 每事件 5 文件: `.elc`(电流) + `.vol`(电压) + `.pow`(功率) + `.factor`(功率因数) + `.rst`
- 格式: `A=1.0,2.0,3.0\nB=...\nC=...`

## C# 项目状态

项目有两个版本的 C# 实现：

### 当前开发版（`02_source/src/`）— Highcharts/WebBrowser 方案
- `SwitchMonitor.UI` — MainForm, ChartDetailForm, Program, HTML/JS 内嵌页面
- `SwitchMonitor.Data` — CurveData, CsvDataReader, DataPipeline, ConfigManager, IndexManager, Logger
- `SwitchMonitor.Tools` — ImportTool
- 编译输出在 `bin/Debug/` 和 `bin/Release/` (x86)

### 归档完整版（`08_archive_gdi/`）— GDI+ 自绘方案
完整实现了 PRD 中描述的 6 个项目：
- `SwitchMonitor.UI` — MainForm, CurveChartPanel（GDI+ 自绘）, AlarmThresholdForm, StatusTimelinePanel, ExportService, DiagnosisResultPanel, ReferenceCurveManagementForm
- `SwitchMonitor.Data` — SwitchCurveParser, DigitParser, CsvCurveParser, DataRepository, QueryService, FileWatcherService, DatabaseInitializer, ProcessedFileRepository
- `SwitchMonitor.Common` — SwitchActionData, SamplePoint, DiagnosisResult, AlarmThresholdConfig, ThresholdValidator, CurveSampleRecord 等 POCO
- `SwitchMonitor.Diagnosis` — IDiagnosisEngine 接口定义
- `DiagnosisEngine` — DiagnosisEngine, DummyDiagnosisEngine, RuleConfig
- `SwitchMonitor.Tests` — Slice2~Slice11 测试套件 + AlarmThresholdTests

此版本功能更完整但已被 Highcharts 方案替代（原因见 [方案B_CSharp_WinForms_方案.md](01_docs/方案B_CSharp_WinForms_方案.md) v2.0 变更记录）。

## 已知问题

1. **道岔映射待确认**: `switch_mapping.json` 中的 point_id ↔ 实际道岔对应关系均为占位符
2. **崩溃记录**: `crash.log` 显示 `MainForm.OnPhaseSwitchClick` 中有 `InvalidCastException`（ToolStripLabel → ToolStripButton 强制转换失败）
3. **数据库路径**: `diag.log` 中记录的是旧路径 `D:\tool\UltraEdit\Data\switch_test.db`，重新运行后会自动更新

## 路径约定

- Python 脚本使用 `Path(__file__).parent.parent.parent` 定位项目根目录
- C# 配置中的相对路径（如 `DataSourceDir`）相对于程序运行目录
- 所有硬编码路径已从旧位置 `D:\tool\UltraEdit\` 更新为当前项目相对路径

## 开发工作流

1. Python 脚本用于数据勘探和一次性导入，运行前确保 `03_raw_data/` 下有对应数据
2. C# 解决方案用 VS2010 或兼容 IDE 打开 `02_source/src/SwitchMonitor.sln`
3. 测试数据库由 `04_tests/scripts/create_test_db.py` 生成
4. 诊断规则编辑 `05_production_data/Rules/default_rules.json` 即可生效（JSON 驱动，无需重编译）
