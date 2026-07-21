#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全新策略：不解析索引，直接在文件中搜索符合功率曲线特征的数据段。
三水北参考：0→峰值3-4KW→稳态0.2-0.4KW→0，采样间隔40ms，持续9-12秒（230-305采样点）
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

def score_power_curve(values, debug=False):
    """
    给一组浮点数值打分，看它是否符合道岔功率曲线特征。
    特征：
    1. 开头几个值接近0
    2. 有一个明显的峰值 1-8 KW
    3. 峰值后有稳态段 0.1-1.0 KW
    4. 结尾接近0
    5. 曲线平滑（相邻值变化不大）
    返回 (score, details) — score越高越像
    """
    n = len(values)
    if n < 30:
        return 0, "too_short"

    # 开头：前10%的点接近0
    head_n = max(5, n // 10)
    head = values[:head_n]
    head_near_zero = sum(1 for v in head if abs(v) < 0.1)
    if head_near_zero < head_n * 0.5:
        return 0, f"head_not_zero: {[round(v,3) for v in head[:8]]}"

    # 峰值：至少有一个点 > 0.5 KW
    peak = max(values)
    peak_idx = values.index(peak)
    if peak < 0.5:
        return 0, f"peak_too_low: {peak:.3f}"
    if peak > 20:
        return 0, f"peak_too_high: {peak:.1f}"

    # 峰值位置合理：在前30%-70%
    if peak_idx < n * 0.1 or peak_idx > n * 0.85:
        return 0, f"peak_position_bad: {peak_idx}/{n}"

    # 稳态段：峰值后应有 0.05-1.5 范围的稳定段
    steady_start = peak_idx + max(5, n // 10)
    steady_end = min(n - max(3, n // 10), peak_idx + n // 2)
    if steady_end <= steady_start:
        return 0, "no_steady_region"

    steady = values[steady_start:steady_end]
    if len(steady) < 5:
        return 0, "steady_too_short"

    steady_in_range = sum(1 for v in steady if 0.03 < abs(v) < 2.0)
    steady_ratio = steady_in_range / len(steady)
    if steady_ratio < 0.4:
        return 0, f"steady_bad: ratio={steady_ratio:.2f}"

    # 结尾接近0
    tail_n = max(3, n // 10)
    tail = values[-tail_n:]
    tail_near_zero = sum(1 for v in tail if abs(v) < 0.15)
    if tail_near_zero < tail_n * 0.5:
        return 0, f"tail_not_zero: {[round(v,3) for v in tail]}"

    # 平滑度：相邻值变化不应太剧烈
    jumps = 0
    for i in range(1, n):
        if abs(values[i] - values[i-1]) > peak * 0.5:  # 半步变化超过峰值一半
            jumps += 1
    smoothness = 1.0 - min(1.0, jumps / n)

    # 综合得分
    score = 50  # 基础分
    score += min(30, peak * 8)  # 峰值奖励（3-4KW ≈ 24-32）
    score += steady_ratio * 20  # 稳态奖励
    score += smoothness * 10   # 平滑奖励

    return score, f"peak={peak:.2f}KW@{peak_idx}/{n} steady={steady_ratio:.2f} smooth={smoothness:.2f}"

def scan_for_curves(filepath, fmt='<f', sample_bytes=4, max_samples=500):
    """
    以滑动窗口扫描文件，尝试解码并评分
    """
    file_size = os.path.getsize(filepath)
    found = []

    # 采样不同的起始偏移（考虑对齐）
    with open(filepath, 'rb') as f:
        # 读取整个文件太过昂贵，采样关键区域
        # 策略：跳过文件头（前1MB），每隔一定距离采样
        chunk_size = 20 * 1024 * 1024  # 20MB chunks
        stride = 5 * 1024 * 1024       # 5MB stride

        for chunk_start in range(0x800, min(file_size, 536_000_000), stride):
            f.seek(chunk_start)
            raw = f.read(min(chunk_size, file_size - chunk_start))
            if len(raw) < 1000:
                continue

            # 每64字节尝试一个起始位置
            for offset in range(0, min(len(raw) - 4000, 4096), 64):
                # 提取样本
                end = offset + max_samples * sample_bytes
                if end > len(raw):
                    continue

                segment = raw[offset:end]
                n_samples = min(max_samples, len(segment) // sample_bytes)

                try:
                    if fmt == '<f':
                        vals = [struct.unpack('<f', segment[i*sample_bytes:(i+1)*sample_bytes])[0] for i in range(n_samples)]
                    elif fmt == '<H':
                        vals = [struct.unpack('<H', segment[i*sample_bytes:(i+1)*sample_bytes])[0] for i in range(n_samples)]
                    elif fmt == '<h':
                        vals = [struct.unpack('<h', segment[i*sample_bytes:(i+1)*sample_bytes])[0] for i in range(n_samples)]
                    elif fmt == '<I':
                        vals = [struct.unpack('<I', segment[i*sample_bytes:(i+1)*sample_bytes])[0] for i in range(n_samples)]
                    elif fmt == '<i':
                        vals = [struct.unpack('<i', segment[i*sample_bytes:(i+1)*sample_bytes])[0] for i in range(n_samples)]
                    elif fmt == '>f':
                        vals = [struct.unpack('>f', segment[i*sample_bytes:(i+1)*sample_bytes])[0] for i in range(n_samples)]
                    else:
                        continue
                except:
                    continue

                # 过滤明显的垃圾（inf, nan, 太大太小）
                valid_vals = [v for v in vals if abs(v) < 1e10 and abs(v) > 1e-10]
                if len(valid_vals) < 30:
                    continue

                # 过滤开头有太多变化剧烈的
                score, detail = score_power_curve(valid_vals)
                if score > 40:
                    abs_offset = chunk_start + offset
                    found.append((score, abs_offset, valid_vals[:300], detail, fmt))

    return found

def main():
    print("直接搜索功率曲线 — 绕过索引解析")
    print("=" * 70)

    for dirpath, label in [(POWER_DIR, "功率"), (CURRENT_DIR, "电流")]:
        if not os.path.exists(dirpath):
            continue

        for fname in sorted(os.listdir(dirpath)):
            if not fname.endswith('.hbf'):
                continue
            fpath = os.path.join(dirpath, fname)
            fsize = os.path.getsize(fpath)
            print(f"\n{'='*70}")
            print(f"扫描 {label}/{fname} ({fsize/1024/1024:.0f}MB)...")

            all_found = []
            for fmt, desc in [('<f', 'float32 LE'), ('>f', 'float32 BE'),
                            ('<h', 'int16 LE'), ('<H', 'uint16 LE'),
                            ('<i', 'int32 LE'), ('<I', 'uint32 LE')]:
                print(f"  尝试 {desc}...")
                found = scan_for_curves(fpath, fmt=fmt)
                print(f"    找到 {len(found)} 候选段")
                all_found.extend(found)

            # 按分数排序
            all_found.sort(key=lambda x: x[0], reverse=True)

            print(f"\n🏆 Top 20 候选功率曲线 ({len(all_found)} 总计):")
            print(f"{'Score':>6} {'Offset':>14} {'Samples':>8} {'Format':>12} {'Detail'}")
            print("-" * 90)

            shown = 0
            seen_offsets = set()
            for score, offset, vals, detail, fmt in all_found[:50]:
                # 去重：相近offset只显示一个
                bucket = offset // 10000
                if bucket in seen_offsets:
                    continue
                seen_offsets.add(bucket)

                print(f"{score:>6.0f} 0x{offset:012x} {len(vals):>8} {fmt:>12} {detail}")

                # 显示数值
                peak = max(vals)
                peak_idx = vals.index(peak)
                print(f"        前30: {[round(v,3) for v in vals[:30]]}")
                if peak_idx > 30:
                    print(f"        峰值附近[{peak_idx}]: {[round(v,3) for v in vals[peak_idx-5:peak_idx+10]]}")
                print(f"        后20: {[round(v,3) for v in vals[-20:]]}")
                print()

                shown += 1
                if shown >= 15:
                    break

    print("✅ 搜索完成")

if __name__ == '__main__':
    main()
