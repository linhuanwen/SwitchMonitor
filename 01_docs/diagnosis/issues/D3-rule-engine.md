# Slice D3: 规则引擎（R0-R8）+ thresholds.json

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖链**: D1 (特征) → D2 (基线) → **D3** → D4 (管道) → D5 (UI)
> **前置阅读**: [`CONTEXT.md §4`](../CONTEXT.md) — 规则表与全量演习结果；[PRD §4.3](../PRD.md) — 规则功能规格

## Type

AFK

## Blocked by

D1（特征提取器）、D2（基线）

## What to build

在 `SwitchMonitor.Diagnosis` 中实现 `IDiagnosisEngine`（规则引擎），阈值全部来自
`Rules/thresholds.json`；在 `DiagTool` 中增加 `dryrun` 子命令做全量演习验证。

### thresholds.json（默认模板，由本 slice 随代码提供）

```json
{
  "version": 1,
  "rules": {
    "R1": { "enabled": true, "level": "故障", "durOverRefSeconds": 3.0 },
    "R2": { "enabled": true, "level": "报警", "durUnderRefRatio": 0.6 },
    "R3": { "enabled": true, "level": "预警", "maxDeviationSeconds": 0.5 },
    "R4": { "enabled": true, "level": "预警", "overRefRatio": 1.3 },
    "R5": { "enabled": true, "level": "预警", "overRefRatio": 1.3 },
    "R6": { "enabled": true, "level": "报警", "maxStepRatio": 1.5, "minStepRatio": 0.67 },
    "R7": { "enabled": true, "level": "预警", "overRefRatio": 1.3 },
    "R8": { "enabled": true, "level": "预警", "deviationRatio": 0.3 }
  }
}
```

R0（采集异常）不可配置、恒启用、级别恒为"报警"。级别字段允许现场改（如把 R6 降为预警）。

### DiagnosisEngine

```csharp
public class DiagnosisEngine : IDiagnosisEngine
{
    public void Initialize(string rulesDir);   // 读 thresholds.json + baselines.json；
                                               // 文件缺失/损坏 → 用内置默认阈值 + 空基线，并 Logger.Warning
    public List<DiagnosisResult> Diagnose(string switchId, CurveFeatures f);
}
```

判定逻辑（判据严格按 `CONTEXT.md §4` 规则表）：

1. `!f.IsValid` → 返回 [R0 采集异常]，**终止**
2. 该 switchId 无基线 → 仅评估 R1 的 `isFullWindow` 分支（唯一不依赖基线的硬判据），
   命中返回 [R1]，否则返回空列表（正常），并在首次遇到时 `Logger.Warning("道岔{id}无基线，仅执行硬规则")`
3. R1 命中（`IsFullWindow || DurationSec > refDur + durOverRefSeconds`）→ 返回 [R1]，**终止**
4. R2 命中（`DurationSec < refDur × durUnderRefRatio`）→ 返回 [R2]，**终止**
5. 依次评估 R3-R8，命中的全部加入结果列表（可多条）；
   R8 特例：`TailMean == 0` 视为"缓放段缺失"命中
6. 返回列表（空 = 正常）

**R1/R2 终止逻辑设计理由**：超时/夭折曲线的后续段特征（convMean、tailMean 等）必然异常，
继续评估 R3-R8 只会输出噪音（如夹具 C 的 convMean=0.545 会误触 R5）。详见 PRD §4.3。

Description 为中文并带数值，模板示例：
- R1: `"动作时长31.36s，超过参考11.72s+3.0s，疑似卡阻/空转未完成"`
- R2: `"动作时长0.84s，不足参考11.72s的60%，动作夭折"`
- R5: `"转换段功率0.545kW，超过参考0.301kW的1.3倍，疑似转换阻力增大"`
- 数值格式：时长 2 位小数 + "s"，功率 3 位小数 + "kW"

### 事件综合级别辅助

```csharp
public static class DiagnosisAggregator
{
    // 取结果列表中最高级别；空列表 → "正常"
    public static string OverallLevel(List<DiagnosisResult> results);
}
```

### DiagTool dryrun 子命令

```
DiagTool.exe dryrun <sanshuibei_csv目录> <Rules目录>
```

对 8 个功率文件（3,7,11,15,19,23,27,31 → 对应 switchId 见 CONTEXT §2.2）的每一行跑
提取+诊断，打印：每台道岔 × 每条规则的触发数矩阵、每台触发率、总触发率。

## Acceptance criteria

- [ ] 4 个金标准夹具的诊断结论正确（用 1-1/1-X/4-1/4-X 的基线）：
      夹具A → 空列表（正常）；夹具B → 空列表；夹具C → 恰好 [R1 故障]；夹具D → 恰好 [R2 报警]
      （selftest 子命令扩展这 4 条断言）
- [ ] `dryrun` 各规则触发数与 `CONTEXT.md §4` 演习值偏差 ≤ ±10%：
      R0≈6、R1≈198、R2≈24、R3≈147、R4≈42、R5≈6、R6≈1，总触发 375（1.56%±0.3%）
      （R7/R8 为新增规则无参照值，只要求打印出触发数备查）
- [ ] thresholds.json 删掉后引擎仍能用内置默认值工作（输出与默认模板一致）
- [ ] 把 R3 的 `maxDeviationSeconds` 改成 0.2 再 dryrun，R3 触发数显著上升——验证阈值真正从 JSON 生效
- [ ] `enabled:false` 的规则不参与评估
- [ ] 交叉验证：`python 02_source/tools/diag_reference_check.py dryrun` 的规则触发矩阵一致（R1-R6）

## Further notes

- R1/R2 终止后不再评估 R3-R8 是**有意设计**：超时曲线的 convMean/tailMean 必然异常，
  叠加输出只会产生噪音（夹具 C 的 convMean=0.545 就会误触 R5）
- 引擎必须无状态可重入（除 Initialize 加载的配置外），后续 D4 在导入线程中调用
- 内置默认阈值直接以常量写在代码里，与 thresholds.json 模板保持同值；模板文件放
  `SwitchMonitor.DiagTool` 输出目录的 `Rules\` 下随构建复制，部署时拷到程序目录
- **R7/R8 的实际误报率未知**（演习未覆盖），默认阈值先按 1.3×/±30% 上线，现场观察一周后调 JSON 即可——
  这正是阈值外置的意义
- **现场调参**：D5 将提供 WinForms 诊断参数设置对话框（合并 Slice 08）；在此之前直接编辑 JSON
