#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 最终突破 — 跳过子索引记录，直接读取采样数据
结构: 256B dir → F4 偏移 → 32B 子索引记录 × N → 采样数据
采样数据编码: 32 bytes/sample = 8 × float32 或 16 × int16
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"
MARKER_POWER = b'\x27\x12\x00\x00'

def read_chunk(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_dir_entry(filepath, switch_id):
    """找到指定道岔的目录项并解析"""
    data = read_chunk(filepath, 0, 0x8000)
    pattern = switch_id.encode('ascii')
    p = data.find(pattern)
    if p == -1:
        return None
    entry = data[p:p+256]
    desc = entry[0x70:0x70+52]
    fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
    return {'offset': p, 'switch_id': switch_id,
            'F3': fields[3], 'F4': fields[4], 'F5': fields[5],
            'F6': fields[6], 'F7': fields[7], 'F8': fields[8]}

def read_data_block(filepath, f4_offset, block_size, label):
    """读取数据块，跳过子索引找采样数据"""
    raw = read_chunk(filepath, f4_offset, min(block_size + 32768, 262144))

    # 先看前512字节的结构
    print(f"\n  [{label}] Block @ 0x{f4_offset:x}, size={block_size}")
    print(f"  前512字节 (每32B一行):")
    for i in range(0, min(512, len(raw)), 32):
        line = raw[i:i+32]
        # 识别每32B记录中的关键字段
        marker = struct.unpack('<I', line[0:4])[0]
        ts_val = struct.unpack('<I', line[16:20])[0]
        ts_str = ""
        if 1_780_000_000 < ts_val < 1_790_000_000:
            ts_str = f" TS={datetime.fromtimestamp(ts_val).strftime('%m-%d %H:%M')}"
        elif 1_700_000_000 < ts_val < 1_800_000_000:
            ts_str = f" TS≈{datetime.fromtimestamp(ts_val).strftime('%Y-%m')}"

        field_v = struct.unpack('<I', line[24:28])[0]
        print(f"    +{i:5d}: mark=0x{marker:04x} ts=0x{ts_val:08x}{ts_str} f3={field_v} | "
              f"{' '.join(f'{b:02x}' for b in line[:16])}")

    # 找子索引结束位置 (看 marker 何时不再重复)
    sub_index_end = 0
    expected_marker = struct.unpack('<I', raw[0:4])[0]
    for i in range(0, len(raw) - 32, 32):
        m = struct.unpack('<I', raw[i:i+4])[0]
        if m != expected_marker and i > 0:
            sub_index_end = i
            break

    print(f"\n  子索引结束于 +{sub_index_end} bytes ({sub_index_end//32} 条记录)")

    # 读取子索引之后的采样数据
    sample_data = raw[sub_index_end:sub_index_end + block_size]

    # 尝试编码
    print(f"\n  采样数据区 ({len(sample_data)} bytes), 编码尝试:")
    for enc_name, enc_size, enc_func, scale in [
        ('float32', 4, lambda b, i: struct.unpack('<f', b[i*4:(i+1)*4])[0], 1),
        ('int16', 2, lambda b, i: struct.unpack('<h', b[i*2:(i+1)*2])[0], 0.001),
        ('uint16', 2, lambda b, i: struct.unpack('<H', b[i*2:(i+1)*2])[0], 0.001),
    ]:
        n = min(500, len(sample_data) // enc_size)
        vals = [enc_func(sample_data, i) * scale for i in range(n)]

        # 道岔特征
        z10 = sum(1 for v in vals[:10] if abs(v) < 0.02)
        peaks = [v for v in vals[:50] if abs(v) > (0.5 if enc_name == 'float32' else 50)]
        nonzero_mid = sum(1 for v in vals[40:200] if abs(v) > (0.005 if enc_name == 'float32' else 2))

        print(f"    {enc_name}: z10={z10} peaks={len(peaks)} nonzero_mid={nonzero_mid}")
        if z10 >= 4 and len(peaks) >= 1 and nonzero_mid > 20:
            print(f"      ✅✅✅ 匹配! 前60: {[round(v,3) if enc_name=='float32' else round(v,1) for v in vals[:60]]}")
            print(f"      120-180: {[round(v,3) if enc_name=='float32' else round(v,1) for v in vals[120:180]]}")
            print(f"      尾20: {[round(v,3) if enc_name=='float32' else round(v,1) for v in vals[-20:]]}")
        elif z10 >= 4:
            print(f"      ~z10 OK, 前30: {[round(v,3) if enc_name=='float32' else round(v,1) for v in vals[:30]]}")

def try_all_encodings_on_raw(raw, max_samples=400):
    """尝试所有可能的编码"""
    results = {}

    # float32
    f32 = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(min(max_samples, len(raw)//4))]
    results['float32'] = f32

    # int16
    i16 = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(min(max_samples, len(raw)//2))]
    results['int16'] = i16

    # uint16
    u16 = [struct.unpack('<H', raw[i*2:(i+1)*2])[0] for i in range(min(max_samples, len(raw)//2))]
    results['uint16'] = u16

    # int32
    i32 = [struct.unpack('<i', raw[i*4:(i+1)*4])[0] for i in range(min(max_samples, len(raw)//4))]
    results['int32'] = i32

    # uint32
    u32 = [struct.unpack('<I', raw[i*4:(i+1)*4])[0] for i in range(min(max_samples, len(raw)//4))]
    results['uint32'] = u32

    return results

def analyze_with_subindex_skip(filepath, label):
    """完整流程: 目录→F4→子索引→采样数据"""
    print(f"\n{'='*60}")
    print(f"完整数据提取: {os.path.basename(filepath)} ({label})")

    data = read_chunk(filepath, 0, 0x8000)
    file_size = os.path.getsize(filepath)

    # 找到所有目录项
    entries = {}
    for sw_id in ['1-J', '1-X', '5-J', '7-J', '9-J']:
        e = parse_dir_entry(filepath, sw_id)
        if e:
            entries[sw_id] = e
            print(f"  {sw_id}: F3={e['F3']} F4={e['F4']} F6={e['F6']} F7={e['F7']}")

    # 对唯一的 F4 偏移做深度分析
    seen_f4 = set()
    for sw_id, e in entries.items():
        if e['F4'] in seen_f4 or e['F4'] == 0 or e['F4'] >= file_size - 10000:
            continue
        seen_f4.add(e['F4'])

        read_data_block(filepath, e['F4'], e['F6'], f"{sw_id} (F7={e['F7']}samples)")

if __name__ == '__main__':
    pf_path = os.path.join(HBF_POWER_DIR, '1.hbf')
    cf_path = os.path.join(HBF_CURRENT_DIR, '1.hbf')

    print("HBF 最终数据提取 - 跳过子索引")
    print("="*60)

    analyze_with_subindex_skip(pf_path, "功率")
    analyze_with_subindex_skip(cf_path, "电流")

    print("\n✅ 分析完成")
