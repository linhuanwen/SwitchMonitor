#!/usr/bin/env python3
"""
从 parsed_data 按方向重建 baselines.json。
输出格式: key = "switchId|direction"，与 BaselineStore.MakeKey 一致。
"""
import json
import os
import sys
import math
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "02_source" / "tools"))
from physeg_prototype import detect_unlock_end, detect_contact_and_lock

if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

PROD_DIR = r'd:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data'
PARSED_DIR = os.path.join(PROD_DIR, 'parsed_data')
RULES_DIR = os.path.join(PROD_DIR, 'Rules')

DIRECTIONS = ['定位→反位', '反位→定位']


def median(values):
    n = len(values)
    if n == 0:
        return 0.0
    s = sorted(values)
    if n % 2 == 1:
        return s[n // 2]
    return (s[n // 2 - 1] + s[n // 2]) / 2.0


def load_parsed_features(switch_id):
    """加载某道岔所有 parsed_data 中的特征，返回按方向分组的列表"""
    sw_dir = os.path.join(PARSED_DIR, switch_id)
    if not os.path.isdir(sw_dir):
        return {}

    by_direction = defaultdict(list)
    for fname in sorted(os.listdir(sw_dir)):
        # 只加载每日事件文件（YYYY-MM-DD.json），跳过 features/current_features/diag 等
        if not fname.endswith('.json') or '.diag' in fname or 'features' in fname.lower():
            continue
        fpath = os.path.join(sw_dir, fname)
        try:
            with open(fpath, 'r', encoding='utf-8') as f:
                events = json.load(f)
        except Exception:
            continue

        for evt in events:
            if not isinstance(evt, dict):
                continue
            pw = evt.get('Power', [])
            if not pw:
                continue
            values = [p[1] for p in pw if len(p) >= 2]
            if not values:
                continue

            direction = evt.get('Direction', '')
            if direction not in DIRECTIONS:
                continue

            # 提取特征
            feat = extract_features(values)
            if not feat['IsValid'] or feat['IsFullWindow'] or feat['DurationSec'] < 2.4:
                continue

            by_direction[direction].append(feat)

    return by_direction


def extract_features(values):
    """提取五段特征（与 C# FeatureExtractor.Extract 对齐）— 物理边界分段版"""
    import statistics as _st
    n = len(values)
    if n < 10:
        return {'IsValid': False, 'IsFullWindow': False, 'DurationSec': 0,
                'SpikePeak': 0, 'SpikeIndex': 0, 'ActiveEnd': 0,
                'UnlockEnd': None, 'LockStart': None,
                'UnlockMean': 0, 'ConvMean': 0, 'LockMean': 0, 'TailMean': 0}

    # spikeIndex: 前15点内最大值下标（不变）
    head_len = min(15, n)
    spike_idx = 0
    spike_peak = values[0]
    for i in range(1, head_len):
        if values[i] > spike_peak:
            spike_peak = values[i]
            spike_idx = i

    # activeEnd: spikeIndex之后出现尾零(≤0.05)的起始位置（不变）
    active_end = n
    for i in range(spike_idx + 1, n):
        if values[i] <= 0.05:
            active_end = i
            break

    # 全窗检测：activeEnd=n 则曲线未结束
    is_full_window = active_end >= n

    duration = active_end * 0.04 if not is_full_window else n * 0.04
    if duration < 2.0:
        return {'IsValid': True, 'IsFullWindow': is_full_window, 'DurationSec': duration,
                'SpikePeak': spike_peak, 'SpikeIndex': spike_idx, 'ActiveEnd': active_end,
                'UnlockEnd': None, 'LockStart': None,
                'UnlockMean': 0, 'ConvMean': 0, 'LockMean': 0, 'TailMean': 0}

    si = spike_idx
    ae = active_end

    # ② 解锁段 — 物理边界检测
    unlock_end = detect_unlock_end(values, si, ae)
    if unlock_end is not None and unlock_end > si + 1:
        unlock_vals = values[si + 2:unlock_end + 1]
    else:
        fallback_end = max(si + 14, int(ae * 0.5))
        unlock_vals = values[si + 2:min(fallback_end, n)]
        unlock_end = None

    # ③ 转换段 — 物理边界检测
    lock_start, lock_peak = detect_contact_and_lock(values, ae)
    if lock_start is None:
        lock_start = ae - 40 if ae > 50 else ae

    conv_start = (unlock_end + 1) if unlock_end is not None else (si + 20)
    conv_end = lock_start
    conv_vals = values[conv_start:conv_end] if conv_start < conv_end and conv_start < n else []

    # ④ 锁闭段 — 物理边界检测
    if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
        pre_ramp = _st.mean(values[lock_start - 5:lock_start + 1]) if lock_start >= 5 else values[lock_start]
        post_peak_end = min(lock_peak + 40, ae - 5)
        lock_end_idx = lock_peak + 5
        for i in range(lock_peak + 8, post_peak_end):
            if i < n and (values[i] <= pre_ramp * 1.08 or values[i] <= values[lock_peak] * 0.55):
                lock_end_idx = i
                break
        lock_vals = values[lock_start:lock_end_idx + 1]
    else:
        lock_vals = []
        if ae > 50:
            ls2 = max(0, ae - 40)
            le2 = ae - 22
            if ls2 >= 0 and le2 > ls2:
                lock_vals = values[ls2:le2]

    # ⑤ 缓放段 — 物理边界优先，对齐 C# FeatureExtractor.Extract()
    tail_vals = []
    if ae > 30:
        if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
            # 物理路径：缓放段 = lockEnd+1 → activeEnd-2
            tail_start = lock_end_idx + 1
            tail_end = ae - 2
        else:
            # 退化路径：固定偏移 [activeEnd-22, activeEnd-2)
            tail_start = ae - 22
            tail_end = ae - 2
        if tail_start >= 0 and tail_end > tail_start:
            tail_vals = values[tail_start:tail_end]

    return {
        'IsValid': True,
        'IsFullWindow': is_full_window,
        'DurationSec': duration,
        'SpikePeak': spike_peak,
        'SpikeIndex': spike_idx,
        'ActiveEnd': active_end,
        'UnlockEnd': unlock_end,
        'LockStart': lock_start,
        'UnlockMean': round(mean(unlock_vals), 3) if unlock_vals else 0,
        'ConvMean': round(mean(conv_vals), 3) if conv_vals else 0,
        'LockMean': round(mean(lock_vals), 3) if lock_vals else 0,
        'TailMean': round(mean(tail_vals), 3) if tail_vals else 0,
    }


def mean(values):
    if not values:
        return 0.0
    return sum(values) / len(values)


def build_baseline(features, min_samples=30):
    """从特征列表计算基线（与 C# BaselineBuilder.Build 一致）"""
    if len(features) < min_samples:
        return None

    # Step 2: DurationSec 中位数
    durations = [f['DurationSec'] for f in features]
    med_dur = median(durations)

    # Step 3: 筛选正常样本 (|duration - med| < med * 0.15)
    threshold = med_dur * 0.15
    normal = [f for f in features if abs(f['DurationSec'] - med_dur) < threshold]

    if len(normal) < min_samples:
        return None

    # Step 4: 各段中位数
    return {
        'RefDurationSec': round(median([f['DurationSec'] for f in normal]), 2),
        'RefSpikePeak': round(median([f['SpikePeak'] for f in normal]), 3),
        'RefUnlockMean': round(median([f['UnlockMean'] for f in normal]), 3),
        'RefConvMean': round(median([f['ConvMean'] for f in normal]), 3),
        'RefLockMean': round(median([f['LockMean'] for f in normal]), 3),
        'RefTailMean': round(median([f['TailMean'] for f in normal]), 3),
        'SampleCount': len(normal),
        'DateFrom': '',  # 从 features 中无法直接获取日期
        'DateTo': '',
    }


def main():
    print('=' * 60)
    print('按方向重建 baselines.json')
    print('=' * 60)

    # 获取所有开关
    switches = sorted(os.listdir(PARSED_DIR))
    switches = [s for s in switches if os.path.isdir(os.path.join(PARSED_DIR, s))]
    print(f'发现 {len(switches)} 个道岔: {switches}')

    new_baselines = {}

    for sw_id in switches:
        print(f'\n[{sw_id}]')
        by_direction = load_parsed_features(sw_id)

        for direction in DIRECTIONS:
            feats = by_direction.get(direction, [])
            print(f'  {direction}: {len(feats)} 条有效曲线')

            bl = build_baseline(feats, min_samples=30)
            if bl:
                # 补充日期范围
                dates = sorted([f'{int(f["DurationSec"])}' for f in feats])  # placeholder
                key = f'{sw_id}|{direction}'
                bl['Direction'] = direction
                new_baselines[key] = bl
                print(f'    → 基线: Dur={bl["RefDurationSec"]}s Spike={bl["RefSpikePeak"]}kW '
                      f'N={bl["SampleCount"]}')
            else:
                print(f'    → 样本不足（需要≥30）')

    # 保存
    output = {
        'ComputedAt': '2026-07-15 10:50:00',
        'Switches': new_baselines,
    }

    out_path = os.path.join(RULES_DIR, 'baselines.json')
    # 备份旧文件
    old_path = os.path.join(RULES_DIR, 'baselines_old.json')
    if os.path.exists(out_path) and not os.path.exists(old_path):
        os.rename(out_path, old_path)
        print(f'\n旧 baselines.json → baselines_old.json')

    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    print(f'\n已保存: {out_path}')
    print(f'共 {len(new_baselines)} 条基线（{len(switches)} 个开关 × 2 方向）')


if __name__ == '__main__':
    main()
