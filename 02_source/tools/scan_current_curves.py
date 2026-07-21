#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在电流HBF文件中扫描可能的电流曲线数据"""
import struct
import os
import sys
import numpy as np

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

MIN_LEN = 50
MAX_LEN = 2000

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def find_float32_curves(data, file_offset=0):
    """扫描float32数据，找类似电流曲线的段"""
    n = len(data) // 4
    f32 = np.frombuffer(data[:n*4], dtype=np.float32).copy()
    
    curves = []
    i = 0
    while i < n:
        if abs(f32[i]) < 0.005:
            i += 1
            continue
        
        start = i
        while start > 0 and abs(f32[start-1]) < 0.01:
            start -= 1
        
        end = i
        while end < n and abs(f32[end]) > 0.001:
            end += 1
        
        # 向后找连续零点作为结束
        while end < n:
            zeros = 0
            for j in range(end, min(end+20, n)):
                if abs(f32[j]) < 0.001:
                    zeros += 1
                else:
                    break
            if zeros >= 10:
                break
            end += 1
        
        seg_len = end - start
        if MIN_LEN <= seg_len <= MAX_LEN:
            seg = f32[start:end]
            peak = float(np.max(seg))
            peak_idx = int(np.argmax(seg))
            
            # 电流曲线特征: 峰值在0.5-5A, 峰值不在边界
            if 0.5 < peak < 5.0 and 5 < peak_idx < seg_len - 5:
                # 起点接近零
                if float(np.mean(np.abs(seg[:10]))) < 0.2:
                    curves.append({
                        'start': start,
                        'file_offset': file_offset + start * 4,
                        'len': seg_len,
                        'peak': round(peak, 3),
                        'peak_idx': peak_idx,
                        'first5': [round(float(v), 3) for v in seg[:5]],
                        'last5': [round(float(v), 3) for v in seg[-5:]],
                    })
        
        i = end + 1
    
    return curves

def find_int16_curves(data, file_offset=0):
    """扫描int16数据，找类似电流曲线的段"""
    n = len(data) // 2
    i16 = np.frombuffer(data[:n*2], dtype=np.int16).copy()
    
    # 尝试几种缩放因子
    scales = [1.0, 0.01, 0.001, 10.0, 100.0, 1000.0]
    best_curves = []
    best_scale = 1.0
    
    for scale in scales:
        scaled = i16.astype(np.float64) * scale
        curves = []
        i = 0
        while i < n:
            if abs(scaled[i]) < 0.005:
                i += 1
                continue
            
            start = i
            while start > 0 and abs(scaled[start-1]) < 0.01:
                start -= 1
            
            end = i
            while end < n and abs(scaled[end]) > 0.001:
                end += 1
            
            while end < n:
                zeros = 0
                for j in range(end, min(end+20, n)):
                    if abs(scaled[j]) < 0.001:
                        zeros += 1
                    else:
                        break
                if zeros >= 10:
                    break
                end += 1
            
            seg_len = end - start
            if MIN_LEN <= seg_len <= MAX_LEN:
                seg = scaled[start:end]
                peak = float(np.max(seg))
                peak_idx = int(np.argmax(seg))
                
                if 0.5 < peak < 5.0 and 5 < peak_idx < seg_len - 5:
                    if float(np.mean(np.abs(seg[:10]))) < 0.2:
                        curves.append({
                            'start': start,
                            'file_offset': file_offset + start * 2,
                            'len': seg_len,
                            'peak': round(peak, 3),
                            'peak_idx': peak_idx,
                            'first5': [round(float(v), 3) for v in seg[:5]],
                            'last5': [round(float(v), 3) for v in seg[-5:]],
                        })
            
            i = end + 1
        
        if len(curves) > len(best_curves):
            best_curves = curves
            best_scale = scale
    
    return best_curves, best_scale

def main():
    for fname in sorted(os.listdir(CURRENT_DIR)):
        if not fname.endswith('.hbf'):
            continue
        fpath = os.path.join(CURRENT_DIR, fname)
        size = os.path.getsize(fpath)
        
        print(f"\n{'='*70}")
        print(f"扫描 {fname} (size={size:,})")
        print(f"{'='*70}")
        
        # 只读取前10MB加速
        read_size = min(10 * 1024 * 1024, size)
        data = read_at(fpath, 0, read_size)
        
        # 尝试float32
        f32_curves = find_float32_curves(data, 0)
        if f32_curves:
            print(f"\n  float32 曲线: {len(f32_curves)} 条")
            for c in f32_curves[:5]:
                print(f"    offset=0x{c['file_offset']:x} len={c['len']} peak={c['peak']}A "
                      f"first={c['first5']} last={c['last5']}")
        else:
            print("\n  未找到 float32 电流曲线")
        
        # 尝试int16
        i16_curves, scale = find_int16_curves(data, 0)
        if i16_curves:
            print(f"\n  int16 (scale={scale}) 曲线: {len(i16_curves)} 条")
            for c in i16_curves[:5]:
                print(f"    offset=0x{c['file_offset']:x} len={c['len']} peak={c['peak']}A "
                      f"first={c['first5']} last={c['last5']}")
        else:
            print("\n  未找到 int16 电流曲线")

if __name__ == '__main__':
    main()
