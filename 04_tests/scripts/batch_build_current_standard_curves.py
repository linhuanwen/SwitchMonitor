#!/usr/bin/env python3
"""
批量生成所有道岔的电流标准曲线。
步骤：
  1. 从每个开关的 parsed_data 中为每个方向挑选一条正常三相电流参考曲线
  2. 保存为 current_reference_curves/{switchId}_{direction}.json
  3. 融合电流基线生成 current_standard_curves/{switchId}_{direction}.json

与 batch_build_standard_curves.py（功率曲线）对应，但处理 A/B/C 三相电流。

用法:
    python batch_build_current_standard_curves.py
"""

import json
import os
import sys
from datetime import datetime

if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

PROD_DIR = r'd:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data'
RULES_DIR = os.path.join(PROD_DIR, 'Rules')
PARSED_DIR = os.path.join(PROD_DIR, 'parsed_data')

# ── 复用 verify_current_standard_curve.py 的核心函数 ──
sys.path.insert(0, os.path.dirname(__file__))
from verify_current_standard_curve import (
    load_json, extract_phase_features,
    build_current_standard_curve,
    save_current_standard_curve_per_phase,
    save_current_reference_curve_per_phase
)


def find_reference_event(switch_id, direction, min_date=None):
    """从 parsed_data 中为指定开关和方向寻找第一条正常三相电流事件。
    返回 dict(va, vb, vc, datetime, ...) 或 None。

    min_date: 可选的最早日期过滤（如 '2026-04-30'），跳过更早的日文件。
    """
    sw_dir = os.path.join(PARSED_DIR, switch_id)
    if not os.path.isdir(sw_dir):
        return None

    for day_file in sorted(os.listdir(sw_dir)):
        if not day_file.endswith('.json') or '.diag' in day_file or 'features' in day_file.lower():
            continue

        if min_date and day_file < min_date:
            continue

        try:
            events = load_json(os.path.join(sw_dir, day_file))
        except Exception:
            continue

        for evt in events:
            ca = evt.get('CurrentA', [])
            cb = evt.get('CurrentB', [])
            cc = evt.get('CurrentC', [])
            if not ca or not cb or not cc:
                continue

            va = [p[1] for p in ca if len(p) >= 2]
            vb = [p[1] for p in cb if len(p) >= 2]
            vc = [p[1] for p in cc if len(p) >= 2]
            if not va or not vb or not vc:
                continue

            # 方向匹配
            evt_dir = evt.get('Direction', '')
            if evt_dir != direction:
                continue

            fa = extract_phase_features(va)
            if fa['IsValid'] and fa['DurationSec'] >= 2.4:
                return {
                    'va': va,
                    'vb': vb,
                    'vc': vc,
                    'fa': fa,
                    'direction': direction,
                    'datetime': evt.get('DateTimeStr', ''),
                    'switch_id': switch_id,
                }

    return None


def save_current_reference_curve(ref, output_dir):
    """保存三相电流参考曲线 JSON。"""
    os.makedirs(output_dir, exist_ok=True)
    file_name = f"{ref['switch_id']}_{ref['direction']}.json"
    path = os.path.join(output_dir, file_name)

    data = {
        'SwitchId': ref['switch_id'],
        'Direction': ref['direction'],
        'SampleInterval': 0.04,
        # 每相独立的 AlignIndex（spikeIndex）
        'AlignIndexA': ref['fa']['SpikeIndex'],
        'AlignIndexB': extract_phase_features(ref['vb']).get('SpikeIndex', 0),
        'AlignIndexC': extract_phase_features(ref['vc']).get('SpikeIndex', 0),
        'ValuesA': [round(v, 3) for v in ref['va']],
        'ValuesB': [round(v, 3) for v in ref['vb']],
        'ValuesC': [round(v, 3) for v in ref['vc']],
        'ComputedAt': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        'Source': 'auto-picked',
        'SourceDateTime': ref['datetime'],
    }
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    # 同步保存分相文件（拆分为 3 个独立 JSON：A/B/C）
    for phase in ['A', 'B', 'C']:
        save_current_reference_curve_per_phase(
            data, ref['switch_id'], ref['direction'], phase, output_dir)

    return path


def save_current_standard_curve(sc, switch_id, direction, bl_computed_at, output_dir):
    """保存三相电流标准曲线 JSON。"""
    os.makedirs(output_dir, exist_ok=True)
    file_name = f"{switch_id}_{direction}.json"
    path = os.path.join(output_dir, file_name)

    data = {
        'SwitchId': switch_id,
        'Direction': direction,
        'SampleInterval': 0.04,
        'AlignIndexA': sc['AlignIndexA'],
        'AlignIndexB': sc['AlignIndexB'],
        'AlignIndexC': sc['AlignIndexC'],
        'ValuesA': sc['ValuesA'],
        'ValuesB': sc['ValuesB'],
        'ValuesC': sc['ValuesC'],
        'OriginalMedianValuesA': sc['ValuesA'],
        'OriginalMedianValuesB': sc['ValuesB'],
        'OriginalMedianValuesC': sc['ValuesC'],
        'FusionWeight': 1.0,
        'ReferenceSource': f"current_reference_curves/{switch_id}_{direction}.json",
        'BaselineComputedAt': bl_computed_at,
        'AlphaTime': sc['AlphaTime'],
        'AlphaSpikeA': sc['AlphaSpikeA'], 'AlphaUnlockA': sc['AlphaUnlockA'],
        'AlphaConvA': sc['AlphaConvA'], 'AlphaLockA': sc['AlphaLockA'], 'AlphaTailA': sc['AlphaTailA'],
        'AlphaSpikeB': sc['AlphaSpikeB'], 'AlphaUnlockB': sc['AlphaUnlockB'],
        'AlphaConvB': sc['AlphaConvB'], 'AlphaLockB': sc['AlphaLockB'], 'AlphaTailB': sc['AlphaTailB'],
        'AlphaSpikeC': sc['AlphaSpikeC'], 'AlphaUnlockC': sc['AlphaUnlockC'],
        'AlphaConvC': sc['AlphaConvC'], 'AlphaLockC': sc['AlphaLockC'], 'AlphaTailC': sc['AlphaTailC'],
        'ComputedAt': datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    }
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    # 同步保存分相文件（拆分为 3 个独立 JSON：A/B/C）
    for phase in ['A', 'B', 'C']:
        save_current_standard_curve_per_phase(
            data, switch_id, direction, phase, bl_computed_at, output_dir)

    return path


def parse_baseline_key(key):
    """从基线 key "switchId|direction" 解析 switchId 和 direction。"""
    idx = key.find('|')
    if idx >= 0:
        return key[:idx], key[idx+1:]
    if key.endswith('-J'):
        return key, '定位→反位'
    if key.endswith('-X'):
        return key, '反位→定位'
    return key, ''


def main():
    print('=' * 60)
    print('批量生成电流标准曲线（按方向，三相独立融合）')
    print('=' * 60)

    # 加载电流基线（新格式: key = "switchId|direction"）
    baselines_path = os.path.join(RULES_DIR, 'current_baselines.json')
    bl_store = load_json(baselines_path)
    switches = bl_store.get('Switches', bl_store.get('switches', {}))
    print(f'\n基线文件: {baselines_path}')
    print(f'基线条目: {len(switches)} 条')
    print(f'基线计算时间: {bl_store.get("ComputedAt", "N/A")}')

    if not switches:
        print('[ERROR] 无基线条目，请先运行 save_current_baselines.py')
        return

    # 输出目录
    ref_dir = os.path.join(RULES_DIR, 'current_reference_curves')
    sc_dir = os.path.join(RULES_DIR, 'current_standard_curves')

    results = []

    for bl_key, baseline in sorted(switches.items()):
        switch_id, direction = parse_baseline_key(bl_key)
        if not direction:
            print(f'\n  [SKIP] {bl_key}: 无方向信息')
            continue

        print(f'\n{"─" * 50}')
        print(f'[{switch_id}] {direction}')

        # Step 1: 找三相参考事件
        # 1-J 的 B/C 相序在 2026-04-29 凌晨被纠正，只用 4/30 及之后的数据
        min_date = '2026-04-30.json' if switch_id == '1-J' else None
        ref = find_reference_event(switch_id, direction, min_date)
        if ref is None:
            print(f'  [SKIP] 无 {direction} 方向正常三相电流事件')
            continue

        fb = extract_phase_features(ref['vb'])
        fc = extract_phase_features(ref['vc'])

        print(f'  参考曲线: {ref["datetime"]}')
        print(f'    A: {len(ref["va"])}点, Dur={ref["fa"]["DurationSec"]}s, '
              f'Spike={ref["fa"]["SpikePeak"]}A')
        print(f'    B: {len(ref["vb"])}点, Dur={fb["DurationSec"]}s, '
              f'Spike={fb["SpikePeak"]}A')
        print(f'    C: {len(ref["vc"])}点, Dur={fc["DurationSec"]}s, '
              f'Spike={fc["SpikePeak"]}A')

        # 打印基线对比
        bl_dur = baseline.get('RefDurationSec', baseline.get('refDurationSec', 0))
        print(f'  基线时长: {bl_dur}s, 样本数: {baseline.get("SampleCount", baseline.get("sampleCount", "?"))}')

        # Step 2: 保存电流参考曲线
        ref_path = save_current_reference_curve(ref, ref_dir)
        print(f'  参考曲线 → {ref_path}')

        # Step 3: 融合生成电流标准曲线
        sc = build_current_standard_curve(
            ref['va'], ref['vb'], ref['vc'],
            baseline, fusion_weight=1.0)

        if sc is None:
            print(f'  [FAIL] 融合失败')
            continue

        print(f'  标准曲线: A={len(sc["ValuesA"])}点, B={len(sc["ValuesB"])}点, C={len(sc["ValuesC"])}点')
        print(f'  α_t={sc["AlphaTime"]:.4f}')

        for p in ['A', 'B', 'C']:
            print(f'    {p}相: α_spike={sc[f"AlphaSpike{p}"]:.4f} '
                  f'α_unlock={sc[f"AlphaUnlock{p}"]:.4f} '
                  f'α_conv={sc[f"AlphaConv{p}"]:.4f} '
                  f'α_lock={sc[f"AlphaLock{p}"]:.4f} '
                  f'α_tail={sc[f"AlphaTail{p}"]:.4f}')

        # Step 4: 保存电流标准曲线
        sc_path = save_current_standard_curve(
            sc, switch_id, direction,
            bl_store.get('ComputedAt', ''), sc_dir)
        print(f'  标准曲线 → {sc_path}')

        results.append({
            'key': bl_key,
            'ref_len_a': len(ref['va']),
            'ref_len_b': len(ref['vb']),
            'ref_len_c': len(ref['vc']),
            'sc_len_a': len(sc['ValuesA']),
            'sc_len_b': len(sc['ValuesB']),
            'sc_len_c': len(sc['ValuesC']),
        })

    # ── 摘要 ──
    print(f'\n{"=" * 60}')
    print(f'完成: {len(results)}/{len(switches)} 条')
    print(f'{"=" * 60}')
    for r in results:
        print(f'  {r["key"]:30s}  '
              f'ref=(A:{r["ref_len_a"]:3d} B:{r["ref_len_b"]:3d} C:{r["ref_len_c"]:3d})  '
              f'sc=(A:{r["sc_len_a"]:3d} B:{r["sc_len_b"]:3d} C:{r["sc_len_c"]:3d})')
    print(f'\n电流参考曲线目录: {ref_dir}')
    print(f'电流标准曲线目录: {sc_dir}')


if __name__ == '__main__':
    main()
