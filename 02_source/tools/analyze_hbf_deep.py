#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 深度分析 — 重点解析子索引记录 + 定位实际采样数据
目标: 解码32B子索引记录结构,找到采样数据的确切位置和编码
"""
import struct, sys, os
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

MARKER_POWER = b'\x27\x12\x00\x00'
MARKER_CURRENT = b'\x77\x32\x00\x00'

def read_chunk(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_all_entries(filepath):
    """解析所有256B目录项"""
    data = read_chunk(filepath, 0, 0x28000)
    entries = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = 0
        while True:
            p = data.find(pattern, pos)
            if p == -1:
                break
            entry = data[p:p+256]
            if len(entry) < 256:
                break
            desc = entry[0x70:0x70+52]
            fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
            entries.append({
                'offset': p, 'switch_id': sw_id,
                'F0': fields[0], 'F1': fields[1], 'F2': fields[2],
                'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
                'F6': fields[6], 'F7': fields[7], 'F8': fields[8],
                'F9': fields[9], 'F10': fields[10], 'F11': fields[11],
                'F12': fields[12],
            })
            pos = p + 1
    entries.sort(key=lambda e: e['offset'])
    return entries

def analyze_subindex_records(filepath, f4_offset, file_size, marker_type, label):
    """深度解析F4偏移处的子索引记录"""
    print(f"\n{'~'*70}")
    print(f"子索引深度分析: {os.path.basename(filepath)} @ 0x{f4_offset:09x} ({label})")

    # 读大块数据
    raw = read_chunk(filepath, f4_offset, min(131072, file_size - f4_offset))

    if len(raw) < 64:
        print(f"  数据不足 ({len(raw)} bytes)")
        return None

    # 找到子索引记录的结束位置
    # 子索引记录通常以固定marker开头 - 找marker不再重复的位置
    first_marker = struct.unpack('<I', raw[0:4])[0]
    print(f"  首条记录 marker=0x{first_marker:04x} ({first_marker})")

    # 收集所有子索引记录
    sub_records = []
    sub_end = 0
    for i in range(0, len(raw) - 32, 32):
        record = raw[i:i+32]
        marker = struct.unpack('<I', record[0:4])[0]

        # 检查是否仍是子索引记录
        # 判断标准: marker匹配 且 看起来结构化
        if marker == first_marker and i < len(raw) - 64:
            # 尝试解析
            fields_u32 = [struct.unpack('<I', record[j:j+4])[0] for j in range(0, 32, 4)]
            fields_u16 = [struct.unpack('<H', record[j:j+2])[0] for j in range(0, 32, 2)]

            ts_val = fields_u32[4]  # offset 16 通常有时间戳
            ts_str = ""
            if 1_780_000_000 < ts_val < 1_790_000_000:
                ts_str = datetime.fromtimestamp(ts_val).strftime('%Y-%m-%d %H:%M:%S')
            elif 1_700_000_000 < ts_val < 1_800_000_000:
                ts_str = str(datetime.fromtimestamp(ts_val))

            sub_records.append({
                'index': i // 32,
                'offset': f4_offset + i,
                'marker': marker,
                'u32': fields_u32,
                'u16': fields_u16,
                'ts_val': ts_val,
                'ts_str': ts_str,
            })
        else:
            sub_end = i
            break
    else:
        sub_end = len(raw)

    if not sub_records:
        print(f"  未找到子索引记录!")
        # 打印前面的原始数据
        print(f"  原始数据 (前256B):")
        for i in range(0, min(256, len(raw)), 32):
            line = raw[i:i+32]
            print(f"    +{i:3d}: {' '.join(f'{b:02x}' for b in line)}")
        return None

    print(f"  找到 {len(sub_records)} 条子索引记录, 结束于 offset +{sub_end}")

    # 分析子索引记录的结构
    print(f"\n  子索引记录结构分析 (8×uint32):")
    print(f"  {'#':>4s} {'marker':>10s} {'u32[1]':>10s} {'u32[2]':>10s} {'u32[3]':>10s} {'u32[4]/TS':>14s} {'u32[5]':>10s} {'u32[6]':>10s} {'u32[7]':>10s}")

    for sr in sub_records[:10]:
        u = sr['u32']
        ts = sr['ts_str'] if sr['ts_str'] else f"0x{u[4]:08x}"
        print(f"  {sr['index']:4d} 0x{sr['marker']:08x} {u[1]:10d} {u[2]:10d} {u[3]:10d} {ts:>14s} {u[5]:10d} {u[6]:10d} {u[7]:10d}")

    if len(sub_records) > 10:
        print(f"  ... ({len(sub_records) - 10} more)")

    # 分析子索引记录之间的模式
    # u32[6] 可能指向数据偏移? u32[7] 可能是采样点数?
    print(f"\n  字段模式分析:")
    for col, col_name in [(1, 'u32[1]'), (2, 'u32[2]'), (3, 'u32[3]'),
                          (5, 'u32[5]'), (6, 'u32[6]'), (7, 'u32[7]')]:
        vals = [sr['u32'][col] for sr in sub_records]
        unique = set(vals)
        print(f"    {col_name}: min={min(vals)} max={max(vals)} unique_values={len(unique)} "
              f"sample={sorted(vals)[:8]}{'...' if len(vals)>8 else ''}")

        # 检查是否是递增值
        if len(sub_records) > 1:
            diffs = [vals[i+1] - vals[i] for i in range(len(vals)-1)]
            unique_diffs = set(diffs)
            if len(unique_diffs) <= 3:
                print(f"      → 差值模式: {sorted(unique_diffs)[:5]} (可能是递增值)")

    # u32[7] 特别重要 — 可能是采样点数或数据大小
    col7_vals = [sr['u32'][7] for sr in sub_records]
    if len(set(col7_vals)) <= 3:
        print(f"\n    u32[7] 几乎恒定 = {set(col7_vals)} — 可能是固定采样点数或固定数据大小")

    # 关键: 子索引后面的数据
    sample_area_offset = f4_offset + sub_end
    sample_data = raw[sub_end:sub_end + min(65536, len(raw) - sub_end)]

    print(f"\n  采样数据区 @ 0x{sample_area_offset:09x} (子索引后 {sub_end} bytes)")
    print(f"  可用数据: {len(sample_data)} bytes")

    # 尝试确定每次采样的字节数
    # 如果 u32[7] 是采样点数, 则 bytes_per_sample = total_data / sample_count
    total_data_size = len(sample_data)
    if col7_vals and col7_vals[0] > 0:
        sample_count = col7_vals[0]
        # 尝试找 bytes_per_sample
        for bps in [2, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 64]:
            expected = sample_count * bps
            if abs(expected - total_data_size) < bps * 2:
                print(f"    ✅ bytes_per_sample={bps}: {sample_count} × {bps} = {expected} ≈ {total_data_size}")
                break
        else:
            print(f"    ⚠️ 无法匹配: {sample_count} 采样, {total_data_size} 字节")

    return {
        'sub_records': sub_records,
        'sub_end': sub_end,
        'sample_area_offset': sample_area_offset,
        'sample_data': sample_data,
        'total_data_size': total_data_size,
    }

def try_decode_samples(sample_data, bytes_per_sample, label):
    """尝试多种编码方式解码采样数据"""
    print(f"\n  编码尝试 (bps={bytes_per_sample}):")

    n_samples = len(sample_data) // bytes_per_sample
    n = min(800, n_samples)

    # 编码方案
    results = {}

    if bytes_per_sample == 4:
        # float32
        vals = [struct.unpack('<f', sample_data[i*4:(i+1)*4])[0] for i in range(n)]
        results['float32'] = vals

        # int32
        vals = [struct.unpack('<i', sample_data[i*4:(i+1)*4])[0] for i in range(n)]
        results['int32'] = vals

        # uint32
        vals = [struct.unpack('<I', sample_data[i*4:(i+1)*4])[0] for i in range(n)]
        results['uint32'] = vals

    elif bytes_per_sample == 2:
        vals = [struct.unpack('<h', sample_data[i*2:(i+1)*2])[0] for i in range(n)]
        results['int16'] = vals
        vals = [struct.unpack('<H', sample_data[i*2:(i+1)*2])[0] for i in range(n)]
        results['uint16'] = vals

    elif bytes_per_sample == 8:
        # 2×float32 (双通道)
        f1 = [struct.unpack('<f', sample_data[i*8:(i+1)*8][0:4])[0] for i in range(n)]
        f2 = [struct.unpack('<f', sample_data[i*8:(i+1)*8][4:8])[0] for i in range(n)]
        results['2×float32_ch0'] = f1
        results['2×float32_ch1'] = f2

        # 2×int32
        i1 = [struct.unpack('<i', sample_data[i*8:(i+1)*8][0:4])[0] for i in range(n)]
        i2 = [struct.unpack('<i', sample_data[i*8:(i+1)*8][4:8])[0] for i in range(n)]
        results['2×int32_ch0'] = i1
        results['2×int32_ch1'] = i2

    elif bytes_per_sample == 12:
        # 3×float32
        for ch in range(3):
            vals = [struct.unpack('<f', sample_data[i*12+ch*4:i*12+ch*4+4])[0] for i in range(n)]
            results[f'3×float32_ch{ch}'] = vals

    elif bytes_per_sample == 16:
        # 4×float32
        for ch in range(4):
            vals = [struct.unpack('<f', sample_data[i*16+ch*4:i*16+ch*4+4])[0] for i in range(n)]
            results[f'4×float32_ch{ch}'] = vals

    elif bytes_per_sample == 32:
        # 8×float32
        for ch in range(8):
            vals = [struct.unpack('<f', sample_data[i*32+ch*4:i*32+ch*4+4])[0] for i in range(n)]
            results[f'8×float32_ch{ch}'] = vals
        # 16×int16
        for ch in range(4):
            vals = [struct.unpack('<h', sample_data[i*32+ch*2:i*32+ch*2+2])[0] for i in range(n)]
            results[f'16×int16_ch{ch}'] = vals

    # 对每个结果评估道岔曲线特征
    for name, vals in results.items():
        z10 = sum(1 for v in vals[:10] if abs(v) < 0.02)
        peaks = [v for v in vals[:60] if abs(v) > (1.0 if 'float' in name else 100)]
        nonzero_mid = sum(1 for v in vals[30:200] if abs(v) > (0.005 if 'float' in name else 2))
        trail_zero = sum(1 for v in vals[-10:] if abs(v) < (0.02 if 'float' in name else 5))

        score = 0
        if z10 >= 4: score += 1
        if len(peaks) >= 1: score += 2
        if nonzero_mid > 20: score += 2
        if trail_zero >= 3: score += 1

        if score >= 4:
            print(f"    ✅ {name}: score={score} z10={z10} peaks={len(peaks)} nonzero_mid={nonzero_mid} trail0={trail_zero}")
            print(f"       前60: {[round(v,3) if 'float' in name else v for v in vals[:60]]}")
            print(f"       中间: {[round(v,3) if 'float' in name else v for v in vals[100:160]]}")
            print(f"       尾20: {[round(v,3) if 'float' in name else v for v in vals[-20:]]}")
        elif score >= 2:
            print(f"    ~ {name}: score={score} z10={z10} peaks={len(peaks)} nonzero_mid={nonzero_mid} trail0={trail_zero}")
            print(f"       前30: {[round(v,3) if 'float' in name else v for v in vals[:30]]}")

    # 如果全部不匹配,打印原始hex
    if not any(score >= 4 for score in [0]):
        print(f"\n    无匹配编码, 原始hex (前256B):")
        for i in range(0, min(256, len(sample_data)), 16):
            line = sample_data[i:i+16]
            print(f"      +{i:4d}: {' '.join(f'{b:02x}' for b in line)}")

def analyze_single_hbf(filepath, label):
    """分析单个HBF文件"""
    print(f"\n{'='*70}")
    print(f"HBF 文件分析: {os.path.basename(filepath)} ({label})")
    print(f"文件大小: {os.path.getsize(filepath):,} bytes ({os.path.getsize(filepath)/1024/1024:.1f} MB)")

    file_size = os.path.getsize(filepath)
    entries = parse_all_entries(filepath)
    print(f"目录项: {len(entries)}")

    # 按 F4 分组
    by_f4 = defaultdict(list)
    for e in entries:
        by_f4[e['F4']].append(e)

    print(f"\n数据块分布 (按F4偏移):")
    for f4, group in sorted(by_f4.items()):
        if f4 == 0 or f4 >= file_size - 10000:
            continue
        switches = [e['switch_id'] for e in group]
        sample_counts = [e['F7'] for e in group]
        data_sizes = [e['F6'] for e in group]
        print(f"  0x{f4:09x}: {switches} F7={sample_counts} F6={data_sizes}")

    # 对每个唯一的F4偏移做子索引分析
    analyzed = set()
    for e in entries:
        f4 = e['F4']
        if f4 in analyzed or f4 == 0 or f4 >= file_size - 10000:
            continue
        analyzed.add(f4)

        result = analyze_subindex_records(filepath, f4, file_size, label,
                                          f"{e['switch_id']} (F7={e['F7']})")
        if result and result['sample_data']:
            # 尝试确定每采样字节数并解码
            bps = 32  # 默认32B/sample
            if result['sub_records']:
                col7 = [sr['u32'][7] for sr in result['sub_records']]
                if len(set(col7)) == 1 and col7[0] > 0:
                    expected_bps = result['total_data_size'] // col7[0]
                    if expected_bps in [2,4,8,12,16,20,24,28,32,36,40,48,64]:
                        bps = expected_bps
            try_decode_samples(result['sample_data'], bps, label)

def analyze_all_hbf_files():
    """分析所有HBF文件"""
    # 功率文件
    for fname in sorted(os.listdir(HBF_POWER_DIR)):
        if fname.endswith('.hbf'):
            analyze_single_hbf(os.path.join(HBF_POWER_DIR, fname), "功率")

    # 电流文件
    for fname in sorted(os.listdir(HBF_CURRENT_DIR)):
        if fname.endswith('.hbf'):
            analyze_single_hbf(os.path.join(HBF_CURRENT_DIR, fname), "电流")

if __name__ == '__main__':
    print("HBF 深度结构分析 - 子索引记录解码")
    print("="*70)
    analyze_all_hbf_files()
    print("\n✅ 深度分析完成")
