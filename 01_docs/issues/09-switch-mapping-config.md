# Slice 9: 道岔映射配置

> **状态（2026-07-08 代码审核）**: ❌ 未实现。主线无 `switch_mapping` 加载代码，名称直接取 `config.json` 的 switchGroups Label（相当于降级策略常态化）。`05_production_data/Config/switch_mapping.json` 为旧 GDI 版遗留，顶层键 `fileMapping` 与本 issue 规格（`switchMapping`/`file_N`）不一致。

## Type

AFK

## Blocked by

Slice 6（全链路交互联动）

## What to build

用 JSON 配置文件管理数据文件与实际道岔的映射关系。程序启动时加载映射，UI 全链路使用可读名称（如"1#道岔"）替代文件编号（如"SwitchCurve(0)"）。支持热加载。

### 映射配置文件

`switch_mapping.json`：

```json
{
  "version": "1.0",
  "stationId": "SSB",
  "stationName": "三水北",
  "switchMapping": {
    "file_0": {
      "switchId": "1-1",
      "switchName": "1#道岔",
      "directionHint": "定位↔反位",
      "description": "待现场确认"
    },
    "file_4": {
      "switchId": "1-X",
      "switchName": "1#道岔(心轨)",
      "directionHint": "定位↔反位",
      "description": "待现场确认"
    }
  }
}
```

### 映射加载器

- `MappingConfig LoadMapping(string path)` → 反序列化
- 程序启动时加载，存入 `ConfigManager` 单例
- 加载失败 → 使用降级名称（"SwitchCurve(0)" 等），程序不崩溃

### UI 全链路替换

影响位置：
- 侧边栏转辙机列表：显示 `switchName`（如"1#道岔"）
- 图表标题：显示 `switchName`
- 状态栏：显示 `switchName`
- 导出文件名：使用 `switchName`
- 所有日志和错误提示：使用 `switchName`

### 热加载

- 菜单栏 → "重新加载映射配置" → 重新读取 JSON
- 更新侧边栏列表、当前图表标题、状态栏
- 不要求重启

### 降级策略

| 情况 | 行为 |
|------|------|
| 配置文件不存在 | 使用 `config.json` 中的 switchGroups label 作为显示名 |
| 某文件未在映射中 | 降级显示 `config.json` 中的 label |
| JSON 解析失败 | 所有项降级，记录错误日志 |
| 缺少字段 | 使用默认值，不报错 |

## Acceptance criteria

- [ ] `switch_mapping.json` 正确加载，UI 各位置显示映射后的可读名称
- [ ] 未映射项降级显示 config.json 中的 label
- [ ] 热加载：修改配置 → 菜单点击重载 → UI 更新
- [ ] 配置文件损坏或缺失时程序不崩溃
- [ ] 与 `config.json` 的 switchGroups 配置不冲突（mapping 优先）

## Further notes

- 映射配置独立于主配置，互不干扰
- 编码：UTF-8 without BOM
- 后续现场核对清楚后，只需修改此 JSON，无需改代码
- 本 Slice 排最后，因为之前 Slices 用 config.json 中的 label 占位即可工作
