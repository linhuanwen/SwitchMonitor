#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
直接读取HBF数据块hex，不做任何解析，纯查看
"""
import struct, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

# 重点分析 0xd9d4 (21-J/21-X power) - 有明确的0x1227标记
pf = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"

# 读取足够大的数据 (200KB)
f4 = 0xd9d4
read_size = 200000
raw = read_at(pf, f4, read_size)

print(f"Read {len(raw)} bytes from offset 0x{f4:x}")
print(f"First 4 bytes: {' '.join(f'{b:02x}' for b in raw[:4])}")

# Step 1: Count 0x1227 markers at 32B boundaries
marker = b'\x27\x12\x00\x00'
sub_count = 0
for i in range(0, len(raw) - 4, 32):
    if raw[i:i+4] == marker:
        sub_count += 1
    else:
        break

print(f"\n0x1227 markers at 32B boundaries: {sub_count}")
print(f"Sub-index region: {f4:#x} - {f4 + sub_count * 32:#x} ({sub_count * 32} bytes)")

# Step 2: Show first 3 records
print(f"\nFirst 3 sub-index records (32B each):")
for ridx in range(3):
    off = ridx * 32
    rec = raw[off:off+32]
    print(f"\n  Record {ridx} @ +{off}:")
    for j in range(0, 32, 16):
        print(f"    {' '.join(f'{b:02x}' for b in rec[j:j+16])}")
    # Parse as uint32
    u32s = [struct.unpack('<I', rec[j:j+4])[0] for j in range(0, 32, 4)]
    print(f"    u32: {[f'0x{v:08x}' for v in u32s]}")

# Step 3: Show the transition zone
data_start = sub_count * 32
data_end = data_start + 5000
print(f"\nData region starts at +{data_start}")
print(f"Transition zone (+{data_start-32} to +{data_start+96}):")
for i in range(data_start-32, min(data_start+96, len(raw)), 32):
    line = raw[i:i+32]
    print(f"  +{i:6d}: {' '.join(f'{b:02x}' for b in line[:16])}  {' '.join(f'{b:02x}' for b in line[16:])}")

# Step 4: Show data region first 512B
print(f"\nData region (first 512B starting at +{data_start}):")
for i in range(data_start, min(data_start+512, len(raw)), 16):
    line = raw[i:i+16]
    print(f"  +{i:6d}: {' '.join(f'{b:02x}' for b in line)}")

# Step 5: Check if data is non-zero
data_area = raw[data_start:data_start+5000]
nz = sum(1 for b in data_area if b != 0)
print(f"\nNon-zero bytes in data area: {nz}/{len(data_area)}")

# Step 6: Try to see if there's a pattern in the data
# Maybe the data is at a different offset, not right after sub-index
# Check for the 0x1227 marker appearing again after data_start
extra_markers = []
for i in range(data_start, min(len(raw) - 4, data_start + 50000), 4):
    if raw[i:i+4] == marker:
        extra_markers.append(i)
print(f"\n0x1227 markers after sub-index: {len(extra_markers)}")
if extra_markers:
    print(f"  Positions: {[hex(f4 + x) for x in extra_markers[:10]]}")

# Step 7: Also check the 0xc7ad block
print(f"\n{'='*60}")
f4b = 0xc7ad
rawb = read_at(pf, f4b, 50000)

# For 0xc7ad, look for 0x1227 at byte granularity
positions_1227 = []
for i in range(len(rawb) - 4):
    if rawb[i:i+4] == marker:
        positions_1227.append(i)
print(f"0xc7ad: 0x1227 markers found at byte positions: {[hex(f4b + x) for x in positions_1227[:10]]}")

# Show first 128 bytes at 0xc7ad
print(f"\n0xc7ad first 128B:")
for i in range(0, 128, 16):
    line = rawb[i:i+16]
    print(f"  +{i:3d}: {' '.join(f'{b:02x}' for b in line)}")

# Also check: does the data after sub-index look like it might start at a different offset?
# F6 = 9312 for 0xc7ad. Does raw[sub_count*32:] have 9312 bytes?
# Let me find sub_count for 0xc7ad differently
# The first 32B at 0xc7ad: look for pattern matching
# The records seem to repeat every 32B
rec32 = rawb[:32]
print(f"\n0xc7ad Record 0 raw: {' '.join(f'{b:02x}' for b in rec32)}")

# The records at 0xc7ad seem different. Let me try to find repetition.
# Each 32B record might have a consistent first 2 bytes (04 00)?
consistent = True
for i in range(0, 200, 32):
    if rawb[i:i+2] != b'\x04\x00':
        consistent = False
        break
print(f"0xc7ad: '04 00' prefix consistent for first 200B: {consistent}")

# Count records at 0xc7ad with 04 00 prefix
rec_count = 0
for i in range(0, len(rawb) - 2, 32):
    if rawb[i:i+2] == b'\x04\x00':
        rec_count += 1
    else:
        break
print(f"0xc7ad: {rec_count} records with 04 00 prefix")

data_start_b = rec_count * 32
print(f"0xc7ad: data starts at +{data_start_b}")

# Show data area at 0xc7ad
if data_start_b < len(rawb):
    print(f"\n0xc7ad data area (first 256B from +{data_start_b}):")
    for i in range(data_start_b, min(data_start_b+256, len(rawb)), 16):
        line = rawb[i:i+16]
        print(f"  +{i:5d}: {' '.join(f'{b:02x}' for b in line)}")

print("\n✅ Done")
