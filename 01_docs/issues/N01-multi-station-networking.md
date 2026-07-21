# N01: 多站组网互通 — PRD

> **模块代号**: Networking（多站组网）
> **版本**: V1.0
> **日期**: 2026-07-19
> **目标平台**: 研华 610H 工控机, Windows XP SP3, .NET Framework 4.0, x86
> **依赖**: SwitchMonitor 现有全部模块（UI / Data / Diagnosis / Station）
> **设计文档**: [多站组网架构设计.md](../design/多站组网架构设计.md)

---

## 1. Problem Statement

SwitchMonitor 目前是纯单机程序，所有数据通过本地文件读写。现场实际部署场景是：

1. **多个机房各有站机**（辉煌/通号/邦诚三家厂商），每个站机产生本站的道岔动作数据
2. **机房之间已通过局域网组网**，站机与监测总终端互通
3. **每个班组管辖 1-3 个有岔站点**，班组人员需要在本地工控机上查看管辖范围内所有站的数据
4. **总终端需要查看全部站点数据**，统一监控

当前痛点：
- 查看其他站数据只能远程桌面登录到那台机器，无法在一个终端上切换查看
- 每个站的数据孤立，无法跨站对比或统一告警
- 缺乏网络通断监控，站机离线无人知晓
- 已有数据接收目录（`03_raw_data/本地接收目录/`）但无自动化传输机制

**核心诉求**：在局域网内实现站间数据自动互通，使班组终端和总终端能通过配置驱动的方式查看管辖站点的实时数据，网络故障时自动感知和恢复。

---

## 2. Solution Overview

### 2.1 一句话描述

> 基于 HTTP + JSON + gzip 的轻量站间数据推送协议，每台站机配备独立转发进程（DataForwarder.exe），通过 SQLite 存储替代 JSON 文件实现高效读写，配置驱动角色（站机/总终端）和站点订阅关系。

### 2.2 核心架构

```
┌──────────────┐     HTTP push     ┌──────────────┐
│ 站机 A (SSB) │◄──────────────────►│ 站机 B (DHD) │  同班组全互联
└──────┬───────┘                    └──────┬───────┘
       │ HTTP push                        │ HTTP push
       ▼                                  ▼
┌─────────────────────────────────────────────────┐
│              总监测终端（可看全部站点）              │
└─────────────────────────────────────────────────┘
```

- 站机产生数据后 10 秒内推送到同班组所有站机 + 总终端
- 总终端每 2 分钟主动探测各站机在线状态
- 网络中断恢复后自动补拉历史数据
- 所有站点数据以 SQLite 存储（每站一个 .db），年增量 300MB~5GB

### 2.3 为什么是 HTTP + SQLite 而非其他方案

| 候选 | 放弃原因 |
|------|---------|
| SMB 文件共享 | WinXP SMB 协议栈不稳定，目录轮询开销大，无法事件驱动 |
| WCF | XP 上配置繁琐，依赖多 |
| TCP 自定义协议 | 省 200 字节 HTTP 头（占一次推送 <0.4%），不值得用额外代码复杂度换 |
| 维持 JSON 文件 | 年增量 3-5GB/站，读取需全量反序列化 |
| **HTTP + SQLite（选中）** | .NET 4.0 全内置，零第三方依赖，200 行代码实现，SQLite BLOB 年增量 ~180MB |

### 2.4 三级角色定义

| 角色 | 配置值 | 说明 |
|------|--------|------|
| 站机 | `"station"` | 产生数据 + 查看班组站点，同班组全互联 |
| 总终端 | `"central"` | 查看全部站点，主动探测 + 数据补拉 |

班组终端与站机共用同一台物理工控机，通过 `teamStations` 限定可查看站点。

---

## 3. User Stories

### 数据互通

> **US-N01**: As a 班组运维人员, I want 在本站工控机上切换到同班组其他站点查看曲线数据, so that 不需要远程桌面登录到其他机器就能了解管辖范围内所有道岔状态。

> **US-N02**: As a 总终端监测人员, I want 在总终端上能查看任意站点的实时曲线和诊断结果, so that 统一监控全部站点异常情况。

> **US-N03**: As a 运维人员, I want 新产生的道岔动作数据在 10 秒内到达班组终端和总终端, so that 异常发现不滞后。

### 网络管理

> **US-N04**: As a 总终端监测人员, I want 总终端自动探测各站机在线状态并在离线时告警提示, so that 通信中断不会被忽略。

> **US-N05**: As a 总终端监测人员, I want 站机恢复在线后自动补充中断期间缺失的数据, so that 历史数据不出现空洞。

> **US-N06**: As a 总终端监测人员, I want 提供手动补拉按钮作为兜底手段, so that 自动补拉失败时可以人工重试。

### 配置管理

> **US-N07**: As a 系统管理员, I want 通过编辑 JSON 配置文件来设定站机角色、订阅关系、站点 IP 和端口, so that 部署到不同站点时只需改配置无需改代码。

> **US-N08**: As a 系统管理员, I want 每台转辙机可手动指定 switchType（ZYJ7/ZDJ9）, so that 混合站点的诊断规则不会串用。

> **US-N09**: As a 系统管理员, I want 通过 vendorType 字段切换厂商数据解析器（辉煌/邦诚/通号）, so that 同一套程序适配不同厂家设备。

### 数据存储

> **US-N10**: As a 运维人员, I want 数据以 SQLite 存储替代原有 JSON 文件, so that 磁盘占用更小、读取速度更快。

> **US-N11**: As a 总终端管理员, I want 能手动按站点清理超过指定天数的历史数据, so that 硬盘空间可控。

> **US-N12**: As a 系统管理员, I want 现有 JSON 历史数据能一次性导入 SQLite, so that 历史数据不丢失。

### 数据可靠性

> **US-N13**: As a 运维人员, I want 网络短暂中断恢复后数据自动补传不丢失, so that 不因网络抖动产生数据空洞。

> **US-N14**: As a 系统管理员, I want 推送失败后自动重试 3 次再放弃, so that 瞬时网络抖动不会导致数据丢失。

---

## 4. Implementation Decisions

### 4.1 新增模块

| 模块 | 类型 | 说明 |
|------|------|------|
| DataForwarder.exe | 独立控制台程序 | 站机侧后台转发进程，轮询 SQLite、打包推送、响应 status/events 请求 |
| SwitchMonitor.Network | 类库 (.dll) | 网络层公共代码：HTTP 服务端/客户端、数据打包/解包、gzip 压缩 |
| SwitchMonitor.Storage | 类库 (.dll) | SQLite 存储层：建表、读写、迁移、清理 |

### 4.2 修改模块

| 模块 | 改动 |
|------|------|
| SwitchMonitor.Data | IndexManager 底层从 JSON 文件 → SQLite；ConfigManager 新增网络字段解析 |
| SwitchMonitor.Station | StationManifest 新增 vendorType、switchGroups 新增 switchType |
| SwitchMonitor.UI | MainForm 新增站点在线状态指示灯、补拉按钮、数据清理对话框 |

### 4.3 HTTP API 契约

**站机端（DataForwarder 提供）：**

```
GET /api/status
  Response 200: {
    "stationId": "SSB",
    "stationName": "三水北站",
    "status": "ok",
    "lastTimestamp": 1712345678,
    "dbSizeMB": 320
  }

GET /api/events?since={timestamp}
  Response 200 (gzip): {
    "stationId": "SSB",
    "since": 1712340000,
    "count": 5,
    "events": [
      {
        "switchId": "1-J",
        "timestamp": 1712340005,
        "direction": "定位到反位",
        "durationSec": 11.76,
        "sampleInterval": 0.04,
        "currentA": [[0.0, 0.5], ...],
        "currentB": [[0.0, 0.3], ...],
        "currentC": [[0.0, 0.4], ...],
        "power": [[0.0, 0.1], ...],
        "diagnosis": { "level": "正常", "results": [] }
      }
    ]
  }
```

**接收端（总终端/班组终端提供）：**

```
POST /api/receive
  Request (gzip): {
    "stationId": "SSB",
    "batchTimestamp": 1712345678,
    "events": [ ... ]
  }
  Response 200: { "received": 5 }
```

### 4.4 SQLite 表结构

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

每站一个独立 `.db` 文件，放在 `parsed_data/{stationId}.db`。

### 4.5 配置结构变更

`config.json` 新增字段：

- `role`: `"station"` | `"central"`
- `vendorType`: `"huihuang"` | `"bangcheng"` | `"tonghao"`
- `listenPort`: 本地 HTTP 监听端口
- `subscribers`: 推送目标 IP:Port 列表
- `teamStations` / `stations`: 可查看的站点列表
- `switchGroups[].switchType`: `"ZYJ7"` | `"ZDJ9"`
- `mergeWindowMs`: 打包合并窗口
- `dataRetentionDays`: 数据保留天数（0=不限）

### 4.6 推送与重试

- DataForwarder 每 1 秒查询 SQLite `WHERE timestamp > lastPushedTimestamp`
- 1 秒合并窗口内所有新行打包为一个 POST
- gzip 压缩后发送给每个 subscriber
- 推送失败：重试 3 次（间隔 2s / 4s / 8s），仍失败则放弃，等待补拉兜底
- 同步状态持久化在 `.sync_state.json`（独立文件，避免与主程序 SQLite 锁冲突）

### 4.7 活性检测与补拉

- 总终端每 2 分钟 `GET /api/status`，超时 10 秒
- 连续 2 次失败 → 标记离线（UI 标红弹提示）
- 恢复在线 → 自动触发补拉 `GET /api/events?since={lastTimestamp}`
- 补拉按时间升序返回，全部完成后才持久化 lastTimestamp（中途失败不更新，下次重拉不丢）
- UI 提供手动补拉按钮

### 4.8 数据清理

- 总终端 UI 菜单 → "清理历史数据" → 对话框
- 可选择站点（多选）、设置保留天数（默认 365）
- 展示当前数据量、最早记录日期、预计释放空间
- 确认后执行 `DELETE + VACUUM`
- 站机/班组终端默认不清理（`dataRetentionDays: 0`）

---

## 5. Testing Decisions

### 5.1 测试接缝

**接缝 1：HTTP API（进程间，主测试面）**

所有 DataForwarder 功能通过 HTTP 端点测试，不涉及 UI：

- 启动 DataForwarder，往其 SQLite 插入测试数据，验证 `/api/status` 和 `/api/events` 返回正确
- 启动接收端点，往 SQLite 插入数据，验证 DataForwarder 自动 POST 到接收端
- 模拟网络中断/恢复，验证离线检测和自动补拉

**接缝 2：SQLite 存储层（进程内）**

不依赖网络，纯本地读写验证：

- 写入包含所有字段的 SwitchEvent，读取对比
- BLOB float32 读写精度验证
- 旧 JSON → SQLite 迁移的数据一致性验证
- 按天数的数据清理验证

### 5.2 测试原则

- 只测试外部行为（HTTP 响应、数据库读写结果），不测试内部实现
- 不在 XP 工控机上进行自动化测试（开发机上用 .NET 4.0 运行环境即可）
- 网络测试使用 localhost 模拟

### 5.3 现有测试先例

- `SwitchMonitor.Tests` 已有 NUnit 测试套件（D4-D7），测试模式可复用
- `DiagTool.exe selftest` 的金标准夹具模式可参考——用已知输入验证已知输出

---

## 6. Out of Scope

| 内容 | 原因 | 计划 |
|------|------|------|
| Web 版总终端（浏览器访问） | 用户选择 WinForms 方案 | 不做 |
| 端到端加密/认证 | 局域网内使用，物理安全已保证 | V2 评估 |
| UDP 广播自动发现站机 | 用户选择手动配置 IP | V2 评估 |
| 自动数据清理（定时任务） | 用户选择手动触发 | 暂不做 |
| 厂商特定数据解析器实现 | 属于数据导入层，不是网络层 | 独立工单 |
| 趋势分析/逐点对比（D6） | 属于诊断模块，不是网络层 | 独立工单 |
| 总终端聚合大盘（多站总览） | 当前先做站点切换查看，大盘后续 | V2 |
| 跨班组数据共享 | 用户场景不需要，各班独立 | 不做 |
| 断点续传 | 补拉从 since 重新开始，宁可重复不遗漏 | 不做 |

---

## 7. Further Notes

- System.Data.SQLite.dll（约 2MB）需纳入部署包，放在 `06_deploy/release/` 同目录
- DataForwarder.exe 编译为 Windows 应用程序（非控制台），隐藏窗口后台运行
- 所有 HTTP 通信使用 `HttpListener`（.NET 4.0 内置），无需管理员权限
- ZYJ7 大站（40 组道岔）单站年数据量约 4.8GB，总终端需关注硬盘容量
- 厂商适配（vendorType）在本 spec 中只做配置字段和切换框架，具体解析器实现在独立工单中完成
- 详细架构决策参见 [多站组网架构设计.md](../design/多站组网架构设计.md)
