#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从u32[2]基地址开始，读取更大的数据块，看曲线数据在块内的布局。
同时检查：是否每个u32[2]块包含2条曲线（A+B相），而C相在另一处？
"""
import struct, os, sys
from collections import defaultdict, Counter

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def extract_3277_records(filepath, f9, f6):
    raw = read_at(filepath, f9, f6)
    marker_3277 = b'\x77\x32'
    records = []
    for i in range(f6 // 32):
        rec = raw[i*32:(i+1)*32]
        if rec[13:15] == marker_3277:
            u32s = struct.unpack_from('<8I', rec, 0)
            u64_addr = struct.unpack_from('<Q', rec, 16)[0]
            records.append({
                'idx': i,
                'u32_0': u32s[0], 'u32_1': u32s[1],
                'u32_2': u32s[2], 'u32_3': u32s[3],
                'u64_addr': u64_addr,
            })
    return records

def find_all_float_segments(raw, base_offset, min_len=50, max_len=600, min_peak=0.3, max_peak=10.0):
    """在二进制数据中找所有float32曲线段"""
    n_floats = len(raw) // 4
    if n_floats == 0:
        return []
    f32 = struct.unpack_from(f'<{n_floats}f', raw, 0)

    segments = []
    i = 0
    while i < n_floats:
        if abs(f32[i]) > 0.03:
            seg_start = i
            seg_vals = []
            while i < n_floats and abs(f32[i]) > 0.005:
                seg_vals.append(f32[i])
                i += 1
            seg_len = len(seg_vals)
            if min_len <= seg_len <= max_len:
                peak = max(abs(v) for v in seg_vals)
                if min_peak <= peak <= max_peak:
                    peak_idx = seg_vals.index(max(seg_vals, key=abs))
                    if 5 <= peak_idx <= seg_len - 5:
                        segments.append({
                            'file_offset': base_offset + seg_start * 4,
                            'local_offset': seg_start * 4,
                            'length': seg_len,
                            'peak': round(peak, 4),
                        })
            i += 1
        else:
            i += 1
    return segments

def main():
    # 获取F9记录
    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)
    pos = data_2mb.find(b'1-J')
    block = data_2mb[pos:pos+256]
    fields = struct.unpack_from('<13I', block, 0x70)
    f9, f6 = fields[9], fields[6]

    records = extract_3277_records(CURRENT_FILE, f9, f6)
    print(f"0x3277记录总数: {len(records)}")

    # ── 从几个u32[2]偏移处读取大块数据 ──
    print(f"\n{'='*80}")
    print("阶段1: 从u32[2]基地址读取大块并搜索曲线")
    print("="*80)

    # 取前3个和"重置后"的前3个
    sample_records = [records[0], records[1], records[5], records[6]]
    # records[5] has u32[1]=0x1FD00, u32[2]=0x00000000

    for r in sample_records:
        base = r['u32_2']
        print(f"\n{'─'*60}")
        print(f"记录[{r['idx']}] u32[1]=0x{r['u32_1']:08X} u32[2]=0x{base:08X} u64=0x{r['u64_addr']:016X}")

        # 读取128KB
        chunk = read_at(CURRENT_FILE, base, 0x20000)
        nz = sum(1 for b in chunk if b != 0)
        print(f"  非零字节: {nz}/{len(chunk)}")

        if nz > 100:
            # 显示前256字节
            print(f"  前256字节:")
            for j in range(0, min(256, len(chunk)), 32):
                hex_str = ' '.join(f'{b:02x}' for b in chunk[j:j+32])
                ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in chunk[j:j+32])
                print(f"    +{j:04x}: {hex_str} |{ascii_str}|")

            # 搜索曲线段
            curves = find_all_float_segments(chunk, base)
            if curves:
                print(f"  找到 {len(curves)} 条曲线:")
                for c in curves:
                    print(f"    @ +0x{c['local_offset']:06X} (0x{c['file_offset']:08X}): "
                          f"len={c['length']}, peak={c['peak']:.2f}A")

    # ── 阶段2: 对比 - 使用u64_addr的低32位作为偏移 ──
    print(f"\n\n{'='*80}")
    print("阶段2: 尝试用 u64_addr & 0x1FFFFFFF 等变换作为文件偏移")
    print("="*80)

    for r in records[:10]:
        off1 = r['u64_addr'] & 0x1FFFFFFF  # 低29位
        off2 = r['u64_addr'] & 0x0FFFFFFF  # 低28位
        off3 = r['u64_addr'] % 0x20000000  # mod 512MB
        off4 = r['u32_4']  # D88A8000 etc
        off5 = r['u32_5']  # DE513DDD etc
        print(f"  [{r['idx']:>4}] u32[2]=0x{r['u32_2']:08X} "
              f"low29=0x{off1:08X} low28=0x{off2:08X} mod512M=0x{off3:08X} "
              f"u32[4]=0x{off4:08X} u32[5]=0x{off5:08X}")

    # ── 阶段3: 用实际找到的曲线偏移反推 u64_addr编码 ──
    print(f"\n\n{'='*80}")
    print("阶段3: 广泛扫描+反推F9记录映射")
    print("="*80)

    # 重新扫描全部曲线（确保完整）
    size = os.path.getsize(CURRENT_FILE)
    all_curves = []
    for base in range(0, size, 0x10000):
        chunk = read_at(CURRENT_FILE, base, 0x10000)
        nz = sum(1 for b in chunk[:0x8000] if b != 0)
        if nz < 50:
            continue
        curves = find_all_float_segments(chunk, base)
        all_curves.extend(curves)

    all_curves.sort(key=lambda c: c['file_offset'])
    print(f"曲线总数: {len(all_curves)}")

    # 计算u64_addr的值域和曲线偏移的值域
    u64_lower = [r['u64_addr'] & 0xFFFFFFFF for r in records]
    u64_upper = [(r['u64_addr'] >> 32) & 0xFFFFFFFF for r in records]
    u32_2_vals = [r['u32_2'] for r in records]
    u32_1_vals = [r['u32_1'] for r in records]

    curve_offsets = [c['file_offset'] for c in all_curves]

    print(f"\n值域对比:")
    print(f"  u64_addr lower32:  0x{min(u64_lower):08X} - 0x{max(u64_lower):08X}")
    print(f"  u64_addr upper32:  0x{min(u64_upper):08X} - 0x{max(u64_upper):08X}")
    print(f"  u32[2]:            0x{min(u32_2_vals):08X} - 0x{max(u32_2_vals):08X}")
    print(f"  u32[1]:            0x{min(u32_1_vals):08X} - 0x{max(u32_1_vals):08X}")
    print(f"  曲线偏移:          0x{min(curve_offsets):08X} - 0x{max(curve_offsets):08X}")

    # 找第一个非零u32[2]和第一个曲线偏移的关系
    first_nonzero_u32_2 = next((r['u32_2'] for r in records if r['u32_2'] > 0), 0)
    first_curve_offset = curve_offsets[0]
    print(f"\n  第一个非零u32[2]: 0x{first_nonzero_u32_2:08X}")
    print(f"  第一个曲线偏移:   0x{first_curve_offset:08X}")
    print(f"  差值:             0x{first_curve_offset - first_nonzero_u32_2:08X} = {first_curve_offset - first_nonzero_u32_2}")

    # ── 阶段4: 检查u32[2]差值序列 vs 曲线簇 ──
    print(f"\n\n{'='*80}")
    print("阶段4: 检查u32[2]作为块基地址，块的间隔=0x327700")
    print("="*80)

    # 0x327700 = 3,307,264 bytes
    # u32[2]序列(按排序): 有4段，每段内间隔0x327700
    u32_2_sorted = sorted(set(r['u32_2'] for r in records))
    print(f"u32[2] 唯一值 ({len(u32_2_sorted)}):")
    # 只显示非零值
    non_zero = [v for v in u32_2_sorted if v > 0]
    for i, v in enumerate(non_zero[:15]):
        if i > 0:
            gap = v - non_zero[i-1]
            print(f"  0x{v:08X} (gap=0x{gap:08X})")
        else:
            print(f"  0x{v:08X}")

    # ── 阶段5: 核心假设 - 每条0x3277记录对应2条曲线 ──
    print(f"\n\n{'='*80}")
    print("阶段5: 核心假设验证 - 每条F9记录对应2条曲线(可能只采集了2相)")
    print("="*80)

    n_pairs = sum(1 for g in [1] if True)  # placeholder
    # 实际上：576对 + 70个单独 = 646组，共1222条曲线
    n_pairs_found = 576
    n_singles = 70
    print(f"找到: {n_pairs_found} 对 (2条/组) + {n_singles} 单条")
    print(f"如果每对=1个事件: {n_pairs_found} 事件")
    print(f"F9记录数: {len(records)}")
    print(f"比例: {n_pairs_found}/{len(records)} = {n_pairs_found/len(records):.4f}")

    # 576 + 510 = 1.13 ratio. Not clean.

    # ── 阶段6: 对比曲线间距模式 ──
    print(f"\n\n{'='*80}")
    print("阶段6: 详细间距模式分析")
    print("="*80)

    # 分析曲线长度分布
    lengths = [c['length'] for c in all_curves]
    length_dist = Counter(lengths)
    most_common_lens = length_dist.most_common(10)
    print(f"曲线长度分布 (top10):")
    for l, cnt in most_common_lens:
        print(f"  len={l}: {cnt} 条")

    # 分析峰值分布
    peaks = [c['peak'] for c in all_curves]
    print(f"\n峰值范围: {min(peaks):.2f}A - {max(peaks):.2f}A")

    # ── 阶段7: 尝试3条一组（放宽间距限制） ──
    print(f"\n\n{'='*80}")
    print("阶段7: 尝试3条一组 (间距 < 0xC000)")
    print("="*80)

    # 用更宽松的间距
    for threshold in [0x5000, 0xC000, 0x15000, 0x20000]:
        groups = []
        current_group = [all_curves[0]]
        for i in range(1, len(all_curves)):
            gap = all_curves[i]['file_offset'] - all_curves[i-1]['file_offset']
            if gap < threshold:
                current_group.append(all_curves[i])
            else:
                groups.append(current_group)
                current_group = [all_curves[i]]
        groups.append(current_group)

        size_dist = Counter(len(g) for g in groups)
        n_triples = size_dist.get(3, 0)
        n_pairs = size_dist.get(2, 0)
        n_singles = size_dist.get(1, 0)
        print(f"  threshold=0x{threshold:06X}: groups={len(groups)}, "
              f"1:{n_singles} 2:{n_pairs} 3:{n_triples}")

    print("\n完成!")

if __name__ == '__main__':
    main()
