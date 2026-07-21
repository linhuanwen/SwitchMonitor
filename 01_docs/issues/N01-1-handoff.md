# N01-1 启动提示（复制到新窗口）

---

/tdd

SwitchMonitor 是铁路道岔监测系统（WinForms + .NET 4.0 + WinXP 工控机）。正在做多站组网功能。

## 本窗口只做一件事

**SQLite 存储层**，替代 JSON 文件存储。建 events 表、BLOB 读写、JSON 迁移脚本。

**前提**：N01-5（配置模型升级）已完成，`AppConfig` 已支持 role/vendorType/subscribers 等新字段。

## 先读

- 架构概览：[01_docs/design/多站组网架构设计.md](01_docs/design/多站组网架构设计.md)（重点第 4 节"数据存储"）
- 详细需求：[01_docs/issues/N01-1-sqlite-storage.md](01_docs/issues/N01-1-sqlite-storage.md)

## 测试接缝

SQLite 存储层接缝：公开的读写方法。测试打在：
- `InsertEvent` → `GetEvent` 往返，所有字段（含 BLOB）一致
- BLOB float32 精度不丢
- 按 switch_id + timestamp 查单条 < 15ms
- 迁移脚本：JSON → SQLite 后数据完全一致
- `DeleteEventsOlderThan` → 过期行不可查，VACUUM 后文件变小

## 要改的代码

**新增** `SwitchMonitor.Storage` 类库项目：
- `StorageManager.cs` — 建表、读写、查询、清理
- `DataMigrator.cs` — JSON → SQLite 一次性迁移

**修改** `SwitchMonitor.Data/IndexManager.cs` — 底层从 JSON 文件切换为 SQLite，保持上层接口不变

## 表结构

```sql
CREATE TABLE events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    switch_id TEXT NOT NULL, timestamp INTEGER NOT NULL,
    direction TEXT, duration_sec REAL,
    sample_interval REAL DEFAULT 0.04, sample_count INTEGER,
    current_a BLOB, current_b BLOB, current_c BLOB, power BLOB,
    diag_json TEXT, created_at TEXT DEFAULT (datetime('now'))
);
CREATE INDEX idx_events_switch_time ON events(switch_id, timestamp);
CREATE INDEX idx_events_timestamp ON events(timestamp);
```

## 约束

.NET 4.0, x86, WinXP。需要 System.Data.SQLite.dll（2MB，放 `06_deploy/release/`）。BLOB 用 BinaryWriter/BinaryReader + BitConverter。
编译：`dotnet build -c Release`
