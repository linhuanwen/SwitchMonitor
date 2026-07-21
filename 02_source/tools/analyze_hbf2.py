#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 分析第2轮 — 深入寻找数据区和记录结构
"""
import struct
import os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

def read_chunk(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def analyze_index_section(filepath, label):
    """分析索引区的记录结构"""
    print(f"\n{'='*60}")
    print(f"索引区分析: {os.path.basename(filepath)} ({label})")

    data = read_chunk(filepath, 0, 0x200000)  # 前 2MB

    # 已知：27 12 00 00 是功率文件的标记，77 32 00 00 是电流文件的标记
    if b'\x27\x12\x00\x00' in data[:0x200000]:
        marker = b'\x27\x12\x00\x00'
        print(f"文件类型标记: 27 12 00 00 (功率)")
    elif b'\x77\x32\x00\x00' in data[:0x200000]:
        marker = b'\x77\x32\x00\x00'
        print(f"文件类型标记: 77 32 00 00 (电流)")
    else:
        print("未找到已知标记，搜索中...")
        return None

    # 找到所有标记位置
    positions = []
    pos = 0
    while True:
        p = data.find(marker, pos)
        if p == -1:
            break
        positions.append(p)
        pos = p + 1

    print(f"标记出现次数: {len(positions)}")

    if len(positions) < 5:
        print("标记不足")
        return None

    # 分析标记周围的结构
    # 标记位于记录的第12-15字节位置（从记录头开始算）
    # 记录结构: [4B seq] [4B id?] [4B offset?] [4B marker] [4B ts] [4B ts2?] [4B size?] [4B 0]

    records = []
    for p in positions[:200]:  # 分析前200个
        rec_start = p - 12  # 记录头从标记前12字节开始
        if rec_start < 0:
            continue
        rec = data[rec_start:rec_start+32]
        if len(rec) < 32:
            continue

        seq = struct.unpack('<I', rec[0:4])[0]
        field1 = struct.unpack('<I', rec[4:8])[0]
        field2 = struct.unpack('<I', rec[8:12])[0]
        mark_val = struct.unpack('<I', rec[12:16])[0]
        ts1 = struct.unpack('<I', rec[16:20])[0]
        ts2 = struct.unpack('<I', rec[20:24])[0]
        field3 = struct.unpack('<I', rec[24:28])[0]
        zero = struct.unpack('<I', rec[28:32])[0]

        records.append({
            'offset': rec_start,
            'seq': seq,
            'field1': field1,
            'field2': field2,
            'marker': mark_val,
            'ts1': ts1,
            'ts2': ts2,
            'field3': field3,
            'zero': zero
        })

    print(f"\n前10条记录:")
    for r in records[:10]:
        ts1_str = datetime.fromtimestamp(r['ts1']).strftime('%Y-%m-%d %H:%M:%S') if 1_700_000_000 < r['ts1'] < 1_800_000_000 else f"0x{r['ts1']:08x}"
        ts2_str = datetime.fromtimestamp(r['ts2']).strftime('%Y-%m-%d %H:%M:%S') if 1_700_000_000 < r['ts2'] < 1_800_000_000 else f"0x{r['ts2']:08x}"
        print(f"  @0x{r['offset']:06x} seq={r['seq']:5d} f1={r['field1']:5d} f2={r['field2']:7d} "
              f"mark=0x{r['marker']:04x} ts1={ts1_str} ts2={ts2_str} f3={r['field3']:7d} zero={r['zero']}")

    # 分析字段含义
    print(f"\n字段统计分析:")
    seqs = [r['seq'] for r in records]
    f1s = [r['field1'] for r in records]
    f2s = [r['field2'] for r in records]
    f3s = [r['field3'] for r in records]

    print(f"  seq: 范围 [{min(seqs)}, {max(seqs)}], 非连续段数: {sum(1 for i in range(1,len(seqs)) if seqs[i]!=seqs[i-1]+1)}")
    print(f"  field1 (可能是道岔/相别ID): 范围 [{min(f1s)}, {max(f1s)}], 唯一值: {sorted(set(f1s))}")
    print(f"  field2 (可能是数据偏移): 范围 [{min(f2s)}, {max(f2s)}]")
    print(f"  field3 (可能是数据大小): 范围 [{min(f3s)}, {max(f3s)}], 唯一值: {sorted(set(f3s))[:30]}")

    # 时间戳分析
    ts1_valid = [r['ts1'] for r in records if 1_700_000_000 < r['ts1'] < 1_800_000_000]
    ts2_valid = [r['ts2'] for r in records if 1_700_000_000 < r['ts2'] < 1_800_000_000]
    if ts1_valid:
        print(f"  ts1: {datetime.fromtimestamp(min(ts1_valid))} ~ {datetime.fromtimestamp(max(ts1_valid))}")
    if ts2_valid:
        print(f"  ts2: {datetime.fromtimestamp(min(ts2_valid))} ~ {datetime.fromtimestamp(max(ts2_valid))}")

    return records, marker

def search_data_section(filepath, label, marker=None):
    """搜索实际采样数据区域（索引区之后）"""
    print(f"\n{'='*60}")
    print(f"搜索数据区: {os.path.basename(filepath)} ({label})")

    file_size = os.path.getsize(filepath)

    # 扫描整个文件，找到数据密度高且结构不同的区域
    # 采样数据通常表现为: 大量连续的非零值，数值在合理范围（0-10A, 0-5kW）

    # 采样间隔：每64MB取一个1MB样本
    SAMPLE_GAP = 0x4000000  # 64MB
    SAMPLE_SIZE = 0x100000  # 1MB

    print(f"文件大小: {file_size:,} bytes ({file_size/1024/1024:.1f} MB)")
    print(f"采样策略: 每{SAMPLE_GAP/1024/1024:.0f}MB取{SAMPLE_SIZE/1024/1024:.0f}MB")

    segments = []
    for start in range(0, file_size, SAMPLE_GAP):
        chunk = read_chunk(filepath, start, min(SAMPLE_SIZE, file_size - start))

        # 统计特征
        nonzero = sum(1 for b in chunk if b != 0)
        density = nonzero / len(chunk) * 100

        # 尝试 float32 解析
        float_count = len(chunk) // 4
        valid_floats = 0
        float_vals = []
        for j in range(0, min(1000, float_count)):
            f = struct.unpack('<f', chunk[j*4:(j+1)*4])[0]
            if 0.001 < abs(f) < 100:  # 合理范围
                valid_floats += 1
                if len(float_vals) < 30:
                    float_vals.append(round(f, 3))

        # 尝试 int16 解析
        int_count = len(chunk) // 2
        valid_ints = 0
        int_vals = []
        for j in range(0, min(1000, int_count)):
            v = struct.unpack('<h', chunk[j*2:(j+1)*2])[0]
            if 10 < abs(v) < 30000:
                valid_ints += 1
                if len(int_vals) < 30:
                    int_vals.append(v)

        # 把字节当 uint8 分析直方图
        byte_vals = list(chunk[:10000])
        unique_bytes = len(set(byte_vals))

        segments.append({
            'offset': start,
            'density': density,
            'valid_float_pct': valid_floats / 10,  # percentage of first 1000
            'valid_int_pct': valid_ints / 10,
            'unique_bytes': unique_bytes,
            'float_samples': float_vals,
            'int_samples': int_vals,
        })

    # 打印所有段
    print(f"\n段分析 (按density排序):")
    by_density = sorted(segments, key=lambda s: s['density'], reverse=True)
    for s in by_density[:15]:
        print(f"  [0x{s['offset']:09x}] density={s['density']:.1f}% "
              f"float_valid={s['valid_float_pct']:.0f}% int_valid={s['valid_int_pct']:.0f}% "
              f"unique_bytes={s['unique_bytes']}")
        if s['float_samples']:
            print(f"    float: {s['float_samples'][:10]}")
        if s['int_samples']:
            print(f"    int:   {s['int_samples'][:10]}")

    # 找数据最密集的段
    best = max(segments, key=lambda s: s['valid_int_pct'])
    print(f"\n最佳数据候选: offset 0x{best['offset']:09x}")

    return segments

def deep_dive_data(filepath, label, target_offset):
    """深入分析特定偏移的数据"""
    print(f"\n{'='*60}")
    print(f"深入分析 @ 0x{target_offset:09x}: {os.path.basename(filepath)}")

    data = read_chunk(filepath, target_offset, 0x10000)  # 64KB

    # 尝试各种编码
    print(f"\n[1] 原始字节 (前256B):")
    for i in range(0, 256, 32):
        hex_s = ' '.join(f'{b:02x}' for b in data[i:i+32])
        print(f"  [{i:4d}] {hex_s}")

    # int16 分析
    print(f"\n[2] int16 LE 解析 (前200个):")
    int16_vals = []
    for j in range(0, min(400, len(data)-2), 2):
        v = struct.unpack('<h', data[j:j+2])[0]
        int16_vals.append(v)
    print(f"  范围: [{min(int16_vals)}, {max(int16_vals)}]")
    print(f"  前50: {int16_vals[:50]}")
    print(f"  100-150: {int16_vals[100:150]}")

    # uint16 分析
    print(f"\n[3] uint16 LE 解析 (前200个):")
    uint16_vals = []
    for j in range(0, min(400, len(data)-2), 2):
        v = struct.unpack('<H', data[j:j+2])[0]
        uint16_vals.append(v)
    print(f"  范围: [{min(uint16_vals)}, {max(uint16_vals)}]")
    print(f"  前50: {uint16_vals[:50]}")
    print(f"  100-150: {uint16_vals[100:150]}")

    # float32 分析
    print(f"\n[4] float32 LE 解析 (前100个):")
    float_vals = []
    for j in range(0, min(400, len(data)-4), 4):
        f = struct.unpack('<f', data[j:j+4])[0]
        float_vals.append(f)
    print(f"  范围: [{min(float_vals):.4f}, {max(float_vals):.4f}]")
    print(f"  前25: {[round(f,4) for f in float_vals[:25]]}")
    print(f"  50-75: {[round(f,4) for f in float_vals[50:75]]}")

    # 寻找似曾相识的功率曲线模式
    # 道岔功率曲线特征: 0→启动峰值(3-5kW)→稳态(0.2-0.4kW)→0
    if float_vals:
        peaks = [f for f in float_vals[:100] if f > 1.0]
        smalls = [f for f in float_vals[:100] if 0 < f < 1.0]
        zeros_start = sum(1 for f in float_vals[:10] if abs(f) < 0.01)
        print(f"  >1.0: {len(peaks)}个, 0~1.0: {len(smalls)}个, 开头零: {zeros_start}/10")
        if zeros_start >= 5 and len(peaks) >= 1:
            print(f"  ✅ 匹配道岔功率曲线模式!")
        elif len(smalls) > 50:
            print(f"  ⚠️ 可能是电流曲线 (小值为主)")

    # 如果 int16 看起来像采样值，检查是否有道岔功率曲线特征
    # 功率: 0→3-5kW→0.2-0.4kW→0 (int16 可能缩放了100倍: 0→300-500→20-40→0)
    if int16_vals:
        peaks = [v for v in int16_vals[:200] if v > 200]
        steady = [v for v in int16_vals[:200] if 0 < v <= 200]
        zeros_start = sum(1 for v in int16_vals[:10] if v == 0)
        print(f"  int16: >200: {len(peaks)}个, 1-200: {len(steady)}个, 开头0: {zeros_start}/10")
        if zeros_start >= 5 and len(peaks) >= 1:
            print(f"  ✅ int16 也匹配道岔功率曲线!")

def scan_end_of_index(filepath, label):
    """找到索引区结束和数据区开始的位置"""
    print(f"\n{'='*60}")
    print(f"索引-数据边界扫描: {os.path.basename(filepath)}")

    # 扫描 ~0x100000 到 ~0x800000 找到数据模式变化
    step = 0x10000  # 64KB steps
    for offset in range(0x80000, 0x200000, step):
        chunk = read_chunk(filepath, offset, 0x4000)

        # 检测: 是否有连续的记录结构? (通过寻找标记)
        # 或者: 是否有连续的采样数据?

        # 检查每32字节的规律性
        uint32_values = []
        for i in range(0, min(256, len(chunk)-4), 4):
            uint32_values.append(struct.unpack('<I', chunk[i:i+4])[0])

        # 检查是否有序列号递增
        increasing = 0
        for i in range(1, min(32, len(uint32_values))):
            if uint32_values[i] == uint32_values[i-1] + 1:
                increasing += 1

        if increasing >= 3:  # 有序列号 = 还在索引区
            pass
        else:
            # 可能是数据区 — 检查 float32/int16 质量
            valid_int16 = 0
            for j in range(0, min(200, len(chunk)-2), 2):
                v = struct.unpack('<h', chunk[j:j+2])[0]
                if -30000 < v < 30000 and v != 0:
                    valid_int16 += 1

            if valid_int16 > 50:
                print(f"  可能的数据区起始: 0x{offset:06x} (int16有效={valid_int16}/200, "
                      f"increasing_seq={increasing})")
                return offset

    return None

if __name__ == '__main__':
    pf_path = os.path.join(HBF_POWER_DIR, '1.hbf')
    cf_path = os.path.join(HBF_CURRENT_DIR, '1.hbf')

    # Step 1: 索引区分析
    power_records, power_marker = analyze_index_section(pf_path, "功率")
    current_records, current_marker = analyze_index_section(cf_path, "电流")

    # Step 2: 全文件数据扫描
    power_segments = search_data_section(pf_path, "功率", power_marker)
    current_segments = search_data_section(cf_path, "电流", current_marker)

    # Step 3: 寻找索引-数据边界
    power_data_start = scan_end_of_index(pf_path, "功率")
    current_data_start = scan_end_of_index(cf_path, "电流")

    # Step 4: 深入分析数据区
    if power_data_start:
        deep_dive_data(pf_path, "功率", power_data_start)
    else:
        # 手动指定偏移尝试
        for test_offset in [0x100000, 0x180000, 0x200000, 0x400000, 0x800000, 0xC00000]:
            deep_dive_data(pf_path, f"功率@0x{test_offset:x}", test_offset)
            print()

    if current_data_start:
        deep_dive_data(cf_path, "电流", current_data_start)

    print("\n✅ 第2轮分析完成")
