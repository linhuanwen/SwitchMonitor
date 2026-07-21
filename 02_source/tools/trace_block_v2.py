#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
深入分析F9记录中的各个字段，寻找曲线数据的实际文件偏移。
重点检查:
1. u32[6] - 看起来在递增，可能是文件偏移
2. u32[1] - 可能是"段号"或高32位地址
3. 有数据的u32[2]块内，曲线数据的具体位置
4. "hhcsmfzzj"文本标记的含义
"""
import struct, os, sys
from collections import Counter

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def extract_all_records(filepath, f9, f6):
    """提取所有0x3277记录的完整字段"""
    raw = read_at(filepath, f9, f6)
    marker_3277 = b'\x77\x32'
    records = []
    for i in range(f6 // 32):
        rec = raw[i*32:(i+1)*32]
        if rec[13:15] == marker_3277:
            u32s = struct.unpack_from('<8I', rec, 0)
            records.append({
                'idx': i,
                'u32': list(u32s),
                'u64': struct.unpack_from('<Q', rec, 16)[0],
                'raw': rec.hex(),
            })
    return records

def find_curves(raw, base_offset):
    """在raw中找曲线段"""
    n_f = len(raw) // 4
    if n_f == 0:
        return []
    f32 = struct.unpack_from(f'<{n_f}f', raw, 0)
    curves = []
    i = 0
    while i < n_f:
        if abs(f32[i]) > 0.03:
            s = i
            vals = []
            while i < n_f and abs(f32[i]) > 0.005:
                vals.append(f32[i])
                i += 1
            if 50 <= len(vals) <= 600:
                peak = max(abs(v) for v in vals)
                if 0.3 <= peak <= 10.0:
                    pi = vals.index(max(vals, key=abs))
                    if 5 <= pi <= len(vals) - 5:
                        curves.append({
                            'offset': base_offset + s * 4,
                            'local_off': s * 4,
                            'len': len(vals),
                            'peak': round(peak, 3),
                            'first5': [round(v, 3) for v in vals[:5]],
                        })
            i += 1
        else:
            i += 1
    return curves

def main():
    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)
    pos = data_2mb.find(b'1-J')
    block = data_2mb[pos:pos+256]
    fields = struct.unpack_from('<13I', block, 0x70)
    f9, f6 = fields[9], fields[6]

    records = extract_all_records(CURRENT_FILE, f9, f6)
    print(f"0x3277记录: {len(records)}")
    size = os.path.getsize(CURRENT_FILE)

    # ── 阶段1: 检查u32[6]是否为文件偏移 ──
    print(f"\n{'='*80}")
    print("阶段1: u32[6]字段分析（增序，可能是文件偏移）")
    print("="*80)

    u32_6 = [r['u32'][6] for r in records]
    print(f"u32[6]范围: 0x{min(u32_6):08X} - 0x{max(u32_6):08X}")
    print(f"严格递增: {all(u32_6[i] < u32_6[i+1] for i in range(len(u32_6)-1))}")

    # 前10个和后10个
    for i, r in enumerate(records[:10]):
        print(f"  [{r['idx']:>4}] u32[6]=0x{r['u32'][6]:08X} "
              f"u32[2]=0x{r['u32'][2]:08X} u32[3]=0x{r['u32'][3]:08X}")

    print(f"  ...")
    for i, r in enumerate(records[-5:], len(records)-5):
        print(f"  [{r['idx']:>4}] u32[6]=0x{r['u32'][6]:08X} "
              f"u32[2]=0x{r['u32'][2]:08X}")

    # 尝试在u32[6]偏移读数据
    print(f"\n尝试在u32[6]偏移处读数据:")
    for r in records[:5]:
        off = r['u32'][6]
        if off < size:
            chunk = read_at(CURRENT_FILE, off, 512)
            nz = sum(1 for b in chunk if b != 0)
            has_data = nz > 50
            print(f"  u32[6]=0x{off:08X}: nz={nz}, data={'YES' if has_data else 'no'}")

    # ── 阶段2: 找"hhcsmfzzj"标记 ──
    print(f"\n\n{'='*80}")
    print("阶段2: 搜索 'hhcsmfzzj' 标记（可能在数据块头部）")
    print("="*80)

    marker = b'hhcsmfzzj'
    marker_positions = []
    for base in range(0, size, 0x100000):
        chunk = read_at(CURRENT_FILE, base, 0x100000 + len(marker))
        pos = 0
        while True:
            found = chunk.find(marker, pos)
            if found == -1:
                break
            marker_positions.append(base + found)
            pos = found + 1

    print(f"找到 {len(marker_positions)} 个 'hhcsmfzzj' 位置")
    for i, mp in enumerate(marker_positions[:10]):
        print(f"  [{i}] 0x{mp:08X}")
        if i > 0:
            print(f"      gap=0x{mp - marker_positions[i-1]:X}")

    # ── 阶段3: 从"hhcsmfzzj"标记处读取完整数据块 ──
    if marker_positions:
        print(f"\n\n{'='*80}")
        print("阶段3: 从 'hhcsmfzzj' 处读数据块结构")
        print("="*80)

        for i, mp in enumerate(marker_positions[:3]):
            print(f"\n块[{i}] @ 0x{mp:08X}:")
            chunk = read_at(CURRENT_FILE, mp, 0x2000)
            # 前256字节
            print(f"  前256字节:")
            for j in range(0, min(256, len(chunk)), 32):
                hex_s = ' '.join(f'{b:02x}' for b in chunk[j:j+32])
                asc_s = ''.join(chr(b) if 32 <= b < 127 else '.' for b in chunk[j:j+32])
                print(f"    +{j:04x}: {hex_s} |{asc_s}|")

            # 在该块中找曲线
            curves = find_curves(chunk, mp)
            if curves:
                print(f"  块内曲线数据 ({len(curves)} 条):")
                for c in curves:
                    print(f"    @ +0x{c['local_off']:05X}: len={c['len']}, "
                          f"peak={c['peak']}A, first5={c['first5']}")
            else:
                print(f"  块内未找到曲线数据")

    # ── 阶段4: u32[1] × 0x327700 + u32[2] = 文件偏移? ──
    print(f"\n\n{'='*80}")
    print("阶段4: 尝试 u32[1]和u32[2]的各种组合作为文件偏移")
    print("="*80)

    # 检查 u32[1] 是 "段号" 的假设
    for r in records[:10]:
        combined1 = r['u32'][1] * 0x327700 + r['u32'][2]  # u32[1] × 块大小
        combined2 = r['u32'][1] * 0x1000 + r['u32'][2]
        combined3 = r['u32'][1] * 0x10000 + r['u32'][2]
        combined4 = (r['u32'][1] << 16) + (r['u32'][2] >> 16)
        print(f"  [{r['idx']:>4}] u32[1]=0x{r['u32'][1]:08X} u32[2]=0x{r['u32'][2]:08X} "
              f"→×327700=0x{combined1:012X} ×1000=0x{combined2:012X} ×10000=0x{combined3:012X}")

    # ── 阶段5: 全面比较 ──
    print(f"\n\n{'='*80}")
    print("阶段5: F9记录的关键字段 vs 实际曲线数据")
    print("="*80)

    # 获取所有曲线
    all_curves = []
    for base in range(0, size, 0x10000):
        chunk = read_at(CURRENT_FILE, base, 0x10000)
        nz = sum(1 for b in chunk[:0x8000] if b != 0)
        if nz < 50:
            continue
        curves = find_curves(chunk, base)
        all_curves.extend(curves)

    all_curves.sort(key=lambda c: c['offset'])
    print(f"曲线总数: {len(all_curves)}")

    # 曲线的实际偏移 vs F9记录的字段
    curve_offs = [c['offset'] for c in all_curves]
    print(f"曲线偏移: 0x{curve_offs[0]:08X} - 0x{curve_offs[-1]:08X}")

    # 检查: 曲线总数 = F9记录数 × 某个倍数?
    for mult in [2, 3]:
        print(f"  记录数×{mult}: {len(records)*mult} vs {len(all_curves)} "
              f"({'匹配!' if len(records)*mult == len(all_curves) else f'差{len(all_curves) - len(records)*mult}'})")

    # ── 阶段6: 查看数据块的hex（找结构） ──
    print(f"\n\n{'='*80}")
    print("阶段6: 搜索 'curve_info' 或 'Current' 等UTF-16LE标签")
    print("="*80)

    for tag_str in ['curve_info', 'Current', 'current', 'Curve', 'Phase', 'DATA']:
        tag = tag_str.encode('utf-16-le')
        count = 0
        first_pos = None
        for base in range(0, size, 0x100000):
            chunk = read_at(CURRENT_FILE, base, 0x100000 + 100)
            pos = chunk.find(tag)
            if pos != -1:
                count += 1
                if first_pos is None:
                    first_pos = base + pos
        if count > 0:
            print(f"  '{tag_str}': {count} 处, 首处 @ 0x{first_pos:08X}")
        else:
            print(f"  '{tag_str}': 未找到")

    # ── 阶段7: 检查 u32[0] = 曲线序号? ──
    print(f"\n\n{'='*80}")
    print("阶段7: u32[0]字段（递增计数器）")
    print("="*80)

    u0 = [r['u32'][0] for r in records]
    print(f"u32[0]: {len(u0)} 个值, "
          f"范围 0x{min(u0):08X}-0x{max(u0):08X}, "
          f"差值={max(u0)-min(u0)}")

    # u0差值 vs 记录数
    expected = len(records) - 1
    actual = max(u0) - min(u0)
    print(f"u32[0]差值: {actual} (预期 {expected} if contiguous)")
    print(f"每个u32[0]可能跳过: {actual - expected} 个(如果非连续记录)")

    # 检查u0是否有gap
    u0_sorted = sorted(u0)
    gaps_gt_1 = sum(1 for i in range(1, len(u0_sorted)) if u0_sorted[i] - u0_sorted[i-1] > 1)
    print(f"gap>1: {gaps_gt_1} 处")

    print("\n完成!")

if __name__ == '__main__':
    main()
