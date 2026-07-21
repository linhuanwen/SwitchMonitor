#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 数据诊断 — 深入检查为什么只有少量曲线数据被提取
关键问题: 30台转辙机 + 3年运行 = 不可能只有8条短曲线
"""
import struct
import sys
import os
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

# 已知的道岔 ID 常量 (F3 字段值 → switch ID)
F3_TO_ID = {}

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def analyze_directory_entries(filepath, label):
    """深入分析256B目录项，特别是F9及之后的字段"""
    print(f"\n{'='*70}")
    print(f"目录项深度分析: {os.path.basename(filepath)} ({label})")
    print(f"{'='*70}")

    file_size = os.path.getsize(filepath)
    data = read_at(filepath, 0, min(0x200000, file_size))  # 读前2MB

    # 验证文件头
    magic = data[:8]
    print(f"Magic: {magic}")

    # 搜索所有 switch ID 字符串位置
    sw_positions = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = 0
        while True:
            p = data.find(pattern, pos)
            if p == -1:
                break
            sw_positions.append((p, sw_id))
            pos = p + 1

    sw_positions.sort()
    print(f"找到 {len(sw_positions)} 个 switch ID 字符串")

    # 对每个 switch ID，分析周围的 256B 区域
    print(f"\n{'─'*70}")
    print(f"分析每个道岔的 256B 目录项 (F0-F12 共13个uint32)")
    print(f"{'─'*70}")

    for pos, sw_id in sw_positions[:30]:  # 每个ID取第一个出现
        # Switch ID 在 256B 块的偏移0处
        block_start = pos  # switch ID is at offset 0 of the block

        # 读取完整 256 字节
        if block_start + 256 > len(data):
            continue
        block = data[block_start:block_start + 256]

        # Switch ID ASCII (前8字节)
        sw_id_ascii = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sw_id_ascii != sw_id:
            # 可能不是目录项开始，跳过
            continue

        # 0x70 (112) 开始的 13×4=52 字节
        fields_start = 0x70
        raw_fields = block[fields_start:fields_start + 52]

        f = []
        for i in range(13):
            val = struct.unpack_from('<I', raw_fields, i * 4)[0]
            f.append(val)

         # 描述 F3 (道岔ID常量)
        if pos == sw_positions[0][0] or True:  # always print for first occurrence
            pass

        # 记录 F3 映射
        F3_TO_ID[f[3]] = sw_id

        # 打印关键字段
        f4_offset = f[4]
        f6_bytes = f[6]
        f7_samples = f[7]
        f9_data_offset = f[9]

        # 判断数据情况
        has_data = f9_data_offset != 0 and f9_data_offset < file_size
        has_index = f4_offset != 0 and f4_offset < file_size

        print(f"\n  {sw_id}: F3=0x{f[3]:08x} F4=0x{f4_offset:08x} F6={f6_bytes:>10d}B F7={f7_samples:>8d}点 "
              f"F9=0x{f9_data_offset:08x}")
        print(f"    F0-F2: {f[0]:10d} {f[1]:10d} {f[2]:10d}  "
              f"F5={f[5]:>8d} F8={f[8]:>8d} F10={f[10]:>6d} F11={f[11]:>6d} F12={f[12]:>6d}")
        print(f"    有数据偏移{'✅' if has_data else '❌'}  有子索引偏移{'✅' if has_index else '❌'}")

    return sw_positions


def analyze_sub_index_block(filepath, dir_entries, label):
    """针对一个道岔，分析 F4 指向的子索引块完整结构"""
    print(f"\n{'='*70}")
    print(f"子索引块详细分析: {os.path.basename(filepath)} ({label})")
    print(f"{'='*70}")

    file_size = os.path.getsize(filepath)

    # 选几个有数据的道岔来分析
    candidates = []
    for pos, sw_id in dir_entries[:30]:
        block = read_at(filepath, pos, 256)
        raw_fields = block[0x70:0x70+52]
        f = [struct.unpack_from('<I', raw_fields, i*4)[0] for i in range(13)]

        f4 = f[4]
        f7 = f[7]
        f9 = f[9]

        if f4 > 0 and f4 < file_size and f7 > 0:
            candidates.append((sw_id, f4, f7, f9, f))

    print(f"有效目录项: {len(candidates)}/{len(dir_entries)}")

    for sw_id, f4, f7, f9, fields in candidates[:6]:
        print(f"\n{'─'*60}")
        print(f"  {sw_id}: F4=0x{f4:x} F7={f7}点 F9=0x{f9:x}")

        # 读取 F4 指向的子索引块
        # 子索引记录是 32 字节，共 F7 条
        sub_block_size = f7 * 32
        if f4 + sub_block_size > file_size:
            print(f"    子索引块超出文件范围!")
            continue

        sub_block = read_at(filepath, f4, min(sub_block_size, 0x100000))

        # 标记值
        marker_le = b'\x27\x12\x00\x00'  # 0x1227 for power
        marker_current = b'\x77\x32\x00\x00'  # 0x3277 for current

        # 分析每条 32B 记录
        type_a_count = 0  # marker at offset 0
        type_b_count = 0  # marker at offset 28
        type_c_count = 0  # marker at byte 7
        other_count = 0

        timestamps = []
        data_ptrs = []

        for i in range(min(f7, 200)):  # 分析前200条
            rec_off = i * 32
            if rec_off + 32 > len(sub_block):
                break
            rec = sub_block[rec_off:rec_off + 32]

            # 检测类型
            u32_at_0 = struct.unpack('<I', rec[0:4])[0]

            if u32_at_0 == 0x1227:
                type_a_count += 1
                # Type A: [0x1227] [u1] [u2] [u3] [0] [seq] [const] [data_ptr]
                u1 = struct.unpack('<I', rec[4:8])[0]
                u2 = struct.unpack('<I', rec[8:12])[0]
                u3 = struct.unpack('<I', rec[12:16])[0]
                seq = struct.unpack('<I', rec[20:24])[0]
                data_ptr = struct.unpack('<I', rec[28:32])[0]
                data_ptrs.append(data_ptr)
            elif u32_at_0 == 0x3277:
                type_a_count += 1
                u1 = struct.unpack('<I', rec[4:8])[0]
                u2 = struct.unpack('<I', rec[8:12])[0]
                u3 = struct.unpack('<I', rec[12:16])[0]
                seq = struct.unpack('<I', rec[20:24])[0]
                data_ptr = struct.unpack('<I', rec[28:32])[0]
                data_ptrs.append(data_ptr)
            elif 1_700_000_000 < u32_at_0 < 1_800_000_000:
                # Type B: starts with timestamp
                type_b_count += 1
                ts = u32_at_0
                mark = struct.unpack('<I', rec[4:8])[0]
                inc = struct.unpack('<I', rec[8:12])[0]
                zero = struct.unpack('<I', rec[12:16])[0]
                seq = struct.unpack('<I', rec[16:20])[0]
                const = struct.unpack('<I', rec[20:24])[0]
                data_ptr = struct.unpack('<I', rec[24:28])[0]
                marker_end = struct.unpack('<I', rec[28:32])[0]
                timestamps.append(ts)
                data_ptrs.append(data_ptr)
            else:
                # 可能是 Type C 或其他
                other_count += 1

        print(f"    类型A (标记在开头): {type_a_count}")
        print(f"    类型B (时间戳开头): {type_b_count}")
        print(f"    其他: {other_count}")

        if timestamps:
            tss = sorted(set(timestamps))
            print(f"    唯一时间戳: {len(tss)} 个")
            if tss:
                print(f"    时间范围: {datetime.fromtimestamp(tss[0])} ~ {datetime.fromtimestamp(tss[-1])}")

        if data_ptrs:
            unique_ptrs = sorted(set(data_ptrs))
            non_zero_ptrs = [p for p in unique_ptrs if p > 0 and p < file_size]
            print(f"    data_ptr 数: {len(unique_ptrs)} 唯一值, {len(non_zero_ptrs)} 个有效文件偏移")
            if non_zero_ptrs:
                print(f"    data_ptr 范围: 0x{non_zero_ptrs[0]:x} ~ 0x{non_zero_ptrs[-1]:x}")
                # 检查相邻 data_ptr 的差值 — 应该等于采样数据大小
                if len(non_zero_ptrs) >= 2:
                    diffs = [non_zero_ptrs[i+1] - non_zero_ptrs[i] for i in range(min(20, len(non_zero_ptrs)-1))]
                    print(f"    相邻 data_ptr 差值 (前20): {diffs[:20]}")


def find_actual_sample_data(filepath, label):
    """在文件中搜索实际采样数据，不依赖目录项指针"""
    print(f"\n{'='*70}")
    print(f"直接搜索采样数据模式: {os.path.basename(filepath)} ({label})")
    print(f"{'='*70}")

    file_size = os.path.getsize(filepath)

    # 策略: 读取文件的不同区域，寻找合理的 float32 采样数据
    # 道岔曲线特征:
    #   - 开始时接近零 (等待阶段)
    #   - 突然上升 (启动)
    #   - 稳定值 (转换过程中)
    #   - 下降归零 (到位)
    #   - 采样率 25Hz或40ms间隔 → 约1032点 (参考配置)
    #   - 功率范围 0-5KW, 电流范围 0-10A

    # 先从目录项 F9 指向的区域提取样本
    # 然后也在文件中扫描其他可能的数据区域

    # 用 F3 映射来定位
    data_at_0x70 = read_at(filepath, 0, 0x200000)
    sw_at_0 = []
    for pos, sw_id in [(p, sid) for p, sid in sorted([(data_at_0x70.find(sid.encode('ascii')), sid)
                         for sid in SWITCH_IDS if data_at_0x70.find(sid.encode('ascii')) >= 0])]:
        sw_at_0.append((pos, sw_id))

    print(f"文件前2MB中找到 {len(sw_at_0)} 个 switch ID")

    # 对每个找到的 switch，获取 F9 指向的数据
    for pos, sw_id in sw_at_0[:5]:  # 分析前5个
        block = data_at_0x70[pos:pos+256]
        if len(block) < 0x70 + 52:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], i*4)[0] for i in range(13)]
        f9 = f[9]
        f7 = f[7]
        f4 = f[4]

        print(f"\n  {sw_id}: F4=0x{f4:x} F7={f7} F9=0x{f9:x}")

        if f9 == 0 or f9 >= file_size:
            print(f"    F9 无效，跳过")
            continue

        # 读取 F9 处的数据
        sample_size = 4096  # 先读 4KB 看布局
        raw = read_at(filepath, f9, sample_size)

        # 作为 float32 解析
        floats = [struct.unpack('<f', raw[i*4:(i+1)*4])[0] for i in range(min(200, len(raw)//4))]

        # 也作为 int16 解析
        int16s = [struct.unpack('<h', raw[i*2:(i+1)*2])[0] for i in range(min(400, len(raw)//2))]

        # 也作为 uint16 解析
        uint16s = [struct.unpack('<H', raw[i*2:(i+1)*2])[0] for i in range(min(400, len(raw)//2))]

        print(f"    float32 前40: {[round(v,2) for v in floats[:40]]}")
        print(f"    int16 前40:   {int16s[:40]}")
        print(f"    uint16 前40:  {uint16s[:40]}")

        # 检查哨兵值 -71.622
        sentinel_count = sum(1 for v in floats[:200] if abs(v - (-71.622)) < 0.01)
        zero_count = sum(1 for v in floats[:200] if abs(v) < 0.001)
        print(f"    哨兵(-71.622): {sentinel_count}/200, 零值: {zero_count}/200")

        # 如果全是哨兵或零值，检查数据是否在别处
        if sentinel_count > 150 or zero_count > 150:
            print(f"    ⚠️ 此处数据几乎全是哨兵/零值，真实数据可能在别处")

            # 检查 F4 子索引块中的 data_ptr
            if f4 > 0 and f4 < file_size:
                sub_block = read_at(filepath, f4, min(f7 * 32, 0x10000))
                # 收集所有非零 data_ptr
                data_ptrs = []
                for i in range(min(f7, 500)):
                    rec = sub_block[i*32:(i+1)*32]
                    u32_0 = struct.unpack('<I', rec[0:4])[0]

                    if 1_700_000_000 < u32_0 < 1_800_000_000:
                        # Type B: data_ptr at offset 24
                        dp = struct.unpack('<I', rec[24:28])[0]
                        if dp > 0 and dp < file_size:
                            data_ptrs.append((i, dp, u32_0))
                    elif u32_0 in (0x1227, 0x3277):
                        # Type A: data_ptr at offset 28
                        dp = struct.unpack('<I', rec[28:32])[0]
                        if dp > 0 and dp < file_size:
                            data_ptrs.append((i, dp, None))

                if data_ptrs:
                    print(f"    子索引中有 {len(data_ptrs)} 个非零 data_ptr")
                    # 取第一个data_ptr检查
                    first_idx, first_dp, first_ts = data_ptrs[0]
                    print(f"    第一条: idx={first_idx} data_ptr=0x{first_dp:x} ts={datetime.fromtimestamp(first_ts) if first_ts else 'N/A'}")

                    raw2 = read_at(filepath, first_dp, 4096)
                    floats2 = [struct.unpack('<f', raw2[i*4:(i+1)*4])[0] for i in range(min(200, len(raw2)//4))]
                    int16s2 = [struct.unpack('<h', raw2[i*2:(i+1)*2])[0] for i in range(min(400, len(raw2)//2))]

                    print(f"    data_ptr处 float32 前40: {[round(v,2) for v in floats2[:40]]}")
                    print(f"    data_ptr处 int16 前40:   {int16s2[:40]}")

                    # 检查差值
                    if len(data_ptrs) >= 2:
                        diffs = [data_ptrs[i+1][1] - data_ptrs[i][1] for i in range(min(20, len(data_ptrs)-1))]
                        print(f"    相邻 data_ptr 差值: {diffs[:20]}")
                        if diffs:
                            avg_diff = sum(diffs[:20]) / min(20, len(diffs))
                            print(f"    平均差值: {avg_diff:.0f} bytes = {avg_diff/4:.0f} floats = {avg_diff/2:.0f} int16s")

        # 如果 F9 数据无效，直接分析 F4 子索引
        if f9 == 0 or f9 >= file_size:
            print(f"    F9无效，直接从F4子索引查找数据")
            if f4 > 0 and f4 < file_size:
                sub_block = read_at(filepath, f4, min(f7 * 32, 0x100000))
                # 收集所有 data_ptr
                data_ptrs = []
                for i in range(min(f7, 2000)):
                    rec = sub_block[i*32:(i+1)*32]
                    u32_0 = struct.unpack('<I', rec[0:4])[0]
                    if 1_700_000_000 < u32_0 < 1_800_000_000:
                        dp = struct.unpack('<I', rec[24:28])[0]
                        ts = u32_0
                        if dp > 0 and dp < file_size:
                            data_ptrs.append((i, dp, ts))
                    elif u32_0 in (0x1227, 0x3277):
                        dp = struct.unpack('<I', rec[28:32])[0]
                        if dp > 0 and dp < file_size:
                            data_ptrs.append((i, dp, None))

                print(f"    F4子索引中有效data_ptr: {len(data_ptrs)}/{min(f7,2000)}")
                if data_ptrs:
                    # 检查每个 data_ptr 指向的数据
                    for idx, dp, ts in data_ptrs[:3]:
                        raw3 = read_at(filepath, dp, 256)
                        # 尝试多种格式
                        print(f"    [{idx}] data_ptr=0x{dp:x} ts={datetime.fromtimestamp(ts) if ts else 'N/A'}:")
                        print(f"        hex: {' '.join(f'{b:02x}' for b in raw3[:64])}")


def main():
    power_files = sorted([f for f in os.listdir(POWER_DIR) if f.endswith('.hbf')])
    current_files = sorted([f for f in os.listdir(CURRENT_DIR) if f.endswith('.hbf')])

    print("HBF 数据诊断 — 寻找真实采样数据")
    print("="*70)

    # 先分析功率文件
    for fname in power_files:
        fpath = os.path.join(POWER_DIR, fname)
        print(f"\n{'#'*70}")
        print(f"# 文件: {fname} ({os.path.getsize(fpath)/1024/1024:.0f} MB)")
        print(f"{'#'*70}")

        sw_positions = analyze_directory_entries(fpath, "功率")
        if sw_positions:
            analyze_sub_index_block(fpath, sw_positions, "功率")

    # 再分析电流文件
    for fname in current_files[:1]:  # 先只看第一个
        fpath = os.path.join(CURRENT_DIR, fname)
        print(f"\n{'#'*70}")
        print(f"# 文件: {fname} ({os.path.getsize(fpath)/1024/1024:.0f} MB)")
        print(f"{'#'*70}")

        sw_positions = analyze_directory_entries(fpath, "电流")
        if sw_positions:
            analyze_sub_index_block(fpath, sw_positions, "电流")

    # 最后做一次全面的数据扫描
    print(f"\n{'='*70}")
    print(f"全文件扫描: 搜索真实采样数据模式")
    print(f"{'='*70}")

    for fname in power_files:
        fpath = os.path.join(POWER_DIR, fname)
        find_actual_sample_data(fpath, "功率")

    for fname in current_files[:1]:
        fpath = os.path.join(CURRENT_DIR, fname)
        find_actual_sample_data(fpath, "电流")


if __name__ == '__main__':
    main()
