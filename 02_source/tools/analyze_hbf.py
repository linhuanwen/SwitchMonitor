#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 二进制格式深度分析脚本
逐步逆向道岔动作曲线 .hbf 文件格式
"""
import struct
import sys
import os
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"
HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"

MARKER = bytes.fromhex('1c6c5e02d5d7de08')
MAGIC = b'hhcsmfzz'

def read_chunk(filepath, offset, size):
    """安全读取文件片段"""
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_all_markers(data, marker=MARKER, start=0):
    """查找所有标记位置"""
    positions = []
    pos = start
    while True:
        p = data.find(marker, pos)
        if p == -1:
            break
        positions.append(p)
        pos = p + 1
    return positions

def analyze_header(filepath):
    """分析 HBF 文件头部结构"""
    print(f"\n{'='*70}")
    print(f"文件: {os.path.basename(filepath)}")
    print(f"大小: {os.path.getsize(filepath):,} bytes ({os.path.getsize(filepath)/1024/1024:.1f} MB)")
    print(f"{'='*70}")

    data = read_chunk(filepath, 0, 0x200000)  # 读前 2MB
    print(f"分析范围: 前 {len(data):,} bytes")

    # 1. Magic
    magic = data[:8]
    print(f"\n[1] 文件魔数: {magic} ({'✅ 有效' if magic == MAGIC else '❌ 无效'})")

    # 2. Header bytes after magic
    print(f"\n[2] Header 原始字节 (offset 8-63):")
    header_bytes = data[8:64]
    for i in range(0, len(header_bytes), 16):
        hex_str = ' '.join(f'{b:02x}' for b in header_bytes[i:i+16])
        ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in header_bytes[i:i+16])
        print(f"  [{8+i:4d}] {hex_str:<48s} {ascii_str}")

    # 3. Parse header as uint32 LE values
    print(f"\n[3] Header uint32 LE 解析:")
    for i in range(0, min(128, len(data)-4), 4):
        val = struct.unpack('<I', data[i:i+4])[0]
        # 筛选有意义的值
        label = ""
        if 2020 <= val <= 2030:
            label = f"  ← 可能是年份?"
        elif 1 <= val <= 1000:
            label = f"  ← 计数值={val}"
        elif 0x100000 <= val <= 0xFFFFFFFF:
            # 高位字节有意义
            hi = (val >> 24) & 0xFF
            if 0 <= hi <= 31:
                label = f"  ← high_byte={hi} (可能是文件索引?)"
        if label or (0 < val < 0x10000):
            print(f"  offset {i:5d}: {val:12d} (0x{val:08x}){label}")

    # 4. 查找 MARKER
    markers = find_all_markers(data, MARKER)
    print(f"\n[4] 记录分隔标记 (MARKER): 找到 {len(markers)} 个")

    if markers:
        print(f"  首个标记位置: offset {markers[0]} (0x{markers[0]:x})")
        if len(markers) > 1:
            print(f"  标记间距: {[markers[i+1]-markers[i] for i in range(min(5, len(markers)-1))]}")

        # 分析标记前/后的数据结构
        for m_idx, m_pos in enumerate(markers[:5]):  # 只看前5个
            print(f"\n  --- 标记 #{m_idx+1} @ offset {m_pos} (0x{m_pos:x}) ---")
            # 标记前 32 bytes
            pre = data[max(0, m_pos-32):m_pos]
            print(f"  标记前 32B: {' '.join(f'{b:02x}' for b in pre)}")
            # 标记后 64 bytes
            post = data[m_pos+8:m_pos+72]
            print(f"  标记后 64B: {' '.join(f'{b:02x}' for b in post)}")

            # 尝试解析标记后的 uint32
            print(f"  标记后 uint32 LE:")
            for j in range(0, min(64, len(post)-4), 4):
                v = struct.unpack('<I', post[j:j+4])[0]
                ts_str = ""
                if 1_700_000_000 < v < 1_800_000_000:
                    ts_str = f"  ← Unix TS: {datetime.fromtimestamp(v)}"
                elif 1_500_000_000 < v < 2_000_000_000:
                    ts_str = f"  ← Unix TS: {datetime.fromtimestamp(v)}"
                print(f"    +{j:3d}: {v:12d} (0x{v:08x}){ts_str}")

    # 5. 全扫描时间戳
    print(f"\n[5] 扫描有效 Unix 时间戳 (前 2MB)...")
    timestamps = []
    for i in range(0, len(data) - 4, 4):
        val = struct.unpack('<I', data[i:i+4])[0]
        if 1_782_000_000 < val < 1_786_000_000:  # 2026-06 ~ 2026-08
            timestamps.append((i, val))

    print(f"  找到 {len(timestamps)} 个时间戳")
    if timestamps:
        ts_values = [t[1] for t in timestamps]
        print(f"  时间范围: {datetime.fromtimestamp(min(ts_values))} ~ {datetime.fromtimestamp(max(ts_values))}")

        # 分析时间戳之间的字节距离
        ts_positions = [t[0] for t in timestamps[:50]]
        gaps = [ts_positions[i+1] - ts_positions[i] for i in range(len(ts_positions)-1)]
        print(f"  时间戳间距 (前50个): min={min(gaps)}, max={max(gaps)}, median={sorted(gaps)[len(gaps)//2]}")

        # 显示前20个时间戳及其周围上下文
        print(f"\n  前20个时间戳详情:")
        for pos, ts in timestamps[:20]:
            ctx_start = max(0, pos - 16)
            ctx = data[ctx_start:pos + 32]
            print(f"    offset {pos:7d} | {datetime.fromtimestamp(ts)} | ctx: {' '.join(f'{b:02x}' for b in ctx[:48])}")

    # 6. 数据密度分析 - 跳过零区域
    print(f"\n[6] 数据密度分析:")
    chunk_size = 0x10000  # 64KB chunks
    for chunk_start in range(0, min(0x200000, len(data)), chunk_size):
        chunk = data[chunk_start:chunk_start+chunk_size]
        nonzero = sum(1 for b in chunk if b != 0)
        density = nonzero / len(chunk) * 100
        if density > 5:  # 只报告有数据的区域
            print(f"  [0x{chunk_start:06x}-0x{chunk_start+chunk_size:06x}] 密度: {density:.1f}%")

    # 7. 寻找非零数据起始点（记录数据区）
    print(f"\n[7] 寻找第一个非零数据区:")
    # 跳过 header 和可能的索引区
    for start_search in [0x1000, 0x10000, 0x40000, 0x80000, 0xC0000, 0x100000, 0x140000, 0x180000]:
        if start_search >= len(data):
            break
        chunk = data[start_search:start_search+0x100]
        nonzero = sum(1 for b in chunk if b != 0)
        if nonzero > 10:
            print(f"  找到数据区 @ 0x{start_search:06x}: {nonzero}/256 非零字节")
            print(f"    原始: {' '.join(f'{b:02x}' for b in chunk[:64])}")
            # 尝试 float32 解析
            for j in range(0, min(64, len(chunk)-4), 4):
                fval = struct.unpack('<f', chunk[j:j+4])[0]
                if abs(fval) > 0.001:
                    print(f"      [+{j:3d}] float32: {fval:.6f}")
            break

    return data, markers

def analyze_record_structure(filepath):
    """深入分析单个记录的数据结构"""
    print(f"\n{'='*70}")
    print(f"记录结构深度分析: {os.path.basename(filepath)}")
    print(f"{'='*70}")

    # 读取更大范围来找到清晰的记录模式
    data = read_chunk(filepath, 0, 0x800000)  # 前 8MB

    markers = find_all_markers(data, MARKER)
    print(f"总标记数(前8MB): {len(markers)}")

    if len(markers) < 3:
        print("标记不足，尝试其他方法...")
        return

    # 分析标记之间的数据
    print(f"\n标记间数据分析:")
    for i in range(min(10, len(markers) - 1)):
        m1 = markers[i] + 8  # 跳过标记本身
        m2 = markers[i + 1]
        seg = data[m1:m2]
        seg_len = len(seg)

        print(f"\n记录 #{i+1}: offset {m1}-{m2} ({seg_len} bytes)")
        if seg_len < 8:
            print(f"  长度太短({seg_len}B)，跳过")
            continue

        # 前 128 bytes hex
        preview = seg[:min(128, seg_len)]
        for j in range(0, len(preview), 32):
            line = preview[j:j+32]
            hex_s = ' '.join(f'{b:02x}' for b in line)
            print(f"  [{j:4d}] {hex_s}")

        # 解析为 uint32 序列，寻找模式
        print(f"  uint32 解析:")
        ts_found = 0
        for j in range(0, min(256, seg_len - 4), 4):
            v = struct.unpack('<I', seg[j:j+4])[0]
            if 1_780_000_000 < v < 1_790_000_000:
                ts_found += 1
                print(f"    +{j:4d}: {v} → {datetime.fromtimestamp(v)} ← 时间戳")
            elif 1 <= v <= 3000:
                # 可能是采样点数/开关号等
                if 100 < v < 2000:
                    print(f"    +{j:4d}: {v:5d} (0x{v:08x}) ← 可能是采样点数")
                elif 1 <= v <= 100:
                    print(f"    +{j:4d}: {v:5d} (0x{v:08x}) ← 可能是开关号/小整数")
            elif 0 < v < 0x100:
                print(f"    +{j:4d}: {v:5d} (0x{v:08x}) ← 小整数")

        if ts_found == 0 and seg_len > 256:
            # 尝试 int16 解析
            print(f"  int16 LE 解析 (前128个):")
            for j in range(0, min(256, seg_len - 2), 2):
                v = struct.unpack('<h', seg[j:j+2])[0]
                if j < 20 or (abs(v) > 100 and abs(v) < 10000):
                    print(f"    +{j:4d}: {v:6d}")

        if i >= 10:
            break

def analyze_sample_encoding(filepath):
    """试图确定采样数据的编码方式"""
    print(f"\n{'='*70}")
    print(f"采样编码分析: {os.path.basename(filepath)}")
    print(f"{'='*70}")

    data = read_chunk(filepath, 0, 0x600000)  # 前 6MB

    # 查找数据密集区域
    markers = find_all_markers(data, MARKER)
    if len(markers) < 3:
        print("标记不足，在文件中搜索数据模式...")
        return

    # 从第二个标记开始（第一个可能是header的一部分）
    for m_idx in [1, 2, 5, 10]:
        if m_idx >= len(markers) - 1:
            break

        m_start = markers[m_idx] + 8
        m_end = markers[m_idx + 1]
        seg = data[m_start:m_end]

        if len(seg) < 200:
            continue

        # 跳过头部字段（时间戳等），找到数据体
        # 策略：找到时间戳后，跳过一些header字段，剩下的可能是采样数据
        data_start = 0
        for j in range(0, min(256, len(seg) - 4), 4):
            v = struct.unpack('<I', seg[j:j+4])[0]
            if 1_780_000_000 < v < 1_790_000_000:
                data_start = j + 16  # 跳过 timestamp + header
                break

        if data_start == 0:
            data_start = 32  # 默认跳过固定头部

        raw_samples = seg[data_start:]
        sample_count = len(raw_samples) // 4  # 假设 float32
        sample_count_2 = len(raw_samples) // 2  # 假设 int16

        print(f"\n记录 #{m_idx+1}: 总长度={len(seg)}, 采样区起始={data_start}, 采样区={len(raw_samples)}B")
        print(f"  假设 float32: {sample_count} 个采样点")
        print(f"  假设 int16:   {sample_count_2} 个采样点")

        # 安全措施：限制尝试的样本数
        max_try = min(sample_count, 200)

        # float32
        floats = []
        for j in range(0, min(max_try * 4, len(raw_samples) - 4), 4):
            f = struct.unpack('<f', raw_samples[j:j+4])[0]
            floats.append(f)

        print(f"  float32: min={min(floats):.4f}, max={max(floats):.4f}, mean={sum(floats)/len(floats):.4f}")
        print(f"    前30: {[round(f,3) for f in floats[:30]]}")
        print(f"    中段: {[round(f,3) for f in floats[max_try//2-15:max_try//2+15]]}")

        # int16
        ints = []
        for j in range(0, min(max_try * 2, len(raw_samples) - 2), 2):
            i = struct.unpack('<h', raw_samples[j:j+2])[0]
            ints.append(i)

        print(f"  int16:   min={min(ints):.4f}, max={max(ints):.4f}, mean={sum(ints)/len(ints):.4f}")
        print(f"    前30: {ints[:30]}")
        print(f"    中段: {ints[max_try//2-15:max_try//2+15]}")

        # 假设是 uint16 + scale
        uints = []
        for j in range(0, min(max_try * 2, len(raw_samples) - 2), 2):
            u = struct.unpack('<H', raw_samples[j:j+2])[0]
            uints.append(u)

        print(f"  uint16:  min={min(uints)}, max={max(uints)}, mean={sum(uints)/len(uints):.1f}")
        print(f"    前30: {uints[:30]}")
        print(f"    中段: {uints[max_try//2-15:max_try//2+15]}")

        if m_idx >= 10:
            break

def compare_power_current():
    """对比功率和电流 HBF 文件结构差异"""
    print(f"\n{'='*70}")
    print(f"功率 vs 电流 HBF 对比分析")
    print(f"{'='*70}")

    # 取第一个文件作为代表
    power_files = sorted([f for f in os.listdir(HBF_POWER_DIR) if f.endswith('.hbf')])
    current_files = sorted([f for f in os.listdir(HBF_CURRENT_DIR) if f.endswith('.hbf')])

    if not power_files or not current_files:
        print("找不到文件")
        return

    pf = os.path.join(HBF_POWER_DIR, power_files[0])
    cf = os.path.join(HBF_CURRENT_DIR, current_files[0])

    print(f"功率文件: {power_files[0]} ({os.path.getsize(pf):,} bytes)")
    print(f"电流文件: {current_files[0]} ({os.path.getsize(cf):,} bytes)")

    pdata = read_chunk(pf, 0, 0x100000)
    cdata = read_chunk(cf, 0, 0x100000)

    # Header 对比
    print(f"\nHeader 对比 (前128字节):")
    print(f"  功率: {' '.join(f'{b:02x}' for b in pdata[8:72])}")
    print(f"  电流: {' '.join(f'{b:02x}' for b in cdata[8:72])}")

    # 寻找差异
    diffs = []
    for i in range(min(256, len(pdata), len(cdata))):
        if pdata[i] != cdata[i]:
            diffs.append((i, pdata[i], cdata[i]))
    print(f"\nHeader 差异字节数(前256B): {len(diffs)}")
    for offset, pv, cv in diffs[:20]:
        print(f"  offset {offset:3d}: 功率={pv:02x}, 电流={cv:02x}")

    # 标记对比
    p_markers = find_all_markers(pdata, MARKER)
    c_markers = find_all_markers(cdata, MARKER)
    print(f"\n前1MB标记数: 功率={len(p_markers)}, 电流={len(c_markers)}")


def scan_full_file_structure(filepath, label=""):
    """扫描整个 HBF 文件的结构概览（分段分析）"""
    print(f"\n{'='*70}")
    print(f"全文件结构扫描: {os.path.basename(filepath)} {label}")
    print(f"{'='*70}")

    file_size = os.path.getsize(filepath)

    # 分段扫描 - 每 32MB 采样分析
    SEGMENT = 0x2000000  # 32MB
    num_segments = (file_size + SEGMENT - 1) // SEGMENT

    all_markers = []
    segment_markers = []

    for seg_idx in range(min(num_segments, 20)):  # 最多20段
        offset = seg_idx * SEGMENT
        size = min(SEGMENT, file_size - offset)
        if size <= 0:
            break

        data = read_chunk(filepath, offset, min(size, 0x100000))  # 每段只读1MB
        markers = find_all_markers(data, MARKER)
        segment_markers.append((offset, len(markers)))
        all_markers.extend([offset + m for m in markers])

    print(f"分段标记密度:")
    for offset, count in segment_markers:
        print(f"  [0x{offset:08x}] {count} 个标记/1MB")

    # 统计时间戳分布
    print(f"\n全段时间戳扫描 (每64MB采1MB)...")
    SAMPLE_INTERVAL = 0x4000000  # 64MB
    ts_distribution = []
    for seg_start in range(0, file_size, SAMPLE_INTERVAL):
        data = read_chunk(filepath, seg_start, min(0x100000, file_size - seg_start))
        tss = []
        for i in range(0, len(data) - 4, 4):
            v = struct.unpack('<I', data[i:i+4])[0]
            if 1_782_000_000 < v < 1_786_000_000:
                tss.append(v)
        ts_distribution.append((seg_start, len(tss), tss[:3] if tss else []))

    total_ts = sum(t[1] for t in ts_distribution)
    print(f"  估计总时间戳数: {total_ts}")
    for seg_start, count, samples in ts_distribution[:15]:
        if count > 0:
            sample_str = ', '.join(datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S') for ts in samples)
            print(f"  [0x{seg_start:08x}] {count:4d} TS | {sample_str}")

    return all_markers


if __name__ == '__main__':
    # 选择分析文件
    current_files = sorted([f for f in os.listdir(HBF_CURRENT_DIR) if f.endswith('.hbf')])
    power_files = sorted([f for f in os.listdir(HBF_POWER_DIR) if f.endswith('.hbf')])

    print("HBF 格式深度逆向分析")
    print("="*70)
    print(f"电流曲线文件: {current_files}")
    print(f"功率曲线文件: {power_files}")

    # Step 1: 分析电流 HBF header
    cf_path = os.path.join(HBF_CURRENT_DIR, current_files[0])
    pf_path = os.path.join(HBF_POWER_DIR, power_files[0])

    data, markers = analyze_header(cf_path)
    analyze_header(pf_path)

    # Step 2: 深入记录结构
    analyze_record_structure(cf_path)
    analyze_record_structure(pf_path)

    # Step 3: 采样编码分析
    analyze_sample_encoding(cf_path)
    analyze_sample_encoding(pf_path)

    # Step 4: 功率vs电流对比
    compare_power_current()

    # Step 5: 全文件扫描
    scan_full_file_structure(cf_path, "(电流)")
    scan_full_file_structure(pf_path, "(功率)")

    print("\n\n✅ 分析完成!")
