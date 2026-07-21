#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
深度分析电流HBF目录结构，理解曲线→开关的映射关系。
关键问题：F9索引区只有1-J和1-X有数据，但所有30个道岔都有F7(曲线数)。
F3/F4/F5字段可能包含曲线池中的偏移信息。
"""
import struct, os, sys, json
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu"
CURRENT_FILE = os.path.join(BASE, "道岔动作电流曲线", "2.hbf")

ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def get_all_dir_entries(filepath):
    """获取所有30个开关的完整目录项（13个uint32字段）"""
    with open(filepath, 'rb') as f:
        data = f.read(0x200000)

    entries = {}
    for sw_id in ALL_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos == -1:
            print(f"  WARNING: {sw_id} not found in first 2MB!")
            continue
        block = data[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            print(f"  WARNING: {sw_id} ID mismatch at pos 0x{pos:X}, got '{sid}'")
            continue
        fields = struct.unpack_from('<13I', block, 0x70)
        entries[sw_id] = {
            'pos': pos,
            'F0': fields[0], 'F1': fields[1], 'F2': fields[2],
            'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
            'F6': fields[6], 'F7': fields[7], 'F8': fields[8],
            'F9': fields[9], 'F10': fields[10], 'F11': fields[11], 'F12': fields[12],
        }
    return entries

def main():
    print("=" * 80)
    print("阶段1: 完整目录项分析")
    print("=" * 80)

    entries = get_all_dir_entries(CURRENT_FILE)

    # 列出所有30个开关的完整字段
    print(f"\n{'Switch':<8} {'pos':>8} ", end='')
    for i in range(13):
        print(f"{'F'+str(i):>12} ", end='')
    print()
    print("-" * 180)

    for sw_id in sorted(entries.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        e = entries[sw_id]
        print(f"{sw_id:<8} 0x{e['pos']:06X} ", end='')
        for i in range(13):
            print(f"{e[f'F{i}']:>12} ", end='')
        print()

    # ── 分析F6/F7关系 ──
    print(f"\n\n{'='*80}")
    print("阶段2: F6/F7/F8 关系分析")
    print("="*80)

    print(f"\n{'Switch':<8} {'F6':>12} {'F7':>12} {'F8':>12} {'F6/F7':>10} {'F8/F7':>10} {'F6/32':>10}")
    print("-" * 80)

    for sw_id in sorted(entries.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        e = entries[sw_id]
        f6, f7, f8 = e['F6'], e['F7'], e['F8']
        ratio67 = f6 / f7 if f7 > 0 else 0
        ratio87 = f8 / f7 if f7 > 0 else 0
        ratio6_32 = f6 / 32 if f6 > 0 else 0
        print(f"{sw_id:<8} {f6:>12} {f7:>12} {f8:>12} {ratio67:>10.2f} {ratio87:>10.2f} {ratio6_32:>10.2f}")

    # ── 分析F9区域 ──
    print(f"\n\n{'='*80}")
    print("阶段3: F9索引区深度分析")
    print("="*80)

    # 找出所有F9>0的开关
    active_f9 = [(sw, e) for sw, e in entries.items() if e['F9'] > 0 and e['F6'] > 0]
    print(f"\nF9>0 的开关: {len(active_f9)}")

    for sw_id, e in sorted(active_f9, key=lambda x: x[1]['F9']):
        f9, f6, f7, f3, f4, f5 = e['F9'], e['F6'], e['F7'], e['F3'], e['F4'], e['F5']
        print(f"\n{'─'*60}")
        print(f"[{sw_id}] F9=0x{f9:08X} F6={f6} F7={f7} F3={f3} F4=0x{f4:08X} F5={f5}")

        # 读取F9区域
        raw = read_at(CURRENT_FILE, f9, min(f6, 0x200000))
        n_records = f6 // 32

        print(f"  总32B记录数: {n_records}")

        # 显示前3条记录
        print(f"  前3条32B记录:")
        for i in range(min(3, n_records)):
            rec = raw[i*32:(i+1)*32]
            u32s = struct.unpack_from('<8I', rec, 0)
            hex_str = ' '.join(f'{b:02x}' for b in rec)
            print(f"    [{i}] {hex_str}")
            print(f"        u32: {[f'0x{v:08X}' for v in u32s]}")

            # 检查marker位置
            m_12_13 = rec[12:14]  # bytes 12-13
            m_13_14 = rec[13:15]  # bytes 13-14
            print(f"        bytes[12:14]=0x{m_12_13.hex()} bytes[13:15]=0x{m_13_14.hex()}")

        # 统计marker: 0x3277和0x1227
        marker_3277 = b'\x77\x32'
        marker_1227 = b'\x27\x12'

        count_3277 = 0
        count_1227 = 0
        count_none = 0

        records_3277 = []
        records_1227 = []

        for i in range(n_records):
            rec = raw[i*32:(i+1)*32]
            m12 = rec[12:14]
            m13 = rec[13:15]

            u32s = struct.unpack_from('<8I', rec, 0)

            if m13 == marker_3277:
                count_3277 += 1
                records_3277.append({
                    'idx': i,
                    'u32': list(u32s),
                    'bytes_16_23': rec[16:24].hex(),
                })
            elif m12 == marker_3277:
                count_3277 += 1
                records_3277.append({
                    'idx': i,
                    'u32': list(u32s),
                    'bytes_16_23': rec[16:24].hex(),
                    'note': 'marker@12-13'
                })
            elif m12 == marker_1227 or m13 == marker_1227:
                count_1227 += 1
                records_1227.append({
                    'idx': i,
                    'u32': list(u32s),
                    'bytes_16_23': rec[16:24].hex(),
                })
            else:
                count_none += 1

        print(f"\n  记录统计: 0x3277={count_3277}, 0x1227={count_1227}, 其他={count_none}")

        if records_3277:
            print(f"\n  前5条0x3277记录:")
            for r in records_3277[:5]:
                u32 = r['u32']
                print(f"    idx={r['idx']:>4}: u32[0]=0x{u32[0]:08X} u32[1]=0x{u32[1]:08X} "
                      f"u32[2]=0x{u32[2]:08X} u32[3]=0x{u32[3]:08X} "
                      f"bytes16-23={r['bytes_16_23']}")

        if records_1227:
            print(f"\n  前5条0x1227记录:")
            for r in records_1227[:5]:
                u32 = r['u32']
                print(f"    idx={r['idx']:>4}: u32[0]=0x{u32[0]:08X} u32[1]=0x{u32[1]:08X} "
                      f"u32[2]=0x{u32[2]:08X} u32[3]=0x{u32[3]:08X} "
                      f"bytes16-23={r['bytes_16_23']}")

    # ── 分析F9=0但有F7>0的开关 ──
    print(f"\n\n{'='*80}")
    print("阶段4: F9=0但F7>0的开关（曲线数据可能在共享池中）")
    print("="*80)

    no_f9_with_data = [(sw, e) for sw, e in entries.items() if e['F9'] == 0 and e['F7'] > 0]
    print(f"\nF9=0但有F7>0的开关: {len(no_f9_with_data)}")

    for sw_id, e in sorted(no_f9_with_data, key=lambda x: (int(x[0].split('-')[0]), x[0].split('-')[1])):
        print(f"  {sw_id}: F7={e['F7']}, F3={e['F3']}, F4=0x{e['F4']:08X}, F5={e['F5']}, F6={e['F6']}, F8={e['F8']}")

    # ── 分析F3和F4字段 ──
    print(f"\n\n{'='*80}")
    print("阶段5: F3/F4/F5 字段含义推演")
    print("="*80)

    # 对每个开关，F3可能是在共享曲线池中的起始索引
    # F4可能是某种偏移量
    # 检查F3+F7是否等于下一个开关的F3

    print(f"\n{'Switch':<8} {'F3':>8} {'F7':>8} {'F3+F7':>10} {'next_F3':>10} {'gap':>10}")
    print("-" * 60)

    sorted_sw = sorted(entries.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1]))
    for i, sw_id in enumerate(sorted_sw):
        e = entries[sw_id]
        f3 = e['F3']
        f7 = e['F7']
        f3_plus_f7 = f3 + f7

        if i + 1 < len(sorted_sw):
            next_f3 = entries[sorted_sw[i+1]]['F3']
            gap = next_f3 - f3_plus_f7
        else:
            next_f3 = 0
            gap = 0

        print(f"{sw_id:<8} {f3:>8} {f7:>8} {f3_plus_f7:>10} {next_f3:>10} {gap:>10}")

    # ── 分析F4字段（可能是数据块偏移） ──
    print(f"\n\nF4字段分析（数据偏移?）:")
    print(f"\n{'Switch':<8} {'F4(hex)':>12} {'F7':>8}")
    print("-" * 35)

    f4_values = []
    for sw_id in sorted_sw:
        e = entries[sw_id]
        f4 = e['F4']
        f7 = e['F7']
        if f4 > 0:
            f4_values.append((sw_id, f4, f7))
            print(f"{sw_id:<8} 0x{f4:010X} {f7:>8}")

    # 找F4和实际曲线数据偏移的关系
    if f4_values:
        # 排序F4值
        f4_values.sort(key=lambda x: x[1])
        print(f"\n  按F4排序:")
        for sw_id, f4, f7 in f4_values:
            print(f"    {sw_id}: F4=0x{f4:08X} F7={f7}")

    print("\n完成!")

if __name__ == '__main__':
    main()
