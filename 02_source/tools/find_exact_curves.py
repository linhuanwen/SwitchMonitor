#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
精确策略：F9 数据中找出真正的功率曲线
假设：32B/采样 = [8B timestamp][6×4B float32 通道]
只提取看起来像功率曲线（0-5KW范围内）的float32值
"""
import struct, sys, os, json
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

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
            'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
            'F6': fields[6], 'F7': fields[7], 'F8': fields[8],
            'F9': fields[9], 'F10': fields[10], 'F11': fields[11],
        }
    return entries

def extract_power_from_f9(filepath, f9, f7, sw_id):
    """
    从F9偏移读取数据，尝试多种32B结构解析，找出真正的功率曲线。
    返回 [(timestamp, power_values), ...]
    """
    data_size = f7 * 32
    raw = read_at(filepath, f9, min(data_size, 20_000_000))
    if len(raw) < 320:  # 至少10个采样
        return []

    n_records = min(f7, len(raw) // 32)

    # 尝试多种解析方式
    best_curves = []
    best_score = 0

    # 方式1: 8个float32，选取最好的通道
    for skip_first in [0, 1, 2]:  # 跳过前几个可能是timestamp的u32
        all_channels = [[] for _ in range(8 - skip_first)]
        for i in range(n_records):
            rec = raw[i*32:(i+1)*32]
            for ch in range(skip_first, 8):
                val = struct.unpack('<f', rec[ch*4:(ch+1)*4])[0]
                all_channels[ch - skip_first].append(val)

        for ch_idx, ch_vals in enumerate(all_channels):
            curves = segment_into_curves(ch_vals)
            for curve in curves:
                score = curve_score(curve)
                if score > best_score:
                    best_score = score
                    best_curves = [(curve, f'skip{skip_first}_ch{ch_idx+skip_first}')]

    # 方式2: 只读offset 8-11, 12-15, 16-19等特定位置
    for byte_offset in [0, 4, 8, 12, 16, 20, 24, 28]:
        vals = []
        for i in range(n_records):
            rec = raw[i*32:(i+1)*32]
            if byte_offset + 4 <= 32:
                val = struct.unpack('<f', rec[byte_offset:byte_offset+4])[0]
                vals.append(val)
        curves = segment_into_curves(vals)
        for curve in curves:
            score = curve_score(curve)
            if score > best_score:
                best_score = score
                best_curves = [(curve, f'offset{byte_offset}')]

    return best_curves

def curve_score(curve):
    """给曲线打分，越高越好"""
    if len(curve) < 30:
        return 0
    peak = max(curve)
    if peak < 0.3 or peak > 10:
        return 0
    peak_idx = curve.index(peak)
    if peak_idx < len(curve) * 0.03 or peak_idx > len(curve) * 0.9:
        return 0
    # 开头接近0
    head = curve[:max(3, len(curve)//15)]
    if sum(1 for v in head if abs(v) < 0.1) < len(head) * 0.3:
        return 0
    # 稳态段（峰值后）
    ss = curve[peak_idx + 5:min(len(curve), peak_idx + len(curve)//2)]
    if len(ss) > 0:
        stable = sum(1 for v in ss if 0.03 < v < 1.5) / len(ss)
    else:
        stable = 0
    # 结尾接近0
    tail = curve[-max(3, len(curve)//15):]
    tz = sum(1 for v in tail if abs(v) < 0.15) / len(tail)
    return peak * 10 + stable * 15 + tz * 10 + min(30, len(curve) / 5)

def segment_into_curves(vals):
    """将一维数据分割成独立曲线"""
    vals = [v for v in vals if abs(v) < 1e8]  # 过滤inf/nan
    segments = []
    in_curve = False
    start = 0
    min_len = 30
    for i, v in enumerate(vals):
        if not in_curve and abs(v) > 0.03:
            in_curve = True
            start = i
        elif in_curve and abs(v) < 0.03:
            # 确认是真正的结束（后10个都接近0）
            trailing = sum(1 for j in range(i, min(i+15, len(vals)))
                          if abs(vals[j]) < 0.05)
            if trailing >= 8:
                seg = vals[start:i]
                if len(seg) >= min_len:
                    segments.append(seg)
                in_curve = False
    if in_curve:
        seg = vals[start:]
        if len(seg) >= min_len:
            segments.append(seg)
    return segments

def main():
    print("HBF 功率曲线精确提取")
    print("=" * 70)

    entries = parse_directory(POWER_2)
    print(f"解析到 {len(entries)} 个目录项")

    # 按F7排序
    sorted_entries = sorted(entries.items(), key=lambda x: x[1]['F7'], reverse=True)

    all_events = []
    for sw_id, e in sorted_entries:
        f7, f9, f6 = e['F7'], e['F9'], e['F6']
        if f7 < 50 or f9 == 0:
            continue

        print(f"\n{sw_id}: F7={f7} F9=0x{f9:x} F6={f6}")

        curves = extract_power_from_f9(POWER_2, f9, f7, sw_id)

        if curves:
            for curve, method in curves[:3]:  # 每个道岔最多3条
                peak = max(curve)
                peak_idx = curve.index(peak)
                print(f"  ✅ {method}: {len(curve)}点 peak={peak:.3f}KW @ idx={peak_idx}")
                print(f"     前20: {[round(v,3) for v in curve[:20]]}")
                if peak_idx >= 20:
                    print(f"     峰附近: {[round(v,3) for v in curve[peak_idx-5:peak_idx+10]]}")
                print(f"     后20: {[round(v,3) for v in curve[-20:]]}")

                event = {
                    'SwitchID': sw_id,
                    'Direction': 'Normal' if '-J' in sw_id else 'Reverse',
                    'SampleCount': len(curve),
                    'SampleInterval': 0.04,
                    'Duration': round(len(curve) * 0.04, 2),
                    'PeakPowerKW': round(peak, 3),
                    'Power': [[round(i*0.04, 2), round(v, 3)] for i, v in enumerate(curve)],
                    'Method': method,
                }
                all_events.append(event)
        else:
            # 直接读取原始数据看统计
            raw = read_at(POWER_2, f9, min(f7*32, 65536))
            all_f32 = []
            for i in range(0, min(len(raw), 4096), 4):
                v = struct.unpack('<f', raw[i:i+4])[0]
                if abs(v) < 50:
                    all_f32.append(v)
            if all_f32:
                print(f"  📊 原始统计: {len(all_f32)}有效值, range={min(all_f32):.3f}~{max(all_f32):.3f}")
                nz = sum(1 for v in all_f32 if abs(v) > 0.01)
                print(f"     非零占比: {nz}/{len(all_f32)}")
                if nz > 10:
                    print(f"     前30: {[round(v,3) for v in all_f32[:30]]}")
            else:
                print(f"  ❌ 无有效数据")

    # 保存
    if all_events:
        os.makedirs(OUTPUT_DIR, exist_ok=True)
        out_path = os.path.join(OUTPUT_DIR, 'panyu_power_curves.json')
        with open(out_path, 'w', encoding='utf-8') as f:
            json.dump(all_events, f, ensure_ascii=False, indent=2)
        print(f"\n{'='*70}")
        print(f"✅ 保存 {len(all_events)} 条曲线到 {out_path}")

        # 统计
        switch_counts = {}
        for evt in all_events:
            sid = evt['SwitchID']
            switch_counts[sid] = switch_counts.get(sid, 0) + 1
        print(f"道岔分布: {dict(sorted(switch_counts.items()))}")
    else:
        print(f"\n❌ 未提取到有效曲线")

    print("\n✅ 完成")

if __name__ == '__main__':
    main()
