#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 数据追踪 v4 — 验证 u32[7] = data_ptr, 并提取完整曲线
关键发现: Type A 记录的相邻 u32[7] 差值 = 0x1227 (4647 字节)!
"""
import struct
import sys
import os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def check_all_formats(raw, label, max_samples=1200):
    """尝试所有可能的编码格式"""
    print(f"\n  [{label}] 数据格式测试 ({len(raw)} bytes):")

    # Format 1: sequential float32 LE
    f32 = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(max_samples, len(raw)//4))]
    non_zero_f32 = sum(1 for v in f32 if abs(v) > 0.001)
    peak_f32 = max(abs(v) for v in f32) if f32 else 0
    print(f"    float32 seq: 非零={non_zero_f32} 峰值={peak_f32:.3f} 前20={[round(v,3) for v in f32[:20]]}")

    # Format 2: interleaved 32B records, channel 4 (offset 16-19) = power
    records = min(max_samples, len(raw) // 32)
    ch4 = [struct.unpack('<f', raw[j*32+16:j*32+20])[0] for j in range(records)]
    ch5 = [struct.unpack('<f', raw[j*32+20:j*32+24])[0] for j in range(records)]
    ch0 = [struct.unpack('<f', raw[j*32:j*32+4])[0] for j in range(records)]
    ch1 = [struct.unpack('<f', raw[j*32+4:j*32+8])[0] for j in range(records)]
    non_zero_ch4 = sum(1 for v in ch4 if abs(v) > 0.001)
    peak_ch4 = max(abs(v) for v in ch4) if ch4 else 0
    print(f"    32B记录/ch4(功率): 非零={non_zero_ch4} 峰值={peak_ch4:.3f} 前20={[round(v,3) for v in ch4[:20]]}")
    print(f"    32B记录/ch5:        前20={[round(v,3) for v in ch5[:20]]}")
    print(f"    32B记录/ch0:        前20={[round(v,3) for v in ch0[:20]]}")

    # Format 3: int16 LE
    i16 = [struct.unpack('<h', raw[j*2:(j+1)*2])[0] for j in range(min(max_samples*2, len(raw)//2))]
    non_zero_i16 = sum(1 for v in i16 if abs(v) > 5)
    print(f"    int16 seq:    非零(>|5|)={non_zero_i16} 前20={i16[:20]}")

    # Format 4: uint16 LE
    u16 = [struct.unpack('<H', raw[j*2:(j+1)*2])[0] for j in range(min(max_samples*2, len(raw)//2))]
    print(f"    uint16 seq:   前20={u16[:20]}")

    # Determine best format
    results = {}
    if non_zero_f32 > 20 and 0.01 < peak_f32 < 100:
        results['float32_seq'] = f32
    if non_zero_ch4 > 20 and 0.01 < peak_ch4 < 100:
        results['32B_ch4_power'] = ch4

    return results


def main():
    # 重点: 追踪 1.hbf 中 21-J 的 Type A 记录 u32[7] data_ptr
    fpath1 = os.path.join(POWER_DIR, '1.hbf')
    file_size1 = os.path.getsize(fpath1)

    print("HBF 数据追踪 v4 — 验证 u32[7]=data_ptr")
    print("="*70)

    # 读 21-J 的目录项
    data = read_at(fpath1, 0, 0x200000)
    pos = data.find(b'21-J')
    block = data[pos:pos+256]
    f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
    f4, f7, f9 = f[4], f[7], f[9]
    print(f"21-J (1.hbf): F4=0x{f4:x} F7={f7} F9=0x{f9:x}")

    # 读 F4 子索引块
    sub_raw = read_at(fpath1, f4, f7 * 32)

    # 提取所有 Type A 记录的 u32[7]
    type_a_records = []
    for i in range(f7):
        off = i * 32
        rec = sub_raw[off:off+32]
        u32s = list(struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8))
        if u32s[0] == 0x1227:
            type_a_records.append((i, u32s))

    print(f"Type A 记录: {len(type_a_records)} 条 (共 {f7} 槽位)")

    if type_a_records:
        # 验证相邻 data_ptr 差值
        u7_values = [u32s[7] for _, u32s in type_a_records]
        diffs = [u7_values[i+1] - u7_values[i] for i in range(min(20, len(u7_values)-1))]
        print(f"u32[7] data_ptr 值 (前10): {[f'0x{v:x}' for v in u7_values[:10]]}")
        print(f"相邻差值: {[f'0x{d:x} ({d})' for d in diffs[:10]]}")

        # 验证差值是否恒定
        unique_diffs = set(diffs[:50])
        print(f"唯一差值: {[f'0x{d:x}' for d in sorted(unique_diffs)[:10]]}")

        # 对每个 data_ptr 检查数据
        print(f"\n--- 验证 u32[7] data_ptr 处的数据 ---")
        for idx, (rec_idx, u32s) in enumerate(type_a_records[:8]):
            dp = u32s[7]
            if dp + 0x1227 > file_size1:
                continue

            raw_data = read_at(fpath1, dp, 0x1227)  # 读一个完整事件块
            print(f"\n[TypeA #{rec_idx}] data_ptr=0x{dp:08x} ({dp}) 块大小=0x1227={0x1227}")

            # 块的前64字节
            print(f"  块头hex: {' '.join(f'{b:02x}' for b in raw_data[:64])}")

            # 尝试多种格式
            results = check_all_formats(raw_data, f"21-J event[{rec_idx}]", max_samples=1160)

            if not results:
                # 也可能是块头+数据
                # 跳过块头(假设32B header)
                body = raw_data[32:]
                results = check_all_formats(body, f"21-J event[{rec_idx}] body(skip32)", max_samples=1100)
            if not results:
                body = raw_data[64:]
                results = check_all_formats(body, f"21-J event[{rec_idx}] body(skip64)", max_samples=1100)
            if not results:
                body = raw_data[128:]
                results = check_all_formats(body, f"21-J event[{rec_idx}] body(skip128)", max_samples=1000)

    # 同时验证 2.hbf 中 21-J 的数据
    print(f"\n\n{'='*70}")
    print(f"验证 2.hbf 中 21-J 的 F9 数据 (float32 seq)")
    print(f"{'='*70}")

    fpath2 = os.path.join(POWER_DIR, '2.hbf')
    data2 = read_at(fpath2, 0, 0x200000)
    pos2 = data2.find(b'21-J')
    block2 = data2[pos2:pos2+256]
    f2 = [struct.unpack_from('<I', block2[0x70:0x70+52], j*4)[0] for j in range(13)]
    f4_2, f7_2, f9_2 = f2[4], f2[7], f2[9]
    print(f"21-J (2.hbf): F4=0x{f4_2:x} F7={f7_2} F9=0x{f9_2:x}")

    # 读 F4 子索引
    sub_raw2 = read_at(fpath2, f4_2, min(f7_2 * 32, 0x100000))
    type_a_2 = []
    for i in range(f7_2):
        off = i * 32
        u32s = list(struct.unpack('<I', sub_raw2[off:off+32], j*4)[0] for j in range(8))
        if u32s[0] == 0x1227:
            type_a_2.append((i, u32s))

    print(f"Type A 记录: {len(type_a_2)} 条 (共 {f7_2} 槽位)")

    if type_a_2:
        u7_vals_2 = [u32s[7] for _, u32s in type_a_2]
        diffs_2 = [u7_vals_2[i+1] - u7_vals_2[i] for i in range(min(20, len(u7_vals_2)-1))]
        print(f"u32[7] 前10: {[f'0x{v:x}' for v in u7_vals_2[:10]]}")
        print(f"相邻差值: {[f'0x{d:x} ({d})' for d in diffs_2[:10]]}")

        for idx, (rec_idx, u32s) in enumerate(type_a_2[:5]):
            dp = u32s[7]
            if dp + 0x1227 > os.path.getsize(fpath2):
                continue
            raw_data = read_at(fpath2, dp, 0x1227)
            print(f"\n[2.hbf TypeA #{rec_idx}] data_ptr=0x{dp:08x}")
            print(f"  块头hex: {' '.join(f'{b:02x}' for b in raw_data[:64])}")
            check_all_formats(raw_data, f"2.hbf 21-J event[{rec_idx}]", max_samples=1160)

    # 最后: 大范围扫描 2.hbf F9 处的数据, 确定每个事件的实际采样点数
    print(f"\n\n{'='*70}")
    print(f"扫描 2.hbf 1-J F9 大数据块,确定数据结构")
    print(f"{'='*70}")

    pos1j = data2.find(b'1-J')
    block1j = data2[pos1j:pos1j+256]
    f1j = [struct.unpack_from('<I', block1j[0x70:0x70+52], j*4)[0] for j in range(13)]
    f9_1j = f1j[9]
    f4_1j = f1j[4]
    f7_1j = f1j[7]

    print(f"1-J: F4=0x{f4_1j:x} F7={f7_1j} F9=0x{f9_1j:x}")

    # 读 F4 子索引看实际事件数
    sub_1j = read_at(fpath2, f4_1j, min(f7_1j * 32, 0x200000))
    type_b_1j = []  # Type B starts with timestamp
    for i in range(f7_1j):
        off = i * 32
        u32s = list(struct.unpack('<I', sub_1j[off:off+32], j*4)[0] for j in range(8))
        if 1_700_000_000 < u32s[0] < 1_800_000_000:
            type_b_1j.append((i, u32s))

    print(f"Type B (时间戳) 记录: {len(type_b_1j)} 条")

    if type_b_1j:
        # 显示前5个时间戳
        for idx, (rec_idx, u32s) in enumerate(type_b_1j[:10]):
            ts = u32s[0]
            dp = u32s[6]  # Type B data_ptr at u32[6]
            print(f"  [{rec_idx}] ts={datetime.fromtimestamp(ts)} u32[6]=0x{dp:08x}")

        # 如果 u32[6] 是 data_ptr, 追踪
        if type_b_1j:
            first_dp = type_b_1j[0][1][6]
            if 0 < first_dp < os.path.getsize(fpath2):
                print(f"\n  追踪第一个 Type B data_ptr=0x{first_dp:x}:")
                raw_b = read_at(fpath2, first_dp, 65536)
                check_all_formats(raw_b, "1-J TypeB[0] data", max_samples=1200)

    # 也检查 F9 的大块数据 (直接作为连续 float32)
    print(f"\n--- F9=0x{f9_1j:x} 大数据块分析 ---")
    raw_f9 = read_at(fpath2, f9_1j, 512 * 1024)  # 读 512KB

    # 作为连续 float32
    f32_big = [struct.unpack('<f', raw_f9[j*4:(j+1)*4])[0] for j in range(min(5000, len(raw_f9)//4))]

    # 找所有非零段
    segments = []
    in_nz = False
    start = 0
    for j, v in enumerate(f32_big):
        if abs(v) > 0.01 and not in_nz:
            in_nz = True
            start = j
        elif abs(v) < 0.01 and in_nz:
            in_nz = False
            if j - start > 20:
                segments.append((start, j, j - start))

    print(f"找到 {len(segments)} 个非零段:")
    for s, e, l in segments[:15]:
        # 每段的峰值
        peak_val = max(f32_big[s:e])
        peak_idx = s + f32_big[s:e].index(peak_val)
        first5 = [round(v,3) for v in f32_big[s:s+5]]
        last5 = [round(v,3) for v in f32_big[e-5:e]]
        print(f"  [{s:5d}-{e:5d}] len={l:4d} 峰值={peak_val:.3f}@{peak_idx} 起始={first5} 结束={last5}")

        if l > 100:
            mid_start = s + l//4
            mid_end = s + 3*l//4
            mids = [round(v,3) for v in f32_big[mid_start:mid_start+10]]
            print(f"        中段10点: {mids}")

    # 检查段间距 (gap between segments)
    if len(segments) >= 2:
        gaps = [segments[i+1][0] - segments[i][1] for i in range(min(15, len(segments)-1))]
        print(f"\n段间gap: {gaps[:15]}")
        if gaps:
            avg_gap = sum(gaps[:10]) / min(10, len(gaps))
            print(f"平均gap: {avg_gap:.0f} floats = {avg_gap*4:.0f} bytes")


if __name__ == '__main__':
    main()
