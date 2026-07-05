# 旧版 GDI+ 版本 - 归档说明

## 这是什么

这是道岔监测系统的**第一版 GDI+ 自绘图表版本**，已归档备用。

## 为什么保留

新版 WebBrowser + Highcharts 版本（`02_程序模块\src\`）尚未在 Windows XP 工控机（研华 610H）上实测。如果 WebBrowser 方案在 XP 上出现兼容性或性能问题，可以回退使用此版本。

**如 Web 版 XP 测试通过，可删除此归档。**

## 架构

```
SwitchMonitor.UI (WinForms EXE, GDI+ 自绘图表)
    ├── SwitchMonitor.Data (SQLite CRUD, FileWatcherService)
    │     └── SwitchMonitor.Common (共享模型)
    ├── SwitchMonitor.Diagnosis (IDiagnosisEngine 接口)
    └── DiagnosisEngine (规则诊断引擎，可独立替换)
```

## 项目清单 (6 个项目, 38 .cs 文件)

| 项目 | 文件数 | 说明 |
|------|--------|------|
| SwitchMonitor.Common | 12 .cs | 公共模型、配置类型、阈值验证 |
| SwitchMonitor.Data | 14 .cs | 数据访问层：SQLite、FileWatcher、解析器 |
| SwitchMonitor.Diagnosis | 1 .cs | 诊断引擎接口 |
| SwitchMonitor.UI | 7 .cs | WinForms 主程序（GDI+ 自绘图表） |
| DiagnosisEngine | 4 .cs | 诊断引擎实现 |
| SwitchMonitor.Tests | 14 .cs | NUnit 测试 |

## 技术栈

- C# .NET Framework 4.0, x86
- WinForms GDI+ 自绘图表 (CurveChartPanel)
- SQLite (sqlite3.dll + NativeSqlite)
- Newtonsoft.Json, NLog
- 数据格式: .dat 二进制 → SQLite

## 与 Web 版的主要差异

| | GDI+ 版 (此归档) | Web 版 (02_程序模块) |
|---|---|---|
| 图表 | GDI+ System.Drawing 自绘 | WebBrowser + Highcharts.js |
| 存储 | SQLite | JSON 文件 |
| 数据输入 | .dat 二进制 | CSV 文本 |
| 依赖 | Newtonsoft.Json, sqlite3.dll, NLog | 无 |
| 诊断引擎 | 有 (可配置规则) | 无 |
| 前端修改 | 改 C# 重新编译 | 改 HTML/JS 即可 |
