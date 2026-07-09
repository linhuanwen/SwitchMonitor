# Slice D6 (二期): 趋势分析 + 参考曲线逐点对比

> **归属模块**: [曲线报警分析 (Diagnosis)](../PRD.md)
> **依赖链**: D1-D4 → **D6**（可与 D5 并行）
> **前置阅读**: [PRD §2.4/§7](../PRD.md) — L3 诊断体系与 Phase 2 规划；[CONTEXT.md §2.5](../CONTEXT.md) — 特征漂移数据
> **阶段**: 🔵 二期内容，一期验收不含

## Type

AFK

## Blocked by

D4（诊断结果存储）。可与 D5 并行。

## Why Phase 2

一期的 R0-R8 规则覆盖了"单次动作明显异常"的场景（超时/夭折/特征超标），但有两类问题
阈值法抓不到：

1. **缓慢劣化**：滑床板缺油是以**周为单位**缓慢发展的——每次动作的 convMean 增加 1-2%，
   永远越不过 1.3× 的固定阈值，但累积 30 天后就已明显偏离正常状态。
2. **形态异常**：某些曲线整体形态改变（如转换段出现锯齿），但均值没有超阈值。

D6 填补这两个盲区。**建议在积累 1-2 周现场运行经验后再开工**，用一期规则的实际误报率
来标定 T1/P1 的默认阈值。

## What to build

两个进阶分析能力：(A) 特征时间序列的渐变劣化预警——抓阈值法抓不到的缓慢恶化；
(B) 与逐点参考曲线的残差对比——抓形态异常。

### A. 特征趋势分析 (T1)

**存储** `parsed_data/{switchId}/features.json`（D4 诊断时顺手追加，或本 slice 补一个
回填工具）：每事件一行的紧凑数组格式，控制体积：

```json
{ "columns": ["timestamp", "durationSec", "spikePeak", "unlockMean", "convMean", "tailMean"],
  "rows": [ [1770922311, 11.76, 3.392, 0.309, 0.308, 0.208] ] }
```

**趋势规则 T1（渐变劣化）**：对 convMean / durationSec 两个指标，
按天聚合中位数后做 7 天滑动中位数序列，若
`最近值 > 基线值 × (1 + trendRatio)` 且最近 `trendDays` 天单调不减 → 预警
"转换段功率呈持续上升趋势，建议检查滑床板润滑"。默认 `trendRatio=0.15`、`trendDays=7`，
进 thresholds.json 新增 `"T1"` 节。触发结果写入当天最后一个事件的 diag 结果或
独立的 `parsed_data/trends.json`（供 UI 汇总页读取，实现者定，倾向后者）。

### B. 逐点参考曲线对比 (P1)

**参考曲线生成**（扩展 D2 的 baseline 子命令）：正常样本按 `spikeIndex` 对齐后逐点取中位数，
截断/补齐到中位长度，存 `Rules/reference_curves/{switchId}.json`：

```json
{ "switchId": "1-1", "sampleInterval": 0.04, "alignIndex": 6,
  "values": [0.0, 0.0, "...每点3位小数"], "computedAt": "..." }
```

**对比规则 P1**：当前曲线与参考曲线按 spikeIndex 对齐后，
在重叠区间计算 `maxAbsDev`（逐点绝对差最大值，排除尖峰前区间）与
`areaDiffRatio`（|差|面积 / 参考面积）。
`areaDiffRatio > 0.25` 或 `maxAbsDev > refConvMean × 1.0` → 预警"曲线形态偏离参考"。
阈值进 thresholds.json `"P1"` 节。

**UI 叠加**：图表页复选框"参考曲线"，勾选后当前功率图叠加参考曲线
（虚线、半透明、图例"参考"）——对应 PRD user story 17，颜色用 config 的 `refPower`。

## Acceptance criteria

- [ ] features.json 回填工具跑完全量数据，8 台 × 全部事件的特征行数正确
- [ ] 人工构造连续 10 天 convMean 递增 20% 的合成数据，T1 触发；平稳数据不触发
- [ ] 8 台道岔的参考曲线生成成功，1-1 参考曲线的转换段均值 ≈ refConvMean（±0.02）
- [ ] 夹具 C（超时曲线）的 P1 必触发；夹具 A（正常）不触发
- [ ] P1 在全量正常数据上的误报率 < 1%（dryrun 扩展统计）
- [ ] UI 勾选参考曲线后叠加显示正确、IE8 下渲染流畅（790 点 ×2 系列，注意 marker 关闭）

## Further notes

- 本 slice 开工前建议先积累 1-2 周现场运行经验（一期规则的实际误报率），再定 T1/P1 默认阈值
- features.json 用列式紧凑格式是刻意的：8 台 × 3000 事件 × 6 列，普通 JSON 对象数组会膨胀数倍
- 逐点对比不用 DTW：实测尖峰位置固定（0.24-0.32s），线性对齐已足够；DTW 在 XP 单核上
  对 790 点 × 每天几十次动作也偏重
- **趋势分析的核心价值**：滑床板缺油以周为单位缓慢发展，单次动作永远不会越过 1.3× 阈值，
  只有趋势能提前发现——这是本模块超越原 CSM 软件固定报警的核心卖点，务必做
- **与一期诊断的关系**：T1/P1 触发结果同样写入 .diag.json 和 alarms_index，UI 无需改代码即可展示。
  T1 建议以独立 ruleId 出现在诊断结果中（如 `"ruleId": "T1"`），与 R 系列区分
