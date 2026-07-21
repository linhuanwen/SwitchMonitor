#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
扫描HBF文件找 27 12 00 00 标记，定位索引记录和采样数据
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"
CURRENT_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\1.hbf"

MARKER = b'\x27\x12\x00\x00'

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def scan_markers(filepath, max_scan=200_000_000):
    """扫描文件找所有0x1227标记位置"""
    file_size = os.path.getsize(filepath)
    scan_size = min(file_size, max_scan)

    marker_positions = []
    chunk_size = 10_000_000  # 10MB chunks

    with open(filepath, 'rb') as f:
        offset = 0
        while offset < scan_size:
            chunk = f.read(min(chunk_size, scan_size - offset))
            if not chunk:
                break
            pos = 0
            while True:
                p = chunk.find(MARKER, pos)
                if p == -1:
                    break
                marker_positions.append(offset + p)
                pos = p + 1
            offset += len(chunk)

    return marker_positions

def analyze_marker_context(filepath, marker_pos):
    """分析标记周围的32B记录"""
    # 标记可能在32B记录的开头或结尾
    # 在开头: pos+0=marker, pos+28应该也是marker(下一条记录)
    # 在结尾: pos-28=marker(上一条记录)

    file_size = os.path.getsize(filepath)

    # 方案A: 标记在开头
    if marker_pos + 32 <= file_size:
        rec_a = read_at(filepath, marker_pos, 32)
        u32s_a = [struct.unpack('<I', rec_a[j:j+4])[0] for j in range(0, 32, 4)]
        ts_a = u32s_a[0]  # 可能是时间戳
        marker_a = u32s_a[7]  # 应该是0x1227 (4647)
    else:
        u32s_a = None

    # 方案B: 标记在结尾
    rec_start = marker_pos - 28
    if rec_start >= 0:
        rec_b = read_at(filepath, rec_start, 32)
        u32s_b = [struct.unpack('<I', rec_b[j:j+4])[0] for j in range(0, 32, 4)]
        ts_b = u32s_b[0]
        marker_b = u32s_b[7]
    else:
        u32s_b = None

    return u32s_a, u32s_b

def main():
    print("HBF 扫描 0x1227 标记")
    print("="*60)

    for fpath, label in [(POWER_1, "功率1.hbf"), (POWER_2, "功率2.hbf"), (CURRENT_1, "电流1.hbf")]:
        if not os.path.exists(fpath):
            print(f"\n{label}: 文件不存在")
            continue

        print(f"\n扫描 {label}...")
        markers = scan_markers(fpath)
        print(f"找到 {len(markers)} 个 0x1227 标记")

        if not markers:
            continue

        # 取样本分析 — 从不同位置取
        samples = []
        step = max(1, len(markers) // 20)
        for i in range(0, len(markers), step):
            samples.append(markers[i])
        samples = samples[:20]

        print(f"\n样本分析 ({len(samples)} 个):")
        valid_timestamps = []
        for mp in samples[:10]:
            u32s_a, u32s_b = analyze_marker_context(fpath, mp)

            # 检查方案A (标记在开头)
            if u32s_a:
                ts = u32s_a[0]
                marker_val = u32s_a[7]
                if 1_500_000_000 < ts < 1_900_000_000 and marker_val == 0x1227:
                    dt = datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S')
                    print(f"  0x{mp:09x} [开头]: TS={ts} ({dt}) seq={u32s_a[4]} ptr=0x{u32s_a[6]:06x}")
                    valid_timestamps.append((mp, ts, u32s_a, 'head'))
                    continue

            # 检查方案B (标记在结尾)
            if u32s_b:
                ts = u32s_b[0]
                marker_val = u32s_b[7]
                if 1_500_000_000 < ts < 1_900_000_000 and marker_val == 0x1227:
                    dt = datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S')
                    rec_start = mp - 28
                    print(f"  0x{rec_start:09x} [结尾]: TS={ts} ({dt}) seq={u32s_b[4]} ptr=0x{u32s_b[6]:06x}")
                    valid_timestamps.append((rec_start, ts, u32s_b, 'tail'))
                    continue

            # 都不匹配,打印hex
            if u32s_a:
                print(f"  0x{mp:09x} [开头-非TS]: u32={[f'0x{v:08x}' for v in u32s_a[:4]]}...")
            if u32s_b and u32s_b[0] != 0:
                print(f"  0x{mp-28:09x} [结尾-非TS]: u32={[f'0x{v:08x}' for v in u32s_b[:4]]}...")

        if valid_timestamps:
            print(f"\n找到 {len(valid_timestamps)} 个有效索引记录(样本中)")

            # 选择一个有效的，检查其数据指针
            for rec_offset, ts, u32s, pos_type in valid_timestamps[:3]:
                data_ptr = u32s[6]
                print(f"\n记录 @ 0x{rec_offset:09x}: TS={datetime.fromtimestamp(ts)} data_ptr=0x{data_ptr:06x} ({data_ptr})")

                # 在功率1和功率2中都检查这个偏移
                for target_path, target_name in [(POWER_1, "power1"), (POWER_2, "power2")]:
                    if data_ptr >= os.path.getsize(target_path) - 5000:
                        continue
                    raw = read_at(target_path, data_ptr, 256)
                    nz = sum(1 for b in raw if b != 0)
                    if nz > 30:
                        print(f"  {target_name} @ 0x{data_ptr:06x}: nz={nz}/256")
                        for j in range(0, min(160, len(raw)), 32):
                            line = raw[j:j+32]
                            print(f"    +{j:3d}: {' '.join(f'{b:02x}' for b in line)}")
                        # 尝试float32
                        f32 = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(60, len(raw)//4))]
                        print(f"    float32: {[round(v,3) for v in f32[:30]]}")
                        break
                else:
                    print(f"  ❌ 数据指针 0x{data_ptr:06x} 在两个文件中都无数据")

    print("\n✅ 扫描完成")

if __name__ == '__main__':
    main()
