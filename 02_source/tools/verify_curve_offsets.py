#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
验证电流HBF的0x3277记录中的u32[2]是否是曲线数据的文件偏移量。
同时检查所有3个current HBF文件中哪些开关有实际F9数据。
"""
import struct, os, sys, json
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu"
CURRENT_DIR = os.path.join(BASE, "道岔动作电流曲线")

ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def get_all_dir_entries(filepath):
    with open(filepath, 'rb') as f:
        data = f.read(0x200000)
    entries = {}
    for sw_id in ALL_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        fields = struct.unpack_from('<13I', block, 0x70)
        entries[sw_id] = {
            'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
            'F6': fields[6], 'F7': fields[7], 'F9': fields[9],
        }
    return entries

def check_f9_nonzero(filepath, entries):
    """检查哪些开关的F9区域有非零数据"""
    results = {}
    for sw_id, e in entries.items():
        f9, f6 = e['F9'], e['F6']
        if f9 == 0 or f6 < 32:
            results[sw_id] = 0
            continue
        raw = read_at(filepath, f9, min(f6, 0x20000))
        # 检查是否有非零的32B记录
        marker_3277 = b'\x77\x32'
        count = 0
        for i in range(f6 // 32):
            rec = raw[i*32:(i+1)*32]
            if rec[13:15] == marker_3277:
                count += 1
        results[sw_id] = count
    return results

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
                'u32_2': u32s[2],  # possible file offset
                'u32_3': u32s[3],
                'u64_addr': u64_addr,
            })
    return records

def find_curves_at_offset(filepath, offset, search_range=0x4000):
    """在给定偏移附近搜索曲线数据（UTF-16LE头 + float32采样）"""
    raw = read_at(filepath, offset, search_range)
    if len(raw) < 100:
        return None

    # 搜索 "curve_info" UTF-16LE 标签
    tag = 'curve_info'.encode('utf-16-le')
    tag_pos = raw.find(tag)
    if tag_pos == -1:
        # 尝试直接找float32曲线段
        n_floats = len(raw) // 4
        f32 = struct.unpack_from(f'<{n_floats}f', raw, 0)
        curves = []
        in_seg = False
        seg_start = 0
        seg_vals = []
        for i, v in enumerate(f32):
            if abs(v) > 0.02:
                if not in_seg:
                    seg_start = i
                    seg_vals = []
                    in_seg = True
                seg_vals.append(v)
            else:
                if in_seg:
                    if 50 <= len(seg_vals) <= 600:
                        peak = max(abs(x) for x in seg_vals)
                        if 0.3 <= peak <= 10.0:
                            curves.append({
                                'local_offset': seg_start * 4,
                                'file_offset': offset + seg_start * 4,
                                'length': len(seg_vals),
                                'peak': round(peak, 4),
                                'first5': [round(x, 4) for x in seg_vals[:5]],
                                'last5': [round(x, 4) for x in seg_vals[-5:]],
                            })
                    in_seg = False
        return curves if curves else None

    # 有UTF-16LE标签，跳过元数据找float32
    search_start = tag_pos + len(tag)
    curve_start = None
    for i in range(search_start, min(search_start + 0x400, len(raw) - 4), 4):
        val = struct.unpack_from('<f', raw, i)[0]
        if 0.1 < val < 10.0:
            next_vals = []
            for j in range(1, 10):
                if i + j*4 + 4 <= len(raw):
                    next_vals.append(struct.unpack_from('<f', raw, i + j*4)[0])
            reasonable = sum(1 for v2 in next_vals if 0.03 < abs(v2) < 10.0)
            if reasonable >= 3:
                curve_start = i
                break

    if curve_start is None:
        return None

    n_remaining = (len(raw) - curve_start) // 4
    f32 = struct.unpack_from(f'<{n_remaining}f', raw, curve_start)
    seg_vals = []
    for v in f32:
        if abs(v) > 0.02:
            seg_vals.append(v)
        else:
            break

    if 50 <= len(seg_vals) <= 600:
        peak = max(abs(x) for x in seg_vals)
        if 0.3 <= peak <= 10.0:
            return [{
                'local_offset': curve_start,
                'file_offset': offset + curve_start,
                'length': len(seg_vals),
                'peak': round(peak, 4),
                'first5': [round(x, 4) for x in seg_vals[:5]],
                'last5': [round(x, 4) for x in seg_vals[-5:]],
            }]
    return None


def main():
    # ── 阶段1: 检查所有3个current HBF文件 ──
    print("=" * 80)
    print("阶段1: 检查所有3个current HBF文件中哪些开关有实际F9数据")
    print("=" * 80)

    for hbf_name in ['1.hbf', '2.hbf', '3.hbf']:
        filepath = os.path.join(CURRENT_DIR, hbf_name)
        if not os.path.exists(filepath):
            print(f"\n{hbf_name}: 不存在")
            continue

        entries = get_all_dir_entries(filepath)
        f9_data = check_f9_nonzero(filepath, entries)

        active = {sw: c for sw, c in f9_data.items() if c > 0}
        print(f"\n{hbf_name}:")
        print(f"  总开关数: {len(entries)}")
        print(f"  有F9数据的开关: {len(active)}")
        for sw, count in sorted(active.items(), key=lambda x: (int(x[0].split('-')[0]), x[0].split('-')[1])):
            print(f"    {sw}: {count} 条0x3277记录 (F7={entries[sw]['F7']})")

    # ── 阶段2: 深入分析2.hbf中1-J/1-X的0x3277记录 ──
    print(f"\n\n{'='*80}")
    print("阶段2: 验证2.hbf中0x3277记录的u32[2]是否为文件偏移量")
    print("="*80)

    filepath = os.path.join(CURRENT_DIR, "2.hbf")
    entries = get_all_dir_entries(filepath)

    # 用1-J的F9
    e = entries['1-J']
    f9, f6 = e['F9'], e['F6']
    print(f"\n1-J: F9=0x{f9:08X}, F6={f6}, F7={e['F7']}")

    records = extract_3277_records(filepath, f9, f6)
    print(f"提取到 {len(records)} 条0x3277记录")

    # 显示前10条
    print(f"\n前10条记录:")
    for r in records[:10]:
        print(f"  idx={r['idx']:>4}: u32[0]=0x{r['u32_0']:08X} u32[1]=0x{r['u32_1']:08X} "
              f"u32[2]=0x{r['u32_2']:08X} u64=0x{r['u64_addr']:016X}")

    # 验证u32[2]之间的间隔
    print(f"\nu32[2]间隔分析:")
    offsets_u32 = [r['u32_2'] for r in records]
    for i in range(1, min(10, len(offsets_u32))):
        gap = offsets_u32[i] - offsets_u32[i-1]
        print(f"  [{i-1}→{i}]: 0x{offsets_u32[i-1]:08X} → 0x{offsets_u32[i]:08X} "
              f"gap=0x{gap:08X} ({gap})")

    # ── 阶段3: 在u32[2]偏移处实际读取数据 ──
    print(f"\n\n{'='*80}")
    print("阶段3: 在u32[2]偏移处读取实际曲线数据")
    print("="*80)

    # 尝试前3个偏移
    for i in range(min(3, len(records))):
        target_offset = records[i]['u32_2']
        print(f"\n记录[{i}] u32[2]=0x{target_offset:08X} ({target_offset}):")

        # 先看原始hex
        raw = read_at(filepath, target_offset, 256)
        print(f"  前256字节hex:")
        for j in range(0, min(256, len(raw)), 32):
            hex_str = ' '.join(f'{b:02x}' for b in raw[j:j+32])
            ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in raw[j:j+32])
            print(f"    +{j:04x}: {hex_str} |{ascii_str}|")

        # 找曲线数据
        curves = find_curves_at_offset(filepath, target_offset)
        if curves:
            print(f"  找到 {len(curves)} 段曲线数据:")
            for c in curves:
                print(f"    偏移 0x{c['file_offset']:08X}: len={c['length']} peak={c['peak']:.3f}A")
                print(f"    first5={c['first5']}")
                print(f"    last5={c['last5']}")
        else:
            print(f"  未找到曲线数据")

    # ── 阶段4: 检查u32[2]偏移周围是不是有3条曲线 ──
    print(f"\n\n{'='*80}")
    print("阶段4: 检查每条0x3277记录对应的数据区是否包含3条曲线(三相)")
    print("="*80)

    # 对前5条记录，扩大搜索范围
    for i in range(min(5, len(records))):
        target_offset = records[i]['u32_2']
        raw = read_at(filepath, target_offset, 0x5000)  # 读20KB

        # 在此区域搜索所有曲线段
        n_floats = len(raw) // 4
        f32 = struct.unpack_from(f'<{n_floats}f', raw, 0)

        curves = []
        seg_start = None
        seg_vals = []
        for fi, v in enumerate(f32):
            if abs(v) > 0.02:
                if seg_start is None:
                    seg_start = fi
                    seg_vals = []
                seg_vals.append(v)
            else:
                if seg_start is not None:
                    if 50 <= len(seg_vals) <= 600:
                        peak = max(abs(x) for x in seg_vals)
                        if 0.3 <= peak <= 10.0:
                            peak_idx = seg_vals.index(max(seg_vals, key=abs))
                            if 5 <= peak_idx <= len(seg_vals) - 5:
                                curves.append({
                                    'offset': target_offset + seg_start * 4,
                                    'len': len(seg_vals),
                                    'peak': round(peak, 3),
                                })
                    seg_start = None
                    seg_vals = []

        print(f"\n记录[{i}] u32[2]=0x{target_offset:08X}: 区域内找到 {len(curves)} 条曲线")
        for ci, c in enumerate(curves):
            print(f"  [{ci}] @ 0x{c['offset']:08X} (rel +0x{c['offset']-target_offset:05X}): "
                  f"len={c['len']}, peak={c['peak']}A")

    # ── 阶段5: F3字段分析 ──
    print(f"\n\n{'='*80}")
    print("阶段5: 按F4分组分析开关（共享数据池？）")
    print("="*80)

    # 按F4分组
    f4_groups = defaultdict(list)
    for sw_id, e in entries.items():
        f4_groups[e['F4']].append((sw_id, e['F3'], e['F7']))

    print(f"\nF4分组 (可能有 {len(f4_groups)} 个数据池):")
    for f4 in sorted(f4_groups.keys()):
        members = f4_groups[f4]
        total_f7 = sum(m[2] for m in members)
        print(f"  F4=0x{f4:08X} ({f4}): {len(members)} 开关, 总F7={total_f7}")
        for sw, f3, f7 in sorted(members, key=lambda x: (int(x[0].split('-')[0]), x[0].split('-')[1])):
            print(f"    {sw}: F3={f3}, F7={f7}")

    print("\n完成!")

if __name__ == '__main__':
    main()
