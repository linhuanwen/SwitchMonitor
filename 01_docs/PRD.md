# 道岔监测系统 PRD

> **版本**: V2.0
> **日期**: 2026-07-04
> **目标平台**: 研华 610H 工控机, Windows XP, .NET Framework 4.0

---

## Problem Statement

现有铁路信号集中监测系统（CSM）站机软件——由 `CsmthStation.exe`、`CsmthStationMonitor.exe`、`CsmthReplay.exe` 等组成——是一套封闭的商业软件，运行在 Windows XP 工控机上。虽然该软件能够正常采集和展示道岔动作电流曲线、开关量状态等数据，但它**不允许用户扩展功能**。

现场运维人员面临以下具体困境：

1. **无法自定义异常诊断规则**：软件虽有内置报警，但规则固定，无法根据实际运维经验调整阈值或增加新的故障判断逻辑。例如，技术人员知道某种功率曲线形态对应"滑床板缺油"，但无法将此知识编码为自动诊断规则。

2. **无法进行深入数据分析**：软件只提供单次曲线查看，不支持同一道岔不同时期的动作曲线叠加对比、转换时间趋势分析、道岔间横向对比等进阶分析功能。

3. **缺少参考曲线管理**：无法为每个道岔设定标准参考曲线并标注设定时间，导致判断"正常与否"完全依赖人工记忆。

4. **软件未开放接口**：虽有 ZeroMQ、Kafka 等库文件存在于安装目录，但实际未配置启用；TCP 通信协议（协议 418）为私有格式，无公开文档。

**结论**：需要从零构建一套道岔监测替代软件，在保留原有曲线展示功能的基础上，增加可配置的异常诊断引擎。

---

## Solution

构建一套 C# .NET WinForms 桌面应用程序，部署到同一台 XP 工控机上，与原有 CSM 软件并存运行。

### 核心模块

1. **数据采集层**：通过文件系统扫描 CSM 软件生成的 `.dat` 二进制文件（`SwitchCurve(*).dat` 和 `Digit(*).dat`），解析后存入自有 SQLite 数据库。不修改或拦截原有软件的任何进程或通信。

2. **曲线展示模块**：替代原有监控界面的道岔曲线查看功能，提供道岔选择、时间筛选、多曲线叠加对比、参考曲线管理、缩放/拖拽/局部放大、数据导出。

3. **异常诊断引擎**：独立于主程序的可替换 DLL，通过 `IDiagnosisEngine` 接口接收道岔动作数据，输出结构化诊断结论。诊断规则存储在 JSON 配置文件中，可在站机上直接编辑阈值而不需重新编译。

### 关键设计原则

- **零侵入**：不修改、不拦截、不依赖原有 CSM 软件的任何内部机制
- **xcopy 部署**：整个程序文件夹拷贝到站机即可运行，不需要安装程序、注册表或系统服务
- **可替换规则**：诊断规则与主程序解耦，更新规则只需替换 JSON 文件或诊断 DLL
- **配置驱动**：道岔名称、编号映射、文件路径等均通过配置文件管理，适配不同车站

---

## User Stories

### 数据采集与存储

1. As a 系统, I want 定时扫描指定目录下的 `SwitchCurve(*).dat` 文件, so that 新产生的道岔动作曲线数据能被及时发现并处理。

2. As a 系统, I want 定时扫描指定目录下的 `Digit(*).dat` 文件, so that 新的开关量状态变化事件能被及时发现并关联到道岔动作。

3. As a 系统, I want 解析 CSM2010 格式的道岔曲线二进制数据, so that 每次动作的三相电流、电压、功率采样序列能被提取为结构化数据。

4. As a 系统, I want 解析 Digit 格式的开关量二进制数据, so that 每个采集点的状态变化（时间戳 + 点号 + 状态值）能被提取为结构化数据。

5. As a 系统, I want 将解析后的动作事件、曲线采样数据、开关量事件分别存入 SQLite 数据库, so that 后续查询和分析有统一的数据来源。

6. As a 系统, I want 对新文件进行增量处理（已处理过的文件不重复解析）, so that 避免重复写入和性能浪费。

7. As a 系统, I want 在数据采集出现异常（文件损坏、格式不匹配）时记录错误日志, so that 运维人员可以排查数据问题。

### 曲线展示

8. As a 运维人员, I want 从下拉列表中选择要查看的道岔编号, so that 我只看到关心的那组道岔的数据。

9. As a 运维人员, I want 通过选择起止时间来筛选历史动作记录, so that 我可以快速定位到某天或某时段内的道岔动作。

10. As a 运维人员, I want 在主图表区域看到选定道岔动作的三相电流曲线（A/B/C 三相以不同颜色叠加显示）, so that 我能直观判断三相是否平衡。

11. As a 运维人员, I want 切换查看同一动作的功率曲线和电压曲线, so that 我可以从不同维度判断道岔状态。

12. As a 运维人员, I want 用鼠标滚轮缩放曲线、拖拽平移曲线、框选局部区域放大, so that 我可以仔细查看曲线的细节特征。

13. As a 运维人员, I want 将当前显示的曲线导出为 PNG 图片文件, so that 我可以把曲线截图放入分析报告或发给其他人。

14. As a 运维人员, I want 将当前曲线的原始采样数据导出为 CSV 文件, so that 我可以用 Excel 或其他工具做进一步分析。

### 参考曲线管理

15. As a 运维人员, I want 从历史动作中选择一次动作"设为参考曲线", so that 后续动作可以与该参考曲线进行视觉对比。

16. As a 运维人员, I want 在参考曲线上看到"设定时间"的标注, so that 我知道这条参考曲线是什么时候录制的、是否需要更新。

17. As a 运维人员, I want 在同一图表中同时看到当前动作曲线和参考曲线（以虚线或半透明叠加）, so that 我能快速发现当前动作与基准的偏差。

### 异常诊断

18. As a 运维人员, I want 程序在每次道岔动作后自动运行诊断引擎, so that 异常情况能在第一时间被发现而不是等到人工查看时才注意到。

19. As a 运维人员, I want 诊断结果以"正常/预警/报警/故障"四个级别呈现, so that 我能根据严重程度安排处理优先级。

20. As a 系统管理员, I want 通过编辑 JSON 配置文件来调整诊断阈值（如转换时间上限、电流峰值范围）, so that 不同道岔可以使用不同的判断标准。

21. As a 系统管理员, I want 更新诊断规则时只需替换 `DiagnosisEngine.dll` 或 `Rules/` 目录下的配置文件, so that 主程序不需要重新编译部署。

22. As a 运维人员, I want 诊断结论以清晰的中文描述呈现（如"3#道岔解锁段电流偏高，疑似密贴过紧"）, so that 即使不熟悉数据分析的人也能理解问题所在。

### 系统管理

23. As a 系统管理员, I want 通过一个配置文件指定数据源目录、数据库路径、日志路径, so that 部署到不同车站时只需修改配置而不需改代码。

24. As a 系统管理员, I want 道岔编号与实际设备的映射关系存储在可编辑的配置中, so that 当现场核对清楚点号对应关系后可以直接更新。

---

## Implementation Decisions

### 技术栈

| 层面 | 选型 | 理由 |
|---|---|---|
| 语言 | C# | 原软件为 .NET 体系，已验证在 XP 上稳定运行 |
| 框架 | .NET Framework 4.0 | XP 兼容的上限版本 |
| 界面 | WinForms | 原生 Windows 控件，XP 无兼容问题 |
| 数据存储 | SQLite (System.Data.SQLite) | 免安装、单文件、资源占用低 |
| 曲线绘制 | GDI+ 自绘或 LiveCharts 1.x | 不依赖新版 .NET 库 |
| 配置格式 | JSON (Newtonsoft.Json) | 原软件已使用，文件已存在于安装目录 |

### 解决方案结构（5 个项目）

```
SwitchMonitor.sln
│
├── SwitchMonitor.UI              # WinForms 主程序 (exe)
│   └── 职责：主窗口、曲线渲染、道岔选择、时间筛选、导出
│
├── SwitchMonitor.Data            # 数据访问层
│   └── 职责：.dat 文件扫描、二进制解析、SQLite 读写、查询服务
│
├── SwitchMonitor.Common          # 公共模型（无外部依赖）
│   └── 职责：SwitchActionData、SamplePoint、DiagnosisResult 等 POCO
│
├── SwitchMonitor.Diagnosis       # 诊断接口定义
│   └── 职责：IDiagnosisEngine 接口、DiagnosisResult 类型
│
└── DiagnosisEngine               # 诊断引擎实现（独立 DLL）
    └── 职责：规则判断逻辑、读 JSON 阈值配置、参考曲线比较
```

依赖方向：
```
UI → Data → Common
UI → Diagnosis → Common
DiagnosisEngine → Diagnosis → Common
```

`DiagnosisEngine` 不引用 UI 或 Data，可以单独编译和替换。

### 数据库 Schema

```sql
-- 道岔动作事件表
CREATE TABLE SwitchActions (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FileSource      TEXT NOT NULL,           -- 来源文件名
    SwitchId        TEXT NOT NULL,           -- 道岔标识（如 "SW_01"，占位符，后续可替换为实际编号）
    StartTime       INTEGER NOT NULL,         -- 动作开始 (Unix timestamp)
    EndTime         INTEGER,                  -- 动作结束 (Unix timestamp)
    Direction       TEXT,                     -- "定位→反位" / "反位→定位" / "未知"
    PhaseCount      INTEGER,                  -- 相数 (1 或 3)
    SampleCount     INTEGER,                  -- 每相采样点数
    SampleRate      INTEGER DEFAULT 25,       -- 采样率 Hz
    CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
);

CREATE INDEX idx_actions_switch_time ON SwitchActions(SwitchId, StartTime);

-- 曲线采样数据表
CREATE TABLE CurveSamples (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ActionId        INTEGER NOT NULL REFERENCES SwitchActions(Id),
    SampleIndex     INTEGER NOT NULL,         -- 采样序号 (0, 1, 2, ...)
    Timestamp       INTEGER NOT NULL,          -- 该采样点的 Unix timestamp
    Phase           TEXT NOT NULL,             -- "A" / "B" / "C" / "P"（功率）
    Current         REAL,                      -- 电流 (A)
    Voltage         REAL,                      -- 电压 (V)
    Power           REAL,                      -- 功率 (W 或 kW)
    RawValue        REAL,                      -- 原始采样值（当暂时无法区分电流/电压/功率时使用）
    UNIQUE(ActionId, SampleIndex, Phase)
);

CREATE INDEX idx_samples_action ON CurveSamples(ActionId);

-- 开关量状态事件表
CREATE TABLE StatusEvents (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FileSource      TEXT NOT NULL,
    Timestamp       INTEGER NOT NULL,
    PointId         INTEGER NOT NULL,         -- 采集点号
    StateByte       INTEGER,                   -- 状态码 (0x2f, 0x00, ...)
    RawValue        INTEGER,                   -- 原始 16-bit 值
    SwitchId        TEXT,                      -- 关联道岔标识（如果能匹配到）
    CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
);

CREATE INDEX idx_status_time ON StatusEvents(Timestamp);
CREATE INDEX idx_status_point ON StatusEvents(PointId, Timestamp);

-- 参考曲线表
CREATE TABLE ReferenceCurves (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    SwitchId        TEXT NOT NULL,
    ActionId        INTEGER REFERENCES SwitchActions(Id),  -- 参考曲线来源的动作
    SetTime         TEXT NOT NULL,                          -- 设定时间
    Description     TEXT,                                   -- 备注
    IsActive        INTEGER DEFAULT 1                       -- 是否当前使用中
);

-- 诊断结果表（可选，用于存储历史诊断记录）
CREATE TABLE DiagnosisLog (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ActionId        INTEGER REFERENCES SwitchActions(Id),
    RuleName        TEXT NOT NULL,
    Level           TEXT NOT NULL,           -- "正常" / "预警" / "报警" / "故障"
    Description     TEXT NOT NULL,
    AbnormalValue   REAL,
    ReferenceValue  REAL,
    CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
);
```

### 核心接口

```csharp
// 单次采样点
public class SamplePoint
{
    public int Index { get; set; }
    public long Timestamp { get; set; }
    public string Phase { get; set; }        // "A" / "B" / "C" / "P"
    public float Current { get; set; }       // A
    public float Voltage { get; set; }       // V
    public float Power { get; set; }         // W
}

// 道岔动作数据包（诊断引擎的输入）
public class SwitchActionData
{
    public string StationName { get; set; }
    public string SwitchId { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public string Direction { get; set; }
    public int SampleRate { get; set; }
    public List<SamplePoint> Samples { get; set; }
    public float? VoltageBefore { get; set; }   // V2
    public float? VoltageAfter { get; set; }    // V2
}

// 诊断结论（诊断引擎的输出）
public class DiagnosisResult
{
    public string RuleName { get; set; }
    public string Level { get; set; }           // "正常" / "预警" / "报警" / "故障"
    public string Description { get; set; }
    public float? AbnormalValue { get; set; }
    public float? ReferenceValue { get; set; }
}

// 诊断引擎接口
public interface IDiagnosisEngine
{
    void Initialize(string rulesPath);
    List<DiagnosisResult> Diagnose(SwitchActionData data);
}
```

### 数据采集流程

```
[后台定时器 tick 每 30 秒]
    │
    ├── 扫描数据源目录
    │   ├── 发现新的 SwitchCurve(*).dat → 排入解析队列
    │   └── 发现新的 Digit(*).dat → 排入解析队列
    │
    ├── 对每个新文件调用解析器
    │   ├── SwitchCurveParser.Parse(byte[]) → List<SwitchActionData>
    │   └── DigitParser.Parse(byte[]) → List<StatusEvent>
    │
    ├── 入库
    │   ├── INSERT SwitchActions + CurveSamples
    │   └── INSERT StatusEvents
    │
    └── 触发诊断
        └── 对每个新 SwitchAction → DiagnosisEngine.Diagnose() → 展示结果
```

### CSM2010 二进制解析规格

来自已验证的 Python 解析器 `parse_csm2010.py`，翻译为 C#：

- 数据区起点: 偏移 100032 字节
- 每个数据块: 4014 字节
- 块头: 42 字节（4 字节 timestamp LE uint32 + 4 字节 flags + 2 字节 sample_rate + 2 字节 sample_count + …）
- 采样值: 42 字节头之后，每 4 字节一个 float32 LE
- 验证条件: timestamp 在合理范围内（1,500,000,000 ~ 2,000,000,000）、sample_rate == 25、10 < sample_count < 2000
- flags 用于区分相别（如 16777216 = 0x01000000 = phase 1，33554432 = 0x02000000 = phase 2）

### Digit 二进制解析规格

来自已验证的 Python 解析器 `parse_local_receive.py`：

- 文件头含 ts_start (offset +12) 和 ts_end (offset +16)
- 从约 offset 1000 起扫描，按 timestamp 窗口匹配第一条记录
- 每条记录: 7 + 2 × record_type 字节
  - 4 字节 timestamp LE uint32
  - 2 字节 record_type BE uint16
  - record_type × 2 字节点数据 BE uint16
  - 高字节 = state_byte, 低字节 = point_id

### 道岔映射配置

```json
{
  "switchMapping": {
    "sourceFiles": {
      "0": { "switchId": "SW_01", "description": "待确认-可能为1#J" },
      "4": { "switchId": "SW_02", "description": "待确认-可能为1#X" },
      "8": { "switchId": "SW_03", "description": "待确认-可能为2#J" }
    },
    "pointIdMapping": {
      "184": { "configName": "537GH", "switchId": null, "meaning": "待确认" },
      "185": { "configName": "537GB", "switchId": null, "meaning": "待确认" }
    }
  }
}
```

所有映射值均为占位符，当现场核对出实际对应关系后，直接在 JSON 中修改。

### 诊断规则配置（示例）

```json
{
  "rules": [
    {
      "name": "转换时间异常",
      "enabled": true,
      "level": "报警",
      "type": "threshold",
      "parameters": {
        "metric": "conversionTime",
        "referenceSeconds": 5.8,
        "warningDeviation": 0.5,
        "alarmDeviation": 1.5
      },
      "description": "转换时间与参考值偏差超过{deviation}秒"
    },
    {
      "name": "解锁段电流偏高",
      "enabled": true,
      "level": "预警",
      "type": "segmentAnalysis",
      "parameters": {
        "segment": "unlock",
        "metric": "peakCurrent",
        "upperRatioThreshold": 1.3
      },
      "description": "解锁段峰值电流超过参考曲线的{ratio}倍，疑似密贴过紧"
    }
  ]
}
```

---

## Testing Decisions

### 测试原则

- 仅测试外部可观察行为，不测试实现细节
- 每个接缝处测试输入→输出的数据正确性
- 使用已知的二进制样本文件作为测试夹具（golden files）

### 四条测试接缝

| 接缝 | 测试对象 | 输入 | 预期输出 | 测试框架 |
|---|---|---|---|---|
| Seam 1 | `SwitchCurveParser` | 已知的 SwitchCurve(0).dat 二进制片段 | `List<SwitchActionData>` 含正确的 sample_count, timestamp, 采样值 | NUnit |
| Seam 2 | `DataRepository` | 构造的 SwitchActionData | SQLite 入库后读回，数据一致 | NUnit + SQLite in-memory |
| Seam 3 | `QueryService` | 道岔ID + 时间范围 | 返回正确的 SwitchActionData 列表，按时间排序 | NUnit + SQLite in-memory |
| Seam 4 | `IDiagnosisEngine` | 构造的 SwitchActionData（正常/异常曲线） | 返回正确的 DiagnosisResult 级别和规则名 | NUnit |

### 已有验证基准

- `parse_csm2010.py` 已验证可正确解析 sanshuibei 目录下全部 SwitchCurve 文件
- `parse_local_receive.py` 解析结果与现场软件测试输出 CSV 一致（digit_test3.csv 91 行 vs 92 行仅差 1 条边界记录）
- 这些 Python 解析器作为 C# 实现的交叉验证参照

---

## Out of Scope (V1 不做)

| 内容 | 原因 | 计划 |
|---|---|---|
| 表示电压（DCBSDYAnalog）解析和展示 | 通道映射待确认 | V2 |
| 历史趋势分析和统计报表 | 先完成单次诊断 | V2 |
| 诊断建议措施输出 | 需积累足够诊断案例 | V2 |
| shiqi/ 文件夹数据 | 属于另一套监测软件 | 暂不纳入 |
| TCP 实时数据采集（协议 418） | 需逆向私有协议 | 后续评估 |
| 多车站切换 | 当前目标站仅为三水北 | 后续按需 |
| 用户登录/权限管理 | 单机工控机已有物理访问控制 | 不做 |
| i18n 多语言 | 仅中文 | 不做 |

---

## Further Notes

### 部署清单

站机部署时，需拷贝以下文件到工控机（如 `E:\道岔监测\`）：

```
SwitchMonitor.exe              # 主程序
SwitchMonitor.Data.dll         # 数据层
SwitchMonitor.Common.dll       # 公共模型
SwitchMonitor.Diagnosis.dll    # 诊断接口
DiagnosisEngine.dll            # 诊断引擎
System.Data.SQLite.dll         # SQLite ADO.NET 驱动
Newtonsoft.Json.dll            # JSON 处理
Rules\                         # 规则配置目录
  thresholds.json
  reference_curves\
Data\                          # 数据库文件（首次运行自动创建）
  switch_data.db
app.config                     # 配置文件
NLog.dll / nlog.config         # 日志
```

### 配置文件 (app.config 关键项)

```xml
<appSettings>
    <add key="DataSourcePath" value="E:\站改\DataFile\" />
    <add key="DatabasePath" value="Data\switch_data.db" />
    <add key="RulesPath" value="Rules\" />
    <add key="ScanIntervalSeconds" value="30" />
    <add key="StationId" value="SSB" />
    <add key="StationName" value="三水北" />
</appSettings>
```

### 与原有软件的共存

- 不修改原有软件的任何文件、配置或注册表
- 仅读取 CSM 软件写入的 .dat 文件（只读模式）
- 如果 CSM 软件未运行（没有新 .dat 文件产生），程序仍可正常浏览历史数据
- 程序异常挂掉不会影响 CSM 软件的数据采集

### 开发数据

- 测试用 SwitchCurve 数据: `03_raw_data/sanshuibei/SwitchCurve(*).dat`（16 个文件，8 组道岔）
- 测试用 Digit 数据: `03_raw_data/本地接收目录扳动/Digit(*).dat`（84 个文件）
- 车站配置: `03_raw_data/Station_SSB/`（三水北站）
- 已解析的参照 CSV 及配置报告位于旧 `shuju/` 目录，未迁移
