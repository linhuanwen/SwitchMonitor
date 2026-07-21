# N01-2 启动提示（复制到新窗口）

---

/tdd

SwitchMonitor 是铁路道岔监测系统（WinForms + .NET 4.0 + WinXP 工控机）。正在做多站组网功能。

## 本窗口只做一件事

**DataForwarder.exe** ——独立的站机后台转发进程：
1. 每 1 秒轮询 SQLite，发现新数据 → gzip 打包 → POST 给所有 subscribers
2. 响应 HTTP 请求：`GET /api/status` 和 `GET /api/events?since=xxx`

**前提**：N01-1（SQLite 存储层）已完成，events 表可读写。

## 先读

- 架构概览：[01_docs/design/多站组网架构设计.md](01_docs/design/多站组网架构设计.md)（重点第 2 节"通信协议"、第 6 节"进程架构"）
- 详细需求：[01_docs/issues/N01-2-dataforwarder.md](01_docs/issues/N01-2-dataforwarder.md)

## 测试接缝

DataForwarder 的接缝是它的 HTTP 端点。测试打在：
- `GET /api/status` → 返回正确的 stationId、lastTimestamp
- 往 SQLite 插入新行 → 2 秒内 DataForwarder POST 到 localhost 接收端
- 1 秒合并窗口内插 5 行 → 打包为 1 个 POST
- `GET /api/events?since=xxx` → 返回所有增量事件，gzip 压缩
- subscriber 离线 → 重试 3 次后放弃，不阻塞其他 subscriber
- `.sync_state.json` 正确持久化推送进度

## 要改的代码

**新增** `DataForwarder` 控制台项目（编译为 Windows 应用程序，隐藏窗口）：
- `Program.cs` — 启动 HttpListener + 定时器轮询 + 读取 config.json
- `PushLoop.cs` — 轮询 SQLite → 合并窗口 → gzip → POST → 更新 sync_state
- `ApiHandlers.cs` — `/api/status` 和 `/api/events` 处理逻辑

## API 契约

```
GET /api/status → {"stationId":"SSB","stationName":"三水北站","status":"ok","lastTimestamp":1712345678,"dbSizeMB":320}
GET /api/events?since=1712340000 → {"stationId":"SSB","since":...,"count":5,"events":[...]}（gzip）
```

## 约束

.NET 4.0, x86, WinXP。HttpListener 内置，gzip 用 System.IO.Compression 内置。对 SQLite 只读不写。sync_state 存独立文件 `.sync_state.json`，不写 SQLite。内存 < 10MB，CPU 空闲时 < 1%。
编译：`dotnet build -c Release`
