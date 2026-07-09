#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Parse shiqi folder data (道岔监测实时采集数据).

Each switch operation event produces 5 files:
  .elc    -> 3-phase current  (A=, B=, C=)
  .vol    -> 3-phase voltage  (A=, B=, C=)
  .pow    -> active power     (single line)
  .factor -> power factor     (single line), scale = 1000
  .rst    -> unknown result   (single line)

Usage:
    python parse_shiqi.py
"""

import os
import re
import csv
import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

# 数据路径：脚本位于 02_source/tools/，数据在 03_raw_data/shiqi/
_PROJECT = Path(__file__).parent.parent.parent
ROOT = _PROJECT / '03_raw_data' / 'shiqi'
OUT_DIR = _PROJECT / '03_raw_data' / 'shiqi_parsed'


def parse_param_file(path):
    """Parse a single .elc/.vol/.pow/.factor/.rst file.

    Returns dict: phase -> list of float values.
    For single-line files, key is '_'.
    """
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        content = f.read()

    result = {}
    for raw_line in content.split('\n'):
        line = raw_line.strip()
        if not line:
            continue
        if '=' in line:
            phase, data = line.split('=', 1)
            phase = phase.strip()
        else:
            phase = '_'
            data = line
        try:
            vals = [float(x) for x in data.split(',') if x.strip()]
        except ValueError as e:
            print(f'  Warning: parse error in {path}: {e}')
            vals = []
        result[phase] = vals
    return result


def parse_filename(filename):
    """Extract timestamp and ids from filename like:
    20210929042606480_<device>_<event>.elc
    Returns (dt_str, dt, device_id, event_id).
    """
    # Remove extension
    name = filename.rsplit('.', 1)[0]
    parts = name.split('_')
    if len(parts) < 3:
        return None, None, None, None

    dt_str = parts[0]
    # Parse 20210929042606480 -> 2021-09-29 04:26:06.480
    try:
        dt = datetime.strptime(dt_str, '%Y%m%d%H%M%S%f')
    except ValueError:
        dt = None

    device_id = parts[1]
    event_id = parts[2]
    return dt_str, dt, device_id, event_id


def collect_events():
    """Walk ROOT and group files by event."""
    events = defaultdict(lambda: {
        'dt_str': None,
        'dt': None,
        'device_id': None,
        'event_id': None,
        'files': {}
    })

    for root, dirs, files in os.walk(ROOT):
        for fname in files:
            ext = fname.rsplit('.', 1)[-1].lower()
            if ext not in ('elc', 'vol', 'pow', 'factor', 'rst'):
                continue

            path = Path(root) / fname
            dt_str, dt, device_id, event_id = parse_filename(fname)
            if dt_str is None:
                continue

            key = f'{device_id}_{event_id}'
            ev = events[key]
            ev['dt_str'] = dt_str
            ev['dt'] = dt
            ev['device_id'] = device_id
            ev['event_id'] = event_id
            ev['files'][ext] = path

    return events


def analyze_event(ev):
    """Parse all files for one event and return structured data."""
    data = {}
    lengths = {}
    for ext, path in ev['files'].items():
        data[ext] = parse_param_file(path)
        for phase, vals in data[ext].items():
            lengths[f'{ext}:{phase}'] = len(vals)

    # Determine common length (use min to handle mismatched .factor length)
    all_lengths = list(lengths.values())
    common_len = min(all_lengths) if all_lengths else 0

    # Build rows
    rows = []
    for i in range(common_len):
        row = {'sample_index': i}

        # Current (3 phases)
        for ph in ['A', 'B', 'C']:
            row[f'current_{ph}'] = data.get('elc', {}).get(ph, [None] * common_len)[i]

        # Voltage (3 phases)
        for ph in ['A', 'B', 'C']:
            row[f'voltage_{ph}'] = data.get('vol', {}).get(ph, [None] * common_len)[i]

        # Power, factor, rst (single line)
        row['power'] = data.get('pow', {}).get('_', [None] * common_len)[i]
        row['factor'] = data.get('factor', {}).get('_', [None] * common_len)[i]
        row['rst'] = data.get('rst', {}).get('_', [None] * common_len)[i]

        rows.append(row)

    return {
        'data': data,
        'rows': rows,
        'lengths': lengths,
        'common_len': common_len,
    }


def write_event_csv(ev, parsed, out_dir):
    """Write one CSV per event."""
    safe_dt = ev['dt'].strftime('%Y%m%d_%H%M%S') if ev['dt'] else 'unknown'
    filename = f"{safe_dt}_{ev['device_id']}_{ev['event_id']}.csv"
    out_path = out_dir / filename

    fieldnames = ['sample_index',
                  'current_A', 'current_B', 'current_C',
                  'voltage_A', 'voltage_B', 'voltage_C',
                  'power', 'factor', 'rst']

    with open(out_path, 'w', newline='', encoding='utf-8') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(parsed['rows'])

    return out_path


def compute_ranges(data, common_len):
    """Compute min/max/mean for each parameter."""
    stats = {}
    for ext, phases in data.items():
        for phase, vals in phases.items():
            if not vals:
                continue
            v = vals[:common_len]
            if None in v:
                continue
            key = f'{ext}:{phase}'
            stats[key] = {
                'min': min(v),
                'max': max(v),
                'mean': sum(v) / len(v),
                'len': len(v),
            }
    return stats


def main():
    print(f'Scanning {ROOT} ...')
    events = collect_events()
    print(f'Found {len(events)} events')

    out_dir = OUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)

    summary_rows = []
    all_rows = []  # combined long-format

    for idx, (key, ev) in enumerate(sorted(events.items(), key=lambda x: x[1]['dt_str'] or ''), 1):
        parsed = analyze_event(ev)
        stats = compute_ranges(parsed['data'], parsed['common_len'])

        # Write per-event CSV
        out_path = write_event_csv(ev, parsed, out_dir)

        # Summary row
        summary_rows.append({
            'event_no': idx,
            'device_id': ev['device_id'],
            'event_id': ev['event_id'],
            'datetime': ev['dt'].strftime('%Y-%m-%d %H:%M:%S.%f')[:-3] if ev['dt'] else '',
            'common_samples': parsed['common_len'],
            'lengths': json.dumps(parsed['lengths']),
            'current_A_max': stats.get('elc:A', {}).get('max'),
            'current_B_max': stats.get('elc:B', {}).get('max'),
            'current_C_max': stats.get('elc:C', {}).get('max'),
            'voltage_A_max': stats.get('vol:A', {}).get('max'),
            'voltage_B_max': stats.get('vol:B', {}).get('max'),
            'voltage_C_max': stats.get('vol:C', {}).get('max'),
            'power_max': stats.get('pow:_', {}).get('max'),
            'factor_max': stats.get('factor:_', {}).get('max'),
            'rst_max': stats.get('rst:_', {}).get('max'),
            'rst_min': stats.get('rst:_', {}).get('min'),
            'csv_file': out_path.name,
        })

        # Combined rows
        for row in parsed['rows']:
            all_rows.append({
                'event_no': idx,
                'device_id': ev['device_id'],
                'event_id': ev['event_id'],
                'datetime': ev['dt'].strftime('%Y-%m-%d %H:%M:%S.%f')[:-3] if ev['dt'] else '',
                **row
            })

        if idx <= 3 or idx % 100 == 0:
            print(f'  [{idx}/{len(events)}] {ev["dt"]} device={ev["device_id"]} samples={parsed["common_len"]}')

    # Write summary CSV
    summary_path = out_dir / '_summary.csv'
    with open(summary_path, 'w', newline='', encoding='utf-8') as f:
        fieldnames = ['event_no', 'device_id', 'event_id', 'datetime', 'common_samples',
                      'current_A_max', 'current_B_max', 'current_C_max',
                      'voltage_A_max', 'voltage_B_max', 'voltage_C_max',
                      'power_max', 'factor_max', 'rst_max', 'rst_min',
                      'lengths', 'csv_file']
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(summary_rows)

    # Write combined CSV
    combined_path = out_dir / '_combined.csv'
    with open(combined_path, 'w', newline='', encoding='utf-8') as f:
        fieldnames = ['event_no', 'device_id', 'event_id', 'datetime', 'sample_index',
                      'current_A', 'current_B', 'current_C',
                      'voltage_A', 'voltage_B', 'voltage_C',
                      'power', 'factor', 'rst']
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(all_rows)

    print(f'\nDone.')
    print(f'  Summary:     {summary_path}')
    print(f'  Combined:    {combined_path}')
    print(f'  Per-event:   {out_dir}/*.csv ({len(events)} files)')


if __name__ == '__main__':
    main()
