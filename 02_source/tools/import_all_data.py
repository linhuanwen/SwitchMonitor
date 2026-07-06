#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
End-to-end data importer: .dat → parsed_data JSON + index.json

Reads binary .dat files from raw data directory, parses CSM2010 blocks,
merges paired files per switch group, and writes JSON output compatible
with SwitchMonitor's DataPipeline.

Usage:
    python import_all_data.py [--raw-dir RAW_DIR] [--output-dir OUTPUT_DIR]
"""

import struct
import json
import os
import sys
import argparse
from datetime import datetime
from pathlib import Path
from collections import defaultdict

# ── CSM2010 binary constants ──────────────────────────────────────────
BLOCK_SIZE = 4014
HEADER_SIZE = 42
DATA_START = 100032

SAMPLE_INTERVAL = 0.04

# Switch group definitions (matching ConfigManager.CreateDefaultConfig)
SWITCH_GROUPS = [
    {"id": "1-1", "label": "1-1", "dataFileIndex": 0},
    {"id": "1-X", "label": "1-X", "dataFileIndex": 4},
    {"id": "3-1", "label": "3-1", "dataFileIndex": 8},
    {"id": "3-X", "label": "3-X", "dataFileIndex": 12},
    {"id": "2-1", "label": "2-1", "dataFileIndex": 16},
    {"id": "2-X", "label": "2-X", "dataFileIndex": 20},
    {"id": "4-1", "label": "4-1", "dataFileIndex": 24},
    {"id": "4-X", "label": "4-X", "dataFileIndex": 28},
]


def parse_dat_file(filepath, file_index, is_second_file=False):
    """Parse a single .dat file, return list of event dicts.

    Phase encoding: each switch group uses 2 paired files (N and N+3).
    - First file (index N): contains phases P, A, B
      Phase byte3 = N + offset, where offset: 0=Power, 1=A, 2=B
    - Second file (index N+3): contains phase C only
      Phase byte3 = N + 3, offset = 3 = C
    Because byte3 = file_index gives 0 for both P and C files,
    we use is_second_file to disambiguate.
    """
    events = []
    with open(filepath, 'rb') as f:
        data = f.read()

    off = DATA_START
    while off + BLOCK_SIZE <= len(data):
        hdr = data[off:off + HEADER_SIZE]
        ts = struct.unpack('<I', hdr[0:4])[0]
        flags = struct.unpack('<I', hdr[4:8])[0]
        sample_rate = struct.unpack('<H', hdr[10:12])[0]
        sample_count = struct.unpack('<H', hdr[12:14])[0]

        # Basic validation
        if not (1_500_000_000 < ts < 2_000_000_000 and
                sample_rate == 25 and 10 < sample_count < 2000):
            off += BLOCK_SIZE
            continue

        n_floats = (BLOCK_SIZE - HEADER_SIZE) // 4
        floats = struct.unpack(f'<{n_floats}f',
                               data[off + HEADER_SIZE:
                                    off + HEADER_SIZE + n_floats * 4])
        samples = list(floats[:sample_count])

        # Phase detection
        phase_byte3 = (flags >> 24) & 0xFF
        if is_second_file:
            # Second file: always C phase (offset 3)
            phase_type = 3
        else:
            # First file: offset = byte3 - file_index → 0=Power, 1=A, 2=B
            phase_type = phase_byte3 - file_index

        events.append({
            'timestamp': ts,
            'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
            'phase': flags,
            'phase_type': phase_type,  # 0=Power, 1=A, 2=B, 3=C
            'sample_count': sample_count,
            'samples': samples,
        })
        off += BLOCK_SIZE

    return events


def merge_events(events_list):
    """Merge events from 2 paired .dat files, grouping by timestamp.

    Uses phase_type field (0=Power, 1=A-current, 2=B-current, 3=C-current)
    set during parsing, which is relative to each file's index.
    """
    event_map = {}  # timestamp -> dict of phase data

    for events in events_list:
        for e in events:
            ts = e['timestamp']
            if ts not in event_map:
                event_map[ts] = {
                    'timestamp': ts,
                    'datetime': e['datetime'],
                    'sample_count': 0,
                    'current_a': [],
                    'current_b': [],
                    'current_c': [],
                    'power': [],
                }

            evt = event_map[ts]
            pt = e['phase_type']
            samples = e['samples']

            if pt == 1:       # A-phase current
                evt['current_a'] = samples
            elif pt == 2:     # B-phase current
                evt['current_b'] = samples
            elif pt == 3:     # C-phase current
                evt['current_c'] = samples
            elif pt == 0:     # Power
                evt['power'] = samples
            # Unknown phase types are ignored

            if e['sample_count'] > evt['sample_count']:
                evt['sample_count'] = e['sample_count']

    return list(event_map.values())


def build_switch_event(evt):
    """Convert internal event dict to SwitchMonitor JSON format."""
    sample_count = evt['sample_count']
    sample_interval = 0.04
    duration = round(sample_count * sample_interval, 3)

    # Build [[t, v], ...] arrays
    def build_samples(values):
        result = []
        for i, v in enumerate(values):
            t = round(i * sample_interval, 3)
            result.append([t, round(v, 3)])
        return result

    return {
        'Timestamp': evt['timestamp'],
        'DateTimeStr': evt['datetime'],
        'Direction': '',  # filled later
        'Duration': duration,
        'SampleInterval': sample_interval,
        'SampleCount': sample_count,
        'CurrentA': build_samples(evt.get('current_a', [])),
        'CurrentB': build_samples(evt.get('current_b', [])),
        'CurrentC': build_samples(evt.get('current_c', [])),
        'Power': build_samples(evt.get('power', [])),
    }


def process_switch_group(group, raw_dir, output_dir):
    """Process one switch group: read 2 paired .dat files, merge, write JSON."""
    idx1 = group['dataFileIndex']
    idx2 = group['dataFileIndex'] + 3
    switch_id = group['id']

    events1 = []
    events2 = []

    file1 = raw_dir / f"SwitchCurve({idx1}).dat"
    file2 = raw_dir / f"SwitchCurve({idx2}).dat"

    print(f"  [{switch_id}] Reading {file1.name}...")
    if file1.exists():
        events1 = parse_dat_file(file1, idx1, is_second_file=False)
        print(f"    -> {len(events1)} blocks")
    else:
        print(f"    -> NOT FOUND, skipping")

    print(f"  [{switch_id}] Reading {file2.name}...")
    if file2.exists():
        events2 = parse_dat_file(file2, idx2, is_second_file=True)
        print(f"    -> {len(events2)} blocks")
    else:
        print(f"    -> NOT FOUND, skipping")

    if not events1 and not events2:
        print(f"  [{switch_id}] No data, skipping")
        return 0

    # Merge by timestamp
    merged = merge_events([events1, events2])
    print(f"  [{switch_id}] Merged: {len(merged)} events")

    # Build SwitchEvent objects
    switch_events = [build_switch_event(e) for e in merged]

    # Group by date
    date_groups = defaultdict(list)
    for evt in switch_events:
        date_str = evt['DateTimeStr'][:10]  # "yyyy-MM-dd"
        date_groups[date_str].append(evt)

    # Sort each day's events by timestamp, assign alternating direction
    total_events = 0
    timestamps_by_date = defaultdict(list)

    for date_str, events in date_groups.items():
        events.sort(key=lambda e: e['Timestamp'])

        for i, evt in enumerate(events):
            evt['Direction'] = '定位→反位' if i % 2 == 0 else '反位→定位'

        # Write day JSON
        switch_dir = output_dir / switch_id
        switch_dir.mkdir(parents=True, exist_ok=True)
        day_file = switch_dir / f"{date_str}.json"

        with open(day_file, 'w', encoding='utf-8') as f:
            json.dump(events, f, ensure_ascii=False, separators=(',', ':'))

        total_events += len(events)

        # Collect timestamps for index
        timestamps_by_date[date_str] = sorted(
            [e['Timestamp'] for e in events], reverse=True)

    print(f"  [{switch_id}] Wrote {len(date_groups)} day files, {total_events} events")
    return switch_id, timestamps_by_date


def build_index(index_data):
    """Build index.json structure from collected timestamp data."""
    result = {}
    for switch_id, timestamps_by_date in index_data.items():
        result[switch_id] = {}
        for date_str in sorted(timestamps_by_date.keys(), reverse=True):
            result[switch_id][date_str] = timestamps_by_date[date_str]
    return result


def main():
    parser = argparse.ArgumentParser(description='Import CSM2010 .dat files to parsed_data JSON')
    parser.add_argument('--raw-dir', default=None,
                        help='Directory containing SwitchCurve(*).dat files')
    parser.add_argument('--output-dir', default=None,
                        help='Output directory for parsed_data (default: ../05_production_data/parsed_data)')
    args = parser.parse_args()

    # Determine paths
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent.parent

    if args.raw_dir:
        raw_dir = Path(args.raw_dir)
    else:
        raw_dir = repo_root / '03_raw_data' / 'sanshuibei'

    if args.output_dir:
        output_dir = Path(args.output_dir)
    else:
        output_dir = repo_root / '05_production_data' / 'parsed_data'

    print(f"Raw data dir: {raw_dir}")
    print(f"Output dir:   {output_dir}")
    print(f"Switch groups: {len(SWITCH_GROUPS)}")
    print()

    if not raw_dir.exists():
        print(f"ERROR: Raw data directory not found: {raw_dir}")
        sys.exit(1)

    # Ensure output directory exists
    output_dir.mkdir(parents=True, exist_ok=True)

    # Process all switch groups
    all_index_data = {}
    total_events = 0

    for group in SWITCH_GROUPS:
        result = process_switch_group(group, raw_dir, output_dir)
        if result:
            switch_id, timestamps_by_date = result
            all_index_data[switch_id] = timestamps_by_date
            total_events += sum(len(t) for t in timestamps_by_date.values())

    # Write index.json
    index = build_index(all_index_data)
    index_path = output_dir / 'index.json'
    with open(index_path, 'w', encoding='utf-8') as f:
        json.dump(index, f, ensure_ascii=False, separators=(',', ':'))
    print(f"\nIndex written: {index_path}")
    print(f"Total events: {total_events}")
    print("Done!")


if __name__ == '__main__':
    main()
