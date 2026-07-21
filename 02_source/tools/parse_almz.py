#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
解析 202607 文件夹中的 ALMZ 报警数据文件
"""
import gzip, struct, sys, os, json
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

ALMZ_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\202607"

def parse_almz(filepath):
    """解压并解析 ALMZ 文件"""
    with gzip.open(filepath, 'rb') as f:
        data = f.read()

    result = {
        'file': os.path.basename(filepath),
        'compressed_size': os.path.getsize(filepath),
        'decompressed_size': len(data),
        'header': data[:16].hex(),
    }

    # 文件头: "V!\x16\x05\x06\x12\x20\x00" 然后 8 字节
    if data[:2] == b'V!':
        # 跳过头部找记录
        # 头部后跟着大量零，然后记录从某个偏移开始
        # 找第一个非零记录位置
        first_nz = 0
        for i in range(16, min(len(data), 1024)):
            if data[i] != 0:
                first_nz = i
                break

        result['first_record_offset'] = first_nz

        # 找 "HHNODEV1" 或类似标记（末尾的设备信息）
        # 也找道岔ID
        switch_ids = []
        for sid in ['1-J', '1-X', '3-J', '5-J', '7-J', '9-J', '11-J', '13-J', '15-J', '17-J', '19-J', '21-J']:
            count = data.count(sid.encode('ascii'))
            if count > 0:
                switch_ids.append((sid, count))

        result['switch_ids_found'] = switch_ids

        # 查找日期/时间信息
        # 查找 0x08DE 标记（与HBF相同的时间戳标记）
        marker_positions = []
        marker = b'\xd5\xd7\xde\x08'  # 0x08ded7d5 in LE
        pos = 0
        while True:
            p = data.find(marker, pos)
            if p == -1:
                break
            marker_positions.append(p)
            pos = p + 1

        result['timestamp_markers'] = len(marker_positions)

        # ALMZ中找 "报警" 或 中文字符串
        # 尝试提取文本片段
        text_fragments = []
        for encoding in ['gbk', 'utf-8', 'utf-16-le']:
            try:
                decoded = data.decode(encoding, errors='ignore')
                # 找可读的中文片段
                import re
                fragments = re.findall(r'[一-鿿㐀-䶿]{2,}', decoded)
                if fragments:
                    text_fragments.extend(fragments[:20])
                    break
            except:
                pass

        result['text_fragments'] = text_fragments[:30]

    return result

def main():
    print("ALMZ 文件解析")
    print("=" * 70)

    # 先看目录概况
    files = sorted(os.listdir(ALMZ_DIR))
    almz_files = [f for f in files if f.endswith('.almz')]
    print(f"总文件数: {len(almz_files)}")

    # 显示时间分布
    print("\n文件时间分布:")
    from collections import Counter
    date_dist = Counter()
    for f in almz_files[:10]:  # 只看前10个做样本
        fpath = os.path.join(ALMZ_DIR, f)
        mtime = os.path.getmtime(fpath)
        date = datetime.fromtimestamp(mtime).strftime('%Y-%m-%d')
        date_dist[date] += 1
    for date, count in sorted(date_dist.items()):
        print(f"  {date}: {count} files")

    # 深入分析第一个文件
    print(f"\n{'='*70}")
    print("深入分析第一个ALMZ文件:")
    fpath = os.path.join(ALMZ_DIR, almz_files[0])
    result = parse_almz(fpath)
    for k, v in result.items():
        if isinstance(v, list) and len(v) > 20:
            print(f"  {k}: [{len(v)} items] {v[:10]}...")
        else:
            print(f"  {k}: {v}")

    # 分析第二个文件
    print(f"\n{'='*70}")
    print("深入分析第二个ALMZ文件:")
    fpath = os.path.join(ALMZ_DIR, almz_files[1])
    result = parse_almz(fpath)
    for k, v in result.items():
        if isinstance(v, list) and len(v) > 20:
            print(f"  {k}: [{len(v)} items] {v[:10]}...")
        else:
            print(f"  {k}: {v}")

    # 找典型的文件进行hex分析
    print(f"\n{'='*70}")
    print("ALMZ原始数据结构分析:")
    fpath = os.path.join(ALMZ_DIR, almz_files[0])
    with gzip.open(fpath, 'rb') as f:
        data = f.read()

    # Hex dump key sections
    print("\n头部 (0-255):")
    for i in range(0, 256, 32):
        hex_str = ' '.join(f'{b:02x}' for b in data[i:i+32])
        ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in data[i:i+32])
        print(f"  {i:4d}: {hex_str}  {ascii_str}")

    # 找第一个非零段
    first_nz = 0
    for i in range(16, min(len(data), 2048)):
        if data[i] != 0:
            first_nz = i
            break

    print(f"\n第一个非零记录 @ offset {first_nz}:")
    for i in range(first_nz, min(first_nz+256, len(data)), 32):
        hex_str = ' '.join(f'{b:02x}' for b in data[i:i+32])
        ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in data[i:i+32])
        print(f"  {i:6d}: {hex_str}  {ascii_str}")

    # 找文件末尾的设备信息
    print(f"\n末尾 (最后512字节):")
    tail_start = max(0, len(data) - 512)
    for i in range(tail_start, len(data), 32):
        hex_str = ' '.join(f'{b:02x}' for b in data[i:i+32])
        ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in data[i:i+32])
        print(f"  {i:6d}: {hex_str}  {ascii_str}")

    print("\n✅ 分析完成")

if __name__ == '__main__':
    main()
