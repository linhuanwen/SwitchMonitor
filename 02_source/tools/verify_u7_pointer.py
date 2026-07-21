#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
验证假设: 子索引记录的u32[7]字段指向实际采样数据的位置
"""
import struct, sys
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

pf = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
file_size = 536910848

# 读取0xd9d4处的子索引记录
f4 = 0xd9d4
raw = read_at(pf, f4, 100000)

MARKER = b'\x27\x12\x00\x00'

# 解析子索引记录
sub_records = []
for i in range(0, len(raw) - 32, 32):
    if raw[i:i+4] == MARKER:
        rec = raw[i:i+32]
        u32s = [struct.unpack('<I', rec[j:j+4])[0] for j in range(0, 32, 4)]
        sub_records.append({
            'index': i//32,
            'u32': u32s,
            'u7': u32s[7],  # 可能是数据偏移
        })
    else:
        break

print(f"子索引记录: {len(sub_records)}")
print(f"\n前5条记录的u32[7] (可能的文件偏移):")
for r in sub_records[:5]:
    print(f"  #{r['index']}: u32[7]=0x{r['u7']:08x} ({r['u7']})")

# 检查由u32[7]指向的数据
for r in sub_records[:3]:
    offset = r['u7']
    if offset > 0 and offset < file_size - 1000:
        data = read_at(pf, offset, 512)
        nz = sum(1 for b in data if b != 0)
        print(f"\n  偏移 0x{offset:09x} (u32[7]): nz={nz}/512")
        # 显示hex
        for j in range(0, min(128, len(data)), 32):
            line = data[j:j+32]
            print(f"    {' '.join(f'{b:02x}' for b in line)}")
        # 尝试float32
        floats = [struct.unpack('<f', data[j:j+4])[0] for j in range(0, min(400, len(data)), 4)]
        print(f"    float32前20: {[round(v,4) for v in floats[:20]]}")

# 也检查第一个offset和它+0x1227的offset之间的数据
# 如果u32[7]确实是数据偏移，那每条记录之间应该有0x1227字节的数据
first_offset = sub_records[0]['u7']
second_offset = sub_records[1]['u7']
gap = second_offset - first_offset
print(f"\n\n前两条记录的数据偏移差距: 0x{second_offset:08x} - 0x{first_offset:08x} = {gap} = 0x{gap:x}")

# 读取第一个offset开始的完整block
block_size = gap
if block_size > 0 and block_size < 100000:
    block = read_at(pf, first_offset, block_size * 2)  # 读两个block以验证
    print(f"\n第一个数据块 (@ 0x{first_offset:09x}, size={block_size}):")
    print(f"前256B:")
    for j in range(0, min(256, len(block)), 32):
        line = block[j:j+32]
        print(f"  +{j:5d}: {' '.join(f'{b:02x}' for b in line)}")

    # 尝试多种编码
    print(f"\n编码尝试:")
    for enc_name, enc_size, enc_func in [
        ('float32(4)', 4, lambda b, i: struct.unpack('<f', b[i*4:(i+1)*4])[0]),
        ('int16(2)', 2, lambda b, i: struct.unpack('<h', b[i*2:(i+1)*2])[0]),
        ('uint16(2)', 2, lambda b, i: struct.unpack('<H', b[i*2:(i+1)*2])[0]),
    ]:
        n = min(800, len(block) // enc_size)
        vals = [enc_func(block, i) for i in range(n)]
        z10 = sum(1 for v in vals[:10] if abs(v) < 0.05)
        peaks = [v for v in vals[:50] if v > 1.0]
        mid_vals = vals[30:len(vals)-20] if len(vals) > 50 else []
        steady = [v for v in mid_vals if 0.1 < abs(v) < 1.0] if mid_vals else []
        t0 = sum(1 for v in vals[-10:] if abs(v) < 0.05)

        if z10 >= 3 and len(peaks) >= 1:
            print(f"  ✅ {enc_name}: z10={z10} peaks={len(peaks)}({max(peaks):.2f}) steady={len(steady)} t0={t0}")
            print(f"    前50: {[round(v,4) for v in vals[:50]]}")
            print(f"    后20: {[round(v,4) for v in vals[-20:]]}")
        elif z10 >= 3:
            print(f"  ~ {enc_name}: z10={z10} peaks={len(peaks)} steady={len(steady)} t0={t0}")
            print(f"    前30: {[round(v,4) for v in vals[:30]]}")

print("\n✅ 验证完成")
