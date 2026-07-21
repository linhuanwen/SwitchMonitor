#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
基于发现的32B索引记录结构，定位并提取实际采样数据
索引记录: [4B timestamp] [4B ?] [4B ?] [4B 0] [4B seq] [4B ?] [4B data_ptr] [4B 0x1227]
data_ptr 指向实际采样数据 (可能在当前文件或另一个HBF文件中)
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"
CURRENT_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\1.hbf"
CURRENT_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"
CURRENT_3 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\3.hbf"

MARKER = b'\x27\x12\x00\x00'

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_index_records(filepath, start_offset=0, max_scan=200_000_000):
    """扫描文件找所有32B索引记录(以0x1227结尾的)"""
    file_size = os.path.getsize(filepath)
    scan_end = min(file_size, start_offset + max_scan)

    records = []
    with open(filepath, 'rb') as f:
        f.seek(start_offset)
        raw = f.read(scan_end - start_offset)

    for i in range(0, len(raw) - 32, 32):
        if raw[i+28:i+32] == MARKER:
            rec = raw[i:i+32]
            u32s = [struct.unpack('<I', rec[j:j+4])[0] for j in range(0, 32, 4)]
            ts = u32s[0]
            if 1_500_000_000 < ts < 1_900_000_000:
                records.append({
                    'file_offset': start_offset + i,
                    'timestamp': ts,
                    'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
                    'u32': u32s,
                    'seq': u32s[4],
                    'data_ptr': u32s[6],
                    'field5': u32s[5],
                })

    return records

def try_extract_data(target_file, offset, size=4647):
    """从目标文件读取并尝试解码采样数据"""
    raw = read_at(target_file, offset, min(size * 3, 65536))
    if len(raw) < 100:
        return None

    nz = sum(1 for b in raw[:min(1024, len(raw))] if b != 0)
    if nz < 20:
        return None

    results = {}

    # 尝试 float32
    n = min(800, len(raw) // 4)
    f32 = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(n)]
    results['float32'] = f32

    # 尝试 int16
    n = min(800, len(raw) // 2)
    i16 = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(n)]
    results['int16'] = i16

    # 尝试 uint16
    u16 = [struct.unpack('<H', raw[i*2:(i+1)*2])[0] for i in range(n)]
    results['uint16'] = u16

    return results

def score_curve(vals, name):
    """评分是否像道岔功率曲线"""
    z10 = sum(1 for v in vals[:10] if abs(v) < 0.05)
    peaks = [v for v in vals[:50] if v > 1.0]
    mid = vals[30:len(vals)-20] if len(vals) > 50 else []
    steady = [v for v in mid if 0.1 < abs(v) < 1.0] if mid else []
    t0 = sum(1 for v in vals[-10:] if abs(v) < 0.05)
    score = (z10>=4)*1 + (len(peaks)>=1)*2 + (len(steady)>10)*2 + (t0>=3)*1
    return score, z10, len(peaks), len(steady), t0

def main():
    print("HBF 索引记录 + 数据定位")
    print("="*60)

    # Step 1: 扫描功率1.hbf中的索引记录
    print("\n扫描功率1.hbf...")
    idx = find_index_records(POWER_1, start_offset=0x8000)
    print(f"找到 {len(idx)} 条索引记录 (0x1227结尾, 带时间戳)")

    if idx:
        print(f"\n前10条:")
        for r in idx[:10]:
            print(f"  @0x{r['file_offset']:09x}: ts={r['datetime']} seq={r['seq']} "
                  f"data_ptr=0x{r['data_ptr']:06x} ({r['data_ptr']})")

    # Step 2: 对每个unique data_ptr，检查数据在哪个文件中
    if idx:
        print(f"\n检查数据指针目标...")
        checked = set()
        found_data = 0
        for r in idx:
            ptr = r['data_ptr']
            if ptr in checked or ptr < 100 or ptr > 500_000_000:
                continue
            checked.add(ptr)

            found_in = None
            for target_name, target_path in [("power1", POWER_1), ("power2", POWER_2)]:
                if not os.path.exists(target_path):
                    continue
                fsize = os.path.getsize(target_path)
                if ptr >= fsize - 5000:
                    continue
                raw = read_at(target_path, ptr, 128)
                nz = sum(1 for b in raw if b != 0)
                if nz > 50:
                    found_in = target_name
                    break

            if found_in:
                found_data += 1
                if found_data <= 10:
                    print(f"\n  data_ptr=0x{ptr:06x} ({ptr}) → 在{found_in}中有数据 (nz={nz}/128)")
                    # Dump first bytes
                    raw = read_at(POWER_1 if found_in == "power1" else POWER_2, ptr, 128)
                    for j in range(0, 128, 32):
                        line = raw[j:j+32]
                        print(f"    +{j:3d}: {' '.join(f'{b:02x}' for b in line)}")

                    # Try to decode
                    results = try_extract_data(POWER_1 if found_in == "power1" else POWER_2, ptr)
                    if results:
                        for name, vals in results.items():
                            score, z10, np, ns, t0 = score_curve(vals, name)
                            if score >= 4:
                                print(f"    ✅ {name}: score={score} z10={z10} peaks={np} steady={ns} t0={t0}")
                                print(f"       前50: {[round(v,3) for v in vals[:50]]}")
                            elif score >= 2:
                                print(f"    ~ {name}: score={score} z10={z10} peaks={np} steady={ns} t0={t0}")
                                print(f"       前30: {[round(v,3) for v in vals[:30]]}")

        print(f"\n总共检查了 {len(checked)} 个唯一data_ptr, {found_data} 个有数据")

    # Step 3: 也检查电流文件
    for cf_path, cf_label in [(CURRENT_1, "电流1"), (CURRENT_2, "电流2"), (CURRENT_3, "电流3")]:
        if not os.path.exists(cf_path):
            continue
        print(f"\n扫描{cf_label}.hbf...")
        idx = find_index_records(cf_path, start_offset=0x8000)
        print(f"找到 {len(idx)} 条索引记录")
        if idx:
            print(f"  时间范围: {idx[0]['datetime']} - {idx[-1]['datetime']}")
            # 检查前几个data_ptr
            for r in idx[:3]:
                ptr = r['data_ptr']
                raw = read_at(cf_path, ptr, 256)
                nz = sum(1 for b in raw[:256] if b != 0)
                print(f"  data_ptr=0x{ptr:06x} nz={nz}/256")
                if nz > 50:
                    for j in range(0, min(128, len(raw)), 32):
                        line = raw[j:j+32]
                        print(f"    {' '.join(f'{b:02x}' for b in line)}")

    print("\n✅ 完成")

if __name__ == '__main__':
    main()
