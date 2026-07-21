# D8 标准曲线融合模块 — TDD 执行交接

> **日期**: 2026-07-14
> **状态**: C# 代码已完成并编译通过，待 TDD 验证

## 当前进度

| # | 任务 | 状态 |
|---|---|---|
| D8-1 | `StandardCurve.cs` | ✅ 已写 |
| D8-2 | `StandardCurveBuilder.cs` | ✅ 已写 |
| D8-3 | `DiagnosisEngine.cs` P1 集成 | ✅ 已写 |
| D8-4 | `dotnet build` | ✅ 0 errors |
| D8-5 | Python dryrun 验证 | ⬜ 有 bug：baselines.json 缺 RefLockMean/RefTailMean 字段，需适配 |
| D8-6 | 目视验证 | ⬜ 依赖 D8-5 输出 |

## TDD 执行计划

### 可并行启动（3 路）

**A 路 — Python dryrun 修复**
- 文件: `04_tests/scripts/verify_standard_curve.py`
- Bug: baselines.json 实际字段名可能不带 "Ref" 前缀（如 LockMean 而非 RefLockMean）
- 修复后跑 `python verify_standard_curve.py`，检查 5 项验收

**B 路 — C# 单元测试**
- 写 `StandardCurveBuilder` 的 NUnit 测试：
  - fusionWeight=0 → 输出 ≈ 输入形态
  - fusionWeight=1 → 各段均值 ≈ baseline
  - clamp 生效
  - 除零保护（LockMean=0 → α=1.0）
  - ResampleLinear 边界（targetCount=1, N=1）
- 写 `StandardCurveStore` 往返测试（Save→Load 一致性）

**C 路 — 代码 review**
- 检查 StandardCurveBuilder.GetPointAlpha() 段边界与 FeatureExtractor 一致性
- 检查过渡区 (blendHalfWidth=3) 的边界 off-by-one
- 检查 DiagnosisEngine P1 集成是否正确使用 StandardCurve 的 AlignIndex

### 串行依赖

```
A 完成 → D8-6 目视验证（用 A 输出的 JSON 叠加 Highcharts）
D8 全部绿灯 → D9 DriftEstimator
```

## 关键文件

| 文件 | 路径 |
|---|---|
| 设计规格 (V2.0) | `01_docs/diagnosis/标准曲线融合模块设计.md` |
| D8 ticket | `01_docs/diagnosis/issues/D8-standard-curve-builder.md` |
| D9 ticket | `01_docs/diagnosis/issues/D9-drift-estimator.md` |
| StandardCurve POCO | `02_source/src/SwitchMonitor.Diagnosis/StandardCurve.cs` |
| StandardCurveBuilder | `02_source/src/SwitchMonitor.Diagnosis/StandardCurveBuilder.cs` |
| DiagnosisEngine (已改) | `02_source/src/SwitchMonitor.Diagnosis/DiagnosisEngine.cs` |
| Python 验证脚本 (有 bug) | `04_tests/scripts/verify_standard_curve.py` |
| 生产基线 | `05_production_data/Rules/baselines.json` |
| 生产数据 | `05_production_data/parsed_data/1-J/` |
| 图片案例 | `01_docs/curve_examples_20220623/` |

## 生产数据格式

- baselines.json: `Switches["1-J"] = { RefDurationSec, RefSpikePeak, RefUnlockMean, RefConvMean, RefLockMean?, RefTailMean?, SampleCount, Direction, DateFrom, DateTo }`
  - ⚠ 需确认 RefLockMean/RefTailMean 是否存在
- parsed_data: `{switchId}/YYYY-MM-DD.json` — 列表，每个事件含 `Power: [[t,v],...]`

## 设计和架构定论（已确认，勿改）

1. **基线不扔** — 独立统计锚点，防漂移中毒
2. **双层架构** — Layer 1 滑动窗口基线（季），Layer 2 近邻漂移（日）
3. **P1 用标准曲线**（未来加 drift），**R4-R8 用 baseline 原值**
4. **fusionWeight 默认 1.0**（完全对齐基线）
5. **α clamp [0.7, 1.3]**（融合），**[0.85, 1.15]**（drift）
6. .NET Framework 4.0 + JavaScriptSerializer + XP 兼容
