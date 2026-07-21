#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 分析第3轮 — 跟进索引记录的偏移字段，定位实际采样数据
借鉴 CSM2010 逆向方法论：
  1. 找重复结构模式
  2. 用已知约束验证 (timestamp范围, sample_rate, sample_count合理范围)
  3. 采样数据合理性 (功率 0-5kW, 电流 0-10A)
"""
import struct
import sys
import os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

def read_chunk(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def extract_index_records(filepath, marker):
    """提取所有32字节索引记录"""
    data = read_chunk(filepath, 0, 0x200000)

    records = []
    pos = 0
    while True:
        p = data.find(marker, pos)
        if p == -1:
            break

        rec_start = p - 12
        if rec_start < 0:
            pos = p + 1
            continue

        rec = data[rec_start:rec_start+32]
        if len(rec) < 32:
            pos = p + 1
            continue

        seq    = struct.unpack('<I', rec[0:4])[0]
        f1     = struct.unpack('<I', rec[4:8])[0]   # 可能是 channel/switch 标识
        f2     = struct.unpack('<I', rec[8:12])[0]   # 可能是 数据偏移 或 累计采样数
        marker_v = struct.unpack('<I', rec[12:16])[0]
        ts1    = struct.unpack('<I', rec[16:20])[0]
        ts2    = struct.unpack('<I', rec[20:24])[0]
        f3     = struct.unpack('<I', rec[24:28])[0]  # 可能是 数据大小(bytes) 或 采样点数
        zero   = struct.unpack('<I', rec[28:32])[0]

        records.append({
            'offset': rec_start,
            'seq': seq, 'f1': f1, 'f2': f2,
            'marker': marker_v, 'ts1': ts1, 'ts2': ts2,
            'f3': f3, 'zero': zero
        })
        pos = p + 1

    return records

def probe_data_offsets(filepath, records, label):
    """根据索引记录的 f2/f3 字段跳转到数据区，尝试解析"""
    print(f"\n{'='*60}")
    print(f"数据偏移探测: {os.path.basename(filepath)} ({label})")
    print(f"索引记录总数: {len(records)}")

    file_size = os.path.getsize(filepath)

    # 假设1: f2 = 文件偏移量, f3 = 数据字节数
    for hyp in ['offset_bytes', 'offset_samples', 'offset_samples_2byte']:
        valid = 0
        for r in records[:50]:
            if hyp == 'offset_bytes':
                data_off = r['f2']
                data_size = r['f3']  # bytes
            elif hyp == 'offset_samples':
                data_off = r['f2']
                data_size = r['f3'] * 4  # samples * 4 bytes per float32
            else:  # offset_samples_2byte
                data_off = r['f2']
                data_size = r['f3'] * 2  # samples * 2 bytes per int16

            if 0 < data_off < file_size and 0 < data_size < 50000:
                valid += 1

        print(f"  假设'{hyp}': {valid}/{min(50, len(records))} 个有效偏移")

    # 用最佳假设读取实际数据
    # 先确定最优解释
    best_hyp = None
    best_valid = 0
    for hyp in ['offset_bytes', 'offset_samples', 'offset_samples_2byte']:
        valid = sum(1 for r in records[:200]
                    if 0 < r['f2'] < file_size and 0 < r['f3'] < 50000)
        if valid > best_valid:
            best_valid = valid
            best_hyp = hyp

    print(f"\n使用最佳假设: '{best_hyp}' (有效记录: {best_valid})")

    # 拿几条记录的数据来分析
    if best_hyp is None:
        print("无有效假设!")
        return

    analyzed = 0
    for r in records[:20]:
        data_off = r['f2']
        if best_hyp == 'offset_bytes':
            byte_size = r['f3']
        elif best_hyp == 'offset_samples':
            byte_size = r['f3'] * 4
        else:
            byte_size = r['f3'] * 2

        if not (0 < data_off < file_size and 16 < byte_size < 50000):
            continue

        raw = read_chunk(filepath, data_off, min(byte_size, 4096))

        # 取前50个 float32
        float_count = min(len(raw) // 4, 50)
        floats = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(float_count)]
        float_nonzero = [f for f in floats if abs(f) > 0.001]

        # 取前50个 int16
        int_count = min(len(raw) // 2, 50)
        ints = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(int_count)]

        ts_str = datetime.fromtimestamp(r['ts1']).strftime('%Y-%m-%d %H:%M:%S') if 1_700_000_000 < r['ts1'] < 1_800_000_000 else "invalid"

        print(f"\n  记录 seq={r['seq']}  ts={ts_str}  data_off=0x{data_off:x}")
        print(f"    f1={r['f1']} f2={r['f2']} f3={r['f3']} byte_size={byte_size}")
        print(f"    float32(前20): {[round(f,3) for f in floats[:20]]}")
        print(f"    int16(前20):   {ints[:20]}")

        # 检查是否有道岔功率曲线的特征：开头一段零→突然跳到3-5→降到0.2-0.4→结尾零
        if float_nonzero:
            has_spike = any(f > 1.0 for f in floats[:30])
            has_steady = any(0.05 < f < 0.6 for f in floats[30:])
            starts_zero = sum(1 for f in floats[:5] if abs(f) < 0.01)
            if has_spike and starts_zero >= 2:
                print(f"    ✅ 匹配功率曲线特征! spike={has_spike} steady_zone={has_steady}")

        analyzed += 1
        if analyzed >= 5:
            break

def try_alternate_record_layout(filepath):
    """尝试不同的记录布局假设"""
    print(f"\n{'='*60}")
    print(f"替代记录布局测试: {os.path.basename(filepath)}")

    # 读取整个文件的前8MB看看有没有其他结构
    data = read_chunk(filepath, 0, 0x800000)

    # 查找所有看起来像记录头的模式
    # 已知模式: 4B递增序列号 + 4B常量 + ...
    # 找所有连续的递增序列 (步长为1)
    print("\n搜索递增序列号模式...")
    for step in [4, 8, 12, 16, 20, 24, 28, 32]:
        seqs = []
        for i in range(0, min(0x200000, len(data) - step * 2), step):
            v = struct.unpack('<I', data[i:i+4])[0]
            if 0 < v < 100000:
                seqs.append((i, v))

        # 检测连续递增段
        inc_runs = []
        run_start = None
        for i in range(1, len(seqs)):
            if seqs[i][1] == seqs[i-1][1] + 1 and seqs[i][0] - seqs[i-1][0] == step:
                if run_start is None:
                    run_start = seqs[i-1]
                # continue run
            else:
                if run_start is not None:
                    run_len = (seqs[i-1][0] - run_start[0]) // step + 1
                    inc_runs.append((run_start[0], run_len, step))
                    run_start = None

        if inc_runs:
            longest = max(inc_runs, key=lambda x: x[1])
            print(f"  step={step:3d}: {len(inc_runs)} 段递增序列, 最长={longest[1]}条 @ 0x{longest[0]:06x}")

    # 关键测试：把 f2 当作数据偏移，实际跳转读取
    print(f"\n关键测试: 假设 32B 记录的 f2=数据偏移, f3=采样点数, 编码=int16*2B")
    print(f"按照这个假设跳到数据位置，看数据是否符合道岔曲线特征...")

    # 用功率文件的标记
    marker_power = b'\x27\x12\x00\x00'
    if marker_power in data:
        records = extract_index_records(filepath, marker_power)
    else:
        marker_current = b'\x77\x32\x00\x00'
        records = extract_index_records(filepath, marker_current)

    if len(records) < 2:
        print("记录不足")
        return

    file_size = os.path.getsize(filepath)

    # 假设: f2=文件偏移, f3=采样点数, float32 (每个采样4字节)
    good_count = 0
    for r in records[:30]:
        data_off = r['f2']
        sample_count = r['f3']

        # 合理性检查
        if not (0x200000 < data_off < file_size - 100000):
            continue
        if not (50 < sample_count < 2000):
            continue

        byte_size = sample_count * 4  # float32
        if data_off + byte_size > file_size:
            continue

        raw = read_chunk(filepath, data_off, min(byte_size, 8000))
        floats = [struct.unpack('<f', raw[i*4:(i+1)*4])[0]
                   for i in range(min(sample_count, 200))]

        # 道岔功率曲线特征验证
        starts_zero = sum(1 for f in floats[:10] if abs(f) < 0.02)
        has_startup_peak = any(f > 1.5 for f in floats[:40])
        steady_values = [f for f in floats[40:150] if 0.01 < f < 2.0]

        if starts_zero >= 5 and has_startup_peak and len(steady_values) >= 10:
            good_count += 1
            if good_count <= 3:
                ts_str = datetime.fromtimestamp(r['ts1']).strftime('%Y-%m-%d %H:%M:%S')
                print(f"\n  ✅ seq={r['seq']} ts={ts_str} data_off=0x{data_off:x} samples={sample_count}")
                print(f"     float32前30: {[round(f,3) for f in floats[:30]]}")

    print(f"\n  匹配数: {good_count}/{min(30, len(records))}")
    if good_count == 0:
        print("  ❌ float32假设不成立，测试 int16...")

        # 改用 int16 测试
        good_count2 = 0
        for r in records[:30]:
            data_off = r['f2']
            sample_count = r['f3']
            if not (0x200000 < data_off < file_size - 100000):
                continue
            if not (50 < sample_count < 2000):
                continue

            byte_size = sample_count * 2
            if data_off + byte_size > file_size:
                continue

            raw = read_chunk(filepath, data_off, min(byte_size, 8000))
            ints = [struct.unpack('<h', raw[i*2:(i+1)*2])[0]
                     for i in range(min(sample_count, 200))]

            starts_zero = sum(1 for v in ints[:10] if abs(v) < 10)
            has_peak = any(abs(v) > 200 for v in ints[:40])
            steady = [v for v in ints[40:150] if abs(v) > 5]

            if starts_zero >= 5 and has_peak and len(steady) >= 10:
                good_count2 += 1
                if good_count2 <= 3:
                    ts_str = datetime.fromtimestamp(r['ts1']).strftime('%Y-%m-%d %H:%M:%S')
                    print(f"\n  ✅ int16: seq={r['seq']} ts={ts_str} data_off=0x{data_off:x} samples={sample_count}")
                    print(f"     int16前30: {ints[:30]}")

        print(f"\n  int16匹配数: {good_count2}/{min(30, len(records))}")

        if good_count2 == 0:
            # 尝试 f2 和 f3 互换含义
            print("\n  ❌ int16也不成立，尝试 f2=采样点数, f3=文件偏移...")
            good_count3 = 0
            for r in records[:30]:
                data_off = r['f3']  # 互换!
                sample_count = r['f2']  # 互换!

                if not (0x200000 < data_off < file_size - 100000):
                    continue
                if not (50 < sample_count < 2000):
                    continue

                for bpe in [4, 2]:  # bytes per element
                    byte_size = sample_count * bpe
                    if data_off + byte_size > file_size:
                        continue
                    raw = read_chunk(filepath, data_off, min(byte_size, 8000))

                    if bpe == 4:
                        vals = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(min(sample_count, 200))]
                    else:
                        vals = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(min(sample_count, 200))]

                    starts_zero = sum(1 for v in vals[:10] if abs(v) < 10)
                    has_peak = any(abs(v) > 200 for v in vals[:40])
                    steady = [v for v in vals[40:150] if abs(v) > 5]

                    if starts_zero >= 5 and has_peak and len(steady) >= 10:
                        good_count3 += 1
                        enc = "float32" if bpe == 4 else "int16"
                        ts_str = datetime.fromtimestamp(r['ts1']).strftime('%Y-%m-%d %H:%M:%S')
                        print(f"\n  ✅ 互换+{enc}: seq={r['seq']} ts={ts_str} data_off=0x{data_off:x} samples={sample_count}")
                        print(f"     前30: {[round(v,3) if bpe==4 else v for v in vals[:30]]}")
                        break  # 找到一个就不继续换编码

            print(f"\n  互换假设匹配数: {good_count3}/{min(30, len(records))}")

if __name__ == '__main__':
    pf_path = os.path.join(HBF_POWER_DIR, '1.hbf')
    cf_path = os.path.join(HBF_CURRENT_DIR, '1.hbf')

    # 提取索引
    data_pf = read_chunk(pf_path, 0, 0x200000)
    marker_p = b'\x27\x12\x00\x00'
    precords = extract_index_records(pf_path, marker_p)
    print(f"功率文件索引记录: {len(precords)} 条")
    if precords:
        print(f"  序列号范围: {precords[0]['seq']} ~ {precords[-1]['seq']}")
        print(f"  f1唯一值: {sorted(set(r['f1'] for r in precords[:100]))}")
        print(f"  f2范围: {min(r['f2'] for r in precords[:100])} ~ {max(r['f2'] for r in precords[:100])}")
        print(f"  f3范围: {min(r['f3'] for r in precords[:100])} ~ {max(r['f3'] for r in precords[:100])}")

    marker_c = b'\x77\x32\x00\x00'
    crecords = extract_index_records(cf_path, marker_c)
    print(f"\n电流文件索引记录: {len(crecords)} 条")
    if crecords:
        print(f"  f1唯一值: {sorted(set(r['f1'] for r in crecords[:100]))}")
        print(f"  f2范围: {min(r['f2'] for r in crecords[:100])} ~ {max(r['f2'] for r in crecords[:100])}")
        print(f"  f3范围: {min(r['f3'] for r in crecords[:100])} ~ {max(r['f3'] for r in crecords[:100])}")

    # 根据偏移跳读数据
    if precords:
        probe_data_offsets(pf_path, precords, "功率")
    if crecords:
        probe_data_offsets(cf_path, crecords, "电流")

    # 替代布局测试
    try_alternate_record_layout(pf_path)
    try_alternate_record_layout(cf_path)

    print("\n✅ 第3轮分析完成")
