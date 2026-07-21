#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""追踪电流HBF子索引记录中的数据指针"""
import struct
import os
import sys
import numpy as np

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"
ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

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

def check_data_block(data, label, file_offset, expected_len=None):
    """检查数据块是否包含电流曲线"""
    results = []
    
    # 尝试 int16 在多个字节偏移
    for byte_offset in [0, 2, 4, 8, 16]:
        if byte_offset + 2 > len(data):
            continue
        i16 = np.frombuffer(data[byte_offset:], dtype=np.int16).copy()
        
        # 多种缩放
        for scale in [0.001, 0.01, 0.1, 1.0, 10.0]:
            scaled = i16.astype(np.float64) * scale
            
            # 找非零段
            i = 0
            while i < len(scaled):
                if abs(scaled[i]) < 0.005:
                    i += 1
                    continue
                start = i
                while i < len(scaled) and abs(scaled[i]) > 0.001:
                    i += 1
                seg_len = i - start
                if 50 <= seg_len <= 1000:
                    seg = scaled[start:i]
                    peak = float(np.max(seg))
                    if 0.5 < peak < 5.0:
                        # 检查起点是否接近零
                        if float(np.mean(np.abs(seg[:10]))) < 0.3:
                            results.append({
                                'scale': scale,
                                'byte_offset': byte_offset,
                                'sample_offset': start,
                                'len': seg_len,
                                'peak': round(peak, 3),
                                'first5': [round(float(v), 3) for v in seg[:5]],
                                'last5': [round(float(v), 3) for v in seg[-5:]],
                            })
                i += 1
    
    # 尝试 float32
    for byte_offset in [0, 4, 8, 16]:
        if byte_offset + 4 > len(data):
            continue
        try:
            f32 = np.frombuffer(data[byte_offset:], dtype=np.float32).copy()
            i = 0
            while i < len(f32):
                if abs(f32[i]) < 0.005 or np.isnan(f32[i]):
                    i += 1
                    continue
                start = i
                while i < len(f32) and abs(f32[i]) > 0.001 and not np.isnan(f32[i]):
                    i += 1
                seg_len = i - start
                if 50 <= seg_len <= 1000:
                    seg = f32[start:i]
                    peak = float(np.max(seg))
                    if 0.5 < peak < 5.0:
                        if float(np.mean(np.abs(seg[:10]))) < 0.3:
                            results.append({
                                'scale': 'f32',
                                'byte_offset': byte_offset,
                                'sample_offset': start,
                                'len': seg_len,
                                'peak': round(peak, 3),
                                'first5': [round(float(v), 3) for v in seg[:5]],
                                'last5': [round(float(v), 3) for v in seg[-5:]],
                            })
                i += 1
        except:
            pass
    
    return results

def analyze_subindex_pointers(fpath, label, size):
    print(f"\n{'='*70}")
    print(f"分析 {label}")
    print(f"{'='*70}")
    
    entries = parse_directory(fpath, size)
    
    for sw_id in ['1-J', '3-J', '7-J', '11-J', '17-J']:
        if sw_id not in entries:
            continue
        e = entries[sw_id]
        f9 = e['F9']
        f7 = e['F7']
        f6 = e['F6']
        
        if f9 == 0 or f9 >= size:
            continue
        
        # 读取F9处的子索引记录
        raw = read_at(fpath, f9, min(f6 + 4096, size - f9))
        
        print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} F6={f6}")
        
        # 解析前10条记录
        for i in range(min(10, f7)):
            off = i * 32
            if off + 32 > len(raw):
                break
            rec = raw[off:off+32]
            u32 = [struct.unpack_from('<I', rec, j*4)[0] for j in range(8)]
            
            # 检查每个字段是否指向包含电流曲线的数据块
            found = False
            for j, val in enumerate(u32):
                if 0 < val < size - 4096 and val > 1000:
                    block = read_at(fpath, val, 4096)
                    results = check_data_block(block, f"{sw_id} rec[{i}] u32[{j}]=0x{val:x}", val)
                    if results:
                        print(f"    ⭐ [{i}] u32[{j}]=0x{val:x} -> {len(results)} 个曲线")
                        for r in results[:2]:
                            print(f"       scale={r['scale']} byte_offset={r['byte_offset']} "
                                  f"len={r['len']} peak={r['peak']}A "
                                  f"first={r['first5']} last={r['last5']}")
                        found = True
            
            if not found:
                # 打印记录原始值
                pass

def main():
    for fname in sorted(os.listdir(CURRENT_DIR)):
        if fname.endswith('.hbf'):
            fpath = os.path.join(CURRENT_DIR, fname)
            size = os.path.getsize(fpath)
            analyze_subindex_pointers(fpath, fname, size)

if __name__ == '__main__':
    main()
