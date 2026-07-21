#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成数据质量最终报告和可视化"""
import json
import os
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

JSON_PATH = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\power_curves_2hbf_final.json"

def main():
    with open(JSON_PATH, 'r', encoding='utf-8') as f:
        data = json.load(f)

    curves = data['curves']

    print("=" * 70)
    print("HBF功率曲线提取 - 最终数据质量报告")
    print("=" * 70)

    # 基础统计
    total = sum(len(v) for v in curves.values())
    print(f"\nFinal dataset: {len(curves)} switches, {total} curves")

    # 分组统计
    groups = {
        'Down throat J (下行咽喉-尖轨)': [],
        'Down throat X (下行咽喉-心轨)': [],
        'Up throat J (上行咽喉-尖轨)': [],
        'Up throat X (上行咽喉-心轨)': [],
    }

    all_ids = []
    down_throat_odd = [1, 3, 7, 11, 17, 19, 21]  # odd = down throat
    up_throat_even = [2, 4, 6, 8]  # even = up throat

    for sw_id, sw_curves in curves.items():
        num, typ = sw_id.split('-')
        num = int(num)
        n = len(sw_curves)

        if num in down_throat_odd:
            if typ == 'J':
                groups['Down throat J (下行咽喉-尖轨)'].append((sw_id, n))
            else:
                groups['Down throat X (下行咽喉-心轨)'].append((sw_id, n))
        elif num in up_throat_even:
            if typ == 'J':
                groups['Up throat J (上行咽喉-尖轨)'].append((sw_id, n))
            else:
                groups['Up throat X (上行咽喉-心轨)'].append((sw_id, n))
        all_ids.append(sw_id)

    for group_name, items in groups.items():
        if not items:
            continue
        total_n = sum(n for _, n in items)
        items_str = ', '.join(f'{sw}({n})' for sw, n in items)
        print(f"\n  {group_name}: {len(items)} switches, {total_n} curves")
        print(f"    {items_str}")

    # 曲线质量统计
    print(f"\n{'='*70}")
    print("曲线质量统计")
    print(f"{'='*70}")

    all_lengths = []
    all_peaks = []
    for sw_curves in curves.values():
        for c in sw_curves:
            all_lengths.append(c['sample_count'])
            all_peaks.append(c['peak_kw'])

    print(f"  采样点数: min={min(all_lengths)}, max={max(all_lengths)}, "
          f"mean={sum(all_lengths)//len(all_lengths)}")
    print(f"  峰值功率: min={min(all_peaks):.2f}KW, max={max(all_peaks):.2f}KW, "
          f"mean={sum(all_peaks)/len(all_peaks):.2f}KW")

    # 峰值分布
    peak_bins = [(0.2, 0.5), (0.5, 1.0), (1.0, 1.5), (1.5, 2.0), (2.0, 2.5),
                 (2.5, 3.0), (3.0, 3.5), (3.5, 4.0)]
    print(f"\n  峰值分布:")
    for lo, hi in peak_bins:
        count = sum(1 for p in all_peaks if lo <= p < hi)
        bar = '#' * (count * 80 // max(1, len(all_peaks)))
        print(f"    {lo:.1f}-{hi:.1f}KW: {count:4d} ({count*100/len(all_peaks):5.1f}%) {bar}")

    # 曲线数量分布
    print(f"\n  每道岔曲线数分布:")
    curve_counts = [len(v) for v in curves.values()]
    from collections import Counter
    count_dist = Counter(curve_counts)
    for cnt, freq in sorted(count_dist.items()):
        switches_with_cnt = [sw for sw, cs in curves.items() if len(cs) == cnt]
        print(f"    {cnt:3d} curves: {freq} switch(es) — {', '.join(sorted(switches_with_cnt))}")

    # 缺失和重复总结
    print(f"\n{'='*70}")
    print("缺失与重复总结")
    print(f"{'='*70}")

    print(f"""
  Missing (5 switches):
    13-J: F9 stores event index records (u32 structs), not float32 samples
    13-X: F9 all zeros
    15-J: F9 has non-zero data but not power curve format (integer metadata)
    15-X: F9 all zeros
    8-J:  F9 all zeros

  Duplicates removed (6 pairs):
    9-J == 7-J  (same F9 pointer)
    5-J ~= 3-J  (shifted by 1 event block, 99.3% identical)
    5-X ~= 3-X  (shifted by 1 event block, 98.7% identical)
    9-X ~= 7-X  (shifted by 1 event block, 99.4% identical)
    19-J ~= 17-J (shifted by 1 event block, 98.8% identical)
    4-X ~= 4-J  (same switch #4, J and X share data, 97.6% identical)

  Event block structure confirmed:
    Block size = 0x1227 = 4647 bytes
    Each block: [header ~100B] + [float32 power curve samples]
    Curve spacing = 4647 float32 values between consecutive curves
    5125 spacing = 4647 + extra zero padding
    51595 spacing = ~11 empty event blocks
""")

    print(f"\nOutput files:")
    print(f"  JSON: {JSON_PATH}")
    print(f"  CSV:  .../power_curves_summary_final.csv")
    print("Done!")


if __name__ == '__main__':
    main()
