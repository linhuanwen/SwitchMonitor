#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
解析 1.hbf F9 区的32B事件头结构，然后用它定位 2.hbf 的真实采样数据
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

def check_data_formats(raw, label, max_samples=1200):
    """尝试多种数据格式"""
    results = {}

    # 从不同偏移开始尝试
    for skip in [0, 8, 16, 24, 32, 40, 48, 56, 64, 128]:
        body = raw[skip:]
        f32 = [struct.unpack('<f', body[j*4:(j+1)*4])[0] for j in range(min(max_samples, len(body)//4))]

        # 找连续非零段
        segments = []
        in_nz = False
        start = 0
        for j, v in enumerate(f32[:max_samples]):
            if abs(v) > 0.01 and not in_nz:
                in_nz = True
                start = j
            elif abs(v) < 0.01 and in_nz:
                in_nz = False
                if j - start > 30:
                    peak = max(f32[start:j])
                    # 功率合理范围
                    if 0.1 < peak < 10:
                        segments.append((start, j, j-start, peak))

        if segments:
            for s, e, l, peak in segments:
                first5 = [round(v,3) for v in f32[s:s+5]]
                last5 = [round(v,3) for v in f32[e-5:e]]
                mid = s + l//2
                mid5 = [round(v,3) for v in f32[mid:mid+5]]
                print(f"  [{label} skip={skip}] [{s}-{e}] len={l} 峰值={peak:.3f}KW"
                      f"  起始={first5}  中点={mid5}  结束={last5}")
                results[(skip, s, e)] = {'len': l, 'peak': peak, 'first5': first5, 'mid5': mid5, 'last5': last5}

    return results


def main():
    fpath1 = os.path.join(POWER_DIR, '1.hbf')
    fpath2 = os.path.join(POWER_DIR, '2.hbf')
    size1 = os.path.getsize(fpath1)
    size2 = os.path.getsize(fpath2)

    print("="*70)
    print("事件头结构解析 + 数据定位")
    print("="*70)

    # 选一个在 1.hbf 中有子索引数据的道岔: 9-X
    # 从之前的分析, 9-X 在 1.hbf 有 291 条非零子索引记录
    SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
                  '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
                  '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

    data1 = read_at(fpath1, 0, 0x200000)
    data2 = read_at(fpath2, 0, 0x200000)

    # =========================================================
    # Step 1: 验证 F6 = F7 × 32 的关系
    # =========================================================
    print("\n【Step 1】验证 F6 = F7 × 32 (事件头数组)")

    for fpath, data, label in [(fpath1, data1, "1.hbf"), (fpath2, data2, "2.hbf")]:
        matches = 0
        mismatches = []
        for sw_id in SWITCH_IDS:
            pos = data.find(sw_id.encode('ascii'))
            if pos == -1:
                continue
            block = data[pos:pos+256]
            sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
            if sid != sw_id:
                continue
            f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
            f6, f7 = f[6], f[7]
            expected = f7 * 32
            if f6 == expected:
                matches += 1
            else:
                mismatches.append((sw_id, f6, expected, f6-expected))

        print(f"  [{label}] F6==F7×32: {matches}/30 ✅" + (f"  不匹配: {mismatches}" if mismatches else ""))

    # =========================================================
    # Step 2: 解析 9-X 在 1.hbf 的 F9 事件头数组
    # =========================================================
    print("\n【Step 2】解析 9-X (1.hbf) 的 F9 事件头结构")

    pos = data1.find(b'9-X')
    block = data1[pos:pos+256]
    f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
    f3_9x, f4, f5, f6, f7, f9 = f[3], f[4], f[5], f[6], f[7], f[9]
    print(f"  9-X: F3=0x{f3_9x:08x} F4=0x{f4:x} F6={f6} F7={f7} F9=0x{f9:x}")
    print(f"  F6={f6} == F7({f7})×32={f7*32} ✅")

    # 读取事件头数组
    event_headers_raw = read_at(fpath1, f9, f6)
    print(f"\n  前8条事件头原始hex:")
    for i in range(min(8, f7)):
        off = i * 32
        rec = event_headers_raw[off:off+32]
        print(f"  [{i:3d}] {' '.join(f'{b:02x}' for b in rec[:16])}")
        print(f"       {' '.join(f'{b:02x}' for b in rec[16:32])}")

    # 多种方式解析
    print(f"\n  作为 u32[8] LE:")
    for i in range(min(8, f7)):
        off = i * 32
        u32s = [struct.unpack_from('<I', event_headers_raw, off + j*4)[0] for j in range(8)]
        # 标识可能的时间戳
        notes = []
        for j, v in enumerate(u32s):
            if 1_700_000_000 < v < 1_800_000_000:
                notes.append(f"u32[{j}]=ts({datetime.fromtimestamp(v)})")
            if v == 0x1227:
                notes.append(f"u32[{j}]=0x1227(标记)")
            if v == f3_9x:
                notes.append(f"u32[{j}]=F3(道岔ID)")
        note_str = " | ".join(notes) if notes else ""
        print(f"  [{i:3d}] {[f'0x{v:08x}' for v in u32s]} {note_str}")

    # 作为 u16[16] 看
    print(f"\n  作为 u16[16] LE (前3条):")
    for i in range(min(3, f7)):
        off = i * 32
        u16s = [struct.unpack_from('<H', event_headers_raw, off + j*2)[0] for j in range(16)]
        print(f"  [{i:3d}] {[f'0x{v:04x}' for v in u16s]}")

    # =========================================================
    # Step 3: 检查事件头中哪些字段可能是数据指针
    # =========================================================
    print(f"\n【Step 3】找事件头中的数据指针字段")

    # 收集所有 u32 字段的统计
    all_fields = [[] for _ in range(8)]
    for i in range(f7):
        off = i * 32
        u32s = [struct.unpack_from('<I', event_headers_raw, off + j*4)[0] for j in range(8)]
        for j in range(8):
            all_fields[j].append(u32s[j])

    for j in range(8):
        vals = all_fields[j]
        unique = len(set(vals))
        vmin, vmax = min(vals), max(vals)
        # 检查是否所有值在合理偏移范围内
        in_file = sum(1 for v in vals if 0 < v < size1)
        # 检查相邻差值
        diffs = [vals[i+1] - vals[i] for i in range(min(20, len(vals)-1))]
        diff_unique = len(set(diffs[:50]))

        # 判断特征
        features = []
        if unique == 1:
            features.append("常量")
        elif unique < 5:
            features.append("少量变化")
        if in_file == len(vals):
            features.append("全部<文件大小(可能是偏移)")
        elif in_file > 0.8 * len(vals):
            features.append(f"{in_file}/{len(vals)}<文件大小")
        if vmin > 1_700_000_000 and vmax < 1_800_000_000:
            features.append("Unix时间戳!")
        if diff_unique == 1 and len(diffs) > 0:
            features.append(f"相邻差={diffs[0]}")

        print(f"  u32[{j}]: 范围=[0x{vmin:08x}~0x{vmax:08x}] unique={unique} {', '.join(features)}"
              f"  前5={[f'0x{v:08x}' for v in vals[:5]]}")

    # =========================================================
    # Step 4: 尝试对每个事件头中看起来像数据指针的字段,读取对应位置的数据
    # =========================================================
    print(f"\n【Step 4】追踪候选数据指针 → 采样数据")

    # 从 2.hbf 找 9-X 的事件头数组
    pos2 = data2.find(b'9-X')
    block2 = data2[pos2:pos2+256]
    f2 = [struct.unpack_from('<I', block2[0x70:0x70+52], j*4)[0] for j in range(13)]
    f3_2, f4_2, f6_2, f7_2, f9_2 = f2[3], f2[4], f2[6], f2[7], f2[9]
    print(f"\n  2.hbf 9-X: F6={f6_2} F7={f7_2} F9=0x{f9_2:x}")

    # 读取 2.hbf 的事件头
    headers2_raw = read_at(fpath2, f9_2, min(f6_2, 0x100000))

    # 找非零事件头
    non_zero_headers = []
    for i in range(f7_2):
        off = i * 32
        if off + 32 > len(headers2_raw):
            break
        rec = headers2_raw[off:off+32]
        if set(rec) != {0}:
            non_zero_headers.append(i)

    print(f"  非零事件头: {len(non_zero_headers)}/{f7_2}")

    if non_zero_headers:
        # Dump 前几个非零头
        print(f"\n  前5个非零事件头 (2.hbf 9-X):")
        for idx in non_zero_headers[:5]:
            off = idx * 32
            rec = headers2_raw[off:off+32]
            u32s = [struct.unpack_from('<I', headers2_raw, off + j*4)[0] for j in range(8)]
            print(f"  [{idx:4d}] hex: {' '.join(f'{b:02x}' for b in rec)}")
            print(f"         u32: {[f'0x{v:08x}' for v in u32s]}")
            # 检查每个 u32 是否指向有效数据
            for j, v in enumerate(u32s):
                if 0 < v < size2 and v > 100000:
                    # 检查指向的数据
                    probe = read_at(fpath2, v, 256)
                    # 看是不是 float32 数据
                    f32_probe = [struct.unpack('<f', probe[k*4:(k+1)*4])[0] for k in range(min(30, len(probe)//4))]
                    nz = sum(1 for fv in f32_probe if abs(fv) > 0.01)
                    if nz > 5:
                        print(f"         ⭐ u32[{j}]=0x{v:x} → float32非零={nz}/30: {[round(fv,3) for fv in f32_probe[:15]]}")

    # =========================================================
    # Step 5: 直接扫描 2.hbf 文件,找 0x1227 标记后跟采样数据的位置
    # =========================================================
    print(f"\n【Step 5】在 2.hbf 中搜索 0x1227 标记块")

    # 从之前 F4 子索引的 data_ptr 入手 - 找 19-J (实际事件最多的道岔)
    pos_19j = data2.find(b'19-J')
    block_19j = data2[pos_19j:pos_19j+256]
    f19 = [struct.unpack_from('<I', block_19j[0x70:0x70+52], j*4)[0] for j in range(13)]
    f4_19, f7_19, f9_19 = f19[4], f19[7], f19[9]
    print(f"\n  2.hbf 19-J: F4=0x{f4_19:x} F7={f7_19} F9=0x{f9_19:x}")

    # 读 F4 子索引, 找 Type A 记录
    sub_raw = read_at(fpath2, f4_19, min(f7_19 * 32, 0x200000))
    type_a = []
    for i in range(f7_19):
        off = i * 32
        u32s = [struct.unpack_from('<I', sub_raw, off + j*4)[0] for j in range(8)]
        if u32s[0] == 0x1227:
            if u32s[7] > 0:
                type_a.append((i, u32s))

    print(f"  Type A 记录: {len(type_a)}")

    if type_a:
        # 检查每个 data_ptr
        for idx, (rec_idx, u32s) in enumerate(type_a[:5]):
            dp = u32s[7]
            print(f"\n  [{rec_idx}] data_ptr=0x{dp:08x}:")
            raw_block = read_at(fpath2, dp, min(0x1227, size2 - dp))
            print(f"    块头64B: {' '.join(f'{b:02x}' for b in raw_block[:64])}")

            # 把块作为 float32 看各种偏移
            results = check_data_formats(raw_block, f"19-J evt[{rec_idx}] dp=0x{dp:x}")

    # =========================================================
    # Step 6: 关键验证 - 追踪 1.hbf F9 事件头中指向的数据
    # =========================================================
    print(f"\n\n【Step 6】关键验证: 1.hbf 9-X 事件头 → 数据追踪")

    # 重新解析 9-X 1.hbf 事件头, 逐字段验证
    # 从头16条记录看模式
    print(f"\n  9-X (1.hbf) 事件头详细分析:")
    for i in range(min(20, f7)):
        off = i * 32
        u32s = [struct.unpack_from('<I', event_headers_raw, off + j*4)[0] for j in range(8)]

        # 计算相邻记录的 u32 差值
        if i == 0:
            print(f"  [{i:3d}] {[f'0x{v:08x}' for v in u32s]}")
        else:
            prev_off = (i-1) * 32
            prev_u32s = [struct.unpack_from('<I', event_headers_raw, prev_off + j*4)[0] for j in range(8)]
            diffs_val = [u32s[j] - prev_u32s[j] for j in range(8)]
            diff_str = '  '.join(f'Δ{j}={d:+d}' for j, d in enumerate(diffs_val) if d != 0)
            print(f"  [{i:3d}] {[f'0x{v:08x}' for v in u32s]}  | {diff_str}")

    # Check if any u32 field increments by exactly 0x1227
    print(f"\n  检查相邻 u32 差值(前10条):")
    for j in range(8):
        diffs_j = [all_fields[j][i+1] - all_fields[j][i] for i in range(min(20, len(all_fields[j])-1))]
        if diffs_j:
            print(f"    u32[{j}]: diffs={diffs_j[:10]}  unique={set(diffs_j[:20])}")

    # 找 F4 子索引中的 data_ptr 与 F9 事件头的关系
    print(f"\n  对比 9-X F4 子索引 与 F9 事件头:")
    sub_raw_9x = read_at(fpath1, f4, f7 * 32)

    # 分析 F4 子索引
    f4_type_b = []
    for i in range(f7):
        off = i * 32
        u32s = [struct.unpack_from('<I', sub_raw_9x, off + j*4)[0] for j in range(8)]
        if 1_700_000_000 < u32s[0] < 1_800_000_000:
            ts = u32s[0]
            dp = u32s[6] if u32s[6] > 0 else u32s[7]
            f4_type_b.append((i, ts, u32s))

    print(f"    F4 Type B (时间戳) 记录: {len(f4_type_b)}")
    if f4_type_b:
        # 显示前几个
        for idx, ts, u32s in f4_type_b[:5]:
            print(f"    [{idx}] ts={datetime.fromtimestamp(ts)} u32s={[f'0x{v:08x}' for v in u32s]}")
            # 看看哪个字段可能指向 F9 中的事件头
            for j in range(8):
                v = u32s[j]
                if f9 <= v < f9 + f6:
                    print(f"        ⭐ u32[{j}]=0x{v:x} 在F9范围内! (F9偏移={v-f9}, 事件头索引={(v-f9)//32})")


if __name__ == '__main__':
    main()
