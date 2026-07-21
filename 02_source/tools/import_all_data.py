#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
End-to-end data importer: .dat → parsed_data JSON + index.json

Reads binary .dat files from raw data directory, parses CSM2010 blocks,
merges paired files per switch group, and writes JSON output compatible
with SwitchMonitor's DataPipeline.

Supports ZYJ7 (2 machines/switch: J/X) and ZDJ9 (4 machines/switch: J1/J2/X1/X2).
Switch groups are auto-derived from DC.ini; site.json provides station metadata.

Usage:
    # Import by station name (reads site.json from 03_raw_data/<station>/)
    python import_all_data.py --station sanshuibei
    python import_all_data.py --station cencun

    # Or specify paths explicitly
    python import_all_data.py --raw-dir RAW_DIR [--output-dir OUTPUT_DIR]
"""

import struct
import json
import os
import re
import sys
import argparse
from datetime import datetime
from pathlib import Path
from collections import defaultdict

# Digit data parsing imports
from parse_digit_ini import parse_digit_ini
from parse_local_receive import parse_digit_file

# ── CSM2010 binary constants ──────────────────────────────────────────
BLOCK_SIZE = 4014
HEADER_SIZE = 14
DATA_START = 100032

SAMPLE_INTERVAL = 0.04

# ── Default ZYJ7 switch groups (fallback when no DC.ini) ──────────────
_DEFAULT_ZYJ7_GROUPS = [
    {"id": "1-J", "label": "1-J", "dataFileIndex": 0},
    {"id": "1-X", "label": "1-X", "dataFileIndex": 4},
    {"id": "3-J", "label": "3-J", "dataFileIndex": 8},
    {"id": "3-X", "label": "3-X", "dataFileIndex": 12},
    {"id": "2-J", "label": "2-J", "dataFileIndex": 16},
    {"id": "2-X", "label": "2-X", "dataFileIndex": 20},
    {"id": "4-J", "label": "4-J", "dataFileIndex": 24},
    {"id": "4-X", "label": "4-X", "dataFileIndex": 28},
]


# ═══════════════════════════════════════════════════════════════════════
# Site config loading
# ═══════════════════════════════════════════════════════════════════════

def load_site_config(site_dir):
    """Load site.json from a station directory.

    Returns dict with keys: name, dataFormat, switchType, machinesPerSwitch,
    switchCount, paths (or None if site.json not found).
    """
    site_json = Path(site_dir) / 'site.json'
    if site_json.exists():
        with open(site_json, 'r', encoding='utf-8') as f:
            return json.load(f)
    return None


def resolve_paths(raw_dir, site_config):
    """Resolve paths from site.json relative to the site directory.

    Args:
        raw_dir: Path to the station directory (where site.json lives)
        site_config: dict from load_site_config()

    Returns:
        (curve_data_dir, digit_ini_path, digit_data_dir) — all Path objects.
        curve_data_dir always non-None; digit paths may be None.
    """
    paths = site_config.get('paths', {}) if site_config else {}
    raw_dir = Path(raw_dir)

    # Curve data directory
    curve_data_dir = raw_dir
    if paths.get('curveData'):
        curve_path = (raw_dir / paths['curveData']).resolve()
        if curve_path.exists():
            curve_data_dir = curve_path

    # digit.ini path
    digit_ini_path = None
    if paths.get('digitIni'):
        dip = (raw_dir / paths['digitIni']).resolve()
        if dip.exists():
            digit_ini_path = dip

    # Digit(*).dat directory
    digit_data_dir = None
    if paths.get('digitData'):
        ddd = (raw_dir / paths['digitData']).resolve()
        if ddd.exists():
            digit_data_dir = ddd

    return curve_data_dir, digit_ini_path, digit_data_dir


# ═══════════════════════════════════════════════════════════════════════
# DC.ini → switch groups derivation
# ═══════════════════════════════════════════════════════════════════════

def derive_switch_groups_from_dc_ini(dc_ini_path: Path):
    """Parse DC.ini (GBK-encoded) and derive switch group definitions.

    Matches the C# DcIniParser logic:
      - Channel line: digit = name , type , file_idx , group_idx
      - Channel name: {switch}-{J|X}{sub?}-{A|B|C|P}
      - Groups by MachineId, dataFileIndex = min current channel FileIndex
      - Only includes machines that have current channels (A/B/C)

    Returns:
        list of dicts: [{"id": "1-J1", "label": "1-J1", "dataFileIndex": 0}, ...]
        Empty list if DC.ini not found or parse fails.
    """
    if not dc_ini_path.exists():
        return []

    chan_re = re.compile(r'^\s*(\d+)\s*=\s*([^,]+?)\s*,\s*(\d+)\s*,\s*(\d+)')
    # Support both formats: "1-J-A" (standard) and "9-A" (single-machine)
    name_re = re.compile(r'^(\d+)-(?:([JX])(\d*)-)?([ABCP])$')

    # machine_id → min current file_index
    machine_current_idx = {}

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
            phase = nm.group(4)  # A/B/C/P

            # Only track current channels (A/B/C)
            if phase not in ('A', 'B', 'C'):
                continue

            switch_no = nm.group(1)
            beam_type = nm.group(2) or ''  # J or X (empty for single-machine)
            sub_id = nm.group(3) or ''     # "" or "1" or "2"
            # Single-machine "9-A" → machine_id="9"; Standard "1-J-A" → machine_id="1-J"
            machine_id = switch_no if not beam_type else f"{switch_no}-{beam_type}{sub_id}"

            if machine_id not in machine_current_idx or \
               file_idx < machine_current_idx[machine_id]:
                machine_current_idx[machine_id] = file_idx

    if not machine_current_idx:
        return []

    # Sort by file index (matches C# DeriveSwitchGroups behavior)
    groups = []
    for machine_id in sorted(machine_current_idx.keys(),
                             key=lambda m: machine_current_idx[m]):
        groups.append({
            'id': machine_id,
            'label': machine_id,
            'dataFileIndex': machine_current_idx[machine_id],
        })

    return groups


def get_switch_groups(raw_dir, site_config):
    """Determine switch groups for a station.

    Priority:
      1. DC.ini in the curve data directory (authoritative)
      2. ZYJ7 default (backward compat fallback)

    Returns:
        list of dicts: [{"id": ..., "label": ..., "dataFileIndex": ...}, ...]
    """
    # Priority 1: DC.ini
    dc_ini = Path(raw_dir) / 'DC.ini'
    if dc_ini.exists():
        groups = derive_switch_groups_from_dc_ini(dc_ini)
        if groups:
            return groups

    # Priority 2: default ZYJ7 (backward compat)
    print("  (no DC.ini found, using default ZYJ7 8-group layout)")
    return list(_DEFAULT_ZYJ7_GROUPS)


# ═══════════════════════════════════════════════════════════════════════
# .dat parsing
# ═══════════════════════════════════════════════════════════════════════

def parse_dat_file(filepath, file_index, is_second_file=False):
    """Parse a single .dat file, return list of event dicts.

    Phase encoding: each switch group uses 2 paired files (N and N+3).
    - First file (index N): contains phases A, B, C
      Phase byte3 = N + offset, where offset: 0=A-current, 1=B-current, 2=C-current
    - Second file (index N+3): contains Power only
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
            phase_type = 0  # Power
        else:
            offset = phase_byte3 - file_index
            if 0 <= offset <= 2:
                phase_type = offset + 1  # 0→1(A), 1→2(B), 2→3(C)
            else:
                phase_type = offset  # fallback

        # Direction from header bytes 8-9 (little-endian uint16 action counter)
        # Byte 9 is the high byte (counter value); parity encodes direction
        # Even counter → 定位→反位, Odd counter → 反位→定位
        dir_byte = hdr[9]

        events.append({
            'timestamp': ts,
            'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
            'phase': flags,
            'phase_type': phase_type,
            'sample_count': sample_count,
            'samples': samples,
            'dir_byte': dir_byte,
        })
        off += BLOCK_SIZE

    return events


def merge_events(events_list):
    """Merge events from 2 paired .dat files, grouping by timestamp."""
    event_map = {}

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

            if pt == 1:
                evt['current_a'] = samples
            elif pt == 2:
                evt['current_b'] = samples
            elif pt == 3:
                evt['current_c'] = samples
            elif pt == 0:
                evt['power'] = samples

            if e['sample_count'] > evt['sample_count']:
                evt['sample_count'] = e['sample_count']

            # Propagate direction byte (all phases of same action share it)
            if 'dir_byte' in e:
                evt['dir_byte'] = e['dir_byte']

    return list(event_map.values())


def build_switch_event(evt):
    """Convert internal event dict to SwitchMonitor JSON format."""
    sample_count = evt['sample_count']
    sample_interval = 0.04
    duration = round(sample_count * sample_interval, 3)

    def build_samples(values):
        return [[round(i * sample_interval, 3), round(v, 3)]
                for i, v in enumerate(values)]

    # Direction from CSM2010 header byte 8 (action sequence counter)
    # Even counter → 定位→反位, Odd counter → 反位→定位
    dir_byte = evt.get('dir_byte', 0)
    if (dir_byte & 0x01) == 0:
        direction = '定位→反位'
    else:
        direction = '反位→定位'

    return {
        'Timestamp': evt['timestamp'],
        'DateTimeStr': evt['datetime'],
        'Direction': direction,
        'Duration': duration,
        'SampleInterval': sample_interval,
        'SampleCount': sample_count,
        'CurrentA': build_samples(evt.get('current_a', [])),
        'CurrentB': build_samples(evt.get('current_b', [])),
        'CurrentC': build_samples(evt.get('current_c', [])),
        'Power': build_samples(evt.get('power', [])),
    }


# ═══════════════════════════════════════════════════════════════════════
# Direction resolution
# ═══════════════════════════════════════════════════════════════════════

def _is_energized(state_byte):
    """Check if a relay is energized based on vendor-specific state byte.

    huihuang (辉黄): uses 0x2f for energized, 0x00 for de-energized
    tonghao (通号): uses various non-zero values (0x83/0x9d/0x1d) for energized

    For compatibility, treat any non-zero value as energized.
    """
    return state_byte != 0x00


def resolve_direction(timestamp, db_point_id, fb_point_id, digit_timeline):
    """Determine switch direction from DB/FB states at the event timestamp.

    Used as an optional verification/override for the direction derived from
    the CSM2010 binary header bytes 8-9.

    Returns:
        "定位→反位" if DB=1/FB=0
        "反位→定位" if DB=0/FB=1
        None if states are ambiguous or unknown
    """
    if not digit_timeline:
        return None

    db_state = None
    fb_state = None

    for event in digit_timeline:
        if event['timestamp'] > timestamp:
            break
        if event['point_id'] == db_point_id:
            db_state = 1 if _is_energized(event['state_byte']) else 0
        elif event['point_id'] == fb_point_id:
            fb_state = 1 if _is_energized(event['state_byte']) else 0

    if db_state == 1 and fb_state == 0:
        return '定位→反位'
    if db_state == 0 and fb_state == 1:
        return '反位→定位'
    return None


def process_switch_group(group, raw_dir, output_dir,
                         digit_config=None, digit_timeline=None):
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

    merged = merge_events([events1, events2])
    print(f"  [{switch_id}] Merged: {len(merged)} events")

    switch_events = [build_switch_event(e) for e in merged]

    # Get digit point IDs for this switch
    db_point_id = None
    fb_point_id = None
    if digit_config and switch_id in digit_config:
        cfg = digit_config[switch_id]
        db_point_id = cfg.get('db_point_id')
        fb_point_id = cfg.get('fb_point_id')

    # Group by date
    date_groups = defaultdict(list)
    for evt in switch_events:
        date_str = evt['DateTimeStr'][:10]
        date_groups[date_str].append(evt)

    total_events = 0
    resolved_count = 0
    timestamps_by_date = defaultdict(list)

    for date_str, events in date_groups.items():
        events.sort(key=lambda e: e['Timestamp'])

        for evt in events:
            direction = resolve_direction(
                evt['Timestamp'], db_point_id, fb_point_id, digit_timeline)
            if direction is not None:
                evt['Direction'] = direction
                resolved_count += 1

        switch_dir = output_dir / switch_id
        switch_dir.mkdir(parents=True, exist_ok=True)
        day_file = switch_dir / f"{date_str}.json"

        with open(day_file, 'w', encoding='utf-8') as f:
            json.dump(events, f, ensure_ascii=False, separators=(',', ':'))

        total_events += len(events)
        timestamps_by_date[date_str] = sorted(
            [e['Timestamp'] for e in events], reverse=True)

    unknown_count = total_events - resolved_count
    print(f"  [{switch_id}] Wrote {len(date_groups)} day files, "
          f"{total_events} events (digit={resolved_count}, unknown={unknown_count})")
    return switch_id, timestamps_by_date


def build_index(index_data):
    """Build index.json structure from collected timestamp data."""
    result = {}
    for switch_id, timestamps_by_date in index_data.items():
        result[switch_id] = {}
        for date_str in sorted(timestamps_by_date.keys(), reverse=True):
            result[switch_id][date_str] = timestamps_by_date[date_str]
    return result


# ═══════════════════════════════════════════════════════════════════════
# Main
# ═══════════════════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(
        description='Import CSM2010 .dat files → parsed_data JSON')
    parser.add_argument('--station', default=None,
                        help='Station name (looks up 03_raw_data/<station>/site.json)')
    parser.add_argument('--raw-dir', default=None,
                        help='Directory containing SwitchCurve(*).dat files '
                             '(overrides site.json paths.curveData)')
    parser.add_argument('--output-dir', default=None,
                        help='Output directory for parsed_data')
    parser.add_argument('--digit-ini', default=None,
                        help='Path to digit.ini (overrides site.json paths.digitIni)')
    parser.add_argument('--digit-dir', default=None,
                        help='Directory containing Digit(*).dat files '
                             '(overrides site.json paths.digitData)')
    args = parser.parse_args()

    # Determine paths
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent.parent

    # Load site config if --station specified
    site_config = None
    if args.station:
        site_dir = repo_root / '03_raw_data' / args.station
        if not site_dir.exists():
            print(f"ERROR: Station directory not found: {site_dir}")
            sys.exit(1)
        site_config = load_site_config(site_dir)
        if site_config:
            print(f"Station: {site_config.get('name', args.station)}")
            print(f"  Data format: {site_config.get('dataFormat', 'CSM2010')}")
            print(f"  Switch type: {site_config.get('switchType', '?')}")
            print(f"  Machines/switch: {site_config.get('machinesPerSwitch', '?')}")
        else:
            print(f"Station: {args.station} (no site.json)")

        # Resolve paths from site.json
        curve_data_dir, auto_digit_ini, auto_digit_dir = resolve_paths(
            site_dir, site_config)
    else:
        site_dir = None
        curve_data_dir = None
        auto_digit_ini = None
        auto_digit_dir = None

    # CLI args override auto-detected paths
    if args.raw_dir:
        raw_dir = Path(args.raw_dir)
    elif curve_data_dir:
        raw_dir = curve_data_dir
    else:
        raw_dir = repo_root / '03_raw_data' / 'sanshuibei'

    if args.output_dir:
        output_dir = Path(args.output_dir)
    elif site_config:
        station_id = args.station or 'default'
        output_dir = repo_root / '05_production_data' / 'parsed_data' / station_id
    else:
        output_dir = repo_root / '05_production_data' / 'parsed_data'

    # Resolve digit.ini path
    if args.digit_ini:
        digit_ini_path = Path(args.digit_ini)
    elif auto_digit_ini:
        digit_ini_path = auto_digit_ini
    else:
        digit_ini_path = repo_root / '03_raw_data' / 'Station_SSB' / 'digit.ini'

    # Resolve digit data dir
    if args.digit_dir:
        digit_dir = Path(args.digit_dir)
    elif auto_digit_dir:
        digit_dir = auto_digit_dir
    else:
        digit_dir = repo_root / '03_raw_data' / '本地接收目录扳动'

    # ── Load digit config ──
    digit_config = None
    if digit_ini_path.exists():
        print(f"Digit config: {digit_ini_path}")
        digit_config = parse_digit_ini(str(digit_ini_path))
        print(f"  Found {len(digit_config)} switch configurations")
    else:
        print(f"Digit config not found: {digit_ini_path} "
              f"(direction will be '未知')")

    # ── Build digit timeline ──
    digit_timeline = None
    if digit_dir.exists() and digit_config:
        digit_files = sorted(digit_dir.glob('Digit*.dat'))
        if digit_files:
            point_ids_of_interest = set()
            for cfg in digit_config.values():
                if 'db_point_id' in cfg:
                    point_ids_of_interest.add(cfg['db_point_id'])
                if 'fb_point_id' in cfg:
                    point_ids_of_interest.add(cfg['fb_point_id'])

            print(f"Digit timeline: {len(digit_files)} files "
                  f"(tracking {len(point_ids_of_interest)} point IDs)...")

            all_events = []
            for fp in digit_files:
                rows = parse_digit_file(str(fp))
                for r in rows:
                    if r['point_id'] in point_ids_of_interest:
                        all_events.append(r)

            all_events.sort(key=lambda e: e['timestamp'])
            digit_timeline = all_events
            print(f"  Timeline: {len(digit_timeline)} relevant events")
        else:
            print(f"No Digit*.dat files found in {digit_dir}")
    else:
        if not digit_dir.exists():
            print(f"Digit data dir not found: {digit_dir} "
                  f"(direction will be '未知')")

    # ── Derive switch groups ──
    switch_groups = get_switch_groups(raw_dir, site_config)

    print(f"\nRaw data dir:  {raw_dir}")
    print(f"Output dir:    {output_dir}")
    print(f"Switch groups: {len(switch_groups)}")
    for g in switch_groups:
        print(f"  {g['id']:<8} → SwitchCurve({g['dataFileIndex']}).dat")
    print()

    if not raw_dir.exists():
        print(f"ERROR: Raw data directory not found: {raw_dir}")
        sys.exit(1)

    output_dir.mkdir(parents=True, exist_ok=True)

    # ── Process all switch groups ──
    all_index_data = {}
    total_events = 0

    for group in switch_groups:
        result = process_switch_group(
            group, raw_dir, output_dir,
            digit_config=digit_config,
            digit_timeline=digit_timeline)
        if result:
            switch_id, timestamps_by_date = result
            all_index_data[switch_id] = timestamps_by_date
            total_events += sum(len(t) for t in timestamps_by_date.values())

    # ── Write index.json ──
    index = build_index(all_index_data)
    index_path = output_dir / 'index.json'
    with open(index_path, 'w', encoding='utf-8') as f:
        json.dump(index, f, ensure_ascii=False, separators=(',', ':'))
    print(f"\nIndex written: {index_path}")
    print(f"Total events: {total_events}")
    print("Done!")


if __name__ == '__main__':
    main()
