# Slice D1: 诊断项目脚手架 + 功率曲线特征提取器

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖**: D2 (基线) → D3 (规则) → D4 (管道) → D5 (UI 展示) → D6 (趋势, 二期)
> **前置阅读**: [`01_docs/diagnosis/CONTEXT.md`](../CONTEXT.md) — 模块背景、算法规格、金标准夹具

## Type

AFK

## Blocked by

None — 可立即开始

## What to build

在 `02_source/src/` 下新增两个项目并加入 `SwitchMonitor.sln`：

```
SwitchMonitor.Diagnosis\          # 类库 (.dll)，.NET 4.0，x86
├── CurveFeatures.cs              # 特征 POCO
├── DiagnosisResult.cs            # 诊断结论 POCO + 级别常量
├── IDiagnosisEngine.cs           # 引擎接口（本 slice 只定义，D3 实现）
├── FeatureExtractor.cs           # 特征提取 + 阶段分割（本 slice 核心）
└── Properties\AssemblyInfo.cs

SwitchMonitor.DiagTool\           # 控制台 (.exe)，.NET 4.0，x86
├── Program.cs                    # 子命令分发: selftest（本 slice 只做这个）
└── Properties\AssemblyInfo.cs
```

依赖方向：`Diagnosis → Data`（复用 `SwitchEvent`）；`DiagTool → Diagnosis + Data`。

### CurveFeatures POCO

```csharp
public class CurveFeatures
{
    public int SampleCount;      public bool IsFullWindow;   public bool IsValid;
    public int ActiveEnd;        public double DurationSec;
    public double SpikePeak;     public int SpikeIndex;
    public double UnlockMean;
    public double ConvMean;      public double ConvMax;      public double StepRatio;
    public double TailMean;
}
```

### DiagnosisResult POCO 与级别

```csharp
public static class DiagnosisLevel   // 级别常量，递增
{
    public const string Normal = "正常"; public const string Warning = "预警";
    public const string Alarm = "报警";  public const string Fault = "故障";
    public static int Severity(string level);   // 正常=0 预警=1 报警=2 故障=3
}

public class DiagnosisResult
{
    public string RuleId;        // "R1"
    public string RuleName;      // "动作超时/未完成"
    public string Level;         // DiagnosisLevel.*
    public string Description;   // 中文结论（含数值）
    public double Value;         // 异常值
    public double Reference;     // 参考值
}

public interface IDiagnosisEngine
{
    void Initialize(string rulesDir);                     // 读 thresholds.json + baselines.json
    List<DiagnosisResult> Diagnose(string switchId, CurveFeatures features);
}
```

### FeatureExtractor

```csharp
public static class FeatureExtractor
{
    // 核心入口：values 为功率采样值序列（kW，0.04s/点）
    public static CurveFeatures Extract(IList<double> values);
    // 便捷入口：从 SwitchEvent.Power 的 [t,v] 对中抽取 v 列后调用上面
    public static CurveFeatures Extract(SwitchEvent evt);
}
```

算法**逐行**按 `CONTEXT.md §3` 的规格实现，不得自行发挥。要点复述：
- `isFullWindow = n >= 780`；`isValid = n>0 && max>0.01`，无效时其余字段保持默认值
- `activeEnd` = 最后一个 `> max(峰值*0.05, 0.01)` 的下标
- 尖峰在前 15 点内找；解锁段 `[sp+2, sp+14)`；转换段 `[sp+20, activeEnd-40)` 带两级退化
- `stepRatio` 前 1/3 长度 < 5 点时恒为 1.0；缓放段 `[activeEnd-22, activeEnd-2)`，`activeEnd<=30` 时 tailMean=0
- 数值输出 `Math.Round(x, 3)`（durationSec 为 2 位）

### DiagTool selftest 子命令

```
DiagTool.exe selftest <sanshuibei_csv目录>     # 目录参数必填；缺失时打印用法并退出码 2
```

内置 `CONTEXT.md §3` 的 4 个金标准夹具（文件名 + timestamp + 12 项期望值），运行时：
1. 用独立的简易 CSV 读取器（跳过表头，每行 = `timestamp,datetime,phase,s0,s1,...`，尾部空列截断）
   读取指定文件，按 timestamp 找到目标行（夹具 D 的 timestamp 在文件中唯一）
2. 对采样值跑 `FeatureExtractor.Extract`
3. 与期望值逐项对比（浮点容差 ±0.002，整型/bool 精确），打印每项 PASS/FAIL
4. 全部通过退出码 0，否则 1

## Acceptance criteria

- [ ] 解决方案新增 2 个项目，Debug|x86 编译零警告零错误，目标框架 .NET Framework 4.0
- [ ] `DiagTool.exe selftest` 4 个夹具 × 12 项特征全部 PASS，退出码 0
- [ ] 夹具 D（27 点短曲线）不抛异常——边界退化路径被覆盖
- [ ] 空数组、全零数组输入 `Extract` 返回 `IsValid=false` 不抛异常
- [ ] `Extract(SwitchEvent)` 正确从 `[t,v]` 对中抽取 v 列（写一个手工构造的最小用例验证）
- [ ] 交叉验证：`python 02_source/tools/diag_reference_check.py golden` 的输出与 selftest 打印的实测值一致

## Further notes

- 不引入任何第三方库；JSON 后续 slice 用 `JavaScriptSerializer`（Data 项目已有先例）
- `SwitchEvent.Power` 可能为空列表（该事件只有电流没功率）——`Extract(SwitchEvent)` 返回 `IsValid=false`
- CSV 读取器放在 DiagTool 内部（`internal class SimpleCsvReader`），不要复用 Data 的
  `CsvDataReader`（它是 internal 且带相位配对逻辑，语义不同）
- Python 参考实现 `diag_reference_check.py` 与本 slice 的 C# 实现必须产出相同数值；
  任何偏差都按 Python 版为准（金标准数值即由它生成）
- **D1 产出物直接影响 D2-D4**：`CurveFeatures` 字段、`DiagnosisResult` 结构、`IDiagnosisEngine` 接口
  一旦在 D2/D3 中被依赖即不可 breaking change。本次定义已充分对齐 `CONTEXT.md` 和 PRD
