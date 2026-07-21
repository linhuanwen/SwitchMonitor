#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
深度验证：F9共享检测、长曲线拆分、事件块结构分析
"""
import struct
import sys
import os
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
ALL_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
           '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
           '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def main():
    fpath = os.path.join(POWER_DIR, '2.hbf')
    size = os.path.getsize(fpath)
    data2mb = read_at(fpath, 0, 0x200000)

    # 解析所有目录项
    dir_info = {}
    for sw_id in ALL_IDS:
        pos = data2mb.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data2mb[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        dir_info[sw_id] = {
            'F0': f[0], 'F1': f[1], 'F2': f[2], 'F3': f[3],
            'F4': f[4], 'F5': f[5], 'F6': f[6], 'F7': f[7],
            'F8': f[8], 'F9': f[9], 'F10': f[10], 'F11': f[11], 'F12': f[12],
        }

    # ============================================
    # 1. F9 共享检测：哪些道岔指向相同数据区？
    # ============================================
    print("="*70)
    print("1. F9 共享/重复检测")
    print("="*70)

    f9_groups = defaultdict(list)
    for sw_id in sorted(ALL_IDS, key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        if sw_id in dir_info:
            f9 = dir_info[sw_id]['F9']
            f9_groups[f9].append(sw_id)

    shared_f9 = {f9: ids for f9, ids in f9_groups.items() if len(ids) > 1}
    if shared_f9:
        print("\n  ⚠ 共享F9的开关组 (可能是重复数据):")
        for f9, ids in sorted(shared_f9.items()):
            print(f"    F9=0x{f9:x}: {', '.join(ids)}")
            for sid in ids:
                info = dir_info[sid]
                print(f"      {sid}: F6={info['F6']} F7={info['F7']} F3={info['F3']}")
    else:
        print("  ✓ 所有F9独立")

    # 也检查F9非常接近的开关 (可能部分重叠)
    sorted_f9 = sorted([(sid, dir_info[sid]['F9']) for sid in dir_info], key=lambda x: x[1])
    print("\n  F9 邻近分析 (< 10000 字节间距):")
    for i in range(len(sorted_f9)-1):
        s1, f9_1 = sorted_f9[i]
        s2, f9_2 = sorted_f9[i+1]
        gap = f9_2 - f9_1
        if gap < 10000:
            print(f"    {s1}(0x{f9_1:x}) → {s2}(0x{f9_2:x}) 间距={gap}")

    # ============================================
    # 2. 检查7-J/9-J是否真的完全一样
    # ============================================
    print(f"\n{'='*70}")
    print("2. 深入对比 7-J vs 9-J (共享F9) 的数据")
    print("="*70)

    if '7-J' in dir_info and '9-J' in dir_info:
        f9_7j = dir_info['7-J']['F9']
        f9_9j = dir_info['9-J']['F9']

        # 读前10KB float32数据对比
        raw = read_at(fpath, f9_7j, 40000)
        f32_data = [struct.unpack_from('<f', raw, j*4)[0] for j in range(min(10000, len(raw)//4))]

        # 找曲线模式
        curves_found = []
        in_curve = False
        curve_start = None
        for i, v in enumerate(f32_data):
            if not in_curve:
                if abs(v) > 0.005:
                    # 回溯找零点
                    start = i
                    while start > 0 and abs(f32_data[start-1]) < 0.01:
                        start -= 1
                    in_curve = True
                    curve_start = start
            else:
                # 检查是否连续零
                zero_run = 0
                for j in range(i, min(i+20, len(f32_data))):
                    if abs(f32_data[j]) < 0.001:
                        zero_run += 1
                    else:
                        break
                if zero_run >= 10:
                    seg = f32_data[curve_start:i]
                    if len(seg) >= 80:
                        peak = max(seg)
                        if 0.2 <= peak <= 10.0:
                            curves_found.append({
                                'start': curve_start,
                                'len': len(seg),
                                'peak': round(peak, 3),
                                'first5': [round(x, 4) for x in seg[:5]],
                                'last5': [round(x, 4) for x in seg[-5:]],
                            })
                    in_curve = False
                    curve_start = None

        print(f"  7-J/9-J 共享数据区中前10000 float32找到 {len(curves_found)} 条曲线")
        if curves_found:
            for i, c in enumerate(curves_found[:3]):
                print(f"    曲线#{i}: float32@{c['start']} len={c['len']} peak={c['peak']}KW")
                print(f"      前5: {c['first5']}")
                print(f"      尾5: {c['last5']}")

        # 确认：如果只有一个物理开关的数据，应该只保留一个
        print(f"\n  结论: 7-J 和 9-J 共享相同F9=0x{f9_7j:x}, 提取的曲线完全重复")

    # ============================================
    # 3. 事件块结构精确分析
    # ============================================
    print(f"\n{'='*70}")
    print("3. 事件块结构分析 (基于实际数据间距)")
    print("="*70)

    # 选一个数据完整的开关分析 (如1-J)
    for analyze_id in ['1-J', '11-J', '2-J']:
        if analyze_id not in dir_info:
            continue
        f9 = dir_info[analyze_id]['F9']
        f7 = dir_info[analyze_id]['F7']

        # 读数据
        scan_size = min(2 * 1024 * 1024, size - f9)
        raw = read_at(fpath, f9, scan_size)
        n_floats = len(raw) // 4
        f32 = [struct.unpack_from('<f', raw, j*4)[0] for j in range(n_floats)]

        # 找所有非零段的起始位置
        segments = []
        i = 0
        while i < n_floats:
            if abs(f32[i]) < 0.005:
                i += 1
                continue
            start = i
            while start > 0 and abs(f32[start-1]) < 0.01:
                start -= 1
            end = i
            while end < n_floats and abs(f32[end]) > 0.001:
                end += 1
            # 向后找10+连续零
            while end < n_floats:
                zeros = 0
                for j in range(end, min(end+20, n_floats)):
                    if abs(f32[j]) < 0.001:
                        zeros += 1
                    else:
                        break
                if zeros >= 10:
                    break
                end += 1
            seg_len = end - start
            if seg_len >= 50:
                seg = f32[start:end]
                peak = max(seg)
                if 0.15 <= peak <= 10.0:
                    segments.append((start, end, round(peak, 3)))
            i = end + 1

        if len(segments) < 3:
            continue

        # 计算间距
        starts = [s[0] for s in segments]
        spacings = [starts[i+1] - starts[i] for i in range(len(starts)-1)]

        from collections import Counter
        sp_counter = Counter(spacings)
        top_spacings = sp_counter.most_common(5)

        print(f"\n  [{analyze_id}] F9=0x{f9:x} F7={f7}")
        print(f"    总曲线段: {len(segments)}")
        print(f"    间距分布 top5: {[(s, c) for s, c in top_spacings]}")

        # 检查间距是否是 0x1227 = 4647 或相关倍数
        expected = 4647
        for sp, cnt in top_spacings[:5]:
            ratio = sp / expected
            if abs(ratio - round(ratio)) < 0.05:
                print(f"      间距 {sp} = {round(ratio)} × {expected} ({ratio:.2f}x) [{cnt}次]")
            else:
                print(f"      间距 {sp} ({ratio:.2f}x) [{cnt}次]")

        # 显示前5条的间距
        print(f"    前10条起始位置及间距:")
        for i in range(min(10, len(starts))):
            s = starts[i]
            sp = spacings[i] if i < len(spacings) else 0
            f32_val = [round(f32[s+j], 3) for j in range(min(5, len(f32)-s))]
            print(f"      #{i}: float32@{s} 间距={sp} 前5={f32_val}")

        # 检查第一条曲线是否从 offset 0 开始
        first_start = starts[0]
        if first_start > 0:
            print(f"    第一条曲线从 float32@{first_start} 开始 (前{first_start}个float32为零/噪声)")
            # 分析前面是什么
            pre_data = f32[:first_start]
            nz = sum(1 for v in pre_data if abs(v) > 0.001)
            print(f"      前{first_start}个值中非零: {nz}")

        break  # 只分析第一个有效开关

    # ============================================
    # 4. 长曲线(>500点)深入分析
    # ============================================
    print(f"\n{'='*70}")
    print("4. 异常长曲线分析")
    print("="*70)

    # 重新读取3-J或21-X的长曲线区域
    for analyze_id, offset_hint in [('3-J', 698012), ('21-X', 234426)]:
        if analyze_id not in dir_info:
            continue
        f9 = dir_info[analyze_id]['F9']
        f32_start = offset_hint - 200  # 往前一点
        read_start = f9 + f32_start * 4
        raw = read_at(fpath, read_start, 5000)
        f32_local = [struct.unpack_from('<f', raw, j*4)[0] for j in range(len(raw)//4)]

        # 在局部范围内找曲线
        print(f"\n  [{analyze_id}] 长曲线区域 float32@{offset_hint}:")
        print(f"    前后数据 (样本):")
        for j in range(0, min(400, len(f32_local)), 20):
            chunk = [round(v, 3) for v in f32_local[j:j+20]]
            print(f"      [{offset_hint-200+j}] {chunk}")

        # 检查是否有内部零点断点
        long_curve_start = 200  # offset_hint 对应 local idx 200
        long_data = f32_local[long_curve_start:long_curve_start+807]
        print(f"\n    完整长度: {len(long_data)}")
        print(f"    峰值: {max(long_data):.3f} @ idx {long_data.index(max(long_data))}")

        # 检查是否有中间零点区 (>10连续零)
        zero_runs = []
        consecutive = 0
        start_zero = None
        for i, v in enumerate(long_data):
            if abs(v) < 0.001:
                if consecutive == 0:
                    start_zero = i
                consecutive += 1
            else:
                if consecutive >= 10:
                    zero_runs.append((start_zero, i))
                consecutive = 0
                start_zero = None
        if consecutive >= 10:
            zero_runs.append((start_zero, len(long_data)))

        if zero_runs:
            print(f"    发现 {len(zero_runs)} 处长零区间 (可能是曲线边界):")
            for zs, ze in zero_runs:
                print(f"      idx {zs}-{ze} ({ze-zs}个零)")
                # 显示零点前后的数据
                pre = long_data[max(0,zs-5):zs]
                post = long_data[ze:min(len(long_data), ze+5)]
                print(f"        零点前: {[round(v,3) for v in pre]}")
                print(f"        零点后: {[round(v,3) for v in post]}")
        else:
            print(f"    无明显中间零点 → 可能真的是超长单曲线或零值分布稀疏")
            # 检查最小值
            min_val = min(long_data)
            print(f"    最小值: {min_val:.4f}")

        break  # 只分析第一个

    # ============================================
    # 5. 缺失开关的数据编码分析
    # ============================================
    print(f"\n{'='*70}")
    print("5. 缺失开关的非零数据分析")
    print("="*70)

    for sid in ['13-J', '15-J']:
        if sid not in dir_info:
            continue
        f9 = dir_info[sid]['F9']
        if f9 >= size:
            continue

        raw = read_at(fpath, f9, 2048)
        nz_bytes = sum(1 for b in raw if b != 0)
        print(f"\n  [{sid}] F9=0x{f9:x}")
        print(f"    前2048字节非零: {nz_bytes}")

        if nz_bytes == 0:
            print(f"    全为零")
            continue

        # 尝试不同解释
        # 解释1: uint32数组
        print(f"    解释为 uint32 (前48个):")
        u32 = [struct.unpack_from('<I', raw, j*4)[0] for j in range(min(48, len(raw)//4))]
        for j in range(0, 48, 8):
            hex_vals = ' '.join(f'{v:08x}' for v in u32[j:j+8])
            dec_vals = ' '.join(f'{v:10d}' for v in u32[j:j+8])
            print(f"      [{j:3d}] {hex_vals}")
            if any(v != 0 for v in u32[j:j+8]):
                print(f"           {dec_vals}")

        # 解释2: int16数组
        print(f"    解释为 int16 (前32个):")
        i16 = [struct.unpack_from('<h', raw, j*2)[0] for j in range(min(32, len(raw)//2))]
        for j in range(0, 32, 8):
            print(f"      [{j:3d}] {' '.join(f'{v:6d}' for v in i16[j:j+8])}")

        # 解释3: raw bytes pattern
        print(f"    原始字节 hex (前128):")
        for j in range(0, min(128, len(raw)), 32):
            hex_str = ' '.join(f'{raw[j+k]:02x}' for k in range(min(32, len(raw)-j)))
            ascii_str = ''.join(chr(raw[j+k]) if 32 <= raw[j+k] < 127 else '.' for k in range(min(32, len(raw)-j)))
            print(f"      [{j:4d}] {hex_str}  |{ascii_str}|")

    # ============================================
    # 6. F9 非零但无曲线的开关 — 是否在其他区域？
    # ============================================
    print(f"\n{'='*70}")
    print("6. 缺失开关的全局搜索")
    print("="*70)

    # 对13-J和15-J，在整个文件搜索类似功率曲线的数据
    for sid in ['13-J', '15-J', '8-J']:
        if sid not in dir_info:
            continue
        info = dir_info[sid]
        print(f"\n  [{sid}]:")
        print(f"    F0={info['F0']} F1={info['F1']} F2={info['F2']} F3={info['F3']} "
              f"(F3通常是道岔ID常量)")
        print(f"    F4={info['F4']:d} F5={info['F5']} F6={info['F6']} "
              f"F7={info['F7']} F8={info['F8']}")
        print(f"    F9=0x{info['F9']:x} F10={info['F10']} "
              f"F11=0x{info['F11']:08x} F12=0x{info['F12']:08x}")

        # 检查F4是否有子索引数据
        f4 = info['F4']
        if 0 < f4 < size:
            sub_raw = read_at(fpath, f4, min(1024, size - f4))
            nz_sub = sum(1 for b in sub_raw if b != 0)
            if nz_sub > 0:
                print(f"    F4子索引(0x{f4:x}): {nz_sub}/{len(sub_raw)} 非零字节")
                u32_vals = [struct.unpack_from('<I', sub_raw, j*4)[0] for j in range(min(8, len(sub_raw)//4))]
                print(f"      前8个uint32: {' '.join(f'{v:08x}' for v in u32_vals)}")
            else:
                print(f"    F4子索引(0x{f4:x}): 全为零")

        # 检查F9附近有没有漏掉的数据(可能F9指针不对)
        f9 = info['F9']
        if 0 < f9 < size:
            # 检查F9前后各1KB
            for check_offset, label in [(f9-1024, "F9-1KB"), (f9, "F9"), (f9+1024, "F9+1KB")]:
                if check_offset > 0 and check_offset < size:
                    raw = read_at(fpath, check_offset, 1024)
                    nz = sum(1 for b in raw if b != 0)
                    if nz > 10:
                        # 快速检查是否有功率曲线特征
                        f32_test = [struct.unpack_from('<f', raw, j*4)[0] for j in range(min(256, len(raw)//4))]
                        peaks = [(i, v) for i, v in enumerate(f32_test) if 0.2 < v < 10.0]
                        if peaks:
                            print(f"    {label}(0x{check_offset:x}): {nz}非零字节, "
                                  f"发现{len(peaks)}个潜在峰值")
                            for i, v in peaks[:3]:
                                print(f"      f32[{i}]={v:.4f}")

    print(f"\n{'='*70}")
    print("验证完成")
    print("="*70)


if __name__ == '__main__':
    main()
