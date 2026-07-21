# N01-3 启动提示（复制到新窗口）

---

/tdd

SwitchMonitor 是铁路道岔监测系统（WinForms + .NET 4.0 + WinXP 工控机）。正在做多站组网功能。

## 本窗口只做一件事

**网络层接收端**——集成到 SwitchMonitor 主程序的网络接收和监控能力：
1. `POST /api/receive` 接收端点，收到推送数据写入本地 SQLite
2. 每 2 分钟主动探测各站机 `/api/status`，超时 10 秒
3. 站机从离线恢复后自动补拉数据
4. 手动补拉按钮触发逻辑

**前提**：N01-2（DataForwarder）已完成，站机端 status/events 端点可用。

## 先读

- 架构概览：[01_docs/design/多站组网架构设计.md](01_docs/design/多站组网架构设计.md)（重点第 2、3 节）
- 详细需求：[01_docs/issues/N01-3-network-layer.md](01_docs/issues/N01-3-network-layer.md)

## 测试接缝

接缝是接收端的外部行为。测试打在：
- POST 数据到 `/api/receive` → SQLite 可查到写入的数据
- 模拟站机 `/api/status` 返回正常 → 标记在线
- 模拟站机 `/api/status` 超时 2 次 → 标记离线，事件通知触发
- 离线→在线 → 自动调 `GET /api/events?since=xxx` → 补拉成功
- 补拉中途断开 → lastTimestamp 不更新 → 下次从同一 since 重拉
- 重复数据（同 stationId+switchId+timestamp）→ INSERT OR IGNORE

## 要改的代码

**新增**类库或直接在现有项目中新增：
- `ReceiveEndpoint.cs` — HttpListener 接收 POST /api/receive，写入 SQLite
- `StationMonitor.cs` — 定时器驱动，每 2 分钟探测所有站机，管理在线/离线状态
- `DataCatcher.cs` — 自动补拉 + 手动补拉逻辑，进度回调

**集成点**：在 SwitchMonitor.UI 启动时初始化接收端和定时器。

## 约束

.NET 4.0, x86, WinXP。接收端在后台线程处理，不阻塞 UI。补拉进度通过 event/callback 通知。lastTimestamp 以站为单位独立维护。
编译：`dotnet build -c Release`
