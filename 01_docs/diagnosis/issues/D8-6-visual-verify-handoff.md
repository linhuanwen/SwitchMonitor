# D8-6 目视验证 — 执行手册

> 日期: 2026-07-14
> 前置: D8 A/B/C 三路验证完成（见 D8-TDD-verification-report.md）

## 一键执行

在新窗口输入：

```
读 01_docs/diagnosis/issues/D8-TDD-verification-report.md，执行 D8-6 目视验证
```

## 手动步骤

如果一键不生效，按以下步骤：

### Step 1 — 生成目视对比页面

```bash
cd SwitchMonitor
python3 04_tests/scripts/D8-6_generate_visual.py
```

### Step 2 — 打开浏览器查看

```bash
start 04_tests/generated/D8-6_visual_verify.html
```

或用资源管理器双击 `04_tests/generated/D8-6_visual_verify.html`。

### Step 3 — 目视检查

页面包含三条信息：

| 图层 | 颜色 | 含义 |
|---|---|---|
| 标准曲线 | 🔴 红色粗线 | 融合输出（fw=1.0），这是最终交付物 |
| 参考曲线 | 🔵 蓝色细线 | 原始输入曲线（第一有效事件） |
| 基线均值 | 🟢 绿色虚线 | baselines.json 中各段的统计均值 |

逐项核对页面底部的 5 条验收标准。

## 关键观察点

1. **Spike 尖峰**: 红色线峰值应略低于蓝色线 → AlphaSpike=0.9537，向基线 3.235kW 对齐
2. **Lock 段**（橙色区域）: 红色线明显低于蓝色线 → AlphaLock=0.7000，RefLockMean 缺失导致
3. **T2/T3/T4 过渡区**（黄色竖条）: 两条曲线在这些区域应平滑过渡，无台阶
4. **零值区域**: 首尾零点应与参考曲线对齐（AlignIndex=6）

## 产出文件

| 文件 | 用途 |
|---|---|
| `04_tests/generated/D8-6_visual_verify.html` | Highcharts 目视对比页面 |
| `04_tests/scripts/D8-6_generate_visual.py` | 生成脚本（可复现） |

## 验证通过标准

- 全部 5 项验收 ✅ → 推进 D9 DriftEstimator
- 有 ❌ → 回退 D8 修复
