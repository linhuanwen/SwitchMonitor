---
id: KB00X
title: "论文标题"
authors: ["作者1", "作者2"]
year: 20XX
source: "期刊/会议名称"
doi: ""
equipment: "S700K / ZYJ7 / ZD6 / ZDJ9 / ..."
signalType: "电流曲线 / 功率曲线 / 振动信号 / 声音信号 / ..."
sampleRate: 25
dataSource: "现场实测 / 实验台模拟 / 仿真生成 / ..."
status: "draft"    # draft | reviewed | implemented
reviewedBy: ""
reviewedDate: ""

knowledgePoints:
  - id: KP1
    category: "preprocessing | feature | rule | observation | insight | threshold"
    description: "一句话概括这个知识点"
    method: "具体方法描述（公式、步骤、参数）"
    transferability: "high | medium | low"
    transferabilityReason: "为什么能/不能迁移到我们的ZYJ7系统"
    thresholdIfAny: "论文中的阈值或参数（如有）"
    implementedAs: null   # 晋升到M2后的规则ID，如 "R9"

  - id: KP2
    category: "feature"
    description: ""
    method: ""
    transferability: "medium"
    transferabilityReason: ""
    thresholdIfAny: ""
    implementedAs: null
---

# {论文标题} — 知识提取

## 1. 论文概要
<!-- 一句话：研究目标、数据来源、设备类型、主要方法 -->



## 2. 研究方法详述
<!-- 论文采用的具体方法——信号处理、特征工程、分类/检测模型 -->
<!-- 尽可能用公式、步骤编号形式表述，便于后续规则化 -->



## 3. 关键发现与数据
<!-- 论文报告的统计数字、阈值、性能指标（准确率/召回率/F1等） -->
<!-- 这是后续设定M3候选规则阈值的重要参考 -->



## 4. 与我们系统的对照分析

| 维度 | 论文 | 我们的系统 | 可迁移性 |
|------|------|-----------|----------|
| 设备类型 | | ZYJ7 电液转辙机 | |
| 信号类型 | | 功率曲线 (25Hz) | |
| 采样率 | | 25 Hz | |
| 样本量 | | 23,999 事件 | |
| 核心方法 | | 物理分段 + 统计基线 | |
| 计算平台 | | XP + .NET 4.0 + 单核 | |



## 5. 可提取的规则建议
<!-- 从论文中可以提取哪些具体的诊断判据？ -->
<!-- 如何适配到我们的五阶段分割 + 统计基线框架？ -->
<!-- 每个建议标注：所需新增特征、判据公式、建议阈值、预期触发率 -->



## 6. 提取人备注
<!-- 任何额外观察、限制条件、需要现场验证的假设、对论文方法的质疑等 -->


