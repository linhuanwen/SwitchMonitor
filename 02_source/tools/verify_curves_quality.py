#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
验证功率曲线数据质量：去重、F9重叠检测、异常曲线识别、缺失道岔排查
"""
import csv
import struct
import sys
import os
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
CSV_PATH = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\power_curves_summary.csv"

ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def sort_key(sid):
    parts = sid.split('-')
    return (int(parts[0]), parts[1])

def load_csv():
    switches = {}
    with open(CSV_PATH, 'r') as f:
        reader = csv.DictReader(f)
        for row in reader:
            sid = row['switch_id']
            if sid not in switches:
                switches[sid] = []
            switches[sid].append(row)
    return switches

def check_missing(switches):
    print("="*70)
    print("1. 缺失道岔检查")
    print("="*70)
    missing = []
    for sid in ALL_IDS:
        if sid not in switches:
            print(f"  MISSING: {sid} (未在CSV中)")
            missing.append(sid)
        elif len(switches[sid]) == 0:
            print(f"  ZERO: {sid} (0条曲线)")
            missing.append(sid)
    if not missing:
        print("  全部30个道岔都有数据!")
    return missing

def check_per_switch(switches):
    print(f"\n{'='*70}")
    print("2. 逐道岔分析")
    print("="*70)

    issues_map = {}
    for sid in sorted(switches.keys(), key=sort_key):
        curves = switches[sid]
        n = len(curves)
        f32_offsets = [int(c['float32_offset']) for c in curves]
        lengths = [int(c['sample_count']) for c in curves]
        peaks = [float(c['peak_kw']) for c in curves]

        offsets_sorted = sorted(f32_offsets)

        if len(offsets_sorted) >= 2:
            spacings = [offsets_sorted[i+1] - offsets_sorted[i] for i in range(len(offsets_sorted)-1)]
            min_sp = min(spacings)
            max_sp = max(spacings)
            avg_sp = sum(spacings) / len(spacings)
        else:
            min_sp = max_sp = avg_sp = 0

        issues = []
        if max(lengths) > 400:
            issues.append(f"LONG: max_len={max(lengths)}")
        if min_sp < 1:
            issues.append(f"OVERLAP: min_spacing={min_sp}")
        if len(offsets_sorted) >= 2 and offsets_sorted[0] != 0:
            if offsets_sorted[0] > 100:
                issues.append(f"STARTS_AT_{offsets_sorted[0]}")
        if n < 30:
            issues.append(f"FEW_CURVES: {n}")

        status = " ⚠ " + "; ".join(issues) if issues else " ✓"
        print(f"  {sid}: {n:4d}条  len={min(lengths)}-{max(lengths)}  "
              f"spacing={min_sp}-{max_sp}  avg={avg_sp:.0f}  "
              f"peak={min(peaks):.2f}-{max(peaks):.2f}{status}")

        if issues:
            issues_map[sid] = issues

    return issues_map

def check_long_curves(switches):
    print(f"\n{'='*70}")
    print("3. 异常长曲线 (可能多条合并)")
    print("="*70)
    found = False
    for sid in sorted(switches.keys(), key=sort_key):
        for c in switches[sid]:
            l = int(c['sample_count'])
            if l > 400:
                print(f"  {sid} #{c['curve_index']}: len={l} peak={c['peak_kw']} "
                      f"float32_offset={c['float32_offset']} file_offset=0x{int(c['file_offset']):x}")
                found = True
    if not found:
        print("  无异常长曲线")

def check_curve_spacing_patterns(switches):
    """分析曲线间距模式，检测合并/拆分问题"""
    print(f"\n{'='*70}")
    print("4. 曲线间距模式分析")
    print("="*70)

    for sid in sorted(switches.keys(), key=sort_key):
        curves = switches[sid]
        if len(curves) < 3:
            continue
        f32_offsets = sorted([int(c['float32_offset']) for c in curves])
        spacings = [f32_offsets[i+1] - f32_offsets[i] for i in range(len(f32_offsets)-1)]

        # 统计最常出现的间距
        from collections import Counter
        counter = Counter(spacings)
        top3 = counter.most_common(3)

        # 预期间距: 每个事件块大小 = F7(事件数) * 32 / 4 = 8 * F7 个float
        # 实际上是 0x1227 = 4647
        expected = 4647

        anomalies = []
        for i, sp in enumerate(spacings):
            if sp < 0.5 * expected or sp > 1.5 * expected:
                anomalies.append((i, sp))

        if anomalies:
            print(f"  {sid}: 预期间距>{expected}, 实际间距分布: {dict(top3)}")
            for idx, sp in anomalies[:5]:
                ratio = sp / expected
                print(f"    spacing[{idx}]={sp} ({ratio:.2f}x 预期) "
                      f"between offsets {f32_offsets[idx]}-{f32_offsets[idx+1]}")

def check_f9_overlap(switches, fpath):
    """检查不同道岔的 F9 数据区是否重叠"""
    print(f"\n{'='*70}")
    print("5. F9数据区独立性检查")
    print("="*70)

    size = os.path.getsize(fpath)
    data2mb = read_at(fpath, 0, 0x200000)

    # 先读所有道岔的目录项
    dir_info = {}
    for sw_id in ALL_IDS:
        pos = data2mb.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data2mb[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4, f6, f7, f9 = f[4], f[6], f[7], f[9]
        if 0 < f9 < size and 10 < f7 < 100000:
            dir_info[sw_id] = {'F4': f4, 'F6': f6, 'F7': f7, 'F9': f9}

    # 排序 F9
    sorted_by_f9 = sorted(dir_info.items(), key=lambda x: x[1]['F9'])

    print("  道岔按F9排序:")
    for sw_id, info in sorted_by_f9:
        # 曲线数据结束位置估算
        if sw_id in switches and len(switches[sw_id]) > 0:
            curves = switches[sw_id]
            max_f32 = max(int(c['float32_offset']) + int(c['sample_count']) for c in curves)
            data_end = info['F9'] + max_f32 * 4
        else:
            data_end = info['F9'] + info['F6'] * 4  # 估算
        print(f"    {sw_id}: F9=0x{info['F9']:08x} F7={info['F7']} "
              f"data_range=0x{info['F9']:x}-0x{data_end:x}"
              + (f" (NO CURVES)" if sw_id not in switches or len(switches[sw_id]) == 0 else ""))

    # 检查重叠
    print("\n  重叠检查:")
    overlaps = []
    items = [(sw_id, info['F9'], info['F9'] + info['F7'] * 0x1227)  # 理论最大范围
             for sw_id, info in sorted_by_f9]

    for i in range(len(items)):
        sid1, f9_1, end1 = items[i]
        for j in range(i+1, len(items)):
            sid2, f9_2, end2 = items[j]
            if f9_1 < end2 and f9_2 < end1:
                overlap_start = max(f9_1, f9_2)
                overlap_end = min(end1, end2)
                overlaps.append((sid1, sid2, overlap_start, overlap_end))

    if overlaps:
        for s1, s2, start, end in overlaps:
            print(f"    ⚠ OVERLAP: {s1} vs {s2} @ 0x{start:x}-0x{end:x}")
    else:
        print("    ✓ F9数据区之间无重叠")

    return dir_info

def investigate_missing(missing, dir_info, fpath):
    """深入检查缺失的道岔，扩大扫描范围"""
    print(f"\n{'='*70}")
    print("6. 缺失道岔深入排查")
    print("="*70)

    size = os.path.getsize(fpath)

    for sid in missing:
        if sid not in dir_info:
            print(f"  {sid}: 无目录项")
            continue

        info = dir_info[sid]
        f4, f6, f7, f9 = info['F4'], info['F6'], info['F7'], info['F9']

        print(f"\n  [{sid}] F4=0x{f4:x} F6={f6} F7={f7} F9=0x{f9:x}")

        # 检查 F9 数据区是否有非零数据
        if f9 < size:
            scan_size = min(2 * 1024 * 1024, size - f9)
            raw = read_at(fpath, f9, scan_size)
            nz_count = sum(1 for b in raw[:min(65536, len(raw))] if b != 0)
            print(f"    F9前64K非零字节: {nz_count}")

            if nz_count > 0:
                # 转为 float32 看看
                n_floats = min(65536, len(raw)) // 4
                f32 = [struct.unpack_from('<f', raw, j*4)[0] for j in range(n_floats)]
                non_zero_f32 = [(i, v) for i, v in enumerate(f32) if abs(v) > 0.001]
                print(f"    F9 前16K float32非零值: {len(non_zero_f32)}")
                if non_zero_f32:
                    # 显示前几个非零值
                    for i, v in non_zero_f32[:10]:
                        print(f"      f32[{i}] = {v:.4f}")

                    # 检查是否有功率曲线模式
                    peaks = [(i, abs(v)) for i, v in non_zero_f32 if 0.2 < abs(v) < 10.0]
                    if peaks:
                        print(f"    可能峰值的值 (>0.2KW): {len(peaks)}")
                        for i, v in peaks[:5]:
                            print(f"      f32[{i}] = {v:.4f}")

            # 也看看F4子索引
            if 0 < f4 < size:
                sub_raw = read_at(fpath, f4, min(16384, size - f4))
                nz_sub = sum(1 for b in sub_raw if b != 0)
                print(f"    F4非零字节: {nz_sub}/{len(sub_raw)}")
                if nz_sub > 0:
                    # 解析子索引记录
                    records = []
                    for rec_idx in range(0, len(sub_raw), 32):
                        rec = sub_raw[rec_idx:rec_idx+32]
                        if len(rec) < 32:
                            break
                        u32 = struct.unpack('<8I', rec)
                        if any(v != 0 for v in u32):
                            records.append(u32)

                    print(f"    F4非零记录: {len(records)}")
                    for u32 in records[:5]:
                        hex_vals = ' '.join(f'{v:08x}' for v in u32[:8])
                        print(f"      {hex_vals}")

        else:
            print(f"    F9=0x{f9:x} 超出文件范围")

def verify_curve_count_consistency(switches, dir_info):
    """验证提取曲线数与F7容量是否匹配"""
    print(f"\n{'='*70}")
    print("7. 曲线数一致性检查")
    print("="*70)

    for sid in sorted(switches.keys(), key=sort_key):
        if sid not in dir_info:
            continue
        n_curves = len(switches[sid])
        f7 = dir_info[sid]['F7']
        ratio = n_curves / f7 if f7 > 0 else 0
        status = ""
        if ratio < 0.1:
            status = " ⚠ 曲线数远少于F7容量"
        elif ratio > 0.95:
            status = " ⚠ 接近于容量上限"
        elif 0.5 <= ratio <= 0.9:
            status = " ✓"

        print(f"  {sid}: curves={n_curves} F7={f7} ratio={ratio:.1%}{status}")

def check_curve_value_ranges(switches):
    """检查每条曲线的值范围是否合理"""
    print(f"\n{'='*70}")
    print("8. 曲线异常值检查")
    print("="*70)

    total_suspect = 0
    for sid in sorted(switches.keys(), key=sort_key):
        for c in switches[sid]:
            peak = float(c['peak_kw'])
            length = int(c['sample_count'])
            # 功率范围检查
            issues = []
            if peak < 0.1:
                issues.append(f"peak_too_low={peak:.4f}")
            if peak > 8.0:
                issues.append(f"peak_too_high={peak:.2f}")
            if length < 30:
                issues.append(f"too_short={length}")
            if length > 2000:
                issues.append(f"too_long={length}")
            if issues:
                print(f"  {sid} #{c['curve_index']}: {'; '.join(issues)}")
                total_suspect += 1
    if total_suspect == 0:
        print("  ✓ 所有曲线值在合理范围内")
    else:
        print(f"  共 {total_suspect} 条可疑曲线")

def main():
    fpath = os.path.join(POWER_DIR, '2.hbf')
    if not os.path.exists(fpath):
        print(f"ERROR: {fpath} not found")
        return

    # 加载 CSV
    print("加载 CSV...")
    switches = load_csv()
    total = sum(len(v) for v in switches.values())
    print(f"已加载 {len(switches)} 个道岔, {total} 条曲线\n")

    # 1. 缺失道岔
    missing = check_missing(switches)

    # 2. 逐道岔分析
    issues_map = check_per_switch(switches)

    # 3. 异常长曲线
    check_long_curves(switches)

    # 4. 间距模式
    check_curve_spacing_patterns(switches)

    # 5. F9 重叠
    dir_info = check_f9_overlap(switches, fpath)

    # 6. 深入排查缺失道岔
    if missing:
        investigate_missing(missing, dir_info, fpath)

    # 7. 一致性
    verify_curve_count_consistency(switches, dir_info)

    # 8. 值范围
    check_curve_value_ranges(switches)

    # 总结
    print(f"\n{'='*70}")
    print("总结")
    print("="*70)
    n_with_issues = len(issues_map)
    print(f"  缺失道岔: {len(missing)}/{len(ALL_IDS)}")
    print(f"  有数据道岔: {len(switches)}/30")
    print(f"  总曲线数: {total}")
    print(f"  存在异常的道岔: {n_with_issues}")
    if missing:
        print(f"  缺失: {', '.join(missing)}")
    if n_with_issues:
        print(f"  异常: {', '.join(sorted(issues_map.keys(), key=sort_key))}")


if __name__ == '__main__':
    main()
