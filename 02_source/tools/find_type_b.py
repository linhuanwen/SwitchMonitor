#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
关键洞察：Type B 记录（标记在末尾 0x1227，开头是 Unix 时间戳）散布在文件中。
之前发现在 0x9cb8 区域有合法时间戳（2024-2026年）。
这些 Type B 记录包含 data_ptr (u32[6])，可能指向实际的采样数据。

策略：
1. 扫描文件找所有 Type B 记录
2. 提取时间戳和 data_ptr
3. 读取 data_ptr 指向的数据
4. 尝试各种解码
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"
CURRENT_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\1.hbf"
CURRENT_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

MARKER = b'\x27\x12\x00\x00'
CURRENT_MARKER = b'\x77\x32\x00\x00'

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_type_b_records(filepath, marker=MARKER, scan_limit=200_000_000):
    """
    找所有 Type B 记录：
    特征是 u32[7] = 0x1227（标记在32B记录的末尾）
    并且 u32[0] 是合法的 Unix 时间戳（2023-2026: 1_670_000_000 ~ 1_800_000_000）
    """
    file_size = os.path.getsize(filepath)
    scan_size = min(file_size, scan_limit)

    records = []
    chunk_size = 10_000_000

    with open(filepath, 'rb') as f:
        offset = 0
        while offset < scan_size:
            chunk = f.read(min(chunk_size, scan_size - offset))
            if not chunk:
                break

            # 找所有 marker 位置
            pos = 0
            while True:
                p = chunk.find(marker, pos)
                if p == -1:
                    break

                # 检查是否是 Type B: 标记在32B记录的offset 28处
                rec_start = p - 28
                if rec_start >= 0 and p + 4 <= len(chunk):
                    rec = chunk[rec_start:rec_start+32]
                    if len(rec) == 32:
                        u32s = struct.unpack('<8I', rec)
                        ts = u32s[0]
                        marker_at_end = u32s[7]

                        # Type B: u32[7]=marker_val, u32[0]=valid timestamp
                        if marker_at_end == 0x1227 and 1_500_000_000 < ts < 1_900_000_000:
                            abs_offset = offset + rec_start
                            dt = datetime.fromtimestamp(ts)
                            data_ptr = u32s[6]
                            records.append({
                                'file_offset': abs_offset,
                                'timestamp': ts,
                                'datetime': dt,
                                'data_ptr': data_ptr,
                                'u32s': u32s,
                                'seq': u32s[4],
                                'const': u32s[5],
                                'u32_1': u32s[1],  # struct marker? 0x08DE...
                                'u32_2': u32s[2],  # incrementing value
                                'u32_3': u32s[3],  # always 0
                            })

                pos = p + 1

            offset += len(chunk)

    return records

def try_decode_power_data(raw, label=""):
    """Try multiple decodings of raw data as power samples"""
    results = []

    # float32 LE
    n_f32 = len(raw) // 4
    if n_f32 >= 20:
        f32 = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(min(n_f32, 500))]
        valid = [v for v in f32 if abs(v) < 1e6 and abs(v) > 1e-12]
        if len(valid) >= 20:
            peak = max(valid)
            head_zero = sum(1 for v in valid[:5] if abs(v) < 0.1)
            results.append(('f32_le', valid, peak, head_zero))

    # int16 LE
    n_i16 = len(raw) // 2
    if n_i16 >= 20:
        i16 = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(min(n_i16, 500))]
        valid = [v for v in i16 if abs(v) < 100000]
        if len(valid) >= 20:
            peak = max(valid)
            head_zero = sum(1 for v in valid[:5] if abs(v) < 5)
            results.append(('i16_le', valid, peak, head_zero))

    # uint16 LE
    if n_i16 >= 20:
        u16 = [struct.unpack('<H', raw[i*2:(i+1)*2])[0] for i in range(min(n_i16, 500))]
        valid = [v for v in u16 if v < 100000]
        if len(valid) >= 20:
            peak = max(valid)
            head_zero = sum(1 for v in valid[:5] if v < 5)
            results.append(('u16_le', valid, peak, head_zero))

    return results

def main():
    print("Type B 记录扫描 — 找含有合法时间戳和 data_ptr 的记录")
    print("=" * 70)

    for fpath, label, marker in [
        (POWER_1, "功率1.hbf", MARKER),
        (POWER_2, "功率2.hbf", MARKER),
        (CURRENT_1, "电流1.hbf", CURRENT_MARKER),
        (CURRENT_2, "电流2.hbf", CURRENT_MARKER),
    ]:
        if not os.path.exists(fpath):
            continue

        print(f"\n{'='*70}")
        print(f"扫描 {label}...")
        records = find_type_b_records(fpath, marker)

        print(f"找到 {len(records)} 条 Type B 记录（合法时间戳）")

        if not records:
            print("  无记录，尝试扫描更大范围...")
            records = find_type_b_records(fpath, marker, scan_limit=500_000_000)
            print(f"  扩大扫描后: {len(records)} 条")

        if not records:
            continue

        # 时间范围
        timestamps = [r['timestamp'] for r in records]
        print(f"时间范围: {datetime.fromtimestamp(min(timestamps))} ~ {datetime.fromtimestamp(max(timestamps))}")
        print(f"记录数: {len(records)}")

        # data_ptr 分布
        data_ptrs = [r['data_ptr'] for r in records]
        print(f"data_ptr 范围: 0x{min(data_ptrs):x} ~ 0x{max(data_ptrs):x}")

        # const 分布（可能是道岔ID）
        consts = set(r['const'] for r in records)
        print(f"不同 const 值: {len(consts)} — {sorted([hex(c) for c in consts])[:10]}...")

        # 按 data_ptr 排序，选几个分析
        records.sort(key=lambda r: r['data_ptr'])

        print(f"\n分析前10条记录的 data_ptr 指向的数据:")
        for r in records[:10]:
            data_ptr = r['data_ptr']
            print(f"\n  seq={r['seq']:4d} TS={r['datetime']} "
                  f"data_ptr=0x{data_ptr:x} const=0x{r['const']:x}")

            # 尝试在同一个文件中读取 data_ptr 处的数据
            fsize = os.path.getsize(fpath)
            if data_ptr < fsize - 1000:
                raw = read_at(fpath, data_ptr, 4096)
                nz = sum(1 for b in raw[:256] if b != 0)
                print(f"    当前文件 @ 0x{data_ptr:x}: nz={nz}/256")

                if nz > 30:
                    print(f"    Hex前128:")
                    for j in range(0, min(128, len(raw)), 32):
                        hex_str = ' '.join(f'{b:02x}' for b in raw[j:j+32])
                        print(f"      {j:4d}: {hex_str}")

                    # 尝试各种解码
                    decodings = try_decode_power_data(raw[:2048])
                    for fmt, vals, peak, head_zero in decodings:
                        if head_zero >= 2 and 0.5 < peak < 100:
                            print(f"    ✅ {fmt}: peak={peak:.3f} head_zero={head_zero}")
                            print(f"       前30: {[round(v,3) for v in vals[:30]]}")
                            mid = len(vals)//2
                            print(f"       中间[{mid}]: {[round(v,3) for v in vals[mid:mid+20]]}")
                            print(f"       后20: {[round(v,3) for v in vals[-20:]]}")
                else:
                    # 尝试在其他文件中找
                    for other_path, other_label in [(POWER_2, "功率2"), (POWER_1, "功率1")]:
                        if other_path == fpath:
                            continue
                        if data_ptr >= os.path.getsize(other_path) - 1000:
                            continue
                        raw2 = read_at(other_path, data_ptr, 1024)
                        nz2 = sum(1 for b in raw2[:256] if b != 0)
                        if nz2 > 30:
                            print(f"    → {other_label} @ 0x{data_ptr:x}: nz={nz2}/256")
                            for j in range(0, min(128, len(raw2)), 32):
                                hex_str = ' '.join(f'{b:02x}' for b in raw2[j:j+32])
                                print(f"      {j:4d}: {hex_str}")
                            decodings = try_decode_power_data(raw2)
                            for fmt, vals, peak, head_zero in decodings:
                                if head_zero >= 2 and 0.5 < peak < 100:
                                    print(f"      ✅ {fmt}: peak={peak:.3f}")
                                    print(f"         前30: {[round(v,3) for v in vals[:30]]}")
                            break
            else:
                print(f"    data_ptr 0x{data_ptr:x} 超出文件范围 ({fsize})")

    print(f"\n{'='*70}")
    print("✅ 扫描完成")

if __name__ == '__main__':
    main()
