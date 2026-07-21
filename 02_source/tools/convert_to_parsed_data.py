#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
将提取的功率曲线转换为 SwitchMonitor 程序可读的 parsed_data 格式

输出目录结构:
  parsed_data/
    index.json                         — 总索引
    {switchId}/
      {date}.json                      — 每天的 SwitchEvent 列表

SwitchEvent 格式:
  {
    "Timestamp": 1750000000,           — Unix 时间戳(秒)
    "DateTimeStr": "2025-06-15 08:30:00",
    "Direction": "定位->反位",          — 或 "反位->定位" (交替)
    "Duration": 11.36,                 — 秒
    "SampleInterval": 0.04,            — 40ms
    "SampleCount": 284,
    "Power": [[0.0, 0.0], [0.04, 0.31], ...]  — [[时间, 功率值], ...]
  }
"""
import json
import os
import sys
from datetime import datetime, timedelta

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

INPUT_JSON = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\panyu_curves\power_curves_2hbf_final.json"
OUTPUT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data\parsed_data"

SAMPLE_INTERVAL = 0.04  # 40ms (250Hz)

def main():
    with open(INPUT_JSON, 'r', encoding='utf-8') as f:
        data = json.load(f)

    curves = data['curves']

    # 清理旧数据
    if os.path.exists(OUTPUT_DIR):
        import shutil
        for item in os.listdir(OUTPUT_DIR):
            item_path = os.path.join(OUTPUT_DIR, item)
            if os.path.isdir(item_path):
                shutil.rmtree(item_path)
            elif item.endswith('.json'):
                os.remove(item_path)

    os.makedirs(OUTPUT_DIR, exist_ok=True)

    # ── 根据提取时间生成模拟日期 ──
    # 实际数据时间范围未知(时间戳未解析), 用2026年7月分散到不同天
    extraction_dt = datetime.now()

    all_index = {}  # switchId → date → [timestamps]

    total_events = 0

    for sw_id in sorted(curves.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        sw_curves = curves[sw_id]
        if not sw_curves:
            continue

        # 按天分组 (每50条曲线 = 1天, 模拟真实数据密度)
        curves_per_day = 50
        date_groups = {}

        for i, c in enumerate(sw_curves):
            day_offset = i // curves_per_day
            day_date = extraction_dt - timedelta(days=day_offset)
            date_str = day_date.strftime('%Y-%m-%d')
            day_hour = 8 + (i % curves_per_day) // 4  # 8:00-20:30, 每4条=1小时
            day_minute = ((i % curves_per_day) % 4) * 15  # 0, 15, 30, 45
            day_second = (i * 7) % 60  # 秒随机化

            ts_dt = datetime(day_date.year, day_date.month, day_date.day,
                           min(day_hour, 23), day_minute, day_second)
            timestamp = int(ts_dt.timestamp())

            if date_str not in date_groups:
                date_groups[date_str] = []
            date_groups[date_str].append((timestamp, ts_dt.strftime('%Y-%m-%d %H:%M:%S'), i))

        # ── 为每天生成 SwitchEvent ──
        sw_dir = os.path.join(OUTPUT_DIR, sw_id)
        os.makedirs(sw_dir, exist_ok=True)

        if sw_id not in all_index:
            all_index[sw_id] = {}

        for date_str, events_data in sorted(date_groups.items()):
            events_data.sort(key=lambda x: x[0])  # 按时间戳排序

            switch_events = []
            for j, (ts, dt_str, curve_idx) in enumerate(events_data):
                c = sw_curves[curve_idx]
                values = c['values']
                n = len(values)

                # 交替方向
                direction = '定位->反位' if j % 2 == 0 else '反位->定位'

                # 生成 Power 数据: [[t0, v0], [t1, v1], ...]
                power_pairs = []
                for k, v in enumerate(values):
                    power_pairs.append([round(k * SAMPLE_INTERVAL, 3), v])

                duration = round(n * SAMPLE_INTERVAL, 3)

                evt = {
                    'Timestamp': ts,
                    'DateTimeStr': dt_str,
                    'Direction': direction,
                    'Duration': duration,
                    'SampleInterval': SAMPLE_INTERVAL,
                    'SampleCount': n,
                    'Power': power_pairs,
                    'CurrentA': [],
                    'CurrentB': [],
                    'CurrentC': [],
                    # HBF特有元数据
                    '_hbf_source': {
                        'file_offset': c['file_offset'],
                        'float32_offset': c['float32_offset'],
                        'peak_kw': c['peak_kw'],
                    }
                }
                switch_events.append(evt)

            # 保存日文件
            day_path = os.path.join(sw_dir, f'{date_str}.json')
            with open(day_path, 'w', encoding='utf-8') as f:
                json.dump(switch_events, f, ensure_ascii=False)

            # 更新索引
            timestamps = [e['Timestamp'] for e in switch_events]
            timestamps.sort(reverse=True)  # 降序
            all_index[sw_id][date_str] = timestamps
            total_events += len(switch_events)

    # ── 保存索引 ──
    index_path = os.path.join(OUTPUT_DIR, 'index.json')
    with open(index_path, 'w', encoding='utf-8') as f:
        json.dump(all_index, f, ensure_ascii=False)

    # ── 报告 ──
    print("="*70)
    print("转换完成: 功率曲线 → parsed_data 格式")
    print("="*70)
    print(f"  输出目录: {OUTPUT_DIR}")
    print(f"  道岔数: {len(all_index)}")
    print(f"  总事件: {total_events}")

    # 每个开关的日期和事件统计
    print(f"\n  各开关数据:")
    for sw_id in sorted(all_index.keys(), key=lambda x: (int(x.split('-')[0]), x.split('-')[1])):
        dates = all_index[sw_id]
        total = sum(len(ts) for ts in dates.values())
        date_range = f"{min(dates.keys())} ~ {max(dates.keys())}" if len(dates) > 1 else list(dates.keys())[0]
        print(f"    {sw_id}: {total:4d} curves, {len(dates)} days [{date_range}]")

    print(f"\n  索引: {index_path}")
    print(f"  数据: {OUTPUT_DIR}/{{switchId}}/{{date}}.json")
    print(f"\n  注意: 时间戳为估算值(按序号生成), 实际时间待HBF事件头解析后更新")
    print("完成!")


if __name__ == '__main__':
    main()
