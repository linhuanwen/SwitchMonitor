# N01 多站组网 — 新窗口 TDD 实现

在新 Claude Code 窗口中粘贴下面整段文字即可开始。

---

/tdd

SwitchMonitor 是铁路道岔监测系统（WinForms + .NET 4.0 + WinXP 工控机）。目前纯单机运行，需要增加局域网多站互通能力。请用 TDD 方式逐步实现。

## 先读这两个文档

- 架构设计：[01_docs/design/多站组网架构设计.md](01_docs/design/多站组网架构设计.md)（20 项决策记录）
- PRD：[01_docs/issues/N01-multi-station-networking.md](01_docs/issues/N01-multi-station-networking.md)

## 测试接缝（已确认）

两个接缝，测试全部打在这上面：
1. **HTTP API** — DataForwarder 的 `/api/status`、`/api/events` 和 `/api/receive`，通过 localhost 模拟通信
2. **SQLite 存储层** — events 表的读写、BLOB 往返、数据清理

## 实现工单（按依赖顺序，逐个 TDD）

每个工单详细内容在 `01_docs/issues/N01-{编号}-*.md`，先通读再动手。

| 顺序 | 工单 | 核心交付 |
|------|------|---------|
| 1 | N01-5 配置模型升级 | role/vendorType/switchType/subscribers 新字段 |
| 2 | N01-1 SQLite 存储层 | events 表 + BLOB 读写 + JSON 迁移脚本 |
| 3 | N01-2 DataForwarder.exe | 轮询推送 + status/events API |
| 4 | N01-3 网络层接收端 | POST receive + 2分钟探测 + 自动补拉 |
| 5 | N01-4 UI 集成 | 站点切换 + 在线状态灯 + 清理对话框 |
| 6 | N01-6 集成测试 | 8 个场景，localhost 全流程验证 |

**做法**：红→绿→重构，一次一个工单。先写失败的测试，再写最小实现让它通过。N01-5 和 N01-1 可并行切入。

## 关键约束

- 目标平台：研华 610H，WinXP SP3，.NET Framework 4.0，x86
- 零第三方 NuGet 依赖（HttpListener / JavaScriptSerializer / GZipStream 全内置）
- 唯一额外文件：System.Data.SQLite.dll（2MB，xcopy 扔 `06_deploy/release/`）
- UI 前端是 WebBrowser + IE8 + Highcharts 2.x，ES5 兼容
- 编译：`dotnet build -c Release`，输出到 `06_deploy/release/`
