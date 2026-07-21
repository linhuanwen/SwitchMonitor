#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Parse digit.ini (GBK-encoded CSM digital point config) and extract
switch machine → {1DQJ, DB, FB} point ID mappings.

Usage:
    python parse_digit_ini.py [--ini-path PATH] [--output PATH]

Output: switch_digit_config.json — mapping from switch ID to DB/FB/1DQJ point IDs.
"""

import json
import re
import argparse
from pathlib import Path
from datetime import datetime


# Regex to match digit.ini entries like:
#   15501=1-J-1DQJ           ,     15500,        9,    1,    9   (ZYJ7)
#   15586=4-X-DB             ,     15585,        8,    0,    9   (ZYJ7)
#   201=1-J1-DB              ,                          ...  (ZDJ9)
#   213=1-J2-1DQJ            ,                          ...  (ZDJ9)
#   237=1-X2-FB              ,                          ...  (ZDJ9)
#
# Groups: point_id, switch_name, relay_type
_LINE_PATTERN = re.compile(
    r'^(\d+)\s*=\s*'          # point_id
    r'(\d+-[JX]\d?)-'         # switch_name: "4-X" (ZYJ7) or "4-X2" (ZDJ9)
    r'(1DQJ|DB|FB)'           # relay_type
    r'\s*,'                    # rest of line after name
)


## digit.ini already uses the standard naming convention (e.g. "1-J", "4-X"),
# matching what the codebase uses, so no mapping is needed.


def parse_digit_ini(filepath: str) -> dict:
    """Parse digit.ini and extract switch → point ID mappings.

    Args:
        filepath: Path to digit.ini (GBK encoded)

    Returns:
        dict: { switch_id: {db_point_id, fb_point_id, dqj_point_id}, ... }
              switch_id uses standard naming (e.g. "1-J", "4-X")
    """
    switches = {}  # switch_id → {db: point_id, fb: point_id, dqj: point_id}

    with open(filepath, 'r', encoding='gbk', errors='replace') as f:
        for line in f:
            line = line.strip()
            m = _LINE_PATTERN.match(line)
            if not m:
                continue

            point_id = int(m.group(1))
            switch_name = m.group(2)      # e.g. "4-X"
            relay_type = m.group(3)       # e.g. "DB", "FB", "1DQJ"

            switch_id = switch_name

            if switch_id not in switches:
                switches[switch_id] = {}

            if relay_type == '1DQJ':
                switches[switch_id]['dqj_point_id'] = point_id
            elif relay_type == 'DB':
                switches[switch_id]['db_point_id'] = point_id
            elif relay_type == 'FB':
                switches[switch_id]['fb_point_id'] = point_id

    return switches


def generate_config(ini_path: str, output_path: str) -> dict:
    """Parse digit.ini and write switch_digit_config.json.

    Args:
        ini_path: Path to digit.ini
        output_path: Path for output JSON

    Returns:
        dict: The config dict that was written
    """
    switches = parse_digit_ini(ini_path)

    # Validate: each switch must have all 3 point IDs
    for switch_id, points in switches.items():
        missing = []
        for key in ('db_point_id', 'fb_point_id', 'dqj_point_id'):
            if key not in points:
                missing.append(key)
        if missing:
            print(f"  WARNING: {switch_id} missing: {', '.join(missing)}")

    # Build output structure
    config = {
        "version": "1.0",
        "station_id": "SSB",
        "source_file": str(Path(ini_path).name),
        "generated_at": datetime.now().strftime('%Y-%m-%dT%H:%M:%S'),
        "switches": switches
    }

    # Ensure output directory exists
    output_dir = Path(output_path).parent
    output_dir.mkdir(parents=True, exist_ok=True)

    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, ensure_ascii=False, indent=2)

    return config


def main():
    parser = argparse.ArgumentParser(
        description='Parse digit.ini → switch_digit_config.json')
    parser.add_argument('--ini-path', default=None,
                        help='Path to digit.ini (default: 03_raw_data/Station_SSB/digit.ini)')
    parser.add_argument('--output', default=None,
                        help='Output JSON path (default: 05_production_data/Config/switch_digit_config.json)')
    args = parser.parse_args()

    # Determine paths
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent.parent

    if args.ini_path:
        ini_path = args.ini_path
    else:
        ini_path = repo_root / '03_raw_data' / 'Station_SSB' / 'digit.ini'

    if args.output:
        output_path = args.output
    else:
        output_path = repo_root / '05_production_data' / 'Config' / 'switch_digit_config.json'

    ini_path = Path(ini_path)
    output_path = Path(output_path)

    if not ini_path.exists():
        print(f"ERROR: digit.ini not found: {ini_path}")
        return 1

    print(f"Parsing: {ini_path}")
    config = generate_config(str(ini_path), str(output_path))

    switch_count = len(config['switches'])
    print(f"Found {switch_count} switches")
    for switch_id, points in sorted(config['switches'].items()):
        print(f"  {switch_id}: DB={points.get('db_point_id', '?')}, "
              f"FB={points.get('fb_point_id', '?')}, "
              f"1DQJ={points.get('dqj_point_id', '?')}")

    print(f"\nOutput: {output_path}")
    return 0


if __name__ == '__main__':
    exit(main())
