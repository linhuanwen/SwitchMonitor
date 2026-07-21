#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在电流HBF文件前50MB中扫描可能的电流曲线"""
import struct
import os
import sys
import numpy as np

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

MIN_LEN = 50
MAX_LEN = 500

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_curves(data, max_offset=50*1024*1024):
    """同时扫描float32和int16的电流曲线"""
    results = []
    
    # float32
    n = len(data) // 4
    f32 = np.frombuffer(data[:n*4], dtype=np.float32).copy()
    i = 0
    while i < n:
        if abs(f32[i]) < 0.005 or np.isnan(f32[i]):
            i += 1
            continue
        start = i
        while i < n and abs(f32[i]) > 0.001 and not np.isnan(f32[i]):
            i += 1
        seg_len = i - start
        if MIN_LEN <= seg_len <= MAX_LEN:
            seg = f32[start:i]
            peak = float(np.max(seg))
            peak_idx = int(np.argmax(seg))
            if 0.5 < peak < 5.0 and 5 < peak_idx < seg_len - 5:
                if float(np.mean(np.abs(seg[:10]))) < 0.2:
                    results.append({
                        'type': 'f32',
                        'offset': start * 4,
                        'len': seg_len,
                        'peak': round(peak, 3),
                        'first5': [round(float(v), 3) for v in seg[:5]],
                        'last5': [round(float(v), 3) for v in seg[-5:]],
                    })
        i += 1
    
    # int16 with scale 0.001
    n = len(data) // 2
    i16 = np.frombuffer(data[:n*2], dtype=np.int16).copy()
    scaled = i16.astype(np.float64) * 0.001
    i = 0
    while i < n:
        if abs(scaled[i]) < 0.005:
            i += 1
            continue
        start = i
        while i < n and abs(scaled[i]) > 0.001:
            i += 1
        seg_len = i - start
        if MIN_LEN <= seg_len <= MAX_LEN:
            seg = scaled[start:i]
            peak = float(np.max(seg))
            peak_idx = int(np.argmax(seg))
            if 0.5 < peak < 5.0 and 5 < peak_idx < seg_len - 5:
                if float(np.mean(np.abs(seg[:10]))) < 0.2:
                    results.append({
                        'type': 'i16',
                        'offset': start * 2,
                        'len': seg_len,
                        'peak': round(peak, 3),
                        'first5': [round(float(v), 3) for v in seg[:5]],
                        'last5': [round(float(v), 3) for v in seg[-5:]],
                    })
        i += 1
    
    return results

def main():
    for fname in sorted(os.listdir(CURRENT_DIR)):
        if not fname.endswith('.hbf'):
            continue
        fpath = os.path.join(CURRENT_DIR, fname)
        size = os.path.getsize(fpath)
        
        print(f"\n{'='*70}")
        print(f"扫描 {fname} 前50MB")
        print(f"{'='*70}")
        
        read_size = min(50 * 1024 * 1024, size)
        data = read_at(fpath, 0, read_size)
        
        curves = find_curves(data)
        
        # Group by offset ranges
        f32_count = sum(1 for c in curves if c['type'] == 'f32')
        i16_count = sum(1 for c in curves if c['type'] == 'i16')
        
        print(f'float32 曲线: {f32_count} 条')
        for c in [c for c in curves if c['type'] == 'f32'][:5]:
            print(f"  offset=0x{c['offset']:x} len={c['len']} peak={c['peak']}A first={c['first5']} last={c['last5']}")
        
        print(f'int16 曲线: {i16_count} 条')
        for c in [c for c in curves if c['type'] == 'i16'][:10]:
            print(f"  offset=0x{c['offset']:x} len={c['len']} peak={c['peak']}A first={c['first5']} last={c['last5']}")

if __name__ == '__main__':
    main()
