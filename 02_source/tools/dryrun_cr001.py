#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CR001 Dryrun — 尾部小平台倍增检测 (表示二极管击穿)"""

import csv
import statistics
import json
from pathlib import Path

SRC = Path(r'd:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\sanshuibei_csv')
BASELINE_PATH = Path(r'd:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\Rules\baselines.json')
POWER_FILES = {3: '1-1', 7: '1-X', 11: '3-1', 15: '3-X',
               19: '2-1', 23: '2-X', 27: '4-1', 31: '4-X'}

MULTIPLIER = 1.8  # CR001 threshold from KB002

with open(BASELINE_PATH, encoding='utf-8-sig') as fh:
    baselines = json.load(fh)

def read_power_rows(path):
    out = []
    with open(path, encoding='utf-8') as fh:
        rd = csv.reader(fh)
        next(rd)
        for r in rd:
            vals = []
            for x in r[3:]:
                if x == '':
                    break
                vals.append(float(x))
            out.append((int(r[0]), r[1], vals))
    return out

def extract(v):
    f = {}
    f['sampleCount'] = len(v)
    f['isFullWindow'] = len(v) >= 780
    peak_all = max(v) if v else 0.0
    f['isValid'] = bool(v) and peak_all > 0.01
    if not f['isValid']:
        return f
    th = max(peak_all * 0.05, 0.01)
    last = 0
    for i, x in enumerate(v):
        if x > th:
            last = i
    f['activeEnd'] = last
    f['durationSec'] = round((last + 1) * 0.04, 2)
    head = v[:15]
    f['spikePeak'] = round(max(head), 3)
    sp = head.index(max(head))
    f['spikeIndex'] = sp
    seg = v[sp + 2:sp + 14]
    f['unlockMean'] = round(statistics.mean(seg), 3) if seg else 0.0
    conv = v[sp + 20:last - 40] if last - 40 > sp + 20 else v[sp + 2:last]
    if not conv:
        conv = v[:last + 1]
    f['convMean'] = round(statistics.mean(conv), 3)
    f['convMax'] = round(max(conv), 3)
    third = len(conv) // 3
    if third >= 5:
        f['stepRatio'] = round(statistics.mean(conv[-third:]) / max(statistics.mean(conv[:third]), 0.01), 3)
    else:
        f['stepRatio'] = 1.0
    lock_seg = v[last - 40:last - 22] if last > 50 else []
    if lock_seg:
        if last - 40 < 0:
            lock_seg = v[0:last - 22]
        f['lockMean'] = round(statistics.mean(lock_seg), 3)
    else:
        f['lockMean'] = 0.0
    tail = v[last - 22:last - 2] if last > 30 else []
    f['tailMean'] = round(statistics.mean(tail), 3) if tail else 0.0
    return f

def build_baseline(feats, min_samples=30):
    pool = [f for f in feats if f.get('isValid') and not f['isFullWindow'] and f['durationSec'] >= 2.4]
    if not pool:
        return None
    med = statistics.median([f['durationSec'] for f in pool])
    normal = [f for f in pool if abs(f['durationSec'] - med) < med * 0.15]
    if len(normal) < min_samples:
        return None
    return dict(
        refTailMean=round(statistics.median([f['tailMean'] for f in normal]), 3),
        sampleCount=len(normal))

print('=' * 80)
print('CR001 Dryrun — 尾部小平台倍增检测 (表示二极管击穿)')
print('阈值: tailMean > refTailMean x %.1f' % MULTIPLIER)
print('=' * 80)
print()

grand_total = 0
grand_fired = 0
all_tail_ratios = []
per_switch = {}

for pf, sid in POWER_FILES.items():
    rows = read_power_rows(SRC / ('SwitchCurve(%d).csv' % pf))
    feats = [extract(v) for _, _, v in rows]
    valid = [f for f in feats if f.get('isValid') and not f['isFullWindow'] and f['durationSec'] >= 2.4]

    # Use the baseline from baselines.json
    b = baselines['Switches'].get(sid, {})
    ref_tail = b.get('RefTailMean', None)

    # Also compute local baseline for comparison
    local_b = build_baseline(feats)
    local_ref_tail = local_b['refTailMean'] if local_b else None

    if ref_tail is None:
        print('%s: baseline missing, skip' % sid)
        continue

    ratios = [f['tailMean'] / ref_tail for f in valid if ref_tail > 0 and f['tailMean'] > 0.001]
    all_tail_ratios.extend(ratios)

    fired_at_mult = {}
    for mult in [1.3, 1.5, 1.8, 2.0]:
        fired_at_mult[mult] = sum(1 for r in ratios if r > mult)

    sorted_ratios = sorted(ratios)
    n = len(sorted_ratios)
    p90 = sorted_ratios[int(n * 0.90)]
    p95 = sorted_ratios[int(n * 0.95)]
    p99 = sorted_ratios[int(n * 0.99)] if int(n * 0.99) < n else sorted_ratios[-1]
    p995 = sorted_ratios[int(n * 0.995)] if int(n * 0.995) < n else sorted_ratios[-1]
    max_ratio = max(ratios)

    grand_total += len(valid)
    grand_fired += fired_at_mult[MULTIPLIER]

    per_switch[sid] = {
        'n': len(valid), 'refTail': ref_tail, 'localRefTail': local_ref_tail,
        'p90': p90, 'p95': p95, 'p99': p99, 'p995': p995, 'max': max_ratio,
        'fired_1.8': fired_at_mult[1.8], 'fired_2.0': fired_at_mult[2.0]
    }

    print('%6s | n=%5d | refTail=%.3fkW | P90=%.2fx P95=%.2fx P99=%.2fx P99.5=%.2fx max=%.2fx | 1.8x=%d 2.0x=%d' % (
        sid, len(valid), ref_tail, p90, p95, p99, p995, max_ratio,
        fired_at_mult[1.8], fired_at_mult[2.0]))

print()
print('合计: %d 条有效曲线, CR001(1.8x) 触发 %d 条 (%.4f%%)' % (grand_total, grand_fired, grand_fired/grand_total*100))

total = len(all_tail_ratios)
sorted_all = sorted(all_tail_ratios)
print()
print('全量 tailMean/refTailMean 分布 (n=%d):' % total)
print('  Mean:   %.3fx' % statistics.mean(all_tail_ratios))
print('  Stdev:  %.3fx' % statistics.stdev(all_tail_ratios))
print('  P50:    %.3fx' % sorted_all[int(total*0.50)])
print('  P90:    %.3fx' % sorted_all[int(total*0.90)])
print('  P95:    %.3fx' % sorted_all[int(total*0.95)])
print('  P99:    %.3fx' % sorted_all[int(total*0.99)])
print('  P99.5:  %.3fx' % sorted_all[int(total*0.995)])
print('  P99.9:  %.3fx' % sorted_all[int(total*0.999)])
print('  Max:    %.3fx' % max(all_tail_ratios))

print()
print('=== 阈值选择分析 ===')
for mult in [1.3, 1.5, 1.8, 2.0, 2.5]:
    fire = sum(1 for r in all_tail_ratios if r > mult)
    rate = fire / total * 100
    print('  x%.1f: 触发 %d/%d (%.4f%%)  %s' % (mult, fire, total, rate, '← R8当前值' if mult == 1.3 else ('← CR001推荐' if mult == 1.8 else '')))

print()
print('=== 结论 ===')
if grand_fired == 0:
    print('OK: 所有正常曲线的尾部小平台均在 x1.8 以内。CR001阈值合理——正常数据不误报。')
    print('GAP: 缺乏二极管击穿故障案例——无法验证 true positive。需要在故障数据上测试。')
else:
    fired_switches = [s for s, d in per_switch.items() if d['fired_1.8'] > 0]
    print('有 %d 条曲线超过 x1.8阈值，涉及道岔: %s' % (grand_fired, ', '.join(fired_switches)))
    print('需逐条审查是否为检测误差或真实异常。')
