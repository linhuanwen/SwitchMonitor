#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从电流HBF提取三相电流曲线 v2 — 基于块结构，关联功率曲线
结构: ~1MB大块/道岔 → 每个事件3条曲线(4KB间隔) → UTF16LE头+float32采样
"""
import struct, os, sys, json
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu"
POWER_FILE = os.path.join(BASE, "道岔动作功率曲线", "2.hbf")
CURRENT_FILE = os.path.join(BASE, "道岔动作电流曲线", "2.hbf")

ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def get_dir_entries(filepath):
    with open(filepath, 'rb') as f:
        data = f.read(0x200000)
    entries = {}
    for sw_id in ALL_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        fields = struct.unpack_from('<13I', block, 0x70)
        if fields[7] > 10 and fields[7] < 100000:
            entries[sw_id] = {
                'F3': fields[3], 'F4': fields[4], 'F6': fields[6],
                'F7': fields[7], 'F9': fields[9],
            }
    return entries

def extract_curves_from_region(raw, base_offset, min_len=50, max_len=600, min_peak=0.3, max_peak=10.0):
    """在一个区域中提取所有电流曲线"""
    n_floats = len(raw) // 4
    f32 = struct.unpack_from(f'<{n_floats}f', raw, 0)

    curves = []
    i = 0
    while i < n_floats:
        if abs(f32[i]) > 0.03:  # 曲线开始
            seg_start = i
            seg_vals = []
            while i < n_floats and abs(f32[i]) > 0.005:
                seg_vals.append(f32[i])
                i += 1

            seg_len = len(seg_vals)
            if min_len <= seg_len <= max_len:
                peak = max(abs(v) for v in seg_vals)
                if min_peak <= peak <= max_peak:
                    peak_idx = seg_vals.index(max(seg_vals, key=abs))
                    if 5 <= peak_idx <= seg_len - 5:
                        curves.append({
                            'file_offset': base_offset + seg_start * 4,
                            'float32_offset': seg_start,
                            'length': seg_len,
                            'peak': round(peak, 4),
                            'avg': round(sum(abs(v) for v in seg_vals)/seg_len, 4),
                            'values': [round(v, 6) for v in seg_vals],
                        })
            i += 1
        else:
            i += 1
    return curves


def main():
    size_cur = os.path.getsize(CURRENT_FILE)

    # ── 阶段1: 获取目录项 ──
    print("阶段1: 目录项")
    pwr_entries = get_dir_entries(POWER_FILE)
    cur_entries = get_dir_entries(CURRENT_FILE)
    print(f"功率: {len(pwr_entries)} 开关, 电流: {len(cur_entries)} 开关")

    # 电流道岔按F9排序，相邻F9之间的gap就是该道岔的数据区
    sorted_cur = sorted(
        [(sw, e) for sw, e in cur_entries.items() if e['F9'] > 0],
        key=lambda x: x[1]['F9']
    )

    # ── 阶段2: 完整扫描电流HBF的高偏移区域 ──
    print("\n阶段2: 全量扫描电流曲线 (0x03000000-0x1F000000)")

    # 先收集所有曲线的起点位置
    all_starts = []
    scan_start = 0x03000000
    scan_end = min(size_cur, 0x1F000000)

    for base in range(scan_start, scan_end, 0x10000):
        chunk = read_at(CURRENT_FILE, base, 0x10000)
        nz = sum(1 for b in chunk[:0x8000] if b != 0)
        if nz < 100:
            continue

        curves = extract_curves_from_region(chunk, base)
        all_starts.extend(curves)

    print(f"  找到 {len(all_starts)} 条电流曲线")

    if not all_starts:
        print("  未找到任何曲线，退出")
        return

    all_starts.sort(key=lambda c: c['file_offset'])

    # ── 阶段3: 找大块边界 (gap > 0x100000 = 1MB) ──
    print("\n阶段3: 按大块分组")

    # 计算间隔
    gaps = []
    for i in range(1, len(all_starts)):
        gaps.append(all_starts[i]['file_offset'] - all_starts[i-1]['file_offset'])

    # 找大gap位置
    big_gap_threshold = 0x80000  # 512KB
    block_boundaries = [0]  # 第一个块的起始

    for i, gap in enumerate(gaps):
        if gap > big_gap_threshold:
            block_boundaries.append(i + 1)

    blocks = []
    for bidx in range(len(block_boundaries)):
        start_idx = block_boundaries[bidx]
        end_idx = block_boundaries[bidx + 1] if bidx + 1 < len(block_boundaries) else len(all_starts)
        block_curves = all_starts[start_idx:end_idx]
        if block_curves:
            blocks.append({
                'start_offset': block_curves[0]['file_offset'],
                'curve_count': len(block_curves),
                'curves': block_curves,
            })

    print(f"  大块数: {len(blocks)}")
    for bi, b in enumerate(blocks):
        print(f"    块{bi}: offset=0x{b['start_offset']:08X}, curves={b['curve_count']}")

    # ── 阶段4: 每个大块内按3条一组分组 ──
    print("\n阶段4: 块内按3条一组分组")

    all_events = []  # 每个元素: {block_idx, curves:[3], ...}

    for bi, block in enumerate(blocks):
        curves = block['curves']
        # 在块内，每3条曲线组成一个事件
        n_events = len(curves) // 3
        remainder = len(curves) % 3

        for ei in range(n_events):
            event_curves = curves[ei*3:(ei+1)*3]
            all_events.append({
                'block_idx': bi,
                'event_idx': ei,
                'start_offset': event_curves[0]['file_offset'],
                'curves': event_curves,
            })

        if remainder > 0:
            print(f"  块{bi}: {remainder} 条剩余曲线 (非3的倍数)")

    print(f"  总事件数: {len(all_events)}")

    # ── 阶段5: 尝试分配开关ID ──
    print("\n阶段5: 分配开关ID")

    # 策略: 大块按F9的顺序分配给道岔
    # 但块数(通过gap检测)可能不等于道岔数(30)
    # 尝试匹配
    switches_with_data = [sw for sw, e in sorted_cur]
    print(f"  有道岔数据的开关: {len(switches_with_data)}")
    print(f"  数据块数: {len(blocks)}")

    # 如果块数和开关数不同，可能需要合并或调整
    # 先直接按顺序分配
    switch_block_map = {}
    if len(blocks) <= len(switches_with_data):
        for i, sw in enumerate(switches_with_data[:len(blocks)]):
            switch_block_map[i] = sw
    else:
        # 块数多于开关数 — 可能是每个开关有多个块
        # 这里简化处理
        for i in range(len(blocks)):
            sw_idx = i % len(switches_with_data)
            switch_block_map[i] = switches_with_data[sw_idx]

    # 为每个事件分配开关
    for event in all_events:
        event['switch_id'] = switch_block_map.get(event['block_idx'], 'unknown')

    # ── 阶段6: 统计报告 ──
    print("\n阶段6: 统计报告")

    sw_event_counts = defaultdict(int)
    sw_curve_counts = defaultdict(int)
    for event in all_events:
        sw = event['switch_id']
        sw_event_counts[sw] += 1
        sw_curve_counts[sw] += len(event['curves'])

    print(f"\n{'Switch':<8} {'Events':>8} {'Curves':>8}")
    print("-"*30)
    for sw in sorted(sw_event_counts.keys()):
        print(f"{sw:<8} {sw_event_counts[sw]:>8} {sw_curve_counts[sw]:>8}")

    # 显示几个样本
    print(f"\n  前3个事件样本:")
    for event in all_events[:3]:
        sw = event['switch_id']
        print(f"  {sw} 事件#{event['event_idx']} @ 0x{event['start_offset']:08X}:")
        for ci, c in enumerate(event['curves']):
            phase = ['A', 'B', 'C'][ci]
            print(f"    相{phase}: peak={c['peak']:.2f}A, len={c['length']}, "
                  f"first5={c['values'][:5]}")

    # ── 阶段7: 关联功率曲线 ──
    print(f"\n阶段7: 加载功率曲线数据用于关联")

    power_json = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\power_curves_2hbf_final.json"
    with open(power_json, 'r', encoding='utf-8') as f:
        power_data = json.load(f)

    # 为每个开关统计功率曲线数
    pwr_curves = power_data.get('curves', power_data)  # handle both formats

    print(f"\n{'Switch':<8} {'Pwr_curves':>12} {'Cur_events':>12} {'Ratio':>10}")
    print("-"*50)
    for sw in sorted(sw_event_counts.keys()):
        pc = len(pwr_curves.get(sw, []))
        ce = sw_event_counts.get(sw, 0)
        ratio = ce / pc if pc > 0 else 0
        print(f"{sw:<8} {pc:>12} {ce:>12} {ratio:>10.3f}")

    # ── 阶段8: 保存 ──
    print(f"\n阶段8: 保存结果")

    # 构建输出 - 按开关组织
    output_curves = {}
    for sw in sw_event_counts:
        sw_events = [e for e in all_events if e['switch_id'] == sw]
        sw_events.sort(key=lambda e: e['start_offset'])
        output_curves[sw] = [{
            'offset': e['start_offset'],
            'curves': [{
                'phase': ['A', 'B', 'C'][ci],
                'peak': c['peak'],
                'length': c['length'],
                'values': c['values'],
            } for ci, c in enumerate(e['curves'])],
        } for e in sw_events]

    out = {
        'meta': {
            'source': CURRENT_FILE,
            'total_events': len(all_events),
            'total_curves': len(all_starts),
            'switches_with_data': sorted(sw_event_counts.keys()),
            'blocks': len(blocks),
            'switch_block_map': switch_block_map,
        },
        'curves': output_curves,
    }

    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_curves_v2.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False)

    size_mb = os.path.getsize(out_path) / 1024 / 1024
    print(f"  保存到: {out_path}")
    print(f"  大小: {size_mb:.1f} MB")

    print("\n完成!")


if __name__ == '__main__':
    main()
