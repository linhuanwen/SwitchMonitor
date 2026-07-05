#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Parse CSM2010 switch curve binary .dat files to CSV.

Usage:
    python parse_csm2010.py shuju/SwitchCurve(0).dat
    python parse_csm2010.py shuju/SwitchCurve(0).dat output.csv
"""

import struct
import sys
import csv
from datetime import datetime
from pathlib import Path

BLOCK_SIZE = 4014
HEADER_SIZE = 42
DATA_START = 100032  # fixed data section offset observed in these files


def parse_file(input_path, output_path=None):
    input_path = Path(input_path)
    if output_path is None:
        output_path = input_path.with_suffix('.csv')
    else:
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

        # Basic validation
        if not (1_500_000_000 < ts < 2_000_000_000 and sample_rate == 25 and 10 < sample_count < 2000):
            off += BLOCK_SIZE
            continue

        n_floats = (BLOCK_SIZE - HEADER_SIZE) // 4
        floats = struct.unpack(f'<{n_floats}f',
                               data[off + HEADER_SIZE:off + HEADER_SIZE + n_floats * 4])
        samples = list(floats[:sample_count])

        events.append({
            'timestamp': ts,
            'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
            'phase': flags,
            'sample_rate': sample_rate,
            'sample_count': sample_count,
            'samples': samples,
        })
        off += BLOCK_SIZE

    if not events:
        print('No valid curve blocks found.')
        return

    max_count = max(e['sample_count'] for e in events)
    fieldnames = ['timestamp', 'datetime', 'phase'] + [f's{i}' for i in range(max_count)]

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

    # Group by timestamp for summary
    groups = {}
    for e in events:
        groups.setdefault(e['timestamp'], []).append(e)

    print(f'Input:  {input_path}')
    print(f'Output: {output_path}')
    print(f'Blocks: {len(events)}  ->  {len(groups)} events')
    print(f'Sample count range: {min(e["sample_count"] for e in events)} - {max(e["sample_count"] for e in events)}')


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    parse_file(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
