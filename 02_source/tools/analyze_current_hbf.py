#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""分析番禺站电流HBF文件格式"""
import struct
import os
import sys
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"
ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def analyze_file(fpath, label):
    size = os.path.getsize(fpath)
    data2mb = read_at(fpath, 0, 0x200000)
    
    print(f"\n{'='*70}")
    print(f"分析 {label}: {os.path.basename(fpath)} (size={size:,})")
    print(f"{'='*70}")
    
    # 解析目录项
    entries = {}
    for sw_id in ALL_IDS:
        pos = data2mb.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data2mb[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        entries[sw_id] = {
            'F3': f[3], 'F4': f[4], 'F5': f[5], 'F6': f[6], 'F7': f[7], 'F8': f[8],
            'F9': f[9], 'F10': f[10], 'F11': f[11], 'F12': f[12]
        }
    
    print(f"找到 {len(entries)} 个目录项")
    for sw_id in sorted(entries.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        e = entries[sw_id]
        print(f"  {sw_id}: F4=0x{e['F4']:08x} F7={e['F7']:>5} F9=0x{e['F9']:08x} F6={e['F6']}")
    
    # 检查F9数据区
    print(f"\nF9 数据区分析:")
    for sw_id in sorted(entries.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        e = entries[sw_id]
        f9 = e['F9']
        f7 = e['F7']
        if f9 == 0 or f9 >= size:
            continue
        
        raw = read_at(fpath, f9, min(4096, size - f9))
        nz_bytes = sum(1 for b in raw if b != 0)
        if nz_bytes == 0:
            continue
        
        print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} 非零字节={nz_bytes}/4096")
        
        # 尝试各种解释
        n_floats = min(256, len(raw) // 4)
        f32 = [struct.unpack_from('<f', raw, j*4)[0] for j in range(n_floats)]
        
        n_int16 = min(256, len(raw) // 2)
        i16 = [struct.unpack_from('<h', raw, j*2)[0] for j in range(n_int16)]
        
        # 检查是否有非零值
        f32_nonzero = [v for v in f32 if abs(v) > 0.01]
        i16_nonzero = [v for v in i16 if abs(v) > 10]
        
        if f32_nonzero:
            print(f"    float32 非零: {len(f32_nonzero)} 个, 范围 [{min(f32_nonzero):.3f}, {max(f32_nonzero):.3f}]")
            print(f"    前50 float32: {[round(v,3) for v in f32[:50]]}")
        
        if i16_nonzero:
            print(f"    int16 非零: {len(i16_nonzero)} 个, 范围 [{min(i16_nonzero)}, {max(i16_nonzero)}]")
            print(f"    前50 int16: {i16[:50]}")
        
        # 原始hex
        print(f"    原始 hex 前64B: {' '.join(f'{b:02x}' for b in raw[:64])}")

def main():
    for fname in sorted(os.listdir(CURRENT_DIR)):
        if fname.endswith('.hbf'):
            fpath = os.path.join(CURRENT_DIR, fname)
            analyze_file(fpath, fname)

if __name__ == '__main__':
    main()
