#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 分析第6轮 — 验证 256字节目录结构 + 数据指针
关键发现: 道岔ID在256字节目录项中
目录项结构: [4B switch_id] + [108B padding] + [52B descriptor] + [92B padding]
descriptor 中某个字段指向实际数据偏移
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

def parse_directory(filepath, label):
    """解析 256字节目录结构"""
    print(f"\n{'='*60}")
    print(f"256B 目录解析: {os.path.basename(filepath)} ({label})")

    data = read_chunk(filepath, 0, 0x8000)  # 前32KB
    file_size = os.path.getsize(filepath)

    # 找所有 switch ID 的 256B 对齐位置
    entries = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = 0
        while True:
            p = data.find(pattern, pos)
            if p == -1:
                break
            # 对齐到256边界: 目录项起始 = p (switch ID在偏移0)
            entry_start = p
            if entry_start + 256 <= len(data):
                entries.append((entry_start, sw_id))
            pos = p + 1

    entries.sort()
    print(f"找到 {len(entries)} 个目录项")

    # 详细解析每个目录项
    parsed_entries = []
    for entry_start, sw_id in entries[:10]:  # 先分析前10个
        entry = data[entry_start:entry_start+256]

        # descriptor 在偏移 112 (0x70)
        desc = entry[0x70:0x70+52]

        # 解析 descriptor 字段 (uint32 LE)
        fields = {}
        for i in range(0, 52, 4):
            v = struct.unpack('<I', desc[i:i+4])[0]
            fields[i//4] = v

        parsed_entries.append({
            'offset': entry_start,
            'switch_id': sw_id,
            'fields': fields,
        })

    # 打印结果
    print(f"\n目录项 descriptor 字段:")
    header = f"{'Switch':>6s} {'@':>6s}"
    for i in range(13):
        header += f" {'F'+str(i):>10s}"
    print(header)
    print("-" * (13 + 13*12))

    for pe in parsed_entries[:10]:
        row = f"{pe['switch_id']:>6s} 0x{pe['offset']:04x}"
        for i in range(13):
            v = pe['fields'].get(i, 0)
            row += f" {v:10d}"
        print(row)

    # 尝试把字段当文件偏移来读数据
    print(f"\n尝试数据偏移读取:")
    for pe in parsed_entries[:5]:
        sw_id = pe['switch_id']
        f = pe['fields']

        # 尝试各种字段作为数据偏移
        for field_name, field_val in [('F4', f.get(4, 0)), ('F6', f.get(6, 0))]:
            if not (0 < field_val < file_size - 1000):
                continue

            # 尝试 int16 编码
            raw = read_chunk(filepath, field_val, 1600)
            int16s = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(min(400, len(raw)//2))]

            # 道岔特征检测
            zeros_start = sum(1 for v in int16s[:10] if abs(v) < 5)
            peaks = [v for v in int16s[:50] if abs(v) > 200]
            steady_vals = [v for v in int16s[50:200] if 5 < abs(v) < 500]

            if zeros_start >= 5 and peaks and len(steady_vals) > 10:
                print(f"  ✅ {sw_id} {field_name}=0x{field_val:x} int16: "
                      f"zeros={zeros_start} peaks={peaks[:3]} steady={len(steady_vals)}")
                print(f"     前40: {int16s[:40]}")

            # 尝试 uint16 编码
            uint16s = [struct.unpack('<H', raw[i*2:(i+1)*2])[0] for i in range(min(400, len(raw)//2))]
            zeros_start_u = sum(1 for v in uint16s[:10] if v == 0)
            peaks_u = [v for v in uint16s[:50] if v > 200]
            if zeros_start_u >= 5 and peaks_u:
                print(f"  ✅ {sw_id} {field_name}=0x{field_val:x} uint16: "
                      f"zeros={zeros_start_u} peaks={peaks_u[:3]}")
                print(f"     前40: {uint16s[:40]}")

    return parsed_entries

def find_data_in_file(filepath, label):
    """用不同编码扫描整个文件中看起来像道岔曲线的数据段"""
    print(f"\n{'='*60}")
    print(f"全文件 int16 曲线扫描: {os.path.basename(filepath)} ({label})")

    file_size = os.path.getsize(filepath)

    # 采样扫描: 每4MB取64KB
    found = []
    for seg_start in range(0, file_size, 0x400000):  # 4MB
        chunk = read_chunk(filepath, seg_start, 0x10000)

        # 用 int16 滑动窗口
        for byte_off in range(0, len(chunk) - 300, 2):
            # 读前10个 int16
            first10 = [struct.unpack('<h', chunk[byte_off+i*2:byte_off+i*2+2])[0] for i in range(10)]
            if sum(1 for v in first10 if abs(v) < 3) < 7:
                continue

            # 读更多
            vals = [struct.unpack('<h', chunk[byte_off+i*2:byte_off+i*2+2])[0]
                     for i in range(min(400, (len(chunk)-byte_off)//2))]

            # 道岔功率曲线: 0→spike→0.2-0.4→0
            # int16: 0→2000-5000→20-40→0 (假设 scale=1000)
            peaks = [v for v in vals[:40] if abs(v) > 500]
            steady = [v for v in vals[40:300] if 10 < abs(v) < 100]

            if peaks and len(steady) > 50:
                abs_offset = seg_start + byte_off
                found.append((abs_offset, vals[:50]))
                break  # 每段取一个

    print(f"找到 {len(found)} 个候选曲线")
    for off, preview in found[:10]:
        print(f"  0x{off:09x}: {preview[:30]}")

    return found

if __name__ == '__main__':
    pf_path = os.path.join(HBF_POWER_DIR, '1.hbf')
    cf_path = os.path.join(HBF_CURRENT_DIR, '1.hbf')

    print("HBF 256B目录结构验证")
    print("="*60)

    power_entries = parse_directory(pf_path, "功率")
    current_entries = parse_directory(cf_path, "电流")

    # 全文件扫描找真正的曲线数据
    power_curves = find_data_in_file(pf_path, "功率")
    current_curves = find_data_in_file(cf_path, "电流")

    print("\n✅ 第6轮分析完成")
