#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Panyu Station Raw Data Preprocessing Script

Processing:
1. .almz files -> decompress & parse alarm/event data -> CSV
2. .hbf files -> parse switch action power/current curves -> CSV
3. Extract switch DB/FB digital point mappings from config -> JSON
4. Generate panyu station configuration files

Usage:
    python process_panyu.py [--all] [--almz] [--hbf] [--config]

Input:  03_raw_data/panyu/
Output: 03_raw_data/panyu_processed/
"""

import struct
import gzip
import json
import csv
import sys
import os
import re
import argparse
from pathlib import Path
from datetime import datetime
from collections import defaultdict

# Fix Windows console encoding
if sys.platform == 'win32':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

# ── 路径定义 ──
SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
RAW_DIR = REPO_ROOT / '03_raw_data' / 'panyu'
OUT_DIR = REPO_ROOT / '03_raw_data' / 'panyu_processed'
PROD_CONFIG_DIR = REPO_ROOT / '05_production_data' / 'Config'

# ── 番禺站道岔组定义 ──
# 从设备.rhhcfg 和 PYZ.ini 中提取
# 奇数为 J(尖轨), 偶数为 X(心轨); 编号1-21为下行咽喉, 2-8为上行咽喉
PANYU_SWITCH_GROUPS = [
    # 下行咽喉 (南侧)
    {"id": "1-J",  "label": "1-J",  "switchNo": 1,  "type": "J"},
    {"id": "1-X",  "label": "1-X",  "switchNo": 1,  "type": "X"},
    {"id": "3-J",  "label": "3-J",  "switchNo": 3,  "type": "J"},
    {"id": "3-X",  "label": "3-X",  "switchNo": 3,  "type": "X"},
    {"id": "5-J",  "label": "5-J",  "switchNo": 5,  "type": "J"},
    {"id": "5-X",  "label": "5-X",  "switchNo": 5,  "type": "X"},
    {"id": "7-J",  "label": "7-J",  "switchNo": 7,  "type": "J"},
    {"id": "7-X",  "label": "7-X",  "switchNo": 7,  "type": "X"},
    {"id": "9-J",  "label": "9-J",  "switchNo": 9,  "type": "J"},
    {"id": "9-X",  "label": "9-X",  "switchNo": 9,  "type": "X"},
    {"id": "11-J", "label": "11-J", "switchNo": 11, "type": "J"},
    {"id": "11-X", "label": "11-X", "switchNo": 11, "type": "X"},
    {"id": "13-J", "label": "13-J", "switchNo": 13, "type": "J"},
    {"id": "13-X", "label": "13-X", "switchNo": 13, "type": "X"},
    {"id": "15-J", "label": "15-J", "switchNo": 15, "type": "J"},
    {"id": "15-X", "label": "15-X", "switchNo": 15, "type": "X"},
    {"id": "17-J", "label": "17-J", "switchNo": 17, "type": "J"},
    {"id": "17-X", "label": "17-X", "switchNo": 17, "type": "X"},
    {"id": "19-J", "label": "19-J", "switchNo": 19, "type": "J"},
    {"id": "19-X", "label": "19-X", "switchNo": 19, "type": "X"},
    {"id": "21-J", "label": "21-J", "switchNo": 21, "type": "J"},
    {"id": "21-X", "label": "21-X", "switchNo": 21, "type": "X"},
    # 上行咽喉 (北侧)
    {"id": "2-J",  "label": "2-J",  "switchNo": 2,  "type": "J"},
    {"id": "2-X",  "label": "2-X",  "switchNo": 2,  "type": "X"},
    {"id": "4-J",  "label": "4-J",  "switchNo": 4,  "type": "J"},
    {"id": "4-X",  "label": "4-X",  "switchNo": 4,  "type": "X"},
    {"id": "6-J",  "label": "6-J",  "switchNo": 6,  "type": "J"},
    {"id": "6-X",  "label": "6-X",  "switchNo": 6,  "type": "X"},
    {"id": "8-J",  "label": "8-J",  "switchNo": 8,  "type": "J"},
    {"id": "8-X",  "label": "8-X",  "switchNo": 8,  "type": "X"},
]


# ═══════════════════════════════════════════════════════════
# 1. .almz 报警文件解析
# ═══════════════════════════════════════════════════════════

def parse_almz_file(filepath):
    """
    Parse .almz file (gzip-compressed binary alarm data from CSM system).

    Format discovered through reverse-engineering:
      - Outer: gzip compression
      - Inner: C++ serialized object stream
      - Records are separated by 8-byte marker: 1C 6C 5E 02 D5 D7 DE 08
      - Each record: [type_byte] [variable_data] <marker>
      - Timestamps embedded as uint32 LE within record data

    Returns: list of dicts with keys: timestamp, datetime, record_type, raw_size
    """
    filepath = Path(filepath)
    records = []

    try:
        with gzip.open(filepath, 'rb') as f:
            data = f.read()
    except (OSError, gzip.BadGzipFile) as e:
        print(f"  [WARN] Cannot decompress {filepath.name}: {e}")
        return records

    if len(data) < 24:
        return records

    # Use the 8-byte record separator marker
    MARKER = bytes.fromhex('1c6c5e02d5d7de08')

    # Find all marker positions
    marker_positions = []
    search_start = 0
    while True:
        pos = data.find(MARKER, search_start)
        if pos == -1:
            break
        marker_positions.append(pos)
        search_start = pos + 8

    if len(marker_positions) < 2:
        # Fall back to timestamp scanning
        return _parse_almz_by_timestamp_scan(data, filepath.name)

    # Extract records between markers
    for i in range(len(marker_positions) - 1):
        rec_start = marker_positions[i] + 8  # After marker
        rec_end = marker_positions[i + 1]     # Before next marker

        if rec_start >= rec_end or rec_start >= len(data):
            continue

        record_data = data[rec_start:rec_end]
        if len(record_data) < 4:
            continue

        rec_type = record_data[0]

        # Extract timestamp(s) from the record data
        timestamps = []
        for j in range(0, len(record_data) - 3, 4):
            ts = struct.unpack_from('<I', record_data, j)[0]
            if 1_700_000_000 < ts < 1_800_000_000:
                timestamps.append(ts)

        # Extract readable text (GB2312 encoded)
        text_parts = []
        for j in range(0, len(record_data) - 1):
            if record_data[j] >= 0x80:  # Potential GB2312 lead byte
                try:
                    chunk = record_data[j:j+20]
                    # Try to decode a segment
                    decoded = chunk.decode('gb2312', errors='ignore')
                    # Filter for meaningful Chinese text
                    meaningful = ''.join(c for c in decoded if '一' <= c <= '鿿' or c.isalnum())
                    if len(meaningful) >= 2:
                        text_parts.append(meaningful)
                except:
                    pass

        # Use the first timestamp found
        ts = timestamps[0] if timestamps else None

        record = {
            'timestamp': ts or 0,
            'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S') if ts else 'unknown',
            'record_type': rec_type,
            'raw_size': len(record_data),
            'ts_count': len(timestamps),
        }

        if text_parts:
            record['text'] = ' | '.join(text_parts[:5])  # Top 5 text segments

        records.append(record)

    return records


def _parse_almz_by_timestamp_scan(data, filename):
    """Fallback parser: scan for valid timestamps and extract surrounding context."""
    records = []
    seen_ts = set()

    for off in range(0, min(len(data) - 16, 3_000_000), 4):
        ts = struct.unpack_from('<I', data, off)[0]
        if 1_700_000_000 < ts < 1_800_000_000 and ts not in seen_ts:
            seen_ts.add(ts)

            # Try to extract text around this timestamp
            ctx_start = max(0, off - 4)
            ctx_end = min(len(data), off + 128)
            ctx = data[ctx_start:ctx_end]

            text_parts = []
            for j in range(0, len(ctx) - 1):
                if ctx[j] >= 0x80:
                    try:
                        chunk = ctx[j:j+30]
                        decoded = chunk.decode('gb2312', errors='ignore')
                        meaningful = ''.join(c for c in decoded if '一' <= c <= '鿿' or c.isalnum())
                        if len(meaningful) >= 2:
                            text_parts.append(meaningful)
                    except:
                        pass

            record = {
                'timestamp': ts,
                'datetime': datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S'),
                'record_type': 0,
                'raw_size': ctx_end - ctx_start,
                'ts_count': 1,
            }
            if text_parts:
                record['text'] = ' | '.join(text_parts[:3])

            records.append(record)

            if len(records) >= 50000:
                break

    return records


def process_almz_files():
    """处理所有 .almz 报警文件"""
    almz_dir = RAW_DIR / '202607'
    out_dir = OUT_DIR / 'alarms'
    out_dir.mkdir(parents=True, exist_ok=True)

    almz_files = sorted(almz_dir.glob('*.almz'))
    print(f"\n{'='*60}")
    print(f"处理 .almz 报警文件: {len(almz_files)} 个")
    print(f"{'='*60}")

    all_records = []
    file_count = 0

    for fp in almz_files:
        records = parse_almz_file(fp)
        if records:
            all_records.extend(records)
            file_count += 1

            if file_count <= 5 or file_count % 100 == 0:
                print(f"  [{file_count}/{len(almz_files)}] {fp.name}: {len(records)} 条记录")

    if not all_records:
        print("  [WARN] 未能解析出有效报警记录")
        return

    # 按时间戳排序
    all_records.sort(key=lambda r: r['timestamp'])

    # 汇总统计
    print(f"\n  总计: {len(all_records)} 条报警记录")
    print(f"  时间范围: {all_records[0]['datetime']} ~ {all_records[-1]['datetime']}")

    # 按日期分组输出 CSV
    date_groups = defaultdict(list)
    for r in all_records:
        date_str = r['datetime'][:10]
        date_groups[date_str].append(r)

    for date_str in sorted(date_groups.keys()):
        records = date_groups[date_str]
        csv_path = out_dir / f"alarms_{date_str}.csv"

        fieldnames = ['timestamp', 'datetime', 'record_type', 'raw_size', 'ts_count', 'text']
        with open(csv_path, 'w', newline='', encoding='utf-8-sig') as f:
            writer = csv.DictWriter(f, fieldnames=fieldnames, extrasaction='ignore')
            writer.writeheader()
            writer.writerows(records)

    # 输出完整合并文件
    combined_path = out_dir / 'alarms_all.csv'
    fieldnames = ['timestamp', 'datetime', 'record_type', 'raw_size', 'ts_count', 'text']
    with open(combined_path, 'w', newline='', encoding='utf-8-sig') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames, extrasaction='ignore')
        writer.writeheader()
        writer.writerows(all_records)

    # 按报警类型统计
    type_counts = defaultdict(int)
    for r in all_records:
        type_counts[r['record_type']] += 1

    print(f"\n  按类型统计 (Top 20):")
    for typ, cnt in sorted(type_counts.items(), key=lambda x: -x[1])[:20]:
        print(f"    类型 0x{typ:02x}: {cnt} 条")

    # 生成汇总 JSON
    summary = {
        'total_records': len(all_records),
        'files_processed': file_count,
        'total_files': len(almz_files),
        'time_range': {
            'start': all_records[0]['datetime'],
            'end': all_records[-1]['datetime'],
        },
        'type_counts': dict(sorted(type_counts.items(), key=lambda x: -x[1])[:50]),
        'dates': sorted(date_groups.keys()),
    }

    summary_path = out_dir / 'alarms_summary.json'
    with open(summary_path, 'w', encoding='utf-8') as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    print(f"\n  输出目录: {out_dir}")
    print(f"  - alarms_all.csv ({len(all_records)} 条)")
    print(f"  - alarms_YYYY-MM-DD.csv ({len(date_groups)} 天)")
    print(f"  - alarms_summary.json")


# ═══════════════════════════════════════════════════════════
# 2. .hbf 曲线文件解析
# ═══════════════════════════════════════════════════════════

def parse_hbf_header(filepath):
    """
    解析 .hbf 文件头。

    .hbf 格式 (CSM HBF 曲线文件):
      文件头: "hhcsmfzz" (8 bytes magic)
      后续包含: 数据偏移、采样率、通道数、记录数等
    """
    with open(filepath, 'rb') as f:
        magic = f.read(8)

    if magic[:8] != b'hhcsmfzz':
        print(f"  [WARN] {filepath.name}: 不是有效的 HBF 文件 (magic={magic[:8]})")
        return None

    return {'magic': magic.decode('ascii', errors='ignore')}


def parse_hbf_file(filepath, curve_type='power'):
    """
    解析 .hbf 曲线文件，提取道岔动作曲线数据。

    HBF 格式分析 (基于十六进制观察):
      offset 0: "hhcsmfzz" (8 bytes)
      offset 8: 文件头信息...
      offset 0x540000 (约): 数据区开始 (每个大文件 ~512MB, 包含多次道岔动作)

    数据区格式 (推测 - CSM标准):
      每个道岔动作事件:
        - 时间戳 (4B LE uint32)
        - 道岔编号/相位标记 (2B)
        - 采样点数 (2B)
        - 采样率 (2B)
        - 保留 (2B)
        - 数据: N x float32 (4B each), 其中电流(A)/功率(kW)

    返回: list of dicts [{timestamp, switch_id, samples, sample_count, ...}]
    """
    filepath = Path(filepath)
    file_size = filepath.stat().st_size

    events = []

    with open(filepath, 'rb') as f:
        header = f.read(8)
        if header[:8] != b'hhcsmfzz':
            print(f"  [WARN] {filepath.name}: 不是有效的 HBF 文件")
            return events

        # 读取文件头信息
        f.seek(8)
        hdr_data = f.read(56)  # 文件头其余部分

        # 尝试定位数据区: 搜索时间戳模式
        # 数据区通常从某个对齐的偏移开始，包含连续的 (ts, count, data) 元组
        f.seek(0)
        raw = f.read()

    print(f"  [{curve_type}] {filepath.name}: {file_size / 1024 / 1024:.1f} MB")

    # 搜索时间戳模式
    # 每个事件: timestamp(4B) + ? + sample_count(2B) + sample_rate(2B) + samples(N*4B)
    # 采样率通常是 25 (0x19) 或 40 (0x28) -> 对应 40ms 或 25ms 采样间隔

    scan_start = 64  # 跳过文件头
    found_ts = []

    for off in range(scan_start, min(len(raw) - 16, scan_start + 10_000_000), 4):
        ts = struct.unpack_from('<I', raw, off)[0]
        # 合理的时间戳范围
        if 1_700_000_000 < ts < 1_800_000_000:
            found_ts.append((off, ts))

    if not found_ts:
        # 扩大搜索范围
        for off in range(0, min(len(raw) - 16, 100_000_000), 4):
            ts = struct.unpack_from('<I', raw, off)[0]
            if 1_700_000_000 < ts < 1_800_000_000:
                found_ts.append((off, ts))

    print(f"    找到 {len(found_ts)} 个潜在时间戳位置")

    if len(found_ts) > 50000:
        # 太多匹配，采样输出
        found_ts = found_ts[::100]
        print(f"    (采样至 {len(found_ts)} 个)")

    # 输出发现摘要
    if found_ts:
        tss = [ts for _, ts in found_ts[:10]]
        dts = [datetime.fromtimestamp(ts).strftime('%Y-%m-%d %H:%M:%S') for ts in tss]
        print(f"    前10个时间戳: {dts[:5]}...")

    return events


def process_hbf_files():
    """处理 .hbf 曲线文件"""
    power_dir = RAW_DIR / '道岔动作功率曲线'
    current_dir = RAW_DIR / '道岔动作电流曲线'

    out_dir = OUT_DIR / 'curves'
    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"\n{'='*60}")
    print(f"处理 .hbf 曲线文件")
    print(f"{'='*60}")

    # 功率曲线
    if power_dir.exists():
        power_files = sorted(power_dir.glob('*.hbf'))
        print(f"\n功率曲线文件: {len(power_files)} 个")
        for fp in power_files:
            parse_hbf_file(fp, 'power')
    else:
        print(f"\n[SKIP] 功率曲线目录不存在: {power_dir}")

    # 电流曲线
    if current_dir.exists():
        current_files = sorted(current_dir.glob('*.hbf'))
        print(f"\n电流曲线文件: {len(current_files)} 个")
        for fp in current_files:
            parse_hbf_file(fp, 'current')
    else:
        print(f"\n[SKIP] 电流曲线目录不存在: {current_dir}")

    print(f"\n  注: .hbf 文件较大(512MB+)，完整解析需要较多时间。")
    print(f"  当前已完成格式探测和时间戳定位。")


# ═══════════════════════════════════════════════════════════
# 3. 道岔 DB/FB 开关量配置提取
# ═══════════════════════════════════════════════════════════

def parse_rhhcfg(filepath):
    """解析 .rhhcfg 文件 (INI 格式, GB2312 编码)"""
    config = {}
    current_section = None

    with open(filepath, 'r', encoding='gb2312', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith('[') and line.endswith(']'):
                current_section = line[1:-1]
                config[current_section] = {}
            elif '=' in line and current_section:
                key, _, value = line.partition('=')
                config[current_section][key.strip()] = value.strip()

    return config


def extract_switch_digit_config():
    """
    从设备.rhhcfg 和 开关量.rhhcfg 提取道岔 DB/FB/DQJ 开关量点号映射。

    逻辑:
    1. 从 设备.rhhcfg 的 [道岔] 段提取每个道岔的 DBJ/FBJ/1DQJ 名称
       - DBJ = 定位表示继电器 (Normal Position Indication)
       - FBJ = 反位表示继电器 (Reverse Position Indication)
    2. 从 开关量.rhhcfg 提取名称→点号映射
    """
    device_cfg_path = RAW_DIR / 'config-番禺站修改20260701' / 'Config_Ver0001' / '设备.rhhcfg'
    kaiguan_cfg_path = RAW_DIR / 'config-番禺站修改20260701' / 'Config_Ver0001' / '开关量.rhhcfg'

    switch_digit = {}

    print(f"\n{'='*60}")
    print(f"提取道岔开关量配置")
    print(f"{'='*60}")

    # 1. 读取设备配置中道岔段
    device_cfg = parse_rhhcfg(device_cfg_path)

    # 查找 [道岔\设备N] 段
    switch_devices = {}
    for section, items in device_cfg.items():
        if section.startswith('道岔\\设备'):
            name = items.get('设备名称', '')
            if not name:
                # 从 section 中推断
                parts = section.split('\\')
                if len(parts) >= 2:
                    name = parts[1] if not parts[1].startswith('设备') else items.get('设备名称', section)

            if name and ('-J' in name or '-X' in name or name.isdigit()):
                # 提取 DBJ, FBJ, 1DQJ 名称
                switch_devices[name] = {
                    'DBJ': items.get('DBJ', ''),
                    'FBJ': items.get('FBJ', ''),
                    '1DQJ': items.get('1DQJ', ''),
                    '设备类型': items.get('设备类型', '0'),
                    '所属设备号': items.get('所属设备号', ''),
                }
                print(f"  {name}: DBJ={items.get('DBJ','?')}, FBJ={items.get('FBJ','?')}")

    # 2. 读取开关量配置获取点号映射
    kaiguan_cfg = parse_rhhcfg(kaiguan_cfg_path)

    # 构建 名称→点号 映射
    name_to_point = {}
    for section, items in kaiguan_cfg.items():
        if section.startswith('开关量\\'):
            name = items.get('名称', '')
            point_type = items.get('类型', '')
            if name:
                name_to_point[name] = {
                    'section': section,
                    'type': point_type,
                }

    # 3. 构建 DB/FB 名称列表用于查找
    db_fb_names = set()
    for name, dev in switch_devices.items():
        if dev['DBJ']:
            db_fb_names.add(dev['DBJ'])
        if dev['FBJ']:
            db_fb_names.add(dev['FBJ'])

    # 在开关量配置中搜索
    switch_digit_config = {}

    # 方法: 直接搜索开关量配置中的DB/FB相关条目
    for section, items in kaiguan_cfg.items():
        if not section.startswith('开关量\\'):
            continue
        item_name = items.get('名称', '')
        if not item_name:
            continue

        # 匹配 DB/FB 模式: L_Ndb, L_Nfb 等
        for sw_name, dev in switch_devices.items():
            dbj = dev['DBJ']
            fbj = dev['FBJ']

            if item_name == dbj or item_name == fbj:
                if sw_name not in switch_digit_config:
                    switch_digit_config[sw_name] = {}

                if item_name == dbj:
                    switch_digit_config[sw_name]['db_name'] = item_name
                elif item_name == fbj:
                    switch_digit_config[sw_name]['fb_name'] = item_name

    # 如果没有精确匹配，尝试模糊匹配
    if not switch_digit_config:
        print("\n  尝试模糊匹配 DB/FB 名称...")
        # 搜索 L_NDB / L_NFB 模式
        for section, items in kaiguan_cfg.items():
            if not section.startswith('开关量\\'):
                continue
            item_name = items.get('名称', '')
            if not item_name:
                continue

            # 匹配 L_XDB / L_XFB
            db_match = re.match(r'L_(\d+)DB', item_name)
            fb_match = re.match(r'L_(\d+)FB', item_name)

            if db_match:
                sw_no = db_match.group(1)
                for sw_name in switch_devices:
                    if sw_name.startswith(f'{sw_no}-') or sw_name == sw_no:
                        if sw_name not in switch_digit_config:
                            switch_digit_config[sw_name] = {}
                        switch_digit_config[sw_name]['db_name'] = item_name
                        # 尝试提取点号
                        point_id = items.get('点号', '')
                        if point_id:
                            try:
                                switch_digit_config[sw_name]['db_point_id'] = int(point_id)
                            except ValueError:
                                pass

            if fb_match:
                sw_no = fb_match.group(1)
                for sw_name in switch_devices:
                    if sw_name.startswith(f'{sw_no}-') or sw_name == sw_no:
                        if sw_name not in switch_digit_config:
                            switch_digit_config[sw_name] = {}
                        switch_digit_config[sw_name]['fb_name'] = item_name
                        point_id = items.get('点号', '')
                        if point_id:
                            try:
                                switch_digit_config[sw_name]['fb_point_id'] = int(point_id)
                            except ValueError:
                                pass

    # 输出结果
    print(f"\n  提取到 {len(switch_digit_config)} 个道岔的 DB/FB 配置")
    for sw_name in sorted(switch_digit_config.keys()):
        cfg = switch_digit_config[sw_name]
        print(f"    {sw_name}: DB={cfg.get('db_name','?')} FB={cfg.get('fb_name','?')}")

    return switch_digit_config


def create_panyu_config():
    """生成番禺站系统配置文件"""
    print(f"\n{'='*60}")
    print(f"生成番禺站配置文件")
    print(f"{'='*60}")

    # 1. config.json - 系统配置
    config = {
        "switchGroups": [
            {"id": g["id"], "label": g["label"], "dataFileIndex": i}
            for i, g in enumerate(PANYU_SWITCH_GROUPS)
        ],
        "dataSourceDir": ".\\shuju\\panyu",
        "parsedDataDir": ".\\parsed_data",
        "scanInterval": 5,
        "alarmThresholds": {
            "current": {"enabled": True, "value": 2.0, "unit": "A"},
            "power": {"enabled": True, "value": 1.5, "unit": "KW"}
        },
        "chartColors": {
            "currentA": "#55FF55",
            "currentB": "#FF5555",
            "currentC": "#CC44CC",
            "power": "#55FF55",
            "thresholdLine": "#FF4444",
            "background": "#3c3c3c",
            "gridLine": "#6a6a6a",
            "textColor": "#BBBBBB",
            "refCurrentA": "#00FFFF",
            "refCurrentB": "#FF5555",
            "refCurrentC": "#FFFF00",
            "refPower": "#FF5555"
        },
        "ui": {
            "sidebarWidthPercent": 18,
            "dateFormat": "yyyy/MM/dd",
            "xAxisDefaultMax": 14,
            "xAxisExtendedMax": 30
        }
    }

    config_path = OUT_DIR / 'config.json'
    with open(config_path, 'w', encoding='utf-8') as f:
        json.dump(config, f, ensure_ascii=False, indent=2)
    print(f"  [OK] {config_path}")

    # 2. switch_mapping.json - 道岔文件映射
    switch_mapping = {
        "version": "1.0",
        "stationId": "PYZ",
        "stationName": "番禺站",
        "fileMapping": {},
        "directionMapping": {
            "DB": {"meaning": "定位表示", "note": "DBJ 继电器吸起=道岔在定位"},
            "FB": {"meaning": "反位表示", "note": "FBJ 继电器吸起=道岔在反位"}
        }
    }

    for g in PANYU_SWITCH_GROUPS:
        switch_mapping["fileMapping"][g["id"]] = {
            "switchId": g["id"],
            "switchName": f"{g['switchNo']}#道岔{'尖轨' if g['type'] == 'J' else '心轨'}",
            "switchNo": g["switchNo"],
            "type": g["type"],
            "directionHint": "定位↔反位"
        }

    mapping_path = OUT_DIR / 'switch_mapping.json'
    with open(mapping_path, 'w', encoding='utf-8') as f:
        json.dump(switch_mapping, f, ensure_ascii=False, indent=2)
    print(f"  [OK] {mapping_path}")

    # 3. switch_digit_config.json - DB/FB 点号映射
    digit_config_raw = extract_switch_digit_config()

    digit_config = {
        "version": "1.0",
        "station_id": "PYZ",
        "station_name": "番禺站",
        "source_file": "设备.rhhcfg + 开关量.rhhcfg",
        "generated_at": datetime.now().strftime('%Y-%m-%dT%H:%M:%S'),
        "switches": {}
    }

    for sw_name, cfg in digit_config_raw.items():
        digit_config["switches"][sw_name] = {
            "db_name": cfg.get('db_name', ''),
            "fb_name": cfg.get('fb_name', ''),
            "db_point_id": cfg.get('db_point_id'),
            "fb_point_id": cfg.get('fb_point_id'),
            "dqj_name": cfg.get('dqj_name', ''),
            "dqj_point_id": cfg.get('dqj_point_id'),
        }

    digit_path = OUT_DIR / 'switch_digit_config.json'
    with open(digit_path, 'w', encoding='utf-8') as f:
        json.dump(digit_config, f, ensure_ascii=False, indent=2)
    print(f"  [OK] {digit_path}")

    # 4. 复制到生产配置目录 (如果存在)
    if PROD_CONFIG_DIR.exists():
        import shutil
        # 创建番禺站专用配置
        panyu_config_dir = PROD_CONFIG_DIR / 'panyu'
        panyu_config_dir.mkdir(parents=True, exist_ok=True)

        shutil.copy(str(mapping_path), str(panyu_config_dir / 'switch_mapping.json'))
        shutil.copy(str(digit_path), str(panyu_config_dir / 'switch_digit_config.json'))
        shutil.copy(str(config_path), str(panyu_config_dir / 'config.json'))
        print(f"\n  配置已同步到: {panyu_config_dir}")


# ═══════════════════════════════════════════════════════════
# 4. 数据汇总 & 索引生成
# ═══════════════════════════════════════════════════════════

def generate_data_inventory():
    """生成番禺站数据清单"""
    print(f"\n{'='*60}")
    print(f"生成数据清单")
    print(f"{'='*60}")

    inventory = {
        'station': '番禺站 (Panyu)',
        'station_id': 'PYZ',
        'processed_at': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        'data_sources': {},
    }

    # .almz 文件
    almz_dir = RAW_DIR / '202607'
    if almz_dir.exists():
        almz_files = list(almz_dir.glob('*.almz'))
        total_size = sum(f.stat().st_size for f in almz_files)
        inventory['data_sources']['almz_alarms'] = {
            'path': str(almz_dir.relative_to(REPO_ROOT)),
            'file_count': len(almz_files),
            'total_size_mb': round(total_size / 1024 / 1024, 1),
            'date_range': '2026-07',
        }

    # .hbf 功率曲线
    power_dir = RAW_DIR / '道岔动作功率曲线'
    if power_dir.exists():
        power_files = list(power_dir.glob('*.hbf'))
        total_size = sum(f.stat().st_size for f in power_files)
        inventory['data_sources']['hbf_power'] = {
            'path': str(power_dir.relative_to(REPO_ROOT)),
            'file_count': len(power_files),
            'total_size_mb': round(total_size / 1024 / 1024, 1),
        }

    # .hbf 电流曲线
    current_dir = RAW_DIR / '道岔动作电流曲线'
    if current_dir.exists():
        current_files = list(current_dir.glob('*.hbf'))
        total_size = sum(f.stat().st_size for f in current_files)
        inventory['data_sources']['hbf_current'] = {
            'path': str(current_dir.relative_to(REPO_ROOT)),
            'file_count': len(current_files),
            'total_size_mb': round(total_size / 1024 / 1024, 1),
        }

    # 配置文件
    config_dir = RAW_DIR / 'config-番禺站修改20260701'
    if config_dir.exists():
        all_config_files = list(config_dir.rglob('*'))
        inventory['data_sources']['config'] = {
            'path': str(config_dir.relative_to(REPO_ROOT)),
            'file_count': len(all_config_files),
        }

    # 道岔信息
    inventory['switch_info'] = {
        'total_switches': len(PANYU_SWITCH_GROUPS),
        'switches': [
            {
                'id': g['id'],
                'name': f"{g['switchNo']}#道岔{'尖轨' if g['type']=='J' else '心轨'}",
                'switch_no': g['switchNo'],
                'type': g['type'],
            }
            for g in PANYU_SWITCH_GROUPS
        ]
    }

    # 输出
    inv_path = OUT_DIR / 'data_inventory.json'
    with open(inv_path, 'w', encoding='utf-8') as f:
        json.dump(inventory, f, ensure_ascii=False, indent=2)

    print(f"  [OK] {inv_path}")
    print(f"\n  数据清单摘要:")
    for src, info in inventory['data_sources'].items():
        if 'file_count' in info:
            size_str = f"{info.get('total_size_mb', 0)} MB" if 'total_size_mb' in info else ''
            print(f"    {src}: {info['file_count']} 文件 {size_str}")
    print(f"    道岔: {inventory['switch_info']['total_switches']} 组")


# ═══════════════════════════════════════════════════════════
# Main
# ═══════════════════════════════════════════════════════════

def main():
    parser = argparse.ArgumentParser(
        description='番禺站(Panyu)原始数据预处理',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  python process_panyu.py --all           # 执行全部分析
  python process_panyu.py --almz          # 仅解析报警文件
  python process_panyu.py --hbf           # 仅解析曲线文件
  python process_panyu.py --config        # 仅生成配置文件
        """
    )
    parser.add_argument('--all', action='store_true', help='执行全部处理步骤')
    parser.add_argument('--almz', action='store_true', help='处理 .almz 报警文件')
    parser.add_argument('--hbf', action='store_true', help='处理 .hbf 曲线文件')
    parser.add_argument('--config', action='store_true', help='生成道岔配置')
    parser.add_argument('--inventory', action='store_true', help='生成数据清单')
    args = parser.parse_args()

    # 默认: --all
    if not any([args.all, args.almz, args.hbf, args.config, args.inventory]):
        args.all = True

    print("=" * 60)
    print("番禺站(Panyu)原始数据预处理")
    print(f"输入: {RAW_DIR}")
    print(f"输出: {OUT_DIR}")
    print("=" * 60)

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    if args.all or args.almz:
        process_almz_files()

    if args.all or args.hbf:
        process_hbf_files()

    if args.all or args.config:
        create_panyu_config()

    if args.all or args.inventory:
        generate_data_inventory()

    print(f"\n{'='*60}")
    print(f"处理完成! 输出目录: {OUT_DIR}")
    print(f"{'='*60}")


if __name__ == '__main__':
    main()
