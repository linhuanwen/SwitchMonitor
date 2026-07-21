#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""解析番禺站电流HBF子索引与采样数据"""
import struct
import os
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"
POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

MARKER_CURRENT = 0x00003277
MARKER_POWER = 0x00001227

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_directory(fpath, size):
    data = read_at(fpath, 0, 0x200000)
    entries = {}
    for sw_id in ALL_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        entries[sw_id] = {
            'F4': f[4], 'F6': f[6], 'F7': f[7], 'F9': f[9]
        }
    return entries

def parse_subindex_block(fpath, offset, marker, size):
    """解析32字节子索引记录，返回记录列表"""
    raw = read_at(fpath, offset, min(65536, size - offset))
    records = []
    for i in range(0, len(raw) - 32, 32):
        rec = raw[i:i+32]
        u32 = [struct.unpack_from('<I', rec, j*4)[0] for j in range(8)]
        # 检查是否有标记
        if marker in u32:
            records.append({
                'offset': offset + i,
                'u32': u32,
                'raw': rec
            })
    return records

def analyze_current_file(fpath, label, size):
    print(f"\n{'='*70}")
    print(f"解析电流文件: {label}")
    print(f"{'='*70}")
    
    entries = parse_directory(fpath, size)
    
    # 选几个开关深入分析
    for sw_id in ['1-J', '3-J', '7-J', '21-X']:
        if sw_id not in entries:
            continue
        e = entries[sw_id]
        f9 = e['F9']
        f7 = e['F7']
        f6 = e['F6']
        print(f"\n  [{sw_id}] F4=0x{e['F4']:x} F7={f7} F9=0x{f9:x} F6={f6}")
        
        # 读取F9处数据
        raw = read_at(fpath, f9, min(65536, size - f9))
        
        # 打印前128字节hex
        print(f"  F9原始 hex 前128B:")
        for j in range(0, min(128, len(raw)), 32):
            hex_str = ' '.join(f'{raw[j+k]:02x}' for k in range(min(32, len(raw)-j)))
            ascii_str = ''.join(chr(raw[j+k]) if 32 <= raw[j+k] < 127 else '.' for k in range(min(32, len(raw)-j)))
            print(f"    +{j:3d}: {hex_str}  |{ascii_str}|")
        
        # 尝试解析子索引记录 (marker 0x3277)
        records = parse_subindex_block(fpath, f9, MARKER_CURRENT, size)
        print(f"  在F9处找到 {len(records)} 条含 0x3277 的32B记录")
        
        for r in records[:5]:
            u32 = r['u32']
            print(f"    偏移 0x{r['offset']:x}: {u32}")
            # 尝试识别各字段含义
            # 假设 data_ptr 是某个字段，且是有效文件偏移
            for idx, val in enumerate(u32):
                if 0 < val < size:
                    print(f"      u32[{idx}] = 0x{val:08x} 可能是文件偏移")
        
        # 如果F9数据本身看起来像采样数据，尝试解释
        # 检查前N个float32/int16
        n_f32 = min(256, len(raw) // 4)
        f32 = [struct.unpack_from('<f', raw, j*4)[0] for j in range(n_f32)]
        n_i16 = min(256, len(raw) // 2)
        i16 = [struct.unpack_from('<h', raw, j*2)[0] for j in range(n_i16)]
        
        # 统计合理的电流值范围
        i16_reasonable = [v for v in i16 if -100 <= v <= 100]
        f32_reasonable = [v for v in f32 if 0 < v < 10]
        
        if i16_reasonable:
            print(f"  int16 合理值 (±100A): {len(i16_reasonable)} 个, 范围 [{min(i16_reasonable)}, {max(i16_reasonable)}]")
        if f32_reasonable:
            print(f"  float32 合理值 (0-10A): {len(f32_reasonable)} 个, 范围 [{min(f32_reasonable):.3f}, {max(f32_reasonable):.3f}]")
        
        print(f"  int16 前60: {i16[:60]}")

def main():
    for fname in sorted(os.listdir(CURRENT_DIR)):
        if fname.endswith('.hbf'):
            fpath = os.path.join(CURRENT_DIR, fname)
            size = os.path.getsize(fpath)
            analyze_current_file(fpath, fname, size)

if __name__ == '__main__':
    main()
