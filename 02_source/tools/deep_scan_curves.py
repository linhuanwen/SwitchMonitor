#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全面扫描2.hbf，找到所有曲线的实际位置，并尝试关联F9记录。
策略：扫描整个文件(不只是0x03000000+)，记录每个曲线的精确位置，
然后看能否和510条F9记录匹配。
"""
import struct, os, sys, json
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def extract_curves_from_region(raw, base_offset, min_len=50, max_len=600, min_peak=0.3, max_peak=10.0):
    """在一个区域中提取所有电流曲线"""
    n_floats = len(raw) // 4
    if n_floats == 0:
        return []
    f32 = struct.unpack_from(f'<{n_floats}f', raw, 0)

    curves = []
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
                        curves.append({
                            'file_offset': base_offset + seg_start * 4,
                            'length': seg_len,
                            'peak': round(peak, 4),
                            'avg': round(sum(abs(v) for v in seg_vals)/seg_len, 4),
                        })
            i += 1
        else:
            i += 1
    return curves

def extract_3277_records(filepath, f9, f6):
    """提取所有0x3277记录"""
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
                'u32_0': u32s[0],
                'u32_1': u32s[1],
                'u32_2': u32s[2],
                'u32_3': u32s[3],
                'u32_4': u32s[4],
                'u32_5': u32s[5],
                'u32_6': u32s[6],
                'u32_7': u32s[7],
                'u64_addr': u64_addr,
                'raw_hex': rec.hex(),
            })
    return records

def main():
    size = os.path.getsize(CURRENT_FILE)
    print(f"文件大小: {size} ({size/1024/1024:.1f} MB)")

    # ── 阶段1: 获取F9记录 ──
    print(f"\n{'='*80}")
    print("阶段1: 获取1-J/1-X的F9 0x3277记录")
    print("="*80)

    # 手动读取目录
    ALL_IDS = ['1-J','1-X']
    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)

    entries = {}
    for sw_id in ALL_IDS:
        pos = data_2mb.find(sw_id.encode('ascii'))
        if pos >= 0:
            block = data_2mb[pos:pos+256]
            fields = struct.unpack_from('<13I', block, 0x70)
            entries[sw_id] = {'F6': fields[6], 'F7': fields[7], 'F9': fields[9]}

    e = entries['1-J']
    records = extract_3277_records(CURRENT_FILE, e['F9'], e['F6'])
    print(f"1-J/1-X 共享F9: 0x{e['F9']:08X}, 0x3277记录数={len(records)}")

    # 显示所有8个u32和u64
    print(f"\n前10条完整记录:")
    for r in records[:10]:
        print(f"  [{r['idx']:>4}] u32[0]=0x{r['u32_0']:08X} u32[1]=0x{r['u32_1']:08X} "
              f"u32[2]=0x{r['u32_2']:08X} u32[3]=0x{r['u32_3']:08X}")
        print(f"         u32[4]=0x{r['u32_4']:08X} u32[5]=0x{r['u32_5']:08X} "
              f"u32[6]=0x{r['u32_6']:08X} u32[7]=0x{r['u32_7']:08X}")
        print(f"         u64_addr=0x{r['u64_addr']:016X}")

    # ── 阶段2: 稀疏扫描整个文件找曲线 ──
    print(f"\n{'='*80}")
    print("阶段2: 稀疏扫描整个文件 (步长64KB)")
    print("="*80)

    all_curves = []
    # 扫描整个文件，不只是后半部分
    for base in range(0, size, 0x10000):
        chunk = read_at(CURRENT_FILE, base, 0x10000)
        nz = sum(1 for b in chunk[:0x8000] if b != 0)
        if nz < 50:
            continue

        curves = extract_curves_from_region(chunk, base)
        all_curves.extend(curves)

    print(f"总共找到 {len(all_curves)} 条曲线")

    if not all_curves:
        print("未找到曲线!")
        return

    all_curves.sort(key=lambda c: c['file_offset'])

    # 显示前10条曲线的偏移范围
    print(f"\n前20条曲线:")
    for c in all_curves[:20]:
        print(f"  0x{c['file_offset']:08X}: len={c['length']}, peak={c['peak']:.2f}A")

    print(f"\n最后10条曲线:")
    for c in all_curves[-10:]:
        print(f"  0x{c['file_offset']:08X}: len={c['length']}, peak={c['peak']:.2f}A")

    # ── 阶段3: 分析曲线之间的间距 ──
    print(f"\n{'='*80}")
    print("阶段3: 曲线间距分析")
    print("="*80)

    # 计算间隔，找规律
    gaps = []
    for i in range(1, len(all_curves)):
        gap = all_curves[i]['file_offset'] - all_curves[i-1]['file_offset']
        gaps.append(gap)

    # 统计间距分布
    from collections import Counter
    # 将间距四舍五入到最近的0x100
    gap_rounded = Counter()
    for g in gaps:
        rounded = (g // 0x100) * 0x100
        gap_rounded[rounded] += 1

    print(f"\n间距分布 (top 20):")
    for gap_val, count in gap_rounded.most_common(20):
        print(f"  ~0x{gap_val:06X} ({gap_val}): {count} 次")

    # ── 阶段4: 3条一组分组 ──
    print(f"\n{'='*80}")
    print("阶段4: 按3条一组分组 (用超短间距 < 0x5000)")
    print("="*80)

    # 短间距(同组内) vs 长间距(组间)
    short_gap = 0x5000  # 20KB

    groups = []
    current_group = [all_curves[0]]
    for i in range(1, len(all_curves)):
        gap = all_curves[i]['file_offset'] - all_curves[i-1]['file_offset']
        if gap < short_gap:
            current_group.append(all_curves[i])
        else:
            groups.append(current_group)
            current_group = [all_curves[i]]
    groups.append(current_group)

    print(f"总组数: {len(groups)}")

    group_sizes = Counter(len(g) for g in groups)
    for size in sorted(group_sizes.keys()):
        print(f"  {size}条/组: {group_sizes[size]} 组")

    # 只保留3条一组
    triple_groups = [g for g in groups if len(g) == 3]
    print(f"\n3条一组(标准三相): {len(triple_groups)} 组")

    # ── 阶段5: 和F9记录数量对比 ──
    print(f"\n{'='*80}")
    print("阶段5: 数量关系分析")
    print("="*80)
    print(f"  F9中的0x3277记录: {len(records)}")
    print(f"  扫描到的曲线总数: {len(all_curves)}")
    print(f"  3条一组: {len(triple_groups)}")
    print(f"  如果每条记录=1事件(3曲线): {len(records)*3} = 预期曲线数")
    print(f"  如果每条记录=1曲线: {len(records)} vs {len(all_curves)}")

    # ── 阶段6: 深度分析：F9 u64_addr vs 曲线实际偏移 ──
    print(f"\n{'='*80}")
    print("阶段6: F9记录的u64_addr与曲线实际偏移的关系")
    print("="*80)

    # u64_addr值看起来很随机，尝试将其作为编码的偏移量来解码
    # 检查: u64_addr & 0xFFFFFFFF 是否等于 u32[2]?
    for r in records[:10]:
        lower32 = r['u64_addr'] & 0xFFFFFFFF
        upper32 = (r['u64_addr'] >> 32) & 0xFFFFFFFF
        match_low = "✓" if lower32 == r['u32_2'] else " "
        match_high = "✓" if upper32 == r['u32_2'] else " "
        print(f"  [{r['idx']:>4}] u64=0x{r['u64_addr']:016X} lower32=0x{lower32:08X} {match_low} upper32=0x{upper32:08X} {match_high}")

    # ── 阶段7: 找曲线数据和F9记录之间的关系 ──
    print(f"\n{'='*80}")
    print("阶段7: u32[0]字段分析")
    print("="*80)

    # u32[0]看起来在递增: 0x0009E700, 0x0009E800, 0x0009E900...
    # 这可能是某种序列号
    u32_0_vals = [r['u32_0'] for r in records]
    print(f"u32[0]范围: 0x{min(u32_0_vals):08X} - 0x{max(u32_0_vals):08X}")
    print(f"u32[0]递增: {all(u32_0_vals[i] < u32_0_vals[i+1] for i in range(len(u32_0_vals)-1))}")

    # u32[1]的值
    u32_1_vals = sorted(set(r['u32_1'] for r in records))
    print(f"u32[1] 唯一值: {[f'0x{v:08X}' for v in u32_1_vals]}")

    # 检查u32[0]的前几个和后几个
    print(f"\nu32[0]前10: {[f'0x{v:08X}' for v in u32_0_vals[:10]]}")
    print(f"u32[0]后10: {[f'0x{v:08X}' for v in u32_0_vals[-10:]]}")

    # ── 阶段8: 用原始字节模式搜索文件中的UTF-16LE头 ──
    print(f"\n{'='*80}")
    print("阶段8: 搜索UTF-16LE 'curve_info' 标签")
    print("="*80)

    # 稀疏扫描找所有 curve_info 标签
    tag = 'curve_info'.encode('utf-16-le')
    tag_positions = []

    for base in range(0, size, 0x100000):
        chunk = read_at(CURRENT_FILE, base, 0x100000 + len(tag))
        pos = 0
        while True:
            found = chunk.find(tag, pos)
            if found == -1:
                break
            tag_positions.append(base + found)
            pos = found + 1

    print(f"找到 {len(tag_positions)} 个 'curve_info' 标签")

    if tag_positions:
        # 显示前10个和间距
        print(f"\n前10个标签位置:")
        for i, pos in enumerate(tag_positions[:10]):
            print(f"  [{i}] 0x{pos:08X}")
            if i > 0:
                gap = pos - tag_positions[i-1]
                print(f"       gap=0x{gap:X} ({gap})")

        if len(tag_positions) > 1:
            print(f"\n最后10个标签位置:")
            for i, pos in enumerate(tag_positions[-10:], len(tag_positions)-10):
                print(f"  [{i}] 0x{pos:08X}")

    print("\n完成!")

if __name__ == '__main__':
    main()
