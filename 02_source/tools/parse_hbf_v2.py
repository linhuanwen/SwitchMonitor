#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
完整 HBF 功率曲线解析器
结构: 256B目录→F9→float32采样数据 (32B/采样=8通道)
"""
import struct, sys, os, json
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"
OUTPUT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\parsed_data\panyu"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_directory(filepath):
    """解析256B目录项，提取关键字段"""
    data = read_at(filepath, 0, 0x30000)
    entries = {}
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        p = data.find(pattern)
        if p == -1 or p >= 0x28000:
            continue
        desc = data[p+0x70:p+0x70+52]
        fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
        entries[sw_id] = {
            'F3': fields[3],   # switch ID constant
            'F4': fields[4],   # sub-index block offset
            'F5': fields[5],   # sequence
            'F6': fields[6],   # total data bytes
            'F7': fields[7],   # total sample count (32B records)
            'F8': fields[8],   # same as F7
            'F9': fields[9],   # DATA OFFSET ★ float32 samples
            'F10': fields[10],
            'F11': fields[11],
        }
    return entries

def extract_curves(filepath, f9, f7, label=""):
    """从F9偏移提取功率曲线，返回 (timestamp, power_values) 列表"""
    bytes_per_sample = 32
    data_size = f7 * bytes_per_sample
    if data_size <= 0 or f9 <= 0 or f9 >= os.path.getsize(filepath):
        return []

    raw = read_at(filepath, f9, min(data_size, 10_000_000))

    curves = []
    # 每个32字节记录中有8个float32值
    # 尝试提取第1通道值（offset 0），并检测曲线的起始和结束
    samples = []
    for i in range(min(f7, len(raw) // 32)):
        rec = raw[i*32:(i+1)*32]
        if len(rec) < 32:
            break
        # 8个float32通道
        channels = [struct.unpack('<f', rec[j*4:(j+1)*4])[0] for j in range(8)]
        samples.append(channels)

    if not samples:
        return []

    # 过滤sentinel值（如-71.622）
    # 检测每个通道的有效值
    all_ch_data = [[] for _ in range(8)]
    for ch in range(8):
        vals = [s[ch] for s in samples]
        # 过滤明显的sentinel
        valid = [v for v in vals if abs(v) < 100 and v > -50]
        all_ch_data[ch] = valid

    # 找最好的通道（有最多有效数据的）
    best_ch = max(range(8), key=lambda ch: len(all_ch_data[ch]))
    vals = [s[best_ch] for s in samples]

    # 找曲线边界：从接近0开始，到接近0结束
    # 根据配置：~1032采样/事件，40ms间隔
    # 将数据分成多个事件

    # 先找有效值段
    # 跳过开头的sentinel
    sentinel_count = 0
    vals_list = list(vals)
    for i, v in enumerate(vals_list[:100]):
        if v < -10 or v > 50:
            sentinel_count += 1
        else:
            break

    if sentinel_count > 0 and sentinel_count < len(vals_list):
        vals_list = vals_list[sentinel_count:]

    # 简单分段：找接近0的断点
    segments = []
    seg_start = 0
    in_curve = False
    for i, v in enumerate(vals_list):
        if not in_curve and abs(v) > 0.02:
            in_curve = True
            seg_start = i
        elif in_curve and abs(v) < 0.02:
            # 检查是否真的是结束（后续也是0）
            trailing_zeros = sum(1 for j in range(i, min(i+10, len(vals_list)))
                               if abs(vals_list[j]) < 0.03)
            if trailing_zeros >= 5:
                seg = vals_list[seg_start:i+trailing_zeros]
                if len(seg) >= 30:
                    segments.append(seg)
                in_curve = False
                seg_start = i + trailing_zeros

    if in_curve:
        seg = vals_list[seg_start:]
        if len(seg) >= 30:
            segments.append(seg)

    return segments, best_ch, vals_list

def main():
    print("HBF 功率曲线完整解析器")
    print("=" * 70)

    os.makedirs(OUTPUT_DIR, exist_ok=True)

    for fpath, fname in [(POWER_1, "power1"), (POWER_2, "power2")]:
        print(f"\n{'='*70}")
        print(f"解析 {fname}: {os.path.basename(fpath)}")
        print(f"{'='*70}")

        entries = parse_directory(fpath)
        print(f"目录项: {len(entries)} 个道岔")

        # 按F7排序，先看数据最多的
        sorted_entries = sorted(entries.items(), key=lambda x: x[1]['F7'], reverse=True)

        all_events = []
        for sw_id, e in sorted_entries:
            f7, f9, f6 = e['F7'], e['F9'], e['F6']
            if f7 < 20 or f9 == 0 or f6 == 0:
                continue

            print(f"\n  {sw_id}: F7={f7}, F9=0x{f9:x}, F6={f6}")

            segments, best_ch, all_vals = extract_curves(fpath, f9, f7, sw_id)

            if segments:
                print(f"    提取到 {len(segments)} 条曲线 (通道{best_ch})")
                for si, seg in enumerate(segments[:5]):  # 只显示前5条
                    peak = max(seg)
                    peak_idx = seg.index(peak)
                    print(f"    曲线{si+1}: {len(seg)}点, peak={peak:.3f}KW @ idx={peak_idx}")
                    if len(seg) >= 20:
                        print(f"      前15: {[round(v,3) for v in seg[:15]]}")
                        mid = min(peak_idx + 5, len(seg) - 5)
                        print(f"      峰附近[{peak_idx}]: {[round(v,3) for v in seg[max(0,peak_idx-3):min(len(seg),peak_idx+8)]]}")
                        print(f"      后15: {[round(v,3) for v in seg[-15:]]}")

                # 保存为 JSON
                for si, seg in enumerate(segments):
                    event = {
                        'SwitchID': sw_id,
                        'Direction': 'Normal' if '-J' in sw_id else 'Reverse',  # J=定位 X=反位
                        'SampleCount': len(seg),
                        'SampleInterval': 0.04,  # 40ms from config
                        'Duration': round(len(seg) * 0.04, 2),
                        'Power': [[round(i * 0.04, 2), round(v, 3)] for i, v in enumerate(seg)],
                        'Source': fname,
                    }
                    all_events.append(event)
            else:
                # 没有分段出来，但可能有连续数据
                # 检查原始数据的统计
                valid = [v for v in all_vals if abs(v) < 50]
                if len(valid) > 50:
                    peak = max(valid)
                    print(f"    原始数据: {len(valid)}有效点, range={min(valid):.3f}~{peak:.3f}KW")
                    print(f"    前30: {[round(v,3) for v in valid[:30]]}")
                else:
                    print(f"    无有效曲线数据")

        # 保存所有事件到 JSON
        if all_events:
            out_path = os.path.join(OUTPUT_DIR, f'{fname}_curves.json')
            with open(out_path, 'w', encoding='utf-8') as f:
                json.dump(all_events, f, ensure_ascii=False, indent=2)
            print(f"\n  ✅ 保存 {len(all_events)} 个事件到 {out_path}")

    print(f"\n{'='*70}")
    print("✅ 解析完成")

if __name__ == '__main__':
    main()
