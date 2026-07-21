#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
批量转换岑村站 CSM2010 .dat 文件 → CSV（三水北格式）

道岔映射来源：DC.ini（自动解析）
  ZDJ9 转辙机，4组道岔，每组4台机器（尖轨J1/J2 + 芯轨X1/X2）
  每台机器：电流文件(A/B/C三相) + 功率文件(P)

用法：
    python convert_cencun.py

输出目录：
    03_raw_data/cencun_gcc_csv/   — GCC 数据
    03_raw_data/cencun_ccz_csv/   — CCZ 数据
    03_raw_data/config_cencun_gcc.json
    03_raw_data/config_cencun_ccz.json
"""

import struct
import csv
import json
import os
import re
import sys
from datetime import datetime
from pathlib import Path

BLOCK_SIZE = 4014
HEADER_SIZE = 14
DATA_START = 100032


def derive_switch_mapping_from_dc_ini(dc_ini_path: Path):
    """Parse DC.ini (GBK-encoded) and derive switch mapping.

    Returns list of (switch_id, current_file_index, power_file_index) tuples,
    sorted by current file index (matching DC.ini channel order).

    DC.ini channel names: {switch}-{J|X}{sub?}-{A|B|C|P}
      e.g. "1-J1-A" → switch=1, beam=J, sub=1, phase=A
    """
    if not dc_ini_path.exists():
        return None

    chan_re = re.compile(r'^\s*(\d+)\s*=\s*([^,]+?)\s*,\s*(\d+)\s*,\s*(\d+)')
    name_re = re.compile(r'^(\d+)-([JX])(\d*)-([ABCP])$')

    # machine_id → {current_min_idx, power_min_idx}
    machine_info = {}

    with open(dc_ini_path, 'r', encoding='gbk', errors='replace') as f:
        for line in f:
            m = chan_re.match(line)
            if not m:
                continue
            name = m.group(2).strip()
            nm = name_re.match(name)
            if not nm:
                continue

            file_idx = int(m.group(4))
            phase = nm.group(4)
            switch_no = nm.group(1)
            beam_type = nm.group(2)
            sub_id = nm.group(3)
            machine_id = f"{switch_no}-{beam_type}{sub_id}"

            if machine_id not in machine_info:
                machine_info[machine_id] = {'current': 999, 'power': 999}

            if phase in ('A', 'B', 'C'):
                if file_idx < machine_info[machine_id]['current']:
                    machine_info[machine_id]['current'] = file_idx
            elif phase == 'P':
                if file_idx < machine_info[machine_id]['power']:
                    machine_info[machine_id]['power'] = file_idx

    # Build ordered list — sort by current file index
    mapping = []
    for machine_id in sorted(machine_info.keys(),
                             key=lambda m: machine_info[m].get('current', 999)):
        info = machine_info[machine_id]
        if info['current'] == 999:
            continue  # skip machines with no current channel
        mapping.append((machine_id, info['current'], info['power']))

    return mapping


def parse_dat_to_csv(input_path, output_path):
    """解析单个 .dat 文件并输出 CSV，返回统计信息"""
    input_path = Path(input_path)
    output_path = Path(output_path)

    with open(input_path, 'rb') as f:
        data = f.read()

    events = []
    off = DATA_START
    while off + BLOCK_SIZE <= len(data):
        hdr = data[off:off + HEADER_SIZE]
        ts = struct.unpack('<I', hdr[0:4])[0]
        flags = struct.unpack('<I', hdr[4:8])[0]
        sample_rate = struct.unpack('<H', hdr[10:12])[0]
        sample_count = struct.unpack('<H', hdr[12:14])[0]

        if not (1_500_000_000 < ts < 2_000_000_000
                and sample_rate == 25
                and 10 < sample_count < 2000):
            off += BLOCK_SIZE
            continue

        n_floats = (BLOCK_SIZE - HEADER_SIZE) // 4
        floats = struct.unpack(
            f'<{n_floats}f',
            data[off + HEADER_SIZE:off + HEADER_SIZE + n_floats * 4]
        )
        samples = list(floats[:sample_count])

        events.append({
            'timestamp': ts,
            'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
            'phase': flags,
            'sample_count': sample_count,
            'samples': samples,
        })
        off += BLOCK_SIZE

    if not events:
        return {'blocks': 0, 'events': 0, 'min_sc': 0, 'max_sc': 0}

    max_count = max(e['sample_count'] for e in events)
    fieldnames = ['timestamp', 'datetime', 'phase'] + [f's{i}' for i in range(max_count)]

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, 'w', newline='', encoding='utf-8') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for e in events:
            row = {
                'timestamp': e['timestamp'],
                'datetime': e['datetime'],
                'phase': e['phase'],
            }
            for i, v in enumerate(e['samples']):
                row[f's{i}'] = v
            writer.writerow(row)

    ts_groups = {}
    for e in events:
        ts_groups.setdefault(e['timestamp'], []).append(e)

    return {
        'blocks': len(events),
        'events': len(ts_groups),
        'min_sc': min(e['sample_count'] for e in events),
        'max_sc': max(e['sample_count'] for e in events),
    }


def build_switch_groups(switch_mapping):
    """根据 switch_mapping 构建 switchGroups 配置"""
    groups = []
    for sw_id, current_idx, power_idx in switch_mapping:
        groups.append({
            'id': sw_id,
            'label': sw_id,
            'dataFileIndex': current_idx,
            'dataFileIndexPower': power_idx,
        })
    return groups


def build_config(src_label, out_dir, project_root, switch_mapping, site_config):
    """生成与三水北格式一致的 config.json"""
    switch_type = (site_config or {}).get('switchType', 'ZDJ9')
    station_name = (site_config or {}).get('name', f'岑村站({src_label})')

    return {
        '_comment': f'{station_name} {src_label} — {switch_type}转辙机 | 映射来源 DC.ini',
        'stationId': f'cencun_{src_label.lower()}',
        'stationName': f'{station_name}({src_label})',
        'switchType': switch_type,
        'switchGroups': build_switch_groups(switch_mapping),
        'dataSourceDir': str(out_dir.relative_to(project_root)),
        'parsedDataDir': f'.\\parsed_data\\cencun_{src_label.lower()}',
        'scanInterval': 5,
        'alarmThresholds': {
            'current': {'enabled': True, 'value': 2.0, 'unit': 'A'},
            'power':   {'enabled': True, 'value': 1.5, 'unit': 'KW'},
        },
        'chartColors': {
            'currentA': '#55FF55',
            'currentB': '#FF5555',
            'currentC': '#CC44CC',
            'power': '#55FF55',
            'thresholdLine': '#FF4444',
            'background': '#3c3c3c',
            'gridLine': '#6a6a6a',
            'textColor': '#BBBBBB',
            'refCurrentA': '#00FFFF',
            'refCurrentB': '#FF5555',
            'refCurrentC': '#FFFF00',
            'refPower': '#FF5555',
        },
        'ui': {
            'sidebarWidthPercent': 18,
            'dateFormat': 'yyyy/MM/dd',
            'xAxisDefaultMax': 14,
            'xAxisExtendedMax': 30,
        },
    }


def main():
    project_root = Path(__file__).resolve().parent.parent.parent
    raw_dir = project_root / '03_raw_data'
    cencun_dir = raw_dir / 'cencun'

    # Load site config
    site_config = None
    site_json = cencun_dir / 'site.json'
    if site_json.exists():
        with open(site_json, 'r', encoding='utf-8') as f:
            site_config = json.load(f)

    # Derive switch mapping from DC.ini
    dc_ini = cencun_dir / 'DC.ini'
    switch_mapping = derive_switch_mapping_from_dc_ini(dc_ini)
    if not switch_mapping:
        print("ERROR: Cannot derive switch mapping — DC.ini not found or empty")
        sys.exit(1)

    print(f"Derived {len(switch_mapping)} switch machines from DC.ini:")
    for sw_id, cur_i, pow_i in switch_mapping:
        print(f"  {sw_id:<6} → 电流[{cur_i:>2}]  功率[{pow_i:>2}]")

    # Build path index for lookup
    idx_to_sw = {}
    for sw_id, cur_i, pow_i in switch_mapping:
        if cur_i != 999:
            idx_to_sw[cur_i] = ('电流', sw_id)
        if pow_i != 999:
            idx_to_sw[pow_i] = ('功率', sw_id)

    raw_cencun_dir = raw_dir / '岑村E-CSM2010-data'
    sources = {
        'GCC': raw_cencun_dir / 'GCC' / 'SwitchCurve',
        'CCZ': raw_cencun_dir / 'CCZ' / 'SwitchCurve',
    }

    all_results = {}

    for src_label, src_dir in sources.items():
        if not src_dir.exists():
            print(f'[跳过] 目录不存在: {src_dir}')
            continue

        out_dir = raw_dir / f'cencun_{src_label.lower()}_csv'
        print(f'\n{"="*60}')
        print(f'> {src_label} 数据转换')
        print(f'  源目录: {src_dir}')
        print(f'  输出到: {out_dir}')
        print(f'{"="*60}')

        dat_files = sorted(src_dir.glob('SwitchCurve(*).dat'))
        print(f'  共 {len(dat_files)} 个 .dat 文件\n')

        results = {}
        for dat_path in dat_files:
            name = dat_path.stem
            idx = name.split('(')[1].rstrip(')')
            csv_name = f'SwitchCurve({idx}).csv'
            csv_path = out_dir / csv_name

            stats = parse_dat_to_csv(dat_path, csv_path)
            if stats['blocks'] > 0:
                sw_info = ''
                if int(idx) in idx_to_sw:
                    phase_type, sw_id = idx_to_sw[int(idx)]
                    sw_info = f' [{phase_type}-{sw_id}]'

                print(f'  [{idx:>2}] → {csv_name:<22} | {stats["blocks"]:>4}块 {stats["events"]:>4}事件'
                      f'  samples:{stats["min_sc"]}~{stats["max_sc"]}{sw_info}')
                results[int(idx)] = stats
            else:
                print(f'  [{idx:>2}] → (空，跳过)')

        all_results[src_label] = results

    # ========== 生成配置文件 ==========
    for src_label in ['GCC', 'CCZ']:
        if src_label not in all_results or not all_results[src_label]:
            continue

        out_dir = raw_dir / f'cencun_{src_label.lower()}_csv'
        config = build_config(src_label, out_dir, project_root, switch_mapping, site_config)

        config_path = raw_dir / f'config_cencun_{src_label.lower()}.json'
        with open(config_path, 'w', encoding='utf-8') as f:
            json.dump(config, f, ensure_ascii=False, indent=2)
        print(f'\n[OK] 配置: {config_path}')

    # ========== 汇总 ==========
    print(f'\n{"="*60}')
    print('转换完成')
    print(f'{"="*60}')
    for label, results in all_results.items():
        if results:
            total_files = len(results)
            total_blocks = sum(r['blocks'] for r in results.values())
            total_events = sum(r['events'] for r in results.values())
            print(f'  {label}: {total_files} CSV, {total_blocks} 数据块, {total_events} 事件')

    print(f'\n道岔映射（来自 DC.ini）：')
    for sw_id, cur_i, pow_i in switch_mapping:
        print(f'  {sw_id:<6} → 电流文件[{cur_i:>2}]  功率文件[{pow_i:>2}]')


if __name__ == '__main__':
    main()
