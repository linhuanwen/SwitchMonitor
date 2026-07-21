#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
直接扫描 2.hbf 文件，找所有看起来像功率曲线的 float32 序列
不再尝试解析索引结构，直接找数据
"""
import struct
import sys
import os

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_power_curves_in_raw(raw_data, min_len=50, max_len=2000, min_peak=0.1, max_peak=10.0):
    """
    在原始字节数据中搜索 float32 功率曲线
    曲线特征: 起点接近0→上升到峰值(0.1-10KW)→下降到接近0
    """
    n_floats = len(raw_data) // 4
    f32 = [struct.unpack_from('<f', raw_data, j*4)[0] for j in range(n_floats)]

    curves = []
    i = 0
    while i < n_floats:
        # 找非零起点
        if abs(f32[i]) < 0.005:
            i += 1
            continue

        # 回溯找真正的起始零点
        start = i
        while start > 0 and abs(f32[start-1]) < 0.01:
            start -= 1

        # 向前找这个非零段的结束
        end = i
        while end < n_floats and abs(f32[end]) > 0.001:
            end += 1

        seg_len = end - start
        if min_len <= seg_len <= max_len:
            seg = f32[start:end]
            peak = max(seg)
            peak_idx = seg.index(peak)

            if min_peak <= peak <= max_peak:
                # 检查曲线形态: 上升→峰值→下降
                pre_peak = seg[:peak_idx]
                post_peak = seg[peak_idx+1:]

                # 简单形态检验
                if len(pre_peak) > 5 and len(post_peak) > 5:
                    first_q = seg[:min(10, len(seg))]
                    last_q = seg[-min(10, len(seg)):]

                    # 起始和结束应接近零
                    avg_first = sum(abs(v) for v in first_q) / len(first_q)
                    avg_last = sum(abs(v) for v in last_q) / len(last_q)

                    if avg_first < 1.0 and avg_last < 0.5:
                        curves.append({
                            'start': start,
                            'end': end,
                            'len': seg_len,
                            'peak': peak,
                            'peak_idx': peak_idx,
                            'values': seg,
                        })

        i = end + 1

    return curves

def main():
    fpath = os.path.join(POWER_DIR, '2.hbf')
    size = os.path.getsize(fpath)
    data2mb = read_at(fpath, 0, 0x200000)

    print("="*70)
    print("直接扫描 2.hbf 全文件 → 提取功率曲线")
    print("="*70)

    # Step 1: 对每个道岔，扫描其 F9 及周边区域
    print("\n【扫描每个道岔的 F9 数据区】")

    all_curves = {}

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

        # 扫描范围: F9 开始, 读较大的区域
        # F6 = F7 × 32 是事件头数组大小
        # 但数据可能在 F9 之后很远处
        # 尝试读 F9 到下一个开关 F9 之间的区域
        scan_size = min(2 * 1024 * 1024, size - f9)  # 最多2MB
        raw = read_at(fpath, f9, scan_size)

        nz_bytes = sum(1 for b in raw[:65536] if b != 0)
        if nz_bytes < 100:
            continue

        curves = find_power_curves_in_raw(raw)
        if curves:
            all_curves[sw_id] = []
            print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} 找到 {len(curves)} 条曲线:")
            for c in curves[:5]:
                print(f"    offset={c['start']}(float32@{c['start']}) len={c['len']} "
                      f"峰值={c['peak']:.3f}KW @ idx={c['peak_idx']}")
                print(f"    前10: {[round(v,3) for v in c['values'][:10]]}")
                mid = c['peak_idx']
                print(f"    峰值附近: {[round(v,3) for v in c['values'][max(0,mid-5):mid+5]]}")
                print(f"    尾10: {[round(v,3) for v in c['values'][-10:]]}")
                all_curves[sw_id].append(c)
            if len(curves) > 5:
                print(f"    ... 还有 {len(curves)-5} 条")

    # Step 2: 也扫描 1.hbf 的数据区
    print(f"\n\n【扫描 1.hbf F9 数据区】")
    fpath1 = os.path.join(POWER_DIR, '1.hbf')
    size1 = os.path.getsize(fpath1)
    data1_2mb = read_at(fpath1, 0, 0x200000)

    for sw_id in SWITCH_IDS:
        pos = data1_2mb.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data1_2mb[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4, f6, f7, f9 = f[4], f[6], f[7], f[9]

        if not (0 < f9 < size1 and 10 < f7 < 100000):
            continue

        scan_size = min(1 * 1024 * 1024, size1 - f9)
        raw = read_at(fpath1, f9, scan_size)

        nz_bytes = sum(1 for b in raw[:65536] if b != 0)
        if nz_bytes < 100:
            continue

        curves = find_power_curves_in_raw(raw)
        if curves:
            print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} 找到 {len(curves)} 条:")
            for c in curves[:3]:
                print(f"    offset={c['start']} len={c['len']} 峰值={c['peak']:.3f}KW")

    # Step 3: 汇总
    print(f"\n\n{'='*70}")
    print(f"汇总: 2.hbf")
    print(f"{'='*70}")
    total = sum(len(v) for v in all_curves.values())
    print(f"有道岔曲线数据: {len(all_curves)}/30  总曲线数: {total}")
    for sw_id in sorted(all_curves.keys()):
        curves = all_curves[sw_id]
        lengths = [c['len'] for c in curves]
        peaks = [c['peak'] for c in curves]
        print(f"  {sw_id}: {len(curves)}条  长度{min(lengths)}-{max(lengths)}  峰值{min(peaks):.2f}-{max(peaks):.2f}KW")

    # Step 4: 如果找到的曲线太少，尝试在整个文件中搜索
    if total < 20:
        print(f"\n\n【全局搜索】在 2.hbf 中扫描大片区域...")
        # 扫描几个大块
        for block_start in range(0x300000, min(size, 0x20000000), 0x400000):  # 从3MB开始, 每4MB扫一次
            raw = read_at(fpath, block_start, 512 * 1024)  # 512KB
            curves = find_power_curves_in_raw(raw)
            if curves:
                print(f"  @0x{block_start:x}: {len(curves)} 条曲线")
                for c in curves[:3]:
                    abs_offset = block_start + c['start'] * 4
                    print(f"    绝对偏移=0x{abs_offset:x} offset={c['start']} len={c['len']} 峰值={c['peak']:.3f}KW")
                    print(f"    前10: {[round(v,3) for v in c['values'][:10]]}")


if __name__ == '__main__':
    main()
