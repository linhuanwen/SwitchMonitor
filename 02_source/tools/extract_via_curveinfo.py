#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
通过 "curve_info" UTF-16LE标签定位并提取所有电流曲线。
分析每个curve_info后的数据结构，确定每条曲线属于哪个事件。
"""
import struct, os, sys, json
from collections import defaultdict, Counter
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_FILE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\2.hbf"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def main():
    size = os.path.getsize(CURRENT_FILE)

    # ── 阶段1: 找所有curve_info标签位置 ──
    print("阶段1: 定位所有curve_info标签")
    tag = 'curve_info'.encode('utf-16-le')
    tag_positions = []

    for base in range(0, size, 0x100000):  # 1MB步长
        chunk = read_at(CURRENT_FILE, base, 0x100000 + 200)
        if len(chunk) < len(tag):
            break
        pos = 0
        while True:
            found = chunk.find(tag, pos)
            if found == -1:
                break
            tag_positions.append(base + found)
            pos = found + 100

    print(f"找到 {len(tag_positions)} 个 curve_info 标签")

    # 显示间距分布
    gaps = []
    for i in range(1, len(tag_positions)):
        gaps.append(tag_positions[i] - tag_positions[i-1])

    gap_dist = Counter()
    for g in gaps:
        gap_dist[g // 0x1000 * 0x1000] += 1  # 按4KB对齐

    print(f"\n标签间距分布 (按4KB桶):")
    for gap, cnt in gap_dist.most_common(15):
        print(f"  ~0x{gap:08X}: {cnt} 个")

    # ── 阶段2: 在几个curve_info处深入分析数据结构 ──
    print(f"\n{'='*80}")
    print("阶段2: 分析curve_info标签周围的数据结构")
    print("="*80)

    for i in [0, 1, 2, 10, 100, 200]:
        if i >= len(tag_positions):
            break
        tp = tag_positions[i]
        chunk = read_at(CURRENT_FILE, tp - 64, 512)

        print(f"\n[{i}] tag @ 0x{tp:08X}:")
        # 前一部分 (标签前64字节)
        print(f"  标签前64字节:")
        for j in range(0, 64, 32):
            off = tp - 64 + j
            hex_s = ' '.join(f'{b:02x}' for b in chunk[j:j+32])
            asc_s = ''.join(chr(b) if 32 <= b < 127 else '.' for b in chunk[j:j+32])
            print(f"    0x{off:08X}: {hex_s} |{asc_s}|")

        # 标签及后面的部分
        print(f"  标签及后续:")
        for j in range(64, min(512, len(chunk)), 32):
            off = tp - 64 + j
            hex_s = ' '.join(f'{b:02x}' for b in chunk[j:j+32])
            asc_s = ''.join(chr(b) if 32 <= b < 127 else '.' for b in chunk[j:j+32])
            print(f"    0x{off:08X}: {hex_s} |{asc_s}|")

        # 在后续2KB中找float32曲线数据
        search_chunk = read_at(CURRENT_FILE, tp, 0x2000)
        curves_after = []
        n_f = len(search_chunk) // 4
        f32_vals = struct.unpack_from(f'<{n_f}f', search_chunk, 0)
        seg = None
        seg_data = []
        for fi, v in enumerate(f32_vals):
            if abs(v) > 0.02:
                if seg is None:
                    seg = fi
                    seg_data = []
                seg_data.append(v)
            else:
                if seg is not None:
                    if 50 <= len(seg_data) <= 600:
                        peak = max(abs(x) for x in seg_data)
                        if 0.3 <= peak <= 10.0:
                            curves_after.append({
                                'local_off': seg * 4,
                                'file_off': tp + seg * 4,
                                'len': len(seg_data),
                                'peak': round(peak, 3),
                            })
                    seg = None
                    seg_data = []

        if curves_after:
            print(f"  标签后找到 {len(curves_after)} 段曲线数据:")
            for c in curves_after:
                print(f"    @ +0x{c['local_off']:05X} (0x{c['file_off']:08X}): "
                      f"len={c['len']}, peak={c['peak']}A")
        else:
            # 可能标签后是元数据，数据在更远处的固定偏移
            print(f"  标签后2KB内未找到曲线数据（可能在更远处）")

    # ── 阶段3: 精确提取所有曲线 ──
    print(f"\n\n{'='*80}")
    print("阶段3: 从每个curve_info标签处扩大搜索范围提取曲线")
    print("="*80)

    all_extracted = []
    for i, tp in enumerate(tag_positions):
        # 读取标签后16KB
        chunk = read_at(CURRENT_FILE, tp, 0x4000)
        n_f = len(chunk) // 4
        f32_vals = struct.unpack_from(f'<{n_f}f', chunk, 0)

        seg = None
        seg_data = []
        for fi, v in enumerate(f32_vals):
            if abs(v) > 0.02:
                if seg is None:
                    seg = fi
                    seg_data = []
                seg_data.append(v)
            else:
                if seg is not None:
                    if 50 <= len(seg_data) <= 600:
                        peak = max(abs(x) for x in seg_data)
                        if 0.3 <= peak <= 10.0:
                            pi = seg_data.index(max(seg_data, key=abs))
                            if 5 <= pi <= len(seg_data) - 5:
                                all_extracted.append({
                                    'tag_idx': i,
                                    'tag_pos': tp,
                                    'file_off': tp + seg * 4,
                                    'local_off': seg * 4,
                                    'len': len(seg_data),
                                    'peak': round(peak, 4),
                                    'values': [round(v, 6) for v in seg_data],
                                })
                    seg = None
                    seg_data = []

    print(f"从curve_info标签后提取到 {len(all_extracted)} 条曲线")

    if not all_extracted:
        print("未提取到曲线，尝试不同策略...")
        return

    # ── 阶段4: 分析每个标签对应的曲线数 ──
    print(f"\n{'='*80}")
    print("阶段4: 每个curve_info标签对应的曲线数")
    print("="*80)

    curves_per_tag = defaultdict(list)
    for c in all_extracted:
        curves_per_tag[c['tag_idx']].append(c)

    n_curves_per_tag = Counter(len(v) for v in curves_per_tag.values())
    print(f"每个标签的曲线数分布:")
    for n, cnt in sorted(n_curves_per_tag.items()):
        print(f"  {n} 条/标签: {cnt} 个标签")

    # ── 阶段5: 统计全部 ──
    total_tags = len(tag_positions)
    tags_with_curves = len(curves_per_tag)
    tags_without = total_tags - tags_with_curves
    print(f"\n总标签: {total_tags}")
    print(f"有曲线的标签: {tags_with_curves}")
    print(f"无曲线的标签: {tags_without}")

    # ── 阶段6: 显示样本曲线 ──
    print(f"\n{'='*80}")
    print("阶段6: 前5条曲线的样本数据")
    print("="*80)

    for c in all_extracted[:5]:
        print(f"\ntag[{c['tag_idx']}] @ 0x{c['tag_pos']:08X}, 曲线 @ 0x{c['file_off']:08X}:")
        print(f"  len={c['len']}, peak={c['peak']:.3f}A")
        print(f"  first10={c['values'][:10]}")
        print(f"  last10={c['values'][-10:]}")

    # ── 阶段7: 与F9记录关联 ──
    print(f"\n\n{'='*80}")
    print("阶段7: 与F9 0x3277记录关联")
    print("="*80)

    with open(CURRENT_FILE, 'rb') as f:
        data_2mb = f.read(0x200000)
    pos = data_2mb.find(b'1-J')
    block = data_2mb[pos:pos+256]
    fields = struct.unpack_from('<13I', block, 0x70)
    f9, f6 = fields[9], fields[6]

    raw = read_at(CURRENT_FILE, f9, f6)
    marker_3277 = b'\x77\x32'
    n_records = f6 // 32
    records_3277 = []
    for i in range(n_records):
        rec = raw[i*32:(i+1)*32]
        if rec[13:15] == marker_3277:
            u32s = struct.unpack_from('<8I', rec, 0)
            records_3277.append({
                'idx': i,
                'u32_0': u32s[0], 'u32_1': u32s[1],
                'u32_2': u32s[2],
            })

    print(f"F9 0x3277记录: {len(records_3277)}")
    print(f"curve_info标签: {len(tag_positions)}")
    print(f"提取的曲线: {len(all_extracted)}")

    # 各种可能的对应关系
    print(f"\n对应关系分析:")
    print(f"  curve_info / F9记录: {len(tag_positions)}/{len(records_3277)} = {len(tag_positions)/len(records_3277):.2f}")
    print(f"  曲线 / F9记录: {len(all_extracted)}/{len(records_3277)} = {len(all_extracted)/len(records_3277):.2f}")
    print(f"  曲线 / curve_info: {len(all_extracted)}/{len(tag_positions)} = {len(all_extracted)/len(tag_positions):.2f}")

    if tags_with_curves == len(records_3277):
        print(f"\n  ✓ curve_info有曲线的标签数 = F9记录数: {len(records_3277)}")
    elif tags_with_curves * 2 == len(records_3277):
        print(f"\n  ✓ 2×curve_info标签 = F9记录数")

    # ── 阶段8: 构建输出 ──
    print(f"\n\n{'='*80}")
    print("阶段8: 按标签组织输出")
    print("="*80)

    # 按tag_idx组织
    organized = {}
    for tag_idx in sorted(curves_per_tag.keys()):
        tag_curves = curves_per_tag[tag_idx]
        organized[tag_idx] = {
            'tag_pos': tag_positions[tag_idx],
            'n_curves': len(tag_curves),
            'curves': [{
                'peak': c['peak'],
                'len': c['len'],
                'values': c['values'],
            } for c in tag_curves],
        }

    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_via_curveinfo.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump({
            'meta': {
                'source': CURRENT_FILE,
                'total_tags': total_tags,
                'tags_with_data': tags_with_curves,
                'total_curves': len(all_extracted),
                'f9_records': len(records_3277),
            },
            'organized': organized,
        }, f, ensure_ascii=False)

    size_mb = os.path.getsize(out_path) / 1024 / 1024
    print(f"保存到: {out_path} ({size_mb:.1f} MB)")
    print("\n完成!")

if __name__ == '__main__':
    main()
