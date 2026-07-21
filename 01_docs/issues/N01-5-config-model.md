# N01-5: 配置模型升级 — role + vendorType + switchType + 订阅列表

> **状态**: 待实现
> **依赖**: 无（可与其他 N01 工单并行）
> **阻塞**: N01-2, N01-3, N01-4（均依赖新配置字段）

## Type

Feature

## What to build

扩展 `config.json` 和 C# 配置模型，支撑网络功能和厂商适配。

### 1. 新增配置字段

```json
{
  "role": "station",                      // "station" | "central"
  "vendorType": "huihuang",              // "huihuang" | "bangcheng" | "tonghao"
  "listenPort": 9000,

  "subscribers": [                        // 站机：推送给谁
    "192.168.1.11:9000",
    "192.168.1.100:9000"
  ],

  "teamStations": [                       // 班组终端：管辖哪些站
    {"id": "SSB", "name": "三水北站", "ip": "127.0.0.1", "port": 9000},
    {"id": "DHD", "name": "大湖东站", "ip": "192.168.1.11", "port": 9000}
  ],

  "stations": [                           // 总终端：管理哪些站
    {"id": "SSB", "name": "三水北站", "ip": "192.168.1.10", "port": 9000}
  ],

  "mergeWindowMs": 1000,
  "dataRetentionDays": 0,

  "switchGroups": [
    {"id": "1-J1", "dataFileIndex": 0, "switchType": "ZDJ9"},
    {"id": "5-J",  "dataFileIndex": 16, "switchType": "ZYJ7"}
  ]
}
```

### 2. C# POCO 变更

- `AppConfig` 新增：`Role`, `VendorType`, `ListenPort`, `Subscribers`, `MergeWindowMs`, `DataRetentionDays`
- `AppConfig.TeamStations` → 当 role=station 时生效
- `AppConfig.Stations` → 当 role=central 时替换现有 `Sites`
- `SiteConfig` 新增：`Ip`, `Port`
- `SwitchGroup` 新增：`SwitchType`（既有的 Data 层和 Station 层 POCO 都需加）

### 3. ConfigManager 兼容处理

- `role` 缺失 → 默认 `"station"`（向后兼容，现有单机部署不用改配置）
- `vendorType` 缺失 → 默认 `"huihuang"`（最常用的厂商格式）
- `teamStations` 为空且 role=station → 行为与旧版一致，只显示本站
- 旧 `Sites` 字段保留兼容，解析时优先用 `teamStations`/`stations`
- `SiteConfig.Ip` 缺失 → 不参与网络通信，仅本地查看

### 4. StationManager 变更

- `StationManifest` 新增 `VendorType`
- `SwitchGroupDef` 新增 `SwitchType`
- DC.ini 解析不自动推断 switchType（由导入人手动在配置中指定）

## Acceptance criteria

- [ ] 现有 `config.json`（无新字段）加载不报错，退化到旧版单机行为
- [ ] 新字段全部可正确反序列化
- [ ] `role=central` 时 `stations` 正确覆盖站点列表
- [ ] `role=station` + 非空 `teamStations` 时，站点列表 = teamStations
- [ ] `switchType` 字段正确传递到诊断引擎（不同型号用不同基线）
- [ ] 向后兼容：旧 switchGroups（无 switchType）编译不报错，运行时 switchType=null

## Further notes

- JSON 反序列化使用 JavaScriptSerializer，新字段缺失不会抛异常（属性保持默认值）
- `subscribers` 不含本站 IP——推送时自动跳过自己
- `teamStations` 第一项总是本站（ip=127.0.0.1），数据走本地 SQLite
