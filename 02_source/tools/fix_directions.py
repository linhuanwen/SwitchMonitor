#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Fix Direction="未知" in parsed_data by assigning alternating directions.

In ZYJ7 switch machines, actions alternate: after 定位→反位 the switch
is at 反位, so the next action MUST be 反位→定位. The only exception is a
failed action + immediate retry (same direction, very short duration).

This script:
1. Reads index.json → all switches and dates
2. For each switch, reads all events sorted by timestamp
3. Assigns alternating directions (定位→反位, 反位→定位, ...)
4. Writes back updated JSON files
5. Regenerates features.json per switch

Usage:
    python fix_directions.py [--parsed-dir PATH] [--dry-run]
"""

import json
import os
import sys
import argparse
from collections import defaultdict
from pathlib import Path

DIR_NORMAL_TO_REVERSE = "定位→反位"
DIR_REVERSE_TO_NORMAL = "反位→定位"


def fix_switch(switch_id, parsed_dir, dry_run=False):
    """Fix directions for one switch's data files."""
    switch_dir = Path(parsed_dir) / switch_id
    if not switch_dir.exists():
        print(f"  [{switch_id}] Directory not found: {switch_dir}")
        return 0, 0

    # Collect all events from all day files
    all_events = []
    day_files = sorted(switch_dir.glob("*.json"))
    for day_file in day_files:
        # Skip features files
        if day_file.name in ("features.json", "current_features.json"):
            continue
        try:
            with open(day_file, 'r', encoding='utf-8-sig') as f:
                events = json.load(f)
            for evt in events:
                evt['_day_file'] = str(day_file)
                all_events.append(evt)
        except Exception as e:
            print(f"  [{switch_id}] WARN: {day_file.name}: {e}")

    if not all_events:
        print(f"  [{switch_id}] No events found")
        return 0, 0

    # Sort by timestamp
    all_events.sort(key=lambda e: e['Timestamp'])

    # Assign alternating directions
    # First event: arbitrary 定位→反位 (correct grouping matters more than absolute label)
    total = len(all_events)
    fixed = 0
    already_ok = 0

    for i, evt in enumerate(all_events):
        if i % 2 == 0:
            new_dir = DIR_NORMAL_TO_REVERSE
        else:
            new_dir = DIR_REVERSE_TO_NORMAL

        if evt.get('Direction') in (DIR_NORMAL_TO_REVERSE, DIR_REVERSE_TO_NORMAL):
            already_ok += 1
            # Don't override already-correct directions from digit data
            continue

        evt['Direction'] = new_dir
        fixed += 1

    # Group back by day file
    day_groups = defaultdict(list)
    for evt in all_events:
        day_file = evt.pop('_day_file')
        day_groups[day_file].append(evt)

    if not dry_run:
        for day_file, events in day_groups.items():
            events.sort(key=lambda e: e['Timestamp'])
            with open(day_file, 'w', encoding='utf-8') as f:
                json.dump(events, f, ensure_ascii=False, separators=(',', ':'))

    print(f"  [{switch_id}] {total} events: fixed={fixed}, already_ok={already_ok}")
    return total, fixed


def rebuild_features(switch_id, parsed_dir, dry_run=False):
    """Rebuild features.json for a switch after fixing directions."""
    switch_dir = Path(parsed_dir) / switch_id
    if not switch_dir.exists():
        return

    # Collect all events
    all_events = []
    for day_file in sorted(switch_dir.glob("*.json")):
        if day_file.name in ("features.json", "current_features.json"):
            continue
        try:
            with open(day_file, 'r', encoding='utf-8-sig') as f:
                events = json.load(f)
            all_events.extend(events)
        except Exception:
            continue

    if not all_events:
        return

    all_events.sort(key=lambda e: e['Timestamp'])

    # Simple feature extraction (matching C# FeaturesStore logic)
    rows = []
    for evt in all_events:
        direction_code = 1.0 if evt.get('Direction') == DIR_NORMAL_TO_REVERSE else \
                         (2.0 if evt.get('Direction') == DIR_REVERSE_TO_NORMAL else 0.0)

        duration = evt.get('Duration', 0)
        power = evt.get('Power', [])

        # Extract power features
        power_vals = [p[1] for p in power] if power else []
        spike_peak = max(power_vals) if power_vals else 0
        n = len(power_vals)
        if n < 10:
            continue

        # Segment power into phases (approximate, matching ZYJ7 timing)
        # Unlock: first ~15% of duration, Conversion: 15-75%, Lock: 75-90%, Tail: 90-100%
        unlock_end = max(1, int(n * 0.15))
        conv_end = max(unlock_end + 1, int(n * 0.75))
        lock_end = max(conv_end + 1, int(n * 0.90))

        unlock_vals = power_vals[:unlock_end]
        conv_vals = power_vals[unlock_end:conv_end]
        lock_vals = power_vals[conv_end:lock_end]
        tail_vals = power_vals[lock_end:]

        unlock_mean = sum(unlock_vals) / len(unlock_vals) if unlock_vals else 0
        conv_mean = sum(conv_vals) / len(conv_vals) if conv_vals else 0
        lock_mean = sum(lock_vals) / len(lock_vals) if lock_vals else 0
        tail_mean = sum(tail_vals) / len(tail_vals) if tail_vals else 0

        rows.append([
            evt['Timestamp'],
            round(duration, 2),
            round(spike_peak, 3),
            round(unlock_mean, 3),
            round(conv_mean, 3),
            round(lock_mean, 3),
            round(tail_mean, 3),
            direction_code
        ])

    features = {
        "Columns": ["timestamp", "durationSec", "spikePeak", "unlockMean",
                     "convMean", "lockMean", "tailMean", "direction"],
        "Rows": rows
    }

    if not dry_run:
        features_path = switch_dir / "features.json"
        with open(features_path, 'w', encoding='utf-8') as f:
            json.dump(features, f, ensure_ascii=False, separators=(',', ':'))
        print(f"  [{switch_id}] features.json rebuilt: {len(rows)} rows")


def main():
    parser = argparse.ArgumentParser(description='Fix direction labels in parsed data')
    parser.add_argument('--parsed-dir', default=None,
                        help='Parsed data directory (default: 05_production_data/parsed_data)')
    parser.add_argument('--dry-run', action='store_true',
                        help='Show what would change without writing')
    args = parser.parse_args()

    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent.parent

    if args.parsed_dir:
        parsed_dir = Path(args.parsed_dir)
    else:
        parsed_dir = repo_root / '05_production_data' / 'parsed_data'

    if not parsed_dir.exists():
        print(f"ERROR: Parsed data directory not found: {parsed_dir}")
        return 1

    # Load index to get switch IDs
    index_path = parsed_dir / 'index.json'
    if not index_path.exists():
        print(f"ERROR: index.json not found in {parsed_dir}")
        return 1

    with open(index_path, 'r', encoding='utf-8') as f:
        index = json.load(f)

    switch_ids = sorted(index.keys())
    print(f"Found {len(switch_ids)} switches in index")
    print(f"Parsed dir: {parsed_dir}")
    if args.dry_run:
        print("DRY RUN - no files will be modified\n")

    grand_total = 0
    grand_fixed = 0
    for sid in switch_ids:
        total, fixed = fix_switch(sid, parsed_dir, dry_run=args.dry_run)
        grand_total += total
        grand_fixed += fixed
        rebuild_features(sid, parsed_dir, dry_run=args.dry_run)

    print(f"\nDone: {grand_fixed}/{grand_total} events fixed across {len(switch_ids)} switches")
    if args.dry_run:
        print("DRY RUN - re-run without --dry-run to apply changes")
    return 0


if __name__ == '__main__':
    sys.exit(main())
