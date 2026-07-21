# N01-1: SQLite 存储层 — events 表 + BLOB 读写 + JSON 迁移

> **状态**: 待实现
> **依赖**: 无
> **阻塞**: N01-2, N01-3, N01-4

## Type

Feature

## What to build

将 SwitchMonitor.Data 的底层存储从 JSON 文件切换为 SQLite。每站一个独立 `.db` 文件，events 单表存储所有道岔动作事件和诊断结果。

### SQLite 表结构

```sql
CREATE TABLE events (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    switch_id     TEXT NOT NULL,
    timestamp     INTEGER NOT NULL,
    direction     TEXT,
    duration_sec  REAL,
    sample_interval REAL DEFAULT 0.04,
    sample_count  INTEGER,
    current_a     BLOB,   -- float32 LE
    current_b     BLOB,
    current_c     BLOB,
    power         BLOB,   -- float32 LE
    diag_json     TEXT,
    created_at    TEXT DEFAULT (datetime('now'))
);
CREATE INDEX idx_events_switch_time ON events(switch_id, timestamp);
CREATE INDEX idx_events_timestamp ON events(timestamp);
```

### 核心功能

1. **写入**：`InsertEvent(stationId, SwitchEvent, diagnosis)` → BLOB 存采样数据，TEXT 存诊断 JSON
2. **读取**：`GetEvent(stationId, switchId, timestamp)` → 还原为 SwitchEvent
3. **查询**：`GetEventsByDate(stationId, switchId, date)` → 按日期范围查
4. **最新时间戳**：`GetLatestTimestamp(stationId)` → 用于 DataForwarder 补拉
5. **BLOB 格式**：4 字节 float32 小端序数组，无 header
6. **JSON 迁移脚本**：一次性脚本，遍历 `parsed_data/` 下所有 JSON 文件 → 逐条 INSERT → 迁移完成后标记（.migrated 文件）
7. **数据清理**：`DeleteEventsOlderThan(stationId, days)` → DELETE + VACUUM

### 依赖

- System.Data.SQLite.dll（2MB），放入 `06_deploy/release/`
- .NET 4.0 兼容的 SQLite 版本

## Acceptance criteria

- [ ] InsertEvent → GetEvent 往返数据完全一致（含 BLOB byte-for-byte）
- [ ] 4 通道 BLOB 各存各的，读取任一通道不需解压其他
- [ ] 按 switch_id + timestamp 查单条 < 15ms
- [ ] 迁移脚本处理三水北站 23,999 条事件，耗时 < 5 分钟，无数据丢失
- [ ] 迁移后 SwitchEvent 所有字段与 JSON 原值完全一致
- [ ] 数据清理后过期行不可查，VACUUM 后文件变小

## Further notes

- 与现有 IndexManager 接口兼容——上层调用方无感知存储引擎变化
- BLOB 写入使用 `byte[]` + `BinaryWriter`，读取使用 `BinaryReader` + `BitConverter.ToSingle`
- 迁移脚本用 Python 或 C# 控制台程序，一次性运行
