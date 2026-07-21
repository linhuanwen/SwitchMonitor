#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 分析第4轮 — 从数据特征反推结构
思路：在 HBF 文件中搜索已知的道岔功率曲线特征 (0→spike→steady→0)
找到数据后再反推 header 结构
"""
import struct
import sys
import os
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

def read_chunk(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def detect_curve_pattern(values, threshold_spike=1.5, threshold_steady_min=0.05, threshold_steady_max=1.0):
    """检测是否匹配道岔功率曲线: 开头零→尖峰→平稳→结尾零"""
    if len(values) < 50:
        return False, {}

    # 前N个接近零 (启动前)
    leading_zeros = sum(1 for v in values[:8] if abs(v) < 0.02)

    # 找尖峰 (启动电流冲击)
    spike_idx = -1
    spike_val = 0
    for i in range(5, min(40, len(values))):
        if values[i] > threshold_spike and values[i] > spike_val:
            spike_val = values[i]
            spike_idx = i

    # 平稳段 (转换过程)
    steady_start = spike_idx + 15 if spike_idx > 0 else 30
    steady_end = min(len(values) - 15, len(values))
    steady = [v for v in values[steady_start:steady_end]
              if threshold_steady_min < v < threshold_steady_max]

    # 尾部零 (动作结束)
    trailing_zeros = sum(1 for v in values[-10:] if abs(v) < 0.02)

    score = 0
    if leading_zeros >= 4: score += 1
    if spike_idx > 0: score += 2
    if len(steady) >= 10: score += 2
    if trailing_zeros >= 3: score += 1

    return score >= 4, {
        'leading_zeros': leading_zeros,
        'spike_idx': spike_idx,
        'spike_val': spike_val,
        'steady_count': len(steady),
        'trailing_zeros': trailing_zeros,
        'score': score,
        'total_len': len(values),
    }

def search_curves_in_data(filepath, label):
    """在整个文件中搜索道岔功率曲线特征"""
    print(f"\n{'='*60}")
    print(f"曲线特征搜索: {os.path.basename(filepath)} ({label})")

    file_size = os.path.getsize(filepath)

    # 策略: 在数据密集区搜索
    # 每1MB采样64KB，先用float32扫描
    SAMPLE_GAP = 0x100000  # 1MB
    SAMPLE_SIZE = 0x10000  # 64KB

    candidates = []

    for seg_start in range(0, file_size, SAMPLE_GAP):
        chunk = read_chunk(filepath, seg_start, SAMPLE_SIZE)

        # float32 滑动窗口搜索
        for byte_off in range(0, len(chunk) - 400, 4):  # step by 4 for float32
            # 先检查前8个样本是否接近零
            leading = struct.unpack('<8f', chunk[byte_off:byte_off+32])
            if sum(1 for v in leading if abs(v) < 0.02) < 4:
                continue

            # 读更多样本
            sample_count = min(400, (len(chunk) - byte_off) // 4)
            vals = []
            for i in range(sample_count):
                f = struct.unpack('<f', chunk[byte_off+i*4:byte_off+i*4+4])[0]
                vals.append(f)

            is_curve, info = detect_curve_pattern(vals)
            if is_curve:
                abs_offset = seg_start + byte_off
                candidates.append((abs_offset, info, vals[:40]))
                break  # 每个段只取第一个匹配

    print(f"  找到 {len(candidates)} 个候选曲线位置")

    if candidates:
        print(f"\n匹配位置详情:")
        for off, info, preview in candidates[:10]:
            print(f"  0x{off:09x}: spike@{info['spike_idx']}={info['spike_val']:.2f} "
                  f"steady={info['steady_count']} leading0={info['leading_zeros']} "
                  f"trailing0={info['trailing_zeros']} total={info['total_len']}")
            print(f"    前20: {[round(v,3) for v in preview[:20]]}")

    return candidates

def analyze_candidate_context(filepath, candidates, label):
    """分析候选位置周围的数据，反推记录结构"""
    print(f"\n{'='*60}")
    print(f"反推记录结构: {os.path.basename(filepath)} ({label})")

    if not candidates:
        print("无候选位置!")
        return

    file_size = os.path.getsize(filepath)

    for off, info, _ in candidates[:5]:
        # 往前读取256字节，寻找记录头
        ctx_start = max(0, off - 256)
        ctx = read_chunk(filepath, ctx_start, 512)

        print(f"\n  数据位置: 0x{off:09x}")
        print(f"  前256字节 (从 0x{ctx_start:09x}):")
        for i in range(0, min(256, off - ctx_start + 32), 32):
            abs_i = ctx_start + i
            line = ctx[i:i+32]
            hex_s = ' '.join(f'{b:02x}' for b in line)

            # 标记时间戳
            for j in range(0, 28, 4):
                v = struct.unpack('<I', line[j:j+4])[0]
                if 1_780_000_000 < v < 1_790_000_000:
                    hex_s += f"  ←TS:{datetime.fromtimestamp(v).strftime('%m-%d %H:%M')}"

            marker = ""
            if abs_i == off:
                marker = " ← DATA_START"
            print(f"    0x{abs_i:09x}: {hex_s}{marker}")

        # 分析: 记录头距离数据有多远?
        # 查找最近的 uint32 时间戳
        nearest_ts_offset = None
        nearest_ts_dist = 999999
        for i in range(0, off - ctx_start - 4, 4):
            if i >= len(ctx) - 4:
                break
            v = struct.unpack('<I', ctx[i:i+4])[0]
            if 1_780_000_000 < v < 1_790_000_000:
                dist = off - (ctx_start + i)
                if 0 < dist < nearest_ts_dist:
                    nearest_ts_dist = dist
                    nearest_ts_offset = ctx_start + i

        if nearest_ts_offset:
            print(f"  最近时间戳: 0x{nearest_ts_offset:09x} (距离数据 {nearest_ts_dist} bytes)")

            # 读取时间戳周围的32字节，分析header
            hdr_pos = nearest_ts_offset - 16  # 假设时间戳在header中offset 16
            if hdr_pos >= 0:
                hdr = read_chunk(filepath, hdr_pos, 32)
                print(f"  假设header @ 0x{hdr_pos:09x}:")
                for j in range(0, 32, 16):
                    sub = hdr[j:j+16]
                    print(f"    +{j:2d}: {' '.join(f'{b:02x}' for b in sub)}")
                # uint32解析
                for j in range(0, 32, 4):
                    v = struct.unpack('<I', hdr[j:j+4])[0]
                    desc = ""
                    if 1_780_000_000 < v < 1_790_000_000:
                        desc = f" ← TS: {datetime.fromtimestamp(v)}"
                    elif 50 < v < 2000:
                        desc = f" ← 可能是采样点数={v}"
                    elif v < 100:
                        desc = f" ← 可能是道岔号={v}"
                    print(f"    [{j:2d}] {v:12d} (0x{v:08x}){desc}")

        # 检查数据后的情况
        # 曲线通常300样本 * 4B = 1200B, 找结尾
        data_end = off + info['total_len'] * 4
        after = read_chunk(filepath, data_end - 32, 64)
        print(f"\n  数据尾部 (0x{data_end-32:09x}):")
        for i in range(0, 64, 32):
            line = after[i:i+32]
            print(f"    {' '.join(f'{b:02x}' for b in line)}")

        break  # 只要第一个

def verify_header_structure(filepath, candidates):
    """如果找到候选曲线，尝试完整解析记录"""
    print(f"\n{'='*60}")
    print(f"验证记录结构: {os.path.basename(filepath)}")

    if len(candidates) < 2:
        print("候选不足")
        return

    # 取两个相邻候选，计算间距
    off1 = candidates[0][0]
    off2 = candidates[1][0] if len(candidates) > 1 else off1 + 1000

    print(f"  候选间距: {off2 - off1} bytes (= {(off2-off1)/4:.0f} float32 samples)")

    # 取第一个候选的完整曲线数据
    off = candidates[0][0]
    info = candidates[0][1]

    # 搜索该位置之前可能的header
    ctx = read_chunk(filepath, max(0, off - 64), 64)
    print(f"\n  数据起始处原始字节:")
    for i in range(0, 64, 16):
        print(f"    +{i:2d}: {' '.join(f'{b:02x}' for b in ctx[i:i+16])}")

    # float32验证
    sample_count = info.get('total_len', 300)
    raw_data = read_chunk(filepath, off, sample_count * 4)
    floats = [struct.unpack('<f', raw_data[i*4:(i+1)*4])[0] for i in range(sample_count)]
    print(f"\n  完整曲线 float32 (前{min(50, sample_count)}个 + 后20个):")
    print(f"  前: {[round(f,3) for f in floats[:50]]}")
    print(f"  后: {[round(f,3) for f in floats[-20:]]}")

if __name__ == '__main__':
    pf_path = os.path.join(HBF_POWER_DIR, '1.hbf')
    cf_path = os.path.join(HBF_CURRENT_DIR, '1.hbf')

    print("HBF 曲线特征搜索 + 结构反推")
    print("="*60)

    # Step 1: 在功率文件中搜索道岔功率曲线特征
    power_candidates = search_curves_in_data(pf_path, "功率")
    current_candidates = search_curves_in_data(cf_path, "电流")

    # Step 2: 分析候选位置周围的结构
    analyze_candidate_context(pf_path, power_candidates, "功率")
    analyze_candidate_context(cf_path, current_candidates, "电流")

    # Step 3: 完整验证
    if power_candidates:
        verify_header_structure(pf_path, power_candidates)
    elif current_candidates:
        verify_header_structure(cf_path, current_candidates)

    print("\n✅ 第4轮分析完成")
