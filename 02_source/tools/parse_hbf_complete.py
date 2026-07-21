#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 完整解析器 v3 — 番禺站道岔动作曲线数据提取。
已验证的映射关系:
  F9索引区 → u32[0]归一化/256 → curve_info标签索引 → 曲线数据
  曲线在 tag + 0x32E8(A相) / +0x4310(B相) / +0x5338(C相), 间距0x1028
  电流文件标记: 0x3277 (bytes 13-14 of 32B record)
  功率文件标记: 0x1227 (bytes 13-14 of 32B record)
"""
import struct, os, sys, json
from collections import defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

# ── 路径配置 ──
RAW_DIR = Path(r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu")
OUT_DIR = Path(r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves")

CURRENT_FILES = sorted((RAW_DIR / "道岔动作电流曲线").glob('*.hbf'))
POWER_FILES = sorted((RAW_DIR / "道岔动作功率曲线").glob('*.hbf'))

MAGIC = b'hhcsmfzz'
PHASE_GAP = 0x1028
PHASE_OFFSETS = [0x32E8, 0x32E8 + PHASE_GAP, 0x32E8 + 2 * PHASE_GAP]
PHASE_NAMES = ['A', 'B', 'C']

# 电流文件用0x3277, 功率文件用0x1227
MARKER_CURRENT = b'\x77\x32'
MARKER_POWER = b'\x27\x12'


def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)


def parse_header(filepath):
    """解析HBF文件头, 返回 {switch_name: {fields...}}"""
    header = read_at(filepath, 0, 0x200000)
    if header[:8] != MAGIC:
        return {}

    import re
    switches = {}
    for m in re.finditer(rb'(\d{1,2}-[JX])', header[:0x200000]):
        pos = m.start()
        name = m.group(0).decode('ascii')
        # 检查描述符是否在有效范围 (+0x70,+0x70+52)
        desc_offset = pos + 0x70
        if desc_offset + 52 > 0x200000:
            continue
        desc = header[desc_offset:desc_offset + 52]
        fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
        switches[name] = {
            'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
            'F6': fields[6], 'F7': fields[7], 'F8': fields[8],
            'F9': fields[9], 'F11': fields[11],
        }
    return switches


def find_curve_info_tags(filepath):
    """查找所有curve_info标签"""
    size = os.path.getsize(filepath)
    tag = 'curve_info'.encode('utf-16-le')
    positions = []
    CHUNK = 0x100000
    with open(filepath, 'rb') as f:
        offset = 0
        while offset < size:
            f.seek(offset)
            chunk = f.read(CHUNK + 200)
            if len(chunk) < len(tag):
                break
            cp = 0
            while True:
                found = chunk.find(tag, cp)
                if found == -1:
                    break
                positions.append(offset + found)
                cp = found + 100
            offset += CHUNK
    return positions


def parse_f9_records(filepath, f9_offset, f9_size, marker):
    """解析F9索引区, 返回 [{seq, u32[0..7]}, ...]"""
    raw = read_at(filepath, f9_offset, f9_size)
    records = []
    # 扫描所有32B对齐的记录
    for i in range(f9_size // 32):
        rec_start = i * 32
        if rec_start + 15 > len(raw):
            break
        if raw[rec_start + 13:rec_start + 15] == marker:
            u32s = struct.unpack_from('<8I', raw, rec_start)
            records.append({'seq': i, 'u32': list(u32s)})
    return records


def extract_curve(raw_chunk, search_start=0):
    """从raw_chunk中提取一条电流/功率曲线"""
    n_f = len(raw_chunk) // 4
    if n_f < 50:
        return None
    f32 = struct.unpack_from(f'<{n_f}f', raw_chunk, 0)

    i = search_start // 4
    while i < n_f and abs(f32[i]) < 0.02:
        i += 1
    if i >= n_f:
        return None

    s = i
    vals = []
    while i < n_f and abs(f32[i]) > 0.005:
        vals.append(round(f32[i], 6))
        i += 1

    if 50 <= len(vals) <= 600:
        peak = max(abs(v) for v in vals)
        min_peak = 0.3  # 功率曲线峰值也可以很低
        if peak >= min_peak and peak <= 50.0:
            return {
                'len': len(vals),
                'peak': round(peak, 4),
                'values': vals,
            }
    return None


def extract_event_curves(filepath, tag_pos):
    """从指定tag提取ABC三相曲线"""
    region_start = tag_pos + PHASE_OFFSETS[0]
    region_size = PHASE_OFFSETS[2] - PHASE_OFFSETS[0] + 0x2000
    raw = read_at(filepath, region_start, region_size)

    curves = {}
    for ci, po in enumerate(PHASE_OFFSETS):
        local_off = po - PHASE_OFFSETS[0]
        c = extract_curve(raw, local_off)
        if c:
            c['phase'] = PHASE_NAMES[ci]
            curves[PHASE_NAMES[ci]] = c

    return curves


def process_file(filepath, file_type='current'):
    """处理一个HBF文件"""
    fname = filepath.name
    fpath = str(filepath)
    size = os.path.getsize(fpath)
    marker = MARKER_CURRENT if file_type == 'current' else MARKER_POWER

    print(f"\n{'='*60}")
    print(f"[{file_type}] {fname} ({size:,} bytes)")
    print(f"{'='*60}")

    # 1. 解析头部
    switches = parse_header(fpath)
    print(f"开关: {len(switches)}")
    if not switches:
        return {}

    # 2. 找标签
    tags = find_curve_info_tags(fpath)
    print(f"curve_info标签: {len(tags)}")
    if not tags:
        return {}

    # 3. 按F9偏移分组
    f9_groups = defaultdict(list)
    for name, info in switches.items():
        if info['F9'] > 0 and info['F6'] > 0:
            f9_groups[info['F9']].append((name, info))

    # 去重: 同一F9偏移只处理一次
    events_by_switch = defaultdict(list)

    for f9_off, group in f9_groups.items():
        f7 = group[0][1]['F7']
        f6 = group[0][1]['F6']
        switch_names = [g[0] for g in group]

        # 解析F9记录
        records = parse_f9_records(fpath, f9_off, f6, marker)
        if not records:
            continue

        # u32[0]归一化
        u0_vals = [r['u32'][0] for r in records]
        u0_min = min(u0_vals)
        F11 = group[0][1]['F11']  # 步长, 通常是0x100=256
        step = F11 if F11 > 0 else 256

        print(f"  F9@0x{f9_off:08X}: {len(records)}条 → {switch_names}")

        for r in records:
            tag_idx = (r['u32'][0] - u0_min) // step
            if tag_idx < 0 or tag_idx >= len(tags):
                continue

            tag_pos = tags[tag_idx]
            curves = extract_event_curves(fpath, tag_pos)

            if not curves:
                continue

            event = {
                'tag_idx': tag_idx,
                'tag_pos': tag_pos,
                'f9_seq': r['seq'],
                'f9_u32': r['u32'],
                'n_phases': len(curves),
                'phases': sorted(curves.keys()),
                'curves': {
                    phase: {
                        'len': c['len'],
                        'peak': c['peak'],
                    }
                    for phase, c in curves.items()
                },
                'values': {
                    phase: c['values']
                    for phase, c in curves.items()
                },
            }
            for sw_name in switch_names:
                events_by_switch[sw_name].append(event)

    # 统计
    for sw_name in sorted(events_by_switch.keys()):
        events = events_by_switch[sw_name]
        n_3 = sum(1 for e in events if e['n_phases'] == 3)
        n_2 = sum(1 for e in events if e['n_phases'] == 2)
        n_1 = sum(1 for e in events if e['n_phases'] == 1)
        print(f"  → {sw_name}: {len(events)}事件 (3相:{n_3}, 2相:{n_2}, 1相:{n_1})")

    return events_by_switch


def save_results(all_events, label):
    """保存解析结果"""
    os.makedirs(str(OUT_DIR), exist_ok=True)

    for sw_name, events in sorted(all_events.items()):
        if not events:
            continue

        output = {
            'switch': sw_name,
            'source': label,
            'total_events': len(events),
            'events': [
                {
                    'tag_idx': e['tag_idx'],
                    'tag_pos': f"0x{e['tag_pos']:08X}",
                    'n_phases': e['n_phases'],
                    'phases': e['phases'],
                    'curves': e['curves'],
                    'values': e['values'],
                }
                for e in events
            ],
        }

        out_path = OUT_DIR / f"{sw_name}_{label}.json"
        with open(out_path, 'w', encoding='utf-8') as f:
            json.dump(output, f, ensure_ascii=False)
        size_kb = os.path.getsize(out_path) / 1024
        print(f"  保存: {out_path.name} ({size_kb:.0f} KB, {len(events)}事件)")

    # 索引
    summary = {
        'label': label,
        'switches': {sw: len(evts) for sw, evts in sorted(all_events.items()) if evts},
        'total_events': sum(len(evts) for evts in all_events.values()),
        'total_switches': sum(1 for evts in all_events.values() if evts),
    }
    return summary


def main():
    print("=" * 60)
    print("HBF 完整解析器 v3 — 番禺站")
    print("=" * 60)

    os.makedirs(str(OUT_DIR), exist_ok=True)
    all_summaries = []

    # ── 处理电流文件 ──
    print("\n[电流曲线]")
    for cf in CURRENT_FILES:
        events = process_file(cf, 'current')
        if events:
            s = save_results(events, f"current_{cf.stem}")
            all_summaries.append(s)

    # ── 处理功率文件 ──
    print("\n[功率曲线]")
    for pf in POWER_FILES:
        events = process_file(pf, 'power')
        if events:
            s = save_results(events, f"power_{pf.stem}")
            all_summaries.append(s)

    # ── 最终汇总 ──
    print(f"\n{'='*60}")
    print("汇总")
    print(f"{'='*60}")
    total_events = sum(s['total_events'] for s in all_summaries)
    all_switches = set()
    for s in all_summaries:
        all_switches.update(s['switches'].keys())
    print(f"文件数: {len(all_summaries)}")
    print(f"总事件: {total_events}")
    print(f"开关: {sorted(all_switches)}")
    print(f"输出: {OUT_DIR}")

    # 写总索引
    full_summary = {
        'files': all_summaries,
        'total_events': total_events,
        'switches': sorted(all_switches),
    }
    idx_path = OUT_DIR / "_index.json"
    with open(idx_path, 'w', encoding='utf-8') as f:
        json.dump(full_summary, f, ensure_ascii=False, indent=2)
    print(f"索引: {idx_path}")
    print("完成!")


if __name__ == '__main__':
    main()
