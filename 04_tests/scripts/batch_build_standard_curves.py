#!/usr/bin/env python3
"""
批量生成所有道岔的标准曲线。
步骤：
  1. 从每个开关的第一天正常曲线中挑选参考曲线
  2. 保存为 reference_curves/{switchId}.json
  3. 融合基线生成 standard_curves/{switchId}.json
"""

import json
import os
import sys
import math
import copy

if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

PROD_DIR = r'd:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data'
RULES_DIR = os.path.join(PROD_DIR, 'Rules')
PARSED_DIR = os.path.join(PROD_DIR, 'parsed_data')

# ── 复用 verify_standard_curve.py 的核心函数 ──
# 直接 import 避免重复代码
sys.path.insert(0, os.path.dirname(__file__))
from verify_standard_curve import (
    load_json, median, lerp, extract_features,
    resample_linear, get_point_alpha, build_standard_curve
)


def pick_reference_curves_by_direction(switch_id):
    """从某开关的 parsed_data 中为每个方向挑选一条代表性正常曲线"""
    sw_dir = os.path.join(PARSED_DIR, switch_id)
    if not os.path.isdir(sw_dir):
        print(f'  [WARN] {switch_id}: 无 parsed_data 目录')
        return {}

    daily_files = sorted([f for f in os.listdir(sw_dir) if f.endswith('.json')])
    if not daily_files:
        print(f'  [WARN] {switch_id}: parsed_data 为空')
        return {}

    # 已找到的方向集合
    found = {}
    for day_file in daily_files:
        events = load_json(os.path.join(sw_dir, day_file))
        for evt in events:
            pw = evt.get('Power', [])
            if not pw:
                continue
            values = [p[1] for p in pw if len(p) >= 2]
            if not values:
                continue
            feat = extract_features(values)
            if feat['IsValid'] and not feat['IsFullWindow'] and feat['DurationSec'] >= 2.4:
                direction = evt.get('Direction', '')
                if direction and direction not in found:
                    found[direction] = {
                        'values': values,
                        'feat': feat,
                        'direction': direction,
                        'datetime': evt.get('DateTimeStr', ''),
                        'switch_id': switch_id,
                    }
                if len(found) >= 2:
                    return found

    return found


def save_reference_curve(ref, output_dir):
    """保存参考曲线 JSON，文件名包含方向"""
    os.makedirs(output_dir, exist_ok=True)
    # 文件名：{switchId}_{direction}.json
    file_name = f"{ref['switch_id']}_{ref['direction']}.json"
    path = os.path.join(output_dir, file_name)
    data = {
        'SwitchId': ref['switch_id'],
        'Direction': ref['direction'],
        'SampleInterval': 0.04,
        'AlignIndex': ref['feat']['SpikeIndex'],
        'Values': ref['values'],
        'Source': 'auto-picked',
        'SourceDateTime': ref['datetime'],
        'CreatedAt': '2026-07-15 batch_build'
    }
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    return path


def save_standard_curve(sc, switch_id, direction, bl_computed_at, output_dir):
    """保存标准曲线 JSON，文件名包含方向"""
    os.makedirs(output_dir, exist_ok=True)
    file_name = f"{switch_id}_{direction}.json"
    path = os.path.join(output_dir, file_name)
    data = {
        'SwitchId': switch_id,
        'Direction': direction,
        'SampleInterval': 0.04,
        'AlignIndex': sc['AlignIndex'],
        'Values': sc['Values'],
        'FusionWeight': 1.0,
        'ReferenceSource': f"reference_curves/{switch_id}_{direction}.json",
        'BaselineComputedAt': bl_computed_at,
        'AlphaTime': sc['AlphaTime'],
        'AlphaSpike': sc['AlphaSpike'],
        'AlphaUnlock': sc['AlphaUnlock'],
        'AlphaConv': sc['AlphaConv'],
        'AlphaLock': sc['AlphaLock'],
        'AlphaTail': sc['AlphaTail'],
        'ComputedAt': '2026-07-15 batch_build'
    }
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    return path


def parse_baseline_key(key):
    """从基线 key "switchId|direction" 或 "switchId" 中解析出 switchId 和 direction。
    key 不含 | 时从后缀推导：-J → 定位→反位，-X → 反位→定位"""
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
    print('批量生成标准曲线（按方向）')
    print('=' * 60)

    # 加载基线（新格式: key = "switchId|direction"）
    baselines_path = os.path.join(RULES_DIR, 'baselines.json')
    bl_store = load_json(baselines_path)
    switches = bl_store['Switches']
    print(f'\n基线文件: {baselines_path}')
    print(f'基线条目: {len(switches)} 条')
    print(f'基线计算时间: {bl_store.get("ComputedAt", "N/A")}')

    # 输出目录
    ref_dir = os.path.join(RULES_DIR, 'reference_curves')
    sc_dir = os.path.join(RULES_DIR, 'standard_curves')

    # 清空旧的无方向文件
    for d in [ref_dir, sc_dir]:
        if os.path.isdir(d):
            for f in os.listdir(d):
                if f.endswith('.json') and '_' not in f.replace('.json', ''):
                    old = os.path.join(d, f)
                    os.remove(old)
                    print(f'  已清理旧文件: {old}')

    results = []

    for bl_key, baseline in sorted(switches.items()):
        switch_id, direction = parse_baseline_key(bl_key)
        if not direction:
            print(f'\n  [SKIP] {bl_key}: 无方向信息')
            continue

        print(f'\n{"─" * 40}')
        print(f'[{switch_id}] {direction}')

        # Step 1: 挑选该方向的一条正常曲线作为参考曲线
        sw_dir = os.path.join(PARSED_DIR, switch_id)
        ref = None
        if os.path.isdir(sw_dir):
            daily_files = sorted([f for f in os.listdir(sw_dir) if f.endswith('.json')])
            for day_file in daily_files:
                events = load_json(os.path.join(sw_dir, day_file))
                for evt in events:
                    pw = evt.get('Power', [])
                    if not pw:
                        continue
                    values = [p[1] for p in pw if len(p) >= 2]
                    if not values:
                        continue
                    feat = extract_features(values)
                    if feat['IsValid'] and not feat['IsFullWindow'] and feat['DurationSec'] >= 2.4:
                        evt_dir = evt.get('Direction', '')
                        if evt_dir == direction:
                            ref = {
                                'values': values,
                                'feat': feat,
                                'direction': direction,
                                'datetime': evt.get('DateTimeStr', ''),
                                'switch_id': switch_id,
                            }
                            break
                if ref is not None:
                    break

        if ref is None:
            print(f'  [SKIP] 无 {direction} 方向参考曲线')
            continue

        print(f'  参考曲线: {ref["datetime"]}, '
              f'点数={len(ref["values"])}')
        print(f'  特征: Dur={ref["feat"]["DurationSec"]}s, '
              f'Spike={ref["feat"]["SpikePeak"]}kW, '
              f'Unlock={ref["feat"]["UnlockMean"]}kW, '
              f'Conv={ref["feat"]["ConvMean"]}kW, '
              f'Lock={ref["feat"]["LockMean"]}kW, '
              f'Tail={ref["feat"]["TailMean"]}kW')

        # Step 2: 保存参考曲线
        ref_path = save_reference_curve(ref, ref_dir)
        print(f'  参考曲线 → {ref_path}')

        # Step 3: 融合生成标准曲线
        sc = build_standard_curve(ref['values'], baseline, fusion_weight=1.0)
        if sc is None:
            print(f'  [FAIL] 融合失败')
            continue

        print(f'  α: t={sc["AlphaTime"]:.4f} spike={sc["AlphaSpike"]:.4f} '
              f'unlock={sc["AlphaUnlock"]:.4f} conv={sc["AlphaConv"]:.4f} '
              f'lock={sc["AlphaLock"]:.4f} tail={sc["AlphaTail"]:.4f}')
        print(f'  标准曲线: {len(sc["Values"])} 点, AlignIndex={sc["AlignIndex"]}')

        # Step 4: 保存标准曲线
        sc_path = save_standard_curve(sc, switch_id, direction,
                                       bl_store.get('ComputedAt', ''), sc_dir)
        print(f'  标准曲线 → {sc_path}')

        results.append({
            'key': bl_key,
            'ref_len': len(ref['values']),
            'sc_len': len(sc['Values']),
            'alphas': {k: sc[k] for k in
                       ['AlphaTime', 'AlphaSpike', 'AlphaUnlock',
                        'AlphaConv', 'AlphaLock', 'AlphaTail']}
        })

    # ── 摘要 ──
    print(f'\n{"=" * 60}')
    print(f'完成: {len(results)}/{len(switches)} 条')
    print(f'{"=" * 60}')
    for r in results:
        a = r['alphas']
        print(f'  {r["key"]:30s}  ref={r["ref_len"]:3d}  sc={r["sc_len"]:3d}  '
              f'α_spike={a["AlphaSpike"]:.3f} α_unlock={a["AlphaUnlock"]:.3f} '
              f'α_conv={a["AlphaConv"]:.3f} α_lock={a["AlphaLock"]:.3f} '
              f'α_tail={a["AlphaTail"]:.3f}')
    print(f'\n参考曲线目录: {ref_dir}')
    print(f'标准曲线目录: {sc_dir}')


if __name__ == '__main__':
    main()
