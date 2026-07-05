# Slice 3: 文件扫描 + 增量采集服务

## Type

AFK

## Blocked by

Slice 2（CSM2010 解析 → JSON 管道）

## What to build

实现后台文件扫描服务，定时检查数据源目录，发现新文件或文件更新后自动触发解析并更新 JSON 中间数据。维护已处理文件追踪，避免重复处理。

### FileWatcherService

- 使用 `FileSystemWatcher` 监控 `dataSourceDir` 下的 `.csv` / `.dat` 文件
- Created 和 Changed 事件 → 触发增量解析
- 预留 `System.Timers.Timer` 兜底扫描（默认 60 秒），因为 XP 上 `FileSystemWatcher` 偶尔丢事件

### 增量解析逻辑

1. 列出 `dataSourceDir` 下所有匹配文件
2. 对每个文件，查询 `parsed_data/_processed.json` 判断是否已处理
3. 未处理或文件修改时间变化 → 调用解析器 → 更新 JSON
4. 写入 `_processed.json`：记录 FilePath、LastProcessedTime、FileSize

### 事件通知

- 解析完成后触发 `event Action<List<string>> OnDataUpdated`，传递变更的 switchId 列表
- UI 层订阅此事件，在有新数据时刷新侧边栏时间列表

### 当前小时文件策略

- 如果文件修改时间距现在 < 扫描间隔 × 2，延迟处理（等完成写入）
- 当前小时文件每次扫描时重新解析（覆盖旧 JSON），保证数据最新

### 错误处理

- 解析失败的文件：记录错误日志，写入 `_processed.json` 标记 Error 状态
- 不阻塞其他文件处理
- 日志写入 `logs/` 目录

## Acceptance criteria

- [ ] 启动程序后，自动扫描并解析历史文件
- [ ] `_processed.json` 正确记录已处理文件，不重复解析
- [ ] 运行时新文件出现后，下一个扫描周期内自动处理
- [ ] 程序重启后不重复处理已有文件
- [ ] 损坏文件不导致崩溃，错误记录到日志
- [ ] 扫描运行在后台线程，不阻塞 UI
- [ ] UI 状态栏显示上次扫描时间和已处理文件数

## Further notes

- `FileWatcherService` 在 `SwitchMonitor.Data` 项目中实现
- 通过事件通知 UI — 使用 `SynchronizationContext` 或 `Control.Invoke` 切回 UI 线程
- 文件系统权限：以只读模式打开文件，不影响既有监测软件写入
