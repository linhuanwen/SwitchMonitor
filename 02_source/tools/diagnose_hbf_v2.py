#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 子索引 + 数据指针 深度诊断
关键问题: F4 子索引记录的真正结构是什么？数据到底在哪里？
"""
import struct
import sys
import os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def dump_sub_index_raw(filepath, sw_id, f4_offset, f7_count, f9_offset):
    """直接dump F4子索引块的原始字节，不做任何假设"""
    print(f"\n{'='*70}")
    print(f"子索引原始字节 Dump: {sw_id} (F4=0x{f4_offset:x}, F7={f7_count}, F9=0x{f9_offset:x})")
    print(f"{'='*70}")

    file_size = os.path.getsize(filepath)
    # 读取子索引块 (32B × F7条)
    block_size = f7_count * 32
    if f4_offset + block_size > file_size:
        print(f"  ⚠️ F4偏移超出文件范围!")
        return None

    raw = read_at(filepath, f4_offset, min(block_size, 0x100000))

    print(f"  子索引块大小: {block_size} bytes ({f7_count} 条 × 32B)")

    # Dump 前 10 条记录原始字节
    print(f"\n  --- 前10条 32B 记录 (原始hex) ---")
    for i in range(min(10, f7_count)):
        off = i * 32
        rec = raw[off:off+32]
        # 每行 16 字节
        print(f"  [{i:3d}] +0: {' '.join(f'{b:02x}' for b in rec[:16])}")
        print(f"       +16: {' '.join(f'{b:02x}' for b in rec[16:32])}")

        # 尝试多种解释
        # 作为 8 个 uint32 LE
        u32s = [struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8)]
        ts_candidates = []
        for j, v in enumerate(u32s):
            if 1_700_000_000 < v < 1_800_000_000:
                ts_candidates.append((j, v))
        print(f"       uint32 LE: {[f'0x{v:08x}' for v in u32s]}")
        if ts_candidates:
            for j, v in ts_candidates:
                print(f"       ⭐ u32[{j}] = {v} → {datetime.fromtimestamp(v)}")

        # 作为 4 个 uint64 LE
        u64s = [struct.unpack('<Q', rec[j*8:(j+1)*8])[0] for j in range(4)]
        print(f"       uint64 LE: {[f'0x{v:016x}' for v in u64s]}")

    # 也检查最后几条
    if f7_count > 10:
        print(f"\n  --- 最后5条 32B 记录 ---")
        for i in range(max(0, f7_count-5), f7_count):
            off = i * 32
            rec = raw[off:off+32]
            print(f"  [{i:3d}] +0: {' '.join(f'{b:02x}' for b in rec[:16])}")
            print(f"       +16: {' '.join(f'{b:02x}' for b in rec[16:32])}")
            u32s = [struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8)]
            ts_candidates = [(j, v) for j, v in enumerate(u32s) if 1_700_000_000 < v < 1_800_000_000]
            print(f"       uint32 LE: {[f'0x{v:08x}' for v in u32s]}")
            if ts_candidates:
                for j, v in ts_candidates:
                    print(f"       ⭐ u32[{j}] = {v} → {datetime.fromtimestamp(v)}")

    return raw


def extract_data_ptrs_from_sub_index(raw, f7_count, file_size):
    """从子索引块中提取所有可能的数据指针"""
    # 尝试多种结构假设来提取 data_ptr

    # 假设1: Type A — u32[0]=0x1227, data_ptr在u32[7]
    # 假设2: Type B — u32[0]=timestamp, data_ptr在u32[6]
    # 假设3: 其他结构

    data_ptrs_a = []  # u32[7] 当 u32[0]==0x1227
    data_ptrs_b = []  # u32[6] 当 u32[0]是时间戳
    data_ptrs_all_u7 = []  # 总是取u32[7]
    data_ptrs_all_u6 = []  # 总是取u32[6]
    data_ptrs_all_u4 = []  # 总是取u32[4]
    data_ptrs_all_u5 = []  # 总是取u32[5]

    for i in range(f7_count):
        off = i * 32
        rec = raw[off:off+32]
        u32s = [struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8)]

        u0 = u32s[0]
        u4 = u32s[4]
        u5 = u32s[5]
        u6 = u32s[6]
        u7 = u32s[7]

        # Type A detection
        if u0 == 0x1227 or u0 == 0x3277:
            if 0 < u7 < file_size:
                data_ptrs_a.append((i, u7, None))

        # Type B detection
        if 1_700_000_000 < u0 < 1_800_000_000:
            if 0 < u6 < file_size:
                data_ptrs_b.append((i, u6, u0))

        # Always collect
        for lst, val in [(data_ptrs_all_u7, u7), (data_ptrs_all_u6, u6),
                          (data_ptrs_all_u4, u4), (data_ptrs_all_u5, u5)]:
            if 0 < val < file_size:
                lst.append(val)

    results = {
        'type_a': data_ptrs_a,
        'type_b': data_ptrs_b,
        'all_u7': data_ptrs_all_u7[:20],
        'all_u6': data_ptrs_all_u6[:20],
        'all_u4': data_ptrs_all_u4[:20],
        'all_u5': data_ptrs_all_u5[:20],
    }

    return results


def check_data_at(filepath, offset, label, read_size=8192):
    """检查某个偏移处的数据"""
    file_size = os.path.getsize(filepath)
    if offset <= 0 or offset >= file_size:
        print(f"  {label}: offset 0x{offset:x} 无效")
        return

    raw = read_at(filepath, offset, min(read_size, file_size - offset))

    # float32
    floats = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(200, len(raw)//4))]
    # int16
    int16s = [struct.unpack('<h', raw[j*2:(j+1)*2])[0] for j in range(min(400, len(raw)//2))]
    # 原始 hex 前64字节
    hex_preview = ' '.join(f'{b:02x}' for b in raw[:64])

    print(f"  {label} @ 0x{offset:08x}:")
    print(f"    hex: {hex_preview}")

    # 统计
    zeros_f = sum(1 for v in floats[:200] if abs(v) < 0.001)
    sentinel_f = sum(1 for v in floats[:200] if abs(v - (-71.622)) < 0.01)
    reasonable_f = sum(1 for v in floats[:200] if 0.01 < v < 100)
    varying_f = len(set(round(v, 2) for v in floats[:50] if abs(v) > 0.001))

    print(f"    float32[0:50]: {[round(v,3) for v in floats[:50]]}")
    print(f"    零值: {zeros_f}/200  哨兵: {sentinel_f}/200  合理值(0.01-100): {reasonable_f}/200  变化值数: {varying_f}")

    # 如果看起来像真实的曲线数据，打印更多
    if reasonable_f > 50 and varying_f > 3:
        print(f"    ✅ 看起来像真实的曲线数据! 继续读更多...")
        raw2 = read_at(filepath, offset, 16384)
        floats2 = [struct.unpack('<f', raw2[j*4:(j+1)*4])[0] for j in range(min(1200, len(raw2)//4))]

        # 找曲线特征: 零点→上升→峰值→下降→零点
        non_zero_start = None
        peak_idx = None
        peak_val = 0
        for j, v in enumerate(floats2[:1200]):
            if non_zero_start is None and abs(v) > 0.05:
                non_zero_start = j
            if v > peak_val:
                peak_val = v
                peak_idx = j
        print(f"    非零起始: idx={non_zero_start}, 峰值: {peak_val:.3f} @ idx={peak_idx}")
        print(f"    全长 float32[0:200]: {[round(v,3) for v in floats2[:200]]}")
        print(f"    全长 float32[200:400]: {[round(v,3) for v in floats2[200:400]]}")
        print(f"    全长 float32[800:1032]: {[round(v,3) for v in floats2[800:1032]]}")


def deep_analyze_one_switch(filepath, sw_id, f4, f7, f9, f3, f5):
    """对一个道岔做完整分析"""
    file_size = os.path.getsize(filepath)

    print(f"\n{'#'*60}")
    print(f"# 深度分析: {sw_id} (F3=0x{f3:x} F4=0x{f4:x} F7={f7} F9=0x{f9:x} F5={f5})")
    print(f"{'#'*60}")

    # Step 1: Dump sub-index
    raw_sub = dump_sub_index_raw(filepath, sw_id, f4, f7, f9)
    if raw_sub is None:
        return

    # Step 2: Extract data pointers
    ptrs = extract_data_ptrs_from_sub_index(raw_sub, f7, file_size)

    print(f"\n  --- 数据指针提取结果 ---")
    print(f"  Type A (0x1227标记): {len(ptrs['type_a'])} 个")
    print(f"  Type B (时间戳开头): {len(ptrs['type_b'])} 个")
    print(f"  all_u7 前20: {[f'0x{v:x}' for v in ptrs['all_u7']]}")
    print(f"  all_u6 前20: {[f'0x{v:x}' for v in ptrs['all_u6']]}")
    print(f"  all_u4 前20: {[f'0x{v:x}' for v in ptrs['all_u4']]}")
    print(f"  all_u5 前20: {[f'0x{v:x}' for v in ptrs['all_u5']]}")

    # Step 3: 对 Type B 的 data_ptr 检查实际数据
    if ptrs['type_b']:
        print(f"\n  ═══ Type B data_ptr 数据检查 ═══")
        for idx, dp, ts in ptrs['type_b'][:5]:
            check_data_at(filepath, dp, f"TypeB[idx={idx}, ts={datetime.fromtimestamp(ts)}]")

    # Step 4: 检查 F9 指向的数据
    if 0 < f9 < file_size:
        print(f"\n  ═══ F9=0x{f9:x} 数据检查 ═══")
        check_data_at(filepath, f9, "F9直接指向")

    # Step 5: 如果上面都没有有效数据，用多种方式扫描
    # 取 all_u7 中看起来最合理的指针
    valid_u7 = [v for v in ptrs['all_u7'] if 0 < v < file_size]
    if valid_u7:
        # 看 unique 的 u7 值
        unique_u7 = sorted(set(valid_u7))
        print(f"\n  ═══ u32[7] unique 值: {len(unique_u7)} 个 ═══")
        if len(unique_u7) > 0 and len(unique_u7) < 100:
            for u7 in unique_u7[:10]:
                check_data_at(filepath, u7, f"u32[7]=0x{u7:x}")

    # 检查 u32[4] 值（可能是字节数或偏移）
    valid_u4 = [v for v in ptrs['all_u4'] if 0 < v < file_size]
    if valid_u4:
        unique_u4 = sorted(set(valid_u4))
        print(f"\n  ═══ u32[4] unique 值: {len(unique_u4)} 个 → 范围 0x{unique_u4[0]:x}-0x{unique_u4[-1]:x} ═══")
        # u32[4] 可能是数据大小(byte count)，检查分布
        if len(unique_u4) < 50:
            print(f"    值列表: {[f'0x{v:x}({v})' for v in unique_u4]}")

        # 也尝试 u32[5]
        valid_u5 = list(set(v for v in ptrs['all_u5'] if 0 < v < file_size))
        if valid_u5:
            print(f"\n  ═══ u32[5] unique 值: {len(valid_u5)} 个 → 范围 0x{valid_u5[0]:x}-0x{valid_u5[-1]:x} ═══")
        else:
            print(f"\n  ═══ u32[5]: 无有效值 ═══")


def main():
    # 重点分析 功率/2.hbf (有真实数据)
    fpath = os.path.join(POWER_DIR, '2.hbf')
    fname = '功率/2.hbf'
    file_size = os.path.getsize(fpath)

    print("HBF 子索引结构深度诊断")
    print("="*70)
    print(f"文件: {fname} ({file_size/1024/1024:.0f} MB)")

    # 读取文件头区域获取目录项
    data_2mb = read_at(fpath, 0, 0x200000)

    # 找所有 switch ID
    SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
                  '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
                  '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

    # 收集有效目录项
    valid_entries = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = data_2mb.find(pattern)
        if pos == -1:
            continue

        block = data_2mb[pos:pos+256]
        if len(block) < 0x70 + 52:
            continue

        sw_id_ascii = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sw_id_ascii != sw_id:
            continue

        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f3, f4, f5, f6, f7, f9 = f[3], f[4], f[5], f[6], f[7], f[9]

        # 合理性检查
        if f4 > 0 and f4 < file_size and 10 < f7 < 100000:
            valid_entries.append((sw_id, f3, f4, f5, f6, f7, f9))

    print(f"有效目录项: {len(valid_entries)}")

    # 按 F7 (事件数) 排序，先分析事件数最多的道岔
    valid_entries.sort(key=lambda x: -x[6])

    print(f"\n事件数排名 (Top 10):")
    for sw_id, f3, f4, f5, f6, f7, f9 in valid_entries[:10]:
        print(f"  {sw_id}: F7={f7}事件 F6={f6}bytes F4=0x{f4:x} F9=0x{f9:x} F3=0x{f3:x}")

    # 深入分析前3个
    for sw_id, f3, f4, f5, f6, f7, f9 in valid_entries[:4]:
        deep_analyze_one_switch(fpath, sw_id, f4, f7, f9, f3, f5)

    # 同时也快速检查 功率/1.hbf
    print(f"\n\n{'#'*70}")
    print(f"# 对比: 功率/1.hbf")
    print(f"{'#'*70}")
    fpath1 = os.path.join(POWER_DIR, '1.hbf')
    data1 = read_at(fpath1, 0, 0x200000)
    entries1 = []
    for sw_id in SWITCH_IDS:
        pos = data1.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data1[pos:pos+256]
        if len(block) < 0x70+52:
            continue
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        if 0 < f[4] < os.path.getsize(fpath1) and 10 < f[7] < 100000:
            entries1.append((sw_id, f[3], f[4], f[5], f[6], f[7], f[9]))
    entries1.sort(key=lambda x: -x[6])
    print(f"有效目录项: {len(entries1)}")
    for sw_id, f3, f4, f5, f6, f7, f9 in entries1[:3]:
        print(f"  {sw_id}: F7={f7} F4=0x{f4:x} F9=0x{f9:x}")
        deep_analyze_one_switch(fpath1, sw_id, f4, f7, f9, f3, f5)


if __name__ == '__main__':
    main()
