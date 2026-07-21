#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 最终验证 — 读取 F4 偏移处的实际曲线数据
结构已确定:
  F4 = 数据文件偏移
  F7 = 采样点数
  F6 = F7 * 32 (数据字节数)
  F3 = 此道岔在数据块中的数据大小或偏移
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_chunk(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_entries(filepath):
    """解析所有256B目录项"""
    data = read_chunk(filepath, 0, 0x8000)
    entries = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = 0
        while True:
            p = data.find(pattern, pos)
            if p == -1:
                break
            entry = data[p:p+256]
            desc = entry[0x70:0x70+52]
            fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
            entries.append({'offset': p, 'switch_id': sw_id,
                'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
                'F6': fields[6], 'F7': fields[7], 'F8': fields[8]})
            pos = p + 1
    entries.sort(key=lambda e: e['offset'])
    return entries

def try_read_curve_data(filepath, entries, label):
    """根据 F4 偏移读取并解析曲线数据"""
    print(f"\n{'='*60}")
    print(f"曲线数据读取: {os.path.basename(filepath)} ({label})")

    file_size = os.path.getsize(filepath)

    # 按 F4 分组 (共享数据块的条目)
    by_f4 = {}
    for e in entries:
        f4 = e['F4']
        if f4 not in by_f4:
            by_f4[f4] = []
        by_f4[f4].append(e)

    print(f"数据块数: {len(by_f4)}")

    for f4, group in sorted(by_f4.items())[:8]:
        if f4 == 0 or f4 >= file_size - 1000:
            continue

        switches = [e['switch_id'] for e in group]
        total_f3 = sum(e['F3'] for e in group)
        total_f7 = sum(e['F7'] for e in group)
        max_f6 = max(e['F6'] for e in group)

        print(f"\n  Block @ 0x{f4:x}: switches={switches}")
        print(f"    F3总和={total_f3}, F7总和={total_f7}, F6最大={max_f6}")

        # 尝试读取数据
        # 方案A: 直接读 float32
        raw = read_chunk(filepath, f4, min(max_f6 + 4096, 32768))

        # 方案B: int16
        for enc_name, enc_size, enc_func in [
            ('float32', 4, lambda b, i: struct.unpack('<f', b[i*4:(i+1)*4])[0]),
            ('int16', 2, lambda b, i: struct.unpack('<h', b[i*2:(i+1)*2])[0]),
            ('uint16', 2, lambda b, i: struct.unpack('<H', b[i*2:(i+1)*2])[0]),
        ]:
            vals = []
            n = min(400, len(raw) // enc_size)
            for i in range(n):
                vals.append(enc_func(raw, i))

            # 道岔曲线特征
            z_start = sum(1 for v in vals[:10] if abs(v) < enc_size)
            peaks = [v for v in vals[:50] if abs(v) > (1.0 if enc_name == 'float32' else 200)]
            nonzero_mid = sum(1 for v in vals[30:200] if abs(v) > (0.01 if enc_name == 'float32' else 5))

            if z_start >= 5 and peaks and nonzero_mid > 20:
                print(f"    ✅ {enc_name}: z_start={z_start} peaks={[round(v,2) if enc_name=='float32' else v for v in peaks[:5]]} nonzero_mid={nonzero_mid}")
                print(f"       前50: {[round(v,3) if enc_name=='float32' else v for v in vals[:50]]}")
                print(f"       100-150: {[round(v,3) if enc_name=='float32' else v for v in vals[100:150]]}")
                break
        else:
            # 无匹配，打印原始数据供分析
            print(f"    ❌ 无匹配编码, 原始hex(前128B):")
            for i in range(0, 128, 32):
                print(f"      +{i:3d}: {' '.join(f'{b:02x}' for b in raw[i:i+32])}")

            # 试试作为int16解析看整体形状
            int16s = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(min(400, len(raw)//2))]
            uint16s = [struct.unpack('<H', raw[i*2:(i+1)*2])[0] for i in range(min(400, len(raw)//2))]
            print(f"    int16 前50: {int16s[:50]}")
            print(f"    uint16前50: {uint16s[:50]}")

if __name__ == '__main__':
    pf_path = os.path.join(HBF_POWER_DIR, '1.hbf')
    cf_path = os.path.join(HBF_CURRENT_DIR, '1.hbf')

    print("HBF 曲线数据最终验证")
    print("="*60)

    power_entries = parse_entries(pf_path)
    current_entries = parse_entries(cf_path)

    print(f"功率文件: {len(power_entries)} 条目")
    for e in power_entries[:5]:
        print(f"  {e['switch_id']}: F3={e['F3']} F4={e['F4']} F7={e['F7']} (samples)")

    print(f"\n电流文件: {len(current_entries)} 条目")
    for e in current_entries[:5]:
        print(f"  {e['switch_id']}: F3={e['F3']} F4={e['F4']} F7={e['F7']} (samples)")

    try_read_curve_data(pf_path, power_entries, "功率")
    try_read_curve_data(cf_path, current_entries, "电流")

    print("\n✅ 最终验证完成")
