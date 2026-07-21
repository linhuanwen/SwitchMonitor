#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
正确解析电流HBF和功率HBF的目录结构，并读取F9区域数据。
重点：电流HBF的目录格式与功率HBF不同，13个uint32字段不在0x70偏移。
"""
import struct
import os
import sys
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu"
POWER_FILE = os.path.join(BASE, "道岔动作功率曲线", "2.hbf")
CURRENT_FILE = os.path.join(BASE, "道岔动作电流曲线", "2.hbf")

SWITCH_IDS = [f"{i}-{d}" for i in [1,2,3,4,5,6,7,8,9,10,11,13,15,17,19,21] for d in ['J','X']]

def parse_current_directory(filepath):
    """解析电流HBF的目录结构。

    电流HBF结构（不同于功率HBF的256字节定长块）：
    - 文件头有 magic(8B) + header fields
    - 每个道岔条目以 0x00021400 tag 开头
    - 条目内字段布局不同
    """
    with open(filepath, 'rb') as f:
        data = f.read(0x100000)  # 读前1MB

    # 先全部扫描开关ID位置
    id_positions = {}
    for sw_id in SWITCH_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos >= 0:
            id_positions[sw_id] = pos

    print(f"找到 {len(id_positions)} 个道岔ID")

    # 对每个ID，找到它所在的条目块
    # 电流HBF条目以4字节tag 0x00021400 开始
    tag = struct.pack('<I', 0x00021400)

    entries = {}
    for sw_id, id_pos in sorted(id_positions.items(), key=lambda x: (int(x[0].split('-')[0]), x[0].split('-')[1])):
        # 从ID位置向前搜索最近的tag
        block_start = None
        for search_pos in range(id_pos - 4, max(0, id_pos - 0x1000), -4):
            if data[search_pos:search_pos+4] == tag:
                block_start = search_pos
                break

        if block_start is None:
            print(f"  {sw_id}: 未找到tag，跳过")
            continue

        # 解析该条目块
        # 块开始于tag，但实际数据从tag后的某个偏移开始
        # 根据对1-J的分析，13个uint32字段在block_start+0xC8处

        # 先检查block_start+8处是否有uint32
        block = data[block_start:block_start+256]

        # 尝试在几个可能的偏移处读取字段
        # 已知：block_start+0xC8开始有类似F0-F12的数据
        fields_at_c8 = struct.unpack_from('<8I', data, block_start + 0xC8)
        fields_at_70 = struct.unpack_from('<8I', data, block_start + 0x70)

        # 从ID前面的字节解析一些元数据
        id_prefix = data[id_pos-8:id_pos]
        prefix_vals = struct.unpack_from('<HHI', data, id_pos-8)  # 2个uint16 + 1个uint32

        # F6和F7的识别：在功率HBF中，F6=F7*32
        # 找到满足此关系的值对
        f7_candidate = None
        f6_candidate = None
        f9_candidate = None

        for i in range(0, 256-16, 4):
            vals = struct.unpack_from('<4I', data, block_start + i)
            for j in range(3):
                if vals[j] > 0 and vals[j+1] > 0:
                    # 检查是否满足 F6 = F7 * 32
                    if vals[j] == vals[j+1] * 32:
                        f6_candidate = vals[j]
                        f7_candidate = vals[j+1]
                        # F9 通常在 F6 附近
                        if j >= 1 and vals[j-1] > 0x10000:
                            f9_candidate = vals[j-1]
                        elif j + 2 < 4 and vals[j+2] > 0x10000:
                            f9_candidate = vals[j+2]
                        break
            if f7_candidate:
                break

        entry = {
            'sw_id': sw_id,
            'block_start': block_start,
            'id_offset': id_pos - block_start,
            'prefix': prefix_vals,
            'f6': f6_candidate,
            'f7': f7_candidate,
            'f9': f9_candidate,
            'fields_c8': fields_at_c8,
        }
        entries[sw_id] = entry

    return entries


def parse_power_directory(filepath):
    """解析功率HBF的目录结构（已知256字节定长块格式）"""
    with open(filepath, 'rb') as f:
        data = f.read(0x200000)

    entries = {}
    for sw_id in SWITCH_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block_start = (pos // 256) * 256
        block = data[block_start:block_start+256]
        fields = struct.unpack_from('<13I', block, 0x70)
        entries[sw_id] = {
            'sw_id': sw_id,
            'block_start': block_start,
            'F0': fields[0], 'F1': fields[1], 'F2': fields[2],
            'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
            'F6': fields[6], 'F7': fields[7], 'F8': fields[8],
            'F9': fields[9], 'F10': fields[10], 'F11': fields[11],
            'F12': fields[12],
        }
    return entries


def read_f9_area(filepath, f9, f6, sw_id, label):
    """读取F9指向的数据区并做初步分析"""
    if f9 is None or f6 is None or f6 == 0:
        return None

    with open(filepath, 'rb') as f:
        f.seek(f9)
        raw = f.read(min(f6, 0x20000))  # 最多128KB

    print(f"\n{'─'*60}")
    print(f"[{label}] {sw_id}: F9=0x{f9:X}, F6={f6}")

    if len(raw) < 32:
        print(f"  数据区太小 ({len(raw)} bytes)")
        return None

    # 前64字节 hex dump
    print(f"  前64字节:")
    for i in range(0, min(64, len(raw)), 16):
        hex_str = ' '.join(f'{b:02x}' for b in raw[i:i+16])
        print(f"    {i:04x}: {hex_str}")

    # 检查是否为32字节记录结构
    # 搜索 0x1227 和 0x3277 markers
    marker_1227 = b'\x27\x12'
    marker_3277 = b'\x77\x32'

    found_1227 = []
    found_3277 = []

    for i in range(0, len(raw) - 1):
        if raw[i:i+2] == marker_1227:
            found_1227.append(i)
        if raw[i:i+2] == marker_3277:
            found_3277.append(i)

    result = {
        'sw_id': sw_id,
        'f9': f9,
        'f6': f6,
        'label': label,
        'len_raw': len(raw),
        'marker_1227_count': len(found_1227),
        'marker_3277_count': len(found_3277),
        'marker_1227_offsets': found_1227[:5],
        'marker_3277_offsets': found_3277[:5],
    }

    if found_1227 or found_3277:
        print(f"  0x1227(功率): {len(found_1227)} 个")
        print(f"  0x3277(电流): {len(found_3277)} 个")

    # 如果按32字节记录解析
    if len(raw) >= 32:
        # 检查前32字节
        rec = raw[:32]
        # 在字节12-15处找marker
        m12 = rec[12:14]  # 正常偏移
        m13 = rec[13:15]  # 偏移+1位置

        if m12 == marker_1227:
            print(f"  记录类型: 32B索引记录 (0x1227 marker @ +12)")
            result['record_type'] = '32b_index_1227'
        elif m13 == marker_3277 or m12 == marker_3277:
            print(f"  记录类型: 32B索引记录 (0x3277 marker)")
            result['record_type'] = '32b_index_3277'
        elif m12 == marker_1227 or m13 == marker_1227:
            print(f"  记录类型: 32B索引记录 (0x1227 marker)")
            result['record_type'] = '32b_index_1227'
        else:
            # 不是32B索引记录 - 可能是直接数据
            # 尝试按float32解析
            f32_sample = struct.unpack_from('<20f', raw, 0)
            print(f"  前20个float32: {[round(v, 3) for v in f32_sample]}")
            result['record_type'] = 'float32_data'

        # 解析前几个32B记录
        if result.get('record_type', '').startswith('32b'):
            n_recs = min(5, f6 // 32)
            print(f"\n  前{n_recs}个32B记录:")
            for i in range(n_recs):
                rec = raw[i*32:(i+1)*32]
                u32s = struct.unpack_from('<8I', rec, 0)
                hex_str = ' '.join(f'{b:02x}' for b in rec)
                print(f"    [{i}] {hex_str}")
                print(f"        u32: {[f'0x{v:08X}' for v in u32s]}")
                # 尝试解析中间8字节作为int64
                mid8 = rec[16:24]
                u64 = struct.unpack_from('<Q', rec, 16)[0]
                print(f"        bytes16-23 (u64): 0x{u64:016X}")

    return result


def main():
    print("="*70)
    print("阶段1: 解析电流HBF目录结构")
    print("="*70)

    current_entries = parse_current_directory(CURRENT_FILE)

    # 显示所有道岔的F6/F7/F9
    print(f"\n{'Switch':<8} {'block':>8} {'F6':>10} {'F7':>10} {'F6/F7':>10} {'F9':>12} {'prefix':>20}")
    print("-"*80)

    active_current = []
    for sw_id in sorted(current_entries.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        e = current_entries[sw_id]
        f6 = e['f6'] or 0
        f7 = e['f7'] or 0
        f9 = e['f9'] or 0
        ratio = f6 / f7 if f7 > 0 else 0
        prefix = f"({e['prefix'][0]},{e['prefix'][1]},{e['prefix'][2]})" if e['prefix'] else ""
        if f7 > 0:
            active_current.append(e)
        print(f"{sw_id:<8} 0x{e['block_start']:06X} {f6:>10} {f7:>10} {ratio:>10.1f} 0x{f9:>010X} {prefix:<20}")

    print(f"\n有数据的电流道岔: {len(active_current)}")

    # 选几个道岔读F9
    sample = [e for e in active_current if e['sw_id'] in ['1-J', '3-J', '7-J', '11-J', '21-J']]
    for e in sample:
        read_f9_area(CURRENT_FILE, e['f9'], e['f6'], e['sw_id'], 'CURRENT_2.hbf')

    # ── 功率HBF ──
    print(f"\n\n{'='*70}")
    print("阶段2: 解析功率HBF目录并对比")
    print("="*70)

    power_entries = parse_power_directory(POWER_FILE)

    print(f"\n{'Switch':<8} {'F6':>10} {'F7':>10} {'F6/F7':>10} {'F9':>12} {'F4':>12}")
    print("-"*60)

    active_power = []
    for sw_id in sorted(power_entries.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        e = power_entries[sw_id]
        f6, f7, f9 = e['F6'], e['F7'], e['F9']
        ratio = f6 / f7 if f7 > 0 else 0
        if f7 > 0:
            active_power.append(e)
        print(f"{sw_id:<8} {f6:>10} {f7:>10} {ratio:>10.1f} 0x{f9:>010X} 0x{e['F4']:>010X}")

    # 对比电流和功率的F7(事件数)
    print(f"\n\n{'='*70}")
    print("阶段3: 电流 vs 功率 事件数(F7) 对比")
    print("="*70)

    # 建立查找表
    cur_f7 = {e['sw_id']: e['f7'] for e in current_entries.values()}
    pwr_f7 = {e['sw_id']: e['F7'] for e in power_entries.values()}

    print(f"\n{'Switch':<8} {'Cur_F7':>10} {'Pwr_F7':>10} {'Ratio':>10}")
    print("-"*45)

    for sw_id in sorted(set(list(cur_f7.keys()) + list(pwr_f7.keys())),
                       key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        c = cur_f7.get(sw_id, 0) or 0
        p = pwr_f7.get(sw_id, 0) or 0
        ratio = c / p if p > 0 else 999
        if c > 0 or p > 0:
            marker = ""
            if c > 0 and p > 0:
                if abs(c/p - 1.0) < 0.05:
                    marker = " ✓ 匹配!"
                elif abs(c/p - 3.0) < 0.1:
                    marker = " ★ 3倍(3相电流?)"
            print(f"{sw_id:<8} {c:>10} {p:>10} {ratio:>10.2f}{marker}")

    # ── 深入分析一对匹配的开关 ──
    if active_current and active_power:
        print(f"\n\n{'='*70}")
        print("阶段4: 深入分析F9区域数据")
        print("="*70)

        # 找一个电流和功率都有数据的开关
        for sw_id in ['1-J', '3-J', '11-J', '21-J']:
            ce = current_entries.get(sw_id)
            pe = power_entries.get(sw_id)
            if ce and pe and ce['f9'] and pe['F9']:
                print(f"\n>>> 对比 {sw_id} <<<")
                read_f9_area(POWER_FILE, pe['F9'], pe['F6'], sw_id, 'POWER_2.hbf')
                read_f9_area(CURRENT_FILE, ce['f9'], ce['f6'], sw_id, 'CURRENT_2.hbf')
                break


if __name__ == '__main__':
    main()
