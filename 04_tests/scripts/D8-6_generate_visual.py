#!/usr/bin/env python3
"""D8-6 目视验证 — 生成 Highcharts 对比页面"""

import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "02_source" / "tools"))
from physeg_prototype import detect_unlock_end, detect_contact_and_lock

BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# ── Load baseline ──
with open(os.path.join(BASE, '05_production_data', 'Rules', 'baselines.json'), 'r', encoding='utf-8-sig') as f:
    bl = json.load(f)['Switches']['1-J']

# ── Load standard curve ──
with open(os.path.join(BASE, '05_production_data', 'Rules', 'standard_curves', '1-J_py_verify.json'), 'r', encoding='utf-8') as f:
    sc = json.load(f)

# ── Load reference curve (same as verify script: first valid event) ──
sw_dir = os.path.join(BASE, '05_production_data', 'parsed_data', '1-J')
daily_files = sorted([f for f in os.listdir(sw_dir) if f.endswith('.json')])


def extract_features(values):
    """物理边界分段版特征提取"""
    import statistics as _st
    n = len(values)
    if n < 10:
        return None
    spike_val = max(values)
    spike_idx = values.index(spike_val)

    # activeEnd: 从尾向前找 > peak*0.05 的最后一点
    threshold = max(spike_val * 0.05, 0.01)
    ae = 0
    for i in range(n):
        if values[i] > threshold:
            ae = i
    dur = (ae + 1) * 0.04
    si = spike_idx

    # ② 解锁段 — 物理边界检测
    unlock_end = detect_unlock_end(values, si, ae)
    if unlock_end is not None and unlock_end > si + 1:
        unlock_start = si + 2
        unlock_end_idx = unlock_end + 1
        unlock_vals = values[unlock_start:unlock_end_idx]
    else:
        fallback_end = max(si + 14, int(ae * 0.5))
        unlock_start = si + 2
        unlock_end_idx = min(fallback_end, n)
        unlock_vals = values[unlock_start:unlock_end_idx]
        unlock_end = None

    # ③ 转换段 — 物理边界检测
    lock_start, lock_peak = detect_contact_and_lock(values, ae)
    if lock_start is None:
        lock_start = ae - 40 if ae > 50 else ae

    conv_start = (unlock_end + 1) if unlock_end is not None else (si + 20)
    conv_end = lock_start
    conv_vals = values[conv_start:conv_end] if conv_start < conv_end and conv_start < n else []

    # ④ 锁闭段 — 物理边界检测
    if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
        pre_ramp = _st.mean(values[lock_start - 5:lock_start + 1]) if lock_start >= 5 else values[lock_start]
        post_peak_end = min(lock_peak + 40, ae - 5)
        lock_end_idx = lock_peak + 5
        for i in range(lock_peak + 8, post_peak_end):
            if i < n and (values[i] <= pre_ramp * 1.08 or values[i] <= values[lock_peak] * 0.55):
                lock_end_idx = i
                break
        lock_seg_start = lock_start
        lock_seg_end = lock_end_idx + 1
        lock_vals = values[lock_seg_start:lock_seg_end]
    else:
        lock_seg_start = max(0, ae - 40) if ae > 50 else conv_end
        lock_seg_end = ae - 22 if ae > 50 else lock_seg_start
        lock_vals = values[lock_seg_start:lock_seg_end] if lock_seg_start < lock_seg_end else []

    # ⑤ 缓放段
    tail_start = lock_seg_end if lock_peak is not None else (ae - 22 if ae > 30 else ae)
    tail_end = ae
    tail_vals = values[tail_start:tail_end] if tail_start < tail_end else []

    def seg_mean(s, e):
        if s >= e or s >= n:
            return 0.0
        e = min(e, n)
        segment = values[s:e]
        return sum(segment) / len(segment) if segment else 0.0

    return {
        'SpikePeak': spike_val, 'SpikeIndex': spike_idx,
        'UnlockEnd': unlock_end, 'LockStart': lock_start,
        'UnlockMean': seg_mean(unlock_start, unlock_end_idx),
        'ConvMean': seg_mean(conv_start, conv_end),
        'LockMean': seg_mean(lock_seg_start, lock_seg_end),
        'TailMean': seg_mean(tail_start, tail_end),
        'DurationSec': dur, 'IsValid': True,
        # 段边界（供可视化使用）
        '_unlock_start': unlock_start, '_unlock_end': unlock_end_idx,
        '_conv_start': conv_start, '_conv_end': conv_end,
        '_lock_start': lock_seg_start, '_lock_end': lock_seg_end,
        '_tail_start': tail_start, '_tail_end': tail_end,
    }


all_events = []
for fname in daily_files:
    with open(os.path.join(sw_dir, fname), 'r', encoding='utf-8') as f:
        events = json.load(f)
    for evt in events:
        pw = evt.get('Power', [])
        if pw:
            values = [p[1] for p in pw if len(p) >= 2]
            if values:
                feat = extract_features(values)
                if feat and feat['IsValid'] and feat['DurationSec'] >= 2.4:
                    all_events.append({
                        'values': values, 'feat': feat,
                        'direction': evt.get('Direction', ''),
                        'datetime': evt.get('DateTimeStr', '')
                    })

ref_event = all_events[0]
ref_values = ref_event['values']
ref_feat = ref_event['feat']

# Segment boundaries — 使用物理边界检测结果
si = ref_feat['SpikeIndex']
ae = len(ref_values)
n = ae
unlock_start = ref_feat.get('_unlock_start', si + 2)
unlock_end = ref_feat.get('_unlock_end', min(si + 14, n))
conv_start = ref_feat.get('_conv_start', min(si + 20, n))
conv_end = ref_feat.get('_conv_end', max(ae - 40, si + 20) if ae > 74 else si + 20)
lock_start = ref_feat.get('_lock_start', conv_end)
lock_end = ref_feat.get('_lock_end', max(ae - 19, conv_end) if ae > 50 else conv_end)
tail_start = ref_feat.get('_tail_start', lock_end)

# JSON data for Highcharts
ref_data_json = json.dumps([[i * 0.04, v] for i, v in enumerate(ref_values)])
sc_data_json = json.dumps([[i * 0.04, v] for i, v in enumerate(sc['Values'])])

# Plot bands for segments
plot_bands_json = json.dumps([
    {'from': 0, 'to': si * 0.04, 'color': 'rgba(255,0,0,0.06)', 'label': {'text': 'Spike', 'style': {'fontSize': '10px'}}},
    {'from': unlock_start * 0.04, 'to': unlock_end * 0.04, 'color': 'rgba(0,128,0,0.06)', 'label': {'text': 'Unlock', 'style': {'fontSize': '10px'}}},
    {'from': conv_start * 0.04, 'to': conv_end * 0.04, 'color': 'rgba(0,0,255,0.06)', 'label': {'text': 'Conv', 'style': {'fontSize': '10px'}}},
    {'from': lock_start * 0.04, 'to': lock_end * 0.04, 'color': 'rgba(255,165,0,0.06)', 'label': {'text': 'Lock', 'style': {'fontSize': '10px'}}},
    {'from': tail_start * 0.04, 'to': ae * 0.04, 'color': 'rgba(128,0,128,0.06)', 'label': {'text': 'Tail', 'style': {'fontSize': '10px'}}},
])

# Transition zones — 使用物理边界
t2_start = unlock_end * 0.04 if unlock_end is not None else (si + 14) * 0.04
t2_end = conv_start * 0.04
t3_start = (conv_end - 3) * 0.04 if conv_end > conv_start else (ae - 43) * 0.04
t3_end = (lock_start + 3) * 0.04 if lock_start > conv_end else (ae - 37) * 0.04
t4_start = (lock_end - 3) * 0.04 if lock_end > lock_start else (ae - 25) * 0.04
t4_end = (tail_start + 3) * 0.04 if tail_start > lock_end else (ae - 19) * 0.04

transitions_json = json.dumps([
    {'from': si * 0.04, 'to': unlock_start * 0.04, 'color': 'rgba(255,255,0,0.12)', 'label': {'text': 'T1', 'style': {'fontSize': '9px'}}},
    {'from': t2_start, 'to': t2_end, 'color': 'rgba(255,255,0,0.12)', 'label': {'text': 'T2', 'style': {'fontSize': '9px'}}},
    {'from': t3_start, 'to': t3_end, 'color': 'rgba(255,255,0,0.12)', 'label': {'text': 'T3', 'style': {'fontSize': '9px'}}},
    {'from': t4_start, 'to': t4_end, 'color': 'rgba(255,255,0,0.12)', 'label': {'text': 'T4', 'style': {'fontSize': '9px'}}},
])

# Baseline values
bl_spike = bl['RefSpikePeak']
bl_unlock = bl['RefUnlockMean']
bl_conv = bl['RefConvMean']
bl_tail = bl['RefTailMean']

html = f'''<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>D8-6 目视验证 — 标准曲线 vs 参考曲线 vs 基线</title>
<script src="https://cdn.jsdelivr.net/npm/highcharts@2.2.1/highcharts.js"></script>
<style>
  body {{ font-family: "Microsoft YaHei", sans-serif; margin: 20px; background: #fff; }}
  h1 {{ font-size: 18px; margin-bottom: 5px; }}
  .info {{ color: #666; font-size: 13px; margin-bottom: 20px; line-height: 1.6; }}
  #container {{ width: 100%; height: 600px; }}
  .legend {{ margin-top: 15px; font-size: 13px; }}
  .legend span {{ display: inline-block; width: 14px; height: 14px; margin-right: 5px; vertical-align: middle; border-radius: 2px; }}
  .checklist {{ margin-top: 20px; padding: 15px; background: #f9f9f9; border-radius: 4px; }}
  .checklist h3 {{ margin-top: 0; }}
  .checklist li {{ margin: 6px 0; font-size: 14px; }}
  .pass {{ color: green; }}
  .warn {{ color: orange; }}
  table {{ border-collapse: collapse; margin: 15px 0; font-size: 13px; }}
  th, td {{ border: 1px solid #ddd; padding: 5px 10px; text-align: center; }}
  th {{ background: #f0f0f0; }}
  .alpha-table td:first-child {{ text-align: left; font-family: monospace; }}
  .missing {{ color: #e67e00; font-weight: bold; }}
</style>
</head>
<body>

<h1>D8-6 目视验证 — 道岔 1-J 标准曲线融合</h1>
<div class="info">
  <b>参考曲线:</b> {ref_event['datetime']} | 方向: {ref_event['direction']} | 点数: {len(ref_values)}<br>
  <b>标准曲线:</b> fw=1.0 | 点数: {len(sc['Values'])} | 来源: python_verify<br>
  <b>基线:</b> Dur={bl['RefDurationSec']}s, Spike={bl['RefSpikePeak']}kW,
  Unlock={bl['RefUnlockMean']}kW, Conv={bl['RefConvMean']}kW, Tail={bl['RefTailMean']}kW
  <br><b class="missing">⚠ RefLockMean 缺失</b> — baselines.json 中无 Lock 段均值 → ratio=0.0 → clamp 到 AlphaLock=0.7
</div>

<div id="container"></div>

<div class="legend">
  <strong>图例:</strong>
  <span style="background:#d7191c"></span> 标准曲线（融合后，fw=1.0）
  <span style="background:#2c7bb6"></span> 参考曲线（原始输入）
  <span style="background:rgba(0,128,0,0.4);border:1px dashed green"></span> 基线各段均值
  <span style="background:rgba(255,255,0,0.2);border:1px solid #cc0"></span> 过渡区
</div>

<table class="alpha-table">
  <tr><th>α 参数</th><th>值</th><th>公式</th><th>说明</th></tr>
  <tr><td>α_t (时长)</td><td>{sc['AlphaTime']:.4f}</td><td>基线Dur/参考Dur</td><td>时长缩放因子</td></tr>
  <tr><td>α_spike</td><td>{sc['AlphaSpike']:.4f}</td><td>clamp(基线Spike/参考Spike)</td><td>尖峰缩放</td></tr>
  <tr><td>α_unlock</td><td>{sc['AlphaUnlock']:.4f}</td><td>clamp(基线Unlock/参考Unlock)</td><td>解锁段缩放</td></tr>
  <tr><td>α_conv</td><td>{sc['AlphaConv']:.4f}</td><td>clamp(基线Conv/参考Conv)</td><td>转换段缩放</td></tr>
  <tr><td class="missing">α_lock</td><td class="missing">{sc['AlphaLock']:.4f}</td><td class="missing">clamp(N/A/参考Lock)=clamp(0)=0.7</td><td class="missing">🔒 RefLockMean缺失→clamp下限</td></tr>
  <tr><td>α_tail</td><td>{sc['AlphaTail']:.4f}</td><td>clamp(基线Tail/参考Tail)</td><td>缓放段缩放</td></tr>
</table>

<div class="checklist">
  <h3>D8-6 验收标准</h3>
  <ol>
    <li><b>形态一致性</b>: 标准曲线与参考曲线形态是否一致？两曲线应有相同的 spike 位置、段形状、零值区域。</li>
    <li><b>过渡区平滑</b>: Spike→Unlock (T1), Unlock→Conv (T2), Conv→Lock (T3), Lock→Tail (T4) 无跳变。</li>
    <li><b>各段均值接近基线</b>: fw=1.0 下标准曲线各段均值应在基线虚线附近。</li>
    <li><b>Lock 段偏低</b>: AlphaLock=0.7 → Lock 段被压低至参考曲线的 70%，这是预期行为。</li>
    <li><b>无异常跳变/毛刺</b>: 标准曲线整体平滑，无人工引入的 artifact。</li>
  </ol>
</div>

<script>
var refData = {ref_data_json};
var scData = {sc_data_json};
var plotBands = {plot_bands_json};
var transitions = {transitions_json};

// Combine plot bands: segments + transitions
var allBands = plotBands.concat(transitions);

new Highcharts.Chart({{
    chart: {{
        renderTo: 'container',
        zoomType: 'x',
        spacingRight: 20
    }},
    title: {{ text: '道岔 1-J 标准曲线目视对比 — fw=1.0 融合输出 vs 原始参考曲线' }},
    xAxis: {{
        title: {{ text: '时间 (秒)' }},
        min: 0,
        max: {ae * 0.04 + 0.1},
        plotBands: allBands,
        gridLineWidth: 1
    }},
    yAxis: {{
        title: {{ text: '功率 (kW)' }},
        min: 0,
        max: 3.5,
        plotLines: [
            {{ value: {bl_spike}, color: 'green', dashStyle: 'dash', width: 1,
               label: {{ text: 'Spike={bl_spike}kW', align: 'right', style: {{ color: 'green', fontSize: '10px' }} }} }},
            {{ value: {bl_unlock}, color: 'green', dashStyle: 'dash', width: 1,
               label: {{ text: 'Unlock={bl_unlock}kW', align: 'right', style: {{ color: 'green', fontSize: '10px' }} }} }},
            {{ value: {bl_conv}, color: 'green', dashStyle: 'dash', width: 1,
               label: {{ text: 'Conv={bl_conv}kW', align: 'right', style: {{ color: 'green', fontSize: '10px' }} }} }},
            {{ value: {bl_tail}, color: 'green', dashStyle: 'dash', width: 1,
               label: {{ text: 'Tail={bl_tail}kW', align: 'right', style: {{ color: 'green', fontSize: '10px' }} }} }}
        ]
    }},
    tooltip: {{
        crosshairs: [true, true],
        shared: true,
        valueDecimals: 4
    }},
    legend: {{
        align: 'center',
        verticalAlign: 'bottom',
        layout: 'horizontal'
    }},
    plotOptions: {{
        series: {{
            animation: false,
            marker: {{ enabled: false, states: {{ hover: {{ enabled: true, radius: 4 }} }} }}
        }}
    }},
    series: [
        {{
            name: '标准曲线 (融合 fw=1.0)',
            data: scData,
            color: '#d7191c',
            lineWidth: 2.5,
            zIndex: 10
        }},
        {{
            name: '参考曲线 (原始)',
            data: refData,
            color: '#2c7bb6',
            lineWidth: 1.5,
            zIndex: 5
        }},
        {{
            name: 'Spike基线',
            type: 'line',
            data: [[0, {bl_spike}], [{si * 0.04}, {bl_spike}]],
            color: 'rgba(0,128,0,0.5)',
            dashStyle: 'Dash',
            lineWidth: 1,
            enableMouseTracking: false,
            showInLegend: false
        }},
        {{
            name: 'Unlock基线',
            type: 'line',
            data: [[{unlock_start * 0.04}, {bl_unlock}], [{unlock_end * 0.04}, {bl_unlock}]],
            color: 'rgba(0,128,0,0.5)',
            dashStyle: 'Dash',
            lineWidth: 1,
            enableMouseTracking: false,
            showInLegend: false
        }},
        {{
            name: 'Conv基线',
            type: 'line',
            data: [[{conv_start * 0.04}, {bl_conv}], [{conv_end * 0.04}, {bl_conv}]],
            color: 'rgba(0,128,0,0.5)',
            dashStyle: 'Dash',
            lineWidth: 1,
            enableMouseTracking: false,
            showInLegend: false
        }},
        {{
            name: 'Tail基线',
            type: 'line',
            data: [[{tail_start * 0.04}, {bl_tail}], [{ae * 0.04}, {bl_tail}]],
            color: 'rgba(0,128,0,0.5)',
            dashStyle: 'Dash',
            lineWidth: 1,
            enableMouseTracking: false,
            showInLegend: false
        }}
    ]
}});
</script>

</body>
</html>'''

out_path = os.path.join(BASE, '04_tests', 'generated', 'D8-6_visual_verify.html')
os.makedirs(os.path.dirname(out_path), exist_ok=True)
with open(out_path, 'w', encoding='utf-8') as f:
    f.write(html)

print(f'HTML saved to: {out_path}')
print(f'Open in browser to verify visually.')
