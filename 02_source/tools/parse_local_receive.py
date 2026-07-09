#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Parse local receive directories:
  03_raw_data/本地接收目录扳动   -> digital switch status events
  03_raw_data/本地接收目录表示   -> analog indication measurements

Usage:
    python parse_local_receive.py
"""

import os
import csv
import struct
from pathlib import Path
from datetime import datetime
from collections import defaultdict

# 数据路径：脚本位于 02_source/tools/，数据应在 03_raw_data/ 下
# ⚠️ 注意：本地接收目录扳动/ 和 本地接收目录表示/ 两个数据目录在项目迁移时未包含，
# 如果这些目录不存在，脚本将跳过处理。请将原始数据放入 03_raw_data/ 对应子目录。
_PROJECT = Path(__file__).parent.parent.parent
BASE = _PROJECT / '03_raw_data'
DIGIT_DIR = BASE / '本地接收目录扳动'
ANALOG_DIR = BASE / '本地接收目录表示'
OUT_DIGIT = BASE / '本地接收目录扳动_parsed'
OUT_ANALOG = BASE / '本地接收目录表示_parsed'


def parse_digit_file(path):
    """Parse Digit(*).dat files (digital point events).

    Record format (variable length):
      - 4 bytes timestamp (little-endian uint32)
      - 2 bytes record_type / count (big-endian uint16)
      - record_type * 2 bytes point data (big-endian uint16 each)
      - 1 byte checksum/padding
      Total size = 7 + 2 * record_type

    Each point data word:
      - high byte: state/quality code
      - low byte:  point ID
    """
    with open(path, 'rb') as f:
        data = f.read()

    if len(data) < 24:
        return []

    ts_start = struct.unpack('<I', data[12:16])[0]
    ts_end = struct.unpack('<I', data[16:20])[0]

    # Locate first data record (skip header, allow ts == ts_start)
    data_start = None
    for i in range(1000, len(data) - 8):
        ts = struct.unpack('<I', data[i:i+4])[0]
        if ts_start - 60 <= ts <= ts_end + 60:
            typ = struct.unpack('>H', data[i+4:i+6])[0]
            if 1 <= typ <= 20:
                data_start = i
                break

    if not data_start:
        return []

    rows = []
    i = data_start
    while i + 7 <= len(data):
        ts = struct.unpack('<I', data[i:i+4])[0]
        if not (ts_start - 60 <= ts <= ts_end + 60):
            break
        typ = struct.unpack('>H', data[i+4:i+6])[0]
        if typ > 20 or typ == 0:
            break
        record_size = 7 + 2 * typ
        if i + record_size > len(data):
            break
        vals = struct.unpack(f'>{typ}H', data[i+6:i+6 + typ * 2])
        for v in vals:
            rows.append({
                'timestamp': ts,
                'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
                'record_type': typ,
                'raw_value': v,
                'state_byte': (v >> 8) & 0xff,
                'point_id': v & 0xff,
            })
        i += record_size

    return rows


def parse_analog_file(path):
    """Parse DCBSDYAnalog(*).dat files (analog measurements).

    Records are variable length, starting with:
      - 4 bytes timestamp (little-endian uint32)
      - 2 bytes channel (little-endian uint16, usually 0)
      - 2 bytes type / measurement point (little-endian uint16)
      - 4 bytes raw int32
      - 4 bytes float value (little-endian float32)
      - additional bytes depending on type

    We extract timestamp, channel, type and the float value at offset +12.
    """
    with open(path, 'rb') as f:
        data = f.read()

    if len(data) < 24:
        return []

    ts_start = struct.unpack('<I', data[12:16])[0]
    ts_end = struct.unpack('<I', data[16:20])[0]

    # Find all valid timestamp positions
    positions = []
    for i in range(1000, len(data) - 16):
        ts = struct.unpack('<I', data[i:i+4])[0]
        if ts_start - 60 <= ts <= ts_end + 60:
            ch = struct.unpack('<H', data[i+4:i+6])[0]
            typ = struct.unpack('<H', data[i+6:i+8])[0]
            if ch <= 1000 and 1 <= typ <= 100:
                positions.append((i, ts, ch, typ))

    # Deduplicate positions at least 8 bytes apart
    unique = []
    for p in sorted(positions):
        if not unique or p[0] - unique[-1][0] >= 8:
            unique.append(p)

    rows = []
    for off, ts, ch, typ in unique:
        if off + 16 > len(data):
            continue
        val = struct.unpack('<f', data[off+12:off+16])[0]
        rows.append({
            'timestamp': ts,
            'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
            'channel': ch,
            'measurement_type': typ,
            'value': val,
        })

    return rows


def process_folder(in_dir, out_dir, parser, name):
    if not in_dir.exists():
        print(f'Folder not found: {in_dir}')
        return

    out_dir.mkdir(parents=True, exist_ok=True)
    files = sorted(in_dir.glob('*.dat'))
    print(f'\nProcessing {name}: {len(files)} files')

    total_rows = 0
    for path in files:
        rows = parser(path)
        if not rows:
            print(f'  {path.name}: no data parsed')
            continue

        out_csv = out_dir / (path.stem + '.csv')
        with open(out_csv, 'w', newline='', encoding='utf-8') as f:
            writer = csv.DictWriter(f, fieldnames=rows[0].keys())
            writer.writeheader()
            writer.writerows(rows)

        total_rows += len(rows)
        if path == files[0] or path == files[-1] or len(files) <= 5:
            print(f'  {path.name}: {len(rows)} rows -> {out_csv.name}')

    print(f'  Total {name} rows: {total_rows}')


def process_mixed_folder(in_dir, out_digit, out_analog, name):
    """Process a folder that may contain both Digit and DCBSDYAnalog files."""
    if not in_dir.exists():
        print(f'Folder not found: {in_dir}')
        return

    out_digit.mkdir(parents=True, exist_ok=True)
    out_analog.mkdir(parents=True, exist_ok=True)
    files = sorted(in_dir.glob('*.dat'))
    print(f'\nProcessing {name}: {len(files)} files')

    digit_rows = 0
    analog_rows = 0
    for path in files:
        if path.name.startswith('Digit'):
            rows = parse_digit_file(path)
            out_csv = out_digit / (path.stem + '.csv')
            label = 'Digital'
            digit_rows += len(rows)
        elif path.name.startswith('DCBSDYAnalog'):
            rows = parse_analog_file(path)
            out_csv = out_analog / (path.stem + '.csv')
            label = 'Analog'
            analog_rows += len(rows)
        else:
            print(f'  {path.name}: unknown prefix, skipped')
            continue

        if not rows:
            print(f'  {path.name}: no data parsed')
            continue

        with open(out_csv, 'w', newline='', encoding='utf-8') as f:
            writer = csv.DictWriter(f, fieldnames=rows[0].keys())
            writer.writeheader()
            writer.writerows(rows)

        if path == files[0] or path == files[-1] or len(files) <= 5:
            print(f'  {path.name}: {len(rows)} {label} rows -> {out_csv.name}')

    print(f'  Total Digital rows: {digit_rows}')
    print(f'  Total Analog rows: {analog_rows}')


def main():
    process_mixed_folder(DIGIT_DIR, OUT_DIGIT, OUT_ANALOG, 'Digit folder')
    process_mixed_folder(ANALOG_DIR, OUT_DIGIT, OUT_ANALOG, 'Analog folder')
    print('\nDone.')


if __name__ == '__main__':
    main()
