#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
严格曲线提取：排除校准padding (0.59, 1.65等重复模式)，
只保留真正的有上升沿+峰值+下降沿的电流曲线。
已知：曲线数据在tag + 0x32E8处开始，3条曲线间距0x1028。
"""
import struct, os, sys, json
from collections import Counter

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def is_calibration_only(values):
    """检查是否全部是校准padding（没有真正的曲线峰）"""
    if len(values) < 20:
        return True
    peak = max(abs(v) for v in values)
    # 如果峰值太低，全是padding
    if peak < 0.8:
        return True
    # 校准padding的特征：所有值都在[0.3, 2.0]之间，没有突出的峰
    avg = sum(abs(v) for v in values) / len(values)
    if peak < avg * 1.8:
        return True  # 没有明显的峰
    return False

def is_valid_curve(values, min_len=80, max_len=500):
    """验证曲线形状：基线→上升→峰值→下降→基线"""
    if len(values) < min_len or len(values) > max_len:
        return False

    if is_calibration_only(values):
        return False

    peak = max(values)
    peak_idx = values.index(peak)
    if peak < 1.0 or peak > 10.0:
        return False

    # 峰值至少离开两端2个采样点（电流曲线上升沿很陡）
    if peak_idx < 2 or peak_idx > len(values) - 3:
        return False

    # 基线：前5个和后5个值的平均
    baseline_start = sum(values[:5]) / 5
    baseline_end = sum(values[-5:]) / 5

    # 峰值必须明显高于两端基线
    if peak < baseline_start * 2.0 and peak < baseline_end * 2.0:
        return False

    # 有上升趋势：峰值后的值总体低于峰值前→后的中间段
    # 主要检查：峰值不是孤立的尖刺
    if peak_idx >= 2:
        pre_peak_val = values[peak_idx - 1]
        if pre_peak_val < baseline_start * 1.3:
            return False  # 峰值前没有上升

    return True

def extract_curve_at(raw, start_search, base_offset):
    """在raw中从start_search开始严格提取一条曲线"""
    n_f = len(raw) // 4
    if start_search // 4 >= n_f:
        return None
    f32 = list(struct.unpack_from(f'<{n_f}f', raw, 0))

    # 跳到第一个非零/非padding值
    i = start_search // 4
    while i < n_f and abs(f32[i]) < 0.02:
        i += 1

    if i >= n_f:
        return None

    s = i
    vals = []
    while i < n_f and abs(f32[i]) > 0.005:
        vals.append(f32[i])
        i += 1

    if is_valid_curve(vals):
        peak = max(abs(v) for v in vals)
        return {
            'local_off': s * 4,
            'file_off': base_offset + s * 4,
            'len': len(vals),
            'peak': round(peak, 4),
            'values': [round(v, 6) for v in vals],
        }
    return None


def main():
    size = os.path.getsize(CURRENT_FILE)

    # ── 找标签 ──
    print("找curve_info标签...")
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

    # ── 严格提取 ──
    # 3条曲线的偏移: 0x32E8, 0x4310, 0x5338
    PHASE_OFFSETS = [0x32E8, 0x4310, 0x5338]
    PHASE_NAMES = ['A', 'B', 'C']
    SEARCH_WINDOW = 0x1800  # 每个曲线搜索窗口6KB

    all_events = []
    total_curves = 0

    for ti, tp in enumerate(tag_positions):
        event_curves = []

        for ci, base_off in enumerate(PHASE_OFFSETS):
            search_start = tp + base_off
            if search_start + SEARCH_WINDOW > size:
                continue

            chunk = read_at(CURRENT_FILE, search_start, SEARCH_WINDOW)
            c = extract_curve_at(chunk, 0, search_start)
            if c:
                c['phase'] = PHASE_NAMES[ci]
                event_curves.append(c)
                total_curves += 1

        if event_curves:
            all_events.append({
                'tag_idx': ti,
                'tag_pos': tp,
                'n_curves': len(event_curves),
                'curves': event_curves,
            })

    print(f"严格提取结果:")
    print(f"  有曲线的事件: {len(all_events)}")
    print(f"  总曲线数: {total_curves}")

    n_dist = Counter(e['n_curves'] for e in all_events)
    for n, cnt in sorted(n_dist.items()):
        print(f"  {n} 条/事件: {cnt}")

    triple = [e for e in all_events if e['n_curves'] == 3]
    print(f"  完整3相事件: {len(triple)}")

    # ── 显示3相事件样本 ──
    print(f"\n完整3相事件样本:")
    for e in triple[:10]:
        print(f"\n  tag[{e['tag_idx']}] @ 0x{e['tag_pos']:08X}:")
        for c in e['curves']:
            rel_off = c['file_off'] - e['tag_pos']
            print(f"    相{c['phase']}: len={c['len']}, peak={c['peak']:.2f}A, "
                  f"@ +0x{rel_off:05X}")
            print(f"      first10={c['values'][:10]}")
            print(f"      last10={c['values'][-10:]}")

    # ── 显示非3相的事件样本 ──
    partial = [e for e in all_events if e['n_curves'] < 3]
    if partial:
        print(f"\n部分事件样本 (前10):")
        for e in partial[:10]:
            phases = ','.join(c['phase'] for c in e['curves'])
            peaks = ','.join(f"{c['peak']:.2f}A" for c in e['curves'])
            print(f"  tag[{e['tag_idx']}] @ 0x{e['tag_pos']:08X}: "
                  f"phases=[{phases}], peaks=[{peaks}]")

    # ── 数量分析 ──
    print(f"\n\n{'='*80}")
    print("数量分析")
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
    print(f"严格曲线事件: {len(all_events)}")
    print(f"完整3相: {len(triple)}")
    print(f"总曲线: {total_curves}")
    print(f"曲线/事件(平均): {total_curves/len(all_events):.2f}" if all_events else "N/A")
    print(f"如果510事件×3相: {n_f9 * 3}")

    # ── 保存 ──
    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_strict.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump({
            'meta': {
                'source': CURRENT_FILE,
                'total_tags': len(tag_positions),
                'events_with_data': len(all_events),
                'triple_phase': len(triple),
                'total_curves': total_curves,
                'f9_records': n_f9,
            },
            'events': [{
                'tag_idx': e['tag_idx'],
                'tag_pos': e['tag_pos'],
                'curves': e['curves'],
            } for e in all_events],
        }, f, ensure_ascii=False)

    print(f"\n保存到: {out_path} ({os.path.getsize(out_path)/1024/1024:.1f} MB)")
    print("完成!")

if __name__ == '__main__':
    main()
