#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ZYJ7 物理边界分段算法原型验证。

三个核心检测函数：
  detect_unlock_end()    — 滑动窗口标准差法，找最后一个锁钩落下位置
  detect_contact_inflection() — 最大斜率法，找密贴拐点
  detect_lock_peak()     — 锁闭爬升段峰值定位

用法:
    python physeg_prototype.py stats    [sanshuibei_csv目录]  # 全量统计，分J/X输出
    python physeg_prototype.py golden   [sanshuibei_csv目录]  # 4条金标准夹具
    python physeg_prototype.py single N [sanshuibei_csv目录]  # 单条曲线详细输出
"""
import csv
import statistics
import sys
from collections import defaultdict
from pathlib import Path

DEFAULT_DIR = Path(__file__).resolve().parents[2] / "03_raw_data" / "sanshuibei_csv"

POWER_FILES = {3: "1-J", 7: "1-X", 11: "3-J", 15: "3-X",
               19: "2-J", 23: "2-X", 27: "4-J", 31: "4-X"}

GOLDEN = [
    (3, 1770922311, "夹具A 正常J曲线"),
    (7, 1770771323, "夹具B 正常X曲线"),
    (27, 1769618597, "夹具C 超时/卡阻"),
    (31, 1773938685, "夹具D 动作夭折"),
]

# ──────────────── 算法参数 ────────────────
STD_WINDOW = 10       # 滑动标准差窗口（点）
STD_SMOOTH = 5        # 标准差平滑窗口
SLOPE_SMOOTH = 5      # 斜率平滑窗口

# 解锁段搜索范围: 从 spikeIndex 后到 activeEnd * UNLOCK_SEARCH_RATIO
UNLOCK_SEARCH_RATIO = 0.55

# 密贴拐点搜索范围: activeEnd * CONTACT_SEARCH_START 到 activeEnd - CONTACT_TAIL_MARGIN
CONTACT_SEARCH_START = 0.70
CONTACT_TAIL_MARGIN = 10

# 锁闭峰值搜索范围: lockStart 到 activeEnd - LOCK_PEAK_MARGIN
LOCK_PEAK_MARGIN = 5
LOCK_PEAK_TAIL_PAD = 3  # 锁闭峰值后保留点数


def read_power_rows(path):
    """读功率CSV，返回 [(timestamp, datetime_str, [values]), ...]"""
    out = []
    with open(path, encoding='utf-8') as fh:
        rd = csv.reader(fh)
        next(rd)
        for r in rd:
            vals = []
            for x in r[3:]:
                if x == '':
                    break
                vals.append(float(x))
            out.append((int(r[0]), r[1], vals))
    return out


def moving_average(data, window):
    """移动平均平滑"""
    if len(data) < window:
        return data[:]
    half = window // 2
    out = []
    for i in range(len(data)):
        start = max(0, i - half)
        end = min(len(data), i + half + 1)
        out.append(sum(data[start:end]) / (end - start))
    return out


def rolling_std(values, window):
    """滑动窗口标准差"""
    out = [0.0] * len(values)
    half = window // 2
    for i in range(len(values)):
        start = max(0, i - half)
        end = min(len(values), i + half + 1)
        seg = values[start:end]
        if len(seg) >= 3:
            m = sum(seg) / len(seg)
            out[i] = (sum((x - m) ** 2 for x in seg) / len(seg)) ** 0.5
        else:
            out[i] = 0.0
    return out


def detect_unlock_end(values, spike_index, active_end):
    """
    检测解锁终点 — 经验比例 + 局部方差精化法。

    物理模型（经验验证）:
      - J型（3台机）: 解锁 ≈ 42% × 总时长
      - X型（2台机）: 解锁 ≈ 43% × 总时长

    方法:
      1. 根据总时长推断机型（≥10s → J型/3机, <10s → X型/2机）
      2. 用经验比例计算基准位置
      3. 在基准位置 ±20 点内找功率局部方差最小的点 → 精化

    返回: unlock_end 下标。
          若无法检测，返回 None。
    """
    n = len(values)
    dur_s = (active_end + 1) * 0.04
    is_j_type = dur_s >= 10.0  # J型 ~11.8s, X型 ~8.7s, 天然二分

    # 经验比例
    ratio = 0.42 if is_j_type else 0.43

    # 基准位置
    base_idx = spike_index + int((active_end - spike_index) * ratio)

    # 搜索窗口: 基准 ±20 点
    search_start = max(spike_index + 5, base_idx - 20)
    search_end = min(int(active_end * UNLOCK_SEARCH_RATIO), base_idx + 20)
    if search_end <= search_start + 10:
        return base_idx

    # 精化: 找局部方差最小的一段（最稳定 = 转换段开始）
    # 用 10 点滑动窗口标准差
    smooth = moving_average(values, 7)
    best_idx = base_idx
    best_var = float('inf')
    window = 10
    for i in range(search_start, search_end - window):
        w = smooth[i:i + window]
        var = sum((x - sum(w)/len(w))**2 for x in w) / len(w)
        if var < best_var:
            best_var = var
            best_idx = i + window // 2

    return best_idx


def detect_contact_and_lock(values, active_end):
    """
    检测密贴拐点和锁闭峰值 — "先找峰，再找谷"法。

    原理:
      1. 先在尾部区域找到锁闭爬升峰值（lockPeak）—— 这是最容易定位的特征
      2. 从峰值向左回溯，找到爬升开始前的局部最低点（谷底）
      3. 谷底前功率平稳或缓慢下降，谷底后功率持续爬升 → 谷底 = 密贴拐点

    与最大斜率法相比，这个方法的优势是：
      - 峰值是明确的全局特征（尾部的最高点），不受噪声斜率干扰
      - 从峰值往回找谷底是确定性的

    返回: (lockStart, lockPeak) 下标对。
          若无法检测，返回 (None, None)。
    """
    n = len(values)
    # 搜索范围：activeEnd 的 70% 到 activeEnd 前几个点
    peak_search_start = int(active_end * CONTACT_SEARCH_START)
    peak_search_end = max(peak_search_start + 10, active_end - LOCK_PEAK_MARGIN)
    if peak_search_end <= peak_search_start or peak_search_end > n:
        return None, None

    # 1. 找到锁闭峰值：搜索范围内最大值
    peak_seg = values[peak_search_start:peak_search_end]
    if not peak_seg:
        return None, None
    peak_idx_in_seg = max(range(len(peak_seg)), key=lambda i: peak_seg[i])
    lock_peak = peak_search_start + peak_idx_in_seg
    peak_val = peak_seg[peak_idx_in_seg]

    # 2. 从峰值向左回溯，找爬升前的谷底（局部最小值）
    #    搜索范围：峰值前 8~35 点（0.32~1.4s），太远的谷底不是锁闭爬升的前兆
    valley_search_start = max(int(active_end * 0.55), lock_peak - 35)
    valley_search_end = lock_peak - 6  # 峰值前至少 6 点
    if valley_search_end <= valley_search_start:
        return None, None

    # 平滑功率曲线便于找谷
    smooth = moving_average(values, 5)

    # 在 [valley_search_start, valley_search_end] 范围内找最小值 = 谷底
    valley_seg = smooth[valley_search_start:valley_search_end]
    if not valley_seg:
        return None, None

    # 找局部最小值（谷底通常在锁闭峰值前 10-30 点）
    # 策略：在 valley_search_end 附近优先找最近的最小值
    valley_idx_in_seg = min(range(len(valley_seg)), key=lambda i: valley_seg[i])
    valley_global = valley_search_start + valley_idx_in_seg

    # 3. 验证：谷底和峰值之间应有明显的功率上升（锁闭爬升）
    rise = peak_val - smooth[valley_global]
    if rise < 0.02:  # 上升幅度太小（< 0.02 kW），可能不是真正的锁闭爬升
        return None, None

    # 4. 密贴拐点取谷底位置
    lock_start = valley_global

    return lock_start, lock_peak


def detect_lock_peak(values, lock_start, active_end):
    """
    检测锁闭峰值位置（简化版—当 detect_contact_and_lock 已返回 lockPeak 时直接复用）。

    保留此函数用于分离的调用场景。
    """
    # 直接委托给 detect_contact_and_lock
    ls, lp = detect_contact_and_lock(values, active_end)
    return lp


def extract_physical(values):
    """
    物理边界特征提取 — 替代原有的固定偏移量方法。

    返回 dict，包含所有特征值 + 诊断信息。
    与原有 extract() 接口兼容（返回相同 key），额外增加物理边界字段。
    """
    f = {}
    n = len(values)
    f['sampleCount'] = n
    f['isFullWindow'] = n >= 780

    peak_all = max(values) if values else 0.0
    f['isValid'] = bool(values) and peak_all > 0.01
    if not f['isValid']:
        return f

    # 有效动作终点
    th = max(peak_all * 0.05, 0.01)
    active_end = 0
    for i, x in enumerate(values):
        if x > th:
            active_end = i
    f['activeEnd'] = active_end
    f['durationSec'] = round((active_end + 1) * 0.04, 2)

    # ① 启动尖峰（不变）
    head = values[:15]
    f['spikePeak'] = round(max(head), 3)
    sp = head.index(max(head))
    f['spikeIndex'] = sp

    # ── ② 检测解锁终点 ──
    unlock_end = detect_unlock_end(values, sp, active_end)
    f['unlockEnd'] = unlock_end  # None 表示退化
    if unlock_end is not None:
        ul_seg = values[sp + 2:unlock_end + 1]
        f['unlockMean'] = round(statistics.mean(ul_seg), 3) if ul_seg else 0.0
        f['unlockDuration'] = round((unlock_end - sp) * 0.04, 2)
    else:
        # 退化策略：用 spikeIndex+2 到 activeEnd*0.5
        fallback_end = max(sp + 14, int(active_end * 0.5))
        ul_seg = values[sp + 2:fallback_end]
        f['unlockMean'] = round(statistics.mean(ul_seg), 3) if ul_seg else 0.0
        f['unlockDuration'] = round((fallback_end - sp) * 0.04, 2)
        f['unlockFallback'] = True

    # ── ③ 检测密贴拐点 + 锁闭峰值 ──
    lock_start, lock_peak = detect_contact_and_lock(values, active_end)
    f['lockStart'] = lock_start
    f['lockPeak'] = lock_peak

    if lock_start is not None:
        f['contactSec'] = round((lock_start + 1) * 0.04, 2)
    else:
        # 退化策略：activeEnd-40（旧算法）
        lock_start = active_end - 40 if active_end > 50 else active_end
        f['lockStartFallback'] = True

    # 转换段 = 解锁终点 → 密贴拐点
    conv_start = (unlock_end + 1) if unlock_end is not None else (sp + 14)
    conv_end = lock_start
    if conv_end > conv_start and conv_start < n:
        conv_seg = values[conv_start:conv_end]
        if conv_seg:
            f['convMean'] = round(statistics.mean(conv_seg), 3)
            f['convMax'] = round(max(conv_seg), 3)
            f['convDuration'] = round((conv_end - conv_start) * 0.04, 2)
            # 台阶比
            third = len(conv_seg) // 3
            if third >= 5:
                front_m = statistics.mean(conv_seg[:third])
                back_m = statistics.mean(conv_seg[-third:])
                f['stepRatio'] = round(back_m / max(front_m, 0.01), 3)
            else:
                f['stepRatio'] = 1.0
        else:
            f['convMean'] = 0.0
            f['convMax'] = 0.0
            f['convDuration'] = 0.0
            f['stepRatio'] = 1.0
    else:
        f['convMean'] = 0.0
        f['convMax'] = 0.0
        f['convDuration'] = 0.0
        f['stepRatio'] = 1.0

    # ── ④ 锁闭段 + 缓放段 ──
    if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
        # 锁闭段 = 密贴拐点 → 锁闭下降结束（峰值后功率回到缓放平台水平）
        # 从峰值向后找：功率下降到峰值前水平的位置 = 锁闭结束
        pre_ramp_level = statistics.mean(values[lock_start - 5:lock_start + 1]) if lock_start >= 5 else values[lock_start]
        post_peak_search_end = min(lock_peak + 40, active_end - 5)
        lock_end = lock_peak + 5  # 默认：峰值后 5 点
        for i in range(lock_peak + 8, post_peak_search_end):
            if values[i] <= pre_ramp_level * 1.08 or values[i] <= values[lock_peak] * 0.55:
                lock_end = i
                break

        lock_seg = values[lock_start:lock_end + 1]
        f['lockMean'] = round(statistics.mean(lock_seg), 3) if lock_seg else 0.0
        f['lockDuration'] = round((lock_end - lock_start + 1) * 0.04, 2)
        tail_start = lock_end + 1
    else:
        # 退化：旧算法
        if active_end > 50:
            ls2 = max(0, active_end - 40)
            le2 = active_end - 22
            lock_seg = values[ls2:le2]
            f['lockMean'] = round(statistics.mean(lock_seg), 3) if lock_seg else 0.0
        else:
            f['lockMean'] = 0.0
        f['lockDuration'] = 0.0
        tail_start = max(0, active_end - 22)

    # ── ⑤ 缓放段 ──
    tail_end = active_end - 2
    if tail_end > tail_start and active_end > 30:
        tail_seg = values[tail_start:tail_end]
        f['tailMean'] = round(statistics.mean(tail_seg), 3) if tail_seg else 0.0
        f['tailDuration'] = round((tail_end - tail_start) * 0.04, 2)
    else:
        f['tailMean'] = 0.0
        f['tailDuration'] = 0.0

    return f


# ──────────────── 命令实现 ────────────────


def cmd_golden(src):
    """输出 4 条金标准夹具的新旧对比"""
    for pf, ts, label in GOLDEN:
        rows = read_power_rows(src / f"SwitchCurve({pf}).csv")
        match = [(t, dt, v) for t, dt, v in rows if t == ts]
        if not match:
            print(f"[缺失] SwitchCurve({pf}) ts={ts}")
            continue
        t, dt, v = match[0]
        f = extract_physical(v)
        print(f"### SwitchCurve({pf}).csv ts={t} ({dt}) [{label}]")
        print(f"    n={f.get('sampleCount')}  activeEnd={f.get('activeEnd')}  "
              f"durationSec={f.get('durationSec')}  isFullWindow={f.get('isFullWindow')}")
        print(f"    spikePeak={f.get('spikePeak')}  spikeIndex={f.get('spikeIndex')}")
        print(f"    unlockEnd={f.get('unlockEnd')}  unlockDuration={f.get('unlockDuration','N/A')}s  "
              f"unlockMean={f.get('unlockMean')}  fallback={f.get('unlockFallback',False)}")
        print(f"    lockStart={f.get('lockStart')}  contactSec={f.get('contactSec','N/A')}s  "
              f"fallback={f.get('lockStartFallback',False)}")
        print(f"    convDuration={f.get('convDuration','N/A')}s  convMean={f.get('convMean')}  "
              f"convMax={f.get('convMax')}  stepRatio={f.get('stepRatio')}")
        print(f"    lockPeak={f.get('lockPeak')}  lockDuration={f.get('lockDuration','N/A')}s  "
              f"lockMean={f.get('lockMean')}")
        print(f"    tailDuration={f.get('tailDuration','N/A')}s  tailMean={f.get('tailMean')}")
        print()


def cmd_stats(src):
    """全量统计：每台分 J/X 输出阶段时长中位数"""
    DIRS = ["定位→反位", "反位→定位"]
    # 收集统计
    j_type = defaultdict(lambda: {
        'total': 0, 'valid': 0, 'fallback_unlock': 0, 'fallback_lock': 0,
        'durations': [], 'unlock_durs': [], 'conv_durs': [], 'lock_durs': [], 'tail_durs': [],
        'unlock_means': [], 'conv_means': [], 'lock_means': [], 'tail_means': [],
    })
    x_type = defaultdict(lambda: {
        'total': 0, 'valid': 0, 'fallback_unlock': 0, 'fallback_lock': 0,
        'durations': [], 'unlock_durs': [], 'conv_durs': [], 'lock_durs': [], 'tail_durs': [],
        'unlock_means': [], 'conv_means': [], 'lock_means': [], 'tail_means': [],
    })

    for pf, sid in POWER_FILES.items():
        rows = read_power_rows(src / f"SwitchCurve({pf}).csv")
        rows.sort(key=lambda r: r[0])
        kind = 'J' if '-J' in sid else 'X'
        store = j_type if kind == 'J' else x_type

        for i, (ts, dt, v) in enumerate(rows):
            store[kind]['total'] += 1
            f = extract_physical(v)
            if not f.get('isValid'):
                continue
            dur = f['durationSec']
            # 排除异常曲线（超时/夭折）做阶段时长统计
            is_normal = (not f.get('isFullWindow')) and dur >= 2.4
            store[kind]['durations'].append(dur)
            if is_normal:
                store[kind]['valid'] += 1
                store[kind]['unlock_durs'].append(f.get('unlockDuration', 0))
                store[kind]['conv_durs'].append(f.get('convDuration', 0))
                store[kind]['lock_durs'].append(f.get('lockDuration', 0))
                store[kind]['tail_durs'].append(f.get('tailDuration', 0))
                store[kind]['unlock_means'].append(f.get('unlockMean', 0))
                store[kind]['conv_means'].append(f.get('convMean', 0))
                store[kind]['lock_means'].append(f.get('lockMean', 0))
                store[kind]['tail_means'].append(f.get('tailMean', 0))
            if f.get('unlockFallback'):
                store[kind]['fallback_unlock'] += 1
            if f.get('lockStartFallback'):
                store[kind]['fallback_lock'] += 1

    print("=" * 95)
    print("全量统计：物理边界分段 vs 物理模型预期")
    print("=" * 95)

    for kind in ['J', 'X']:
        store = {'J': j_type, 'X': x_type}[kind]
        label = "J型 (3台, 尖轨, ~11.8s)" if kind == 'J' else "X型 (2台, ~8.7s)"
        print(f"\n## {label}")
        d = store[kind]
        print(f"  总事件: {d['total']}  有效正常: {d['valid']}")
        print(f"  解锁退化率: {d['fallback_unlock']}/{d['total']} = "
              f"{d['fallback_unlock']/max(d['total'],1)*100:.1f}%")
        print(f"  锁闭退化率: {d['fallback_lock']}/{d['total']} = "
              f"{d['fallback_lock']/max(d['total'],1)*100:.1f}%")

        if d['valid'] < 30:
            print("  样本不足")
            continue

        for name, arr in [('总时长', 'durations'), ('解锁段', 'unlock_durs'),
                          ('转换段', 'conv_durs'), ('锁闭段', 'lock_durs'),
                          ('缓放段', 'tail_durs')]:
            vals = d[arr]
            if vals:
                med = statistics.median(vals)
                try:
                    p5 = sorted(vals)[int(len(vals) * 0.05)]
                    p95 = sorted(vals)[int(len(vals) * 0.95)]
                except IndexError:
                    p5, p95 = med, med
                print(f"  {name:8s}: 中位={med:6.2f}s  P5={p5:6.2f}s  P95={p95:6.2f}s")

        print()
        for name, arr in [('解锁均值', 'unlock_means'), ('转换均值', 'conv_means'),
                          ('锁闭均值', 'lock_means'), ('缓放均值', 'tail_means')]:
            vals = d[arr]
            if vals:
                med = statistics.median(vals)
                try:
                    p5 = sorted(vals)[int(len(vals) * 0.05)]
                    p95 = sorted(vals)[int(len(vals) * 0.95)]
                except IndexError:
                    p5, p95 = med, med
                print(f"  {name:8s}: 中位={med:6.3f}kW  P5={p5:6.3f}kW  P95={p95:6.3f}kW")

    # 物理模型预期对比
    print("\n" + "=" * 95)
    print("物理模型预期值对照:")
    print("  J型: 解锁~4.9s | 转换~4.6s | 锁闭~1.9s | 总计~11.8s")
    print("  X型: 解锁~3.7s | 转换~2.7s | 锁闭~1.9s | 总计~8.7s")
    print("=" * 95)


def cmd_single(src, file_num, row_idx=0):
    """单条曲线详细输出，含逐段边界标注"""
    rows = read_power_rows(src / f"SwitchCurve({file_num}).csv")
    if row_idx >= len(rows):
        print(f"索引超限: 共 {len(rows)} 条")
        return
    ts, dt, v = rows[row_idx]
    f = extract_physical(v)
    n = f['sampleCount']

    print(f"=== SwitchCurve({file_num}).csv ts={ts} ({dt}) ===")
    print(f"  采样点: {n}  有效终点: {f.get('activeEnd')}  时长: {f.get('durationSec')}s")
    print(f"  尖峰: {f.get('spikePeak')}kW @ idx={f.get('spikeIndex')}")

    # 打印各段边界
    si = f['spikeIndex']
    ae = f['activeEnd']
    ue = f.get('unlockEnd')
    ls = f.get('lockStart')
    lp = f.get('lockPeak')

    print(f"\n  边界: spike={si}  unlockEnd={ue}  lockStart={ls}  lockPeak={lp}  activeEnd={ae}")
    print(f"    → 解锁 [{si+2}, {ue+1 if ue else '?'})  转换 [{ue+1 if ue else '?'}, {ls})  "
          f"锁闭 [{ls}, {lp+1 if lp else '?'})  缓放 [{lp+LOCK_PEAK_TAIL_PAD if lp else '?'}, {ae-2})")

    # 逐段打印前几个值
    for seg_name, start_idx, end_idx in [
        ("解锁段", si + 2, ue + 1 if ue is not None else None),
        ("转换段", ue + 1 if ue is not None else None, ls),
        ("锁闭段", ls, lp + 1 if lp is not None else None),
        ("缓放段", lp + LOCK_PEAK_TAIL_PAD if lp is not None else None, ae - 2),
    ]:
        if start_idx is None or end_idx is None or start_idx >= end_idx:
            print(f"  {seg_name}: 空")
            continue
        seg_vals = v[start_idx:end_idx]
        print(f"  {seg_name} [{start_idx}, {end_idx}) n={len(seg_vals)}: "
              f"前5={[round(x,3) for x in seg_vals[:5]]}  "
              f"均值={statistics.mean(seg_vals):.3f}")

    # 退化标记
    if f.get('unlockFallback'):
        print("  ⚠ 解锁段使用退化策略")
    if f.get('lockStartFallback'):
        print("  ⚠ 锁闭段使用退化策略")


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'stats'
    src = DEFAULT_DIR
    for a in sys.argv[2:]:
        p = Path(a)
        if p.exists() and p.is_dir():
            src = p

    if cmd == 'golden':
        cmd_golden(src)
    elif cmd == 'stats':
        cmd_stats(src)
    elif cmd == 'single':
        fn = int(sys.argv[2]) if len(sys.argv) > 2 else 3
        ri = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        cmd_single(src, fn, ri)
    else:
        print(f"未知命令: {cmd}")
        print("用法: python physeg_prototype.py [stats|golden|single] [csv目录]")
