# N01-5 启动提示（复制到新窗口）

---

/tdd

SwitchMonitor 是铁路道岔监测系统（WinForms + .NET 4.0 + WinXP 工控机）。正在做多站组网功能。

## 本窗口只做一件事

**扩展 config.json 配置模型**，新增网络和厂商适配字段。不动存储、不动网络、不动 UI——只改配置层。

## 先读

- 架构概览：[01_docs/design/多站组网架构设计.md](01_docs/design/多站组网架构设计.md)（重点看第 5 节"配置文件"）
- 详细需求：[01_docs/issues/N01-5-config-model.md](01_docs/issues/N01-5-config-model.md)

## 测试接缝

配置层的接缝是 `ConfigManager.LoadConfig` + `StationManager.DiscoverStations` 的返回结果。测试打在：
- `AppConfig` 反序列化：给一份 `config.json`，验证所有新字段正确解析
- 向后兼容：旧版 config.json（无新字段）加载不报错，退化到默认值
- `StationManifest` / `SwitchGroupDef` 新字段正确传递

## 要改的代码

`SwitchMonitor.Data/CurveData.cs` — AppConfig、SiteConfig、SwitchGroup 加新字段
`SwitchMonitor.Station/StationConfig.cs` — StationManifest、SwitchGroupDef 加新字段
`SwitchMonitor.Data/ConfigManager.cs` — 解析新字段 + 向后兼容

## 新字段清单

- `AppConfig`: role, vendorType, listenPort, subscribers, mergeWindowMs, dataRetentionDays, teamStations
- `SiteConfig`: ip, port
- `SwitchGroup`: switchType
- `StationManifest`: vendorType
- `SwitchGroupDef`: switchType

## 约束

.NET 4.0, x86, WinXP 兼容。反序列化用 JavaScriptSerializer。缺字段时属性保持默认值，不抛异常。
编译：`dotnet build -c Release`，输出 `06_deploy/release/`
