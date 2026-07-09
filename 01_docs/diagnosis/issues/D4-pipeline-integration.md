# Slice D4: 数据管道集成 + 诊断结果存储 + 诊断日志

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖链**: D1 (特征) → D2 (基线) → D3 (规则) → **D4** → D5 (UI 展示)
> **前置阅读**: [PRD §4.4-§4.7](../PRD.md) — 管道集成、存储文件、诊断日志规格

## Type

AFK

## Blocked by

D3（规则引擎）

## What to build

三件事：(A) 把诊断引擎接入现有导入管道——每次导入/更新一天的数据后自动跑诊断；(B) 诊断结果落盘
供 UI 读取；(C) 诊断运行日志 `diag.log` 记录每次评估过程。

**不改变现有日数据 JSON 的格式**（向后兼容）。

### A. 存储格式

**`parsed_data/{switchId}/{date}.diag.json`**（与 `{date}.json` 同目录并列）：

```json
[
  { "timestamp": 1770922311, "level": "正常", "results": [] },
  { "timestamp": 1769618597, "level": "故障",
    "results": [ { "ruleId": "R1", "ruleName": "动作超时/未完成", "level": "故障",
                   "description": "动作时长31.36s，超过参考11.72s+3.0s，疑似卡阻/空转未完成",
                   "value": 31.36, "reference": 11.72 } ] }
]
```

**`parsed_data/alarms_index.json`**（UI 角标/汇总用）：

```json
{ "1-1": { "2026-02-13": { "预警": 1, "报警": 0, "故障": 0 } },
  "4-1": { "2026-01-29": { "预警": 0, "报警": 0, "故障": 2 } } }
```

计数只统计非正常事件；三个键恒输出（含 0），日期无异常则不写该日期条目。

### B. 集成点（SwitchMonitor.Data 侧）

1. `DataPipeline` 构造时接受可选的诊断回调/接口引用（**Data 项目不得反向依赖 Diagnosis**，
   用委托解耦）：

```csharp
// DataPipeline 新增
public Func<string, SwitchEvent, EventDiagnosis> DiagnoseHook;  // switchId, evt → 结果（可为 null）
```

   `EventDiagnosis`（timestamp/level/results）POCO 放在 Data 项目（或用 object 传递后在
   调用方序列化——实现者二选一，倾向前者：Data 定义存储 POCO，Diagnosis 只产生它）。

2. `ImportSwitchGroup` 里 `SaveDayData` 之后：若 `DiagnoseHook != null`，对该日每个事件调用，
   汇集为列表交给 `IndexManager.SaveDayDiagnosis(switchId, date, list)`（新方法，写 .diag.json
   并更新 alarms_index.json）。

3. 装配点在 UI/工具层：`MainForm` 与 `ImportTool`/`DiagTool` 创建
   `DiagnosisEngine` → `Initialize("Rules")` → 挂到 `pipeline.DiagnoseHook`
   （engine.Diagnose + FeatureExtractor.Extract + DiagnosisAggregator 的组合封装成一个静态方法，
   放 Diagnosis 项目：`DiagnosisRunner.Run(engine, switchId, evt)`）。

4. `IndexManager` 新增读取接口，UI（D5）用：

```csharp
public List<EventDiagnosis> LoadDayDiagnosis(string switchId, string date);  // 文件缺失 → 空列表
public Dictionary<string, Dictionary<string, Dictionary<string, int>>> LoadAlarmsIndex();
```

### C. 诊断运行日志（diag.log）

每条诊断记录以紧凑格式写入 `diag.log`，便于事后审计诊断逻辑、评估规则质量、为阈值调优提供依据：

```
[2026-01-29 00:43:17] switchId=4-1 eventTs=1769618597
  Features: dur=31.36s spikePeak=4.353 convMean=0.545 tailMean=0.706 stepRatio=1.108
             unlockMean=0.302 activeEnd=783 isFullWindow=True isValid=True
  Baseline: refDur=11.72 refSpikePeak=3.294 refConvMean=0.267 refTailMean=0.215 refUnlockMean=0.298
  R1: isFullWindow=True && dur(31.36) > refDur(11.72)+3.0 → HIT (故障) — 终止
  Overall: 故障 (1条)
```

正常事件仅记录一行概要：

```
[2026-02-13 02:51:51] switchId=1-1 eventTs=1770922311 → 正常 (R0-R8 无命中)
```

**实现要点**：
- `Logger` 类新增 `LogDiagnosis(string text)` 方法，写入 `diag.log`（每日轮转或单文件追加，单文件即可——每天约 120 条，半年 ≈ 20k 条 ≈ 5MB）
- 日志在 `DiagnosisRunner.Run()` 内部调用，与诊断计算同步完成，不产生额外 IO 开销
- R1/R2 命中时注明"终止"及被跳过的规则
- 多规则命中时（R3-R8）逐条列出

### D. 行为要求

- 基线/阈值文件缺失时：导入照常完成，诊断按 D3 的降级逻辑执行（仅硬规则），
  不得因诊断失败中断导入——诊断环节整体 try/catch，异常记 `Logger.Error` 后继续
- 重复导入同一天（覆盖场景）：.diag.json 与 alarms_index 一并覆盖更新
- `ImportTool` 导入完成后打印报警汇总（每台道岔 故障/报警/预警 计数）
- config.json 新增节（`AppConfig` 补字段，缺省启用）：
  `"diagnosis": { "enabled": true, "rulesDir": "Rules" }`

## Acceptance criteria

- [ ] 全量导入 sanshuibei_csv 后，每个 `{date}.json` 均有对应 `{date}.diag.json`，
      事件条数一一对应、按 timestamp 对齐
- [ ] `alarms_index.json` 汇总数与 D3 dryrun 结果一致（总非正常事件 ≈375）
- [ ] 4-1 的 2026-01-29 出现 ≥2 条"故障"（夹具 C 所在日，实际该日有多条超时）
- [ ] 删除 Rules/ 目录后重新导入：导入成功完成，diag.json 仍生成（仅含 R0/R1-窗口 判定），
      日志有降级警告
- [ ] `diagnosis.enabled=false` 时不产生任何 .diag.json，导入行为与改造前完全一致
- [ ] 导入耗时相比改造前增幅 < 30%（诊断是纯内存计算，理论开销极小）
- [ ] 现有 `{date}.json` 格式与内容零变化（对比改造前后文件）
- [ ] **diag.log 验收**：全量导入后 diag.log 包含每条异常事件的详细评估记录；
      夹具 C（1769618597）的日志含完整 R1 命中详情和终止标记；
      正常事件仅一行概要，无冗余信息

## Further notes

- 依赖方向复核：`Data` 不引用 `Diagnosis`（委托解耦）；`UI → Diagnosis`、`DiagTool → Diagnosis`
  负责装配。这保证 Diagnosis.dll 可整体替换而 Data 不需重编译（PRD 的可替换性要求）
- alarms_index.json 写入频率 = 每天数据保存一次，量小（8 台 × ~200 天），无性能顾虑；
  但要在 IndexManager 的 `_lock` 内更新，与 index.json 同步一致
- `.diag.json` 不进 index.json 索引——它是 `{date}.json` 的影子文件，存在性由日数据决定
- 后续实时扫描服务（主项目 Slice 3）接入时自动获得诊断能力，无需再改
- diag.log 不做结构化（JSON 每行）——运维人员更习惯 grep 纯文本日志；
  如需结构化分析，.diag.json 本身已是完整的结构化诊断结果
