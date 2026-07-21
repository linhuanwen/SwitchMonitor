# Slice D8: 标准曲线融合模块（StandardCurveBuilder）

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖链**: D1 (特征) → D2 (基线) → D3 (规则引擎) → **D8** → P1 集成
> **前置阅读**: [`标准曲线融合模块设计.md`](../标准曲线融合模块设计.md) — 完整设计规格

## Type

AFK

## Blocked by

D2（基线 baselines.json）、参考曲线管理（ReferenceCurveStore）

## What to build

在 `SwitchMonitor.Diagnosis` 中新增标准曲线融合模块，将人工设定的参考曲线（形态模板）
与统计基线（水平锚定）融合为"标准曲线"。

### 设计背景

- **基线** (baselines.json)：6 个标量中位数值，来自 ~3000 条正常曲线统计，稳健但无形态信息
- **参考曲线** (reference_curves/)：电务人员手动指定的单条标杆曲线，有完整波形但无统计校准
- **标准曲线** (standard_curves/)：融合两者——形态来自参考曲线，水平对齐到基线统计中心

详见 [`标准曲线融合模块设计.md`](../标准曲线融合模块设计.md) V2.0（已与林焕文讨论确认双层架构、基线保留决策等全部设计定论）。

### 新增文件

#### 1. `StandardCurve.cs` — POCO + 存储

路径: `SwitchMonitor.Diagnosis/StandardCurve.cs`

```csharp
public class StandardCurve
{
    public string SwitchId;
    public string Direction;
    public double SampleInterval;     // 0.04
    public int AlignIndex;            // spikeIndex，用于 P1 对齐
    public List<double> Values;       // 逐点功率值（kW），保留 3 位小数

    // 融合溯源
    public double FusionWeight;       // 0~1
    public string ReferenceSource;
    public string BaselineComputedAt;

    // 审计追踪：各段实际应用的缩放因子
    public double AlphaTime, AlphaSpike, AlphaUnlock, AlphaConv, AlphaLock, AlphaTail;
    public string ComputedAt;
}

public static class StandardCurveStore
{
    public static void Save(string directory, StandardCurve curve);
    public static StandardCurve Load(string filePath);
    public static Dictionary<string, StandardCurve> LoadAll(string directory);
}
```

存储路径: `Rules/standard_curves/{switchId}.json`。读写模式与 `ReferenceCurveStore` 一致
（JavaScriptSerializer + Encoding.UTF8 + 目录按需创建 + 损坏文件返回 null）。

#### 2. `StandardCurveBuilder.cs` — 融合算法

路径: `SwitchMonitor.Diagnosis/StandardCurveBuilder.cs`

```csharp
public static class StandardCurveBuilder
{
    public static StandardCurve Build(
        ReferenceCurve referenceCurve,
        SwitchBaseline baseline,
        double fusionWeight = 1.0,    // 0=保持原参考曲线, 1=完全对齐基线
        double clampMin = 0.7,
        double clampMax = 1.3,
        int blendHalfWidth = 3
    );
}
```

算法六步（详见设计规格 §5）：

1. `FeatureExtractor.Extract(referenceCurve.Values)` → 参考曲线五阶段特征
2. 计算 α = baseline.Ref* / ref.*，clamp [0.7, 1.3]，按 fusionWeight 混合：
   `α = 1.0 + (α_raw - 1.0) × fusionWeight`
3. `ResampleLinear(Values, targetLen)` → 时间轴对齐到 baseline.RefDurationSec
4. `FeatureExtractor.Extract(resampled)` → 重采样后的新段边界
5. 逐点 α(i) 分配 + 段边界 ±3 点线性过渡 → `Values[i] = resampled[i] × α(i)`
6. 输出 StandardCurve

关键内部方法：
- `ResampleLinear(List<double> src, int targetCount)` — 线性插值重采样
- `GetPointAlpha(i, si, ae, n, αs, blendHalfWidth)` — 逐点 α 查表，含五段边界识别和过渡区线性混合
- `MixAlpha(raw, w, min, max)` — clamp + fusionWeight 混合

### 修改现有文件

#### 3. `DiagnosisEngine.cs` — P1 优先使用标准曲线

- 新增字段 `_standardCurves: Dictionary<string, StandardCurve>`
- `Initialize()` 中加载 `Rules/standard_curves/` 目录
- `EvaluateP1()` 修改：优先从 `_standardCurves` 查标准曲线；未找到时回退到 `reference_curves/`
- 日志中标注 P1 使用的是 standard 还是 reference 模板
- 初始化日志增加标准曲线数量

### 不变的文件

- `FeatureExtractor.cs` — 复用其 Extract() 方法
- `BaselineBuilder.cs` — 复用基线计算
- `ReferenceCurveBuilder.cs` — 复用 ReferenceCurve POCO + ReferenceCurveStore
- `SwitchBaseline.cs` — 复用 BaselineStore.Load()
- `ProfileComparer.cs` — API 兼容（StandardCurve.Values + AlignIndex 等同于 ReferenceCurve 的对应字段）

### 数据流

```
电务人员 UI "设为参考曲线"
  → 保存 reference_curves/{switchId}.json
  → 触发 StandardCurveBuilder.Build(referenceCurve, baseline)
  → 保存 standard_curves/{switchId}.json
  → P1 自动切换为使用标准曲线（下次 Diagnose 调用时生效）
```

## Acceptance criteria

### 代码编译

- [ ] `dotnet build` 全项目 0 错误（Diagnosis 项目为 Library，无执行入口）
- [ ] 新增三个文件 `StandardCurve.cs`、`StandardCurveBuilder.cs` 经诊断引擎项目成功编译

### 算法正确性 — Python dryrun 验证

取 1-J 的一条已知正常曲线作为"参考曲线"，搭配 `baselines.json` 中 1-J 的基线值，
用 Python 复刻的算法运行 Build()：

- [ ] `fusionWeight=1.0` 时，输出 Values 长度 ≈ `baseline.RefDurationSec / 0.04`
- [ ] `fusionWeight=1.0` 时，各段均值接近基线对应 Ref 值（误差 < 5%，允许 clamp 和重采样引入的偏差）
- [ ] `fusionWeight=0` 时，输出各段均值 ≈ 参考曲线原值（误差 < 2%）
- [ ] 逐点值无 NaN / ±Infinity
- [ ] 所有 α 在 [0.7, 1.3] 范围内
- [ ] 因除零保护的 α（LockMean/TailMean 为 0 时）恒为 1.0

### 模板代表性 — P1 验证

- [ ] 对 1-J 用标准曲线做 P1 模板，30 条正常曲线中位 areaDiffRatio < 0.15
- [ ] 与用原参考曲线做模板相比，误报率不升高（中位 areaDiffRatio ≤ 原参考曲线）

### 集成检查

- [ ] `DiagnosisEngine.Initialize()` 加载 standard_curves 目录不抛异常（目录不存在或为空均合法）
- [ ] P1 规则：有标准曲线时使用标准曲线，无标准曲线时回退到参考曲线
- [ ] 初始化日志打印标准曲线数量

## Further notes

- 本模块是**纯新增**，不影响现有 R1-R8 和 T1 规则。P1 是唯一集成点
- Phase 2（DriftEstimator 近邻漂移估计）将在 D9 中实现，不在本 ticket 范围
- 融合算法 Python 复刻验证脚本位于 `04_tests/scripts/verify_standard_curve.py`
- 标准曲线定义和架构设计详见 `01_docs/diagnosis/标准曲线融合模块设计.md` V2.0
- 段边界过渡算法需与 FeatureExtractor 的五段分割定义完全一致——使用相同的边界计算公式
- `fusionWeight` 默认值 1.0（完全对齐基线），因为基线 ~3000 样本的统计稳健性远超单条参考曲线
- JSON 序列化与项目其他存储一致使用 `JavaScriptSerializer`
- 标准曲线存储目录 `Rules/standard_curves/` 需在部署时创建（诊断引擎会在目录不存在时自动跳过加载）
