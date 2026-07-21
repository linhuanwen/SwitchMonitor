#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
最终版: 灵活的3相电流曲线提取。
策略: 对每个curve_info标签，在+0x2000到+0x7000范围扫描所有曲线段，
然后按~0x1028间距分组为3相事件。
"""
import struct, os, sys, json
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def scan_curves_in_range(raw, base_offset, min_len=50, max_len=600, min_peak=0.3, max_peak=10.0):
    """扫描整个raw区域，返回所有有效的曲线段"""
    n_f = len(raw) // 4
    if n_f == 0:
        return []
    f32 = struct.unpack_from(f'<{n_f}f', raw, 0)
    curves = []
    i = 0
    while i < n_f:
        if abs(f32[i]) > 0.03:
            s = i
            vals = []
            while i < n_f and abs(f32[i]) > 0.005:
                vals.append(f32[i])
                i += 1
            if min_len <= len(vals) <= max_len:
                peak = max(abs(v) for v in vals)
                if min_peak <= peak <= max_peak:
                    pi = vals.index(max(vals, key=abs))
                    if 5 <= pi <= len(vals) - 5:
                        curves.append({
                            'local_off': s * 4,
                            'file_off': base_offset + s * 4,
                            'len': len(vals),
                            'peak': round(peak, 4),
                        })
            i += 1
        else:
            i += 1
    return curves


def main():
    size = os.path.getsize(CURRENT_FILE)

    # ── 阶段1: 找所有curve_info标签 ──
    print("阶段1: 定位curve_info标签")
    tag = 'curve_info'.encode('utf-16-le')
    tag_positions = []

    for base in range(0, size, 0x100000):
        chunk = read_at(CURRENT_FILE, base, 0x100000 + 200)
        if len(chunk) < len(tag):
            break
        pos = 0
        while True:
            found = chunk.find(tag, pos)
            if found == -1:
                break
            tag_positions.append(base + found)
            pos = found + 100

    print(f"找到 {len(tag_positions)} 个标签")

    # ── 阶段2: 从每个标签+0x2000到+0x7000扫描曲线 ──
    print("\n阶段2: 从每个标签+0x2000到+0x7000扫描曲线")
    SCAN_START = 0x2000
    SCAN_SIZE = 0x5000  # 20KB

    phase_labels = ['A', 'B', 'C']
    all_events = []
    empty_count = 0

    for ti, tp in enumerate(tag_positions):
        scan_start = tp + SCAN_START
        if scan_start + SCAN_SIZE > size:
            continue

        chunk = read_at(CURRENT_FILE, scan_start, SCAN_SIZE)
        curves = scan_curves_in_range(chunk, scan_start)

        if not curves:
            empty_count += 1
            continue

        # 按~0x1028间距分组为事件（一个tag范围内只能有一个事件）
        # 曲线间距约为0x1028 (4136 bytes)
        # 但执行过程中有padding变化，所以放宽到0x1000-0x1100
        phase_curves = []
        for c in curves:
            local_off = c['file_off'] - tp  # 相对于tag的偏移
            # 映射到ABC相: 最近的标准偏移
            if local_off < 0x2C00:
                phase_idx = 0  # ~0x22C0 → A
            elif local_off < 0x3C00:
                phase_idx = 0  # ~0x32E8 → A
            elif local_off < 0x4A00:
                phase_idx = 1  # ~0x4310 → B
            elif local_off < 0x5A00:
                phase_idx = 2  # ~0x5338 → C
            elif local_off < 0x6A00:
                phase_idx = 2  # ~0x6360 → C
            else:
                continue  # 超出范围

            # 如果这个相位已经有值，保留峰值更大的
            existing = next((pc for pc in phase_curves if pc['phase'] == phase_labels[phase_idx]), None)
            if existing:
                if c['peak'] > existing['peak']:
                    existing.update({
                        'local_off': c['local_off'],
                        'file_off': c['file_off'],
                        'len': c['len'],
                        'peak': c['peak'],
                    })
            else:
                phase_curves.append({
                    'phase': phase_labels[phase_idx],
                    'file_off': c['file_off'],
                    'len': c['len'],
                    'peak': c['peak'],
                })

        if phase_curves:
            all_events.append({
                'tag_idx': ti,
                'tag_pos': tp,
                'n_curves': len(phase_curves),
                'curves': sorted(phase_curves, key=lambda x: x['phase']),
            })

    print(f"有曲线的事件: {len(all_events)}")
    print(f"无线索的标签: {empty_count}")

    # ── 阶段3: 统计 ──
    print("\n阶段3: 分布统计")
    n_dist = Counter(e['n_curves'] for e in all_events)
    for n, cnt in sorted(n_dist.items()):
        print(f"  {n} 条/事件: {cnt} 个事件")

    triple = [e for e in all_events if e['n_curves'] == 3]
    print(f"  完整3相事件: {len(triple)}")
    print(f"  总曲线数: {sum(e['n_curves'] for e in all_events)}")

    # ── 阶段4: 显示完整3相事件样本 ──
    print("\n阶段4: 完整3相事件样本")
    for e in triple[:5]:
        print(f"\ntag[{e['tag_idx']}] @ 0x{e['tag_pos']:08X}:")
        for c in e['curves']:
            rel_off = c['file_off'] - e['tag_pos']
            print(f"  相{c['phase']}: len={c['len']}, peak={c['peak']:.2f}A, @ +0x{rel_off:05X}")

    # 显示1-2条曲线的事件样本
    print("\n\n不完整事件样本:")
    partial = [e for e in all_events if e['n_curves'] < 3]
    for e in partial[:8]:
        phases = ','.join(c['phase'] for c in e['curves'])
        rel_offs = ','.join(f"0x{c['file_off']-e['tag_pos']:05X}" for c in e['curves'])
        print(f"  tag[{e['tag_idx']}] @ 0x{e['tag_pos']:08X}: "
              f"phases=[{phases}], offsets=[{rel_offs}]")

    # ── 阶段5: F9记录 ──
    print(f"\n\n{'='*80}")
    print("阶段5: F9记录")
    print("="*80)

    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)
    pos = data_2mb.find(b'1-J')
    block = data_2mb[pos:pos+256]
    fields = struct.unpack_from('<13I', block, 0x70)
    f9, f6 = fields[9], fields[6]

    raw_f9 = read_at(CURRENT_FILE, f9, f6)
    marker_3277 = b'\x77\x32'
    n_f9 = sum(1 for i in range(f6//32) if raw_f9[i*32+13:i*32+15] == marker_3277)
    print(f"F9 0x3277记录: {n_f9}")

    if n_f9 > 0:
        records = []
        for i in range(f6//32):
            rec = raw_f9[i*32:(i+1)*32]
            if rec[13:15] == marker_3277:
                u32s = struct.unpack_from('<8I', rec, 0)
                records.append({
                    'idx': i,
                    'u32_0': u32s[0], 'u32_1': u32s[1], 'u32_2': u32s[2],
                })

        min_u0 = min(r['u32_0'] for r in records)
        max_u0 = max(r['u32_0'] for r in records)
        print(f"u32[0] range: 0x{min_u0:08X} - 0x{max_u0:08X} (span={max_u0-min_u0})")
        print(f"事件数: {len(all_events)}")
        print(f"F9记录: {n_f9}")

        # 尝试映射: u32[0]归一化后作为tag索引
        indices = [(r['u32_0'] - min_u0) // 0x100 for r in records]
        n_in_range = sum(1 for idx in indices if 0 <= idx < len(tag_positions))
        print(f"u32[0]索引在tag范围内的: {n_in_range}/{len(records)}")

    # ── 阶段6: 带曲线值的保存（抽样以减小文件） ──
    print(f"\n{'='*80}")
    print("阶段6: 读取完整曲线值并保存")
    print("="*80)

    # 为3相事件读取完整的values
    for e in triple[:20]:  # 只读前20个完整事件的详细值用于验证
        for c in e['curves']:
            data_start = c['file_off']
            chunk = read_at(CURRENT_FILE, data_start, c['len'] * 4 + 16)
            f32_vals = struct.unpack_from(f'<{c["len"]}f', chunk, 0)
            c['values'] = [round(v, 6) for v in f32_vals]

    # 完整保存
    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_events_final.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump({
            'meta': {
                'source': CURRENT_FILE,
                'total_tags': len(tag_positions),
                'events_with_data': len(all_events),
                'triple_phase': len(triple),
                'f9_records': n_f9,
            },
            'events': [{
                'tag_idx': e['tag_idx'],
                'tag_pos': e['tag_pos'],
                'n_curves': e['n_curves'],
                'curves': e['curves'],
            } for e in all_events],
        }, f, ensure_ascii=False)

    print(f"保存到: {out_path} ({os.path.getsize(out_path)/1024/1024:.1f} MB)")
    print("\n完成!")


if __name__ == '__main__':
    main()
