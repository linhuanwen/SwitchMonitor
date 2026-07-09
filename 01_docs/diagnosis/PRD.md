# 曲线报警分析模块 PRD

> **模块代号**: Diagnosis（曲线报警）
> **版本**: V2.0
> **日期**: 2026-07-08
> **目标平台**: 研华 610H 工控机, Windows XP SP3 32-bit, .NET Framework 4.0
> **依赖**: SwitchMonitor 主项目（数据导入、曲线展示）

---

## 1. Problem Statement

既有 CSM 站机软件虽然能够正常采集和展示道岔动作电流/功率曲线，但存在以下问题：

1. **无自动诊断能力**：软件只提供曲线查看，不对每次动作自动判断是否异常。运维人员只能在故障发生后，被动地翻看曲线查找原因。

2. **诊断知识无法沉淀**：经验丰富的技术人员知道"转换段功率抬高→滑床板缺油""动作时长突增→卡阻"等规律，但这些知识无法编码为自动规则，人走经验丢。

3. **异常发现严重滞后**：道岔以每天约 15 动的频率工作，人工不可能逐条查看。从数据来看，三水北站 8 台道岔半年 23,999 次动作中已有 375 次明确异常（1.56%），包括 198 次卡阻超时、24 次动作夭折，这些本可以提前预警。

4. **无法量化评估道岔健康**：没有基线数据，判断"正常与否"完全依赖人工记忆和主观经验，无法做趋势分析。

**核心诉求**：在现有曲线展示功能之上，增加一套**自动诊断引擎**，每次道岔动作后自动分析功率曲线，输出 正常/预警/报警/故障 四级结论，让异常在第一时间被发现。

---

## 2. Solution Overview

### 2.1 一句话描述

> 基于每台道岔历史数据自动建立统计基线，对每次动作的功率曲线提取 12 维特征，通过 9 条可配置规则判定异常级别，结果在 UI 中以颜色和文字清晰呈现。

### 2.2 核心思路

```
历史功率曲线 (23,999条)
       │
       ▼
  ┌─────────────┐     ┌──────────────────┐
  │ D2 基线构建  │────▶│ baselines.json   │  每台道岔的"正常标准"
  └─────────────┘     └──────────────────┘
                             │
  新动作发生                  │
       │                     │
       ▼                     ▼
  ┌─────────────┐     ┌──────────────────┐
  │ D1 特征提取  │     │ thresholds.json  │  可配置的判定阈值
  └─────────────┘     └──────────────────┘
       │                     │
       └──────────┬──────────┘
                  ▼
         ┌───────────────┐
         │ D3 规则引擎    │  R0-R8 规则评估
         └───────────────┘
                  │
                  ▼
         ┌───────────────┐
         │ D4 管道集成    │  .diag.json + alarms_index.json
         └───────────────┘
                  │
                  ▼
         ┌───────────────┐
         │ D5 UI 展示     │  诊断条 + 时间列表着色 + 日期角标
         └───────────────┘
```

### 2.3 为什么是统计阈值而非 ML/DL

实测 23,999 条正常曲线特征分布显示，**动作时长 P5-P95 离散仅 ±1%，转换段均值离散约 ±6%**。正常道岔的动作曲线高度一致，简单统计阈值即可高灵敏低误报。考虑到目标平台 (XP + .NET 4.0 + 单核 CPU) 的计算能力限制，以及规则的可解释性要求（现场人员需要理解"为什么报警"），决定采用**统计基线 + 可配置阈值规则**的方案。

### 2.4 三级诊断体系

| 层级 | 内容 | 阶段 | 说明 |
|------|------|------|------|
| L1 完整性硬规则 | 超时/夭折/采集异常 | 一期 (D1-D5) | 不依赖基线，直接判定 |
| L2 分阶段特征阈值 | 相对基线的偏差 | 一期 (D1-D5) | 核心诊断能力 |
| L3 逐点对比 + 趋势 | 形态偏离 + 缓慢劣化 | 二期 (D6) | 补充阈值法盲区 |

---

## 3. User Stories

### 自动诊断

> **US-25**: As a 运维人员, I want 每次道岔动作后程序自动分析功率曲线并给出诊断结论, so that 异常情况第一时间被发现而不是等到人工查看时。

> **US-26**: As a 运维人员, I want 诊断结果以"正常/预警/报警/故障"四个级别标记, so that 我能根据严重程度安排处理优先级。

> **US-27**: As a 运维人员, I want 诊断结论以清晰的中文描述呈现（如"转换段功率 0.545kW，超过参考 0.301kW 的 1.3 倍，疑似转换阻力增大"）, so that 不熟悉数据分析的人也能理解问题所在。

### 基线管理

> **US-28**: As a 系统管理员, I want 程序能从历史数据自动计算每台道岔的正常基线, so that 不需要人工逐台设定标准值。

> **US-29**: As a 系统管理员, I want 基线可按季度手动重算更新, so that 适应设备随季节的缓慢特性漂移（实测半年仅 ~3%）。

### 诊断规则配置

> **US-30**: As a 系统管理员, I want 通过编辑 JSON 配置文件来调整每条诊断规则的阈值和启停, so that 不同道岔的差异化标准无需改代码即可生效。

> **US-31**: As a 系统管理员, I want 修改阈值后能一键重跑全量诊断而不重新导入原始数据, so that 调参后立即看到效果。

### UI 报警展示

> **US-32**: As a 运维人员, I want 侧边栏时间列表中异常动作以黄/橙/红色标记, so that 我一眼就能从历史记录中定位异常事件。

> **US-33**: As a 运维人员, I want 图表页顶部显示当前事件的诊断结论条（正常绿/故障红）, so that 打开曲线立即看到诊断结果。

> **US-34**: As a 运维人员, I want 日期下拉菜单中带异常计数的角标, so that 我知道哪些天有道岔异常需要关注。

### 诊断日志

> **US-35**: As a 系统管理员, I want 诊断引擎输出运行日志（diag.log），记录每次诊断的规则评估过程, so that 可以事后审计诊断逻辑是否正确、评估规则质量。

---

## 4. Functional Specification

### 4.1 功率曲线特征提取

**输入**：一次道岔动作的功率采样值序列（kW，25Hz / 0.04s 间隔，来源于 `SwitchEvent.Power`）

**输出**：12 维特征值 `CurveFeatures`

| 字段 | 类型 | 说明 |
|------|------|------|
| `SampleCount` | int | 原始采样点数 |
| `IsFullWindow` | bool | 是否打满录制窗口（n ≥ 780 点 ≈31.2s） |
| `IsValid` | bool | 曲线是否有效（n > 0 且峰值 > 0.01kW） |
| `ActiveEnd` | int | 有效动作终点下标（去尾部零填充） |
| `DurationSec` | double | 动作时长 (activeEnd+1) × 0.04，秒 |
| `SpikePeak` | double | 启动尖峰最大值（前 15 点内搜索） |
| `SpikeIndex` | int | 启动尖峰所在下标 |
| `UnlockMean` | double | 解锁段均值（尖峰后 [sp+2, sp+14)） |
| `ConvMean` | double | 转换段均值（[sp+20, activeEnd-40)，带两级退化） |
| `ConvMax` | double | 转换段最大值 |
| `StepRatio` | double | 台阶比 = 转换段后 1/3 均值 / 前 1/3 均值 |
| `TailMean` | double | 缓放段均值（[activeEnd-22, activeEnd-2)） |

**五阶段分割**（道岔动作的物理过程）：

```
启动尖峰 ──▶ 解锁段 ──▶ 转换段（主体）──▶ 锁闭凹口 ──▶ 缓放尾段
 0.3s         0.5s        5-10s           0.3s         0.9s
```

### 4.2 基线定义

对每台道岔（switchId），从全部历史曲线中筛选正常样本：

1. 排除 `IsFullWindow`、`!IsValid`、`DurationSec < 2.4s`
2. 时长在总体中位数 ±15% 范围内
3. 对剩余样本的每项特征取**中位数**，得到 5 项基线值：

| 基线值 | 对应特征 | 1-1 (J型) 参考值 |
|--------|----------|------------------|
| `RefDurationSec` | 动作时长 | 11.72s |
| `RefSpikePeak` | 启动峰值 | 3.235 kW |
| `RefUnlockMean` | 解锁段均值 | 0.307 kW |
| `RefConvMean` | 转换段均值 | 0.301 kW |
| `RefTailMean` | 缓放段均值 | 0.214 kW |

一期不分动作方向建基线（同一道岔时长无双峰分布），后续如需分方向，基线结构已按 switchId 组织可直接扩展。

### 4.3 诊断规则表（R0-R8）

判定顺序：R0 → R1 → R2 命中即终止；否则 R3-R8 全部评估可多命。

| ID | 规则名 | 判据 | 级别 | 对应病害 | 24k 演习触发 |
|----|--------|------|------|----------|-------------|
| R0 | 采集异常 | `!isValid` | 报警 | 采集/解析故障 | 6 |
| R1 | 动作超时/未完成 | `isFullWindow` 或 `dur > refDur + 3.0s` | **故障** | 卡阻、空转、异物 | 198 |
| R2 | 动作夭折 | `dur < refDur × 0.6` | **报警** | 未解锁、保护切断 | 24 |
| R3 | 动作时长偏差 | `|dur − refDur| > 0.5s` | 预警 | 阻力变化早期征兆 | 147 |
| R4 | 启动峰值偏高 | `spikePeak > refSpikePeak × 1.3` | 预警 | 启动回路/机械卡滞 | 42 |
| R5 | 转换段功率偏高 | `convMean > refConvMean × 1.3` | 预警 | 滑床板缺油、异物 | 6 |
| R6 | 转换段台阶突变 | `stepRatio > 1.5` 或 `stepRatio < 0.67` | 报警 | 中途受阻/空转 | 1 |
| R7 | 解锁段偏高 | `unlockMean > refUnlockMean × 1.3` | 预警 | 密贴过紧、卡缺口 | 未演习 |
| R8 | 缓放段异常 | `tailMean` 偏离 `refTailMean ±30%` | 预警 | 锁闭/开闭器异常 | 未演习 |

**事件综合级别** = 命中规则的最高级别；无命中 = 正常。

**R1/R2 终止逻辑**：超时/夭折曲线的后续段特征必然异常，叠加输出只会产生噪音（如超时曲线的 convMean 必然偏高），终止评估避免重复报警。

**演习总结**（三水北站 8 台道岔，2025-12 ~ 2026-06，23,999 事件）：
- 触发 375 条（1.56%），≈每台每天 0.2 条报警，运维可接受
- 分道岔触发率：4-1 最差 3.17%，3-X 最好 0.53%

### 4.4 诊断管道集成

- 导入管道在每天数据保存后自动触发诊断（`DataPipeline.DiagnoseHook` 委托）
- 委托解耦：Data 项目不引用 Diagnosis，由 UI/DiagTool 在启动时装配
- 诊断失败不中断导入（try/catch，记 Logger.Error）
- 可通过 `config.json` 的 `diagnosis.enabled` 开关控制
- 重复导入同一天时覆盖旧的诊断结果

### 4.5 存储文件

| 文件 | 内容 | 格式 |
|------|------|------|
| `Rules/thresholds.json` | 9 条规则的启用/级别/阈值参数 | JSON，手工可编辑 |
| `Rules/baselines.json` | 每台道岔的 5 项基线值 + 元数据 | JSON，D2 工具生成 |
| `parsed_data/{switchId}/{date}.diag.json` | 该日每个事件的诊断结论 | JSON 数组，与 `{date}.json` 并列 |
| `parsed_data/alarms_index.json` | switchId → date → 级别计数 | JSON，UI 角标用 |
| `diag.log` | 诊断运行日志 | 文本，含规则评估明细 |

### 4.6 UI 报警展示

三处改动，全部基于现有 WebBrowser + HTML/JS 架构，**ES5 + IE8 兼容**：

#### a) 诊断结论条（charts.html 顶部）

```
┌──────────────────────────────────────────────────────────┐
│ ● 故障  动作时长31.36s，超过参考11.72s+3.0s，疑似卡阻/空转未完成  │  ← 红色背景
└──────────────────────────────────────────────────────────┘
```

- 正常 = 绿色小圆点 + "诊断正常"
- 预警 = 黄色背景 + 结论文字
- 报警 = 橙色背景 + 结论文字
- 故障 = 红色背景 + 结论文字
- 无数据 = 灰色"未诊断"

#### b) 侧边栏时间列表着色

```
时间列表:
  06:16:30
  07:22:15 预警          ← 黄色文字
  09:05:42
  10:30:18 报警          ← 橙色文字 + 左边框
  12:11:33
  14:28:07
  16:43:51 预警
  18:19:24
```

#### c) 日期下拉角标

```
2026-01-29 (3)    ← 红色（有故障/报警）
2026-02-13 (1)    ← 橙色（仅有预警）
2026-02-14        ← 无异常不显示计数
```

### 4.7 诊断日志（diag.log）

每条诊断记录包含：

```
[2026-01-29 00:43:17] switchId=4-1 eventTs=1769618597
  Features: dur=31.36s convMean=0.545 spikePeak=4.353 ...
  Baseline: refDur=11.72 refConvMean=0.266 ...
  R1: dur(31.36) > refDur(11.72)+3.0 → HIT (故障)
  R2-R8: SKIPPED (R1 终止)
  Overall: 故障
```

用途：事后审计诊断逻辑、评估规则误报/漏报、为阈值调整提供依据。

### 4.8 阈值配置 UI（合并 Slice 08）

将原 Slice 08 的静态阈值配置融入诊断参数管理：

- "工具"菜单 → "诊断参数设置" → 弹出模态 WinForms 对话框
- 展示/编辑 thresholds.json 中每条规则的启用状态、级别、阈值参数
- 保存后更新内存中的引擎配置 + 写回 JSON
- 可选：保存后自动触发"重新诊断当前数据"

> 原 Slice 08 的电流/功率阈值线属于图表展示层（yAxis.plotLines），与诊断引擎的规则阈值是不同概念。图表阈值线继续走 `config.json → JS updateThreshold()` 通道，诊断规则阈值走 `thresholds.json → DiagnosisEngine` 通道。两者在 UI 上合并在同一个"诊断参数"对话框中管理。

---

## 5. Technical Design

### 5.1 新增项目结构

```
SwitchMonitor.sln
├── SwitchMonitor.Diagnosis\        # 类库 (.dll)，.NET 4.0 / x86
│   ├── CurveFeatures.cs            # 12 维特征 POCO
│   ├── DiagnosisResult.cs          # 诊断结论 POCO + 级别常量
│   ├── IDiagnosisEngine.cs         # 引擎接口
│   ├── FeatureExtractor.cs         # 特征提取 + 五阶段分割
│   ├── BaselineBuilder.cs          # 基线计算
│   ├── SwitchBaseline.cs           # 基线 POCO + BaselineStore 读写
│   ├── DiagnosisEngine.cs          # 规则引擎实现 (R0-R8)
│   ├── DiagnosisAggregator.cs      # 事件综合级别
│   ├── DiagnosisRunner.cs          # 引擎 + 提取 + 聚合的组合封装
│   └── Properties\AssemblyInfo.cs
│
├── SwitchMonitor.DiagTool\         # 控制台 (.exe)，.NET 4.0 / x86
│   ├── Program.cs                  # 子命令: selftest / baseline / dryrun / rerun
│   ├── SimpleCsvReader.cs          # 独立 CSV 读取器（不依赖 Data 的 CsvDataReader）
│   └── Properties\AssemblyInfo.cs
```

依赖方向：`UI → Diagnosis → Data`、`DiagTool → Diagnosis → Data`。**Data 不引用 Diagnosis**（通过委托解耦），保证引擎可独立替换。

### 5.2 核心接口

```csharp
// 引擎接口（UI 和管道通过此接口调用诊断）
public interface IDiagnosisEngine
{
    void Initialize(string rulesDir);                       // 加载 thresholds.json + baselines.json
    List<DiagnosisResult> Diagnose(string switchId, CurveFeatures features);
}

// 管道集成钩子（定义在 Data 项目，Diagnosis 产生诊断结果）
// DataPipeline 新增：
public Func<string, SwitchEvent, EventDiagnosis> DiagnoseHook;  // switchId, evt → 结果

// 装配封装（Diagnosis 项目提供，避免 UI 层写样板代码）
public static class DiagnosisRunner
{
    public static EventDiagnosis Run(IDiagnosisEngine engine, string switchId, SwitchEvent evt);
    public static void RerunAll(IndexManager indexManager, IDiagnosisEngine engine);
}
```

### 5.3 算法要点

**特征分割退化策略**：
- 转换段首选 `[sp+20, activeEnd-40)`；区间无效 → `[sp+2, activeEnd)`；仍空 → `[0, activeEnd]`
- 缓放段 `[activeEnd-22, activeEnd-2)`；`activeEnd ≤ 30` 时 tailMean = 0（短曲线无缓放段）
- StepRatio 前 1/3 区间 < 5 点时恒为 1.0

**基线计算**：
- 正常样本筛选后取各特征中位数（偶数样本取中间两数均值，与 Python `statistics.median` 一致）
- 某个道岔正常样本 < 30 条 → 该台不建立基线，引擎对其仅执行 R0/R1 硬规则

**引擎降级**：
- baselines.json 缺失/损坏 → 仅执行 R0/R1 的 `isFullWindow` 分支（唯一不依赖基线的判据）
- thresholds.json 缺失/损坏 → 使用代码内置默认阈值（与默认模板同值）

### 5.4 平台兼容性

| 层面 | 策略 | 状态 |
|------|------|------|
| C# 后端 | .NET 4.0 / x86，纯内存计算（毫秒级），无第三方依赖 | ✅ 已确认 |
| IE8 前端 (D5) | ES5 only, VML 渲染, attachEvent, 无 CSS3 | ✅ 已实测 |
| WebBrowser 控件 | FEATURE_BROWSER_EMULATION=8888, ScriptErrorsSuppressed=true | ✅ 已验证 |
| console polyfill | `window.console = window.console \|\| {log:function(){}}` | ⚠️ 待加 |
| JSON 序列化 | JavaScriptSerializer (.NET 4.0 内置) | ✅ 已有先例 |

---

## 6. Data Model

### 6.1 CurveFeatures（特征）

```csharp
public class CurveFeatures
{
    public int SampleCount;        // 采样点数
    public bool IsFullWindow;      // n ≥ 780
    public bool IsValid;           // 曲线有效
    public int ActiveEnd;          // 有效终点下标
    public double DurationSec;     // 动作时长 (s)
    public double SpikePeak;       // 启动尖峰 (kW)
    public int SpikeIndex;         // 尖峰下标
    public double UnlockMean;      // 解锁段均值 (kW)
    public double ConvMean;        // 转换段均值 (kW)
    public double ConvMax;         // 转换段最大值 (kW)
    public double StepRatio;       // 台阶比
    public double TailMean;        // 缓放段均值 (kW)
}
```

### 6.2 SwitchBaseline（基线）

```csharp
public class SwitchBaseline
{
    public double RefDurationSec;
    public double RefSpikePeak;
    public double RefUnlockMean;
    public double RefConvMean;
    public double RefTailMean;
    public int SampleCount;        // 参与计算样本数
    public string DateFrom;        // "2025-12-13"
    public string DateTo;          // "2026-06-29"
}
```

### 6.3 DiagnosisResult（诊断结论）

```csharp
public static class DiagnosisLevel
{
    public const string Normal  = "正常";
    public const string Warning = "预警";
    public const string Alarm   = "报警";
    public const string Fault   = "故障";
    public static int Severity(string level);  // 0/1/2/3
}

public class DiagnosisResult
{
    public string RuleId;          // "R1"
    public string RuleName;        // "动作超时/未完成"
    public string Level;           // DiagnosisLevel.*
    public string Description;     // 中文结论（含数值）
    public double Value;           // 异常值
    public double Reference;       // 参考值
}
```

### 6.4 存储文件样例

**`.diag.json`**（与 `{date}.json` 并列）：

```json
[
  { "timestamp": 1770922311, "level": "正常", "results": [] },
  { "timestamp": 1769618597, "level": "故障",
    "results": [
      { "ruleId": "R1", "ruleName": "动作超时/未完成", "level": "故障",
        "description": "动作时长31.36s，超过参考11.72s+3.0s，疑似卡阻/空转未完成",
        "value": 31.36, "reference": 11.72 }
    ]
  }
]
```

**`alarms_index.json`**：

```json
{
  "1-1": { "2026-02-13": { "预警": 1, "报警": 0, "故障": 0 } },
  "4-1": { "2026-01-29": { "预警": 0, "报警": 0, "故障": 2 } }
}
```

---

## 7. Development Phases

### Phase 1（一期，D1-D5）— 自动诊断 pipeline + UI

```
D1: 特征提取器 + 项目脚手架         ← 可立即开始
D2: 基线构建工具                    ← 依赖 D1
D3: 规则引擎 + thresholds.json      ← 依赖 D1、D2
D4: 管道集成 + 结果存储 + diag.log  ← 依赖 D3
D5: UI 报警展示 + 阈值配置窗口       ← 依赖 D4
```

| Slice | 内容 | 核心交付物 | 验收标准 |
|-------|------|-----------|----------|
| D1 | FeatureExtractor + DiagTool selftest | SwitchMonitor.Diagnosis.dll | 4 个金标准夹具 × 12 特征全部 PASS |
| D2 | BaselineBuilder + DiagTool baseline | baselines.json (8 台道岔) | 与 CONTEXT.md §2.2 期望值逐项偏差 ≤ ±0.02 |
| D3 | DiagnosisEngine R0-R8 + DiagTool dryrun | 规则引擎 + thresholds.json | dryrun 触发数与演习基准偏差 ≤ ±10% |
| D4 | 管道集成 + .diag.json + diag.log | 自动诊断 pipeline | 全量导入后 alarms_index 与 dryrun 一致 |
| D5 | 诊断条 + 时间着色 + 角标 + 阈值配置窗口 | UI 报警展示 | IE8 模式无脚本错误，全部验收条件通过 |

### Phase 2（二期，D6）— 趋势 + 形态分析

```
D6: 特征趋势分析 + 参考曲线逐点对比
```

| 能力 | 说明 |
|------|------|
| T1 渐变劣化预警 | 对 convMean/durationSec 做 7 天滑动中位数，检测持续上升趋势 |
| P1 逐点形态对比 | 与参考曲线对齐后计算残差面积比，抓阈值法漏掉的形态异常 |
| 参考曲线叠加 | 图表页勾选"参考曲线"叠加显示虚线 |

---

## 8. Testing Strategy

### 8.1 四层金标准验证

| 层级 | 方法 | 验收基准 |
|------|------|----------|
| 特征提取 | 4 个夹具（正常 J / 正常 X / 超时 / 夭折）× 12 字段 | 与 Python 参考实现一致，容差 ±0.002 |
| 基线计算 | 8 台道岔 × 5 项基线值 | 与 CONTEXT.md §2.2 表值一致，容差 ±0.02 |
| 规则判定 | 4 个夹具的诊断结论 | 夹具 A/B → 空，夹具 C → [R1]，夹具 D → [R2] |
| 全量演习 | 23,999 事件的 dryrun | 触发数与演习基准偏差 ≤ ±10% |

### 8.2 交叉验证链

```
Python diag_reference_check.py (golden)
    │
    ├── golden 子命令 → 生成金标准夹具值
    ├── baseline 子命令 → 生成基线期望值
    └── dryrun 子命令 → 生成规则触发矩阵
    │
    ▼
C# FeatureExtractor / BaselineBuilder / DiagnosisEngine
    │
    └── DiagTool.exe selftest / baseline / dryrun → 必须与 Python 输出一致
```

### 8.3 IE8 兼容性测试

- WebBrowser 控件 + FEATURE_BROWSER_EMULATION=8888 模拟 XP 工控机实际运行环境
- 测试项：诊断条渲染、时间列表着色、场景切换、渲染压测（10 次快速切换）
- 验收：无 JS 错误弹窗（`ScriptErrorsSuppressed=true` 生产环境模式下）

---

## 9. Configuration

### 9.1 thresholds.json（诊断规则模板）

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

R0 不可配置（恒启用，恒"报警"）。级别字段允许现场调整（如把 R6 从报警降为预警）。

### 9.2 config.json 新增节

```json
{
  "diagnosis": {
    "enabled": true,
    "rulesDir": "Rules"
  }
}
```

---

## 10. Out of Scope (V1)

| 内容 | 原因 | 计划 |
|------|------|------|
| 三相电流曲线诊断规则 | 一期仅分析功率曲线 | V2 |
| 趋势分析 (T1) + 逐点对比 (P1) | 需先积累现场运行经验再定阈值 | D6 (二期) |
| DTW / 动态时间规整 | XP 单核算 790 点负担重，且正常曲线尖峰对齐度高，线性对齐已足够 | 不做 |
| 分方向基线 | 当前方向字段为占位值，且实测无双峰分布 | 方向确认后扩展 |
| shiqi 数据集 | 属于另一套监测软件，设备不同 | 暂不纳入 |
| 基线过期自动提醒 | 漂移缓慢（半年 ~3%），季度手工重算足够 | V2 评估 |
| 道岔间横向对比 | 先做单台纵向诊断 | V2 评估 |
| 诊断建议措施自动输出 | 需积累足够案例 | V2 |

---

## 11. Open Questions

1. **R7/R8 实际误报率**：当前无演习数据，默认阈值（1.3×/±30%）上线后需观察 1-2 周，按实际表现调 JSON。
2. **现场调参体验**：阈值 JSON 直接编辑 vs 配置窗口，哪种更受现场欢迎？D5 的配置窗口为 WinForms 原生控件，JSON 编辑则需外部编辑器。两者并行提供。
3. **基线的方向差异**：一期不分方向建基线。如后续发现定→反和反→定的特征存在显著差异，基线结构已预留 switchId+direction 扩展路径。
4. **参考曲线要不要历史版本管理**：目前设计是每次重算覆盖。如果现场需要对比"新基线 vs 旧基线"，可在 BaselineStore 中加 computedHistory 数组。

---

## Appendix A: 金标准测试夹具

| 夹具 | 文件 | Timestamp | 曲线类型 | 关键特征 |
|------|------|-----------|----------|----------|
| A | SwitchCurve(3).csv | 1770922311 | 正常 J (1-1) | dur=11.76, spike=3.392, conv=0.308 |
| B | SwitchCurve(7).csv | 1770771323 | 正常 X (1-X) | dur=8.56, spike=3.294, conv=0.254 |
| C | SwitchCurve(27).csv | 1769618597 | 超时卡阻 (4-1) | dur=31.36, isFullWindow, conv=0.545 |
| D | SwitchCurve(31).csv | 1773938685 | 动作夭折 (4-X) | dur=0.84, n=27, tail=0 |

## Appendix B: 各道岔基线期望值

| 组 | 正常样本 | 时长 (s) | 峰值 (kW) | 解锁 (kW) | 转换 (kW) | 缓放 (kW) |
|----|---------|----------|----------|----------|----------|----------|
| 1-1 (J) | 2962 | 11.72 | 3.235 | 0.307 | 0.300 | 0.213 |
| 1-X (X) | 2985 | 8.64 | 3.333 | 0.311 | 0.259 | 0.207 |
| 2-1 (J) | 2967 | 11.76 | 3.216 | 0.283 | 0.266 | 0.214 |
| 2-X (X) | 2976 | 8.72 | 3.235 | 0.255 | 0.239 | 0.212 |
| 3-1 (J) | 2977 | 11.96 | 3.353 | 0.366 | 0.329 | 0.208 |
| 3-X (X) | 2989 | 8.68 | 3.255 | 0.285 | 0.250 | 0.209 |
| 4-1 (J) | 2938 | 11.72 | 3.294 | 0.298 | 0.267 | 0.215 |
| 4-X (X) | 2938 | 8.92 | 3.392 | 0.299 | 0.266 | 0.205 |
