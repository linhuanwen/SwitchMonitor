#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
保存电流基线到 current_baselines.json（C# CurrentBaselineStore 兼容格式）。

用法:
    python save_current_baselines.py [parsed_data目录] [输出路径]

默认:
    parsed_data: ../05_production_data/parsed_data
    输出:        ../05_production_data/Rules/current_baselines.json
"""
import json
import sys
from datetime import datetime
from pathlib import Path

# 复用 reference check 模块的函数
from current_baseline_ref_check import (
    load_parsed_events, extract_current_features,
    build_current_baseline, _load_normal_timestamps
)


def save_baselines(parsed_dir, output_path):
    """生成并保存电流基线到 JSON 文件。"""
    all_data = load_parsed_events(parsed_dir)
    DIRS = ["定位→反位", "反位→定位"]

    switches = {}

    for sid in sorted(all_data.keys()):
        events = all_data[sid]

        # 加载功率诊断"正常"时间戳
        normal_ts = _load_normal_timestamps(parsed_dir, sid)

        # 提取特征
        features = []
        for evt in events:
            if normal_ts is not None:
                ts = evt.get('Timestamp', 0)
                if ts not in normal_ts:
                    continue
            features.append(extract_current_features(evt))

        if not features:
            print(f"  [{sid}] 无电流数据，跳过")
            continue

        # 计算日期范围
        all_dates = []
        for evt in events:
            dt = evt.get('DateTimeStr', '')
            if len(dt) >= 10:
                all_dates.append(dt[:10])
        date_from = min(all_dates) if all_dates else ""
        date_to = max(all_dates) if all_dates else ""

        for dir_ in DIRS:
            baseline = build_current_baseline(features, min_samples=30, direction=dir_)

            if baseline is None:
                valid = sum(1 for f in features
                           if f.get('isValid') and not f.get('isFullWindow')
                           and f.get('durationSec', 0) >= 2.4
                           and f.get('direction') == dir_)
                print(f"  [{sid}] {dir_}: 正常样本={valid} 不足30，跳过")
                continue

            key = f"{sid}|{dir_}"
            switches[key] = {
                # A相
                "RefSpikePeakA": baseline['refSpikePeakA'],
                "RefSpikeIndexA": baseline['refSpikeIndexA'],
                "RefUnlockMeanA": baseline['refUnlockMeanA'],
                "RefConvMeanA": baseline['refConvMeanA'],
                "RefLockMeanA": baseline['refLockMeanA'],
                "RefTailMeanA": baseline['refTailMeanA'],
                # B相
                "RefSpikePeakB": baseline['refSpikePeakB'],
                "RefSpikeIndexB": baseline['refSpikeIndexB'],
                "RefUnlockMeanB": baseline['refUnlockMeanB'],
                "RefConvMeanB": baseline['refConvMeanB'],
                "RefLockMeanB": baseline['refLockMeanB'],
                "RefTailMeanB": baseline['refTailMeanB'],
                # C相
                "RefSpikePeakC": baseline['refSpikePeakC'],
                "RefSpikeIndexC": baseline['refSpikeIndexC'],
                "RefUnlockMeanC": baseline['refUnlockMeanC'],
                "RefConvMeanC": baseline['refConvMeanC'],
                "RefLockMeanC": baseline['refLockMeanC'],
                "RefTailMeanC": baseline['refTailMeanC'],
                # 汇总
                "RefDurationSec": baseline['refDurationSec'],
                "RefMaxUnbalanceRatio": baseline['refMaxUnbalanceRatio'],
                # 元数据
                "SampleCount": baseline['sampleCount'],
                "Direction": dir_,
                "DateFrom": date_from,
                "DateTo": date_to,
            }
            print(f"  [{key}] SampleCount={baseline['sampleCount']} DateFrom={date_from} DateTo={date_to}")

    store = {
        "ComputedAt": datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        "Switches": switches,
    }

    output = Path(output_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    with open(output, 'w', encoding='utf-8') as f:
        json.dump(store, f, ensure_ascii=False)

    print(f"\n已保存 {len(switches)} 条基线 → {output}")
    return 0


def main():
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent.parent

    if len(sys.argv) > 1:
        parsed_dir = sys.argv[1]
    else:
        parsed_dir = str(repo_root / '05_production_data' / 'parsed_data')

    if len(sys.argv) > 2:
        output_path = sys.argv[2]
    else:
        output_path = str(repo_root / '05_production_data' / 'Rules' / 'current_baselines.json')

    print(f"Parsed data: {parsed_dir}")
    print(f"Output:      {output_path}")
    print()

    return save_baselines(parsed_dir, output_path)


if __name__ == '__main__':
    sys.exit(main())
