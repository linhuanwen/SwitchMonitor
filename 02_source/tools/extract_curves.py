#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
最终尝试: 从HBF文件中提取实际波形数据
策略: 直接扫描文件中所有看起来像采样数据(非标记)的连续段
特征: 不包含0x1227标记, 连续非零, 使用float32/int16可解码
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_dir_entries(filepath):
    """解析256B目录项"""
    data = read_at(filepath, 0, 0x28000)
    entries = {}
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        p = data.find(pattern)
        if p != -1 and p < 0x28000:
            desc = data[p+0x70:p+0x70+52]
            fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
            f4, f7, f6 = fields[4], fields[7], fields[6]
            if 0 < f7 < 2000 and 0 < f6 < 100000:
                entries[sw_id] = {'F4': f4, 'F7': f7, 'F6': f6, 'F3': fields[3]}
    return entries

def find_actual_data(filepath, start_offset, search_size, f7):
    """在给定区域搜索匹配f7的非标记采样数据"""
    raw = read_at(filepath, start_offset, search_size)
    if len(raw) < 100:
        return None

    # 找0x1227标记来确定记录边界
    MARKER = b'\x27\x12\x00\x00'
    marker_pos = []
    for i in range(len(raw) - 4):
        if raw[i:i+4] == MARKER:
            marker_pos.append(i)

    if len(marker_pos) < 2:
        # 没有标记, 直接尝试解码
        pass

    # 尝试在标记之间找数据
    for i in range(len(marker_pos) - 1):
        gap_start = marker_pos[i] + 32  # 跳过32B记录
        gap_end = marker_pos[i + 1]
        gap = raw[gap_start:gap_end]

        if len(gap) < 40:
            continue

        # 跳过全零
        nz = sum(1 for b in gap[:min(1024, len(gap))] if b != 0)
        if nz < 20:
            continue

        # 尝试float32
        for bps in [4, 2]:
            if bps == 4:
                n = min(f7, len(gap) // 4) if f7 > 0 else min(500, len(gap) // 4)
                vals = [struct.unpack('<f', gap[j*4:(j+1)*4])[0] for j in range(n)]
            else:
                n = min(f7, len(gap) // 2) if f7 > 0 else min(500, len(gap) // 2)
                vals = [struct.unpack('<h', gap[j*2:(j+1)*2])[0] for j in range(n)]

            z10 = sum(1 for v in vals[:10] if abs(v) < 0.05)
            peaks = [v for v in vals[:50] if v > 1.5]
            mid = vals[30:len(vals)-15] if len(vals) > 45 else []
            steady = [v for v in mid if 0.1 < abs(v) < 1.0] if mid else []
            t0 = sum(1 for v in vals[-8:] if abs(v) < 0.05)

            if z10 >= 3 and len(peaks) >= 1 and len(steady) > 5:
                print(f"\n      ✅ 找到曲线 @ gap +{gap_start} (markers @ {marker_pos[i]},{marker_pos[i+1]})")
                print(f"      {bps*8}bit: z10={z10} peaks={len(peaks)} steady={len(steady)} t0={t0}")
                print(f"      前50: {[round(v,3) for v in vals[:50]]}")
                return vals

    return None

def main():
    print("HBF 曲线数据提取 - 最终方案")
    print("="*60)

    for fpath, label in [(POWER_1, "power1"), (POWER_2, "power2")]:
        print(f"\n{label}: {os.path.basename(fpath)}")
        entries = parse_dir_entries(fpath)
        print(f"  有效目录项: {len(entries)}")

        for sw_id, info in sorted(entries.items()):
            f4, f7, f6 = info['F4'], info['F7'], info['F6']
            if f6 < 100:
                continue

            # 读取F4处的数据
            # 需要读足够多的数据: 子索引记录 + 采样数据
            read_size = min(f6 * 4 + 262144, 524288)
            data = read_at(fpath, f4, read_size)

            nz_first = sum(1 for b in data[:min(4096, len(data))] if b != 0)
            if nz_first < 50:
                continue  # 跳过几乎全空的块

            print(f"\n  {sw_id} @ 0x{f4:x}: F7={f7} F6={f6} nz={nz_first}")

            # 找0x1227标记
            MARKER = b'\x27\x12\x00\x00'
            markers = []
            for i in range(len(data) - 4):
                if data[i:i+4] == MARKER:
                    markers.append(i)

            print(f"    0x1227标记: {len(markers)} 个")

            if len(markers) < 2:
                continue

            # 找标记之间的数据
            found = False
            for i in range(min(len(markers) - 1, 200)):
                gap_start = markers[i] + 32
                gap_end = markers[i + 1]
                gap = data[gap_start:gap_end]
                if len(gap) < 40:
                    continue

                nz = sum(1 for b in gap[:min(1024, len(gap))] if b != 0)
                if nz < 20:
                    continue

                # float32
                n = min(300, len(gap) // 4)
                f32 = [struct.unpack('<f', gap[j*4:(j+1)*4])[0] for j in range(n)]
                z10 = sum(1 for v in f32[:10] if abs(v) < 0.05)
                peaks = [v for v in f32[:50] if v > 1.0]
                mid = f32[30:min(len(f32)-15, 280)]
                steady = [v for v in mid if 0.1 < abs(v) < 1.0] if mid else []
                t0 = sum(1 for v in f32[-8:] if abs(v) < 0.05)

                if z10 >= 3 and len(peaks) >= 1 and len(steady) > 5:
                    print(f"    ✅ GAP[{i}] ({markers[i]}→{markers[i+1]}): {len(gap)}B")
                    print(f"       float32: z10={z10} peaks={len(peaks)} steady={len(steady)} t0={t0}")
                    print(f"       前50: {[round(v,3) for v in f32[:50]]}")
                    found = True
                    break

                # int16
                n = min(300, len(gap) // 2)
                i16 = [struct.unpack('<h', gap[j*2:(j+1)*2])[0] for j in range(n)]
                z10 = sum(1 for v in i16[:10] if abs(v) < 5)
                peaks = [v for v in i16[:50] if v > 100]
                mid = i16[30:min(len(i16)-15, 280)]
                steady = [v for v in mid if 10 < abs(v) < 100] if mid else []
                t0 = sum(1 for v in i16[-8:] if abs(v) < 5)

                if z10 >= 3 and len(peaks) >= 1 and len(steady) > 5:
                    print(f"    ✅ GAP[{i}] ({markers[i]}→{markers[i+1]}): {len(gap)}B")
                    print(f"       int16: z10={z10} peaks={len(peaks)} steady={len(steady)} t0={t0}")
                    print(f"       前50: {i16[:50]}")
                    found = True
                    break

            if not found:
                # 没有在gap中找到, 试试最后一个标记之后
                last_marker_end = markers[-1] + 32
                if last_marker_end < len(data) - 40:
                    tail = data[last_marker_end:last_marker_end + 65536]
                    nz = sum(1 for b in tail[:1024] if b != 0)
                    if nz > 50:
                        print(f"    标记后数据: {len(tail)}B, nz={nz}")
                        f32 = [struct.unpack('<f', tail[j*4:(j+1)*4])[0] for j in range(min(100, len(tail)//4))]
                        print(f"    float32前30: {[round(v,3) for v in f32[:30]]}")

    print("\n✅ 曲线提取完成")

if __name__ == '__main__':
    main()
