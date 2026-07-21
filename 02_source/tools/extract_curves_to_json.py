#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 2.hbf 提取所有道岔的功率曲线，保存为 JSON
"""
import struct
import sys
import os
import json
import math
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
OUTPUT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_power_curves(f32_values, min_len=80, max_len=2000, min_peak=0.2, max_peak=10.0):
    """
    在 float32 数组中找到功率曲线段
    返回: [(start_idx, end_idx, peak, peak_idx), ...]
    """
    n = len(f32_values)
    curves = []
    i = 0
    while i < n:
        # 跳过零点
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
                if consecutive_zeros > 10:  # 至少10个连续零才认为段结束
                    end = end - consecutive_zeros
                    break
            else:
                consecutive_zeros = 0
            end += 1
        if end >= n:
            end = n

        seg_len = end - start
        if min_len <= seg_len <= max_len:
            seg = f32_values[start:end]
            peak = max(seg)
            peak_idx = seg.index(peak)

            if min_peak <= peak <= max_peak:
                # 形态验证
                first_q = seg[:min(15, seg_len)]
                last_q = seg[-min(15, seg_len):]
                avg_first = sum(abs(v) for v in first_q) / len(first_q)
                avg_last = sum(abs(v) for v in last_q) / len(last_q)

                # 起点应接近零，终点也应接近零或低稳态
                if avg_first < 1.0 and avg_last < 0.8:
                    # 峰值不应在开头或结尾
                    if 5 < peak_idx < seg_len - 5:
                        curves.append({
                            'start': start, 'end': end, 'len': seg_len,
                            'peak': round(peak, 4), 'peak_idx': peak_idx,
                        })

        i = end + 1
        while i < n and abs(f32_values[i]) < 0.001:
            i += 1

    return curves

def extract_curve_values(f32_values, start, end):
    """提取并清理曲线值"""
    return [round(v, 4) for v in f32_values[start:end]]

def main():
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    fpath = os.path.join(POWER_DIR, '2.hbf')
    size = os.path.getsize(fpath)
    data2mb = read_at(fpath, 0, 0x200000)

    print("="*70)
    print("提取 2.hbf 功率曲线 → JSON")
    print("="*70)

    all_curves = {}  # sw_id -> [{offset, curve_data, peak, ...}]

    for sw_id in SWITCH_IDS:
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

        # 读数据区
        # 用 F6 作为预分配事件头大小，实际数据在 F9 开始
        # 先读 F9 前2MB 试
        scan_size = min(3 * 1024 * 1024, size - f9)
        raw = read_at(fpath, f9, scan_size)

        nz_bytes = sum(1 for b in raw[:65536] if b != 0)
        if nz_bytes < 100:
            print(f"  [{sw_id}] F9=0x{f9:x} — 无数据")
            continue

        # 转为 float32 数组
        n_floats = len(raw) // 4
        f32 = [struct.unpack_from('<f', raw, j*4)[0] for j in range(n_floats)]

        curves = find_power_curves(f32)

        # 去重：合并重叠或相邻过近的曲线
        if curves:
            merged = [curves[0]]
            for c in curves[1:]:
                last = merged[-1]
                # 如果起始位置太接近（在10个点内），保留更长的
                if c['start'] - last['start'] < 10:
                    if c['len'] > last['len']:
                        merged[-1] = c
                else:
                    merged.append(c)
            curves = merged

        if curves:
            all_curves[sw_id] = []
            print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} 找到 {len(curves)} 条有效曲线:")

            for i, c in enumerate(curves[:10]):  # 显示前10条
                vals = extract_curve_values(f32, c['start'], c['end'])
                byte_offset = f9 + c['start'] * 4
                print(f"    #{i}: file_offset=0x{byte_offset:x} len={c['len']} "
                      f"峰值={c['peak']:.3f}KW @ idx={c['peak_idx']} "
                      f"前5={vals[:5]} 尾5={vals[-5:]}")

                all_curves[sw_id].append({
                    'curve_index': i,
                    'file_offset': byte_offset,
                    'float32_offset': c['start'],
                    'sample_count': c['len'],
                    'peak_kw': c['peak'],
                    'peak_index': c['peak_idx'],
                    'values': vals,
                })

            if len(curves) > 10:
                for i, c in enumerate(curves[10:], 10):
                    vals = extract_curve_values(f32, c['start'], c['end'])
                    byte_offset = f9 + c['start'] * 4
                    all_curves[sw_id].append({
                        'curve_index': i,
                        'file_offset': byte_offset,
                        'float32_offset': c['start'],
                        'sample_count': c['len'],
                        'peak_kw': c['peak'],
                        'peak_index': c['peak_idx'],
                        'values': vals,
                    })
                print(f"    ... 还有 {len(curves)-10} 条")

    # 保存
    output_path = os.path.join(OUTPUT_DIR, 'power_curves_2hbf.json')
    summary = {
        'source_file': '功率/2.hbf',
        'extraction_time': datetime.now().isoformat(),
        'total_switches_with_data': len(all_curves),
        'total_curves': sum(len(v) for v in all_curves.values()),
        'curves': all_curves,
    }

    with open(output_path, 'w', encoding='utf-8') as fout:
        json.dump(summary, fout, ensure_ascii=False)

    print(f"\n{'='*70}")
    print(f"保存到: {output_path}")
    print(f"{'='*70}")
    print(f"道岔: {len(all_curves)}/30  总曲线: {summary['total_curves']}")

    for sw_id in sorted(all_curves.keys()):
        curves = all_curves[sw_id]
        lengths = [c['sample_count'] for c in curves]
        peaks = [c['peak_kw'] for c in curves]
        print(f"  {sw_id}: {len(curves)}条  长度{min(lengths)}-{max(lengths)}  峰值{min(peaks):.2f}-{max(peaks):.2f}KW")

    # 同时保存 CSV 汇总
    csv_path = os.path.join(OUTPUT_DIR, 'power_curves_summary.csv')
    with open(csv_path, 'w', encoding='utf-8') as fcsv:
        fcsv.write("switch_id,curve_index,file_offset,float32_offset,sample_count,peak_kw,peak_index\n")
        for sw_id in sorted(all_curves.keys()):
            for c in all_curves[sw_id]:
                fcsv.write(f"{sw_id},{c['curve_index']},{c['file_offset']},{c['float32_offset']},"
                          f"{c['sample_count']},{c['peak_kw']},{c['peak_index']}\n")
    print(f"\nCSV 汇总: {csv_path}")


if __name__ == '__main__':
    main()
