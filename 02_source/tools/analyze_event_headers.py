#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
分析电流和功率 HBF 的事件块头部结构，寻找时间戳和事件标记。
核心假设：同一开关动作同时生成3条电流+1条功率曲线，可通过事件索引/时间戳关联。
"""
import struct
import os
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu"
POWER_DIR = os.path.join(BASE, "道岔动作功率曲线")
CURRENT_DIR = os.path.join(BASE, "道岔动作电流曲线")

# 道岔 ID 列表 (30个)
SWITCH_IDS = [f"{i}-{d}" for i in [1,2,3,4,5,6,7,8,9,10,11,13,15,17,19,21] for d in ['J','X']]

def find_directory_entries(filepath):
    """在每个HBF文件中搜索30个道岔的目录项(F0-F12)"""
    with open(filepath, 'rb') as f:
        header_data = f.read(0x200000)  # 前2MB

    entries = {}
    for sw_id in SWITCH_IDS:
        pos = header_data.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        # 目录块对齐到256字节边界——搜索最近的前一个256B对齐位置
        block_start = (pos // 256) * 256
        block = header_data[block_start:block_start+256]
        # F0-F12 从偏移 0x70 开始
        fields = struct.unpack_from('<13I', block, 0x70)
        entries[sw_id] = {
            'F0': fields[0], 'F1': fields[1], 'F2': fields[2],
            'F3': fields[3], 'F4': fields[4],  # F4=子索引块偏移
            'F5': fields[5],
            'F6': fields[6],  # 数据区大小
            'F7': fields[7],  # 事件容量/记录数
            'F8': fields[8],
            'F9': fields[9],  # 事件数据偏移
            'F10': fields[10],
            'F11': fields[11],
            'F12': fields[12],
            'block_offset': block_start,
        }
    return entries

def analyze_event_block(filepath, f9, f6, sw_id, file_type='power'):
    """分析F9指向的事件块数据区域，寻找头部结构和marker"""
    with open(filepath, 'rb') as f:
        f.seek(f9)
        raw = f.read(min(f6, 0x100000))  # 最多读1MB

    print(f"\n{'='*70}")
    print(f"[{file_type}] {sw_id}: F9=0x{f9:X}, F6=0x{f6:X} ({f6} bytes)")
    print(f"{'='*70}")

    if len(raw) < 64:
        print("  数据区太小，跳过")
        return None

    results = {'file_type': file_type, 'sw_id': sw_id, 'f9': f9, 'f6': f6}

    # ── 1. 检查文件头部magic bytes ──
    print(f"\n  前64字节 hex:")
    for i in range(0, min(64, len(raw)), 16):
        hex_str = ' '.join(f'{b:02x}' for b in raw[i:i+16])
        ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in raw[i:i+16])
        print(f"    {i:04x}: {hex_str:<48s}  {ascii_str}")

    # ── 2. 搜索已知marker ──
    marker_1227 = b'\x27\x12'  # 功率marker (0x1227)
    marker_3277 = b'\x77\x32'  # 电流marker (0x3277)

    pos_1227 = raw.find(marker_1227)
    pos_3277 = raw.find(marker_3277)
    print(f"\n  0x1227 (功率marker) 首次出现: 0x{pos_1227:X}" if pos_1227 >= 0 else f"\n  0x1227: 未找到")
    print(f"  0x3277 (电流marker) 首次出现: 0x{pos_3277:X}" if pos_3277 >= 0 else f"  0x3277: 未找到")

    # ── 3. 尝试解析事件块头部 ──
    # 对于功率HBF: F9区域直接是float32采样数据，每个事件块可能有个小头部
    # 对于电流HBF: F9区域是32字节索引记录
    # 两种文件的事件块结构可能不同

    # 检查是否像32字节记录结构
    if len(raw) >= 32:
        # 尝试按32字节记录解析
        rec = raw[:32]
        # 检查记录中是否有marker
        has_marker = rec[12:14] == marker_1227 or rec[12:14] == marker_3277 or \
                     rec[13:15] == marker_1227 or rec[13:15] == marker_3277

        if has_marker:
            print(f"\n  前32字节包含marker，按索引记录结构解析:")
            u32_vals = struct.unpack_from('<8I', rec, 0)
            print(f"    u32[0-7]: {[f'0x{v:08X}' for v in u32_vals]}")

            # 检查后面是否还有更多记录
            n_records = f6 // 32
            print(f"    总记录数(F6/32): {n_records}")

            # 解析前几个记录
            marker_type = '0x3277(电流)' if raw[13:15] == marker_3277 or raw[12:14] == marker_3277 else '0x1227(功率)'
            print(f"    记录类型: {marker_type}")
            results['record_type'] = 'index'
            results['marker'] = marker_type
        else:
            print(f"\n  前32字节不含marker，可能为直接数据区")
            results['record_type'] = 'data'

    # ── 4. 如果是数据区，尝试找事件边界 ──
    if results.get('record_type') != 'index':
        # 按float32解析看看
        n_floats = min(len(raw) // 4, 500)
        f32 = struct.unpack_from(f'<{n_floats}f', raw, 0)

        # 统计零值分布，找事件边界
        zeros = [i for i, v in enumerate(f32) if abs(v) < 0.001]
        if zeros:
            zero_gaps = []
            gap_start = zeros[0]
            for i in range(1, len(zeros)):
                if zeros[i] - zeros[i-1] > 1:
                    gap_len = zeros[i-1] - gap_start + 1
                    zero_gaps.append((gap_start, gap_len))
                    gap_start = zeros[i]
            zero_gaps.append((zeros[-1] - gap_start + 1, 0))  # last gap

            # 找长零值段(>50个连续零)
            long_gaps = [(s, l) for s, l in zero_gaps if l > 50]
            print(f"\n  长零值段(>50连续零): {len(long_gaps)} 个")
            for s, l in long_gaps[:5]:
                print(f"    offset={s} floats, len={l}")

    # ── 5. 搜索可能的时间戳 ——
    # 在数据区附近搜索Unix时间戳 (1.7e9 ~ 1.8e9, 即2024-2026)
    print(f"\n  搜索时间戳范围 (2024-2026, unix 1.70e9~1.80e9):")
    ts_found = []
    for i in range(0, len(raw) - 4, 4):
        val = struct.unpack_from('<I', raw, i)[0]
        if 1700000000 <= val <= 1800000000:
            ts_found.append((i, val))

    if ts_found:
        from datetime import datetime
        print(f"    找到 {len(ts_found)} 个候选时间戳:")
        for offset, ts in ts_found[:10]:
            dt = datetime.fromtimestamp(ts)
            print(f"      offset 0x{offset:06X}: {ts} = {dt.strftime('%Y-%m-%d %H:%M:%S')}")
    else:
        print(f"    未找到候选时间戳")

    # ── 6. 搜索uint32数值模式 (可能是event ID, 计数器等) ──
    # 找连续的递增序列
    u32s = []
    for i in range(0, min(len(raw), 0x10000), 4):
        u32s.append(struct.unpack_from('<I', raw, i)[0])

    # 检查是否有递增模式
    inc_runs = []
    run_start = 0
    for i in range(1, len(u32s)):
        if u32s[i] != u32s[i-1] + 1:
            if i - run_start >= 3:
                inc_runs.append((run_start * 4, (i - run_start)))
            run_start = i

    if inc_runs:
        print(f"\n  连续递增uint32序列 (可能是事件计数器):")
        for offset, count in inc_runs[:5]:
            print(f"    offset 0x{offset:06X}: {count} 个值递增, 从 {u32s[offset//4]} 到 {u32s[offset//4 + count - 1]}")

    return results


def main():
    # ── 先分析功率HBF（已知可解析）的事件块结构 ──
    print("\n" + "="*70)
    print("阶段1: 分析功率HBF (2.hbf) 的事件块结构")
    print("="*70)

    power_file = os.path.join(POWER_DIR, "2.hbf")
    power_entries = find_directory_entries(power_file)
    print(f"功率2.hbf: 找到 {len(power_entries)} 个道岔目录项")

    # 选几个有道岔数据分析
    sample_switches = ['1-J', '2-J', '3-J', '11-J', '17-J', '21-J']
    for sw_id in sample_switches:
        if sw_id in power_entries:
            e = power_entries[sw_id]
            if e['F9'] > 0 and e['F6'] > 0:
                analyze_event_block(power_file, e['F9'], e['F6'], sw_id, 'power')

    # ── 分析电流HBF ──
    print("\n\n" + "="*70)
    print("阶段2: 分析电流HBF 的目录结构和事件块")
    print("="*70)

    for hbf_name in ['1.hbf', '2.hbf', '3.hbf']:
        current_file = os.path.join(CURRENT_DIR, hbf_name)
        if not os.path.exists(current_file):
            print(f"\n  文件不存在: {current_file}")
            continue

        print(f"\n{'─'*70}")
        print(f"电流 {hbf_name}")
        print(f"{'─'*70}")

        entries = find_directory_entries(current_file)
        print(f"找到 {len(entries)} 个道岔目录项")

        # 列出F9/F6非零的开关
        active_switches = {sw: e for sw, e in entries.items() if e['F9'] > 0 and e['F6'] > 0}
        print(f"其中有数据的开关: {len(active_switches)}")
        for sw, e in sorted(active_switches.items(), key=lambda x: (int(x[0].split('-')[0]), x[0].split('-')[1])):
            print(f"  {sw}: F9=0x{e['F9']:08X}, F6={e['F6']}, F7={e['F7']}, F4=0x{e['F4']:08X}")

        # 选几个分析
        for sw_id in sample_switches:
            if sw_id in active_switches:
                e = active_switches[sw_id]
                analyze_event_block(current_file, e['F9'], e['F6'], sw_id, f'current_{hbf_name}')

    # ── 阶段3: 对比功率和电流的F7/F6，看是否有对应关系 ──
    print("\n\n" + "="*70)
    print("阶段3: 对比功率和电流的 F7(事件数) 与 F6(数据区大小)")
    print("="*70)

    power_entries_all = find_directory_entries(power_file)
    current_entries_all = {}
    for hbf_name in ['1.hbf', '2.hbf', '3.hbf']:
        f = os.path.join(CURRENT_DIR, hbf_name)
        if os.path.exists(f):
            current_entries_all[hbf_name] = find_directory_entries(f)

    print(f"\n{'Switch':<8} {'Pwr_F7':>8} {'Pwr_F6':>10} {'Cur1_F7':>8} {'Cur2_F7':>8} {'Cur3_F7':>8} {'Cur_F6_max':>10}")
    print("-" * 70)

    for sw_id in sorted(SWITCH_IDS, key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        p = power_entries_all.get(sw_id, {})
        p_f7 = p.get('F7', 0)
        p_f6 = p.get('F6', 0)

        cur_f7s = []
        cur_f6s = []
        for hbf_name in ['1.hbf', '2.hbf', '3.hbf']:
            c = current_entries_all.get(hbf_name, {}).get(sw_id, {})
            cur_f7s.append(c.get('F7', 0))
            cur_f6s.append(c.get('F6', 0))

        if p_f7 > 0 or any(f > 0 for f in cur_f7s):
            print(f"{sw_id:<8} {p_f7:>8} {p_f6:>10} {cur_f7s[0]:>8} {cur_f7s[1]:>8} {cur_f7s[2]:>8} {max(cur_f6s):>10}")


if __name__ == '__main__':
    main()
