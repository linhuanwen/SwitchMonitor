#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
基于固定偏移0x32E8提取完整的3相电流曲线。
结构: tag → +0x32E8 → 曲线1 → 曲线2 → 曲线3
验证"phase_count = 3"是否意味着每个tag后有3条曲线。
"""
import struct, os, sys, json
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_curves_strict(raw, base_offset, min_len=50, max_len=600, min_peak=0.3, max_peak=10.0):
    """严格曲线提取，返回(曲线数据, 结束位置)"""
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
                            'end_off': i * 4,
                            'len': len(vals),
                            'peak': round(peak, 4),
                            'values': [round(v, 6) for v in vals],
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

    # ── 阶段2: 从固定偏移提取所有3条曲线 ──
    print("\n阶段2: 从每个标签+0x32E8处提取3条曲线")
    DATA_OFFSET = 0x32E8  # 固定偏移
    MAX_SEARCH = 0x6000   # 搜索范围足够放3条曲线+padding

    all_events = []  # 每个元素: {tag_idx, curves: [{phase, peak, len, values}]}

    for ti, tp in enumerate(tag_positions):
        data_start = tp + DATA_OFFSET
        if data_start + MAX_SEARCH > size:
            continue

        chunk = read_at(CURRENT_FILE, data_start, MAX_SEARCH)
        curves = find_curves_strict(chunk, data_start, min_len=100, max_len=600, min_peak=0.3, max_peak=10.0)

        if curves:
            # 按顺序取前3条
            event_curves = curves[:3]
            if len(event_curves) >= 1:
                all_events.append({
                    'tag_idx': ti,
                    'tag_pos': tp,
                    'data_start': data_start,
                    'n_curves_found': len(event_curves),
                    'curves': [{
                        'phase': ['A', 'B', 'C'][ci] if ci < 3 else f'X{ci}',
                        'file_off': c['local_off'] + data_start,
                        'len': c['len'],
                        'peak': c['peak'],
                        'values': c['values'],
                    } for ci, c in enumerate(event_curves)],
                })

    print(f"提取到 {len(all_events)} 个事件（至少1条曲线的标签）")

    if not all_events:
        print("未提取到事件!")
        return

    # ── 阶段3: 统计每个事件的曲线数 ──
    print("\n阶段3: 每个事件的曲线数分布")
    n_dist = Counter(e['n_curves_found'] for e in all_events)
    for n, cnt in sorted(n_dist.items()):
        print(f"  {n} 条/事件: {cnt} 个事件")

    # ── 阶段4: 检查DATA_OFFSET的准确性 ──
    print("\n阶段4: 验证第一个曲线到tag的偏移")
    offsets_from_tag = []
    for e in all_events[:100]:
        if e['curves']:
            off = e['curves'][0]['file_off'] - e['tag_pos']
            offsets_from_tag.append(off)

    off_dist = Counter(offsets_from_tag)
    print(f"tag→第一条曲线偏移分布 (前10):")
    for off, cnt in off_dist.most_common(10):
        print(f"  0x{off:05X} ({off}): {cnt} 次")

    # ── 阶段5: 显示几个完整事件 ──
    print("\n阶段5: 3条曲线的事件样本")
    triple_events = [e for e in all_events if e['n_curves_found'] == 3]
    print(f"完整3相事件: {len(triple_events)}")

    for e in triple_events[:5]:
        print(f"\n事件 tag[{e['tag_idx']}] @ 0x{e['tag_pos']:08X}:")
        for c in e['curves']:
            print(f"  相{c['phase']}: len={c['len']}, peak={c['peak']:.2f}A, "
                  f"@ 0x{c['file_off']:08X} (+0x{c['file_off']-e['tag_pos']:05X})")
            print(f"    first5={c['values'][:5]}")

    # ── 阶段6: 与F9记录对比 ──
    print(f"\n\n{'='*80}")
    print("阶段6: 数量关系")
    print("="*80)

    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)
    pos = data_2mb.find(b'1-J')
    fields = struct.unpack_from('<13I', data_2mb, pos, 0x70)
    f9, f6 = fields[9], fields[6]

    raw = read_at(CURRENT_FILE, f9, f6)
    marker_3277 = b'\x77\x32'
    n_f9 = sum(1 for i in range(f6//32) if raw[i*32+13:i*32+15] == marker_3277)

    print(f"F9 0x3277记录: {n_f9}")
    print(f"curve_info标签(有数据): {len(all_events)}")
    print(f"完整3相事件: {len(triple_events)}")
    print(f"如果F9记录=事件: {n_f9} 预期事件, {len(all_events)} 实际事件")
    print(f"比例: {len(all_events)/n_f9:.3f}")

    # ── 阶段7: 检查u32[0]和tag_idx的关系 ──
    print(f"\n{'='*80}")
    print("阶段7: 检查F9记录的u32[0]序列号与tag索引的关系")
    print("="*80)

    records = []
    for i in range(f6//32):
        rec = raw[i*32:(i+1)*32]
        if rec[13:15] == marker_3277:
            u32s = struct.unpack_from('<8I', rec, 0)
            records.append({
                'idx': i,
                'u32_0': u32s[0], 'u32_1': u32s[1], 'u32_2': u32s[2],
            })

    # u32[0] ÷ 0x100 应该 = 0-509 的索引
    min_u0 = min(r['u32_0'] for r in records)
    indices = [(r['u32_0'] - min_u0) // 0x100 for r in records]
    print(f"u32[0]范围: 0x{min_u0:08X} - 0x{max(r['u32_0'] for r in records):08X}")
    print(f"推导索引范围: {min(indices)} - {max(indices)}")
    print(f"索引连续: {indices == list(range(len(indices)))}")

    # ── 阶段8: 保存结果 ──
    print(f"\n{'='*80}")
    print("阶段8: 保存结果")
    print("="*80)

    out = {
        'meta': {
            'source': CURRENT_FILE,
            'total_tags': len(tag_positions),
            'events_with_data': len(all_events),
            'triple_phase_events': len(triple_events),
            'f9_records': n_f9,
            'data_offset_from_tag': f'0x{DATA_OFFSET:04X}',
        },
        'events': [{
            'tag_idx': e['tag_idx'],
            'tag_pos': e['tag_pos'],
            'curves': e['curves'],
        } for e in all_events],
    }

    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_3phase_events.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False)

    size_mb = os.path.getsize(out_path) / 1024 / 1024
    print(f"保存到: {out_path} ({size_mb:.1f} MB)")

    print("\n完成!")

if __name__ == '__main__':
    main()
