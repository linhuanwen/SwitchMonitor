#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 2.hbf 提取功率曲线 — 修复版
- 移除共享F9的重复道岔 (7-J/9-J → 只保留 7-J)
- 修复第一条伪曲线 (offset=0 且有稳态值)
- 拆分超长合并曲线 (长度>500, 内部有零间隔)
"""
import struct
import sys
import os
import json
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
OUTPUT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves"

ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_power_curves_v2(f32_values, min_len=80, max_len=500, min_peak=0.2, max_peak=10.0):
    """
    改进版功率曲线检测:
    - max_len 降低到 500, 超过此长度的检查内部零点
    - 排除 offset=0 且首段不接近零的伪曲线
    """
    n = len(f32_values)
    curves = []
    i = 0
    while i < n:
        if abs(f32_values[i]) < 0.005:
            i += 1
            continue

        # 回溯找零点边界
        start = i
        while start > 0 and abs(f32_values[start-1]) < 0.01:
            start -= 1

        # 向前找段尾
        end = i
        consecutive_zeros = 0
        while end < n:
            if abs(f32_values[end]) < 0.001:
                consecutive_zeros += 1
                if consecutive_zeros > 10:
                    end = end - consecutive_zeros
                    break
            else:
                consecutive_zeros = 0
            end += 1
        if end >= n:
            end = n

        seg_len = end - start

        # 如果段太长 (>500), 尝试在内部找零间隔拆分
        if seg_len > max_len:
            sub_curves = split_long_segment(f32_values, start, end, min_len)
            for sc in sub_curves:
                sc_len = sc['end'] - sc['start']
                if min_len <= sc_len <= max_len:
                    seg = f32_values[sc['start']:sc['end']]
                    peak = max(seg)
                    peak_idx = seg.index(peak)
                    if min_peak <= peak <= max_peak and is_valid_curve(seg, peak_idx):
                        curves.append({
                            'start': sc['start'], 'end': sc['end'],
                            'len': sc_len, 'peak': round(peak, 4),
                            'peak_idx': peak_idx,
                        })
            i = end + 1
            while i < n and abs(f32_values[i]) < 0.001:
                i += 1
            continue

        if min_len <= seg_len <= max_len:
            seg = f32_values[start:end]
            peak = max(seg)
            peak_idx = seg.index(peak)

            if min_peak <= peak <= max_peak and is_valid_curve(seg, peak_idx):
                # 额外检查：如果start==0且首段不接近零 → 排除
                if start == 0:
                    first_q = seg[:15]
                    avg_first = sum(abs(v) for v in first_q) / len(first_q)
                    if avg_first > 0.1:
                        # 第一条伪曲线，跳过
                        i = end + 1
                        while i < n and abs(f32_values[i]) < 0.001:
                            i += 1
                        continue

                curves.append({
                    'start': start, 'end': end, 'len': seg_len,
                    'peak': round(peak, 4), 'peak_idx': peak_idx,
                })

        i = end + 1
        while i < n and abs(f32_values[i]) < 0.001:
            i += 1

    return curves

def split_long_segment(f32_values, seg_start, seg_end, min_len=50):
    """在长段中查找零间隔(>20个连续接近零的值)来拆分"""
    sub_curves = []
    sub_start = seg_start
    zero_count = 0
    zero_start = None

    for i in range(seg_start, seg_end):
        if abs(f32_values[i]) < 0.005:
            if zero_count == 0:
                zero_start = i
            zero_count += 1
            if zero_count >= 20:
                # 找到分隔点
                sub_end = zero_start
                if sub_end - sub_start >= min_len:
                    sub_curves.append({'start': sub_start, 'end': sub_end})
                sub_start = i + 1
                zero_count = 0
                zero_start = None
        else:
            zero_count = 0
            zero_start = None

    # 最后一段
    if seg_end - sub_start >= min_len:
        sub_curves.append({'start': sub_start, 'end': seg_end})

    return sub_curves

def is_valid_curve(seg, peak_idx):
    """曲线形态验证"""
    seg_len = len(seg)
    first_q = seg[:min(15, seg_len)]
    last_q = seg[-min(15, seg_len):]
    avg_first = sum(abs(v) for v in first_q) / len(first_q)
    avg_last = sum(abs(v) for v in last_q) / len(last_q)

    # 起点接近零，终点接近零或低稳态
    if avg_first >= 1.0 or avg_last >= 0.8:
        return False
    # 峰值不在边界
    if peak_idx < 5 or peak_idx > seg_len - 5:
        return False
    return True

def extract_curve_values(f32_values, start, end):
    return [round(v, 4) for v in f32_values[start:end]]

def main():
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    fpath = os.path.join(POWER_DIR, '2.hbf')
    size = os.path.getsize(fpath)
    data2mb = read_at(fpath, 0, 0x200000)

    print("="*70)
    print("提取 2.hbf 功率曲线 → 修复版 (去重+修正)")
    print("="*70)

    # Step 1: 检测共享F9
    f9_map = {}
    for sw_id in ALL_IDS:
        pos = data2mb.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data2mb[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        if 0 < f[9] < size and 10 < f[7] < 100000:
            f9_map[sw_id] = f[9]

    # 共享F9组
    f9_groups = defaultdict(list)
    for sw_id, f9 in f9_map.items():
        f9_groups[f9].append(sw_id)

    shared_f9 = {f9: ids for f9, ids in f9_groups.items() if len(ids) > 1}
    skip_ids = set()
    if shared_f9:
        print("\n⚠ 共享F9的道岔组 (只保留第一个):")
        for f9, ids in shared_f9.items():
            keep = ids[0]
            for sid in ids[1:]:
                skip_ids.add(sid)
                print(f"  移除 {sid} (重复于 {keep}, F9=0x{f9:x})")

    # Step 2: 提取
    all_curves = {}
    issues_log = []

    for sw_id in ALL_IDS:
        if sw_id in skip_ids:
            continue

        pos = data2mb.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data2mb[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4, f6, f7, f9 = f[4], f[6], f[7], f[9]

        if not (0 < f9 < size and 10 < f7 < 100000):
            continue

        scan_size = min(3 * 1024 * 1024, size - f9)
        raw = read_at(fpath, f9, scan_size)

        nz_bytes = sum(1 for b in raw[:65536] if b != 0)
        if nz_bytes < 100:
            continue

        n_floats = len(raw) // 4
        f32 = [struct.unpack_from('<f', raw, j*4)[0] for j in range(n_floats)]

        curves = find_power_curves_v2(f32)

        # 去重：合并重叠或过近的曲线
        if curves:
            merged = [curves[0]]
            for c in curves[1:]:
                last = merged[-1]
                if c['start'] - last['start'] < 10:
                    if c['len'] > last['len']:
                        merged[-1] = c
                else:
                    merged.append(c)
            curves = merged

        if curves:
            all_curves[sw_id] = []
            print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} 找到 {len(curves)} 条曲线:")

            for i, c in enumerate(curves):
                vals = extract_curve_values(f32, c['start'], c['end'])
                byte_offset = f9 + c['start'] * 4

                if i < 5:
                    print(f"    #{i}: float32@{c['start']} len={c['len']} "
                          f"峰值={c['peak']:.3f}KW @ idx={c['peak_idx']} "
                          f"前5={vals[:5]}")

                all_curves[sw_id].append({
                    'curve_index': i,
                    'file_offset': byte_offset,
                    'float32_offset': c['start'],
                    'sample_count': c['len'],
                    'peak_kw': c['peak'],
                    'peak_index': c['peak_idx'],
                    'values': vals,
                })

            if len(curves) > 5:
                print(f"    ... 还有 {len(curves)-5} 条")

    # Step 3: 保存
    output_path = os.path.join(OUTPUT_DIR, 'power_curves_2hbf_v2.json')
    total_curves = sum(len(v) for v in all_curves.values())
    summary = {
        'source_file': '功率/2.hbf',
        'extraction_time': datetime.now().isoformat(),
        'version': 'v2 — 去重+修正',
        'fixes_applied': [
            '移除共享F9的重复道岔: 9-J (重复于7-J)',
            '修复第一条伪曲线: start=0且首段不接近零时排除',
            '拆分超长曲线: 查找20+连续零值作为曲线边界',
        ],
        'total_switches_with_data': len(all_curves),
        'total_curves': total_curves,
        'curves': all_curves,
    }

    with open(output_path, 'w', encoding='utf-8') as fout:
        json.dump(summary, fout, ensure_ascii=False)

    # Step 4: 报告
    print(f"\n{'='*70}")
    print(f"保存到: {output_path}")
    print(f"{'='*70}")
    print(f"道岔: {len(all_curves)}/30  总曲线: {total_curves}")

    for sw_id in sorted(all_curves.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        curves = all_curves[sw_id]
        lengths = [c['sample_count'] for c in curves]
        peaks = [c['peak_kw'] for c in curves]
        print(f"  {sw_id}: {len(curves)}条  len={min(lengths)}-{max(lengths)}  "
              f"peak={min(peaks):.2f}-{max(peaks):.2f}KW")

    # CSV
    csv_path = os.path.join(OUTPUT_DIR, 'power_curves_summary_v2.csv')
    with open(csv_path, 'w', encoding='utf-8') as fcsv:
        fcsv.write("switch_id,curve_index,file_offset,float32_offset,sample_count,peak_kw,peak_index\n")
        for sw_id in sorted(all_curves.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
            for c in all_curves[sw_id]:
                fcsv.write(f"{sw_id},{c['curve_index']},{c['file_offset']},{c['float32_offset']},"
                          f"{c['sample_count']},{c['peak_kw']},{c['peak_index']}\n")
    print(f"\nCSV: {csv_path}")

    # 质量报告
    print(f"\n{'='*70}")
    print("数据质量报告")
    print("="*70)

    missing = [sid for sid in ALL_IDS if sid not in all_curves and sid not in skip_ids]
    if missing:
        print(f"\n缺失道岔 ({len(missing)}):")
        for sid in missing:
            reason = "未知"
            if sid in f9_map:
                # 检查是否有非零数据
                f9 = f9_map[sid]
                raw = read_at(fpath, f9, 1024)
                nz = sum(1 for b in raw if b != 0)
                if nz == 0:
                    reason = "F9全为零"
                else:
                    # 检查是否是索引记录而非浮点数据
                    u32_test = [struct.unpack_from('<I', raw, j*4)[0] for j in range(4)]
                    if u32_test[3] == 0x1227 or any(v == 0x1227 for v in u32_test):
                        reason = "F9存储事件索引记录(非浮点采样)"
                    else:
                        reason = f"F9有{nz}非零字节, 但无法识别为功率曲线"
            else:
                reason = "无有效目录项"
            print(f"  {sid}: {reason}")

    removed_by_dedup = len(skip_ids)
    print(f"\n因重复F9移除的道岔: {removed_by_dedup} ({', '.join(sorted(skip_ids))})")

    # 核对: 哪些开关数据来自相同F9?
    print(f"\n独特的F9数据区: {len(set(f9_map.values()) - set(shared_f9.keys())) + len(shared_f9)}")

    print(f"\n最终有效道岔: {len(all_curves)}")
    print(f"最终有效曲线: {total_curves}")
    print("完成!")


if __name__ == '__main__':
    main()
