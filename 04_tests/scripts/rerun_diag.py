#!/usr/bin/env python3
"""
重跑全部诊断 — 用新 baselines.json 对 parsed_data 中所有事件重新评估 R0-R9，
覆盖写入 .diag.json 文件。

对齐 C# DiagnosisEngine.EvaluateR0..R9 + FeatureExtractor.Extract
"""

import json
import os
import sys
import statistics
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

BASELINES_PATH = os.path.join(RULES_DIR, 'baselines.json')
THRESHOLDS_PATH = os.path.join(RULES_DIR, 'thresholds.json')

DIRECTIONS = ['定位→反位', '反位→定位']

# ── 诊断级别（与 C# 一致） ──
LEVEL_RANK = {'正常': 0, '预警': 1, '报警': 2, '故障': 3}


def load_json(path):
    with open(path, 'r', encoding='utf-8-sig') as f:
        return json.load(f)


def mean(values):
    if not values:
        return 0.0
    return sum(values) / len(values)


def extract_features(values):
    """与 C# FeatureExtractor.Extract 对齐的五段特征提取"""
    n = len(values)
    f = {
        'IsValid': False, 'IsFullWindow': False, 'SampleCount': n,
        'DurationSec': 0, 'SpikePeak': 0, 'SpikeIndex': 0, 'ActiveEnd': 0,
        'UnlockEnd': None, 'LockStart': None,
        'UnlockMean': 0, 'ConvMean': 0, 'LockMean': 0, 'TailMean': 0,
        'StepRatio': 1.0, 'RawValues': values,
    }
    if n < 10:
        return f

    peak_all = max(values)
    f['IsValid'] = peak_all > 0.01
    if not f['IsValid']:
        return f

    # spikeIndex: 前15点内最大值
    head_len = min(15, n)
    spike_idx = max(range(head_len), key=lambda i: values[i])
    spike_peak = values[spike_idx]
    f['SpikePeak'] = round(spike_peak, 3)
    f['SpikeIndex'] = spike_idx

    # activeEnd: spikeIndex之后出现尾零(≤0.05)的起始位置
    active_end = n
    for i in range(spike_idx + 1, n):
        if values[i] <= 0.05:
            active_end = i
            break
    f['IsFullWindow'] = active_end >= n
    f['ActiveEnd'] = active_end
    f['DurationSec'] = round(active_end * 0.04, 2)

    if f['DurationSec'] < 2.0:
        return f

    si, ae = spike_idx, active_end

    # ② 解锁段
    unlock_end = detect_unlock_end(values, si, ae)
    if unlock_end is not None and unlock_end > si + 1:
        unlock_vals = values[si + 2:unlock_end + 1]
    else:
        fallback_end = max(si + 14, int(ae * 0.5))
        unlock_vals = values[si + 2:min(fallback_end, n)]
        unlock_end = None
    f['UnlockEnd'] = unlock_end
    f['UnlockMean'] = round(mean(unlock_vals), 3) if unlock_vals else 0

    # ③ 转换段
    lock_start, lock_peak = detect_contact_and_lock(values, ae)
    f['LockStart'] = lock_start
    if lock_start is None:
        lock_start = ae - 40 if ae > 50 else ae

    conv_start = (unlock_end + 1) if unlock_end is not None else (si + 20)
    conv_end = lock_start
    if conv_start < conv_end and conv_start < n:
        conv_vals = values[conv_start:conv_end]
        f['ConvMean'] = round(mean(conv_vals), 3) if conv_vals else 0
        # R6: stepRatio = 后1/3均值 / 前1/3均值
        third = len(conv_vals) // 3
        if third >= 5:
            f['StepRatio'] = round(
                mean(conv_vals[-third:]) / max(mean(conv_vals[:third]), 0.01), 3)
        else:
            f['StepRatio'] = 1.0
    else:
        f['ConvMean'] = 0
        f['StepRatio'] = 1.0

    # ④ 锁闭段 + ⑤ 缓放段
    lock_end_idx = None
    if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
        pre_ramp_val = statistics.mean(values[lock_start - 5:lock_start + 1]) if lock_start >= 5 else values[lock_start]
        post_peak_end = min(lock_peak + 40, ae - 5)
        lock_end_idx = lock_peak + 5
        for i in range(lock_peak + 8, post_peak_end):
            if i < n and (values[i] <= pre_ramp_val * 1.08 or values[i] <= values[lock_peak] * 0.55):
                lock_end_idx = i
                break
        lock_vals = values[lock_start:lock_end_idx + 1]
    else:
        lock_vals = []
        if ae > 50:
            ls2, le2 = max(0, ae - 40), ae - 22
            if le2 > ls2:
                lock_vals = values[ls2:le2]
    f['LockMean'] = round(mean(lock_vals), 3) if lock_vals else 0

    # 缓放段
    tail_vals = []
    if ae > 30:
        if lock_end_idx is not None:
            tail_start = lock_end_idx + 1
            tail_end = ae - 2
        else:
            tail_start = ae - 22
            tail_end = ae - 2
        if tail_start >= 0 and tail_end > tail_start:
            tail_vals = values[tail_start:tail_end]
    f['TailMean'] = round(mean(tail_vals), 3) if tail_vals else 0

    return f


def overall_level(results):
    """综合级别取最高（与 C# DiagnosisAggregator.OverallLevel 一致）"""
    if not results:
        return '正常'
    max_rank = max(LEVEL_RANK.get(r['Level'], 0) for r in results)
    for k, v in LEVEL_RANK.items():
        if v == max_rank:
            return k
    return '正常'


def diagnose(f, baseline, thresholds):
    """
    对一条曲线执行 R0-R9 规则评估。
    对齐 C# DiagnosisEngine.Diagnose() 的判定顺序。
    返回 (results, overall_level)。
    """
    results = []

    # ── R0: 采集异常 ──
    if not f['IsValid']:
        results.append({
            'RuleId': 'R0', 'RuleName': '采集异常', 'Level': '报警',
            'Description': '采集异常，曲线无效',
            'Value': f['SampleCount'], 'Reference': 0.0
        })
        return results, '报警'

    # ── 获取规则阈值 ──
    t1 = thresholds.get('R1')
    t2 = thresholds.get('R2')
    t3 = thresholds.get('R3')
    t4 = thresholds.get('R4')
    t5 = thresholds.get('R5')
    t6 = thresholds.get('R6')
    t7 = thresholds.get('R7')
    t8 = thresholds.get('R8')
    t9 = thresholds.get('R9')

    # ── R1: 动作超时/未完成 ──
    if t1 and t1.get('enabled', True):
        dur_over = t1.get('durOverRefSeconds', 3.0)
        ref_dur = baseline.get('RefDurationSec', 0) if baseline else 0
        if f['IsFullWindow'] or f['DurationSec'] > ref_dur + dur_over:
            desc = f"动作时长{f['DurationSec']:.2f}s，超过参考{ref_dur:.2f}s+{dur_over:.1f}s，疑似卡阻/空转未完成"
            results.append({
                'RuleId': 'R1', 'RuleName': '动作超时/未完成', 'Level': t1.get('level', '故障'),
                'Description': desc, 'Value': f['DurationSec'], 'Reference': ref_dur
            })
            return results, overall_level(results)

    if baseline is None:
        if f['IsFullWindow']:
            results.append({
                'RuleId': 'R1', 'RuleName': '动作超时/未完成', 'Level': '故障',
                'Description': f"动作时长{f['DurationSec']:.2f}s，打满录制窗口≥31.2s，疑似卡阻/空转未完成",
                'Value': f['DurationSec'], 'Reference': 0.0
            })
        return results, overall_level(results)

    # ── R2: 动作夭折 ──
    if t2 and t2.get('enabled', True):
        ratio = t2.get('durUnderRefRatio', 0.6)
        ref_dur = baseline.get('RefDurationSec', 0)
        if f['DurationSec'] < ref_dur * ratio:
            results.append({
                'RuleId': 'R2', 'RuleName': '动作夭折', 'Level': t2.get('level', '报警'),
                'Description': f"动作时长{f['DurationSec']:.2f}s，不足参考{ref_dur:.2f}s的{ratio:.0%}，动作夭折",
                'Value': f['DurationSec'], 'Reference': ref_dur
            })
            return results, overall_level(results)

    # ── R3-R9: 依次评估，全部命中加入结果 ──

    # R3: 动作时长偏差
    if t3 and t3.get('enabled', True):
        max_dev = t3.get('maxDeviationSeconds', 0.5)
        ref_dur = baseline.get('RefDurationSec', 0)
        deviation = abs(f['DurationSec'] - ref_dur)
        if deviation > max_dev:
            results.append({
                'RuleId': 'R3', 'RuleName': '动作时长偏差', 'Level': t3.get('level', '预警'),
                'Description': f"动作时长偏差{deviation:.2f}s，超出阈值{max_dev:.2f}s，疑似阻力变化",
                'Value': f['DurationSec'], 'Reference': ref_dur
            })

    # R4: 启动峰值偏高
    if t4 and t4.get('enabled', True):
        over_ratio = t4.get('overRefRatio', 1.3)
        ref_spike = baseline.get('RefSpikePeak', 0)
        if f['SpikePeak'] > ref_spike * over_ratio:
            results.append({
                'RuleId': 'R4', 'RuleName': '启动峰值偏高', 'Level': t4.get('level', '预警'),
                'Description': f"启动峰值{f['SpikePeak']:.3f}kW，超过参考{ref_spike:.3f}kW的{over_ratio:.1f}倍，疑似启动回路/机械卡滞",
                'Value': f['SpikePeak'], 'Reference': ref_spike
            })

    # R5: 转换段功率偏高
    if t5 and t5.get('enabled', True):
        over_ratio = t5.get('overRefRatio', 1.3)
        ref_conv = baseline.get('RefConvMean', 0)
        if f['ConvMean'] > ref_conv * over_ratio:
            results.append({
                'RuleId': 'R5', 'RuleName': '转换段功率偏高', 'Level': t5.get('level', '预警'),
                'Description': f"转换段功率{f['ConvMean']:.3f}kW，超过参考{ref_conv:.3f}kW的{over_ratio:.1f}倍，疑似转换阻力增大",
                'Value': f['ConvMean'], 'Reference': ref_conv
            })

    # R6: 转换段台阶突变
    if t6 and t6.get('enabled', True):
        max_sr = t6.get('maxStepRatio', 1.5)
        min_sr = t6.get('minStepRatio', 0.67)
        if f['StepRatio'] > max_sr or f['StepRatio'] < min_sr:
            reason = '偏大（中途受阻）' if f['StepRatio'] > max_sr else '偏小（空转）'
            results.append({
                'RuleId': 'R6', 'RuleName': '转换段台阶突变', 'Level': t6.get('level', '报警'),
                'Description': f"转换段台阶比{f['StepRatio']:.3f}，超出正常范围[{min_sr:.2f}, {max_sr:.2f}]，{reason}",
                'Value': f['StepRatio'], 'Reference': 1.0
            })

    # R7: 解锁段偏高
    if t7 and t7.get('enabled', True):
        over_ratio = t7.get('overRefRatio', 1.3)
        ref_unlock = baseline.get('RefUnlockMean', 0)
        if f['UnlockMean'] > ref_unlock * over_ratio:
            results.append({
                'RuleId': 'R7', 'RuleName': '解锁段偏高', 'Level': t7.get('level', '预警'),
                'Description': f"解锁段功率{f['UnlockMean']:.3f}kW，超过参考{ref_unlock:.3f}kW的{over_ratio:.1f}倍，疑似密贴过紧/卡缺口",
                'Value': f['UnlockMean'], 'Reference': ref_unlock
            })

    # R8: 缓放段异常
    if t8 and t8.get('enabled', True):
        dev_ratio = t8.get('deviationRatio', 0.3)
        ref_tail = baseline.get('RefTailMean', 0)
        if f['TailMean'] == 0.0:
            results.append({
                'RuleId': 'R8', 'RuleName': '缓放段异常', 'Level': t8.get('level', '预警'),
                'Description': '缓放段缺失（功率曲线尾部无缓放段），疑似锁闭/开闭器异常',
                'Value': 0.0, 'Reference': ref_tail
            })
        elif ref_tail > 0:
            deviation = abs(f['TailMean'] - ref_tail) / ref_tail
            if deviation > dev_ratio:
                direction_str = '偏高' if f['TailMean'] > ref_tail else '偏低'
                results.append({
                    'RuleId': 'R8', 'RuleName': '缓放段异常', 'Level': t8.get('level', '预警'),
                    'Description': f"缓放段功率{f['TailMean']:.3f}kW，{direction_str}参考{ref_tail:.3f}kW超过{dev_ratio:.0%}，疑似锁闭/开闭器异常",
                    'Value': f['TailMean'], 'Reference': ref_tail
                })

    # R9: 锁闭段异常
    if t9 and t9.get('enabled', True):
        dev_ratio = t9.get('deviationRatio', 0.3)
        ref_lock = baseline.get('RefLockMean', 0)
        if f['LockMean'] == 0.0 and f['ActiveEnd'] > 50:
            results.append({
                'RuleId': 'R9', 'RuleName': '锁闭段异常', 'Level': t9.get('level', '预警'),
                'Description': '锁闭段缺失（转换与缓放之间无卸载凹口），疑似锁闭机构卡滞/开闭器异常',
                'Value': 0.0, 'Reference': ref_lock
            })
        elif ref_lock > 0:
            deviation = abs(f['LockMean'] - ref_lock) / ref_lock
            if deviation > dev_ratio:
                direction_str = '偏高' if f['LockMean'] > ref_lock else '偏低'
                results.append({
                    'RuleId': 'R9', 'RuleName': '锁闭段异常', 'Level': t9.get('level', '预警'),
                    'Description': f"锁闭段功率{f['LockMean']:.3f}kW，{direction_str}参考{ref_lock:.3f}kW超过{dev_ratio:.0%}，疑似锁闭机构卡滞/开闭器异常",
                    'Value': f['LockMean'], 'Reference': ref_lock
                })
            # 额外判据：锁闭段显著高于转换段（凹口消失）
            elif f['ConvMean'] > 0 and f['LockMean'] > f['ConvMean'] * 1.2:
                results.append({
                    'RuleId': 'R9', 'RuleName': '锁闭段异常', 'Level': t9.get('level', '预警'),
                    'Description': f"锁闭段功率{f['LockMean']:.3f}kW，显著高于转换段{f['ConvMean']:.3f}kW（凹口消失），疑似锁闭机构卡滞",
                    'Value': f['LockMean'], 'Reference': f['ConvMean']
                })

    return results, overall_level(results)


def main():
    print('=' * 70)
    print('重跑全部诊断 (R0-R9) — 对齐 C# DiagnosisEngine')
    print('=' * 70)

    # 加载配置
    baselines_data = load_json(BASELINES_PATH)
    baselines_switches = baselines_data.get('Switches', {})

    thresholds_data = load_json(THRESHOLDS_PATH)
    thresholds = thresholds_data.get('rules', {})

    print(f'已加载 {len(baselines_switches)} 条基线, {len(thresholds)} 条规则阈值')

    # 遍历所有道岔
    switches = sorted(
        s for s in os.listdir(PARSED_DIR)
        if os.path.isdir(os.path.join(PARSED_DIR, s)) and s != 'panyu'
    )
    print(f'发现 {len(switches)} 个道岔: {switches}')

    grand_total = 0
    grand_abnormal = 0
    rule_counts = defaultdict(int)

    for sw_id in switches:
        sw_dir = os.path.join(PARSED_DIR, sw_id)
        # 按日期处理
        day_files = sorted(
            f for f in os.listdir(sw_dir)
            if f.endswith('.json') and '.diag' not in f
            and 'features' not in f.lower()
        )

        sw_total = 0
        sw_abnormal = 0

        for day_file in day_files:
            fpath = os.path.join(sw_dir, day_file)
            try:
                with open(fpath, 'r', encoding='utf-8-sig') as fh:
                    events = json.load(fh)
            except Exception as e:
                print(f'  [跳过] {sw_id}/{day_file} 读取失败: {e}')
                continue

            diagnoses = []

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
                f = extract_features(values)

                # 获取对应方向基线
                key = f'{sw_id}|{direction}'
                baseline = baselines_switches.get(key)

                # 诊断
                results, level = diagnose(f, baseline, thresholds)

                sw_total += 1
                grand_total += 1
                if level != '正常':
                    sw_abnormal += 1
                    grand_abnormal += 1

                for r in results:
                    rule_counts[r['RuleId']] += 1

                diagnoses.append({
                    'Timestamp': evt['Timestamp'],
                    'Level': level,
                    'Results': results,
                })

            # 写入 .diag.json（覆盖）
            diag_path = os.path.join(sw_dir, day_file.replace('.json', '.diag.json'))
            with open(diag_path, 'w', encoding='utf-8') as fh:
                json.dump(diagnoses, fh, ensure_ascii=False)

        print(f'  {sw_id}: {sw_total} 事件, {sw_abnormal} 异常 ({sw_abnormal/sw_total*100:.2f}%)' if sw_total else f'  {sw_id}: 无数据')

    print()
    print('=' * 70)
    print(f'总计: {grand_total} 事件, {grand_abnormal} 异常 ({grand_abnormal/grand_total*100:.2f}%)' if grand_total else '无数据')
    print('规则触发统计:')
    for rid in sorted(rule_counts.keys()):
        print(f'  {rid}: {rule_counts[rid]} 条')
    print()
    print('全部 .diag.json 已覆盖写入。')


if __name__ == '__main__':
    main()
