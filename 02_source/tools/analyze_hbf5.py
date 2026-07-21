#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 分析第5轮 — 利用发现的 switch ID 字符串定位记录结构
突破: HBF 文件中嵌入了 ASCII switch ID (如 "1-J", "7-X")
"""
import struct
import sys
import os
import re
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

# 30个番禺道岔
SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_chunk(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_all_switch_ids(filepath, label):
    """在HBF文件中搜索所有道岔ID字符串的位置"""
    print(f"\n{'='*60}")
    print(f"Switch ID 搜索: {os.path.basename(filepath)} ({label})")

    data = read_chunk(filepath, 0, 0x800000)  # 前8MB

    positions = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = 0
        while True:
            p = data.find(pattern, pos)
            if p == -1:
                break
            positions.append((p, sw_id))
            pos = p + 1

    positions.sort()
    print(f"找到 {len(positions)} 个 switch ID 出现位置")

    # 按 switch ID 分组统计
    by_sw = defaultdict(list)
    for p, sw in positions:
        by_sw[sw].append(p)

    for sw in sorted(by_sw.keys(), key=lambda x: (int(re.match(r'(\d+)', x).group(1)), x[-1])):
        positions_list = by_sw[sw]
        print(f"  {sw}: {len(positions_list)} 次, 位置: {[f'0x{p:x}' for p in positions_list[:5]]}" +
              (f" ..." if len(positions_list) > 5 else ""))

    return positions, by_sw

def analyze_record_around_id(filepath, positions, label):
    """分析 switch ID 周围的完整记录结构"""
    print(f"\n{'='*60}")
    print(f"记录结构分析: {os.path.basename(filepath)} ({label})")

    file_size = os.path.getsize(filepath)
    file_data_early = read_chunk(filepath, 0, 0x800000)

    # 选几个代表性的来分析
    samples_to_analyze = []
    for p, sw_id in positions[:20]:
        # 从 switch ID 往前搜索记录起始标记
        ctx = file_data_early[max(0, p-64):p+64]

        # 找到记录头的可能起始 (寻找合理的结构边界)
        # 往前找 marker 或 header 起始
        for lookback in range(0, min(64, p), 4):
            candidate_start = p - lookback
            candidate = file_data_early[candidate_start:candidate_start+32]
            if len(candidate) < 32:
                continue

            # 检查是否有 uint16 字段模式: small_uint16, small_uint16, small_uint16, 0, ASCII...
            v0 = struct.unpack('<H', candidate[0:2])[0]
            v1 = struct.unpack('<H', candidate[2:4])[0]
            v2 = struct.unpack('<H', candidate[4:6])[0]
            v3 = struct.unpack('<H', candidate[6:8])[0]

            # ASCII 在 offset 8-11 应该是 "XX-X\0" (4字节)
            ascii_at_8 = candidate[8:12]
            ascii_at_12 = candidate[12:16]

            if (v3 == 0 and
                (ascii_at_8 == sw_id.encode('ascii') + b'\x00' or
                 ascii_at_8[-1:] == b'\x00')):
                samples_to_analyze.append((candidate_start, sw_id, candidate))
                break
            elif (v3 == 0 and
                  ascii_at_12 == sw_id.encode('ascii') + b'\x00'):
                samples_to_analyze.append((candidate_start + 4, sw_id, candidate[4:36]))
                break

    if not samples_to_analyze:
        print("无法找到记录头!")
        # 直接查看 switch ID 附近的原始字节
        for p, sw_id in positions[:3]:
            ctx = file_data_early[max(0, p-32):p+32]
            print(f"\n  {sw_id} @ 0x{p:x}:")
            for i in range(0, len(ctx), 16):
                line = ctx[i:i+16]
                print(f"    {p-32+i:5d}: {' '.join(f'{b:02x}' for b in line)}")
        return

    print(f"找到 {len(samples_to_analyze)} 个有效记录头")

    # 详细分析记录结构
    for rec_start, sw_id, header_bytes in samples_to_analyze[:5]:
        print(f"\n  --- {sw_id} @ 0x{rec_start:x} ---")
        for i in range(0, 32, 16):
            line = header_bytes[i:i+16]
            print(f"    [{i:2d}] {' '.join(f'{b:02x}' for b in line)}")

        # 尝试不同的结构解析
        print(f"    作为 uint16 LE:")
        for i in range(0, 16, 2):
            v = struct.unpack('<H', header_bytes[i:i+2])[0]
            print(f"      [{i:2d}] {v:6d} (0x{v:04x})")
        print(f"    作为 uint32 LE:")
        for i in range(0, 16, 4):
            v = struct.unpack('<I', header_bytes[i:i+4])[0]
            desc = ""
            if 1_780_000_000 < v < 1_790_000_000:
                desc = f" ← TS: {datetime.fromtimestamp(v)}"
            elif 100 < v < 3000:
                desc = f" ← 可能是采样点数={v}"
            print(f"      [{i:2d}] {v:12d} (0x{v:08x}){desc}")

        # 检查 header 后面是否直接跟着数据
        # 读取 header 后的数据
        data_after_header = read_chunk(filepath, rec_start + 32, 200)
        print(f"    header后数据(int16 LE, 前50): {[struct.unpack('<h', data_after_header[i*2:(i+1)*2])[0] for i in range(50)]}")
        print(f"    header后数据(uint16 LE, 前50): {[struct.unpack('<H', data_after_header[i*2:(i+1)*2])[0] for i in range(50)]}")

    return samples_to_analyze

def try_data_encoding(filepath, samples, label):
    """基于记录位置尝试多种数据编码"""
    print(f"\n{'='*60}")
    print(f"数据编码测试: {os.path.basename(filepath)}")

    if not samples:
        return

    file_data = read_chunk(filepath, 0, 0x800000)

    for rec_start, sw_id, hdr in samples[:3]:
        data_start = rec_start + 32  # 假设header是32字节
        data_raw = file_data[data_start:data_start+2400]  # 读取2400字节

        print(f"\n  {sw_id} @ 0x{rec_start:x}, 数据区 @ 0x{data_start:x}")

        # 尝试1: int16 LE (电流/功率原始值)
        int16_data = [struct.unpack('<h', data_raw[i*2:(i+1)*2])[0] for i in range(min(400, len(data_raw)//2))]
        peaks = [v for v in int16_data[:40] if abs(v) > 100]
        steady_vals = [v for v in int16_data[40:200] if 10 < abs(v) < 500]
        zeros = sum(1 for v in int16_data[:10] if abs(v) < 5)

        print(f"    int16: 前10零点={zeros} 尖峰(>|100|)={peaks[:5]} 稳态值数={len(steady_vals)}")
        if zeros >= 5 and len(peaks) > 0:
            print(f"    ✅ int16 匹配! 前40: {int16_data[:40]}")
        else:
            print(f"    前40: {int16_data[:40]}")

        # 尝试2: uint16 LE + 偏移
        uint16_data = [struct.unpack('<H', data_raw[i*2:(i+1)*2])[0] for i in range(min(400, len(data_raw)//2))]
        print(f"    uint16: 前40: {uint16_data[:40]}")

        # 尝试3: 两个 uint16 组成一个 float (高16位+低16位)
        # 可能是 uint32 = (high<<16) | low, 然后再 scale

        break

def find_data_boundary(filepath, samples, label):
    """精确定位 header → 数据 的边界"""
    print(f"\n{'='*60}")
    print(f"数据边界分析: {os.path.basename(filepath)}")

    if not samples:
        return

    file_data = read_chunk(filepath, 0, 0x800000)

    for rec_start, sw_id, hdr in samples[:1]:
        # 从 header 结束位置开始，查找"非零数据起始"
        # 道岔数据开头通常是一段接近零的值 (等待电流/功率建立)
        search_start = rec_start + 16  # header 最小可能大小
        search_end = rec_start + 256

        segment = file_data[search_start:search_end]

        print(f"\n  {sw_id} @ 0x{rec_start:x}, 边界搜索 [0x{search_start:x} - 0x{search_end:x}]:")
        for i in range(0, min(240, len(segment)), 16):
            abs_off = search_start + i
            line = segment[i:i+16]
            hex_s = ' '.join(f'{b:02x}' for b in line)

            # 标记可能的含义
            try:
                int16s = [struct.unpack('<h', line[j:j+2])[0] for j in range(0, 16, 2)]
                hex_s += f"  int16:{int16s}"
            except:
                pass

            print(f"    0x{abs_off:06x}: {hex_s}")

        # 检测从哪个位置开始连续有非零数据
        data_start = None
        consecutive_nonzero = 0
        for i in range(0, min(500, len(file_data) - rec_start - 32)):
            byte_val = file_data[rec_start + 32 + i]
            if byte_val != 0:
                consecutive_nonzero += 1
            else:
                if consecutive_nonzero > 10 and data_start is None:
                    # 之前有连续非零段
                    pass
                consecutive_nonzero = 0

            if consecutive_nonzero > 20 and data_start is None:
                data_start = rec_start + 32 + i - consecutive_nonzero
                print(f"\n  检测到数据起始 @ 0x{data_start:x} (连续{consecutive_nonzero}个非零字节)")
                break

        # 如果找到了，尝试读取数据
        if data_start:
            raw = file_data[data_start:data_start+1600]
            print(f"\n  数据(0x{data_start:x}起) 原始字节:")
            for i in range(0, min(128, len(raw)), 32):
                print(f"    +{i:4d}: {' '.join(f'{b:02x}' for b in raw[i:i+32])}")

if __name__ == '__main__':
    pf_path = os.path.join(HBF_POWER_DIR, '1.hbf')
    cf_path = os.path.join(HBF_CURRENT_DIR, '1.hbf')

    print("HBF 记录结构发现 (基于 Switch ID 字符串)")
    print("="*60)

    # Step 1: 找所有 switch ID
    p_positions, p_by_sw = find_all_switch_ids(pf_path, "功率")
    c_positions, c_by_sw = find_all_switch_ids(cf_path, "电流")

    # Step 2: 分析每个 switch ID 周围的记录结构
    p_samples = analyze_record_around_id(pf_path, p_positions, "功率")
    c_samples = analyze_record_around_id(cf_path, c_positions, "电流")

    # Step 3: 数据编码测试
    if p_samples:
        try_data_encoding(pf_path, p_samples, "功率")
    if c_samples:
        try_data_encoding(cf_path, c_samples, "电流")

    # Step 4: 找 header-数据边界
    if p_samples:
        find_data_boundary(pf_path, p_samples, "功率")
    if c_samples:
        find_data_boundary(cf_path, c_samples, "电流")

    print("\n✅ 第5轮分析完成")
