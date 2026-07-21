#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
测试"3个连续标签=1个事件(3相)"的假设。
提取每个curve_info标签±0x4000范围内的唯一一条曲线，
然后看3个连续标签的曲线能否组成3相事件。
"""
import struct, os, sys, json
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_best_curve(raw, base_offset):
    """在raw中找最优的一条曲线（峰值最大的）"""
    n_f = len(raw) // 4
    if n_f == 0:
        return None
    f32 = struct.unpack_from(f'<{n_f}f', raw, 0)
    best = None
    i = 0
    while i < n_f:
        if abs(f32[i]) > 0.03:
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
                        if best is None or peak > best['peak']:
                            best = {
                                'local_off': s * 4,
                                'file_off': base_offset + s * 4,
                                'len': len(vals),
                                'peak': round(peak, 4),
                                'values': [round(v, 6) for v in vals],
                            }
            i += 1
        else:
            i += 1
    return best


def main():
    size = os.path.getsize(CURRENT_FILE)

    # ── 找所有标签 ──
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

    # ── 阶段2: 每个标签提取1条最佳曲线 ──
    print("\n阶段2: 每个标签提取1条最佳曲线（+0x2000 ~ +0x6000）")

    tag_curves = {}  # tag_idx → best_curve
    for ti, tp in enumerate(tag_positions):
        chunk = read_at(CURRENT_FILE, tp + 0x2000, 0x4000)
        c = find_best_curve(chunk, tp + 0x2000)
        if c:
            tag_curves[ti] = c

    print(f"有曲线的标签: {len(tag_curves)}/{len(tag_positions)}")

    # ── 阶段3: 3个连续标签组成事件 ──
    print("\n阶段3: 3个连续标签组成事件")

    # 找到所有有曲线的标签索引
    active_tags = sorted(tag_curves.keys())
    print(f"活跃标签范围: {active_tags[0]} - {active_tags[-1]}")

    # 检查间距模式
    gaps = []
    for i in range(1, min(1000, len(active_tags))):
        gaps.append(active_tags[i] - active_tags[i-1])

    gap_dist = Counter(gaps)
    print(f"\n活跃标签间距分布 (前10):")
    for gap, cnt in gap_dist.most_common(10):
        print(f"  间距={gap}: {cnt} 次")

    # 尝试按连续3个分组
    events_3consecutive = []
    i = 0
    while i < len(active_tags) - 2:
        t0, t1, t2 = active_tags[i], active_tags[i+1], active_tags[i+2]
        # 3个连续标签的条件：索引连续
        if t1 == t0 + 1 and t2 == t1 + 1:
            c0 = tag_curves[t0]
            c1 = tag_curves[t1]
            c2 = tag_curves[t2]
            events_3consecutive.append({
                'tags': [t0, t1, t2],
                'phases': [
                    {'phase': 'A', 'peak': c0['peak'], 'len': c0['len'], 'offset': c0['file_off']},
                    {'phase': 'B', 'peak': c1['peak'], 'len': c1['len'], 'offset': c1['file_off']},
                    {'phase': 'C', 'peak': c2['peak'], 'len': c2['len'], 'offset': c2['file_off']},
                ],
            })
            i += 3
        else:
            i += 1

    print(f"\n3连续标签事件: {len(events_3consecutive)}")

    if events_3consecutive:
        print(f"\n前5个事件样本:")
        for e in events_3consecutive[:5]:
            print(f"  tags={e['tags']}: ", end='')
            for p in e['phases']:
                print(f"{p['phase']}={p['peak']:.2f}A(len={p['len']}) ", end='')
            print()

    # ── 阶段4: 检查tag间距与F9记录的u32[0]关系 ──
    print(f"\n{'='*80}")
    print("阶段4: tag间距 vs u32[0]步长")
    print("="*80)

    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)
    pos = data_2mb.find(b'1-J')
    block = data_2mb[pos:pos+256]
    fields = struct.unpack_from('<13I', block, 0x70)
    f9, f6 = fields[9], fields[6]
    raw_f9 = read_at(CURRENT_FILE, f9, f6)
    marker_3277 = b'\x77\x32'

    records = []
    for i in range(f6//32):
        rec = raw_f9[i*32:(i+1)*32]
        if rec[13:15] == marker_3277:
            u32s = struct.unpack_from('<8I', rec, 0)
            records.append({
                'idx': i,
                'u32_0': u32s[0], 'u32_1': u32s[1], 'u32_2': u32s[2],
                'u32_6': u32s[6],
            })

    # u32[0]步长 = 0x100 = 256
    # tag间距 = ? 看看活跃标签的实际间距
    # 检查: 活跃标签的总数 vs F9记录×某个值

    n_active = len(active_tags)
    n_f9 = len(records)
    print(f"活跃标签: {n_active}")
    print(f"F9记录: {n_f9}")
    print(f"比值: {n_active/n_f9:.2f}")
    print(f"如果每个F9→3个标签: {n_f9*3} (vs {n_active})")
    print(f"如果每个F9→6个标签: {n_f9*6} (vs {n_active})")

    # 检查活跃标签在哪些序号范围内连续
    # 标签总范围: 0 - 33392
    # 活跃标签: 前1000个
    first_consecutive_runs = []
    run_start = active_tags[0]
    for i in range(1, len(active_tags)):
        if active_tags[i] != active_tags[i-1] + 1:
            run_len = active_tags[i-1] - run_start + 1
            if run_len >= 3:
                first_consecutive_runs.append((run_start, run_len))
            run_start = active_tags[i]

    print(f"\n活跃标签中的连续段(≥3): {len(first_consecutive_runs)}")
    for rs, rl in first_consecutive_runs[:20]:
        print(f"  起始tag={rs}, 长度={rl}")

    # ── 阶段5: 最关键的测试——只看"密集连续"的区域 ──
    print(f"\n{'='*80}")
    print("阶段5: 连续活跃标签段的3相分组")
    print("="*80)

    # 找连续的活跃标签段
    runs = []
    rs = active_tags[0]
    for i in range(1, len(active_tags)):
        if active_tags[i] != active_tags[i-1] + 1:
            rl = active_tags[i-1] - rs + 1
            runs.append((rs, rl))
            rs = active_tags[i]
    runs.append((active_tags[-1] - rs + 1, rs))  # last run

    # 修正runs
    runs = []
    rs = active_tags[0]
    for i in range(1, len(active_tags)):
        if active_tags[i] != active_tags[i-1] + 1:
            rl = active_tags[i-1] - rs + 1
            runs.append((rs, rl))
            rs = active_tags[i]
    runs.append((rs, active_tags[-1] - rs + 1))

    # 检查 run长度÷3 的关系
    run_len_dist = Counter(rl for _, rl in runs)
    print(f"活跃标签连续段长度分布:")
    for rl, cnt in sorted(run_len_dist.items()):
        mod3 = rl % 3
        print(f"  长度={rl}: {cnt} 段 (mod3={mod3})")

    print("\n完成!")


if __name__ == '__main__':
    main()
