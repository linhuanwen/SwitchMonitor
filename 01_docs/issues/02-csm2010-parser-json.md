# Slice 2: CSM2010 二进制解析 → JSON 管道

> **状态（2026-07-08 代码审核）**: ✅ 已实现。缺口：坏行静默跳过、未记警告日志；采样值未强制 round 3 位小数（依赖 CSV 源精度）。另有未提交修正：`HEADER_SIZE` 42→14（parsed_data 已全量重新生成）。

## Type

AFK

## Blocked by

Slice 1（项目脚手架 + 数据模型）

## What to build

解析 `SwitchCurve(*).dat` 二进制文件（CSM2010 格式），将每次道岔动作的 A/B/C 相电流和功率曲线写入 JSON 中间文件。解析结果按转辙机 ID + 日期分组存储。

### CSM2010 二进制格式

参照已逆向的格式：
- 8 字节 magic header "CSM2010"
- 之后是连续的 event 记录块
- 每条记录含 timestamp、phase bitmask（16777216=A相, 33554432=B相, 50332416=C相, 0=功率）、约 790 个 float 采样点

### 解析流程

1. 确定文件对应的转辙机 ID：通过 `config.json` 中 `switchGroups[].dataFileIndex` 映射
2. 读取 .dat → 解析所有 event 记录
3. 按 **timestamp 分组**：同一 timestamp 的不同 phase 记录属于同一次动作
4. 对每组构造 `SwitchEvent`：
   - 从 csv 中提取 A/B/C 相电流值和功率值
   - 计算 Duration = SampleCount × SampleInterval
   - 推断 Direction（暂标"定位↔反位"）
5. 按 **日期** 分组：同一天的所有 `SwitchEvent` 写入一个 `YYYY-MM-DD.json`
6. 写入 `parsed_data/{switchId}/YYYY-MM-DD.json`
7. 更新 `parsed_data/index.json`：`{"1-1": {"2026-06-29": [ts1, ts2, ...]}}`

### 中间 JSON 结构

**index.json:**
```json
{
  "1-1": {
    "2026-06-29": ["1776243701", "1776286259"],
    "2026-06-28": ["1776157301"]
  }
}
```

**日数据文件 (YYYY-MM-DD.json):**
```json
[
  {
    "timestamp": 1776243701,
    "datetime": "2026-04-15 17:01:41",
    "direction": "定位↔反位",
    "duration": 11.80,
    "sampleInterval": 0.04,
    "sampleCount": 790,
    "currentA": [5.647, 1.451, ...],
    "currentB": [5.529, 1.451, ...],
    "currentC": [2.078, 1.490, ...],
    "power": [3.020, 0.294, ...]
  }
]
```

### 数据来源

本项目使用已有 **CSV 导出文件** 作为数据输入（CSM2010 的原生二进制 .dat 暂不直读，原因见 Further notes）：
- 电流文件：`03_raw_data/sanshuibei/SwitchCurve(0).csv` 等（1000 events × 3 相）
- 功率文件：`03_raw_data/sanshuibei/SwitchCurve(3).csv` 等（3000 events × 1 相）
- CSV 格式：`timestamp,datetime,phase,s0,s1,...,s789`

### 文件配对

参照 `_file_type_summary.csv`：
- SwitchCurve(0) ↔ SwitchCurve(3)
- SwitchCurve(4) ↔ SwitchCurve(7)
- SwitchCurve(8) ↔ SwitchCurve(11)
- SwitchCurve(12) ↔ SwitchCurve(15)
- SwitchCurve(16) ↔ SwitchCurve(19)
- SwitchCurve(20) ↔ SwitchCurve(23)
- SwitchCurve(24) ↔ SwitchCurve(27)
- SwitchCurve(28) ↔ SwitchCurve(31)

每对 = 一个转辙机的完整数据（电流 + 功率）。

## Acceptance criteria

- [ ] 能正确解析 sanshuibei 目录下全部 16 个 CSV 文件（8 对 × 2）
- [ ] 电流文件和功率文件按 timestamp 正确配对
- [ ] 每 event 有 3 相电流（A/B/C）+ 1 相功率
- [ ] `parsed_data/index.json` 中所有转辙机的时间戳列表正确且降序
- [ ] 日数据 JSON 中采样值精度保留 3 位小数
- [ ] 损坏/格式异常的行不导致崩溃，记录警告日志跳过
- [ ] 对已有数据跑一次全量解析 < 30 秒完成

## Further notes

- CSV 作为第一步输入，因为已有素材并已验证格式
- 后续如果接入实时 .dat 文件，再实现原生 CSM2010 二进制读取（DatParser.cs）
- JSON 使用 `Newtonsoft.Json` 序列化，`Formatting.Indented` 方便人工排查
- 采样值数组长度约 790，JSON 文件单日预估 500KB-2MB，磁盘 I/O 可控
