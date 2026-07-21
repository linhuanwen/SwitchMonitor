#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全面对比 功率/1.hbf 和 功率/2.hbf 的内容差异
"""
import struct
import sys
import os
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_dir_entry(block):
    """解析256B目录项，返回F0-F12"""
    sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
    f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
    return sid, f

def count_sub_index_types(sub_raw, f7, file_size):
    """统计子索引记录类型"""
    type_a = 0  # u32[0] == 0x1227
    type_b = 0  # u32[0] 是时间戳
    all_zero = 0
    other = 0
    tss = []

    for i in range(f7):
        off = i * 32
        if off + 32 > len(sub_raw):
            break
        rec = sub_raw[off:off+32]
        # 判断是否全零
        if set(rec) == {0}:
            all_zero += 1
            continue

        u32s = list(struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8))
        u0 = u32s[0]

        if u0 == 0x1227:
            type_a += 1
        elif 1_700_000_000 < u0 < 1_800_000_000:
            type_b += 1
            tss.append(u0)
        else:
            other += 1

    return type_a, type_b, all_zero, other, tss

def analyze_directory_region(filepath, label):
    """分析文件前2MB目录区域的结构"""
    file_size = os.path.getsize(filepath)
    data = read_at(filepath, 0, min(0x200000, file_size))

    # 文件头
    magic = data[:8]
    rest_header = data[8:256]

    print(f"\n{'='*70}")
    print(f"{label} — 文件头与目录区分析")
    print(f"{'='*70}")
    print(f"文件大小: {file_size:,} bytes ({file_size/1024/1024:.1f} MB)")
    print(f"Magic: {magic}")
    print(f"Magic ASCII: {magic.decode('ascii', errors='replace')}")

    # 找所有 switch ID 的位置
    print(f"\n--- 目录项位置分布 ---")
    dir_entries = []
    for sw_id in SWITCH_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos != -1:
            block = data[pos:pos+256]
            if len(block) >= 256:
                sid, f = parse_dir_entry(block)
                if sid == sw_id:
                    dir_entries.append((pos, sw_id, f))

    dir_entries.sort(key=lambda x: x[0])
    print(f"找到 {len(dir_entries)} 个目录项:")
    for pos, sw_id, f in dir_entries:
        block_raw = data[pos:pos+256]
        # 前面还有别的文本吗？
        before = data[max(0,pos-32):pos]
        before_text = before.rstrip(b'\x00').decode('ascii', errors='replace') if any(b != 0 for b in before) else "(无)"
        # 256B 块中 offset 0x70 以后的 F0-F12
        f3, f4, f5, f6, f7, f8, f9 = f[3], f[4], f[5], f[6], f[7], f[8], f[9]
        # 块中除了 switch ID 还有别的文本吗？检查 0x00-0x6F 区域
        text_region = block_raw[8:0x70]
        # 查找可打印ASCII文本
        text_parts = []
        i = 0
        while i < len(text_region):
            b = text_region[i]
            if 32 <= b < 127:
                j = i
                while j < len(text_region) and 32 <= text_region[j] < 127:
                    j += 1
                if j - i >= 3:
                    text_parts.append(text_region[i:j].decode('ascii', errors='replace'))
                i = j
            else:
                i += 1
        extra_text = ', '.join(text_parts) if text_parts else ""

        # 检查 F8 (之前未关注)
        f8_info = f"F8=0x{f[8]:08x}({f[8]})" if f[8] > 0 else f"F8=0x{f[8]:08x}"

        print(f"  @0x{pos:06x} [{sw_id:5s}] "
              f"F3=0x{f3:08x} F4=0x{f4:08x}({f4:>10,}) F5={f5:>8} F6={f6:>10,} "
              f"F7={f7:>6} F9=0x{f9:08x}({f9:>10,}) {f8_info}")
        if extra_text:
            print(f"           附加文本: [{extra_text}]")
        if before_text and before_text != "(无)":
            print(f"           前置文本: [{before_text}]")

    return dir_entries

def compare_sub_index(fpath1, fpath2, sw_id, f4_1, f7_1, f4_2, f7_2):
    """对比两个文件中同一道岔的子索引"""
    file_size1 = os.path.getsize(fpath1)
    file_size2 = os.path.getsize(fpath2)

    # 读子索引
    raw1 = read_at(fpath1, f4_1, min(f7_1 * 32, 0x200000)) if f4_1 > 0 and f7_1 > 0 else b''
    raw2 = read_at(fpath2, f4_2, min(f7_2 * 32, 0x200000)) if f4_2 > 0 and f7_2 > 0 else b''

    print(f"\n--- [{sw_id}] 子索引对比 ---")
    print(f"  1.hbf: F4=0x{f4_1:x} F7={f7_1}  实际块大小={len(raw1)}")
    print(f"  2.hbf: F4=0x{f4_2:x} F7={f7_2}  实际块大小={len(raw2)}")

    # 统计类型
    for label, raw, f7, fs in [("1.hbf", raw1, f7_1, file_size1), ("2.hbf", raw2, f7_2, file_size2)]:
        type_a, type_b, all_zero, other, tss = count_sub_index_types(raw, f7, fs)
        print(f"  [{label}] TypeA(0x1227)={type_a}  TypeB(时间戳)={type_b}  全零={all_zero}  其他={other}")
        if tss:
            tss_sorted = sorted(tss)
            print(f"         时间戳范围: {datetime.fromtimestamp(tss_sorted[0])} ~ {datetime.fromtimestamp(tss_sorted[-1])}")
            print(f"         时间跨度: {(tss_sorted[-1]-tss_sorted[0])/86400:.1f} 天")

    # 比较前几条非零记录的差异
    print(f"\n  前5条非零记录对比:")

    for label, raw, f7, fs in [("1.hbf", raw1, f7_1, file_size1), ("2.hbf", raw2, f7_2, file_size2)]:
        count = 0
        for i in range(min(f7, 5000)):
            off = i * 32
            if off + 32 > len(raw):
                break
            rec = raw[off:off+32]
            if set(rec) == {0}:
                continue
            u32s = list(struct.unpack('<I', rec[j*4:(j+1)*4])[0] for j in range(8))
            if count < 5:
                ts_info = ""
                for j, v in enumerate(u32s):
                    if 1_700_000_000 < v < 1_800_000_000:
                        ts_info += f" u32[{j}]=ts({datetime.fromtimestamp(v)})"
                print(f"    [{label}][{i}] u32: {[f'0x{v:08x}' for v in u32s]}{ts_info}")
            count += 1
        print(f"    [{label}] 非零记录总数: {count}/{min(f7,5000)}")

def compare_data_areas(fpath1, fpath2, sw_id, f9_1, f7_1, f9_2, f7_2, f4_1, f4_2):
    """对比 F9 数据区的内容"""
    file_size1 = os.path.getsize(fpath1)
    file_size2 = os.path.getsize(fpath2)

    print(f"\n--- [{sw_id}] F9 数据区对比 ---")

    for label, fpath, f9, f7, fs in [("1.hbf", fpath1, f9_1, f7_1, file_size1),
                                       ("2.hbf", fpath2, f9_2, f7_2, file_size2)]:
        if not (0 < f9 < fs):
            print(f"  [{label}] F9=0x{f9:x} 无效")
            continue

        read_size = min(128 * 1024, fs - f9)
        raw = read_at(fpath, f9, read_size)

        # 非零字节统计
        nz_bytes = sum(1 for b in raw if b != 0)
        print(f"  [{label}] F9=0x{f9:x} 读{read_size}字节  非零字节={nz_bytes}/{read_size} ({100*nz_bytes/read_size:.1f}%)")

        # float32 分析
        f32 = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(2000, len(raw)//4))]
        nz_f32 = sum(1 for v in f32 if abs(v) > 0.001)
        peak_f32 = max(f32) if f32 else 0
        print(f"  [{label}] float32连续: 非零={nz_f32}/2000 峰值={peak_f32:.3f}")

        if nz_f32 > 0:
            # 找非零段
            segments = []
            in_nz = False
            start = 0
            for j, v in enumerate(f32[:2000]):
                if abs(v) > 0.001 and not in_nz:
                    in_nz = True
                    start = j
                elif abs(v) < 0.001 and in_nz:
                    in_nz = False
                    if j - start > 20:
                        segments.append((start, j, j-start))
            if in_nz:
                segments.append((start, len(f32), len(f32)-start))

            if segments:
                print(f"  [{label}] 非零段 ({len(segments)}个):")
                for s, e, l in segments[:5]:
                    seg_peak = max(f32[s:e])
                    first5 = [round(v,3) for v in f32[s:s+5]]
                    last5 = [round(v,3) for v in f32[e-5:e]]
                    print(f"         [{s:5d}-{e:5d}] len={l:4d} 峰值={seg_peak:.3f} 起始={first5} 结束={last5}")

        # 前128字节hex对比
        if nz_bytes > 0:
            print(f"  [{label}] 前128字节hex:")
            for i in range(0, 128, 32):
                line = raw[i:i+32]
                hex_str = ' '.join(f'{b:02x}' for b in line)
                ascii_str = ''.join(chr(b) if 32 <= b < 127 else '.' for b in line)
                print(f"         {i:04x}: {hex_str}  {ascii_str}")


def main():
    fpath1 = os.path.join(POWER_DIR, '1.hbf')
    fpath2 = os.path.join(POWER_DIR, '2.hbf')

    if not os.path.exists(fpath1) or not os.path.exists(fpath2):
        print("文件不存在!")
        print(f"1.hbf: {os.path.exists(fpath1)}")
        print(f"2.hbf: {os.path.exists(fpath2)}")
        return

    print("功率/1.hbf vs 功率/2.hbf 全面对比")
    print("="*70)

    # ==== 1. 文件级别对比 ====
    size1 = os.path.getsize(fpath1)
    size2 = os.path.getsize(fpath2)
    print(f"\n【1. 文件大小】")
    print(f"  1.hbf: {size1:,} bytes ({size1/1024/1024:.1f} MB)")
    print(f"  2.hbf: {size2:,} bytes ({size2/1024/1024:.1f} MB)")
    print(f"  差异: {size2-size1:+,} bytes")

    # ==== 2. 文件头对比 ====
    print(f"\n【2. 文件头 0-255 对比】")
    hdr1 = read_at(fpath1, 0, 256)
    hdr2 = read_at(fpath2, 0, 256)

    print(f"  Magic 1.hbf: {hdr1[:16].hex()} → '{hdr1[:8].decode('ascii', errors='replace')}'")
    print(f"  Magic 2.hbf: {hdr2[:16].hex()} → '{hdr2[:8].decode('ascii', errors='replace')}'")

    # 逐字节diff
    diff_positions = []
    for i in range(256):
        if hdr1[i] != hdr2[i]:
            diff_positions.append(i)

    if diff_positions:
        print(f"  文件头不同字节: {len(diff_positions)} 处 (共256)")
        for i in diff_positions[:20]:
            print(f"    offset 0x{i:02x}: 1.hbf=0x{hdr1[i]:02x}  2.hbf=0x{hdr2[i]:02x}")
        if len(diff_positions) > 20:
            print(f"    ... 还有 {len(diff_positions)-20} 处")
        # 按区域分组
        ranges = []
        start = diff_positions[0]
        end = diff_positions[0]
        for pos in diff_positions[1:]:
            if pos == end + 1:
                end = pos
            else:
                ranges.append((start, end))
                start = end = pos
        ranges.append((start, end))
        print(f"  差异区域: {[f'0x{s:02x}-0x{e:02x}' for s, e in ranges]}")
    else:
        print(f"  文件头完全相同!")

    # ==== 3. 目录项对比 ====
    print(f"\n【3. 256B 目录项对比】")
    entries1 = analyze_directory_region(fpath1, "1.hbf")
    entries2 = analyze_directory_region(fpath2, "2.hbf")

    # 逐项对比 F0-F12
    print(f"\n--- 逐道岔 F字段对比 ---")
    e1_dict = {sw_id: f for _, sw_id, f in entries1}
    e2_dict = {sw_id: f for _, sw_id, f in entries2}

    for sw_id in SWITCH_IDS:
        f1 = e1_dict.get(sw_id)
        f2 = e2_dict.get(sw_id)

        if f1 is None and f2 is None:
            continue
        if f1 is None:
            print(f"  [{sw_id}] 仅在2.hbf中存在")
            continue
        if f2 is None:
            print(f"  [{sw_id}] 仅在1.hbf中存在")
            continue

        diffs = []
        for j in range(13):
            if f1[j] != f2[j]:
                diffs.append((j, f1[j], f2[j]))

        if diffs:
            diff_str = '  '.join(f"F{j}: {v1}→{v2}" for j, v1, v2 in diffs)
            print(f"  [{sw_id}] 差异: {diff_str}")
        # else: 完全相同的就不打印，减少噪音

    # 统计哪些字段最常变化
    field_diff_count = defaultdict(int)
    field_values1 = defaultdict(list)
    field_values2 = defaultdict(list)
    for sw_id in SWITCH_IDS:
        f1 = e1_dict.get(sw_id)
        f2 = e2_dict.get(sw_id)
        if f1 and f2:
            for j in range(13):
                field_values1[j].append(f1[j])
                field_values2[j].append(f2[j])
                if f1[j] != f2[j]:
                    field_diff_count[j] += 1

    print(f"\n--- 字段差异统计 (30道岔中) ---")
    print(f"  {'字段':<6} {'差异数':<8} {'1.hbf值范围':<50} {'2.hbf值范围':<50}")
    for j in range(13):
        if field_values1[j]:
            v1_min, v1_max = min(field_values1[j]), max(field_values1[j])
            v2_min, v2_max = min(field_values2[j]), max(field_values2[j])
            # 判断差异类型
            if field_diff_count[j] == 0:
                note = "✅ 完全相同"
            elif field_diff_count[j] == 30:
                note = "🔴 全部不同"
            else:
                note = f"⚠️ {field_diff_count[j]}/30不同"

            # 格式化值范围
            if v1_min >= 0x100:
                v1r = f"0x{v1_min:x}~0x{v1_max:x}"
                v2r = f"0x{v2_min:x}~0x{v2_max:x}"
            else:
                v1r = f"{v1_min}~{v1_max}"
                v2r = f"{v2_min}~{v2_max}"
            print(f"  F{j:<5} {note:<12} {v1r:<50} {v2r:<50}")

    # ==== 4. 选几个代表道岔对比子索引和数据区 ====
    print(f"\n{'='*70}")
    print(f"【4. 代表道岔深度对比】")
    print(f"{'='*70}")

    # 选 F7 差异大的几个
    f7_diffs = []
    for sw_id in SWITCH_IDS:
        f1 = e1_dict.get(sw_id)
        f2 = e2_dict.get(sw_id)
        if f1 and f2:
            f7_diffs.append((abs(f1[7] - f2[7]), sw_id, f1, f2))
    f7_diffs.sort(key=lambda x: -x[0])

    for _, sw_id, f1, f2 in f7_diffs[:5]:
        print(f"\n{'='*60}")
        print(f"深度对比: [{sw_id}]")
        print(f"{'='*60}")

        # 目录项层面的差异
        for j in range(13):
            if f1[j] != f2[j]:
                print(f"  F{j}: 1.hbf={f1[j]} (0x{f1[j]:x})  →  2.hbf={f2[j]} (0x{f2[j]:x})")

        # 子索引对比
        compare_sub_index(fpath1, fpath2, sw_id, f1[4], f1[7], f2[4], f2[7])

        # F9数据区对比
        compare_data_areas(fpath1, fpath2, sw_id, f1[9], f1[7], f2[9], f2[7], f1[4], f2[4])

    # ==== 5. 全局扫描: 两个文件中非零数据分布 ====
    print(f"\n{'='*70}")
    print(f"【5. 全文件非零数据分布抽样】")
    print(f"{'='*70}")

    for label, fpath, size in [("1.hbf", fpath1, size1), ("2.hbf", fpath2, size2)]:
        # 在多个位置抽样
        sample_positions = list(range(0, size, size // 20))  # 20个采样点
        print(f"\n  [{label}] ({size/1024/1024:.0f}MB) 20点抽样:")
        for sample_idx, pos in enumerate(sample_positions):
            chunk = read_at(fpath, pos, 4096)
            nz = sum(1 for b in chunk if b != 0)
            ratio = nz / len(chunk) * 100
            bar = '█' * int(ratio / 5) + '░' * (20 - int(ratio / 5))
            print(f"    @0x{pos:010x} [{bar}] {ratio:5.1f}% ({nz}/{len(chunk)})")

    # ==== 6. F3字段分析 (可能是道岔ID常量) ====
    print(f"\n{'='*70}")
    print(f"【6. F3 字段分析 (switch ID constant?)】")
    print(f"{'='*70}")

    for label, entries in [("1.hbf", entries1), ("2.hbf", entries2)]:
        print(f"\n  [{label}]:")
        for _, sw_id, f in entries[:5]:
            print(f"    {sw_id}: F3=0x{f[3]:08x} ({f[3]})")
            # F3 是否看起来像开关编号？
        # 看所有F3值是否随道岔号有规律
        all_f3 = [(sw_id, f[3]) for _, sw_id, f in entries]
        all_f3.sort(key=lambda x: x[1])
        # 只看一些样本
        for sw_id, f3_val in all_f3:
            pass  # 下面汇总打印

    # 汇总 F3 的分布
    f3_set_1 = set(f[3] for _, _, f in entries1)
    f3_set_2 = set(f[3] for _, _, f in entries2)
    print(f"\n  1.hbf F3唯一值: {len(f3_set_1)} 个")
    print(f"  2.hbf F3唯一值: {len(f3_set_2)} 个")
    print(f"  两个文件F3交集: {len(f3_set_1 & f3_set_2)} 个")
    if f3_set_1 == f3_set_2:
        print(f"  ✅ F3值两个文件完全相同!")
    else:
        print(f"  ⚠️ F3值不同: 仅在1.hbf={f3_set_1-f3_set_2}  仅在2.hbf={f3_set_2-f3_set_1}")


if __name__ == '__main__':
    main()
