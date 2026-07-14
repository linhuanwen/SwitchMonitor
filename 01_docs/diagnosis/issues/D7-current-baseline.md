# Slice D7: 电流曲线基线构建（CurrentFeatureExtractor + CurrentBaselineBuilder）

> **归属模块**: [三模块诊断架构设计](../三模块诊断架构设计.md) Phase 2 — 电流曲线基础设施
> **依赖链**: D1-D6 (功率诊断链路) → **D7** → 未来 M3 电流规则孵化
> **前置阅读**: [`CONTEXT.md §3`](../CONTEXT.md) — 五阶段分割算法；[`三模块诊断架构设计.md §5`](../三模块诊断架构设计.md) — M3 知识库规则晋升管道

## Type

AFK（Away From Keyboard — 独立实现，不阻塞功率诊断链路）

## Blocked by

无。电流基线完全独立于功率诊断链路，不需要改动任何现有代码。

## Design Decisions (Grill-with-Docs 确认)

| # | 决策点 | 结论 |
|---|---|---|
| 1 | 范围 | **仅基线构建**（特征提取 + 基线计算 + 存储），诊断规则全部走 M3 孵化→晋升管道 |
| 2 | 特征维度 | 分相段统计（6维×3相）+ 三相汇总（2维）= **20 个基线标量** |
| 3 | 代码集成 | **独立类簇**，不触碰功率侧任何代码，不修改 `CurveFeatures`/`FeatureExtractor`/`SwitchBaseline`/`BaselineBuilder` |
| 4 | 分段策略 | 电流独立五阶段分割（启动尖峰→解锁段→转换段→锁闭段→缓放段），**各相用自己的 spikeIndex 独立切分** |
| 5 | 存储 | 独立文件 **`Rules/current_baselines.json`**，由 `CurrentBaselineStore` 读写 |
| 6 | 基线算法 | 中位数聚合 + **迭代 MAD 过滤**（≥30 正常样本下限，90 天窗口暂不强制，沿功率侧逻辑） |
| 7 | 数据格式 | 已预处理（整流后），直接使用原值。不做包络提取/RMS 计算 |
| 8 | 正常曲线筛选 | **借功率诊断结果**——功率诊断为"正常"的曲线，其电流曲线视为正常 |
| 9 | 命名前缀 | `Current*`：`CurrentFeatures` / `CurrentFeatureExtractor` / `CurrentBaseline` / `CurrentBaselineBuilder` / `CurrentBaselineStore` |
| 10 | CLI | `DiagTool.exe baseline --current` / `--all`（`--all` = 功率基线 + 电流基线一并执行） |
| 11 | 测试 | `D7Tests.cs`（C# 回归）+ `current_baseline_ref_check.py`（Python 算法验证，仅开发机运行，不部署到终端） |
| 12 | 管道集成 | `DiagnosisRunner.Run()` 中同步提取电流特征 → `current_features.json`（列式存储）。基线构建本身为离线批处理 |
| 13 | 容错 | 电流数据缺失或不完整 → `CurrentFeatures.IsValid = false`，写入 `current_features.json` 保留审计痕迹，基线构建时跳过 |

---

## What to build

### 1. CurrentFeatures — 电流特征 POCO

```csharp
// 文件: SwitchMonitor.Diagnosis/CurrentFeatures.cs

public class CurrentFeatures
{
    // ── 三相独立特征（每相 6 维 × 3 = 18 维）──

    // A 相
    public double SpikePeakA;      // 启动尖峰电流峰值 (A)
    public int SpikeIndexA;        // 尖峰所在采样点下标
    public double UnlockMeanA;     // 解锁段均值 (A)
    public double ConvMeanA;       // 转换段均值 (A)
    public double LockMeanA;       // 锁闭段均值 (A)
    public double TailMeanA;       // 缓放段/尾部平台均值 (A)

    // B 相（同上）
    public double SpikePeakB; public int SpikeIndexB;
    public double UnlockMeanB; public double ConvMeanB;
    public double LockMeanB; public double TailMeanB;

    // C 相（同上）
    public double SpikePeakC; public int SpikeIndexC;
    public double UnlockMeanC; public double ConvMeanC;
    public double LockMeanC; public double TailMeanC;

    // ── 三相汇总（2 维）──
    public double DurationSec;              // 动作时长（秒，取三相 activeEnd 的最大值 ×0.04）
    public double MaxUnbalanceRatio;        // 三相间最大不平衡度

    // ── 元数据 ──
    public List<double> RawValuesA;         // A 相原始采样值（供未来 P1 逐点对比）
    public List<double> RawValuesB;
    public List<double> RawValuesC;
    public int SampleCount;                 // 原始采样点数
    public bool IsValid;                    // 是否有效（三相都非空且有数据）
    public bool IsFullWindow;               // 是否打满录制窗口（n ≥ 780）
    public int ActiveEnd;                   // 有效动作终点下标（取三相最大值）
}
```

### 2. CurrentFeatureExtractor — 三相电流特征提取器

```
算法概要（每相独立执行五阶段分割）：

输入: SwitchEvent (取 CurrentA / CurrentB / CurrentC)

对每相分别执行:
  1. 有效性检查: 该相列表非空 && 采样点数 > 0 && 最大值 > 0.01
  2. activeEnd: 从尾向前找到 > peakAll×0.05 的最后一点
  3. ①启动尖峰: 前 15 点内找最大值 → SpikePeak + SpikeIndex
  4. ②解锁段: [spikeIndex+2, spikeIndex+14) 均值 → UnlockMean
  5. ③转换段: 首选 [spikeIndex+20, activeEnd-40)，退化 [spikeIndex+2, activeEnd)
     均值 + 最大值 → ConvMean (当前只存均值，ConvMax 暂不存但保留计算)
  6. ④锁闭段: [activeEnd-40, activeEnd-22)，activeEnd≤50 → 0
  7. ⑤缓放段: [activeEnd-22, activeEnd-2)，activeEnd≤30 → 0

三相汇总:
  - DurationSec = max(activeEnd_A, activeEnd_B, activeEnd_C) × 0.04
  - MaxUnbalanceRatio = max over segments of |phaseMean - threePhaseMean| / threePhaseMean
    计算段取转换段（ConvMean），因为转换段样本最充足、最稳定

输出: CurrentFeatures POCO
```

**与功率 FeatureExtractor 的关键差异**：
- 三相独立分段——每相用自己的 spikeIndex，不共享时间边界
- 不做 StepRatio（台阶比）——三相电流的台阶比含义不如功率明确
- 新增 MaxUnbalanceRatio——三相间的比较是电流特有的诊断维度
- 不做 ConvMax（转换段最大值）存储——当前基线暂不需要

**公共 API**：

```csharp
public static class CurrentFeatureExtractor
{
    /// <summary>从 SwitchEvent 的三相电流提取特征。</summary>
    public static CurrentFeatures Extract(SwitchEvent evt);

    /// <summary>从单相采样值列表提取特征（供测试/验证使用）。</summary>
    public static CurrentFeatures ExtractPhase(IList<double> values);
}
```

### 3. CurrentBaseline — 电流基线 POCO 与存储

```csharp
// 文件: SwitchMonitor.Diagnosis/CurrentBaseline.cs

public class CurrentBaseline
{
    // ── A 相基线（6 项）──
    public double RefSpikePeakA;
    public int RefSpikeIndexA;          // 中位数取整
    public double RefUnlockMeanA;
    public double RefConvMeanA;
    public double RefLockMeanA;
    public double RefTailMeanA;

    // ── B 相基线 ──
    public double RefSpikePeakB; public int RefSpikeIndexB;
    public double RefUnlockMeanB; public double RefConvMeanB;
    public double RefLockMeanB; public double RefTailMeanB;

    // ── C 相基线 ──
    public double RefSpikePeakC; public int RefSpikeIndexC;
    public double RefUnlockMeanC; public double RefConvMeanC;
    public double RefLockMeanC; public double RefTailMeanC;

    // ── 三相汇总基线 ──
    public double RefDurationSec;
    public double RefMaxUnbalanceRatio;

    // ── 元数据 ──
    public int SampleCount;              // 参与统计的正常曲线数
    public string DateFrom;              // "yyyy-MM-dd"
    public string DateTo;
}

public class CurrentBaselineStore
{
    public string ComputedAt;                                         // "yyyy-MM-dd HH:mm:ss"
    public Dictionary<string, CurrentBaseline> Switches;              // key = switchId

    public static CurrentBaselineStore Load(string path);
    public void Save(string path);
}
```

**存储格式 `Rules/current_baselines.json`**：

```json
{
  "computedAt": "2026-07-14 15:00:00",
  "switches": {
    "1-1": {
      "refSpikePeakA": 5.500, "refSpikeIndexA": 6,
      "refUnlockMeanA": 3.200, "refConvMeanA": 2.800,
      "refLockMeanA": 1.500, "refTailMeanA": 1.700,
      "refSpikePeakB": 5.450, "refSpikeIndexB": 6,
      "refUnlockMeanB": 3.180, "refConvMeanB": 2.780,
      "refLockMeanB": 1.480, "refTailMeanB": 1.680,
      "refSpikePeakC": 5.520, "refSpikeIndexC": 7,
      "refUnlockMeanC": 3.220, "refConvMeanC": 2.820,
      "refLockMeanC": 1.520, "refTailMeanC": 1.720,
      "refDurationSec": 11.72,
      "refMaxUnbalanceRatio": 0.03,
      "sampleCount": 2840,
      "dateFrom": "2025-12-13",
      "dateTo": "2026-06-29"
    }
  }
}
```

### 4. CurrentBaselineBuilder — 迭代中位数基线构建器

```
算法（迭代中位数 + MAD 过滤）：

输入: List<CurrentFeatures> allFeatures, int minSamples = 30

Step 1 — 前置过滤:
  - 排除 IsValid == false
  - 排除 IsFullWindow == true
  - 排除 DurationSec < 2.4
  
Step 2 — 第一次中位数聚合:
  - 对剩余样本的每一维特征（20维）分别取中位数 → baseline_0 [20]
  - DurationSec 保留 2 位，SpikeIndex 取整，其余保留 3 位

Step 3 — MAD 过滤（迭代剔除）:
  - 对每条曲线，计算其 20 维特征向量到 baseline_0 的标准化欧氏距离:
    dist_i = sqrt( Σ((feature_j - baseline_0_j) / MAD_j)^2 )  对于 j=1..20
    其中 MAD_j = median(|feature_j - baseline_0_j|)，若 MAD_j == 0 则设为 1e-6
  - 计算所有距离的中位数 medDist
  - 计算 MAD of distances: madDist = median(|dist_i - medDist|)
  - 阈值: medDist + 3.0 × madDist
  - 剔除距离 > 阈值的曲线

Step 4 — 第二次中位数聚合:
  - 用保留的曲线重算每维中位数 → baseline_final [20]
  - 若剩余样本 < minSamples → 返回 null

输出: CurrentBaseline（含 20 项基线标量 + 元数据）
```

**公共 API**：

```csharp
public static class CurrentBaselineBuilder
{
    /// <summary>从电流特征列表计算基线。正常样本不足 minSamples 返回 null。</summary>
    public static CurrentBaseline Build(List<CurrentFeatures> allFeatures, int minSamples = 30);
}
```

### 5. CurrentFeaturesStore — 电流特征列式存储

```csharp
// 文件: SwitchMonitor.Diagnosis/CurrentFeaturesStore.cs
// 格式参照 FeaturesStore，列式 JSON 存储

public class CurrentFeaturesStore
{
    public List<string> Columns;       // 默认: ["timestamp","durationSec","spikePeakA","unlockMeanA",...]
    public List<List<double>> Rows;

    public static readonly List<string> DefaultColumns = new List<string>
    {
        "timestamp", "durationSec", "maxUnbalanceRatio",
        "spikePeakA", "spikeIndexA", "unlockMeanA", "convMeanA", "lockMeanA", "tailMeanA",
        "spikePeakB", "spikeIndexB", "unlockMeanB", "convMeanB", "lockMeanB", "tailMeanB",
        "spikePeakC", "spikeIndexC", "unlockMeanC", "convMeanC", "lockMeanC", "tailMeanC"
    };

    public static List<double> RowFromCurrentFeatures(long timestamp, CurrentFeatures f);
    public static CurrentFeaturesStore Load(string filePath);
    public static void Save(string parsedDir, string switchId, CurrentFeaturesStore store);
    public static void Append(string parsedDir, string switchId, long timestamp, CurrentFeatures f);
    public static int BackfillWithDir(IndexManager im, string switchId, string parsedDataDir);
    public int ColumnIndex(string name);
    public double Value(int row, int col);
}
```

存储路径：`parsed_data/{switchId}/current_features.json`

### 6. 管道集成：DiagnosisRunner 扩展

在 `DiagnosisRunner.Run()` 方法的步骤 6（写入 features.json 后）新增步骤 6b：

```csharp
// 6b. D7: 追加电流特征到 current_features.json
if (!string.IsNullOrEmpty(parsedDataDir))
{
    try
    {
        var currentFeatures = CurrentFeatureExtractor.Extract(evt);
        CurrentFeaturesStore.Append(parsedDataDir, switchId, evt.Timestamp, currentFeatures);
    }
    catch (Exception ex)
    {
        Logger.Warning("current_features.json 追加失败 switchId=" + switchId + ": " + ex.Message);
    }
}
```

**注意**：即使 `currentFeatures.IsValid == false` 也写入（保留审计痕迹）。基线构建时 `CurrentBaselineBuilder` 会跳过无效行。

### 7. CLI 集成：DiagTool 扩展

扩展现有 `baseline` 子命令，支持新标志：

```
用法: DiagTool.exe baseline <parsed_data目录> [输出路径] [--current|--all]

  baseline <dir>                   仅生成功率基线 baselines.json（现有行为，向后兼容）
  baseline <dir> [path] --current  仅生成电流基线 current_baselines.json
  baseline <dir> [path] --all      同时生成功率基线 + 电流基线
```

`--current` 模式实现：

```
RunCurrentBaseline(parsedDataDir, outputPath):
  1. 用 IndexManager 加载索引
  2. 遍历每个 switchId 的每个日期，LoadDayData → 过滤功率诊断为正常的 → CurrentFeatureExtractor.Extract
  3. 若已存在 current_features.json，优先从中读取（更快）；否则回退到从日JSON实时提取
  4. 每台道岔调用 CurrentBaselineBuilder.Build(allFeatures, 30)
  5. 写入 CurrentBaselineStore，附 DateFrom/DateTo
  6. 打印每台道岔的基线值表格（分相显示）+ 样本量
  7. 样本不足的道岔打印警告并跳过
```

`--all` 模式：先后调用 `RunBaseline` 和 `RunCurrentBaseline`。

### 8. DiagnosisRunner.RerunAll 扩展

`RerunAll` 方法中，在功率特征提取和诊断之后，同步追加电流特征提取和 `current_features.json` 写入。与 `Run()` 中 6b 逻辑一致。

---

## Acceptance Criteria

- [ ] **AC1**: `CurrentFeatureExtractor.Extract(evt)` 从含完整 A/B/C 三相电流的 SwitchEvent 正确提取 20 维特征
- [ ] **AC2**: `CurrentBaselineBuilder.Build()` 对 ≥30 条正常电流特征输出合法基线；<30 条返回 null
- [ ] **AC3**: `current_baselines.json` 格式与上述 sample 一致，`CurrentBaselineStore.Load/Save` 可逆
- [ ] **AC4**: `DiagTool.exe baseline --current` 从 parsed_data 目录生成 `current_baselines.json`
- [ ] **AC5**: `DiagTool.exe baseline --all` 同时生成 `baselines.json` + `current_baselines.json`
- [ ] **AC6**: `DiagnosisRunner.Run()` 在管道中自动追加电流特征到 `current_features.json`
- [ ] **AC7**: 电流数据缺失/不完整时 `IsValid = false`，`current_features.json` 保留记录不抛异常
- [ ] **AC8**: 交叉验证通过：`python current_baseline_ref_check.py` 输出与 C# 一致
- [ ] **AC9**: D7Tests.cs 覆盖：特征提取（正常+边界）、基线构建（正常+MAD过滤+样本不足）、存储读写（存在+损坏+缺失）

---

## File Manifest

| # | 文件 | 项目 | 说明 |
|---|---|---|---|
| 1 | `CurrentFeatures.cs` | `SwitchMonitor.Diagnosis` | 20维电流特征 POCO |
| 2 | `CurrentFeatureExtractor.cs` | `SwitchMonitor.Diagnosis` | 三相独立五阶段分割 |
| 3 | `CurrentBaseline.cs` | `SwitchMonitor.Diagnosis` | 电流基线 POCO + `CurrentBaselineStore` |
| 4 | `CurrentBaselineBuilder.cs` | `SwitchMonitor.Diagnosis` | 迭代中位数基线构建器 |
| 5 | `CurrentFeaturesStore.cs` | `SwitchMonitor.Diagnosis` | `current_features.json` 列式存储 |
| 6 | `DiagnosisRunner.cs` | `SwitchMonitor.Diagnosis` | **修改**：追加电流特征提取步骤（6b） |
| 7 | `Program.cs` | `SwitchMonitor.DiagTool` | **修改**：`--current` / `--all` 标志 |
| 8 | `D7Tests.cs` | `SwitchMonitor.Tests` | C# 测试套件 |
| 9 | `current_baseline_ref_check.py` | `02_source/tools/` | Python 参考验证（不部署） |

## Further Notes

- **中位数实现注意偶数样本取平均**——与 Python `statistics.median` 语义一致，直接复用 `BaselineBuilder` 中的 `Median()` 逻辑
- **MAD = 0 的保护**：若某维特征所有样本值完全相同，MAD=0 会导致距离计算公式除零。此时将该维 MAD 设为 1e-6
- **JavaScriptSerializer** 序列化 Dictionary 为 JSON 对象天然支持；`MaxJsonLength = int.MaxValue`
- **SpikeIndex 的中位数**：取整（偶数平均后四舍五入），作为分段边界的"典型位置"
- **MaxUnbalanceRatio 定义**：对每条曲线，计算 `max(|ConvMean_A − threePhaseMean|, |ConvMean_B − threePhaseMean|, |ConvMean_C − threePhaseMean|) / threePhaseMean`，其中 `threePhaseMean = (ConvMean_A + ConvMean_B + ConvMean_C) / 3`
- **基线重算策略**：与功率基线一致，季度手动重跑一次。一期不做自动过期提醒
- **不与功率分段共享时间边界**：各相独立找尖峰、独立切分段。三相不平衡在特征层（`MaxUnbalanceRatio`）体现
- **电流数据确认已预处理**（整流后），不需要在提取器做包络提取或 RMS 计算
- **`--current` 模式下正常曲线筛选**：读取该事件的 `.diag.json`，功率诊断结果为 `Normal`（即 `Results` 为空数组）的才纳入基线候选池。电流基线构建依赖功率诊断先行完成——若功率诊断未执行过，需先运行 `diagtool dryrun` 或手动导入管道
