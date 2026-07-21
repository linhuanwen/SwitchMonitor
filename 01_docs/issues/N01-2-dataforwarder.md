# N01-2: DataForwarder.exe — 站机转发进程

> **状态**: 待实现
> **依赖**: N01-1
> **阻塞**: N01-3, N01-4

## Type

Feature

## What to build

独立的 Windows 应用程序 `DataForwarder.exe`，在站机后台运行，负责：

1. 监控 SQLite 新增数据 → 打包推送
2. 响应 HTTP 请求（status / events）

### 推送循环

```
每 1 秒:
  SELECT * FROM events WHERE timestamp > lastPushedTimestamp
  如果有新行:
    等待 mergeWindowMs（默认 1000ms）收集更多
    合并 → gzip 压缩 → POST 给所有 subscribers
    成功后更新 lastPushedTimestamp → 写入 .sync_state.json
```

### HTTP 端点（使用 HttpListener）

**GET /api/status**
```json
{
  "stationId": "SSB",
  "stationName": "三水北站",
  "status": "ok",
  "lastTimestamp": 1712345678,
  "dbSizeMB": 320
}
```

**GET /api/events?since={timestamp}**
- 从 SQLite 查询 `WHERE timestamp > since` 的所有事件
- 按 timestamp 升序排列
- gzip 压缩返回

### 同步状态

`.sync_state.json` 独立文件（不是 SQLite 表），存储格式：
```json
{
  "SSB": {
    "192.168.1.100:9000": 1712345678,
    "192.168.1.11:9000": 1712345670
  }
}
```
每个 subscriber 独立记录推送进度，一个断了不影响另一个。

### 重试策略

POST 失败 → 重试 3 次（间隔 2s / 4s / 8s）→ 仍失败则放弃该条，记录日志，等待对方恢复后的补拉兜底。

### 配置来源

读取 `config.json` 中的 `stationId`, `stationName`, `listenPort`, `subscribers`, `mergeWindowMs`。

## Acceptance criteria

- [ ] 往 SQLite 插入新行 2 秒内，DataForwarder POST 到接收端
- [ ] 1 秒合并窗口内插入 5 行 → 打包为 1 个 POST
- [ ] `/api/status` 返回正确 stationId 和 lastTimestamp
- [ ] `/api/events?since=xxx` 返回所有增量事件，gzip 压缩后体积 < 原 JSON 的 30%
- [ ] subscriber 接收端不响应时重试 3 次后放弃，不阻塞其他 subscriber 的推送
- [ ] `.sync_state.json` 文件正确持久化每个 subscriber 的推送进度
- [ ] 进程运行时内存 < 10MB，CPU 空闲时 < 1%

## Further notes

- 编译为 Windows 应用程序（非控制台），无窗口运行，系统托盘放一个图标
- HttpListener 监听 `http://+:9000/`，需要 `netsh http add urlacl`（首次运行时自动注册或部署文档说明）
- 对 SQLite 只读——不写 events 表，避免与 SwitchMonitor 主程序锁冲突
