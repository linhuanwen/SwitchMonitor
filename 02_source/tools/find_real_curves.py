#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全新策略：
1. 从配置得知：1032采样点/事件, 40ms间隔, 0-5KW范围, 3通道
2. 直接扫描整个文件，找不含0x1227标记的连续非零段
3. 对每个段尝试：(a) float32解码 (b) 除以比例因子 (c) 拆分3通道
"""
import struct, sys, os

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"
CURRENT_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线\1.hbf"

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def score_curve(vals, expected_peak_range=(0.5, 8), expected_steady_range=(0.05, 2)):
    """评分：是否符合功率曲线特征"""
    n = len(vals)
    if n < 50:
        return 0, {}

    peak = max(vals)
    if peak < expected_peak_range[0] or peak > expected_peak_range[1]:
        return 0, {}

    peak_idx = vals.index(peak)

    # 开头接近0
    head = vals[:max(5, n//20)]
    hz = sum(1 for v in head if abs(v) < 0.1)
    if hz < len(head) * 0.3:
        return 0, {}

    # 峰值位置
    if peak_idx < n * 0.05 or peak_idx > n * 0.8:
        return 0, {}

    # 稳态段
    steady_start = peak_idx + max(3, n//15)
    steady_end = min(n - max(3, n//15), peak_idx + n//2)
    if steady_end <= steady_start:
        return 0, {}
    steady = vals[steady_start:steady_end]
    sr = sum(1 for v in steady if expected_steady_range[0] < abs(v) < expected_steady_range[1])
    if len(steady) > 0 and sr / len(steady) < 0.3:
        return 0, {}

    # 结尾接近0
    tail = vals[-max(3, n//15):]
    tz = sum(1 for v in tail if abs(v) < 0.1)
    if tz < len(tail) * 0.3:
        return 0, {}

    # 平滑度
    jumps = sum(1 for i in range(1, min(n, 200)) if abs(vals[i]-vals[i-1]) > peak*0.4)
    smooth = 1 - min(1, jumps / min(n, 200))

    score = 30 + min(30, peak*8) + sr/max(1,len(steady))*20 + smooth*15
    return score, {'peak': peak, 'peak_idx': peak_idx, 'n': n, 'smooth': smooth, 'steady_ratio': sr/max(1,len(steady))}

def find_curve_data_block(filepath, region_start, region_size):
    """在非零区域中找曲线数据——跳过索引记录部分"""
    raw = read_at(filepath, region_start, min(region_size, 524288))

    # 找到所有0x1227的位置
    marker = b'\x27\x12\x00\x00'
    marker_positions = []
    pos = 0
    while True:
        p = raw.find(marker, pos)
        if p == -1:
            break
        marker_positions.append(p)
        pos = p + 1

    results = []

    # 策略1: 找标记之间的gap
    for i in range(len(marker_positions) - 1):
        gap_start = marker_positions[i] + 32
        gap_end = marker_positions[i+1]
        gap_size = gap_end - gap_start
        if gap_size < 200:  # 太小，跳过
            continue

        gap_data = raw[gap_start:gap_end]

        # 跳过全零
        nz = sum(1 for b in gap_data[:1024] if b != 0)
        if nz < 100:
            continue

        # 尝试float32
        for stride, fmt, bps in [(4, '<f', 4), (2, '<H', 2), (2, '<h', 2)]:
            for offset in range(0, min(stride, 32)):
                sub = gap_data[offset:]
                n_samples = min(1200, len(sub) // bps)
                if n_samples < 50:
                    continue

                try:
                    vals = [struct.unpack(fmt, sub[j*bps:(j+1)*bps])[0] for j in range(n_samples)]
                except:
                    continue

                # 过滤异常值
                valid = [v for v in vals if abs(v) < 1e8]
                if len(valid) < 50:
                    continue

                score, info = score_curve(valid)
                if score > 30:
                    abs_offset = region_start + gap_start + offset
                    results.append((score, abs_offset, valid, {**info, 'fmt': fmt, 'stride': stride}))

    # 策略2: 找最后一个标记之后的数据
    if marker_positions:
        last_marker = marker_positions[-1]
        tail_start = last_marker + 32
        tail_data = raw[tail_start:tail_start + 262144]

        nz = sum(1 for b in tail_data[:4096] if b != 0)
        if nz > 100:
            # 尝试float32
            for stride, fmt, bps in [(4, '<f', 4), (2, '<H', 2), (2, '<h', 2)]:
                for offset in range(0, min(stride, 32)):
                    sub = tail_data[offset:]
                    n_samples = min(1200, len(sub) // bps)
                    try:
                        vals = [struct.unpack(fmt, sub[j*bps:(j+1)*bps])[0] for j in range(n_samples)]
                    except:
                        continue
                    valid = [v for v in vals if abs(v) < 1e8]
                    if len(valid) < 50:
                        continue
                    score, info = score_curve(valid)
                    if score > 30:
                        abs_offset = region_start + tail_start + offset
                        results.append((score, abs_offset, valid, {**info, 'fmt': fmt, 'stride': stride}))

    return results

def main():
    print("搜索不含0x1227标记的功率曲线数据段")
    print("=" * 70)

    # 已知的大型非零区域偏移（从密度扫描）
    regions = [
        (POWER_1, 0x001c9c0c, 170000, "power1_zone1"),
        (POWER_1, 0x00249c0c, 170000, "power1_zone2"),
        (POWER_1, 0x00349c0c, 170000, "power1_zone3"),
        (POWER_1, 0x00009c0c, 150000, "power1_zone0"),
        (POWER_2, 0x000c9c0c, 330000, "power2_zone1"),
        (POWER_2, 0x00149c0c, 330000, "power2_zone2"),
        (POWER_2, 0x00109c0c, 330000, "power2_zone3"),
    ]

    all_results = []
    for fpath, offset, size, label in regions:
        print(f"\n分析 {label} @ 0x{offset:x}...")
        results = find_curve_data_block(fpath, offset, size)
        print(f"  找到 {len(results)} 个候选曲线")
        all_results.extend([(r[0], r[1], r[2], r[3], label, fpath) for r in results])

    # 也直接搜索一些未探索的偏移
    print(f"\n直接搜索关键偏移...")
    for fpath, flabel in [(POWER_1, "power1"), (POWER_2, "power2")]:
        for test_off in [0x1227, 0x244e, 0x3675, 0x48e0, 0x9cb8, 0xc7ad, 0xd9d4]:
            raw = read_at(fpath, test_off, 8192)
            nz = sum(1 for b in raw[:4096] if b != 0)
            if nz < 100:
                continue
            markers = len([i for i in range(len(raw)-4) if raw[i:i+4] == b'\x27\x12\x00\x00'])
            if markers > 10:  # 有太多标记，可能是索引区
                continue

            print(f"  {flabel} @ 0x{test_off:x}: nz={nz}/4096, markers={markers}")

            # 尝试float32
            for offset in range(0, 64, 4):
                n_samples = min(500, (len(raw) - offset) // 4)
                vals = [struct.unpack('<f', raw[offset+j*4:offset+(j+1)*4])[0] for j in range(n_samples)]
                valid = [v for v in vals if abs(v) < 1e6 and abs(v) > 1e-15]
                if len(valid) < 50:
                    continue
                score, info = score_curve(valid)
                if score > 30:
                    print(f"    ✅ offset={offset}: score={score:.0f} peak={info['peak']:.3f}@{info['peak_idx']}")
                    print(f"       前30: {[round(v,3) for v in valid[:30]]}")
                    all_results.append((score, test_off+offset, valid, info, f'{flabel}_direct', fpath))

    # 排序并显示最佳结果
    all_results.sort(key=lambda x: x[0], reverse=True)

    print(f"\n{'='*70}")
    print(f"🏆 最佳候选曲线 (共 {len(all_results)} 个)")
    print(f"{'='*70}")

    shown = set()
    for score, offset, vals, info, label, fpath in all_results[:20]:
        key = (fpath, offset // 4096)
        if key in shown:
            continue
        shown.add(key)

        print(f"\nScore={score:.0f} @ 0x{offset:x} ({label})")
        print(f"  peak={info['peak']:.3f} @ idx={info['peak_idx']}/{info['n']} "
              f"fmt={info.get('fmt','?')} smooth={info.get('smooth',0):.2f}")
        print(f"  前40: {[round(v,3) for v in vals[:40]]}")
        mid = info['peak_idx']
        if mid > 40:
            print(f"  峰值附近[{mid}]: {[round(v,3) for v in vals[mid-5:mid+15]]}")
        print(f"  后30: {[round(v,3) for v in vals[-30:]]}")

    print("\n✅ 搜索完成")

if __name__ == '__main__':
    main()
