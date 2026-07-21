#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 数据追踪 v3 — 直接验证数据指针和实际曲线
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

def check_curve_data(filepath, label, offset, read_kb=256):
    """读取并分析可能包含曲线数据的位置"""
    file_size = os.path.getsize(filepath)
    if offset <= 0 or offset + 1024 >= file_size:
        return None

    raw = read_at(filepath, offset, min(read_kb * 1024, file_size - offset))

    # float32
    floats = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(2000, len(raw)//4))]

    # 找非零段
    non_zero_ranges = []
    in_nonzero = False
    start = None
    for j, v in enumerate(floats[:2000]):
        if abs(v) > 0.001 and not in_nonzero:
            in_nonzero = True
            start = j
        elif abs(v) < 0.001 and in_nonzero:
            in_nonzero = False
            if j - start > 20:  # 至少20个连续非零点
                non_zero_ranges.append((start, j))

    if not non_zero_ranges:
        return None

    # 对每个非零段，检查是否有曲线特征
    results = []
    for seg_start, seg_end in non_zero_ranges[:5]:
        seg = floats[seg_start:seg_end]
        if len(seg) < 30:
            continue

        peak = max(seg)
        peak_idx = seg.index(peak)

        # 曲线特征: 起始低→上升→峰值→下降→归零
        first_vals = seg[:10]
        last_vals = seg[-10:]

        # 功率合理范围
        if 0.01 < peak < 10:
            results.append({
                'start': seg_start + offset // 4,
                'length': len(seg),
                'peak': peak,
                'peak_idx': peak_idx,
                'first10': [round(v, 2) for v in first_vals],
                'last10': [round(v, 2) for v in last_vals],
                'seg': [round(v, 2) for v in seg[:60]],
            })

    return results


def deep_trace_2hbf():
    """专门追踪 2.hbf 的真实数据"""
    fpath = os.path.join(POWER_DIR, '2.hbf')
    file_size = os.path.getsize(fpath)

    print("HBF 数据追踪 v3 — 功率/2.hbf")
    print("="*70)

    # 读取 256B 目录项获取 F4 和 F9
    data = read_at(fpath, 0, 0x200000)

    SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
                  '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
                  '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

    # Step 1: 全面扫描 2.hbf 的 F9 区域，找所有非零数据块
    print("\n--- Step 1: 扫描所有道岔的 F9 数据区 ---")

    switches_with_data = []
    for sw_id in SWITCH_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4, f5, f7, f9 = f[4], f[5], f[7], f[9]

        if not (0 < f4 < file_size and 10 < f7 < 100000):
            continue

        # 检查 F9 数据区
        if 0 < f9 < file_size:
            result = check_curve_data(fpath, f"{sw_id}-F9", f9, read_kb=512)
            if result:
                switches_with_data.append((sw_id, f4, f7, f9, f5, 'F9', result))
                print(f"  ✅ {sw_id}: F9=0x{f9:x} — {len(result)} 段曲线数据!")
            else:
                # F9 全零
                pass

        # 检查 F4 子索引 (不只看前2MB)
        # 读 F4 块，找非零记录
        sub_raw = read_at(fpath, f4, min(f7 * 32, 0x100000))

        # 检查是否全零
        non_zero_count = sum(1 for b in sub_raw if b != 0)
        if non_zero_count > 0:
            # 提取 Type A 记录的 u32[4]
            type_a_ptrs = []
            for i in range(f7):
                off = i * 32
                rec = sub_raw[off:off+32]
                u32s = list(struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8))
                if u32s[0] == 0x1227:
                    u4 = u32s[4]
                    if 0 < u4 < file_size:
                        type_a_ptrs.append((i, u4, u32s))

            if type_a_ptrs:
                print(f"  📋 {sw_id}: F4=0x{f4:x} 有 {len(type_a_ptrs)} 个 0x1227 标记记录 (共{f7}槽位)")
                # 检查几个 u32[4] 指向的数据
                for idx, u4, u32s in type_a_ptrs[:3]:
                    res = check_curve_data(fpath, f"{sw_id}-F4[{idx}]", u4, read_kb=64)
                    if res:
                        print(f"      [{idx}] u32[4]=0x{u4:x} → 曲线! {res[0]['length']}点 峰值{res[0]['peak']:.2f}")
                        switches_with_data.append((sw_id, f4, f7, f9, f5, f'F4[{idx}]', res))
                    else:
                        print(f"      [{idx}] u32[4]=0x{u4:x} → 非曲线数据")

    print(f"\n共 {len(switches_with_data)} 个数据源")

    # Step 2: 对有数据的道岔，做详细曲线分析
    if switches_with_data:
        print(f"\n{'='*70}")
        print(f"详细曲线分析")
        print(f"{'='*70}")

        for sw_id, f4, f7, f9, f5, source, results in switches_with_data[:8]:
            for r in results:
                print(f"\n  {sw_id} [{source}]: {r['length']} 采样点, 峰值 {r['peak']:.3f}")
                print(f"    前段: {r['first10']}")
                print(f"    详细(60点): {r['seg']}")
                print(f"    尾段: {r['last10']}")

    # Step 3: 对比 1.hbf 的 F4 数据
    print(f"\n\n{'='*70}")
    print(f"对比: 功率/1.hbf 的 F4 子索引数据")
    print(f"{'='*70}")

    fpath1 = os.path.join(POWER_DIR, '1.hbf')
    file_size1 = os.path.getsize(fpath1)
    data1 = read_at(fpath1, 0, 0x200000)

    for sw_id in ['1-J', '5-J', '21-J']:  # 选几个不同事件数的
        pos = data1.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data1[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4, f7, f9 = f[4], f[7], f[9]

        print(f"\n  {sw_id}: F4=0x{f4:x} F7={f7} F9=0x{f9:x}")

        # 读 F4 块
        sub_raw = read_at(fpath1, f4, min(f7 * 32, 0x10000))
        non_zero = sum(1 for b in sub_raw if b != 0)
        print(f"    F4块非零字节: {non_zero}/{len(sub_raw)} ({100*non_zero/len(sub_raw):.1f}%)")

        if non_zero > 0:
            # Dump 前几条非零记录
            shown = 0
            for i in range(min(f7, 100)):
                off = i * 32
                rec = sub_raw[off:off+32]
                if sum(1 for b in rec if b != 0) == 0:
                    continue
                if shown >= 5:
                    break
                u32s = [struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8)]
                print(f"    [{i}] u32: {[f'0x{v:08x}' for v in u32s]}")
                # Check for timestamp
                for j, v in enumerate(u32s):
                    if 1_700_000_000 < v < 1_800_000_000:
                        print(f"        ⭐ u32[{j}] = {v} → {datetime.fromtimestamp(v)}")
                # Check u32[4] as data pointer
                if 0 < u32s[4] < file_size1:
                    print(f"        u32[4]=0x{u32s[4]:x} → 检查数据...")
                    res = check_curve_data(fpath1, f"{sw_id}-1hbf-F4[{i}]", u32s[4], read_kb=64)
                    if res:
                        print(f"        ✅ 曲线! {res[0]['length']}点 峰值{res[0]['peak']:.3f}")
                    else:
                        print(f"        ❌ 非曲线")
                shown += 1

            # 如果 u32[4] 不是数据指针，分析 sub-index 的整体模式
            if shown > 0:
                print(f"\n    分析 F4 子索引记录结构...")
                # 收集所有非零记录的 u32 字段看看模式
                all_u0 = []
                all_u4 = []
                all_u6 = []
                all_u7 = []
                for i in range(min(f7, 500)):
                    off = i * 32
                    rec = sub_raw[off:off+32]
                    u32s = [struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8)]
                    if u32s[0] != 0 or u32s[4] != 0:
                        all_u0.append(u32s[0])
                        all_u4.append(u32s[4])
                        all_u6.append(u32s[6])
                        all_u7.append(u32s[7])

                print(f"    u32[0] 范围: {min(all_u0) if all_u0 else 'N/A'} - {max(all_u0) if all_u0 else 'N/A'}")
                print(f"    u32[4] 范围: {min(all_u4) if all_u4 else 'N/A'} - {max(all_u4) if all_u4 else 'N/A'} (差值: {(max(all_u4)-min(all_u4)) if len(all_u4)>1 else 'N/A'})")
                print(f"    u32[6] 范围: {min(all_u6) if all_u6 else 'N/A'} - {max(all_u6) if all_u6 else 'N/A'}")
                print(f"    u32[7] 范围: {min(all_u7) if all_u7 else 'N/A'} - {max(all_u7) if all_u7 else 'N/A'}")


if __name__ == '__main__':
    deep_trace_2hbf()
