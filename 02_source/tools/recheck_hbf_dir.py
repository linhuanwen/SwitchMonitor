#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
回归基础：完整解析 HBF 文件头和目录区，验证所有字段含义
"""
import struct, sys, os

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"

def read_at(fp, off, sz):
    with open(fp, 'rb') as f:
        f.seek(off)
        return f.read(sz)

def analyze_file(fpath, label):
    print(f"\n{'='*70}")
    print(f"{label}: {os.path.basename(fpath)} ({os.path.getsize(fpath):,} bytes)")
    print(f"{'='*70}")

    # 文件头
    header = read_at(fpath, 0, 0x2000)
    magic = header[:8]
    print(f"Magic: {magic}")

    # 搜索 "hhcsmfzz" 确认
    print(f"Magic match: {magic == b'hhcsmfzz'}")

    # 头部之后找第一个 switch ID
    SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
                  '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
                  '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

    # 找所有 switch ID 出现的位置
    print(f"\n目录项位置扫描:")
    entries = {}
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        p = header.find(pattern)
        if p != -1 and p < 0x2000:
            # 256B 对齐检查
            aligned = p % 256
            # 读取descriptor (offset+0x70 开始 52 bytes)
            desc_start = p + 0x70
            desc = header[desc_start:desc_start+52]
            fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]

            entries[sw_id] = {
                'offset': p,
                'aligned': aligned,
                'fields': fields,
            }
            print(f"  {sw_id:>5} @ 0x{p:04x} (256B对齐={256-aligned if aligned else 0}): "
                  f"F0=0x{fields[0]:08x} F1=0x{fields[1]:08x} F2=0x{fields[2]:08x} "
                  f"F3=0x{fields[3]:08x} F4=0x{fields[4]:08x} F5=0x{fields[5]:08x} "
                  f"F6=0x{fields[6]:08x} F7=0x{fields[7]:08x} "
                  f"F8=0x{fields[8]:08x} F9=0x{fields[9]:08x}")

    # 分析 F4 值的分布
    f4_values = set(e['fields'][4] for e in entries.values())
    print(f"\n唯一 F4 值: {[hex(v) for v in sorted(f4_values)]}")

    # 分析 F3 和 F4 的关系
    print(f"\nF3 vs F4 vs Switch ID:")
    for sw_id, e in sorted(entries.items()):
        f = e['fields']
        print(f"  {sw_id:>5}: F3=0x{f[3]:08x} ({f[3]:>10}) F4=0x{f[4]:08x} ({f[4]:>10}) "
              f"F6=0x{f[6]:08x} ({f[6]:>10}) F7={f[7]:>6}")

    # 读取每个唯一的 F4 偏移处的数据
    print(f"\n读取 F4 偏移处的实际数据:")
    for f4 in sorted(f4_values):
        raw = read_at(fpath, f4, 128)
        nz = sum(1 for b in raw if b != 0)
        has_1227 = b'\x27\x12\x00\x00' in raw
        has_3277 = b'\x77\x32\x00\x00' in raw

        print(f"\n  F4=0x{f4:x} ({f4}): nz={nz}/128, 0x1227={has_1227}, 0x3277={has_3277}")
        for i in range(0, min(128, len(raw)), 32):
            hex_str = ' '.join(f'{b:02x}' for b in raw[i:i+32])
            ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in raw[i:i+32])
            print(f"    {i:4d}: {hex_str}  {ascii_str}")

    return entries

# 分析两个文件
entries1 = analyze_file(POWER_1, "功率1")
entries2 = analyze_file(POWER_2, "功率2")

# 关键对比：同名开关在两个文件中的 F 值差异
print(f"\n{'='*70}")
print("跨文件对比 (同名道岔):")
print(f"{'='*70}")
common = set(entries1.keys()) & set(entries2.keys())
for sw_id in sorted(common):
    f1 = entries1[sw_id]['fields']
    f2 = entries2[sw_id]['fields']
    print(f"\n  {sw_id}:")
    for i in range(13):
        v1, v2 = f1[i], f2[i]
        diff = ""
        if v1 != v2:
            diff = f" ← DIFF (Δ={v2-v1})"
        print(f"    F{i}: 功率1=0x{v1:08x}  功率2=0x{v2:08x}{diff}")

print("\n✅ 分析完成")
