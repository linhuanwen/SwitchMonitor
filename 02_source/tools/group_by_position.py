#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
新策略：不依赖标签位置假设。扫描整个文件的数据区，收集所有曲线，
然后按文件偏移位置分组：间距恰好0x1028的3条曲线=1个3相事件。
"""
import struct, os, sys, json
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def is_valid_curve(values):
    """验证曲线：有真正的电流峰值"""
    if len(values) < 80 or len(values) > 500:
        return False
    peak = max(values)
    peak_idx = values.index(peak)
    if peak < 1.0 or peak > 10.0:
        return False
    if peak_idx < 2 or peak_idx > len(values) - 3:
        return False
    baseline_start = sum(values[:5]) / 5
    if peak < baseline_start * 2.0:
        return False
    if peak_idx >= 2 and values[peak_idx - 1] < baseline_start * 1.3:
        return False
    return True

def scan_all_curves(data_start, data_end, step=0x4000):
    """扫描数据区的所有曲线"""
    all_curves = []
    for base in range(data_start, data_end, step):
        chunk = read_at(CURRENT_FILE, base, min(step + 0x2000, data_end - base))
        if len(chunk) < 400:
            continue

        n_f = len(chunk) // 4
        f32 = struct.unpack_from(f'<{n_f}f', chunk, 0)

        i = 0
        while i < n_f:
            if abs(f32[i]) > 0.03:
                s = i
                vals = []
                while i < n_f and abs(f32[i]) > 0.005:
                    vals.append(f32[i])
                    i += 1
                if is_valid_curve(vals):
                    all_curves.append({
                        'file_offset': base + s * 4,
                        'len': len(vals),
                        'peak': round(max(vals), 4),
                    })
                i += 1
            else:
                i += 1

    return all_curves


def main():
    size = os.path.getsize(CURRENT_FILE)

    # ── 扫描整个数据区（避开文件头2MB和F9索引区） ──
    print("扫描数据区 (0x02000000 ~ EOF)...")
    # First curve_info tag is at 0x033271B8, so data starts ~0x03300000
    # But let's be safe and start from 0x03000000
    all_curves = scan_all_curves(0x03000000, size, 0x8000)

    print(f"找到 {len(all_curves)} 条有效曲线")

    if not all_curves:
        print("未找到曲线!")
        return

    all_curves.sort(key=lambda c: c['file_offset'])

    # ── 按间距0x1028分组 ──
    print("\n按间距0x1028 (±0x100) 分组为3相事件")
    PHASE_GAP = 0x1028
    TOLERANCE = 0x200  # 容差

    events = []
    i = 0
    while i < len(all_curves):
        c0 = all_curves[i]
        # 找下一条在 c0 + PHASE_GAP 附近的曲线
        c1 = None
        for j in range(i+1, min(i+5, len(all_curves))):
            gap = all_curves[j]['file_offset'] - c0['file_offset']
            if abs(gap - PHASE_GAP) < TOLERANCE:
                c1 = all_curves[j]
                break

        c2 = None
        if c1:
            for j in range(i+2, min(i+8, len(all_curves))):
                gap = all_curves[j]['file_offset'] - (c0['file_offset'] + 2*PHASE_GAP)
                if abs(gap) < TOLERANCE:
                    c2 = all_curves[j]
                    break

        if c0 and c1 and c2:
            events.append({
                'phases': [
                    {'peak': c0['peak'], 'len': c0['len'], 'offset': c0['file_offset']},
                    {'peak': c1['peak'], 'len': c1['len'], 'offset': c1['file_offset']},
                    {'peak': c2['peak'], 'len': c2['len'], 'offset': c2['file_offset']},
                ]
            })
            i = max(all_curves.index(c2), i) + 1
        elif c0 and c1:
            events.append({
                'phases': [
                    {'peak': c0['peak'], 'len': c0['len'], 'offset': c0['file_offset']},
                    {'peak': c1['peak'], 'len': c1['len'], 'offset': c1['file_offset']},
                ]
            })
            i = max(all_curves.index(c1), i) + 1
        else:
            events.append({
                'phases': [
                    {'peak': c0['peak'], 'len': c0['len'], 'offset': c0['file_offset']},
                ]
            })
            i += 1

    # ── 统计 ──
    print(f"\n事件数: {len(events)}")
    n_dist = Counter(len(e['phases']) for e in events)
    for n, cnt in sorted(n_dist.items()):
        print(f"  {n} 相/事件: {cnt}")
    triple = [e for e in events if len(e['phases']) == 3]
    print(f"  完整3相事件: {len(triple)}")

    # ── 显示3相事件样本 ──
    if triple:
        print(f"\n3相事件样本 (前10):")
        for ei, e in enumerate(triple[:10]):
            print(f"\n  事件{ei}:")
            for pi, p in enumerate(e['phases']):
                print(f"    相{chr(65+pi)}: peak={p['peak']:.2f}A, len={p['len']}, @ 0x{p['offset']:08X}")

    # ── 事件间距分析 ──
    if len(events) >= 2:
        event_gaps = []
        for i in range(1, min(100, len(events))):
            prev_end = events[i-1]['phases'][-1]['offset']
            curr_start = events[i]['phases'][0]['offset']
            event_gaps.append(curr_start - prev_end)

        print(f"\n事件间间距分析 (前50对):")
        gap_dist = Counter(event_gaps)
        for gap, cnt in gap_dist.most_common(10):
            print(f"  gap=0x{gap:06X} ({gap}): {cnt}次")

    # ── F9对比 ──
    print(f"\n{'='*80}")
    print("F9对比")
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
    print(f"事件总数: {len(events)}")
    print(f"3相事件: {len(triple)}")
    total_c = sum(len(e['phases']) for e in events)
    print(f"总曲线: {total_c}")
    print(f"如果每个F9→3相: {n_f9*3} (vs {total_c})")
    if n_f9 > 0:
        print(f"F9→事件比例: {len(events)/n_f9:.3f}")

    # ── 保存 ──
    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_position_grouped.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump({
            'meta': {
                'total_events': len(events),
                'triple_phase': len(triple),
                'total_curves': total_c,
                'f9_records': n_f9,
            },
            'events': events,
        }, f, ensure_ascii=False)
    print(f"\n保存: {out_path} ({os.path.getsize(out_path)/1024/1024:.1f} MB)")
    print("完成!")


if __name__ == '__main__':
    main()
