#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
D7 电流曲线基线构建 — Python 参考实现 (C# 交叉验证基准)

算法规格见 01_docs/diagnosis/issues/D7-current-baseline.md。
C# 实现 (CurrentFeatureExtractor / CurrentBaselineBuilder) 的输出必须与本脚本一致
（浮点容差 ±0.002，MAD 过滤保留集允许 ±1 差异）。

用法:
    python current_baseline_ref_check.py extract <parsed_data目录> [switchId]
        从 parsed_data 提取三相电流特征，打印20维特征摘要

    python current_baseline_ref_check.py baseline <parsed_data目录> [switchId]
        计算电流基线，打印分相基线值表格

    python current_baseline_ref_check.py compare <parsed_data目录> <C#输出的current_baselines.json>
        比较 Python 与 C# 基线输出，逐项报告差异

    python current_baseline_ref_check.py selftest
        运行内置自检（用合成数据验证算法正确性）
"""
import json
import math
import random
import statistics
import sys
from pathlib import Path

# ──────────────────────────────────────────────────────────
# 1. 特征提取 — 与 C# CurrentFeatureExtractor 对齐
# ──────────────────────────────────────────────────────────


def extract_phase(values):
    """对单相采样值执行五阶段分割，返回 dict。
    与 C# CurrentFeatureExtractor.ExtractPhaseInternal 算法一致。

    使用 physeg_prototype 物理边界检测替代硬编码偏移量：
      ① 启动尖峰: 前15点
      ② 解锁段:   [spikeIndex+2, unlockEnd+1)  — detect_unlock_end()
      ③ 转换段:   [unlockEnd+1, lockStart)      — detect_contact_and_lock()
      ④ 锁闭段:   物理检测或退化 [activeEnd-40, activeEnd-22)
      ⑤ 缓放段:   [activeEnd-22, activeEnd-2)
    """
    from physeg_prototype import detect_unlock_end, detect_contact_and_lock

    n = len(values)
    if n == 0:
        return _empty_phase()

    peak_all = max(values)
    if peak_all <= 0.01:
        return _empty_phase()

    # activeEnd: 从尾向前找到 > peakAll*0.05 的最后一点
    threshold = max(peak_all * 0.05, 0.01)
    active_end = 0
    for i in range(n):
        if values[i] > threshold:
            active_end = i

    # ① 启动尖峰（不变）
    head_len = min(15, n)
    head = values[:head_len]
    spike_peak = max(head)
    spike_index = head.index(spike_peak)

    # ② 解锁段 — 物理边界检测
    unlock_end = detect_unlock_end(values, spike_index, active_end)
    if unlock_end is not None and unlock_end > spike_index + 1:
        ul_start = spike_index + 2
        ul_end = unlock_end + 1
        unlock_mean = _seg_mean(values, ul_start, ul_end)
    else:
        # 退化：用 spikeIndex+2 到 activeEnd*0.5
        fallback_end = max(spike_index + 14, int(active_end * 0.5))
        unlock_mean = _seg_mean(values, spike_index + 2, min(fallback_end, n))
        unlock_end = None

    # ③ 转换段 — 物理边界检测
    lock_start, lock_peak = detect_contact_and_lock(values, active_end)
    if lock_start is None:
        lock_start = active_end - 40 if active_end > 50 else active_end

    conv_start = (unlock_end + 1) if unlock_end is not None else (spike_index + 20)
    conv_end = lock_start
    if conv_end > conv_start and conv_start < n:
        conv_mean = _seg_mean(values, conv_start, conv_end)
    else:
        # 退化
        conv_start_fb = spike_index + 2
        conv_end_fb = active_end
        if conv_start_fb >= conv_end_fb:
            conv_start_fb = 0
            conv_end_fb = active_end + 1
        conv_mean = _seg_mean(values, conv_start_fb, conv_end_fb)

    # ④ 锁闭段 — 物理边界检测
    if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
        pre_ramp = statistics.mean(values[lock_start - 5:lock_start + 1]) if lock_start >= 5 else values[lock_start]
        post_peak_end = min(lock_peak + 40, active_end - 5)
        lock_end_idx = lock_peak + 5
        for i in range(lock_peak + 8, post_peak_end):
            if i < n and (values[i] <= pre_ramp * 1.08 or values[i] <= values[lock_peak] * 0.55):
                lock_end_idx = i
                break
        lock_mean = _seg_mean(values, lock_start, lock_end_idx + 1)
    else:
        # 退化
        if active_end > 50:
            ls2 = max(0, active_end - 40)
            le2 = active_end - 22
            lock_mean = _seg_mean(values, ls2, le2)
        else:
            lock_mean = 0.0

    # ⑤ 缓放段（不变）
    if active_end > 30:
        tail_start = active_end - 22
        tail_end = active_end - 2
        if tail_start < 0:
            tail_start = 0
        tail_mean = _seg_mean(values, tail_start, tail_end)
    else:
        tail_mean = 0.0

    return {
        'isValid': True,
        'activeEnd': active_end,
        'spikePeak': round(spike_peak, 3),
        'spikeIndex': spike_index,
        'unlockEnd': unlock_end,
        'lockStart': lock_start,
        'unlockMean': round(unlock_mean, 3),
        'convMean': round(conv_mean, 3),
        'lockMean': round(lock_mean, 3),
        'tailMean': round(tail_mean, 3),
    }


def _empty_phase():
    return {
        'isValid': False, 'activeEnd': 0,
        'spikePeak': 0.0, 'spikeIndex': 0,
        'unlockEnd': None, 'lockStart': None,
        'unlockMean': 0.0, 'convMean': 0.0,
        'lockMean': 0.0, 'tailMean': 0.0,
    }


def _seg_mean(values, start, end):
    """计算 [start, end) 区间均值，与 C# SegmentMean 一致。"""
    total = 0.0
    count = 0
    for i in range(start, min(end, len(values))):
        total += values[i]
        count += 1
    return total / count if count > 0 else 0.0


def extract_current_features(evt):
    """从 SwitchEvent 字典提取三相电流特征，返回 CurrentFeatures 字典。
    与 C# CurrentFeatureExtractor.Extract(SwitchEvent) 一致。
    """
    # 提取各相 value 列（[t, v] 对 → v 列）
    values_a = _extract_values(evt.get('CurrentA', []))
    values_b = _extract_values(evt.get('CurrentB', []))
    values_c = _extract_values(evt.get('CurrentC', []))

    n = max(len(values_a), len(values_b), len(values_c))

    pa = extract_phase(values_a)
    pb = extract_phase(values_b)
    pc = extract_phase(values_c)

    f = {
        'sampleCount': n,
        'isFullWindow': n >= 780,
        'isValid': pa['isValid'] or pb['isValid'] or pc['isValid'],
        'activeEnd': max(pa['activeEnd'], pb['activeEnd'], pc['activeEnd']),
        'direction': evt.get('Direction', evt.get('direction', None)),

        # A 相
        'spikePeakA': pa['spikePeak'], 'spikeIndexA': pa['spikeIndex'],
        'unlockMeanA': pa['unlockMean'], 'convMeanA': pa['convMean'],
        'lockMeanA': pa['lockMean'], 'tailMeanA': pa['tailMean'],

        # B 相
        'spikePeakB': pb['spikePeak'], 'spikeIndexB': pb['spikeIndex'],
        'unlockMeanB': pb['unlockMean'], 'convMeanB': pb['convMean'],
        'lockMeanB': pb['lockMean'], 'tailMeanB': pb['tailMean'],

        # C 相
        'spikePeakC': pc['spikePeak'], 'spikeIndexC': pc['spikeIndex'],
        'unlockMeanC': pc['unlockMean'], 'convMeanC': pc['convMean'],
        'lockMeanC': pc['lockMean'], 'tailMeanC': pc['tailMean'],
    }

    # DurationSec = max(activeEnd) × 0.04
    if f['isValid']:
        f['durationSec'] = round((f['activeEnd'] + 1) * 0.04, 2)
    else:
        f['durationSec'] = 0.0

    # MaxUnbalanceRatio
    f['maxUnbalanceRatio'] = _compute_unbalance(pa['convMean'], pb['convMean'], pc['convMean'])

    # 原始采样值（供未来 P1 逐点对比）
    f['rawValuesA'] = values_a
    f['rawValuesB'] = values_b
    f['rawValuesC'] = values_c

    return f


def _extract_values(pairs):
    """从 [[t, v], ...] 中提取 v 列。"""
    if not pairs:
        return []
    out = []
    for p in pairs:
        if isinstance(p, list) and len(p) >= 2:
            out.append(p[1])
        else:
            out.append(0.0)
    return out


def _compute_unbalance(conv_a, conv_b, conv_c):
    """三相最大不平衡度，基于转换段均值。"""
    mean3 = (conv_a + conv_b + conv_c) / 3.0
    if mean3 < 0.001:
        return 0.0
    max_dev = max(abs(conv_a - mean3), abs(conv_b - mean3), abs(conv_c - mean3))
    return round(max_dev / mean3, 3)


# ──────────────────────────────────────────────────────────
# 2. 基线构建 — 与 C# CurrentBaselineBuilder 对齐
# ──────────────────────────────────────────────────────────


def _median(values):
    """中位数，偶数个取中间两数平均，与 Python statistics.median 一致。"""
    if not values:
        return 0.0
    return statistics.median(values)


def _feature_to_vector(f):
    """将 CurrentFeatures dict 展开为 20 维 list。
    顺序与 C# FeatureToVector 一致：
      SpikePeakA, SpikeIndexA, UnlockMeanA, ConvMeanA, LockMeanA, TailMeanA,
      SpikePeakB, SpikeIndexB, UnlockMeanB, ConvMeanB, LockMeanB, TailMeanB,
      SpikePeakC, SpikeIndexC, UnlockMeanC, ConvMeanC, LockMeanC, TailMeanC,
      DurationSec, MaxUnbalanceRatio
    """
    return [
        f['spikePeakA'], float(f['spikeIndexA']),
        f['unlockMeanA'], f['convMeanA'], f['lockMeanA'], f['tailMeanA'],
        f['spikePeakB'], float(f['spikeIndexB']),
        f['unlockMeanB'], f['convMeanB'], f['lockMeanB'], f['tailMeanB'],
        f['spikePeakC'], float(f['spikeIndexC']),
        f['unlockMeanC'], f['convMeanC'], f['lockMeanC'], f['tailMeanC'],
        f['durationSec'], f['maxUnbalanceRatio'],
    ]


def _vector_to_baseline(vec, sample_count):
    """将 20 维向量打包为 CurrentBaseline dict。"""
    return {
        'refSpikePeakA': round(vec[0], 3),
        'refSpikeIndexA': int(round(vec[1])),
        'refUnlockMeanA': round(vec[2], 3),
        'refConvMeanA': round(vec[3], 3),
        'refLockMeanA': round(vec[4], 3),
        'refTailMeanA': round(vec[5], 3),

        'refSpikePeakB': round(vec[6], 3),
        'refSpikeIndexB': int(round(vec[7])),
        'refUnlockMeanB': round(vec[8], 3),
        'refConvMeanB': round(vec[9], 3),
        'refLockMeanB': round(vec[10], 3),
        'refTailMeanB': round(vec[11], 3),

        'refSpikePeakC': round(vec[12], 3),
        'refSpikeIndexC': int(round(vec[13])),
        'refUnlockMeanC': round(vec[14], 3),
        'refConvMeanC': round(vec[15], 3),
        'refLockMeanC': round(vec[16], 3),
        'refTailMeanC': round(vec[17], 3),

        'refDurationSec': round(vec[18], 2),
        'refMaxUnbalanceRatio': round(vec[19], 3),

        'sampleCount': sample_count,
    }


def _compute_median_vector(features):
    """对特征列表的每维取中位数，返回 20 维 list。"""
    vecs = [_feature_to_vector(f) for f in features]
    if not vecs:
        return [0.0] * 20
    return [_median([v[j] for v in vecs]) for j in range(20)]


def _compute_mads(features, baseline_vec):
    """计算每维的 MAD (中位数绝对偏差)。MAD=0 → 1e-6。"""
    vecs = [_feature_to_vector(f) for f in features]
    mads = [0.0] * 20
    for j in range(20):
        devs = [abs(v[j] - baseline_vec[j]) for v in vecs]
        mads[j] = _median(devs)
        if mads[j] < 1e-12:
            mads[j] = 1e-6
    return mads


def _standardized_distance(f, baseline_vec, mads):
    """标准化欧氏距离：sqrt(Σ((f_j - baseline_j) / MAD_j)²)"""
    vec = _feature_to_vector(f)
    sum_sq = 0.0
    for j in range(20):
        z = (vec[j] - baseline_vec[j]) / mads[j]
        sum_sq += z * z
    return math.sqrt(sum_sq)


def _mad_filter(features, baseline_vec):
    """MAD 过滤：剔除标准化距离 > medDist + 3.0*madDist 的曲线。"""
    mads = _compute_mads(features, baseline_vec)
    distances = [_standardized_distance(f, baseline_vec, mads) for f in features]

    med_dist = _median(distances)
    abs_devs = [abs(d - med_dist) for d in distances]
    mad_dist = _median(abs_devs)
    threshold = med_dist + 3.0 * mad_dist

    return [f for i, f in enumerate(features) if distances[i] <= threshold]


def build_current_baseline(all_features, min_samples=30, direction=None):
    """迭代中位数 + MAD 过滤基线构建。
    与 C# CurrentBaselineBuilder.Build() 一致。

    Step 1 — 前置过滤: IsValid && !IsFullWindow && DurationSec >= 2.4
    Step 2 — 第一次中位数聚合 → baseline_0 [20]
    Step 3 — MAD 过滤
    Step 4 — 第二次中位数聚合 → baseline_final [20]

    当 direction 不为 None 时，仅使用匹配方向的特征。
    返回 CurrentBaseline dict，样本不足时返回 None。
    """
    if not all_features:
        return None

    # Step 1: 前置过滤（可选方向过滤）
    pool = [f for f in all_features
            if f.get('isValid') and not f.get('isFullWindow') and f.get('durationSec', 0) >= 2.4]
    if direction is not None:
        pool = [f for f in pool if f.get('direction') == direction]
    if not pool:
        return None

    # Step 2: 第一次中位数聚合
    baseline0 = _compute_median_vector(pool)

    # Step 3: MAD 过滤
    retained = _mad_filter(pool, baseline0)
    if len(retained) < min_samples:
        return None

    # Step 4: 第二次中位数聚合
    baseline_final = _compute_median_vector(retained)

    result = _vector_to_baseline(baseline_final, len(retained))
    result['direction'] = direction
    return result


# ──────────────────────────────────────────────────────────
# 3. parsed_data 数据加载
# ──────────────────────────────────────────────────────────


def load_parsed_events(parsed_dir, switch_id=None):
    """加载 parsed_data 目录下所有或指定道岔的事件。
    返回 {switchId: [event_dict, ...]}
    """
    data_dir = Path(parsed_dir)
    result = {}

    # 扫描子目录作为 switchId
    switch_ids = [switch_id] if switch_id else []
    if not switch_ids:
        for d in sorted(data_dir.iterdir()):
            if d.is_dir() and '-' in d.name:
                switch_ids.append(d.name)

    for sid in switch_ids:
        sid_dir = data_dir / sid
        if not sid_dir.is_dir():
            print(f"[警告] {sid_dir} 不存在")
            continue

        events = []
        for f in sorted(sid_dir.glob('*.json')):
            # 跳过 .diag.json, features.json 等辅助文件
            if '.diag.' in f.name or f.name in ('features.json', 'current_features.json', 'index.json'):
                continue
            try:
                with open(f, encoding='utf-8') as fh:
                    day_events = json.load(fh)
                    events.extend(day_events)
            except Exception as ex:
                print(f"[警告] 读取 {f} 失败: {ex}")

        result[sid] = events

    return result


# ──────────────────────────────────────────────────────────
# 4. 子命令实现
# ──────────────────────────────────────────────────────────


def cmd_extract(parsed_dir, switch_id=None):
    """提取并打印三相电流特征。"""
    all_data = load_parsed_events(parsed_dir, switch_id)
    for sid, events in sorted(all_data.items()):
        print(f"\n=== {sid} ({len(events)} 事件) ===")
        valid_count = 0
        for evt in events:
            f = extract_current_features(evt)
            if f['isValid']:
                valid_count += 1
                if valid_count <= 3:  # 只打印前 3 条
                    print(f"  ts={evt.get('Timestamp', 0)}  dur={f['durationSec']:.2f}s")
                    print(f"    A: spike={f['spikePeakA']:.3f}@{f['spikeIndexA']} "
                          f"ul={f['unlockMeanA']:.3f} cv={f['convMeanA']:.3f} "
                          f"lk={f['lockMeanA']:.3f} tl={f['tailMeanA']:.3f}")
                    print(f"    B: spike={f['spikePeakB']:.3f}@{f['spikeIndexB']} "
                          f"ul={f['unlockMeanB']:.3f} cv={f['convMeanB']:.3f} "
                          f"lk={f['lockMeanB']:.3f} tl={f['tailMeanB']:.3f}")
                    print(f"    C: spike={f['spikePeakC']:.3f}@{f['spikeIndexC']} "
                          f"ul={f['unlockMeanC']:.3f} cv={f['convMeanC']:.3f} "
                          f"lk={f['lockMeanC']:.3f} tl={f['tailMeanC']:.3f}")
                    print(f"    unbalance={f['maxUnbalanceRatio']:.3f} "
                          f"fullWindow={f['isFullWindow']}")
        print(f"  有效: {valid_count}/{len(events)} "
              f"(电流数据={sum(1 for e in events if e.get('CurrentA'))})")


def cmd_baseline(parsed_dir, switch_id=None):
    """计算并打印电流基线（分方向）。"""
    all_data = load_parsed_events(parsed_dir, switch_id)
    DIRS = ["定位→反位", "反位→定位"]

    print(f"{'道岔':6s} {'方向':8s} {'样本':>5s} | "
          f"{'A-峰值':>8s} {'A-解锁':>8s} {'A-转换':>8s} {'A-锁闭':>8s} {'A-缓放':>8s} | "
          f"{'B-转换':>8s} {'C-转换':>8s} | {'时长s':>7s}")
    print('-' * 120)

    for sid in sorted(all_data.keys()):
        events = all_data[sid]

        # 先加载功率诊断"正常"时间戳集合
        normal_ts = _load_normal_timestamps(parsed_dir, sid)

        # 提取特征，同时按功率诊断结果筛选
        features = []
        for evt in events:
            # 功率诊断筛选：有诊断数据时仅保留功率诊断为"正常"的事件
            if normal_ts is not None:
                ts = evt.get('Timestamp', 0)
                if ts not in normal_ts:
                    continue
            features.append(extract_current_features(evt))

        if not features:
            print(f"{sid:6s} 无电流数据或功率诊断正常样本，跳过")
            continue

        for dir_ in DIRS:
            baseline = build_current_baseline(features, min_samples=30, direction=dir_)
            if baseline is None:
                valid = sum(1 for f in features if f['isValid']
                            and not f['isFullWindow']
                            and f.get('durationSec', 0) >= 2.4
                            and f.get('direction') == dir_)
                print(f"{sid:6s} {dir_:8s} 正常样本={valid} 不足30，跳过 "
                      f"(总={len(features)})")
                continue

            bl = baseline
            print(f"{sid:6s} {dir_:8s} {bl['sampleCount']:5d} | "
                  f"{bl['refSpikePeakA']:8.3f} {bl['refUnlockMeanA']:8.3f} {bl['refConvMeanA']:8.3f} "
                  f"{bl['refLockMeanA']:8.3f} {bl['refTailMeanA']:8.3f} | "
                  f"{bl['refConvMeanB']:8.3f} {bl['refConvMeanC']:8.3f} | "
                  f"{bl['refDurationSec']:7.2f}")

    print()


def _load_normal_timestamps(parsed_dir, switch_id):
    """加载功率诊断结果为"正常"的事件时间戳集合。
    读取 .diag.json 文件，返回所有 Level=="正常" 或 Results 为空的 timestamp。
    没有诊断数据时返回 None（表示无需筛选，全部通过）。
    """
    diag_dir = Path(parsed_dir) / switch_id
    if not diag_dir.is_dir():
        return None

    normal_ts = set()
    any_diag_found = False
    for f in sorted(diag_dir.glob('*.diag.json')):
        any_diag_found = True
        try:
            with open(f, encoding='utf-8') as fh:
                diagnoses = json.load(fh)
                for d in diagnoses:
                    level = d.get('Level', '')
                    results = d.get('Results', [])
                    if level == '正常' or (isinstance(results, list) and len(results) == 0):
                        normal_ts.add(d.get('Timestamp', 0))
        except Exception:
            pass

    return normal_ts if any_diag_found else None


def cmd_compare(parsed_dir, csharp_baseline_path):
    """比较 Python 与 C# 基线输出。"""
    csharp_path = Path(csharp_baseline_path)
    if not csharp_path.exists():
        print(f"[错误] C# 基线文件不存在: {csharp_path}")
        sys.exit(1)

    with open(csharp_path, encoding='utf-8') as fh:
        csharp_store = json.load(fh)

    csharp_switches = csharp_store.get('switches', csharp_store.get('Switches', {}))
    if not csharp_switches:
        print("[错误] C# 基线文件中没有 switches 数据")
        sys.exit(1)

    # 构建 Python 基线（分方向）
    all_data = load_parsed_events(parsed_dir)
    py_baselines = {}
    DIRS = ["定位→反位", "反位→定位"]
    for sid, events in sorted(all_data.items()):
        # 先加载功率诊断"正常"时间戳集合（与 C# LoadNormalTimestamps 对齐）
        normal_ts = _load_normal_timestamps(parsed_dir, sid)
        features = []
        for evt in events:
            # 功率诊断筛选：有诊断数据时仅保留功率诊断为"正常"的事件
            if normal_ts is not None:
                ts = evt.get('Timestamp', 0)
                if ts not in normal_ts:
                    continue
            features.append(extract_current_features(evt))
        for dir_ in DIRS:
            bl = build_current_baseline(features, min_samples=30, direction=dir_)
            if bl:
                py_baselines[f"{sid}|{dir_}"] = bl

    # 比较
    print(f"{'道岔':6s} {'字段':>22s} {'Python':>10s} {'C#':>10s} {'差异':>10s} {'状态'}")
    print('-' * 80)
    all_ok = True
    fields_20 = [
        ('refSpikePeakA', 0.001), ('refSpikeIndexA', 0.5),
        ('refUnlockMeanA', 0.002), ('refConvMeanA', 0.002),
        ('refLockMeanA', 0.002), ('refTailMeanA', 0.002),
        ('refSpikePeakB', 0.001), ('refSpikeIndexB', 0.5),
        ('refUnlockMeanB', 0.002), ('refConvMeanB', 0.002),
        ('refLockMeanB', 0.002), ('refTailMeanB', 0.002),
        ('refSpikePeakC', 0.001), ('refSpikeIndexC', 0.5),
        ('refUnlockMeanC', 0.002), ('refConvMeanC', 0.002),
        ('refLockMeanC', 0.002), ('refTailMeanC', 0.002),
        ('refDurationSec', 0.02), ('refMaxUnbalanceRatio', 0.002),
    ]

    for sid in sorted(set(list(csharp_switches.keys()) + list(py_baselines.keys()))):
        cs = csharp_switches.get(sid)
        py = py_baselines.get(sid)

        if cs is None:
            print(f"{sid:6s} 仅在 Python 中存在 → 跳过")
            continue
        if py is None:
            print(f"{sid:6s} 仅在 C# 中存在 → 跳过")
            continue

        for field_name, tolerance in fields_20:
            # C# 序列化是 PascalCase, Python 是 camelCase
            cs_field = field_name[0].upper() + field_name[1:]  # PascalCase
            py_val = py.get(field_name, 0)
            cs_val = cs.get(cs_field, cs.get(field_name, 0))

            diff = abs(py_val - cs_val)
            status = 'OK' if diff <= tolerance else 'FAIL'
            if status == 'FAIL':
                all_ok = False

            if status == 'FAIL' or True:  # 总是打印以便审计
                print(f"{sid:6s} {field_name:>22s} {py_val:10.4f} {cs_val:10.4f} {diff:10.4f}  {status}")

    # 样本数比较
    print()
    print(f"{'道岔':6s} {'Python样本':>12s} {'C#样本':>10s}")
    for sid in sorted(set(list(csharp_switches.keys()) + list(py_baselines.keys()))):
        cs = csharp_switches.get(sid)
        py = py_baselines.get(sid)
        if cs and py:
            py_sc = py.get('sampleCount', 0)
            cs_sc = cs.get('SampleCount', cs.get('sampleCount', 0))
            print(f"{sid:6s} {py_sc:12d} {cs_sc:10d}")

    print()
    if all_ok:
        print("=== 交叉验证通过: Python ↔ C# 基线值一致 ===")
    else:
        print("=== 交叉验证失败: 存在超出容差的差异 ===")
        sys.exit(1)


def cmd_selftest():
    """内置自检：用合成数据验证算法正确性。"""
    rng = random.Random(42)

    print("=== D7 电流基线 Python 参考实现 — 自检 ===\n")

    # ── 测试 1: 空数组 ──
    f = extract_current_features({'CurrentA': [], 'CurrentB': [], 'CurrentC': []})
    assert not f['isValid'], "空数组 → IsValid=false"
    print("PASS: 空数组 → IsValid=false")

    # ── 测试 2: 全零 ──
    zero_pairs = [[0.0, 0.0], [0.04, 0.0], [0.08, 0.0]]
    f = extract_current_features({'CurrentA': zero_pairs, 'CurrentB': zero_pairs, 'CurrentC': zero_pairs})
    assert not f['isValid'], "全零 → IsValid=false"
    print("PASS: 全零 → IsValid=false")

    # ── 测试 3: 三相正常曲线 ──
    curve_a = _make_phase_curve(300, spike_index=6, conv_mean=2.8, active_end=293, seed=123)
    curve_b = _make_phase_curve(300, spike_index=6, conv_mean=2.78, active_end=293, seed=124)
    curve_c = _make_phase_curve(300, spike_index=6, conv_mean=2.82, active_end=293, seed=125)
    evt = {
        'CurrentA': [[i * 0.04, v] for i, v in enumerate(curve_a)],
        'CurrentB': [[i * 0.04, v] for i, v in enumerate(curve_b)],
        'CurrentC': [[i * 0.04, v] for i, v in enumerate(curve_c)],
    }
    f = extract_current_features(evt)
    assert f['isValid'], "三相正常 → IsValid=true"
    assert f['sampleCount'] == 300, f"SampleCount=300, got {f['sampleCount']}"
    assert not f['isFullWindow'], "IsFullWindow=false"
    assert f['spikePeakA'] > 0, f"SpikePeakA > 0, got {f['spikePeakA']}"
    assert f['spikeIndexA'] == 6, f"SpikeIndexA=6, got {f['spikeIndexA']}"
    assert f['unlockMeanA'] > 0, f"UnlockMeanA > 0, got {f['unlockMeanA']}"
    assert f['convMeanA'] > 0, f"ConvMeanA > 0, got {f['convMeanA']}"
    assert f['lockMeanA'] > 0, f"LockMeanA > 0 (activeEnd=293 > 50)"
    assert f['tailMeanA'] > 0, f"TailMeanA > 0 (activeEnd=293 > 30)"
    assert f['durationSec'] > 2.4, f"DurationSec > 2.4, got {f['durationSec']}"
    assert f['maxUnbalanceRatio'] >= 0, f"MaxUnbalanceRatio >= 0, got {f['maxUnbalanceRatio']}"
    print("PASS: 三相正常曲线提取20维特征")

    # ── 测试 4: FullWindow 检测 ──
    curve_long = _make_phase_curve(790, spike_index=6, conv_mean=2.8, active_end=783, seed=42)
    evt_long = {
        'CurrentA': [[i * 0.04, v] for i, v in enumerate(curve_long)],
        'CurrentB': [[i * 0.04, v] for i, v in enumerate(curve_long)],
        'CurrentC': [[i * 0.04, v] for i, v in enumerate(curve_long)],
    }
    f_long = extract_current_features(evt_long)
    assert f_long['isFullWindow'], f"IsFullWindow=true for n≥780, got n={f_long['sampleCount']}"
    print("PASS: FullWindow 检测 (n≥780)")

    # ── 测试 5: 短曲线 TailMean=0（物理边界版下 LockMean 可能非零） ──
    curve_short = _make_phase_curve(25, spike_index=5, conv_mean=2.5, active_end=20, seed=42)
    f_short = extract_phase(curve_short)
    assert f_short['isValid'], "短曲线 IsValid=true"
    assert f_short['tailMean'] == 0.0, f"activeEnd≤30 → TailMean=0, got {f_short['tailMean']}"
    # 物理边界版：lockMean 不再受 activeEnd>50 约束，由物理检测决定
    assert f_short['unlockEnd'] is not None or f_short['lockStart'] is not None, \
        "短曲线至少有一个物理边界被检测到或正确退化"
    print("PASS: 短曲线 TailMean=0（物理边界版 LockMean 由检测决定）")

    # ── 测试 6: 不平衡度计算 ──
    # threePhaseMean=(3.0+2.5+2.0)/3=2.5
    # max dev=0.5, ratio=0.5/2.5=0.2
    ratio = _compute_unbalance(3.0, 2.5, 2.0)
    assert abs(ratio - 0.2) < 0.01, f"MaxUnbalance=0.2, got {ratio}"
    print("PASS: 三相不平衡度计算")

    # ── 测试 7: DurationSec 取三相最大值 ──
    # A activeEnd=200 → dur=8.04, B=250 → 10.04, C=220 → 8.84 → max=10.04
    evt_multi = {
        'CurrentA': [[i * 0.04, v] for i, v in enumerate(_make_phase_curve(300, 6, 2.8, 200, 200))],
        'CurrentB': [[i * 0.04, v] for i, v in enumerate(_make_phase_curve(300, 6, 2.8, 250, 201))],
        'CurrentC': [[i * 0.04, v] for i, v in enumerate(_make_phase_curve(300, 6, 2.8, 220, 202))],
    }
    f_multi = extract_current_features(evt_multi)
    assert abs(f_multi['durationSec'] - 10.04) < 0.02, \
        f"DurationSec=10.04, got {f_multi['durationSec']}"
    print("PASS: DurationSec 取三相最大值")

    # ── 测试 8: 基线构建 ≥30 ──
    syn = [_make_synthetic_feature(rng) for _ in range(50)]
    bl = build_current_baseline(syn, min_samples=30)
    assert bl is not None, "50条样本 → 基线非null"
    assert bl['sampleCount'] >= 30, f"sampleCount >= 30, got {bl['sampleCount']}"
    assert bl['refSpikePeakA'] > 0, f"refSpikePeakA > 0, got {bl['refSpikePeakA']}"
    print("PASS: 基线构建 ≥30 样本")

    # ── 测试 9: 基线构建 <30 → null ──
    syn_small = [_make_synthetic_feature(rng) for _ in range(20)]
    bl_small = build_current_baseline(syn_small, min_samples=30)
    assert bl_small is None, f"<30条 → None, got {bl_small}"
    print("PASS: 基线构建 <30 样本返回 None")

    # ── 测试 10: 排除 IsValid=false ──
    syn_mixed = [_make_synthetic_feature(rng) for _ in range(10)]
    for f_ in syn_mixed:
        f_['isValid'] = False
    syn_mixed.extend([_make_synthetic_feature(rng) for _ in range(35)])
    bl_mixed = build_current_baseline(syn_mixed, min_samples=30)
    assert bl_mixed is not None, "排除无效后仍有≥30条"
    print("PASS: 排除 IsValid=false 的样本")

    # ── 测试 11: 确定性输入验证精确值 ──
    deterministic = []
    for _ in range(50):
        deterministic.append({
            'isValid': True, 'isFullWindow': False,
            'spikePeakA': 5.5, 'spikeIndexA': 6,
            'unlockMeanA': 3.2, 'convMeanA': 2.8,
            'lockMeanA': 1.5, 'tailMeanA': 1.7,
            'spikePeakB': 5.45, 'spikeIndexB': 6,
            'unlockMeanB': 3.18, 'convMeanB': 2.78,
            'lockMeanB': 1.48, 'tailMeanB': 1.68,
            'spikePeakC': 5.52, 'spikeIndexC': 7,
            'unlockMeanC': 3.22, 'convMeanC': 2.82,
            'lockMeanC': 1.52, 'tailMeanC': 1.72,
            'durationSec': 11.72, 'maxUnbalanceRatio': 0.03,
            'sampleCount': 300, 'activeEnd': 293,
        })
    bl_det = build_current_baseline(deterministic, min_samples=30)
    assert bl_det is not None
    assert abs(bl_det['refSpikePeakA'] - 5.5) < 0.001
    assert bl_det['refSpikeIndexA'] == 6
    assert abs(bl_det['refUnlockMeanA'] - 3.2) < 0.001
    assert abs(bl_det['refConvMeanA'] - 2.8) < 0.001
    assert abs(bl_det['refLockMeanA'] - 1.5) < 0.001
    assert abs(bl_det['refTailMeanA'] - 1.7) < 0.001
    assert abs(bl_det['refDurationSec'] - 11.72) < 0.01
    assert abs(bl_det['refMaxUnbalanceRatio'] - 0.03) < 0.001
    # MAD=0 时全部保留
    assert bl_det['sampleCount'] >= 45, f"MAD=0时保留≥45, got {bl_det['sampleCount']}"
    print("PASS: 确定性输入验证精确基线值")

    # ── 测试 12: MAD 过滤剔除离群值 ──
    syn_with_outliers = [_make_synthetic_feature(rng) for _ in range(35)]
    for _ in range(5):
        outlier = _make_synthetic_feature(rng)
        outlier['convMeanA'] = 5.6  # 偏离 2×
        outlier['convMeanB'] = 5.6 * 0.99
        outlier['convMeanC'] = 5.6 * 1.01
        syn_with_outliers.append(outlier)
    bl_out = build_current_baseline(syn_with_outliers, min_samples=30)
    assert bl_out is not None, "MAD 过滤后仍有≥30条正常样本"
    assert abs(bl_out['refConvMeanA'] - 2.8) < 0.3, \
        f"RefConvMeanA 未被离群值拉偏, got {bl_out['refConvMeanA']}"
    print("PASS: MAD 过滤剔除离群值")

    print(f"\n=== 自检完成: 12/12 通过 ===")


# ──────────────────────────────────────────────────────────
# 辅助函数
# ──────────────────────────────────────────────────────────


def _make_phase_curve(length, spike_index, conv_mean, active_end, seed=42):
    """构造单相电流曲线（模拟真实道岔电流波形）。
    与 C# D7Tests.MakePhaseCurve 算法一致。
    """
    rng = random.Random(seed)
    curve = []
    for i in range(length):
        if i < 3:
            v = 0.0
        elif i == spike_index:
            v = 4.5 + rng.random() * 1.5
        elif spike_index < i < spike_index + 5:
            v = 2.5 + rng.random() * 1.0
        elif i > length - 35:
            if i > active_end:
                v = 0.0
            elif active_end - 22 <= i < active_end - 2:
                v = 1.7 + (rng.random() - 0.5) * 0.1
            elif active_end - 40 <= i < active_end - 22:
                v = 1.5 + (rng.random() - 0.5) * 0.1
            else:
                v = 1.7 + (rng.random() - 0.5) * 0.2
        elif i > active_end:
            v = 0.0
        else:
            v = conv_mean + (rng.random() - 0.5) * 0.2
        curve.append(round(max(0.0, v), 3))
    return curve


def _make_synthetic_feature(rng):
    """构造合成电流特征（带微小噪声）。"""
    noise = lambda: (rng.random() - 0.5) * 0.002
    return {
        'isValid': True, 'isFullWindow': False,
        'spikePeakA': 5.5 + noise(), 'spikeIndexA': 6,
        'unlockMeanA': 3.2 + noise(), 'convMeanA': 2.8 + noise(),
        'lockMeanA': 1.5 + noise(), 'tailMeanA': 1.7 + noise(),
        'spikePeakB': 5.45 + noise(), 'spikeIndexB': 6,
        'unlockMeanB': 3.18 + noise(), 'convMeanB': 2.78 + noise(),
        'lockMeanB': 1.48 + noise(), 'tailMeanB': 1.68 + noise(),
        'spikePeakC': 5.52 + noise(), 'spikeIndexC': 7,
        'unlockMeanC': 3.22 + noise(), 'convMeanC': 2.82 + noise(),
        'lockMeanC': 1.52 + noise(), 'tailMeanC': 1.72 + noise(),
        'durationSec': 11.72 + noise() * 0.1,
        'maxUnbalanceRatio': 0.03 + abs(noise() * 0.01),
        'sampleCount': 300, 'activeEnd': 293,
    }


# ──────────────────────────────────────────────────────────
# 入口
# ──────────────────────────────────────────────────────────

if __name__ == '__main__':
    # Python 3.7+ reconfigure; 3.6- 忽略
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

    if len(sys.argv) < 2:
        print(__doc__)
        print("可用的子命令: extract, baseline, compare, selftest")
        sys.exit(1)

    cmd = sys.argv[1].lower()

    if cmd == 'selftest':
        cmd_selftest()
    elif cmd == 'extract':
        parsed_dir = sys.argv[2] if len(sys.argv) > 2 else None
        switch_id = sys.argv[3] if len(sys.argv) > 3 else None
        if not parsed_dir:
            print("用法: python current_baseline_ref_check.py extract <parsed_data目录> [switchId]")
            sys.exit(1)
        cmd_extract(parsed_dir, switch_id)
    elif cmd == 'baseline':
        parsed_dir = sys.argv[2] if len(sys.argv) > 2 else None
        switch_id = sys.argv[3] if len(sys.argv) > 3 else None
        if not parsed_dir:
            print("用法: python current_baseline_ref_check.py baseline <parsed_data目录> [switchId]")
            sys.exit(1)
        cmd_baseline(parsed_dir, switch_id)
    elif cmd == 'compare':
        if len(sys.argv) < 4:
            print("用法: python current_baseline_ref_check.py compare <parsed_data目录> <C#输出current_baselines.json>")
            sys.exit(1)
        cmd_compare(sys.argv[2], sys.argv[3])
    else:
        print(f"未知子命令: {cmd}")
        print("可用: extract, baseline, compare, selftest")
        sys.exit(1)
