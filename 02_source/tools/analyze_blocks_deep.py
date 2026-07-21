#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
关键洞察：非零块中索引记录只占一小部分（131×32B=4KB, 但块有160KB+）
采样数据应该在索引记录之后。
读取索引记录之后的数据，尝试各种解码方式。
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"
CURRENT_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\1.hbf"

# 从之前的扫描得知的关键非零区域偏移
# 功率1: 0x1c9c0c, 0x249c0c, 0x189c0c, 0x209c0c, 0x349c0c...
# 功率2: 0x0c9c0c, 0x149c0c, 0x109c0c, 0x089c0c, 0x1c9c0c...
# 电流1: 0x16e700c, 0x152700c, ...

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_markers(data, marker=b'\x27\x12\x00\x00'):
    """找所有标记位置"""
    positions = []
    pos = 0
    while True:
        p = data.find(marker, pos)
        if p == -1:
            break
        positions.append(p)
        pos = p + 1
    return positions

def try_decode_samples(raw, sample_bytes, fmt, label, max_samples=500):
    """尝试解码raw数据为采样值"""
    n = min(max_samples, len(raw) // sample_bytes)
    if n < 20:
        return None

    try:
        if fmt == 'f32_le':
            vals = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(n)]
        elif fmt == 'f32_be':
            vals = [struct.unpack('>f', raw[i*4:(i+1)*4])[0] for i in range(n)]
        elif fmt == 'i16_le':
            vals = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(n)]
        elif fmt == 'u16_le':
            vals = [struct.unpack('<H', raw[i*2:(i+1)*2])[0] for i in range(n)]
        elif fmt == 'i32_le':
            vals = [struct.unpack('<i', raw[i*4:(i+1)*4])[0] for i in range(n)]
        elif fmt == 'u32_le':
            vals = [struct.unpack('<I', raw[i*4:(i+1)*4])[0] for i in range(n)]
        else:
            return None
    except:
        return None

    # 过滤无效值
    valid = [v for v in vals if abs(v) < 1e8]
    if len(valid) < 20:
        return None

    # 评分：功率曲线特征
    peak = max(valid)
    if peak < 0.3 or peak > 30:
        return None

    # 开头接近0
    head = valid[:max(3, len(valid)//10)]
    if sum(1 for v in head if abs(v) < 0.15) < len(head) * 0.4:
        return None

    # 有稳态段
    peak_idx = valid.index(peak)
    tail_start = peak_idx + len(valid) // 5
    if tail_start < len(valid):
        tail = valid[tail_start:]
        tail_ok = sum(1 for v in tail if 0.02 < v < 1.5)
        if len(tail) > 0 and tail_ok / len(tail) > 0.2:
            return {
                'vals': valid[:300],
                'peak': peak,
                'peak_idx': peak_idx,
                'n': len(valid),
                'fmt': fmt,
                'label': label,
            }
    return None

def analyze_block(filepath, block_offset, label=""):
    """分析一个非零块：读取索引记录，然后分析后面的数据"""
    # 读取足够大的区域
    raw = read_at(filepath, block_offset, 262144)  # 256KB

    # 找到所有0x1227标记
    marker = b'\x27\x12\x00\x00'
    alt_marker = b'\x77\x32\x00\x00'
    markers = find_markers(raw, marker)
    if not markers:
        markers = find_markers(raw, alt_marker)
        marker = alt_marker

    if len(markers) < 2:
        return None

    print(f"\n{'='*60}")
    print(f"块 @ 0x{block_offset:x} ({label}): {len(markers)} 个标记")
    print(f"{'='*60}")

    # 解析前几个索引记录
    print(f"\n前5条索引记录:")
    for i in range(min(5, len(markers))):
        mp = markers[i]
        rec_start = mp
        if rec_start + 32 > len(raw):
            continue
        rec = raw[rec_start:rec_start+32]
        u32s = struct.unpack('<8I', rec)
        print(f"  [{i}] @ +{mp:5d} (0x{mp:04x}): "
              f"u32=0x{u32s[0]:08x} 0x{u32s[1]:08x} 0x{u32s[2]:08x} 0x{u32s[3]:08x} "
              f"0x{u32s[4]:08x} 0x{u32s[5]:08x} 0x{u32s[6]:08x} 0x{u32s[7]:08x}")

    # 最后一个标记之后的数据
    last_marker = markers[-1]
    data_start = last_marker + 32

    if data_start >= len(raw):
        print("❌ 标记后无数据")
        return None

    # 跳过尾部的索引记录区
    # 标记之间的gap可能也包含数据
    # 先检查标记之间是否有大gap
    gaps = []
    for i in range(len(markers) - 1):
        gap = markers[i+1] - (markers[i] + 32)
        if gap > 100:
            gaps.append((markers[i] + 32, gap))

    # 也检查最后一个标记之后
    tail_size = len(raw) - data_start
    if tail_size > 100:
        gaps.append((data_start, tail_size))

    # 检查标记之前
    head_gap = markers[0]
    if head_gap > 100:
        gaps.append((0, head_gap))

    if not gaps:
        # 没有大gap — 标记之间紧密排列(32B间隔)
        # 数据可能在标记记录内部，或者数据区完全为空
        print(f"\n无大gap: 标记间距分析:")
        spacings = [markers[i+1] - markers[i] for i in range(min(len(markers)-1, 20))]
        print(f"  间距: min={min(spacings)} max={max(spacings)} "
              f"unique={sorted(set(spacings))[:10]}...")
        return None

    print(f"\n发现 {len(gaps)} 个大数据gap:")
    for gap_start, gap_size in sorted(gaps):
        if gap_size < 100:
            continue
        abs_gap = block_offset + gap_start
        print(f"\n  Gap @ +{gap_start} (0x{gap_start:x}), 绝对值 0x{abs_gap:x}, {gap_size:,} 字节")

        # 读取gap数据
        gap_data = raw[gap_start:gap_start + min(gap_size, 65536)]

        # 统计
        nz = sum(1 for b in gap_data[:4096] if b != 0)
        print(f"  前4KB非零: {nz}/4096 ({100*nz/4096:.1f}%)")

        if nz < 50:
            print(f"  → 几乎全零，跳过")
            continue

        # 尝试32字节对齐的struct解码
        best = None
        best_score = 0

        # 尝试各种步长和格式
        for stride, fmt, bytes_per in [(4, 'f32_le', 4), (2, 'i16_le', 2),
                                        (2, 'u16_le', 2), (4, 'i32_le', 4)]:
            for offset in range(min(stride, 48)):
                sub = gap_data[offset:]
                if len(sub) < 100:
                    continue

                slabel = f'{fmt}@{offset}'
                result = try_decode_samples(sub, bytes_per, fmt, slabel)
                if result and result['peak'] > best_score:
                    # 额外检查稳态
                    if result['peak_idx'] < len(result['vals']) - 10:
                        best = result
                        best_score = result['peak']

        if best:
            print(f"  ✅ 找到可能的曲线! fmt={best['label']}")
            print(f"     peak={best['peak']:.3f} @ idx={best['peak_idx']}/{best['n']}")
            print(f"     前50: {[round(v,3) for v in best['vals'][:50]]}")
            if best['peak_idx'] > 50:
                print(f"     峰附近: {[round(v,3) for v in best['vals'][best['peak_idx']-5:best['peak_idx']+15]]}")
            print(f"     后30: {[round(v,3) for v in best['vals'][-30:]]}")
        else:
            # 全部失败，dump hex
            print(f"  ❌ 无匹配的功率曲线")
            nz_first = sum(1 for b in gap_data[:256] if b != 0)
            if nz_first > 20:
                print(f"  前256字节hex:")
                for i in range(0, min(256, len(gap_data)), 16):
                    line = gap_data[i:i+16]
                    hex_str = ' '.join(f'{b:02x}' for b in line)
                    ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in line)
                    print(f"    {i:4d}: {hex_str}  {ascii_str}")

                # 尝试float32解码前100个值
                f32_test = [struct.unpack('<f', gap_data[j*4:(j+1)*4])[0]
                           for j in range(min(100, len(gap_data)//4))]
                valid_f32 = [v for v in f32_test if abs(v) < 1e6]
                print(f"   float32测试: {[round(v,3) for v in valid_f32[:40]]}")

    return gaps

def main():
    print("HBF 数据块深入分析 — 索引记录之后找采样数据")
    print("=" * 70)

    # 分析功率1.hbf中的几个块
    for offset, label in [
        (0x1c9c0c, "power1/zone1"),
        (0x249c0c, "power1/zone2"),
        (0x349c0c, "power1/zone3"),
    ]:
        try:
            analyze_block(POWER_1, offset, label)
        except Exception as e:
            print(f"\n❌ 块 0x{offset:x} 分析出错: {e}")

    # 分析功率2.hbf中的块
    for offset, label in [
        (0x0c9c0c, "power2/zone1"),
        (0x149c0c, "power2/zone2"),
    ]:
        try:
            analyze_block(POWER_2, offset, label)
        except Exception as e:
            print(f"\n❌ 块 0x{offset:x} 分析出错: {e}")

    # 电流文件
    for offset, label in [
        (0x16e700c, "current1/zone1"),
    ]:
        try:
            analyze_block(CURRENT_1, offset, label)
        except Exception as e:
            print(f"\n❌ 块 0x{offset:x} 分析出错: {e}")

    print("\n✅ 分析完成")

if __name__ == '__main__':
    main()
