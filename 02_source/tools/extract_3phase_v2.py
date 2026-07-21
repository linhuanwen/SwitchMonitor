#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
基于固定曲线间距0x1028提取完整的3相电流曲线。
修复：扩大搜索范围到0x8000，正确计算3条曲线的位置。
相间间距：0x1028 = 4136 bytes
"""
import struct, os, sys, json
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_curve_at(raw, start_search, base_offset):
    """在raw中从start_search位置开始找一条曲线"""
    n_f = len(raw) // 4
    if start_search // 4 >= n_f:
        return None
    f32 = struct.unpack_from(f'<{n_f}f', raw, 0)
    i = start_search // 4

    # 跳到第一个非零值
    while i < n_f and abs(f32[i]) < 0.02:
        i += 1

    if i >= n_f:
        return None

    s = i
    vals = []
    while i < n_f and abs(f32[i]) > 0.005:
        vals.append(f32[i])
        i += 1

    if 50 <= len(vals) <= 600:
        peak = max(abs(v) for v in vals)
        if 0.3 <= peak <= 10.0:
            pi = vals.index(max(vals, key=abs))
            if 5 <= pi <= len(vals) - 5:
                return {
                    'local_off': s * 4,
                    'end_off': i * 4,
                    'len': len(vals),
                    'peak': round(peak, 4),
                    'values': [round(v, 6) for v in vals],
                }
    return None


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

    # ── 阶段2: 提取3条曲线（间距0x1028，范围0x8000） ──
    print("\n阶段2: 提取3条曲线（间距0x1028，每个标签搜索0x8000字节）")

    PHASE_GAP = 0x1028  # 曲线间距
    SEARCH_RANGE = 0x8000  # 总搜索范围
    DATA_OFFSETS = [0x32E8, 0x32E8 + PHASE_GAP, 0x32E8 + 2*PHASE_GAP]  # A, B, C

    all_events = []
    empty_count = 0

    for ti, tp in enumerate(tag_positions):
        data_start = tp + DATA_OFFSETS[0]
        if data_start + SEARCH_RANGE > size:
            continue

        chunk = read_at(CURRENT_FILE, data_start, SEARCH_RANGE)

        curves = []
        for ci, off in enumerate(DATA_OFFSETS):
            # 每个曲线起始位置相对于data_start
            local_start = off - DATA_OFFSETS[0]
            c = find_curve_at(chunk, local_start, data_start)
            if c:
                curves.append({
                    'phase': ['A', 'B', 'C'][ci],
                    'file_off': data_start + c['local_off'],
                    'len': c['len'],
                    'peak': c['peak'],
                    'values': c['values'],
                })

        if curves:
            all_events.append({
                'tag_idx': ti,
                'tag_pos': tp,
                'data_start': data_start,
                'n_curves': len(curves),
                'curves': curves,
            })
        else:
            empty_count += 1

    print(f"提取到 {len(all_events)} 个事件（至少1条曲线）")
    print(f"无线索的标签: {empty_count}")

    # ── 阶段3: 统计每个事件的曲线数 ──
    print("\n阶段3: 每个事件的曲线数分布")
    n_dist = Counter(e['n_curves'] for e in all_events)
    for n, cnt in sorted(n_dist.items()):
        print(f"  {n} 条/事件: {cnt} 个事件")

    # 检查那些只有1-2条曲线的事件——可能偏移不同
    incomplete = [e for e in all_events if e['n_curves'] < 3]
    if incomplete:
        print(f"\n  不完整事件样本 (前10个):")
        for e in incomplete[:10]:
            offs = [c['file_off'] - e['tag_pos'] for c in e['curves']]
            peaks = [c['peak'] for c in e['curves']]
            print(f"  tag[{e['tag_idx']}] @ 0x{e['tag_pos']:08X}: "
                  f"n={e['n_curves']}, offsets={[f'0x{o:05X}' for o in offs]}, peaks={peaks}")

    # ── 阶段4: 显示完整3相事件 ──
    print("\n阶段4: 完整3相事件样本")
    triple = [e for e in all_events if e['n_curves'] == 3]
    print(f"完整3相事件: {len(triple)}")

    for e in triple[:5]:
        print(f"\n事件 tag[{e['tag_idx']}] @ 0x{e['tag_pos']:08X}:")
        for c in e['curves']:
            rel_off = c['file_off'] - e['tag_pos']
            print(f"  相{c['phase']}: len={c['len']}, peak={c['peak']:.2f}A, "
                  f"@ +0x{rel_off:05X}")

    # ── 阶段5: 与F9记录对比 ──
    print(f"\n\n{'='*80}")
    print("阶段5: 数量关系对比")
    print("="*80)

    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)
    pos = data_2mb.find(b'1-J')
    fields = struct.unpack_from('<13I', data_2mb, pos)
    f9, f6 = fields[9], fields[6]

    raw = read_at(CURRENT_FILE, f9, f6)
    marker_3277 = b'\x77\x32'
    n_f9 = sum(1 for i in range(f6//32) if raw[i*32+13:i*32+15] == marker_3277)

    print(f"F9 0x3277记录: {n_f9}")
    print(f"有曲线的事件: {len(all_events)}")
    print(f"完整3相事件: {len(triple)}")
    print(f"预期曲线数(510×3): {n_f9 * 3}")
    print(f"实际曲线数: {sum(e['n_curves'] for e in all_events)}")
    print(f"事件/F9记录: {len(all_events)/n_f9:.2f}")

    # ── 阶段6: F9记录的u32[0] vs tag索引 ──
    print(f"\n{'='*80}")
    print("阶段6: F9记录与tag映射")
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

    min_u0 = min(r['u32_0'] for r in records)
    indices = [(r['u32_0'] - min_u0) // 0x100 for r in records]
    print(f"F9 u32[0]推导索引: {min(indices)} - {max(indices)} (连续: {indices == list(range(510))})")

    # 用u32[0]作为tag索引查找对应事件
    matched = 0
    total_test = 0
    for r in records[:10]:
        ti = (r['u32_0'] - min_u0) // 0x100
        event = next((e for e in all_events if e['tag_idx'] == ti), None)
        total_test += 1
        if event:
            matched += 1
            print(f"  F9[{r['idx']}] u32_0=0x{r['u32_0']:08X} → tag[{ti}] → {event['n_curves']} curves ✓")
        else:
            print(f"  F9[{r['idx']}] u32_0=0x{r['u32_0']:08X} → tag[{ti}] → 未找到 ✗")
    print(f"  前10匹配率: {matched}/{total_test}")

    # ── 阶段7: 保存 ──
    print(f"\n{'='*80}")
    print("阶段7: 保存结果")
    print("="*80)

    out = {
        'meta': {
            'source': CURRENT_FILE,
            'total_tags': len(tag_positions),
            'events_with_data': len(all_events),
            'triple_phase_events': len(triple),
            'total_curves': sum(e['n_curves'] for e in all_events),
            'f9_records': n_f9,
            'phase_gap': f'0x{PHASE_GAP:X}',
            'max_search_range': f'0x{SEARCH_RANGE:X}',
        },
        'events': [{
            'tag_idx': e['tag_idx'],
            'tag_pos': e['tag_pos'],
            'n_curves': e['n_curves'],
            'curves': e['curves'],
        } for e in all_events],
    }

    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_3phase_v2.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False)

    print(f"保存到: {out_path} ({os.path.getsize(out_path)/1024/1024:.1f} MB)")
    print("\n完成!")


if __name__ == '__main__':
    main()
