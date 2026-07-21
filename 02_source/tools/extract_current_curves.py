#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从电流HBF提取三相电流曲线，并关联功率曲线。
格式：UTF-16LE元数据头 + float32采样数据（与功率HBF相同）
曲线按3条一组排列 = A/B/C三相
"""
import struct
import os
import sys
import json
from collections import defaultdict
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

BASE = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu"
POWER_FILE = os.path.join(BASE, "道岔动作功率曲线", "2.hbf")
CURRENT_FILE = os.path.join(BASE, "道岔动作电流曲线", "2.hbf")

ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def get_dir_entries(filepath):
    """获取新版格式的目录项"""
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
        f3, f4, f6, f7, f9 = fields[3], fields[4], fields[6], fields[7], fields[9]
        if f7 > 10 and f7 < 100000:
            entries[sw_id] = {
                'F3': f3, 'F4': f4, 'F6': f6, 'F7': f7, 'F9': f9,
                'all_fields': list(fields)
            }
    return entries

def find_curves_in_data(raw, base_offset=0, min_len=50, max_len=600, min_peak=0.5, max_peak=10.0):
    """在float32数据中寻找曲线段（电流范围0.5-10A）"""
    n_floats = len(raw) // 4
    f32 = struct.unpack_from(f'<{n_floats}f', raw, 0)

    curves = []
    in_segment = False
    seg_start = 0
    seg_values = []

    for i, v in enumerate(f32):
        if abs(v) > 0.02:  # 非零阈值
            if not in_segment:
                seg_start = i
                seg_values = []
                in_segment = True
            seg_values.append(v)
        else:
            if in_segment:
                seg_len = len(seg_values)
                if min_len <= seg_len <= max_len:
                    peak = max(abs(x) for x in seg_values)
                    if min_peak <= peak <= max_peak:
                        # 验证曲线形状：峰值不在两端
                        peak_idx = seg_values.index(max(seg_values, key=abs))
                        if 3 <= peak_idx <= seg_len - 3:
                            curves.append({
                                'float32_offset': seg_start,
                                'file_offset': base_offset + seg_start * 4,
                                'length': seg_len,
                                'peak': round(peak, 4),
                                'values': [round(x, 6) for x in seg_values],
                            })
                in_segment = False

    return curves

def find_curves_with_utf16_headers(raw, base_offset=0):
    """
    在包含UTF-16LE元数据头的raw数据中寻找曲线。
    功率/电流 HBF 格式:
    [UTF-16LE metadata] [padding] [float32 curve data]
    其中元数据包含 "curve_info_len" 和 "curve_info" 标签
    """
    curves = []
    pos = 0

    # 搜索UTF-16LE标签
    tag_curve_info = 'curve_info'.encode('utf-16-le')
    tag_curve_info_len = 'curve_info_len'.encode('utf-16-le')

    while pos < len(raw) - 100:
        # 找下一个 "curve_info" 标签
        tag_pos = raw.find(tag_curve_info, pos)
        if tag_pos == -1:
            break

        # 从这个标签往后找float32数据
        # 跳过标签和后续的零值/元数据
        # 典型结构: tag + null + numeric_fields + null_padding → float32 data
        search_start = tag_pos + len(tag_curve_info)

        # 跳过后续的零值字节，找到第一个非零float32
        # 但要注意：元数据字段可能包含小整数
        # 找float32曲线的开始: 通常是 0x3E/0x3F/0x40 开头的值(0.1-5.0范围)
        curve_start = None
        for i in range(search_start, min(search_start + 0x200, len(raw) - 4), 4):
            val = struct.unpack_from('<f', raw, i)[0]
            if 0.1 < val < 10.0:
                # 可能是曲线起点，验证后面几个值
                next_vals = []
                for j in range(1, 10):
                    if i + j*4 + 4 <= len(raw):
                        next_vals.append(struct.unpack_from('<f', raw, i + j*4)[0])
                # 至少有3个值在合理范围
                reasonable = sum(1 for v in next_vals if 0.05 < abs(v) < 10.0)
                if reasonable >= 3:
                    curve_start = i
                    break

        if curve_start is None:
            pos = tag_pos + 1
            continue

        # 从curve_start开始提取连续非零段
        n_remaining = (len(raw) - curve_start) // 4
        f32 = struct.unpack_from(f'<{n_remaining}f', raw, curve_start)

        seg_values = []
        for v in f32:
            if abs(v) > 0.02:
                seg_values.append(v)
            else:
                break

        if 50 <= len(seg_values) <= 600:
            peak = max(abs(x) for x in seg_values)
            if 0.3 <= peak <= 10.0:
                peak_idx = seg_values.index(max(seg_values, key=abs))
                if 3 <= peak_idx <= len(seg_values) - 3:
                    curves.append({
                        'float32_offset': curve_start // 4,
                        'file_offset': base_offset + curve_start,
                        'length': len(seg_values),
                        'peak': round(peak, 4),
                        'tag_offset': tag_pos,
                        'values': [round(x, 6) for x in seg_values],
                    })

        pos = curve_start + len(seg_values) * 4 + 4

    return curves


def main():
    size_cur = os.path.getsize(CURRENT_FILE)
    size_pwr = os.path.getsize(POWER_FILE)

    print("="*70)
    print("阶段1: 读取目录项")
    print("="*70)

    pwr_entries = get_dir_entries(POWER_FILE)
    cur_entries = get_dir_entries(CURRENT_FILE)
    print(f"功率: {len(pwr_entries)} 开关, 电流: {len(cur_entries)} 开关")

    # ── 阶段2: 提取电流曲线 ──
    print(f"\n{'='*70}")
    print("阶段2: 扫描电流HBF，提取三相电流曲线")
    print(f"{'='*70}")

    # 电流曲线在文件的后半部分（稀疏扫描发现从 ~0x3400000 开始）
    # 使用稀疏扫描方式，以1MB为步长
    all_current_curves = []

    scan_range = range(0x3000000, min(size_cur, 0x1F000000), 0x100000)
    total_regions = len(scan_range)

    for idx, base_offset in enumerate(scan_range):
        chunk = read_at(CURRENT_FILE, base_offset, 0x20000)  # 128KB/chunk
        if len(chunk) < 0x10000:
            break

        nz = sum(1 for b in chunk[:0x10000] if b != 0)
        if nz < 200:
            continue

        # 先用简单方法找曲线
        curves = find_curves_in_data(chunk, base_offset, min_len=50, max_len=600, min_peak=0.5, max_peak=10.0)
        all_current_curves.extend(curves)

        if idx % 50 == 0:
            print(f"  扫描进度: {idx}/{total_regions} 区域, 已找到 {len(all_current_curves)} 段")

    print(f"\n  总计找到 {len(all_current_curves)} 个电流曲线段")

    # ── 阶段3: 按3条一组分组 (A/B/C三相) ──
    print(f"\n{'='*70}")
    print("阶段3: 按3条一组分组 (A/B/C三相)")
    print(f"{'='*70}")

    # 按文件偏移排序
    all_current_curves.sort(key=lambda c: c['file_offset'])

    # 找间距很小的曲线组（< 0x2000 字节 = 8KB）
    groups = []
    current_group = [all_current_curves[0]]

    for i in range(1, len(all_current_curves)):
        gap = all_current_curves[i]['file_offset'] - all_current_curves[i-1]['file_offset']
        if gap < 0x2000:  # 同一组内
            current_group.append(all_current_curves[i])
        else:
            if len(current_group) >= 1:
                groups.append(current_group)
            current_group = [all_current_curves[i]]

    if current_group:
        groups.append(current_group)

    print(f"  分组数: {len(groups)}")

    # 统计组大小分布
    size_dist = defaultdict(int)
    for g in groups:
        size_dist[len(g)] += 1
    for size in sorted(size_dist.keys()):
        print(f"    {size}条/组: {size_dist[size]} 组")

    # ── 阶段4: 过滤 - 只保留3条一组的 ──
    triple_groups = [g for g in groups if len(g) == 3]
    print(f"\n  3条一组 (标准三相): {len(triple_groups)} 组")

    # 显示几个示例
    print(f"\n  前5组示例:")
    for gi, g in enumerate(triple_groups[:5]):
        offs = ', '.join('0x%08X' % c['file_offset'] for c in g)
        peaks = ', '.join('%.2fA' % c['peak'] for c in g)
        lens = ', '.join(str(c['length']) for c in g)
        print(f"  组{gi}: offsets=[{offs}]")
        print(f"         peaks=[{peaks}]")
        print(f"         lengths=[{lens}]")

    # ── 阶段5: 关联电流HBF的F9索引记录来分配开关ID ──
    print(f"\n{'='*70}")
    print("阶段5: 解析F9索引记录，为曲线组分配开关ID")
    print(f"{'='*70}")

    # 电流HBF的F9区域有32B记录，用F3作为该开关的起始索引
    # 读取每个开关的F9索引记录，获取事件排序信息
    switch_events = {}

    for sw_id, entry in sorted(cur_entries.items()):
        f9 = entry['F9']
        f6 = entry['F6']
        f7 = entry['F7']
        f3 = entry['F3']

        raw = read_at(CURRENT_FILE, f9, f6)
        n_records = f6 // 32

        # 解析32B记录找0x3277标记
        marker_3277 = b'\x77\x32'
        events_3277 = []

        for i in range(n_records):
            rec = raw[i*32:(i+1)*32]
            # 在字节13-14搜索标记
            if rec[13:15] == marker_3277:
                u32s = struct.unpack_from('<8I', rec, 0)
                u64_addr = struct.unpack_from('<Q', rec, 16)[0]
                events_3277.append({
                    'record_index': i,
                    'seq': u32s[0],
                    'u64_addr': u64_addr,
                })

        if events_3277:
            switch_events[sw_id] = {
                'total_records': n_records,
                'events_3277': len(events_3277),
                'f9': f9,
                'f3': f3,
                'events': events_3277,
            }
            print(f"  {sw_id}: {len(events_3277)} 个0x3277事件 (总{entry['F7']}条记录)")

    # 计算总事件数
    total_3277_events = sum(len(v['events']) for v in switch_events.values())
    print(f"\n  总0x3277事件: {total_3277_events}")
    print(f"  三相组数: {len(triple_groups)}")

    # ── 阶段6: 保存结果 ──
    print(f"\n{'='*70}")
    print("阶段6: 保存提取结果")
    print(f"{'='*70}")

    output = {
        'meta': {
            'source_power': POWER_FILE,
            'source_current': CURRENT_FILE,
            'extraction_time': datetime.now().isoformat(),
            'total_current_curves': len(all_current_curves),
            'total_triple_groups': len(triple_groups),
            'switches_with_current_data': list(switch_events.keys()),
            'total_3277_events': total_3277_events,
        },
        'switch_events': {sw: {
            'total_records': v['total_records'],
            'events_3277': v['events_3277'],
            'f9': v['f9'],
            'f3': v['f3'],
        } for sw, v in switch_events.items()},
        'triple_groups': [{
            'curves': [{
                'file_offset': c['file_offset'],
                'length': c['length'],
                'peak': c['peak'],
                'values': c['values'],
            } for c in g],
        } for g in triple_groups],
    }

    out_path = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\current_curves_extracted.json"
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(output, f, ensure_ascii=False)

    print(f"  已保存: {out_path}")
    print(f"  文件大小: {os.path.getsize(out_path) / 1024 / 1024:.1f} MB")

    # ── 报告 ──
    print(f"\n{'='*70}")
    print("提取报告")
    print(f"{'='*70}")
    print(f"  电流曲线总数: {len(all_current_curves)}")
    print(f"  三相组: {len(triple_groups)}")
    print(f"  有道岔索引的0x3277事件: {total_3277_events}")
    print(f"  有道岔数据的开关: {len(switch_events)}")

    if total_3277_events > 0 and len(triple_groups) > 0:
        ratio = len(triple_groups) / total_3277_events
        print(f"  三相组 / 0x3277事件: {ratio:.2f}")
        if 0.95 < ratio < 1.05:
            print(f"  ✓ 几乎完美匹配!")
        elif ratio > 0.8:
            print(f"  ~ 约80%匹配，可能有漏检")


if __name__ == '__main__':
    main()
