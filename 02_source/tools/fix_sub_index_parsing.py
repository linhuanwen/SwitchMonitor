#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
修复 F4 子索引的字节错位问题，找到真正的数据指针并提取功率曲线
关键思路：不假设 u32 对齐，在 32B 记录中搜索 0x1227 标记的位置
"""
import struct
import sys
import os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_marker_in_32b(raw_record):
    """在32B记录中找 0x1227 标记的字节位置，返回偏移列表"""
    positions = []
    marker = b'\x27\x12'  # 0x1227 in LE bytes
    pos = 0
    while True:
        pos = raw_record.find(marker, pos)
        if pos == -1:
            break
        positions.append(pos)
        pos += 1
    return positions

def parse_data_pointer(raw_record, marker_pos):
    """
    给定0x1227在记录中的字节位置，推断其他字段的布局
    看 marker 前面和后面的 u32 值
    """
    u32s = []
    for j in range(8):
        u32s.append(struct.unpack_from('<I', raw_record, j*4)[0])
    return u32s

def scan_f4_sub_index(fpath, f4, f7, sw_id, file_size):
    """扫描 F4 子索引，用灵活的标记搜索"""
    if f4 <= 0 or f7 <= 0:
        return []

    raw = read_at(fpath, f4, min(f7 * 32, 0x200000))
    if len(raw) < 32:
        return []

    results = []
    for i in range(f7):
        off = i * 32
        if off + 32 > len(raw):
            break
        rec = raw[off:off+32]

        # 跳过全零
        if set(rec) == {0}:
            continue

        # 找 0x1227 标记
        marker_positions = find_marker_in_32b(rec)
        if not marker_positions:
            # 也试试找 0x3277 (电流标记)
            marker_positions = find_marker_in_32b(rec.replace(b'\x77\x32', b'\x27\x12'))
            if not marker_positions:
                results.append((i, 'no_marker', None, rec))
                continue

        # 对每个标记位置，尝试解析
        for mp in marker_positions:
            u32s = parse_data_pointer(rec, mp)

            # 找可能的 data_ptr: 在标记附近且值在合理文件偏移范围
            for j, v in enumerate(u32s):
                if 0 < v < file_size and v > 0x10000:  # 大于64KB的偏移
                    # 检查这个偏移处是否有实际数据
                    results.append((i, f'marker@{mp}_u32[{j}]=0x{v:x}', v, rec))
                    break  # 每个标记只取第一个候选
            else:
                results.append((i, f'marker@{mp}_no_ptr', None, rec))

    return results

def extract_float32_curve(raw, max_samples=1200):
    """从原始数据中提取float32曲线"""
    f32 = []
    for j in range(min(max_samples, len(raw)//4)):
        f32.append(struct.unpack_from('<f', raw, j*4)[0])

    # 找最好的连续段
    best_segment = None
    in_nz = False
    start = 0
    for j, v in enumerate(f32):
        if abs(v) > 0.01 and not in_nz:
            in_nz = True
            start = j
        elif abs(v) < 0.001 and in_nz:
            in_nz = False
            seg_len = j - start
            if seg_len > 50:
                seg = f32[start:j]
                peak = max(seg)
                if 0.1 < peak < 10:  # 合理功率范围 KW
                    if best_segment is None or seg_len > best_segment['len']:
                        best_segment = {
                            'start': start, 'end': j, 'len': seg_len,
                            'peak': peak, 'values': seg
                        }
    if in_nz:
        j = len(f32)
        seg_len = j - start
        if seg_len > 50:
            seg = f32[start:j]
            peak = max(seg)
            if 0.1 < peak < 10:
                if best_segment is None or seg_len > best_segment['len']:
                    best_segment = {
                        'start': start, 'end': j, 'len': seg_len,
                        'peak': peak, 'values': seg
                    }

    return best_segment

def describe_curve_from_raw(raw, name, max_read=65536):
    """读取数据区并描述曲线"""
    results = {}
    # 尝试从不同偏移开始读取
    for skip in [0, 32, 64, 128, 256, 512, 1024]:
        body = raw[skip:skip+max_read]
        curve = extract_float32_curve(body)
        if curve and curve['len'] > 50:
            key = f"{name} skip={skip}"
            results[key] = curve
    return results

def main():
    fpath2 = os.path.join(POWER_DIR, '2.hbf')
    size2 = os.path.getsize(fpath2)
    data2 = read_at(fpath2, 0, 0x200000)

    print("="*70)
    print("F4 子索引 → 数据指针 → 功率曲线 (修复字节错位)")
    print("="*70)

    # Step 1: 对几个关键道岔，扫描 F4 子索引
    # 选事件数多的: 3-X(55 TypeA), 9-X(82 TypeB), 19-J(97 TypeA), 17-J(36)
    print("\n【Step 1】F4 子索引扫描 — 找真正的数据指针")

    all_data_ptrs = {}  # sw_id -> [data_ptr, ...]

    for sw_id in SWITCH_IDS:
        pos = data2.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data2[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4, f7, f9 = f[4], f[7], f[9]
        if not (0 < f4 < size2 and 10 < f7 < 100000):
            continue

        records = scan_f4_sub_index(fpath2, f4, f7, sw_id, size2)

        # 分类
        with_ptr = [r for r in records if r[2] is not None]
        no_ptr = [r for r in records if r[2] is None]

        if with_ptr:
            # 收集所有唯一的 data_ptr
            ptrs = list(set(r[2] for r in with_ptr))
            all_data_ptrs[sw_id] = ptrs

        print(f"\n  [{sw_id}] F4=0x{f4:x} F7={f7} F9=0x{f9:x}")
        print(f"    总非零记录: {len(records)}/{f7}")
        print(f"    有数据指针: {len(with_ptr)}")
        if with_ptr:
            # 显示前几个
            for r in with_ptr[:3]:
                print(f"      [{r[0]}] {r[1]}")
            if len(with_ptr) > 3:
                print(f"      ... 还有 {len(with_ptr)-3} 个")
        if no_ptr:
            # 显示前几个无标记的记录
            for r in no_ptr[:2]:
                hex_str = ' '.join(f'{b:02x}' for b in r[3][:32])
                print(f"      [{r[0]}] {r[1]}: {hex_str}")
            if len(no_ptr) > 2:
                print(f"      ... 还有 {len(no_ptr)-2} 个无标记记录")

    # Step 2: 对于有 data_ptr 的道岔，追踪数据位置
    print(f"\n\n【Step 2】追踪数据指针 → 实际采样数据")

    curve_summary = {}

    for sw_id, ptrs in all_data_ptrs.items():
        print(f"\n  [{sw_id}] {len(ptrs)} 个唯一数据指针")

        # 取几个不同指针验证
        valid_curves = 0
        sample_ptrs = sorted(ptrs)[:5]

        for dp in sample_ptrs:
            if dp + 0x1227 > size2:
                continue

            raw_block = read_at(fpath2, dp, 0x1227)

            # 检查块头
            header_hex = ' '.join(f'{b:02x}' for b in raw_block[:64])
            nz_bytes = sum(1 for b in raw_block[:1024] if b != 0)

            if nz_bytes < 50:
                continue

            # 尝试提取曲线
            curves = describe_curve_from_raw(raw_block, f"{sw_id} dp=0x{dp:x}")

            if curves:
                for key, curve in curves.items():
                    print(f"    ✅ {key}: len={curve['len']} 峰值={curve['peak']:.3f}KW")
                    print(f"       前10: {[round(v,3) for v in curve['values'][:10]]}")
                    print(f"       峰值附近: {[round(v,3) for v in curve['values'][max(0,curve['values'].index(curve['peak'])-5):curve['values'].index(curve['peak'])+5]]}")
                    valid_curves += 1
                    if sw_id not in curve_summary:
                        curve_summary[sw_id] = []
                    curve_summary[sw_id].append(curve)

            if valid_curves == 0 and nz_bytes > 50:
                # 没有找到有效曲线，但数据不为零 — dump原始字节
                print(f"    ⚠️ dp=0x{dp:x} 有{nz_bytes}个非零字节但无有效曲线")
                print(f"       块头: {header_hex}")

    # Step 3: 汇总
    print(f"\n\n{'='*70}")
    print(f"【Step 3】汇总：成功提取曲线的道岔")
    print(f"{'='*70}")

    for sw_id, curves in sorted(curve_summary.items()):
        lengths = [c['len'] for c in curves]
        peaks = [c['peak'] for c in curves]
        print(f"  {sw_id}: {len(curves)}条曲线  长度={min(lengths)}-{max(lengths)}  峰值={min(peaks):.2f}-{max(peaks):.2f}KW")


if __name__ == '__main__':
    main()
