#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
直接读取HBF数据块，跳过子索引，提取实际采样数据。
关键洞察: F4指向子索引记录，实际采样数据在子索引之后。
采样数据大小 = F6, 采样点数 = F7, 每采样字节数 = F6/F7
"""
import struct, sys, os, json, math
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_real_entries(filepath):
    """解析真正的目录项"""
    data = read_at(filepath, 0, 0x28000)
    entries = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = 0
        while True:
            p = data.find(pattern, pos)
            if p == -1 or p >= 0x28000:
                break
            entry = data[p:p+256]
            if len(entry) < 256:
                break
            desc = data[p+0x70:p+0x70+52]
            fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
            f4, f7, f6 = fields[4], fields[7], fields[6]
            if not (0 < f7 < 2000 and 0 < f6 < 100000):
                pos = p + 1
                continue
            entries.append({
                'offset': p, 'switch_id': sw_id,
                'F3': fields[3], 'F4': f4, 'F5': fields[5],
                'F6': f6, 'F7': f7, 'F8': fields[8],
            })
            pos = p + 1
    entries.sort(key=lambda e: e['offset'])
    return entries

def count_subindex(raw, marker=b'\x27\x12\x00\x00'):
    """统计子索引记录数量"""
    count = 0
    for i in range(0, len(raw) - 4, 4):
        if raw[i:i+4] == marker:
            count += 1
        elif count > 0:
            break
    # 子索引记录有时不全是4字节对齐的, step by 1
    if count == 0:
        for i in range(0, len(raw) - 4):
            if raw[i:i+4] == marker:
                count += 1
            elif count > 0 and i > 64:
                break
    return count

def extract_sample_data(filepath, f4, f6, f7, max_read=1048576):
    """提取F4处的采样数据"""
    file_size = os.path.getsize(filepath)
    read_size = min(f6 * 3 + 131072, file_size - f4, max_read)
    raw = read_at(filepath, f4, read_size)

    if len(raw) < 64:
        return None, None

    # 找子索引结束位置
    first4 = raw[0:4]
    sub_count = count_subindex(raw)
    if sub_count == 0:
        # 尝试不同的marker
        for marker in [first4, b'\x27\x12\x00\x00', b'\x77\x32\x00\x00']:
            sub_count = count_subindex(raw, marker)
            if sub_count > 0:
                break

    # 子索引大小
    if sub_count > 0:
        # 每条子索引32B
        sub_size = sub_count * 32
    else:
        # 没找到子索引, 数据可能直接在F4处
        sub_size = 0

    sample_start = sub_size
    sample_data = raw[sample_start:sample_start + f6]

    return sample_data, sub_count

def decode_and_score(sample_data, bps, f7):
    """解码采样数据并评分"""
    n = min(f7, len(sample_data) // bps) if bps > 0 else 0
    if n < 10:
        return None, 0

    results = {}

    if bps == 2:
        results['int16'] = [struct.unpack('<h', sample_data[i*2:(i+1)*2])[0] for i in range(n)]
        results['uint16'] = [struct.unpack('<H', sample_data[i*2:(i+1)*2])[0] for i in range(n)]
    elif bps == 4:
        results['float32'] = [struct.unpack('<f', sample_data[i*4:(i+1)*4])[0] for i in range(n)]
        results['int32'] = [struct.unpack('<i', sample_data[i*4:(i+1)*4])[0] for i in range(n)]
    elif bps >= 8:
        # 取第一个float32通道
        results[f'float32_ch0'] = [struct.unpack('<f', sample_data[i*bps:(i+1)*bps][0:4])[0] for i in range(n)]
        results[f'int16_ch0'] = [struct.unpack('<h', sample_data[i*bps:(i+1)*bps][0:2])[0] for i in range(n)]

    # 评分
    best = None
    best_score = -1
    for name, vals in results.items():
        z10 = sum(1 for v in vals[:10] if abs(v) < 0.05)
        peaks = [v for v in vals[:40] if v > 1.0]
        mid_vals = vals[20:n-15] if n > 35 else []
        steady = [v for v in mid_vals if 0.1 < abs(v) < 1.0] if mid_vals else []
        trail = sum(1 for v in vals[-10:] if abs(v) < 0.05)
        score = (z10>=4)*1 + (len(peaks)>=1)*2 + (len(steady)>10)*2 + (trail>=3)*1
        if score > best_score:
            best_score = score
            best = (name, vals, score, z10, len(peaks), len(steady), trail)

    return best, results

def process_file(filepath, label):
    """处理一个HBF文件，提取所有开关的曲线数据"""
    print(f"\n{'='*70}")
    print(f"处理: {os.path.basename(filepath)} ({label})")

    entries = parse_real_entries(filepath)
    file_size = os.path.getsize(filepath)

    # 按F4分组
    by_f4 = defaultdict(list)
    for e in entries:
        by_f4[e['F4']].append(e)

    all_events = []
    analyzed_offsets = set()

    for f4, group in sorted(by_f4.items()):
        if f4 in analyzed_offsets:
            continue
        analyzed_offsets.add(f4)

        switches = [e['switch_id'] for e in group]
        f7 = group[0]['F7']
        f6 = min(e['F6'] for e in group)

        # 跳过太小的块
        if f6 < 100 or f7 < 10:
            continue

        # 提取采样数据
        sample_data, sub_count = extract_sample_data(filepath, f4, f6, f7)

        if sample_data is None or len(sample_data) < f6:
            print(f"  [{','.join(switches)}] @ 0x{f4:x}: 数据不足 (got {len(sample_data) if sample_data else 0}, need {f6})")
            continue

        # 非零检查
        nz = sum(1 for b in sample_data[:min(1024, len(sample_data))] if b != 0)
        if nz < 10:
            print(f"  [{','.join(switches)}] @ 0x{f4:x}: 全零 (或接近全零)")
            continue

        bps = f6 // f7 if f7 > 0 else 0
        print(f"  [{','.join(switches)}] @ 0x{f4:x}: F7={f7} F6={f6} bps={bps} sub_idx={sub_count} nz={nz}")

        # 解码
        best, all_results = decode_and_score(sample_data, bps, f7)

        if best and best[2] >= 4:
            name, vals, score, z10, n_peaks, n_steady, trail = best
            print(f"    ✅ {name}: score={score} z10={z10} peaks={n_peaks} steady={n_steady} trail={trail}")
            print(f"    前30: {[round(v,3) for v in vals[:30]]}")
            print(f"    后15: {[round(v,3) for v in vals[-15:]]}")
            # 找峰值
            peak_idx = max(range(min(50, len(vals))), key=lambda i: vals[i])
            print(f"    峰值: [{peak_idx}]={vals[peak_idx]:.3f}")
        elif best:
            name, vals, score, z10, n_peaks, n_steady, trail = best
            print(f"    ~ {name}: score={score} z10={z10} peaks={n_peaks} steady={n_steady} trail={trail}")
            print(f"    前30: {[round(v,3) for v in vals[:30]]}")
        else:
            # 打印原始hex
            print(f"    ❌ 无法解码, 原始hex(前128B):")
            for i in range(0, min(128, len(sample_data)), 16):
                line = sample_data[i:i+16]
                print(f"      +{i:4d}: {' '.join(f'{b:02x}' for b in line)}")

    return all_events

def main():
    print("HBF 曲线数据提取")
    print("="*70)

    # 功率文件
    pf_1 = os.path.join(HBF_POWER_DIR, '1.hbf')
    pf_2 = os.path.join(HBF_POWER_DIR, '2.hbf')

    # 电流文件
    cf_1 = os.path.join(HBF_CURRENT_DIR, '1.hbf')
    cf_2 = os.path.join(HBF_CURRENT_DIR, '2.hbf')
    cf_3 = os.path.join(HBF_CURRENT_DIR, '3.hbf')

    for fpath, label in [
        (pf_1, "功率1.hbf"),
        (pf_2, "功率2.hbf"),
        (cf_1, "电流1.hbf"),
        (cf_2, "电流2.hbf"),
        (cf_3, "电流3.hbf"),
    ]:
        if os.path.exists(fpath):
            process_file(fpath, label)

    print("\n✅ 提取完成")

if __name__ == '__main__':
    main()
