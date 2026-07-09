# Slice D2: 基线构建工具（BaselineBuilder + baselines.json）

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖链**: D1 (特征提取) → **D2** → D3 (规则引擎) → D4 (管道) → D5 (UI)
> **前置阅读**: [`CONTEXT.md §2.2`/`§5`](../CONTEXT.md) — 基线定义与各台期望值；[PRD §4.2](../PRD.md) — 基线功能规格

## Type

AFK

## Blocked by

D1（特征提取器）

## What to build

在 `SwitchMonitor.Diagnosis` 中实现基线计算，在 `DiagTool` 中增加 `baseline` 子命令，
对历史数据一键生成 `Rules/baselines.json`。

### 基线用途

基线是 D3 规则引擎中 R1-R8（除 R0）的**参照基准**。每台道岔独立一套基线值，由该台
全部历史正常曲线统计得出。没有基线的道岔只能执行 R0/R1 硬规则（见 D3）。

### Baseline POCO 与存储格式

```csharp
public class SwitchBaseline
{
    public double RefDurationSec;   public double RefSpikePeak;
    public double RefUnlockMean;    public double RefConvMean;    public double RefTailMean;
    public int SampleCount;         // 参与统计的正常曲线数
    public string DateFrom;         // "2025-12-13"
    public string DateTo;
}

public class BaselineStore          // 负责 baselines.json 读写
{
    public string ComputedAt;                              // "yyyy-MM-dd HH:mm:ss"
    public Dictionary<string, SwitchBaseline> Switches;    // key = switchId
    public static BaselineStore Load(string path);         // 不存在/损坏 → 返回空 Store
    public void Save(string path);
}
```

`Rules/baselines.json` 样例：

```json
{
  "computedAt": "2026-07-08 15:00:00",
  "switches": {
    "1-1": { "refDurationSec": 11.72, "refSpikePeak": 3.235, "refUnlockMean": 0.307,
             "refConvMean": 0.301, "refTailMean": 0.214,
             "sampleCount": 2962, "dateFrom": "2025-12-13", "dateTo": "2026-06-29" }
  }
}
```

### BaselineBuilder

```csharp
public class BaselineBuilder
{
    // 输入某台道岔的全部特征列表，按 CONTEXT.md §5 计算基线；正常样本不足 minSamples 返回 null
    public static SwitchBaseline Build(List<CurveFeatures> allFeatures, int minSamples = 30);
}
```

算法（严格按 `CONTEXT.md §5`）：
1. 过滤：排除 `IsFullWindow`、`!IsValid`、`DurationSec < 2.4`
2. `med` = 剩余样本 DurationSec 的中位数
3. 正常样本 = `|DurationSec − med| < med × 0.15` 的样本
4. 各 `ref*` = 正常样本对应特征的**中位数**（偶数个取中间两数均值），round 到 3 位（时长 2 位）

### DiagTool baseline 子命令

```
DiagTool.exe baseline <parsed_data目录> [输出路径=Rules\baselines.json]
```

1. 用 `IndexManager` 加载索引，遍历每个 switchId 的每个日期，`LoadDayData` → `FeatureExtractor.Extract(evt)`
2. 每台道岔调用 `BaselineBuilder.Build`，写入 BaselineStore（附 DateFrom/DateTo = 该台数据的日期范围）
3. 打印每台道岔的基线值表格与样本量；样本不足的道岔打印警告并跳过

## Acceptance criteria

- [ ] 先跑 `ImportTool` 生成 parsed_data（数据源 `03_raw_data/sanshuibei_csv`），再跑
      `DiagTool.exe baseline`，生成合法的 `Rules/baselines.json` 含全部 8 台道岔
- [ ] 基线值与 `CONTEXT.md §2.2` 期望值表逐台逐项对比（时长/峰值/解锁/转换/缓放 5 项），
      容差 ±0.02；`sampleCount` 与期望值偏差 ≤ ±5
- [ ] 某台道岔正常样本 < 30 条时不写入该台基线，控制台给出明确警告
- [ ] baselines.json 损坏/缺失时 `BaselineStore.Load` 不抛异常，返回空 Store
- [ ] 交叉验证：`python 02_source/tools/diag_reference_check.py baseline` 输出一致

## Further notes

- 中位数实现注意偶数样本取平均——与 Python `statistics.median` 语义一致
- `JavaScriptSerializer` 序列化 Dictionary 为 JSON 对象是天然支持的；`MaxJsonLength = int.MaxValue`
- 运行顺序上 baseline 依赖 parsed_data 已导入——工具里检测 index.json 不存在时给出
  "请先运行 ImportTool" 的提示
- **基线重算策略**：季度手动重跑一次即可（漂移仅 ~3%/半年，见 CONTEXT §2.5）；
  一期不做自动过期提醒。D4 之后也可从 UI 菜单触发重算（`DiagTool.exe baseline` 或
  `DiagnosisRunner.RerunAll`，不在本 slice 范围）
- **不分方向建基线**：当前 Direction 为占位交替值，同一道岔时长无双峰分布，
  方向差异可忽略。如需分方向，BaselineStore 按 switchId+direction 扩展即可
