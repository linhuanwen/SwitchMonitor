#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全面扫描 HBF 文件，映射所有非零区域。
7月份数据应该有足够的实际内容。
"""
import struct, sys, os
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

def find_nonzero_regions(filepath, min_run=64, chunk_size=1024*1024):
    """扫描文件，找到所有非零连续区域（>= min_run 字节）"""
    file_size = os.path.getsize(filepath)
    regions = []

    with open(filepath, 'rb') as f:
        offset = 0
        in_nz = False
        nz_start = 0
        nz_bytes = bytearray()

        while offset < file_size:
            chunk = f.read(min(chunk_size, file_size - offset))
            if not chunk:
                break

            for i, b in enumerate(chunk):
                abs_offset = offset + i
                if b != 0:
                    if not in_nz:
                        in_nz = True
                        nz_start = abs_offset
                        nz_bytes = bytearray([b])
                    else:
                        nz_bytes.append(b)
                else:
                    if in_nz:
                        # 检查是否应该结束这个run
                        # 允许少量零字节（连续不超过8个）
                        zero_count = 0
                        for j in range(i+1, min(i+9, len(chunk))):
                            if chunk[j] == 0:
                                zero_count += 1
                            else:
                                break
                        if zero_count >= 8:
                            if len(nz_bytes) >= min_run:
                                regions.append((nz_start, len(nz_bytes), bytes(nz_bytes[:256])))
                            in_nz = False
                            nz_bytes = bytearray()
                        else:
                            nz_bytes.append(b)
                            # 也加上那些零字节
                            for j in range(i+1, i+1+zero_count):
                                nz_bytes.append(0)

            offset += len(chunk)

        if in_nz and len(nz_bytes) >= min_run:
            regions.append((nz_start, len(nz_bytes), bytes(nz_bytes[:256])))

    return regions

def analyze_region(filepath, offset, size, preview):
    """分析一个非零区域"""
    with open(filepath, 'rb') as f:
        f.seek(offset)
        data = f.read(min(size, 4096))

    result = {
        'offset': offset,
        'size': size,
        'has_1227': b'\x27\x12\x00\x00' in data,
        'has_3277': b'\x77\x32\x00\x00' in data,
        'has_hhcsmfzz': b'hhcsmfzz' in data,
    }

    # 检查是否是索引记录区域（32B对齐的0x1227标记）
    markers_1227 = []
    for i in range(len(data) - 4):
        if data[i:i+4] == b'\x27\x12\x00\x00':
            markers_1227.append(i)

    result['marker_count'] = len(markers_1227)

    # 检查32B对齐
    aligned = sum(1 for m in markers_1227 if m % 32 == 0)
    result['aligned_markers'] = aligned

    # 尝试 float32 解码
    f32_vals = []
    for i in range(0, min(len(data), 4096) - 4, 4):
        try:
            v = struct.unpack('<f', data[i:i+4])[0]
            if not (abs(v) > 1e30 or abs(v) < 1e-30):
                f32_vals.append(v)
        except:
            pass

    if f32_vals:
        in_range = sum(1 for v in f32_vals if 0.01 < v < 5000)
        result['f32_total'] = len(f32_vals)
        result['f32_in_power_range'] = in_range
        result['f32_max'] = max(f32_vals)
        result['f32_min'] = min(f32_vals)
        result['f32_sample'] = f32_vals[:20]

    # 尝试 int16 解码
    i16_vals = []
    for i in range(0, min(len(data), 4096) - 2, 2):
        try:
            v = struct.unpack('<h', data[i:i+2])[0]
            i16_vals.append(v)
        except:
            pass

    if i16_vals:
        in_range = sum(1 for v in i16_vals if 10 < v < 5000)
        result['i16_total'] = len(i16_vals)
        result['i16_in_power_range'] = in_range
        result['i16_max'] = max(i16_vals)
        result['i16_min'] = min(i16_vals)

    return result

def main():
    print("HBF 文件全面数据密度扫描")
    print("=" * 70)

    for dirpath, label in [(POWER_DIR, "功率"), (CURRENT_DIR, "电流")]:
        if not os.path.exists(dirpath):
            print(f"\n{label}目录不存在: {dirpath}")
            continue

        for fname in sorted(os.listdir(dirpath)):
            if not fname.endswith('.hbf'):
                continue
            fpath = os.path.join(dirpath, fname)
            fsize = os.path.getsize(fpath)
            print(f"\n{'='*70}")
            print(f"{label}/{fname} ({fsize:,} bytes = {fsize/1024/1024:.0f} MB)")
            print(f"{'='*70}")

            # 找非零区域
            print("扫描非零区域...")
            regions = find_nonzero_regions(fpath, min_run=64)

            total_nz = sum(r[1] for r in regions)
            print(f"非零区域数: {len(regions)}")
            print(f"非零总字节: {total_nz:,} ({100*total_nz/fsize:.2f}%)")

            # 分析每个区域
            interesting = []
            for offset, size, preview in regions:
                r = analyze_region(fpath, offset, size, preview)
                r['preview'] = preview
                interesting.append(r)

            # 按大小排序，显示前20个最大的区域
            interesting.sort(key=lambda r: r['size'], reverse=True)

            print(f"\n最大的非零区域 (前20):")
            print(f"{'Offset':>12} {'Size':>12} {'1227标记':>10} {'f32功率范围':>12} {'类型':<30}")
            print("-" * 85)

            for r in interesting[:20]:
                type_desc = ""
                if r['has_hhcsmfzz']:
                    type_desc = "文件头"
                elif r['aligned_markers'] > 5:
                    type_desc = f"索引区({r['aligned_markers']}条对齐记录)"
                elif r['marker_count'] > 0:
                    type_desc = f"混合({r['marker_count']}标记)"
                elif 'f32_in_power_range' in r and r['f32_in_power_range'] > 10:
                    pct = 100 * r['f32_in_power_range'] / max(1, r['f32_total'])
                    type_desc = f"可能的功率数据({pct:.0f}%在0-5000范围)"
                else:
                    type_desc = "未知"

                f32_info = ""
                if 'f32_in_power_range' in r:
                    f32_info = f"{r['f32_in_power_range']}/{r['f32_total']}"

                print(f"0x{r['offset']:010x} {r['size']:>12,} {r['marker_count']:>10} {f32_info:>12} {type_desc:<30}")

            # 重点分析可能的功率数据区域
            power_candidates = [r for r in interesting if 'f32_in_power_range' in r and r['f32_in_power_range'] > 5]
            if power_candidates:
                print(f"\n🔍 可能的功率数据区域详情:")
                for r in power_candidates[:5]:
                    print(f"\n  Offset 0x{r['offset']:010x}, Size {r['size']:,} bytes")
                    print(f"  f32范围: {r.get('f32_min', 0):.3f} ~ {r.get('f32_max', 0):.3f}")
                    print(f"  f32功率范围内: {r.get('f32_in_power_range', 0)}/{r.get('f32_total', 0)}")
                    if 'f32_sample' in r:
                        print(f"  f32前20: {[round(v, 3) for v in r['f32_sample'][:20]]}")

                    # 读取更多数据做详细分析
                    with open(fpath, 'rb') as f:
                        f.seek(r['offset'])
                        raw = f.read(min(r['size'], 1024))

                    # 检查32B结构
                    print(f"  十六进制前128字节:")
                    for i in range(0, min(128, len(raw)), 16):
                        hex_str = ' '.join(f'{b:02x}' for b in raw[i:i+16])
                        print(f"    {i:4d}: {hex_str}")

    print(f"\n{'='*70}")
    print("扫描完成")

if __name__ == '__main__':
    main()
