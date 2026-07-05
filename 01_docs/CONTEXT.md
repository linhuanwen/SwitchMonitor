# 道岔监测系统 领域术语表

## 领域概念

### 道岔 (Switch / Switch Machine)
铁路线路的关键设备，用于实现列车从一股道转入另一股道。每组道岔由转辙机驱动。

### 定位 (Normal Position) / 反位 (Reverse Position)
道岔的两个固定位置。**定位**为道岔的常用位置（直向通过），**反位**为扳动后的位置（侧向通过）。H = 定位/反位之一，B = 另一个，具体对应关系需现场确认。

### 道岔动作 (Switch Action / Switch Throw)
转辙机驱动道岔从一个位置转换到另一个位置的过程，持续时间通常 5-8 秒。

### 道岔动作曲线 (Switch Action Curve)
一次道岔动作期间记录的电流/电压/功率随时间变化的波形。曲线分为三个阶段：**解锁 → 转换 → 锁闭**。采样率通常为 25 Hz。

### 表示电压 (Indication Voltage)
道岔静止时，表示继电器线圈两端的直流电压。用于确认道岔当前位置（定位或反位）。正常范围通常在 DC 24V ± 10% 左右。

### 参考曲线 (Reference Curve)
某道岔在确认正常状态下记录的"标准"动作曲线，作为后续对比的基准。每条参考曲线有**设定时间**标签。

### 开关量 (Digital / Binary Status)
道岔表示继电器等设备的通/断状态（如 0x2f = 吸起，0x00 = 落下）。通过 point_id 区分不同采集点。

### CSM2010
铁路信号集中监测系统的数据格式版本标识。`.dat` 文件以 `CSM2010\x00` 魔数开头，内部按数据块组织，每个数据块对应一次道岔动作。

### 站机 (Station Computer)
部署在各个车站的工控机，运行 CSM 监测软件，负责本地数据采集、处理和显示。

### 三水北 (SSB - Sanshuibei)
本项目的目标车站，位于广佛肇城际铁路线。

## 文件格式

### SwitchCurve(*).dat
CSM2010 格式的道岔动作曲线文件。包含魔数头、索引记录（24 字节）和数据块。每个数据块对应一次道岔动作，含时间戳、标志、采样数和采样值（i16 或 f32 数组）。

### Digit(*).dat
开关量事件文件。每条记录包含时间戳、点数、状态字节序列。以 point_id 区分不同采集点。

### DCBSDYAnalog(*).dat
模拟量采样文件。每条记录含时间戳、通道、测量类型和 float32 值。用于记录表示电压等连续模拟量。

## 缩写对照

| 缩写 | 全称 |
|---|---|
| CSM | Centralized Signal Monitoring（信号集中监测系统） |
| SSB | Sanshuibei（三水北站） |
| TCC | Train Control Center（列控中心） |
| CBI | Computer-Based Interlocking（计算机联锁） |
| CTC | Centralized Traffic Control（调度集中） |
| DYP | Power Supply Panel（电源屏） |
| H/B | 定位/反位表示继电器代号后缀，H 与 B 互为一对 |
| J/X | 道岔尖轨定位/反位表示 |

## 项目术语

### SwitchAction
数据库中的一次道岔动作记录，包含车站、道岔编号、起止时间、方向、关联曲线数据。

### CurveSample
一次道岔动作中单个采样点的数据（时间戳、相别、电流、电压、功率、功率因数）。

### DiagnosisEngine
独立于主程序的诊断模块，实现 `IDiagnosisEngine` 接口，输入动作数据，输出诊断结论。

### DiagnosisResult
诊断模块的输出：规则名称、级别（正常/预警/报警/故障）、可读结论、异常值、参考值。
