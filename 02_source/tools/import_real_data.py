#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
将 03_raw_data/ 目录下的真实数据导入 switch_test.db。

用法: python import_real_data.py
"""
import sqlite3
import struct
from pathlib import Path
from datetime import datetime
from collections import defaultdict

# 路径说明：脚本位于 02_source/tools/，数据库在 04_tests/Data/，原始数据在 03_raw_data/
_PROJECT = Path(__file__).parent.parent.parent
DB_PATH = _PROJECT / '04_tests' / 'Data' / 'switch_test.db'
SHUJU = _PROJECT / '03_raw_data'


def parse_digit_file(path):
    """解析 Digit(*).dat → StatusEvent 记录"""
    with open(path, 'rb') as f:
        data = f.read()
    if len(data) < 24:
        return [], ''

    ts_start = struct.unpack('<I', data[12:16])[0]
    ts_end   = struct.unpack('<I', data[16:20])[0]

    data_start = None
    for i in range(1000, len(data) - 8):
        ts = struct.unpack('<I', data[i:i+4])[0]
        if ts_start - 60 <= ts <= ts_end + 60:
            typ = struct.unpack('>H', data[i+4:i+6])[0]
            if 1 <= typ <= 20:
                data_start = i
                break
    if data_start is None:
        return [], ''

    rows = []
    i = data_start
    while i + 7 <= len(data):
        ts = struct.unpack('<I', data[i:i+4])[0]
        if not (ts_start - 60 <= ts <= ts_end + 60):
            break
        typ = struct.unpack('>H', data[i+4:i+6])[0]
        if typ > 20 or typ == 0:
            break
        rec_sz = 7 + 2 * typ
        if i + rec_sz > len(data):
            break
        vals = struct.unpack(f'>{typ}H', data[i+6:i+6 + typ * 2])
        for v in vals:
            rows.append((ts, v & 0xFF, (v >> 8) & 0xFF, v))
        i += rec_sz
    return rows, path.name


def parse_analog_file(path):
    """解析 DCBSDYAnalog(*).dat → (timestamp, measurement_type, value) 记录"""
    with open(path, 'rb') as f:
        data = f.read()
    if len(data) < 24:
        return [], ''

    ts_start = struct.unpack('<I', data[12:16])[0]
    ts_end   = struct.unpack('<I', data[16:20])[0]

    positions = []
    for i in range(1000, len(data) - 16):
        ts = struct.unpack('<I', data[i:i+4])[0]
        if ts_start - 60 <= ts <= ts_end + 60:
            ch = struct.unpack('<H', data[i+4:i+6])[0]
            typ = struct.unpack('<H', data[i+6:i+8])[0]
            if ch <= 1000 and 1 <= typ <= 100:
                positions.append((i, ts, typ))

    unique = []
    for p in sorted(positions):
        if not unique or p[0] - unique[-1][0] >= 8:
            unique.append(p)

    rows = []
    for off, ts, typ in unique:
        if off + 16 > len(data):
            continue
        val = struct.unpack('<f', data[off+12:off+16])[0]
        rows.append((ts, typ, round(val, 4)))
    return rows, path.name


def main():
    if not DB_PATH.exists():
        print(f'数据库不存在: {DB_PATH}')
        return

    conn = sqlite3.connect(str(DB_PATH))
    conn.execute('PRAGMA journal_mode=WAL')
    conn.execute('PRAGMA synchronous=OFF')
    conn.execute('PRAGMA cache_size=-64000')

    try:
        # ---- 清空 ----
        print('清空测试数据...')
        conn.execute('DELETE FROM CurveSamples')
        conn.execute('DELETE FROM StatusEvents')
        conn.execute('DELETE FROM SwitchActions')
        conn.execute('DELETE FROM ReferenceCurves')
        conn.commit()

        # ---- 1. Digit → StatusEvents ----
        print('\n' + '='*60)
        print('导入 开关量状态事件 (Digit → StatusEvents)')
        print('='*60)

        digit_files = sorted(SHUJU.glob('*/Digit*.dat'))
        total_se = 0
        for fp in digit_files:
            rows, name = parse_digit_file(fp)
            if not rows:
                continue
            conn.executemany(
                'INSERT INTO StatusEvents (FileSource, Timestamp, PointId, StateByte, RawValue) '
                'VALUES (?,?,?,?,?)',
                [(name, ts, pt, st, rv) for ts, pt, st, rv in rows]
            )
            total_se += len(rows)
            if len(digit_files) <= 10 or fp == digit_files[0] or fp == digit_files[-1]:
                print(f'  {name}: {len(rows)} 条')

        conn.commit()
        print(f'StatusEvents 合计: {total_se} 条 ({len(digit_files)} 个文件)')

        # ---- 2. DCBSDYAnalog → SwitchActions + CurveSamples ----
        print('\n' + '='*60)
        print('导入 模拟量数据 (DCBSDYAnalog → SwitchActions + CurveSamples)')
        print('='*60)

        analog_files = sorted(SHUJU.glob('*/DCBSDYAnalog*.dat'))
        total_actions = 0
        total_samples = 0

        for fp in analog_files:
            rows, name = parse_analog_file(fp)
            if not rows:
                continue

            # 按时间戳分组
            by_ts = defaultdict(dict)
            for ts, typ, val in rows:
                by_ts[ts][typ] = val

            timestamps = sorted(by_ts.keys())
            if not timestamps:
                continue

            # 一条 SwitchAction 代表该文件的连续监测数据
            switch_id = name.replace('DCBSDYAnalog(', '').replace(').dat', '')
            start_ts = timestamps[0]
            end_ts = timestamps[-1]

            # 统计该文件中出现的所有 measurement_type
            all_types = set()
            for vals in by_ts.values():
                all_types.update(vals.keys())
            phase_count = len(all_types)

            conn.execute(
                'INSERT INTO SwitchActions (FileSource, SwitchId, StartTime, EndTime, Direction, '
                'PhaseCount, SampleCount, SampleRate) VALUES (?,?,?,?,?,?,?,?)',
                (name, f'DCBSDY_{switch_id}', start_ts, end_ts, '监测', phase_count, len(timestamps), 1))
            action_id = conn.execute('SELECT last_insert_rowid()').fetchone()[0]

            # 写入 CurveSamples — 测量类型 1/2/3 映射为 A/B/C 相电流
            # 值同时写入 Current（电流）和 RawValue（原始值）列
            phase_map = {1: 'A', 2: 'B', 3: 'C'}
            sample_rows = []
            for idx, ts in enumerate(timestamps):
                for typ, val in by_ts[ts].items():
                    phase = phase_map.get(typ, f'T{typ}')
                    sample_rows.append((action_id, idx, ts, phase, val, val))

            conn.executemany(
                'INSERT INTO CurveSamples (ActionId, SampleIndex, Timestamp, Phase, Current, Voltage, Power, RawValue) '
                'VALUES (?,?,?,?,?,NULL,NULL,?)',
                sample_rows
            )

            total_actions += 1
            total_samples += len(timestamps)

            dt = datetime.fromtimestamp(start_ts).strftime('%m-%d %H:%M')
            print(f'  {name}: {len(rows)} 条 → #{action_id} ({dt}, {len(timestamps)}s, types={sorted(all_types)})')

        conn.commit()
        print(f'\nSwitchActions: {total_actions}')
        print(f'CurveSamples 明细: {conn.execute("SELECT COUNT(*) FROM CurveSamples").fetchone()[0]} 条')

        # ---- 验证 ----
        print('\n' + '='*60)
        print('数据库总览')
        print('='*60)
        for table in ['SwitchActions', 'CurveSamples', 'StatusEvents']:
            cnt = conn.execute(f'SELECT COUNT(*) FROM {table}').fetchone()[0]
            print(f'  {table}: {cnt} 条')

        print('\n✅ 导入完成！')

    finally:
        conn.close()


if __name__ == '__main__':
    main()
