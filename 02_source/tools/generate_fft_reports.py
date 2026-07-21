#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
自动为所有转辙机生成 FFT 分析 HTML 报告。
读取统计信息和 PNG 图表，生成带中文注释的报告页面。
"""

import json
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np

PRODUCTION_DATA_DIR = Path(r"D:\tool\SwitchMonitor\05_production_data")
PARSED_DATA_DIR = PRODUCTION_DATA_DIR / "parsed_data"
RULES_DIR = PRODUCTION_DATA_DIR / "Rules"
OUTPUT_DIR = Path(r"D:\Vibe coding\04 DCjiance\SwitchMonitor\06_deploy\fft_explore")

SWITCHES = ["1-1", "1-X", "2-1", "2-X", "3-1", "3-X", "4-1", "4-X"]
DIRECTION = "定位→反位"
SAMPLE_RATE = 25.0

BANDS_CN = [
    ("超低频 (0.04-0.5Hz)", 0.04, 0.5),
    ("低频 (0.5-2Hz)",     0.5,  2.0),
    ("中频 (2-5Hz)",       2.0,  5.0),
    ("高频 (5-12.5Hz)",    5.0,  12.5),
]


def load_curves_stats(switch_id):
    """加载曲线并计算统计信息"""
    switch_dir = PARSED_DATA_DIR / switch_id
    if not switch_dir.is_dir():
        return None

    curves = []
    json_files = sorted(switch_dir.glob("*.json"))
    json_files = [f for f in json_files
                  if f.name != "index.json" and not f.name.endswith(".diag.json")]

    for fp in json_files:
        try:
            with open(fp, encoding="utf-8") as fh:
                events = json.load(fh)
        except (json.JSONDecodeError, OSError):
            continue
        for evt in events:
            if evt.get("Direction") != DIRECTION:
                continue
            power = evt.get("Power", [])
            if not power or len(power) < 50:
                continue
            curves.append(dict(
                timestamp=evt["Timestamp"],
                datetime=evt.get("DateTimeStr", ""),
                duration=evt.get("Duration", 0.0),
                sample_count=len(power),
            ))

    if not curves:
        return None

    durs = np.array([c["duration"] for c in curves])
    samples = np.array([c["sample_count"] for c in curves])

    # 频段能量统计（对所有曲线采样计算）
    p25, p75 = np.percentile(durs, [25, 75])
    short = [c for c in curves if c["duration"] <= p25]
    mid = [c for c in curves if p25 < c["duration"] <= p75]
    long_ = [c for c in curves if c["duration"] > p75]

    # 加载基线
    baselines = None
    bp = RULES_DIR / "baselines.json"
    if bp.is_file():
        with open(bp, encoding="utf-8-sig") as fh:
            data = json.load(fh)
        baselines = data.get("Switches", {}).get(switch_id, None)

    ref_dur = baselines.get("RefDurationSec", 12.0) if baselines else 12.0
    normal_count = sum(1 for c in curves if abs(c["duration"] - ref_dur) < 1.0)
    abnormal_count = len(curves) - normal_count

    return dict(
        switch=switch_id,
        total=len(curves),
        dur_min=float(np.min(durs)),
        dur_median=float(np.median(durs)),
        dur_max=float(np.max(durs)),
        dur_mean=float(np.mean(durs)),
        dur_std=float(np.std(durs)),
        dur_p25=float(p25),
        dur_p75=float(p75),
        sample_min=int(np.min(samples)),
        sample_max=int(np.max(samples)),
        time_start=curves[0]["datetime"],
        time_end=curves[-1]["datetime"],
        ref_dur=ref_dur,
        normal_count=normal_count,
        abnormal_count=abnormal_count,
        short_count=len(short),
        mid_count=len(mid),
        long_count=len(long_),
        extreme_count=sum(1 for c in curves if c["duration"] > 18),
    )


def generate_switch_report(stats):
    """为单台转辙机生成 HTML 报告"""
    sw = stats["switch"]
    prefix = f"{sw.replace('-','_')}_{DIRECTION.replace('→','to')}"

    # 异常比例
    abnormal_pct = stats["abnormal_count"] / stats["total"] * 100

    html = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>FFT 频域分析报告 —— 转辙机 {sw}</title>
<style>
* {{ margin:0; padding:0; box-sizing:border-box; }}
body {{ font-family:"Microsoft YaHei","SimHei",sans-serif; background:#f0f2f5; color:#2c3e50; line-height:1.8; }}
.container {{ max-width:1200px; margin:0 auto; padding:20px; }}

/* Hero */
.hero {{ background:linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%); color:white; padding:50px 40px; border-radius:16px; margin-bottom:30px; text-align:center; }}
.hero h1 {{ font-size:2em; margin-bottom:8px; }}
.hero .subtitle {{ font-size:1.1em; opacity:0.8; }}
.hero .stats-grid {{ display:flex; gap:16px; justify-content:center; flex-wrap:wrap; margin-top:24px; }}
.hero .stat-card {{ background:rgba(255,255,255,0.1); backdrop-filter:blur(10px); border-radius:12px; padding:18px 24px; min-width:120px; }}
.hero .stat-card .num {{ font-size:2em; font-weight:bold; }}
.hero .stat-card .label {{ font-size:0.85em; opacity:0.75; }}

/* Section */
.section {{ background:white; border-radius:12px; padding:32px; margin-bottom:24px; box-shadow:0 1px 3px rgba(0,0,0,0.08); }}
.section h2 {{ font-size:1.4em; border-left:4px solid #0f3460; padding-left:14px; margin-bottom:20px; }}
.section h3 {{ font-size:1.1em; color:#555; margin:16px 0 10px; }}

/* Table */
.info-table {{ width:100%; border-collapse:collapse; margin:16px 0; }}
.info-table td {{ padding:8px 14px; border-bottom:1px solid #eee; }}
.info-table td:first-child {{ font-weight:bold; color:#555; white-space:nowrap; width:30%; }}

/* Chart */
.chart-box {{ margin:24px 0; }}
.chart-box img {{ max-width:100%; border-radius:8px; box-shadow:0 2px 8px rgba(0,0,0,0.1); }}
.chart-box .caption {{ background:#f8f9fa; padding:14px 18px; border-radius:0 0 8px 8px; margin-top:-4px; font-size:0.92em; }}

/* Finding */
.finding {{ background:#fefefe; border:1px solid #e0e0e0; border-radius:10px; padding:20px 24px; margin:16px 0; }}
.finding .finding-num {{ display:inline-block; background:#0f3460; color:white; padding:2px 12px; border-radius:20px; font-size:0.85em; margin-bottom:8px; }}
.finding .translate {{ background:#fff8e1; border-left:4px solid #f39c12; padding:12px 16px; margin-top:10px; border-radius:0 6px 6px 0; }}
.finding .translate::before {{ content:"🈯 翻译成大白话："; font-weight:bold; color:#e67e22; }}

/* Alert */
.alert-good {{ background:#e8f5e9; border-left:4px solid #27ae60; padding:14px 18px; border-radius:6px; margin:12px 0; }}
.alert-warn {{ background:#fff3e0; border-left:4px solid #e67e22; padding:14px 18px; border-radius:6px; margin:12px 0; }}
.alert-info {{ background:#e3f2fd; border-left:4px solid #2980b9; padding:14px 18px; border-radius:6px; margin:12px 0; }}

/* TOC */
.toc {{ display:flex; gap:12px; flex-wrap:wrap; margin-bottom:24px; }}
.toc a {{ background:#16213e; color:white; padding:8px 18px; border-radius:20px; text-decoration:none; font-size:0.9em; }}
.toc a:hover {{ background:#0f3460; }}

/* Footer */
.footer {{ text-align:center; padding:20px; color:#999; font-size:0.85em; }}

/* Compare bar */
.bar-container {{ margin:8px 0; }}
.bar-label {{ display:flex; justify-content:space-between; font-size:0.9em; margin-bottom:2px; }}
.bar-track {{ background:#e9ecef; border-radius:10px; height:22px; overflow:hidden; }}
.bar-fill {{ height:100%; border-radius:10px; display:flex; align-items:center; justify-content:center; color:white; font-size:0.8em; font-weight:bold; }}
</style>
</head>
<body>
<div class="container">

<!-- ====== Hero ====== -->
<div class="hero">
<h1>🔬 FFT 功率曲线频域分析报告</h1>
<div class="subtitle">转辙机 <strong>{sw}</strong> | 方向：{DIRECTION} | 采样率 25Hz</div>
<div class="stats-grid">
  <div class="stat-card"><div class="num">{stats["total"]}</div><div class="label">总曲线数</div></div>
  <div class="stat-card"><div class="num">{stats["dur_median"]:.1f}s</div><div class="label">中位时长</div></div>
  <div class="stat-card"><div class="num">{abnormal_pct:.1f}%</div><div class="label">异常比例</div></div>
  <div class="stat-card"><div class="num">{stats["extreme_count"]}</div><div class="label">极端事件(&gt;18s)</div></div>
</div>
</div>

<!-- ====== TOC ====== -->
<div class="toc">
  <a href="#basic">📊 基本统计</a>
  <a href="#chart1">📈 图1：频谱叠加</a>
  <a href="#chart2">🔥 图2：STFT 时频谱</a>
  <a href="#chart3">⚖️ 图3：正常 vs 异常</a>
  <a href="#chart4">🔍 图4：单曲线剖析</a>
  <a href="#chart5">📅 图5：长期趋势</a>
  <a href="#summary">📋 总结</a>
</div>

<!-- ====== 基本统计 ====== -->
<div class="section" id="basic">
<h2>📊 基本统计信息</h2>
<table class="info-table">
<tr><td>转辙机编号</td><td>{sw}</td></tr>
<tr><td>分析方向</td><td>{DIRECTION}</td></tr>
<tr><td>总曲线数</td><td>{stats["total"]} 条</td></tr>
<tr><td>时间跨度</td><td>{stats["time_start"]} → {stats["time_end"]}</td></tr>
<tr><td>时长范围</td><td>最短 {stats["dur_min"]:.1f}s / 中位数 {stats["dur_median"]:.1f}s / 最长 {stats["dur_max"]:.1f}s</td></tr>
<tr><td>平均时长 ± 标准差</td><td>{stats["dur_mean"]:.2f} ± {stats["dur_std"]:.2f}s</td></tr>
<tr><td>25%/75% 分位数</td><td>{stats["dur_p25"]:.1f}s / {stats["dur_p75"]:.1f}s</td></tr>
<tr><td>采样点范围</td><td>{stats["sample_min"]} ~ {stats["sample_max"]} 点</td></tr>
<tr><td>参考时长（基线）</td><td>{stats["ref_dur"]:.2f}s</td></tr>
<tr><td>正常/异常 数量</td><td>正常 {stats["normal_count"]} 条 / 异常 {stats["abnormal_count"]} 条（偏离基线 ±1s）</td></tr>
<tr><td>按时长分组</td><td>短时长 ≤{stats["dur_p25"]:.1f}s: {stats["short_count"]} 条 | 中等: {stats["mid_count"]} 条 | 长时长 >{stats["dur_p75"]:.1f}s: {stats["long_count"]} 条</td></tr>
<tr><td>极端异常（&gt;18s）</td><td>{stats["extreme_count"]} 条</td></tr>
</table>

<div class="alert-info">
<strong>💡 解读要点：</strong>时长偏离基线 ±1s =「异常」只是一个粗糙分组。
「异常」里面混了多种故障类型（卡阻、摩擦增大、解锁困难等），每种故障的频域特征可能不同。
真正有价值的分析是按故障类型分组的差异频谱。
</div>
</div>

<!-- ====== 图1：频谱叠加 ====== -->
<div class="section" id="chart1">
<h2>📈 图1：频谱叠加（4合1）</h2>
<div class="chart-box">
  <img src="{prefix}_01_spectrum_overlay.png" alt="频谱叠加">
  <div class="caption">
    <strong>怎么看这张图：</strong><br>
    <strong>子图1（左上）：</strong>所有曲线的频谱透明叠加。线条集中在低频 → 正常；线条发散且有高频突起 → 异常。<br>
    <strong>子图2（右上）：</strong>均值 ± 1σ 带。蓝色带越窄 → 频谱越稳定，设备状态越一致。超出蓝带的个别曲线需要关注。<br>
    <strong>子图3（左下）：</strong>按时长分组的均值频谱。三条线明显分开 → 时长和频谱有关联，时长异常的设备可能有特定频域特征。<br>
    <strong>子图4（右下）：</strong>各频段能量占比的箱线图。箱子越矮 → 该频段越稳定。高频箱子的异常值（圆点）是重点关注对象。
  </div>
</div>
</div>

<!-- ====== 图2：STFT ====== -->
<div class="section" id="chart2">
<h2>🔥 图2：STFT 时频谱（4条代表曲线）</h2>
<div class="chart-box">
  <img src="{prefix}_02_stft_examples.png" alt="STFT 时频谱">
  <div class="caption">
    <strong>怎么看这张图：</strong><br>
    横轴 = 时间（秒），纵轴 = 频率（Hz），颜色 = 该时刻该频率的能量强度（dB）。<br>
    青色虚线 = 原始功率曲线（对照参考）。<br>
    <strong>正常特征：</strong>能量集中在低频区域（底部），高潮出现在启动尖峰和解锁段。<br>
    <strong>异常信号：</strong>中高频（2Hz以上）出现亮斑 → 有异常振动/摩擦。<br>
    <strong>极端异常（时长>20s）：</strong>注意看能量是否在中高频持续存在，而不是只在启动阶段。
  </div>
</div>
</div>

<!-- ====== 图3：正常 vs 异常 ====== -->
<div class="section" id="chart3">
<h2>⚖️ 图3：正常 vs 异常频谱对比（6合1）</h2>
<div class="chart-box">
  <img src="{prefix}_03_normal_vs_abnormal.png" alt="正常 vs 异常">
  <div class="caption">
    <strong>怎么看这张图：</strong><br>
    <strong>子图1&2：</strong>正常（绿）和异常（红）的频谱叠加。肉眼能看出分布差异 → 频域方法有效。<br>
    <strong>子图3：</strong>均值频谱 ± 1σ 对比。绿带和红带明显分开的频段 = 能区分正常/异常的特征频段。<br>
    <strong>子图4：</strong>各频段能量占比柱状图。哪根柱子差距大 → 那个频段是关键差异频段。<br>
    <strong>★ 子图5（差异频谱）：</strong>红线山峰 = 异常比正常能量高的频率 → 可能是故障特征频率！<br>
    <strong>子图6（时域对照）：</strong>传统功率曲线对比，用于和频域互相印证。
  </div>
</div>
</div>

<!-- ====== 图4：单曲线 ====== -->
<div class="section" id="chart4">
<h2>🔍 图4：单曲线深度剖析（中位时长曲线）</h2>
<div class="chart-box">
  <img src="{prefix}_04_deep_dive.png" alt="单曲线深度剖析">
  <div class="caption">
    <strong>怎么看这张图：</strong><br>
    <strong>子图1：</strong>熟悉的功率曲线（时域）。<br>
    <strong>子图2：</strong>FFT 频谱。每根茎 = 该频率的正弦波分量有多强。绝大部分能量在低频（左边）。<br>
    <strong>子图3：</strong>饼图，直观显示各频段能量占比。正常情况超低频占 95%+。<br>
    <strong>子图4：</strong>STFT 时频谱。横轴 = 时间，纵轴 = 频率，颜色 = 强度。可以在时间轴上定位"哪个阶段出现了哪个频率的异常"。
  </div>
</div>
</div>

<!-- ====== 图5：长期趋势 ====== -->
<div class="section" id="chart5">
<h2>📅 图5：频段能量长期趋势</h2>
<div class="chart-box">
  <img src="{prefix}_05_band_trend.png" alt="长期趋势">
  <div class="caption">
    <strong>怎么看这张图：</strong><br>
    <strong>子图1（堆叠面积）：</strong>每个颜色带宽 = 该频段占总能量的比例。某颜色突然变厚 → 该频段能量占比升高，设备状态可能有变化。<br>
    <strong>子图2（各频段绝对能量）：</strong>7点滑动均值。红线（高频）持续上升 → 杂波越来越多，可能是机械老化/磨损信号。<br>
    <strong>子图3（时长对照）：</strong>如果时长和频段能量同步变化 → 同源问题；如果频段能量变了时长没变 → 频域更灵敏。
  </div>
</div>
</div>

<!-- ====== 总结 ====== -->
<div class="section" id="summary">
<h2>📋 总结与观察</h2>

<div class="finding">
<div class="finding-num">观察 1</div>
<p><strong>数据概览：</strong>共 <strong>{stats["total"]}</strong> 条曲线，
时间跨度从 {stats["time_start"][:10]} 到 {stats["time_end"][:10]}，
时长范围 {stats["dur_min"]:.1f}s ~ {stats["dur_max"]:.1f}s，中位数 {stats["dur_median"]:.1f}s。</p>
<div class="translate">这是 {sw} 号转辙机定位→反位方向的完整频域画像。正常参考时长={stats["ref_dur"]:.1f}s，偏离超过 1s 的有 {stats["abnormal_count"]} 条（占 {abnormal_pct:.1f}%）。</div>
</div>

<div class="finding">
<div class="finding-num">观察 2</div>
<p><strong>频域稳定性：</strong>看「图1 子图1」（频谱叠加），如果所有曲线高度重叠、集中在超低频区域 → 设备状态一致性好。如果有很多条线明显偏离主线、或在中高频出现额外峰值 → 存在异常事件。</p>
<div class="translate">正常功率曲线应集中在超低频（&lt;0.5Hz），高频（&gt;5Hz）几乎没有能量。高频一旦有能量 = 异常。</div>
</div>

<div class="finding">
<div class="finding-num">观察 3</div>
<p><strong>关键差异频谱：</strong>看「图3 子图5」（紫色差异频谱曲线）。红色山峰（异常偏高）对应的频率就是区分正常和异常的特征频率。如果差异频谱很平坦、没有明显的峰 → 说明「按偏离基线 ±1s」的分组方式太粗糙，里面混了不同类型的异常，它们的频谱差异互相抵消了。</p>
<div class="translate">如果差异频谱没有大峰，意味着需要更精细的分组方式（按故障类型分、按极端程度分）才能找到特征频率。</div>
</div>

<div class="finding">
<div class="finding-num">观察 4</div>
<p><strong>长期退化趋势：</strong>看「图5 子图2」。如果高频段（红线）在监测期间持续上升 → 设备可能存在渐进式机械老化。
如果各频段能量稳定 → 设备状态没有明显退化。</p>
<div class="translate">频域趋势比单纯看时长趋势更灵敏——时长可能还没明显变化，频域已经能检测到杂波增多了。</div>
</div>

<div class="finding">
<div class="finding-num">观察 5</div>
<p><strong>极端事件：</strong>本台转辙机有 <strong>{stats["extreme_count"]}</strong> 条时长超过 18s 的极端异常事件（最长 {stats["dur_max"]:.1f}s）。
这些事件在时域已经非常明显，频域上大概率有强烈的特征信号，是故障诊断的「金矿」。</p>
<div class="translate">极端事件（20s+）应该单独拉出来做差异频谱，它们的频域特征会比「所有异常混在一起」清晰得多。</div>
</div>

</div>

<div class="footer">
<p>由 FFT 功率曲线频域探索模块自动生成 | 转辙机 {sw} | 数据截止 {stats["time_end"][:10]}</p>
</div>

</div>
</body>
</html>"""
    return html


def generate_index(stats_list):
    """生成总索引页"""
    rows = []
    for s in stats_list:
        abnormal_pct = s["abnormal_count"] / s["total"] * 100
        rows.append(f"""<tr>
      <td><a href="{s['switch']}.html"><strong>{s['switch']}</strong></a></td>
      <td>{s['total']}</td>
      <td>{s['dur_min']:.1f} ~ {s['dur_max']:.1f}</td>
      <td>{s['dur_median']:.1f}s</td>
      <td>{s['ref_dur']:.1f}s</td>
      <td>{abnormal_pct:.1f}%</td>
      <td>{s['extreme_count']}</td>
      <td>{s['time_start'][:10]} → {s['time_end'][:10]}</td>
    </tr>""")

    return f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>FFT 频域分析 —— 全部转辙机总览</title>
<style>
* {{ margin:0; padding:0; box-sizing:border-box; }}
body {{ font-family:"Microsoft YaHei","SimHei",sans-serif; background:#f0f2f5; color:#2c3e50; line-height:1.8; }}
.container {{ max-width:1300px; margin:0 auto; padding:20px; }}
.hero {{ background:linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%); color:white; padding:40px; border-radius:16px; margin-bottom:30px; text-align:center; }}
.hero h1 {{ font-size:2em; margin-bottom:8px; }}
.compare-table {{ width:100%; border-collapse:collapse; background:white; border-radius:12px; overflow:hidden; box-shadow:0 1px 3px rgba(0,0,0,0.08); }}
.compare-table th {{ background:#16213e; color:white; padding:14px 12px; text-align:left; font-weight:normal; font-size:0.9em; }}
.compare-table td {{ padding:10px 12px; border-bottom:1px solid #eee; font-size:0.92em; }}
.compare-table tr:hover {{ background:#f5f7fa; }}
.compare-table a {{ color:#2980b9; text-decoration:none; }}
.compare-table a:hover {{ text-decoration:underline; }}
.intro {{ background:white; border-radius:12px; padding:28px; margin-bottom:24px; box-shadow:0 1px 3px rgba(0,0,0,0.08); }}
.intro h2 {{ font-size:1.3em; border-left:4px solid #0f3460; padding-left:14px; margin-bottom:16px; }}
.intro ul {{ margin-left:20px; }}
.intro li {{ margin:6px 0; }}
.footer {{ text-align:center; padding:20px; color:#999; font-size:0.85em; }}
.highlight {{ background:#fff8e1; }}
</style>
</head>
<body>
<div class="container">

<div class="hero">
<h1>🔬 FFT 功率曲线频域分析 —— 全部转辙机总览</h1>
<p style="opacity:0.8; margin-top:8px;">共 {len(stats_list)} 台转辙机 | 方向：{DIRECTION} | 采样率 25Hz</p>
</div>

<div class="intro">
<h2>📖 使用说明</h2>
<ul>
  <li>点击转辙机编号进入详细报告</li>
  <li><strong>异常比例</strong> = 时长偏离基线 ±1s 的曲线占比。注意这只是粗糙分组，不代表真正故障率</li>
  <li><strong>极端事件</strong> = 时长超过 18s 的事件。这些是频域分析最有价值的对象</li>
  <li>比较不同转辙机的「差异频谱」找到各台设备的特征频率</li>
  <li>关注「长期趋势」中高频段是否持续上升 → 可能预示机械老化</li>
</ul>
</div>

<table class="compare-table">
<thead>
<tr>
  <th>转辙机</th>
  <th>总曲线数</th>
  <th>时长范围</th>
  <th>中位时长</th>
  <th>参考时长</th>
  <th>异常比例</th>
  <th>极端事件</th>
  <th>时间跨度</th>
</tr>
</thead>
<tbody>
{"".join(rows)}
</tbody>
</table>

<div class="footer">
<p>FFT 功率曲线频域探索模块 | 全部报告自动生成 | 数据截止 2026-06-29</p>
</div>

</div>
</body>
</html>"""


def main():
    print("=" * 60)
    print("FFT 分析报告自动生成器")
    print("=" * 60)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    stats_list = []

    for sw in SWITCHES:
        print(f"\n[{sw}] 统计中...")
        stats = load_curves_stats(sw)
        if stats is None:
            print(f"  [跳过] 无数据")
            continue

        stats_list.append(stats)

        report_html = generate_switch_report(stats)
        report_path = OUTPUT_DIR / f"{sw}.html"
        with open(report_path, "w", encoding="utf-8") as f:
            f.write(report_html)
        print(f"  [保存] {report_path}")

    # 总索引
    index_html = generate_index(stats_list)
    index_path = OUTPUT_DIR / "index.html"
    with open(index_path, "w", encoding="utf-8") as f:
        f.write(index_html)
    print(f"\n[索引] {index_path}")

    print(f"\n{'=' * 60}")
    print(f"完成! {len(stats_list)}/8 台转辙机报告已生成")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
