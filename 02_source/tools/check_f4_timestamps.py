#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查2.hbf中各道岔的F4子索引，提取时间戳"""
import struct
import sys
import os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
SWITCH_IDS = ['1-J','1-X','3-J','3-X','7-J','7-X','11-J','11-X','17-J','17-X',
              '19-X','21-J','21-X','2-J','2-X','4-J','6-J','6-X','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def main():
    fpath = os.path.join(POWER_DIR, '2.hbf')
    size = os.path.getsize(fpath)
    data2mb = read_at(fpath, 0, 0x200000)

    print("="*70)
    print("检查 2.hbf F4 子索引时间戳")
    print("="*70)

    for sw_id in SWITCH_IDS:
        pos = data2mb.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data2mb[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4 = f[4]

        if not (0 < f4 < size):
            print(f"  {sw_id}: F4=0x{f4:x} (无效)")
            continue

        # 读F4子索引
        sub_raw = read_at(fpath, f4, min(16384, size - f4))
        nz = sum(1 for b in sub_raw if b != 0)

        if nz == 0:
            print(f"  {sw_id}: F4=0x{f4:x} 全为零")
            continue

        # 解析32字节记录
        print(f"\n  {sw_id}: F4=0x{f4:x} ({nz}非零字节)")
        records = []
        for i in range(0, min(len(sub_raw), 1024), 32):
            rec = sub_raw[i:i+32]
            if len(rec) < 32 or all(b == 0 for b in rec):
                break
            u32 = struct.unpack('<8I', rec)
            # 搜索0x1227标记
            has_1227 = any(v == 0x1227 for v in u32)
            # 搜索时间戳 (0x08deXXXX)
            ts_candidates = [v for v in u32 if (v >> 16) == 0x08de]
            # 搜索可能的时间戳 (1700M-1800M)
            unix_ts = [v for v in u32 if 1_700_000_000 < v < 1_800_000_000]

            records.append({
                'idx': i//32,
                'has_1227': has_1227,
                'ts_08de': ts_candidates,
                'unix_ts': unix_ts,
                'u32': list(u32),
            })

        # 统计
        type_a = [r for r in records if r['has_1227']]
        type_b = [r for r in records if r['unix_ts']]

        print(f"    共 {len(records)} 条记录")
        print(f"    Type A (含0x1227): {len(type_a)}")
        print(f"    Type B (含Unix时间戳): {len(type_b)}")

        if type_b:
            print(f"    时间戳样例:")
            for r in type_b[:3]:
                for ts in r['unix_ts']:
                    dt = datetime.fromtimestamp(ts)
                    print(f"      rec[{r['idx']}]: ts={ts} → {dt}")

        if type_a:
            print(f"    Type A 样例:")
            for r in type_a[:2]:
                hex_vals = ' '.join(f'{v:08x}' for v in r['u32'])
                print(f"      rec[{r['idx']}]: {hex_vals}")
                if r['ts_08de']:
                    print(f"        ts_08de: {[f'{v:08x}' for v in r['ts_08de']]}")


if __name__ == '__main__':
    main()
