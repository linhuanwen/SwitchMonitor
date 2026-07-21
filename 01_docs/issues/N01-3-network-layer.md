# N01-3: 网络层 — 接收端 + 活性检测 + 自动补拉

> **状态**: 待实现
> **依赖**: N01-1, N01-2
> **阻塞**: N01-4

## Type

Feature

## What to build

SwitchMonitor 主程序内置的网络接收和监控能力：

### 1. 接收端点

在 SwitchMonitor 启动时启动一个后台 HttpListener：

```
POST /api/receive
  → 解包 gzip JSON
  → 按 stationId 写入对应 SQLite
  → 更新内存中的 lastTimestamp
  → 返回 { "received": N }
```

### 2. 主动探测

总终端 / 班组终端每 **2 分钟** 向配置中每个站机发 `GET /api/status`：

- 超时 10 秒
- 有响应 → 在线，记录心跳时间
- 无响应 → 下次重试
- 连续 2 次失败 → 标记离线，触发 UI 告警
- 离线→在线转换 → 自动触发补拉

### 3. 自动补拉

```
检测到站机恢复在线:
  GET /api/events?since={lastTimestamp}
  → 按时间升序逐条接收
  → 逐条写入 SQLite（边收边写）
  → 全部完成后 → 持久化 lastTimestamp
  → 中途失败 → 不更新 lastTimestamp（下次重拉不丢）
```

### 4. 手动补拉按钮

UI 上每个站点的上下文菜单/按钮 → "补拉数据" → 触发同上的补拉逻辑 → 显示进度。

### 5. 接收端线程安全

- 后台接收 + 主动探测 + 补拉可能并发
- SQLite 写入需要锁保护（同进程内可接受）
- 状态变更通过事件通知 UI 线程刷新

## Acceptance criteria

- [ ] POST /api/receive 接收数据 → 写入 SQLite → 查询可返回正确数据
- [ ] 主动探测 2 分钟一次，10 秒超时，连续 2 次失败后 UI 站点标红
- [ ] 模拟站机离线 5 分钟 → 重新上线 → 自动补拉成功，数据无缺失
- [ ] 补拉中途断开 → 再次恢复 → 从同一 since 重新补拉，无重复无遗漏
- [ ] 手动补拉按钮触发补拉，显示进度（"正在补拉XX站…已拉 N 条"）
- [ ] 接收端不影响主程序 UI 响应（后台线程处理）

## Further notes

- 补拉进度通过 event/callback 通知 UI，不影响接收线程阻塞
- 重复数据（同 stationId + switchId + timestamp）做 INSERT OR IGNORE 去重
- 总终端 lastTimestamp 以站为单位独立维护，存在每个站对应的 SQLite 旁
