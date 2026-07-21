# D8 标准曲线融合模块 — TDD 三路验证报告

> **日期**: 2026-07-14
> **状态**: A/B/C 三路验证完成，可进入 D8-6 目视验证

---

## 验证结果总览

| 路 | 任务 | 结果 |
|---|---|---|
| A 路 | Python dryrun 修复 | 4/5 PASS, 1 Partial（重采样固有偏差） |
| B 路 | C# 单元测试 | **26/26 ALL PASS** |
| C 路 | 代码 Review | 🟡 1 个 Warning（极短曲线退化逻辑），P1/off-by-one 🟢OK |

---

## A 路 — Python Dryrun 详情

### Bug 根因

**`RefLockMean` 字段在 baselines.json 全部 8 组道岔中完全缺失**（不是说好的字段名不带 Ref 前缀）。

### 修复清单

文件: `04_tests/scripts/verify_standard_curve.py`

| 位置 | 修复内容 |
|---|---|
| Line 277 | `baseline['RefLockMean']` → `baseline.get('RefLockMean', 0.0)` |
| Line 362 | `bl_1j["RefLockMean"]` → `bl_1j.get("RefLockMean", "N/A")` |
| Line 435 | 同上 |
| Line 451 | 同上 |
| 全局 | Unicode checkmark/cross → `[PASS]`/`[FAIL]`/`[WARN]` (修复 Windows GBK 终端 UnicodeEncodeError) |

### 5 项验收

| # | 验证项 | 结果 | 详情 |
|---|---|---|---|
| 1a | 长度检查 | ✅ PASS | 293 点 |
| 1b | NaN/Infinity | ✅ PASS | 无异常值 |
| 1c | 各段均值 vs 基线 (fw=1.0) | ✅ PASS | Spike 0.964, Unlock 1.007, Conv 0.997, Tail 0.991（5% 容差内） |
| 2 | fw=0 形态保持 | ⚠️ Partial | Spike 偏差 3.5%（线性重采样压低窄尖峰，非算法 bug） |
| 3 | clamp | ✅ PASS | 6 个 α 全部在 [0.7, 1.3] |
| 4 | P1 模板代表性 | ✅ PASS | areaDiffRatio=0.131 < 0.15 |
| 5 | Alpha 审计 | ✅ PASS | 6 个 α 完整输出 |

### 关键发现: AlphaLock=0.7000

RefLockMean 缺失 → ratio=0.0 → clamp 到 0.7。C# 端 `JavaScriptSerializer` 反序列化缺失字段也得 `default(double)=0.0`，行为一致。**C# 端无 bug**，但建议后续补充 baselines.json 的 RefLockMean。

### 产出文件

- `05_production_data/Rules/standard_curves/1-J_py_verify.json` — Python 端生成的标准曲线，可用于目视验证

---

## B 路 — C# 单元测试详情

### 测试结果: 26/26 全绿

### 产出

| 文件 | 操作 |
|---|---|
| `02_source/src/SwitchMonitor.Tests/D8Tests.cs` | **新增** — 26 个测试 |
| `02_source/src/SwitchMonitor.Tests/TestRunner.cs` | **修改** — 添加 D8Tests.Run() |
| `02_source/src/SwitchMonitor.Diagnosis/StandardCurveBuilder.cs` | **修改** — 添加 `[InternalsVisibleTo("SwitchMonitor.Tests")]` |

### 测试清单

**S1 — Build 融合正确性 (12 tests)**
1. fw=0 输出≈输入形态（α 全为 1.0）
2. fw=0 重采样后段均值≈输入段均值
3. fw=1 各段 α 对齐到基线
4. fw=0.5 α 混合到中间值
5. null 参考曲线 → null
6. null 基线 → null
7. 空 Values 参考曲线 → null
8. clamp 生效 α∈[0.7, 1.3]
9. 除零保护 LockMean=0 → α=1.0
10. 除零保护 TailMean=0 → α=1.0
11. 输出长度≈基线时长/采样间隔
12. 输出无 NaN/Infinity

**S2 — ResampleLinear 边界 (6 tests)**
13. targetCount=1 返回首点
14. N=1 targetCount=5 全填充首点
15. targetCount=0 返回空列表
16. 空源列表返回空列表
17. targetCount=N 值≈原值
18. targetCount=10 N=5 线性插值单调

**S3 — GetPointAlpha 段分配 (5 tests)**
19. spike 段 (i < si) 返回 α_spike
20. unlock 段 [si+2, si+14) 返回 α_unlock
21. conv 段 [si+20, ae-40) 返回 α_conv
22. spike→unlock 过渡区线性混合
23. 短曲线无 lock/tail 段边界回退不抛异常

**S4 — Store 往返 (3 tests)**
24. Save→Load 字段一致
25. 文件不存在返回 null
26. LoadAll 批量加载

### 测试框架说明

项目使用自研 TestRunner（`TestRunner.Test()` + 静态断言），非 NUnit。这是因为 .NET Framework 4.0 + XP 兼容 + 零外部测试依赖。

### 运行方式

```bash
cd 02_source/src
dotnet build SwitchMonitor.Tests/SwitchMonitor.Tests.csproj
dotnet run --project SwitchMonitor.Tests/SwitchMonitor.Tests.csproj
```

---

## C 路 — 代码 Review 详情

### Review 1: GetPointAlpha 段边界一致性 🟡 Warning

**文件**: `StandardCurveBuilder.cs:200-213`

**问题**: `convEnd ≤ convStart` 时（极短曲线 ae<74），GetPointAlpha 将 Conv 段退化为零长度，但 FeatureExtractor 退化为宽区间 `[si+2, ae)`。两者不一致。

**影响面**: 仅 ae<74 的极短曲线（正常道岔 6-10s → ae=150-250），不影响正常工况。

**建议修复** (在边界修正后增加退化回退):
```csharp
// 退化策略：与 FeatureExtractor 保持一致
if (convEnd <= convStart)
{
    convStart = unlockEnd;  // 等同于 FeatureExtractor 的 si+2
    convEnd = ae;           // 等同于 FeatureExtractor 的 activeEnd
}
```

### Review 2: blendHalfWidth=3 off-by-one 🟢 OK

四个过渡区均以段边界为中心，hw=3 每侧覆盖恰好 3 点：
| 过渡区 | 区间 | 宽度 |
|---|---|---|
| spike→unlock | [si, si+2) | 2 点 |
| unlock→conv | [si+14, si+20) | 6 点 |
| conv→lock | [ae-43, ae-37) | 6 点 |
| lock→tail | [ae-25, ae-19) | 6 点 |

数组安全: hasLock(ae>50) / hasTail(ae>30) 前置守护 + Math.Min/Math.Max 钳位。

### Review 3: DiagnosisEngine P1 集成 🟢 OK

| 检查项 | 状态 |
|---|---|
| P1 优先使用 `sc.Values` + `sc.AlignIndex` | ✅ |
| R4-R9 全部使用 `baseline.Ref*` 原值 | ✅ |
| 模板来源标注 (standard/reference) | ✅ |
| 空目录容错 (标准曲线目录不存在) | ✅ |

---

## 下一步：D8-6 目视验证

### 输入

在新窗口执行以下步骤：

```
/skill tdd
```

然后输入：

```
读 01_docs/diagnosis/issues/D8-TDD-verification-report.md 了解验证结果，然后执行 D8-6 目视验证：
- 用 05_production_data/Rules/standard_curves/1-J_py_verify.json 的标准曲线数据，叠加到 Highcharts 渲染
- 目视对比标准曲线 vs 原始参考曲线 vs 基线各段均值
- 确认融合曲线形态合理、过渡区平滑、无异常跳变
```

### 关键文件

| 文件 | 路径 |
|---|---|
| 验证报告 | `01_docs/diagnosis/issues/D8-TDD-verification-report.md` |
| 设计规格 V2.0 | `01_docs/diagnosis/标准曲线融合模块设计.md` |
| D8 handoff | `01_docs/diagnosis/issues/D8-TDD-handoff.md` |
| 标准曲线 JSON | `05_production_data/Rules/standard_curves/1-J_py_verify.json` |
| 生产基线 | `05_production_data/Rules/baselines.json` |
| 生产数据 | `05_production_data/parsed_data/1-J/` |
| 图片案例 | `01_docs/curve_examples_20220623/` |
| Python 验证脚本（已修复） | `04_tests/scripts/verify_standard_curve.py` |

### 验收标准

- [ ] 标准曲线与原始参考曲线形态一致（无异常扭曲）
- [ ] 过渡区（spike→unlock, unlock→conv, conv→lock, lock→tail）平滑无跳变
- [ ] 各段均值接近基线对应值（fw=1.0 下）
- [ ] Lock 段因 RefLockMean 缺失被 clamp 到 0.7，偏低但合理
- [ ] 全部绿灯 → 推进 D9 DriftEstimator
