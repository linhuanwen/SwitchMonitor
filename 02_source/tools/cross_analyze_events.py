#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
交叉分析功率HBF和电流HBF的F9事件数据区，寻找对应关系。
同一开关动作应同时产生3条电流+1条功率曲线。
"""
import struct
import os
import sys
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu"
POWER_FILE = os.path.join(BASE, "道岔动作功率曲线", "2.hbf")
CURRENT_FILE = os.path.join(BASE, "道岔动作电流曲线", "2.hbf")

# 开关列表
ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def get_dir_entry(filepath, sw_id, data_2mb):
    """获取新版格式的目录项（开关ID在条目开头，13个字段在ID+0x70）"""
    pos = data_2mb.find(sw_id.encode('ascii'))
    if pos == -1:
        return None
    block = data_2mb[pos:pos+256]
    sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
    if sid != sw_id:
        return None
    fields = struct.unpack_from('<13I', block, 0x70)
    f4, f6, f7, f9 = fields[4], fields[6], fields[7], fields[9]
    if not (f7 > 10 and f7 < 100000):
        return None
    return {
        'sw_id': sw_id, 'pos': pos,
        'F4': f4, 'F6': f6, 'F7': f7, 'F9': f9,
        'F10': fields[10], 'F11': fields[11], 'F12': fields[12],
        'all_fields': list(fields),
    }


def analyze_power_f9(filepath, entry):
    """分析功率HBF的F9数据区 - 寻找事件块头部"""
    f9 = entry['F9']
    f6 = entry['F6']
    sw_id = entry['sw_id']

    # 读取整个F9区域
    raw = read_at(filepath, f9, min(f6, 0x500000))

    print(f"\n{'='*60}")
    print(f"[功率] {sw_id}: F9=0x{f9:X}, F6={f6}, F7={entry['F7']}")
    print(f"{'='*60}")

    # 检查前几个字节
    print(f"\n  前128字节:")
    for i in range(0, min(128, len(raw)), 16):
        hex_str = ' '.join(f'{b:02x}' for b in raw[i:i+16])
        print(f"    {i:04x}: {hex_str}")

    # 检查事件块结构
    # 已知: F7=event_count, F6=F7*32 表示每个事件有一个32字节索引记录
    # 但功率数据的实际采样是float32数组
    # 我们需要找到事件块头部中的时间戳

    # 在F9区域搜索可能的时间戳
    print(f"\n  搜索Unix时间戳 (1.70e9~1.80e9, 即2024-2026):")
    ts_found = []
    for i in range(0, min(len(raw), 0x10000), 4):
        val = struct.unpack_from('<I', raw, i)[0]
        if 1700000000 <= val <= 1800000000:
            ts_found.append((i, val))

    if ts_found:
        print(f"    找到 {len(ts_found)} 个 (仅显示前10):")
        for offset, ts in ts_found[:10]:
            dt = datetime.fromtimestamp(ts)
            print(f"      offset 0x{offset:06X}: {ts} = {dt.strftime('%Y-%m-%d %H:%M:%S')}")
    else:
        print(f"    未找到时间戳")

    # 检查F9+0开始是否直接是float32数据
    f32_sample = struct.unpack_from('<20f', raw, 0)
    print(f"\n  前20个float32值: {[round(v, 3) for v in f32_sample]}")

    # 寻找功率marker 0x1227
    marker_1227 = b'\x27\x12'
    pos_1227 = raw.find(marker_1227, 0, 0x100000)
    if pos_1227 >= 0:
        print(f"\n  0x1227标记首次出现: offset 0x{pos_1227:X}")
        # 显示周围数据
        ctx = raw[max(0,pos_1227-16):pos_1227+48]
        for i in range(0, len(ctx), 16):
            hex_str = ' '.join(f'{b:02x}' for b in ctx[i:i+16])
            print(f"    {hex_str}")

    return raw


def analyze_current_f9(filepath, entry):
    """分析电流HBF的F9数据区 - 解析32B索引记录"""
    f9 = entry['F9']
    f6 = entry['F6']
    sw_id = entry['sw_id']

    raw = read_at(filepath, f9, min(f6, 0x200000))

    print(f"\n{'='*60}")
    print(f"[电流] {sw_id}: F9=0x{f9:X}, F6={f6}, F7={entry['F7']}")
    print(f"{'='*60}")

    # 前128字节
    print(f"\n  前128字节:")
    for i in range(0, min(128, len(raw)), 16):
        hex_str = ' '.join(f'{b:02x}' for b in raw[i:i+16])
        print(f"    {i:04x}: {hex_str}")

    # 前几个32B记录
    n_records = f6 // 32
    print(f"\n  总记录数: {n_records} (F6/32)")

    # 统计marker分布
    marker_3277 = b'\x77\x32'
    marker_1227 = b'\x27\x12'

    records_3277 = 0
    records_1227 = 0
    records_other = 0

    print(f"\n  前10条记录:")
    for i in range(min(10, n_records)):
        rec = raw[i*32:(i+1)*32]
        u32s = struct.unpack_from('<8I', rec, 0)
        hex_str = ' '.join(f'{b:02x}' for b in rec)

        # 标记类型
        m12 = rec[12:14]
        m13 = rec[13:15]
        rtype = '?'
        if m12 == marker_1227:
            rtype = '0x1227(功率)'
        elif m13 == marker_3277:
            rtype = '0x3277(电流)@13-14'
        elif m12 == marker_3277:
            rtype = '0x3277(电流)@12-13'

        print(f"    [{i}] {hex_str}")
        print(f"         u32: {[f'0x{v:08X}' for v in u32s]}")
        print(f"         type: {rtype}")

    # 统计所有记录的类型
    for i in range(n_records):
        rec = raw[i*32:(i+1)*32]
        m12 = rec[12:14]
        m13 = rec[13:15]
        if m12 == marker_1227:
            records_1227 += 1
        elif m13 == marker_3277 or m12 == marker_3277:
            records_3277 += 1
        else:
            records_other += 1

    print(f"\n  记录统计: 0x1227={records_1227}, 0x3277={records_3277}, other={records_other}")

    # 搜索时间戳
    print(f"\n  搜索Unix时间戳:")
    ts_found = []
    for i in range(0, min(len(raw), 0x50000), 4):
        val = struct.unpack_from('<I', raw, i)[0]
        if 1700000000 <= val <= 1800000000:
            ts_found.append((i, val))

    if ts_found:
        print(f"    找到 {len(ts_found)} 个 (前10):")
        for offset, ts in ts_found[:10]:
            dt = datetime.fromtimestamp(ts)
            print(f"      offset 0x{offset:06X}: {ts} = {dt.strftime('%Y-%m-%d %H:%M:%S')}")
    else:
        print(f"    未找到")

    return raw

def main():
    # 读取两个HBF的前2MB
    size_pwr = os.path.getsize(POWER_FILE)
    size_cur = os.path.getsize(CURRENT_FILE)

    with open(POWER_FILE, 'rb') as f:
        pwr_2mb = f.read(0x200000)
    with open(CURRENT_FILE, 'rb') as f:
        cur_2mb = f.read(0x200000)

    print("="*70)
    print("目录项对比: 功率 vs 电流")
    print("="*70)

    # 找所有有效目录项
    pwr_entries = {}
    cur_entries = {}

    for sw_id in ALL_IDS:
        pe = get_dir_entry(POWER_FILE, sw_id, pwr_2mb)
        ce = get_dir_entry(CURRENT_FILE, sw_id, cur_2mb)
        if pe:
            pwr_entries[sw_id] = pe
        if ce:
            cur_entries[sw_id] = ce

    print(f"\n功率有数据开关: {len(pwr_entries)}, 电流有数据开关: {len(cur_entries)}")

    # 列出对比
    print(f"\n{'Switch':<8} {'P_F7':>8} {'P_F6':>10} {'P_F9':>12} | {'C_F7':>8} {'C_F6':>10} {'C_F9':>12} {'F7比':>8}")
    print("-"*85)

    all_sw = set(list(pwr_entries.keys()) + list(cur_entries.keys()))
    for sw_id in sorted(all_sw, key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        pe = pwr_entries.get(sw_id, {})
        ce = cur_entries.get(sw_id, {})
        p_f7 = pe.get('F7', 0)
        c_f7 = ce.get('F7', 0)
        ratio = c_f7/p_f7 if p_f7 and c_f7 else 0
        print(f"{sw_id:<8} {p_f7:>8} {pe.get('F6',0):>10} 0x{pe.get('F9',0):>010X} | {c_f7:>8} {ce.get('F6',0):>10} 0x{ce.get('F9',0):>010X} {ratio:>8.2f}")

    # ── 深度分析一个开关 ──
    # 选择1-J（功率和电流都有数据，F7不一样但都>1000）
    sw_id = '1-J'
    if sw_id in pwr_entries and sw_id in cur_entries:
        print(f"\n\n{'#'*70}")
        print(f"# 深度分析: {sw_id}")
        print(f"{'#'*70}")

        pwr_raw = analyze_power_f9(POWER_FILE, pwr_entries[sw_id])
        cur_raw = analyze_current_f9(CURRENT_FILE, cur_entries[sw_id])

        # ── 在功率F9区域搜索0x1227标记 ──
        print(f"\n\n{'='*60}")
        print(f"[功率] 在F9区搜索事件块边界标记")
        print(f"{'='*60}")

        # 寻找功率F9区域中连续的float32曲线数据前的头部
        # 搜索0x1227标记
        marker = b'\x27\x12'
        marker_positions = []
        for i in range(0, min(len(pwr_raw), 0x100000)):
            if pwr_raw[i:i+2] == marker:
                marker_positions.append(i)

        print(f"  0x1227出现次数: {len(marker_positions)} (前1MB)")
        if marker_positions:
            print(f"  前10个位置: {[f'0x{p:X}' for p in marker_positions[:10]]}")

            # 查看第一个标记周围的上下文
            for mp in marker_positions[:3]:
                print(f"\n  marker @ 0x{mp:X}:")
                ctx_start = max(0, mp - 32)
                ctx_end = min(len(pwr_raw), mp + 96)
                ctx = pwr_raw[ctx_start:ctx_end]
                for j in range(0, len(ctx), 16):
                    off = ctx_start + j
                    hex_str = ' '.join(f'{b:02x}' for b in ctx[j:j+16])
                    print(f"    0x{off:06X}: {hex_str}")

                # 也按float32解析看看
                f32_here = struct.unpack_from('<20f', pwr_raw, mp)
                print(f"    float32: {[round(v,3) for v in f32_here]}")

        # ── 在功率F9中搜索时间戳 ──
        print(f"\n\n[功率] 在F9区全范围搜索时间戳:")
        # F7=3067 个事件，每个事件可能有时间戳头部
        # 每个事件的采样数据长度 ≈ 285点 * 4B/点 ≈ 1140 字节
        # 加上可能的头部，每个事件块大约1300-1400字节

        # 搜索所有可能的时间戳
        for scan_start in range(0, min(len(pwr_raw), 0x200000), 0x10000):
            scan_end = min(scan_start + 0x10000, len(pwr_raw))
            for i in range(scan_start, scan_end - 4, 4):
                val = struct.unpack_from('<I', pwr_raw, i)[0]
                if 1700000000 <= val <= 1800000000:
                    # 检查周围是否有0x1227标记
                    ctx = pwr_raw[max(0,i-64):min(len(pwr_raw),i+128)]
                    has_1227 = b'\x27\x12' in ctx[:64] or b'\x27\x12' in ctx[64:]
                    dt = datetime.fromtimestamp(val)
                    print(f"  TS @ 0x{i:08X}: {val} = {dt.strftime('%Y-%m-%d %H:%M:%S')}  {'[near 0x1227]' if has_1227 else ''}")
                    break  # 只打印第一个
            else:
                continue
            break


if __name__ == '__main__':
    main()
