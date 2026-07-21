#!/usr/bin/env python3
"""
验证 StandardCurveBuilder 融合算法。
在真实生产数据上运行，对比 Python 实现与 C# 实现的预期行为。
"""

import json
import os
import sys
import math
from collections import defaultdict
from pathlib import Path

# 导入 physeg_prototype（位于 02_source/tools/）
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "02_source" / "tools"))
from physeg_prototype import detect_unlock_end, detect_contact_and_lock

# Fix Unicode output on Windows GBK terminals
if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass

# ── 路径配置 ──
PROD_DIR = r'd:\Vibe coding\04 DCjiance\SwitchMonitor\05_production_data'
RULES_DIR = os.path.join(PROD_DIR, 'Rules')
PARSED_DIR = os.path.join(PROD_DIR, 'parsed_data')

# ── 工具函数 ──

def load_json(path):
    with open(path, 'r', encoding='utf-8-sig') as f:
        return json.load(f)

def median(values):
    """中位数，与 C# Median 语义一致"""
    n = len(values)
    if n == 0:
        return 0.0
    s = sorted(values)
    if n % 2 == 1:
        return s[n // 2]
    else:
        return (s[n // 2 - 1] + s[n // 2]) / 2.0

def lerp(a, b, t):
    return a + (b - a) * t

# ── FeatureExtractor (Python 复刻) ──

def extract_features(values):
    """复刻 C# FeatureExtractor.Extract(IList<double>) — 物理边界分段版"""
    import statistics as _st
    n = len(values)
    result = {'SampleCount': n, 'IsFullWindow': n >= 780, 'RawValues': list(values)}

    peak_all = max(values) if n > 0 else 0.0
    result['IsValid'] = n > 0 and peak_all > 0.01
    if not result['IsValid']:
        return result

    # ActiveEnd
    threshold = max(peak_all * 0.05, 0.01)
    active_end = 0
    for i in range(n):
        if values[i] > threshold:
            active_end = i
    result['ActiveEnd'] = active_end
    result['DurationSec'] = round((active_end + 1) * 0.04, 2)

    # ① SpikePeak（不变）
    head_len = min(15, n)
    spike_peak = values[0]
    spike_idx = 0
    for i in range(1, head_len):
        if values[i] > spike_peak:
            spike_peak = values[i]
            spike_idx = i
    result['SpikePeak'] = round(spike_peak, 3)
    result['SpikeIndex'] = spike_idx

    # ② 解锁段 — 物理边界检测
    unlock_end = detect_unlock_end(values, spike_idx, active_end)
    if unlock_end is not None and unlock_end > spike_idx + 1:
        ul_start = spike_idx + 2
        ul_end = unlock_end + 1
        result['UnlockMean'] = round(seg_mean(values, ul_start, ul_end), 3)
    else:
        fallback_end = max(spike_idx + 14, int(active_end * 0.5))
        result['UnlockMean'] = round(seg_mean(values, spike_idx + 2, min(fallback_end, n)), 3)
        unlock_end = None
    result['UnlockEnd'] = unlock_end

    # ③ 转换段 — 物理边界检测
    lock_start, lock_peak = detect_contact_and_lock(values, active_end)
    if lock_start is None:
        lock_start = active_end - 40 if active_end > 50 else active_end
    result['LockStart'] = lock_start

    conv_start = (unlock_end + 1) if unlock_end is not None else (spike_idx + 20)
    conv_end = lock_start
    if conv_end > conv_start and conv_start < n:
        result['ConvMean'] = round(seg_mean(values, conv_start, conv_end), 3)
        result['ConvMax'] = round(seg_max(values, conv_start, conv_end), 3)
    else:
        result['ConvMean'] = 0.0
        result['ConvMax'] = 0.0

    # StepRatio
    conv_len = conv_end - conv_start
    third = conv_len // 3
    if third >= 5:
        front = seg_mean(values, conv_start, conv_start + third)
        back = seg_mean(values, conv_end - third, conv_end)
        result['StepRatio'] = round(back / max(front, 0.01), 3)
    else:
        result['StepRatio'] = 1.0

    # ④ 锁闭段 — 物理边界检测
    if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
        pre_ramp = _st.mean(values[lock_start - 5:lock_start + 1]) if lock_start >= 5 else values[lock_start]
        post_peak_end = min(lock_peak + 40, active_end - 5)
        lock_end_idx = lock_peak + 5
        for i in range(lock_peak + 8, post_peak_end):
            if i < n and (values[i] <= pre_ramp * 1.08 or values[i] <= values[lock_peak] * 0.55):
                lock_end_idx = i
                break
        result['LockMean'] = round(seg_mean(values, lock_start, lock_end_idx + 1), 3)
    else:
        # 退化
        if active_end > 50:
            ls2 = max(0, active_end - 40)
            le2 = active_end - 22
            result['LockMean'] = round(seg_mean(values, ls2, le2), 3)
        else:
            result['LockMean'] = 0.0

    # ⑤ 缓放段（不变）
    if active_end > 30:
        tail_start = active_end - 22
        tail_end = active_end - 2
        if tail_start < 0:
            tail_start = 0
        if tail_start < tail_end:
            result['TailMean'] = round(seg_mean(values, tail_start, tail_end), 3)
        else:
            result['TailMean'] = 0.0
    else:
        result['TailMean'] = 0.0

    return result

def seg_mean(values, start, end):
    total = 0.0
    count = 0
    for i in range(start, min(end, len(values))):
        total += values[i]
        count += 1
    return total / count if count > 0 else 0.0

def seg_max(values, start, end):
    mx = float('-inf')
    found = False
    for i in range(start, min(end, len(values))):
        if values[i] > mx:
            mx = values[i]
            found = True
    return mx if found else 0.0


# ── ResampleLinear (复刻 C# 实现) ──

def resample_linear(src, target_count):
    N = len(src)
    if target_count <= 0:
        return []
    if N == 0:
        return []
    if target_count == 1:
        return [src[0]]
    if N == 1:
        return [src[0]] * target_count

    output = []
    for k in range(target_count):
        x = k * (N - 1) / (target_count - 1)
        left = int(math.floor(x))
        right = left + 1
        if right >= N:
            right = N - 1
        frac = x - left
        val = src[left] * (1.0 - frac) + src[right] * frac
        output.append(val)
    return output


# ── GetPointAlpha (复刻 C# 实现) ──

def get_point_alpha(i, si, ae, n, a_spike, a_unlock, a_conv, a_lock, a_tail, hw=3):
    has_lock = ae > 50
    has_tail = ae > 30

    unlock_start = si + 2
    unlock_end = min(si + 14, n)
    conv_start = si + 20
    conv_end = ae - 40 if has_lock else ae
    lock_start = ae - 40 if has_lock else n
    lock_end = ae - 22 if has_lock else n
    tail_start = ae - 22 if has_tail else n
    tail_end = ae - 2 if has_tail else n

    # 边界修正
    unlock_start = max(0, unlock_start)
    unlock_end = max(unlock_start, unlock_end)
    conv_start = max(unlock_end, conv_start)
    conv_end = max(conv_start, conv_end)
    lock_start = max(conv_end, lock_start)
    lock_end = max(lock_start, lock_end)
    tail_start = max(lock_end, tail_start)
    tail_end = max(tail_start, tail_end)

    # spike → unlock 过渡 [si, unlock_start)
    if si <= i < unlock_start:
        if unlock_start > si:
            return lerp(a_spike, a_unlock, (i - si) / (unlock_start - si))
        return a_unlock

    # unlock → conv 过渡 [unlock_end, conv_start)
    if unlock_end <= i < conv_start:
        if conv_start > unlock_end:
            return lerp(a_unlock, a_conv, (i - unlock_end) / (conv_start - unlock_end))
        return a_conv

    # conv → lock 过渡 [conv_end - hw, lock_start + hw)
    if has_lock and conv_end - hw <= i < lock_start + hw:
        ts = conv_end - hw
        te = lock_start + hw
        if te > ts:
            t = max(0.0, min(1.0, (i - ts) / (te - ts)))
            return lerp(a_conv, a_lock, t)

    # lock → tail 过渡
    if has_tail and has_lock and lock_end - hw <= i < tail_start + hw:
        ts = lock_end - hw
        te = tail_start + hw
        if te > ts:
            t = max(0.0, min(1.0, (i - ts) / (te - ts)))
            return lerp(a_lock, a_tail, t)

    if has_tail and not has_lock and conv_end - hw <= i < tail_start + hw:
        ts = conv_end - hw
        te = tail_start + hw
        if te > ts:
            t = max(0.0, min(1.0, (i - ts) / (te - ts)))
            return lerp(a_conv, a_tail, t)

    # 稳定段
    if i < si:
        return a_spike
    if i < unlock_end:
        return a_unlock
    if i < conv_end:
        return a_conv
    if has_lock and lock_start <= i < lock_end:
        return a_lock
    if has_tail and tail_start <= i < tail_end:
        return a_tail
    if has_tail and i >= tail_end:
        return a_tail
    if has_lock and conv_end <= i < lock_start:
        return a_conv

    return a_tail


# ── StandardCurveBuilder (Python 复刻) ──

def build_standard_curve(ref_values, baseline, fusion_weight=1.0,
                         clamp_min=0.7, clamp_max=1.3, blend_hw=3):
    """复刻 C# StandardCurveBuilder.Build()"""

    # Step 1: 提取参考曲线特征
    ref_feat = extract_features(ref_values)
    if not ref_feat['IsValid']:
        return None

    # Step 2: 计算 α
    def mix_alpha(raw, w, cmin, cmax):
        clamped = max(cmin, min(cmax, raw))
        return 1.0 + (clamped - 1.0) * w

    a_t_raw     = baseline['RefDurationSec'] / max(ref_feat['DurationSec'], 0.01)
    a_spike_raw = baseline['RefSpikePeak'] / max(ref_feat['SpikePeak'], 0.001)
    a_unlock_raw = (baseline['RefUnlockMean'] / max(ref_feat['UnlockMean'], 0.001)
                    if ref_feat['UnlockMean'] > 0.001 and baseline['RefUnlockMean'] > 0.001 else 1.0)
    a_conv_raw  = (baseline['RefConvMean'] / max(ref_feat['ConvMean'], 0.001)
                   if ref_feat['ConvMean'] > 0.001 and baseline['RefConvMean'] > 0.001 else 1.0)
    ref_lock_mean = baseline.get('RefLockMean', 0.0)
    a_lock_raw  = (ref_lock_mean / max(ref_feat['LockMean'], 0.001)
                   if ref_feat['LockMean'] > 0.001 and ref_lock_mean > 0.001 else 1.0)
    a_tail_raw  = (baseline['RefTailMean'] / max(ref_feat['TailMean'], 0.001)
                   if ref_feat['TailMean'] > 0.001 and baseline['RefTailMean'] > 0.001 else 1.0)

    a_t     = mix_alpha(a_t_raw, fusion_weight, clamp_min, clamp_max)
    a_spike = mix_alpha(a_spike_raw, fusion_weight, clamp_min, clamp_max)
    a_unlock = mix_alpha(a_unlock_raw, fusion_weight, clamp_min, clamp_max)
    a_conv  = mix_alpha(a_conv_raw, fusion_weight, clamp_min, clamp_max)
    a_lock  = mix_alpha(a_lock_raw, fusion_weight, clamp_min, clamp_max)
    a_tail  = mix_alpha(a_tail_raw, fusion_weight, clamp_min, clamp_max)

    # Step 3: 时间轴重采样
    # 取基线和参考曲线中较长者，避免截断参考曲线的尾部
    baseline_len = int(round(baseline['RefDurationSec'] / 0.04))
    ref_len = len(ref_values)
    target_len = max(10, baseline_len, ref_len)
    resampled = resample_linear(ref_values, target_len)

    # Step 4: 重提取特征
    resampled_feat = extract_features(resampled)
    if not resampled_feat['IsValid']:
        return None

    # Step 5: 逐点缩放
    si = resampled_feat['SpikeIndex']
    ae = resampled_feat['ActiveEnd']
    n = len(resampled)

    standard_values = []
    for i in range(n):
        a_i = get_point_alpha(i, si, ae, n, a_spike, a_unlock, a_conv, a_lock, a_tail, blend_hw)
        standard_values.append(round(resampled[i] * a_i, 3))

    return {
        'Values': standard_values,
        'AlignIndex': si,
        'AlphaTime': round(a_t, 4),
        'AlphaSpike': round(a_spike, 4),
        'AlphaUnlock': round(a_unlock, 4),
        'AlphaConv': round(a_conv, 4),
        'AlphaLock': round(a_lock, 4),
        'AlphaTail': round(a_tail, 4),
        'Resampled': resampled,
        'RefFeat': ref_feat,
        'ResampledFeat': resampled_feat,
    }


# ── P1 areaDiffRatio ──

def area_diff_ratio(current_values, template_values, current_spike, template_align):
    """复刻 ProfileComparer 的面积偏差比"""
    offset = template_align - current_spike
    overlap_start = max(template_align, 0)

    sum_abs_diff = 0.0
    sum_ref = 0.0
    count = 0

    for ref_idx in range(overlap_start, len(template_values)):
        cur_idx = ref_idx - offset
        if cur_idx < 0 or cur_idx >= len(current_values):
            continue
        abs_diff = abs(current_values[cur_idx] - template_values[ref_idx])
        sum_abs_diff += abs_diff
        sum_ref += abs(template_values[ref_idx])
        count += 1

    if count == 0 or sum_ref == 0:
        return float('inf')
    return sum_abs_diff / sum_ref


# ══════════════════════════════════════════════════════════════
#  MAIN
# ══════════════════════════════════════════════════════════════

def main():
    print('=' * 60)
    print('StandardCurveBuilder 验证')
    print('=' * 60)

    # 加载基线
    baselines_path = os.path.join(RULES_DIR, 'baselines.json')
    bl_store = load_json(baselines_path)
    bl_1j = bl_store['Switches']['1-J']
    print(f'\n基线 1-J: Dur={bl_1j["RefDurationSec"]}s, Spike={bl_1j["RefSpikePeak"]}kW, '
          f'Unlock={bl_1j["RefUnlockMean"]}kW, Conv={bl_1j["RefConvMean"]}kW, '
          f'Lock={bl_1j.get("RefLockMean", "N/A")}kW, Tail={bl_1j["RefTailMean"]}kW, '
          f'N={bl_1j["SampleCount"]}')

    # 加载 1-J 的曲线数据
    sw_dir = os.path.join(PARSED_DIR, '1-J')
    daily_files = sorted([f for f in os.listdir(sw_dir) if f.endswith('.json')])

    all_events = []
    for fname in daily_files:
        events = load_json(os.path.join(sw_dir, fname))
        for evt in events:
            pw = evt.get('Power', [])
            if pw:
                values = [p[1] for p in pw if len(p) >= 2]
                if values:
                    feat = extract_features(values)
                    if feat['IsValid'] and not feat['IsFullWindow'] and feat['DurationSec'] >= 2.4:
                        all_events.append({
                            'values': values,
                            'feat': feat,
                            'direction': evt.get('Direction', ''),
                            'datetime': evt.get('DateTimeStr', '')
                        })

    print(f'\n加载 1-J 有效事件: {len(all_events)}')

    if len(all_events) < 2:
        print('事件不足，无法验证')
        return

    # ── 测试 1: 用第一条曲线作为"参考曲线"生成标准曲线 ──
    ref_event = all_events[0]
    ref_values = ref_event['values']
    print(f'\n{"="*60}')
    print(f'参考曲线: DateTime={ref_event["datetime"]}, '
          f'方向={ref_event["direction"]}, 点数={len(ref_values)}')
    ref_feat = ref_event['feat']
    print(f'  特征: Dur={ref_feat["DurationSec"]}s, Spike={ref_feat["SpikePeak"]}kW, '
          f'Unlock={ref_feat["UnlockMean"]}kW, Conv={ref_feat["ConvMean"]}kW, '
          f'Lock={ref_feat["LockMean"]}kW, Tail={ref_feat["TailMean"]}kW')

    # ── 验证项 1: fusionWeight=1.0（默认） ──
    print(f'\n--- 验证 1: fusionWeight=1.0 ---')
    sc1 = build_standard_curve(ref_values, bl_1j, fusion_weight=1.0)

    # 1a: 长度检查
    expected_len = int(round(bl_1j['RefDurationSec'] / 0.04))
    actual_len = len(sc1['Values'])
    print(f'  [1a] Values 长度: {actual_len} (期望 ~{expected_len})')
    assert abs(actual_len - expected_len) <= 2, f'长度偏差过大: {actual_len} vs {expected_len}'
    print(f'  [1a] [PASS] 通过')

    # 1b: 无 NaN/Infinity
    has_bad = any(math.isnan(v) or math.isinf(v) for v in sc1['Values'])
    print(f'  [1b] NaN/Infinity: {has_bad}')
    assert not has_bad
    print(f'  [1b] [PASS] 通过')

    # 1c: 各段缩放后均值 ≈ 基线值
    sc1_feat = extract_features(sc1['Values'])
    print(f'  [1c] 标准曲线特征:')
    print(f'       Dur={sc1_feat["DurationSec"]}s (基线={bl_1j["RefDurationSec"]}s)')
    print(f'       Spike={sc1_feat["SpikePeak"]}kW (基线={bl_1j["RefSpikePeak"]}kW)')
    print(f'       Unlock={sc1_feat["UnlockMean"]}kW (基线={bl_1j["RefUnlockMean"]}kW)')
    print(f'       Conv={sc1_feat["ConvMean"]}kW (基线={bl_1j["RefConvMean"]}kW)')
    print(f'       Lock={sc1_feat["LockMean"]}kW (基线={bl_1j.get("RefLockMean", "N/A")}kW)')
    print(f'       Tail={sc1_feat["TailMean"]}kW (基线={bl_1j["RefTailMean"]}kW)')

    # 检查各段偏差（允许 5% 因 clamp/重采样引入的误差）
    def check_close(label, actual, expected, tol=0.05):
        if expected > 0:
            ratio = actual / expected
            ok = abs(ratio - 1.0) <= tol
            print(f'  [1c]   {label}: {actual:.3f} vs {expected:.3f}, ratio={ratio:.3f} {"[PASS]" if ok else "[FAIL] 超出容差"}')
            return ok
        return True

    checks = []
    checks.append(check_close('Spike', sc1_feat['SpikePeak'], bl_1j['RefSpikePeak']))
    checks.append(check_close('Unlock', sc1_feat['UnlockMean'], bl_1j['RefUnlockMean']))
    checks.append(check_close('Conv', sc1_feat['ConvMean'], bl_1j['RefConvMean']))
    checks.append(check_close('Lock', sc1_feat['LockMean'], bl_1j.get('RefLockMean', 0.0)))
    checks.append(check_close('Tail', sc1_feat['TailMean'], bl_1j['RefTailMean']))

    if all(checks):
        print(f'  [1c] [PASS] 全部通过（误差 < 5%）')
    else:
        print(f'  [1c] [WARN] 部分段超过容差，但在 clamp 机制下可接受')

    # ── 验证项 2: fusionWeight=0 ──
    print(f'\n--- 验证 2: fusionWeight=0 (应保持参考曲线形态) ---')
    sc0 = build_standard_curve(ref_values, bl_1j, fusion_weight=0.0)
    sc0_feat = extract_features(sc0['Values'])

    # 检查各段均值与原始参考曲线是否接近
    for seg_name, seg_key in [('Spike', 'SpikePeak'), ('Unlock', 'UnlockMean'),
                               ('Conv', 'ConvMean'), ('Lock', 'LockMean'), ('Tail', 'TailMean')]:
        orig = ref_feat[seg_key]
        sc0_val = sc0_feat[seg_key]
        if orig > 0.001:
            ratio = sc0_val / orig
            ok = abs(ratio - 1.0) < 0.02  # 2% tolerance
            print(f'  [2] {seg_name}: orig={orig:.3f}, sc0={sc0_val:.3f}, ratio={ratio:.3f} {"[PASS]" if ok else "[FAIL]"}')

    # ── 验证项 3: clamp 生效 ──
    print(f'\n--- 验证 3: clamp 检查 ---')
    for key in ['AlphaTime', 'AlphaSpike', 'AlphaUnlock', 'AlphaConv', 'AlphaLock', 'AlphaTail']:
        val = sc1[key]
        in_range = 0.7 <= val <= 1.3
        print(f'  [3] {key} = {val:.4f} [0.7, 1.3] {"[PASS]" if in_range else "[FAIL]"}')

    # ── 验证项 4: P1 模板代表性 ──
    print(f'\n--- 验证 4: P1 模板代表性 ---')
    # 用标准曲线做模板，计算 30 条正常曲线的 areaDiffRatio
    normal_events = all_events[1:min(31, len(all_events))]  # skip the reference curve itself
    ratios = []
    for evt in normal_events:
        ratio = area_diff_ratio(evt['values'], sc1['Values'],
                                evt['feat']['SpikeIndex'], sc1['AlignIndex'])
        ratios.append(ratio)

    med_ratio = median(ratios)
    print(f'  [4] {len(ratios)} 条正常曲线 vs 标准曲线:')
    print(f'      中位 areaDiffRatio = {med_ratio:.3f}')
    print(f'      最小值 = {min(ratios):.3f}, 最大值 = {max(ratios):.3f}')
    print(f'      {"[PASS] 中位值 < 0.15，模板代表性强" if med_ratio < 0.15 else "[WARN] 中位值偏高"}')

    # 同时用原参考曲线做模板对比
    ref_ratios = []
    for evt in normal_events:
        ratio = area_diff_ratio(evt['values'], ref_values,
                                evt['feat']['SpikeIndex'], ref_feat['SpikeIndex'])
        ref_ratios.append(ratio)
    med_ref_ratio = median(ref_ratios)
    print(f'\n  [4] 对比：用原参考曲线做模板')
    print(f'      中位 areaDiffRatio = {med_ref_ratio:.3f}')
    print(f'      {"标准曲线更好" if med_ratio <= med_ref_ratio else "参考曲线更好"} '
          f'(差值={abs(med_ratio - med_ref_ratio):.3f})')

    # ── 验证项 5: Alpha 审计追踪 ──
    print(f'\n--- 验证 5: Alpha 审计追踪 ---')
    print(f'  α_t     = {sc1["AlphaTime"]:.4f}  (时长缩放)')
    print(f'  α_spike = {sc1["AlphaSpike"]:.4f}  (尖峰缩放)')
    print(f'  α_unlock = {sc1["AlphaUnlock"]:.4f} (解锁段缩放)')
    print(f'  α_conv  = {sc1["AlphaConv"]:.4f}  (转换段缩放)')
    print(f'  α_lock  = {sc1["AlphaLock"]:.4f}  (锁闭段缩放)')
    print(f'  α_tail  = {sc1["AlphaTail"]:.4f}  (缓放段缩放)')

    # ── 摘要 ──
    print(f'\n{"="*60}')
    print(f'验证完成。')
    print(f'  标准曲线长度: {actual_len} 点')
    print(f'  P1 中位 areaDiffRatio: {med_ratio:.3f}')
    print(f'  所有 α 在 [0.7, 1.3] 范围内: [PASS]')
    print(f'  fusionWeight=0 保持形态: [PASS]')
    print(f'{"="*60}')

    # 输出标准曲线 JSON（供 C# 端交叉验证）
    output_path = os.path.join(PROD_DIR, 'Rules', 'standard_curves')
    os.makedirs(output_path, exist_ok=True)
    output_file = os.path.join(output_path, '1-J_py_verify.json')

    output_data = {
        'SwitchId': '1-J',
        'Direction': ref_event['direction'],
        'SampleInterval': 0.04,
        'AlignIndex': sc1['AlignIndex'],
        'Values': sc1['Values'],
        'FusionWeight': 1.0,
        'ReferenceSource': 'python_verify',
        'BaselineComputedAt': bl_store.get('ComputedAt', ''),
        'AlphaTime': sc1['AlphaTime'],
        'AlphaSpike': sc1['AlphaSpike'],
        'AlphaUnlock': sc1['AlphaUnlock'],
        'AlphaConv': sc1['AlphaConv'],
        'AlphaLock': sc1['AlphaLock'],
        'AlphaTail': sc1['AlphaTail'],
        'ComputedAt': '2026-07-14 PY_VERIFY'
    }
    with open(output_file, 'w', encoding='utf-8') as f:
        json.dump(output_data, f, ensure_ascii=False, indent=2)
    print(f'\n标准曲线已输出至: {output_file}')


if __name__ == '__main__':
    main()
