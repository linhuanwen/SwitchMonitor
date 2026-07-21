# Slice D9: 近邻漂移估计（DriftEstimator）

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖链**: D2 (基线) → D8 (标准曲线) → **D9** → P1 日内温度适应
> **前置阅读**: [`标准曲线融合模块设计.md`](../标准曲线融合模块设计.md) §4 双层架构、§6.4 DriftEstimator

## Type

AFK

## Blocked by

D8（标准曲线融合模块）

## What to build

在 `SwitchMonitor.Diagnosis` 中新增近邻漂移估计模块，实现 Layer 2（日内温度适应）。

### 设计背景

标准曲线（D8）将参考曲线形态校准到基线统计中心，但基线是固定值——无法应对日内温度变化
（中午热油粘度低、晚上冷油粘度高）。DriftEstimator 从最近 20 条正常事件的局部水平估计当前
温度下的合理漂移，生成当日调整版标准曲线 S'，用于 P1 逐点对比。

**关键设计约束**：
- P1 使用 drift 调整后的 S'（需温度适应，否则冬天参考曲线误报夏天正常曲线）
- R4-R8 继续使用 baseline 原值（需固定锚点防漂移中毒）
- drift clamp 到 [0.85, 1.15]，比 baseline 的 [0.7, 1.3] 更紧——日内温变不应超过 ±15%

详见设计规格 §4 双层架构和 §6.4。

### 新增文件

**`DriftEstimator.cs`**

路径: `SwitchMonitor.Diagnosis/DriftEstimator.cs`

```csharp
/// <summary>各段漂移估计系数。>1 = 当前水平高于标准曲线</summary>
public class SegmentDrift
{
    public double DriftSpike;
    public double DriftUnlock;
    public double DriftConv;
    public double DriftLock;
    public double DriftTail;
    public int NeighborCount;
    public string ComputedAt;
}

public static class DriftEstimator
{
    /// <summary>
    /// 从最近 N 条正常事件的特征中估计各段当前漂移系数。
    /// drift_seg = median(近邻.segMean) / standardCurve 对应段均值
    /// 近邻不足 N 条 → 返回全 1.0；drift clamp 到 [0.85, 1.15]
    /// </summary>
    public static SegmentDrift Estimate(
        StandardCurve standardCurve,
        List<CurveFeatures> recentNormalFeatures,
        int neighborCount = 20
    );

    /// <summary>
    /// 将各段 drift 应用到标准曲线，生成当日温度调整版。
    /// 逐点 α 分配逻辑与 StandardCurveBuilder Step 5 一致。
    /// </summary>
    public static StandardCurve ApplyDrift(
        StandardCurve baseCurve,
        SegmentDrift drift
    );
}
```

**算法**：

1. `Estimate()` — 取最近 N 条正常事件的 CurveFeatures，对每段计算：
   - `drift_conv = median(近邻.ConvMean) / standardCurve 对应段均值`
   - 同理 spike、unlock、lock、tail
   - 各 drift clamp 到 [0.85, 1.15]
   - 近邻不足 N 条 → 全 1.0（无调整）
2. `ApplyDrift()` — 复用 `StandardCurveBuilder` 的 `GetPointAlpha()` 逻辑：
   - 提取 standardCurve 的五阶段边界（调用 FeatureExtractor）
   - 逐点用 drift 替代 baseline α 做缩放
   - 段边界 ±3 点线性过渡

### 修改现有文件

**`DiagnosisEngine.cs`** — P1 集成 drift

- `EvaluateP1()` 中，加载标准曲线后：
  1. 查询最近 20 条正常事件的特征（通过 FeaturesStore 或内存缓存）
  2. 调用 `DriftEstimator.Estimate()` → drift
  3. 调用 `DriftEstimator.ApplyDrift()` → S'
  4. 用 S' 做 P1 逐点对比
- 新增 `_recentNormalCache` 字段（按 switchId 缓存最近 N 条正常事件的特征）
- 每次诊断完成后，如果当前事件判定为正常，添加到缓存

**前置条件**：需要 `features.json` 持久化（D1 已通过 FeaturesStore 实现），或通过
DiagnosisEngine 内部缓存最近正常事件特征。

## Acceptance criteria

- [ ] `dotnet build` 0 错误
- [ ] 近邻 < 20 条时 Estimate 返回全 1.0（不抛异常、不做 drift 调整）
- [ ] drift 值 clamp 在 [0.85, 1.15] 范围内
- [ ] `ApplyDrift` 输出的 Values 长度与输入相同，形态保持（尖峰位置不变，段边界不变）
- [ ] P1 使用 S' 对比，同日不同温度的误报率低于使用固定标准曲线（dryrun 验证）
- [ ] R4-R8 不受 drift 影响（继续用 baseline 原值）

## Further notes

- 本模块是 **Phase 2**，在 D8 完成并验证后再开始实现
- ApplyDrift 的逐点 α 分配逻辑应与 StandardCurveBuilder.GetPointAlpha 完全一致——
  考虑抽取为共享的 internal 方法避免代码重复
- 近邻查询需要按时间排序的 CurveFeatures 列表——建议在 DiagnosisEngine 中维护
  每个 switchId 的最近 N 条正常事件缓存，随 Diagnose() 调用自动更新
- 日内温变通常不超过 ±10%（基于实际运营经验），因此 drift clamp [0.85, 1.15] 是合理的
- 如果发现 drift 超出 clamp 范围，说明近期"正常"曲线实际上已发生系统性偏移，
  应触发 Logger.Warning 提示关注——这可能是基线需要更新的信号
