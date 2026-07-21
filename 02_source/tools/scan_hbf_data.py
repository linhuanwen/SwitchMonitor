#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
扫描HBF文件中所有大的非零段，定位实际采样数据
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

MARKER = b'\x27\x12\x00\x00'

def find_nonzero_segments(filepath, min_size=4096, step=65536):
    """找所有大于min_size的非零段"""
    file_size = os.path.getsize(filepath)
    segments = []
    in_segment = False
    seg_start = 0
    seg_nz = 0
    seg_total = 0

    for pos in range(0, file_size, step):
        chunk_size = min(step, file_size - pos)
        chunk = read_at(filepath, pos, chunk_size)
        nz = sum(1 for b in chunk if b != 0)
        density = nz / len(chunk) if chunk else 0

        if density > 0.1 and not in_segment:
            in_segment = True
            seg_start = pos
            seg_nz = nz
            seg_total = len(chunk)
        elif density > 0.1 and in_segment:
            seg_nz += nz
            seg_total += len(chunk)
        elif density <= 0.1 and in_segment:
            in_segment = False
            seg_size = pos - seg_start
            if seg_size >= min_size:
                segments.append((seg_start, seg_size, seg_nz / max(seg_total, 1)))
        elif density <= 0.1:
            pass

    return segments

def analyze_segment(filepath, start, size):
    """分析一个非零段"""
    raw = read_at(filepath, start, min(size, 16384))

    # 找0x1227标记
    markers = []
    for i in range(len(raw) - 4):
        if raw[i:i+4] == MARKER:
            markers.append(start + i)

    # 找float32特征值
    float_count = min(4000, len(raw) // 4)
    floats = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(float_count)]

    # 统计float32值的分布
    abs_vals = [abs(f) for f in floats]
    in_range = sum(1 for f in floats if 0.05 < abs(f) < 10.0)
    near_zero = sum(1 for f in floats[:20] if abs(f) < 0.05)
    huge = sum(1 for f in floats if abs(f) > 1e10)

    return {
        'start': start,
        'size': size,
        'marker_count': len(markers),
        'marker_positions': markers[:5],
        'float_in_range': in_range,
        'float_near_zero': near_zero,
        'float_huge': huge,
        'first_floats': [round(f, 3) for f in floats[:30]],
    }

def main():
    pf_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"

    print("扫描非零段...")
    segments = find_nonzero_segments(pf_1, min_size=32768)

    print(f"找到 {len(segments)} 个大于32KB的非零段:")
    for start, size, density in segments[:30]:
        print(f"  0x{start:09x} - 0x{start+size:09x}: {size:,} bytes, 密度={density:.1%}")

    # 分析前几个段
    print(f"\n详细分析前10个段:")
    for start, size, density in segments[:10]:
        info = analyze_segment(pf_1, start, size)
        print(f"\n0x{start:09x} (size={size:,}, density={density:.1%}):")
        print(f"  0x1227标记: {info['marker_count']}个 @ {[f'0x{x:x}' for x in info['marker_positions']]}")
        print(f"  float32: in_range(0.05-10)={info['float_in_range']} near0_start={info['float_near_zero']} huge={info['float_huge']}")
        print(f"  first30: {info['first_floats']}")

    # 重点: 查找第一个看起来像功率曲线的段
    print(f"\n\n寻找功率曲线特征段:")
    for start, size, density in segments:
        raw = read_at(pf_1, start, min(size, 65536))
        # 尝试int16解释
        for bps in [2, 4]:
            n = min(600, len(raw) // bps)
            if bps == 2:
                vals = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(n)]
            else:
                vals = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(n)]

            z10 = sum(1 for v in vals[:10] if abs(v) < 0.1)
            peaks = [v for v in vals[:50] if v > 1.5]
            mid = vals[30:len(vals)-20]
            steady = [v for v in mid if 0.1 < abs(v) < 1.0] if mid else []
            t0 = sum(1 for v in vals[-10:] if abs(v) < 0.1)

            if z10 >= 4 and len(peaks) >= 1 and len(steady) > 10:
                enc = 'int16' if bps == 2 else 'float32'
                print(f"\n  ✅ 0x{start:09x} {enc}: z10={z10} peaks={len(peaks)}(max={max(peaks):.2f}) steady={len(steady)} t0={t0}")
                print(f"    前30: {[round(v,3) for v in vals[:30]]}")
                print(f"    中段: {[round(v,3) for v in vals[100:130]]}")
                print(f"    尾20: {[round(v,3) for v in vals[-20:]]}")

                # 读取这一段更多的数据来确认
                more_raw = read_at(pf_1, start, 131072)
                more_vals = [struct.unpack('<h', more_raw[i*2:(i+1)*2])[0] for i in range(min(5000, len(more_raw)//2))]
                print(f"    全段int16 (5000点) 统计: min={min(more_vals)} max={max(more_vals)} mean={sum(more_vals)/len(more_vals):.1f}")
                break
        else:
            continue
        break

    print("\n✅ 扫描完成")

if __name__ == '__main__':
    main()
