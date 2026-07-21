#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 v2 数据中去除重复道岔，生成最终干净数据集

重复对 (经过验证):
  3-J ≈ 5-J (99.3%) → 保留 3-J
  3-X ≈ 5-X (98.7%) → 保留 3-X
  7-J = 9-J (同一F9)  → 保留 7-J (已在v2移除)
  7-X ≈ 9-X (99.4%) → 保留 7-X
  17-J ≈ 19-J (98.8%) → 保留 17-J
  4-J ≈ 4-X (97.6%) → 保留 4-J
"""
import json
import os
from datetime import datetime

INPUT_PATH = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\power_curves_2hbf_v2.json"
OUTPUT_PATH = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\power_curves_2hbf_final.json"
CSV_PATH = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\power_curves_summary_final.csv"

# 重复道岔 (移除 → 保留)
DUPLICATE_MAP = {
    '5-J': '3-J',
    '5-X': '3-X',
    '9-J': '7-J',   # 已在v2移除
    '9-X': '7-X',
    '19-J': '17-J',
    '4-X': '4-J',
}

def sort_key(sid):
    parts = sid.split('-')
    return (int(parts[0]), parts[1])

def main():
    with open(INPUT_PATH, 'r', encoding='utf-8') as f:
        data = json.load(f)

    all_curves = data['curves']

    print("="*70)
    print("去重处理")
    print("="*70)

    removed = {}
    for dup_id, keep_id in DUPLICATE_MAP.items():
        if dup_id in all_curves:
            n_dup = len(all_curves[dup_id])
            n_keep = len(all_curves.get(keep_id, []))
            removed[dup_id] = {'kept_by': keep_id, 'curves_removed': n_dup}
            del all_curves[dup_id]
            print(f"  移除 {dup_id} ({n_dup}条) → 保留 {keep_id} ({n_keep}条)")
        elif dup_id == '9-J':
            print(f"  9-J 已在 v2 移除 (共享 7-J 的 F9)")

    # 保存
    total_curves = sum(len(v) for v in all_curves.values())

    summary = {
        'source_file': '功率/2.hbf',
        'extraction_time': datetime.now().isoformat(),
        'version': 'final — 去重+修正',
        'fixes_applied': [
            '移除共享F9的重复: 9-J→7-J',
            '修复第一条伪曲线 (start=0, 非零首段)',
            '拆分超长合并曲线 (查找20+连续零点)',
            '移除跨开关重复对: 5-J→3-J, 5-X→3-X, 9-X→7-X, 19-J→17-J, 4-X→4-J',
        ],
        'duplicates_removed': removed,
        'total_unique_switches': len(all_curves),
        'total_unique_curves': total_curves,
        'missing_switches': {
            '13-J': 'F9存储事件索引记录(非float32)',
            '13-X': 'F9全为零',
            '15-J': 'F9有非零但非功率曲线格式',
            '15-X': 'F9全为零',
            '8-J': 'F9全为零',
        },
        'curves': all_curves,
    }

    with open(OUTPUT_PATH, 'w', encoding='utf-8') as fout:
        json.dump(summary, fout, ensure_ascii=False, indent=2)

    # CSV
    with open(CSV_PATH, 'w', encoding='utf-8') as fcsv:
        fcsv.write("switch_id,curve_index,file_offset,float32_offset,sample_count,peak_kw,peak_index\n")
        for sw_id in sorted(all_curves.keys(), key=sort_key):
            for c in all_curves[sw_id]:
                fcsv.write(f"{sw_id},{c['curve_index']},{c['file_offset']},{c['float32_offset']},"
                          f"{c['sample_count']},{c['peak_kw']},{c['peak_index']}\n")

    # ========================================
    # 最终报告
    # ========================================
    print(f"\n{'='*70}")
    print(f"最终数据集")
    print(f"{'='*70}")
    print(f"  文件: {OUTPUT_PATH}")
    print(f"  独立道岔: {len(all_curves)}/30")
    print(f"  总曲线数: {total_curves}")
    print(f"  移除重复: {len(removed)} 个道岔")
    print(f"  缺失(无数据): 5 个道岔")

    print(f"\n{'─'*70}")
    print(f"{'道岔':8s} {'曲线数':>6s} {'长度范围':>14s} {'峰值范围':>18s} {'说明'}")
    print(f"{'─'*70}")

    ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
               '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
               '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

    for sw_id in ALL_IDS:
        if sw_id in all_curves:
            curves = all_curves[sw_id]
            lengths = [c['sample_count'] for c in curves]
            peaks = [c['peak_kw'] for c in curves]
            note = ""
            # 检查是否保留了其他开关的数据
            for dup, keep in DUPLICATE_MAP.items():
                if keep == sw_id:
                    note = f"(含{dup}数据)"
                    break
            print(f"  {sw_id:6s} {len(curves):6d}  {min(lengths):4d}-{max(lengths):4d}      "
                  f"{min(peaks):5.2f}-{max(peaks):5.2f} KW  {note}")
        elif sw_id in removed:
            keep_id = removed[sw_id]['kept_by']
            print(f"  {sw_id:6s} {'─':>6s}  {'─':>14s}  {'─':>18s}  重复→{keep_id}")
        else:
            reason = summary['missing_switches'].get(sw_id, '未知')
            print(f"  {sw_id:6s} {'─':>6s}  {'─':>14s}  {'─':>18s}  缺失:{reason[:30]}")

    print(f"{'─'*70}")
    print(f"  合计: {len(all_curves)} 个独立道岔, {total_curves} 条功率曲线")
    print(f"\n数据质量评注:")
    print(f"  - 所有曲线均经过形态验证: 零->峰值->零/低稳态")
    print(f"  - 6对跨开关重复已去重 (保留低编号侧)")
    print(f"  - 长曲线(>500点)已通过内部零点检测拆分")
    print(f"  - 缺失开关的F9数据为事件索引记录(非采样数据)或全为零")
    print(f"  - 6/8号开关曲线数较少(~50条), 可能为手动/维护操作记录")

    print(f"\nCSV: {CSV_PATH}")
    print("完成!")


if __name__ == '__main__':
    main()
