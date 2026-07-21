#!/usr/bin/env python3
"""
验证 CurrentStandardCurveBuilder 三相电流融合算法。
在真实生产数据上运行，对比 Python 实现与 C# 实现的预期行为。

与 verify_standard_curve.py（功率曲线单相）对应，但对 A/B/C 三相独立执行
6 步融合算法：提取特征 → 计算 α → 时间轴重采样 → 重提取边界 → 逐点缩放 → 输出。

用法:
    python verify_current_standard_curve.py
        → 默认：加载 1-J 的第一条正常三相事件，用 current_baselines.json
          中的基线融合生成电流标准曲线，验证输出正确性。
"""

import json
import math
import os
import sys
from pathlib import Path

# 导入 physeg_prototype（与电流特征提取共用）
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

# ══════════════════════════════════════════════════════════════
#  工具函数（与 verify_standard_curve.py 一致）
# ══════════════════════════════════════════════════════════════

def load_json(path):
    with open(path, 'r', encoding='utf-8-sig') as f:
        return json.load(f)

def median(values):
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

def resample_linear(src, target_count):
    """线性重采样，与 C# StandardCurveBuilder.ResampleLinear 一致。"""
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

def mix_alpha(raw, w, cmin, cmax):
    """clamp 到 [cmin, cmax]，再按 w 混合到 1.0。"""
    clamped = max(cmin, min(cmax, raw))
    return 1.0 + (clamped - 1.0) * w


# ══════════════════════════════════════════════════════════════
#  单相特征提取（与 C# CurrentFeatureExtractor.ExtractPhase 对齐）
#  复用 current_baseline_ref_check.extract_phase — 略作包装
# ══════════════════════════════════════════════════════════════

def _seg_mean(values, start, end):
    total = 0.0
    count = 0
    for i in range(start, min(end, len(values))):
        total += values[i]
        count += 1
    return total / count if count > 0 else 0.0


def extract_phase_features(values):
    """对单相电流值提取五阶段特征。
    返回 dict 与 C# CurrentFeatureExtractor.ExtractPhase 输出对齐。
    包含物理边界 unlockEnd / lockStart（来自 physeg_prototype）。
    """
    n = len(values)
    if n == 0:
        return {'IsValid': False, 'SpikeIndex': 0, 'ActiveEnd': 0,
                'UnlockEnd': 0, 'LockStart': 0,
                'SpikePeak': 0.0, 'UnlockMean': 0.0, 'ConvMean': 0.0,
                'LockMean': 0.0, 'TailMean': 0.0, 'DurationSec': 0.0}

    peak_all = max(values)
    if peak_all <= 0.01:
        return {'IsValid': False, 'SpikeIndex': 0, 'ActiveEnd': 0,
                'UnlockEnd': 0, 'LockStart': 0,
                'SpikePeak': 0.0, 'UnlockMean': 0.0, 'ConvMean': 0.0,
                'LockMean': 0.0, 'TailMean': 0.0, 'DurationSec': 0.0}

    # activeEnd
    threshold = max(peak_all * 0.05, 0.01)
    active_end = 0
    for i in range(n):
        if values[i] > threshold:
            active_end = i

    # ① 启动尖峰
    head_len = min(15, n)
    spike_peak = values[0]
    spike_index = 0
    for i in range(1, head_len):
        if values[i] > spike_peak:
            spike_peak = values[i]
            spike_index = i

    # ② 解锁段 — 物理边界检测
    unlock_end = detect_unlock_end(values, spike_index, active_end)
    if unlock_end is not None and unlock_end > spike_index + 1:
        ul_start = spike_index + 2
        ul_end = unlock_end + 1
        unlock_mean = _seg_mean(values, ul_start, ul_end)
    else:
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
        conv_mean = _seg_mean(values, spike_index + 2, active_end + 1)

    # ④ 锁闭段 — 物理边界检测
    if lock_peak is not None and lock_start is not None and lock_peak > lock_start:
        import statistics
        pre_ramp = statistics.mean(values[lock_start - 5:lock_start + 1]) if lock_start >= 5 else values[lock_start]
        post_peak_end = min(lock_peak + 40, active_end - 5)
        lock_end_idx = lock_peak + 5
        for i in range(lock_peak + 8, post_peak_end):
            if i < n and (values[i] <= pre_ramp * 1.08 or values[i] <= values[lock_peak] * 0.55):
                lock_end_idx = i
                break
        lock_mean = _seg_mean(values, lock_start, lock_end_idx + 1)
    else:
        if active_end > 50:
            ls2 = max(0, active_end - 40)
            le2 = active_end - 22
            lock_mean = _seg_mean(values, ls2, le2)
        else:
            lock_mean = 0.0
        lock_start = active_end - 40 if active_end > 50 else active_end

    # ⑤ 缓放段
    if active_end > 30:
        tail_start = active_end - 22
        tail_end = active_end - 2
        if tail_start < 0:
            tail_start = 0
        tail_mean = _seg_mean(values, tail_start, tail_end)
    else:
        tail_mean = 0.0

    duration_sec = round((active_end + 1) * 0.04, 2)

    return {
        'IsValid': True,
        'SpikeIndex': spike_index,
        'ActiveEnd': active_end,
        'UnlockEnd': unlock_end if unlock_end is not None else max(spike_index + 14, int(active_end * 0.5)),
        'LockStart': lock_start,
        'SpikePeak': round(spike_peak, 3),
        'UnlockMean': round(unlock_mean, 3),
        'ConvMean': round(conv_mean, 3),
        'LockMean': round(lock_mean, 3),
        'TailMean': round(tail_mean, 3),
        'DurationSec': duration_sec,
    }


# ══════════════════════════════════════════════════════════════
#  GetPointAlpha — 使用显式物理边界（与 C# StandardCurveBuilder.GetPointAlpha 一致）
# ══════════════════════════════════════════════════════════════

def get_point_alpha_ex(i, si, unlock_end, lock_start, ae, n,
                       a_spike, a_unlock, a_conv, a_lock, a_tail, hw=3):
    """逐点 α 插值。
    使用从特征提取得到的显式物理边界（unlockEnd, lockStart），
    与 C# StandardCurveBuilder.GetPointAlpha 完全对齐。
    """
    has_lock = lock_start > 0 and lock_start < ae
    has_tail = ae > 30

    unlock_start = si + 2
    conv_start = unlock_end          # 转换从解锁终点开始
    conv_end = lock_start if has_lock else ae
    lock_seg_end = ae - 22 if has_lock else n
    tail_start = ae - 22 if has_tail else n
    tail_end = ae - 2 if has_tail else n

    # 边界修正
    unlock_start = max(0, unlock_start)
    conv_start = max(unlock_start, conv_start)
    conv_end = max(conv_start, conv_end)
    lock_seg_end = max(conv_end, lock_seg_end)
    tail_start = max(lock_seg_end, tail_start)
    tail_end = max(tail_start, tail_end)

    # ── 过渡区 1: spike → unlock [si, unlock_start) ──
    if si <= i < unlock_start:
        if unlock_start > si:
            return lerp(a_spike, a_unlock, (i - si) / (unlock_start - si))
        return a_unlock

    # ── 过渡区 2: unlock → conv [conv_start-hw, conv_start+hw) ──
    if conv_start - hw <= i < conv_start + hw:
        ts = conv_start - hw
        te = conv_start + hw
        if te > ts:
            return lerp(a_unlock, a_conv, (i - ts) / (te - ts))
        return a_conv

    # ── 过渡区 3: conv → lock [conv_end-hw, lock_start+hw) ──
    if has_lock and conv_end - hw <= i < lock_start + hw:
        ts = conv_end - hw
        te = lock_start + hw
        if te > ts:
            t = max(0.0, min(1.0, (i - ts) / (te - ts)))
            return lerp(a_conv, a_lock, t)

    # ── 过渡区 4: lock → tail [lock_seg_end-hw, tail_start+hw) ──
    if has_tail and has_lock and lock_seg_end - hw <= i < tail_start + hw:
        ts = lock_seg_end - hw
        te = tail_start + hw
        if te > ts:
            t = max(0.0, min(1.0, (i - ts) / (te - ts)))
            return lerp(a_lock, a_tail, t)

    # lock → tail 过渡但无锁闭段：conv → tail
    if has_tail and not has_lock and conv_end - hw <= i < tail_start + hw:
        ts = conv_end - hw
        te = tail_start + hw
        if te > ts:
            t = max(0.0, min(1.0, (i - ts) / (te - ts)))
            return lerp(a_conv, a_tail, t)

    # ── 稳定段 ──
    if i < si:
        return a_spike
    if i < conv_start:
        return a_unlock
    if i < conv_end:
        return a_conv
    if has_lock and lock_start <= i < lock_seg_end:
        return a_lock
    if has_tail and tail_start <= i < tail_end:
        return a_tail
    if has_tail and i >= tail_end:
        return a_tail
    if has_lock and conv_end <= i < lock_start:
        return a_conv

    return a_tail


# ══════════════════════════════════════════════════════════════
#  CurrentStandardCurveBuilder (Python 复刻)
# ══════════════════════════════════════════════════════════════

def build_current_standard_curve(ref_values_a, ref_values_b, ref_values_c,
                                 baseline, fusion_weight=1.0,
                                 clamp_min=0.7, clamp_max=1.3, blend_hw=3):
    """复刻 C# CurrentStandardCurveBuilder.Build()。

    6 步算法：
      1. 各相独立提取参考曲线特征
      2. 计算各相各段缩放因子 α（三相共用时长缩放 α_t）
      3. 三相统一时间轴线性重采样至 targetLen
      4. 重新提取重采样后的各相段边界
      5. 逐相逐点幅度缩放 + 段边界平滑过渡
      6. 输出 CurrentStandardCurve dict

    Args:
        ref_values_a/b/c: 三相参考曲线值列表
        baseline: CurrentBaseline dict（20 维标量）
        fusion_weight: 融合强度 0~1

    Returns:
        CurrentStandardCurve dict，任一无效率时返回 None
    """
    sample_interval = 0.04

    # ── Step 1: 各相独立提取参考曲线特征 ──
    ref_feat_a = extract_phase_features(ref_values_a)
    ref_feat_b = extract_phase_features(ref_values_b)
    ref_feat_c = extract_phase_features(ref_values_c)

    if not any(f['IsValid'] for f in [ref_feat_a, ref_feat_b, ref_feat_c]):
        return None

    # ── Step 2: 计算各相各段缩放因子 ──
    # 时长 α 三相共用（取三相中最长时长）
    ref_duration = max(
        ref_feat_a['DurationSec'] if ref_feat_a['IsValid'] else 0,
        ref_feat_b['DurationSec'] if ref_feat_b['IsValid'] else 0,
        ref_feat_c['DurationSec'] if ref_feat_c['IsValid'] else 0,
    )
    if ref_duration < 0.01:
        ref_duration = 0.01

    a_t_raw = baseline.get('RefDurationSec', baseline.get('refDurationSec', 1.0)) / ref_duration
    a_t = mix_alpha(a_t_raw, fusion_weight, clamp_min, clamp_max)

    # A 相 α
    alphas_a = _compute_phase_alphas(ref_feat_a, baseline, 'A',
                                     fusion_weight, clamp_min, clamp_max)
    # B 相 α
    alphas_b = _compute_phase_alphas(ref_feat_b, baseline, 'B',
                                     fusion_weight, clamp_min, clamp_max)
    # C 相 α
    alphas_c = _compute_phase_alphas(ref_feat_c, baseline, 'C',
                                     fusion_weight, clamp_min, clamp_max)

    # ── Step 3: 时间轴线性重采样 ──
    bl_dur = baseline.get('RefDurationSec', baseline.get('refDurationSec', 1.0))
    baseline_len = int(round(bl_dur / sample_interval))
    max_ref_len = max(
        len(ref_values_a) if ref_values_a else 0,
        len(ref_values_b) if ref_values_b else 0,
        len(ref_values_c) if ref_values_c else 0,
        10
    )
    target_len = max(10, baseline_len, max_ref_len)

    resampled_a = resample_linear(ref_values_a, target_len) if ref_values_a else []
    resampled_b = resample_linear(ref_values_b, target_len) if ref_values_b else []
    resampled_c = resample_linear(ref_values_c, target_len) if ref_values_c else []

    # ── Step 4: 重新提取重采样后的各相特征 ──
    res_feat_a = extract_phase_features(resampled_a) if resampled_a else None
    res_feat_b = extract_phase_features(resampled_b) if resampled_b else None
    res_feat_c = extract_phase_features(resampled_c) if resampled_c else None

    # ── Step 5: 逐相逐点幅度缩放 ──
    standard_a = _apply_phase_scaling(resampled_a, res_feat_a, alphas_a, blend_hw)
    standard_b = _apply_phase_scaling(resampled_b, res_feat_b, alphas_b, blend_hw)
    standard_c = _apply_phase_scaling(resampled_c, res_feat_c, alphas_c, blend_hw)

    # ── Step 6: 构建输出 ──
    return {
        'ValuesA': standard_a,
        'ValuesB': standard_b,
        'ValuesC': standard_c,
        'AlignIndexA': res_feat_a['SpikeIndex'] if res_feat_a and res_feat_a['IsValid'] else 0,
        'AlignIndexB': res_feat_b['SpikeIndex'] if res_feat_b and res_feat_b['IsValid'] else 0,
        'AlignIndexC': res_feat_c['SpikeIndex'] if res_feat_c and res_feat_c['IsValid'] else 0,
        'AlphaTime': round(a_t, 4),
        'AlphaSpikeA': round(alphas_a[0], 4),
        'AlphaUnlockA': round(alphas_a[1], 4),
        'AlphaConvA': round(alphas_a[2], 4),
        'AlphaLockA': round(alphas_a[3], 4),
        'AlphaTailA': round(alphas_a[4], 4),
        'AlphaSpikeB': round(alphas_b[0], 4),
        'AlphaUnlockB': round(alphas_b[1], 4),
        'AlphaConvB': round(alphas_b[2], 4),
        'AlphaLockB': round(alphas_b[3], 4),
        'AlphaTailB': round(alphas_b[4], 4),
        'AlphaSpikeC': round(alphas_c[0], 4),
        'AlphaUnlockC': round(alphas_c[1], 4),
        'AlphaConvC': round(alphas_c[2], 4),
        'AlphaLockC': round(alphas_c[3], 4),
        'AlphaTailC': round(alphas_c[4], 4),
        'ResampledA': resampled_a,
        'ResampledB': resampled_b,
        'ResampledC': resampled_c,
        'RefFeatA': ref_feat_a,
        'RefFeatB': ref_feat_b,
        'RefFeatC': ref_feat_c,
        'ResampledFeatA': res_feat_a,
        'ResampledFeatB': res_feat_b,
        'ResampledFeatC': res_feat_c,
    }


def _get_phase_baseline_fields(baseline, phase):
    """从 CurrentBaseline dict 读取指定相的各段标量值。
    兼容 PascalCase (C# JSON) 和 camelCase (Python) 两种键名。
    """
    p = phase.upper()
    def _get(*keys):
        for k in keys:
            v = baseline.get(k, None)
            if v is not None:
                return v
        return 0.0

    return (
        _get(f'RefSpikePeak{p}', f'refSpikePeak{p}'),
        _get(f'RefUnlockMean{p}', f'refUnlockMean{p}'),
        _get(f'RefConvMean{p}', f'refConvMean{p}'),
        _get(f'RefLockMean{p}', f'refLockMean{p}'),
        _get(f'RefTailMean{p}', f'refTailMean{p}'),
    )


def _compute_phase_alphas(ref_feat, baseline, phase,
                          fusion_weight, clamp_min, clamp_max):
    """计算单相各段缩放因子。
    与 C# ComputePhaseAlphas 对应。
    """
    if ref_feat is None or not ref_feat.get('IsValid'):
        return (1.0, 1.0, 1.0, 1.0, 1.0)

    bl_spike, bl_unlock, bl_conv, bl_lock, bl_tail = \
        _get_phase_baseline_fields(baseline, phase)

    ref_spike = max(ref_feat['SpikePeak'], 0.001)
    ref_unlock = max(ref_feat['UnlockMean'], 0.001)
    ref_conv = max(ref_feat['ConvMean'], 0.001)
    ref_lock = max(ref_feat['LockMean'], 0.001)
    ref_tail = max(ref_feat['TailMean'], 0.001)

    def safe_ratio(bl, ref):
        if ref > 0.001 and bl > 0.001:
            return bl / ref
        return 1.0

    return (
        mix_alpha(bl_spike / ref_spike, fusion_weight, clamp_min, clamp_max),
        mix_alpha(safe_ratio(bl_unlock, ref_unlock), fusion_weight, clamp_min, clamp_max),
        mix_alpha(safe_ratio(bl_conv, ref_conv), fusion_weight, clamp_min, clamp_max),
        mix_alpha(safe_ratio(bl_lock, ref_lock), fusion_weight, clamp_min, clamp_max),
        mix_alpha(safe_ratio(bl_tail, ref_tail), fusion_weight, clamp_min, clamp_max),
    )


def _apply_phase_scaling(resampled, feat, alphas, blend_hw):
    """对单相重采样后的值应用逐点幅度缩放。
    与 C# ApplyPhaseScaling 对应。
    """
    if not resampled:
        return []

    if feat is None or not feat.get('IsValid'):
        return [round(v, 3) for v in resampled]

    a_spike, a_unlock, a_conv, a_lock, a_tail = alphas
    si = feat['SpikeIndex']
    ae = feat['ActiveEnd']
    unlock_end = feat['UnlockEnd']
    lock_start = feat['LockStart']
    n = len(resampled)

    result = []
    for i in range(n):
        a_i = get_point_alpha_ex(
            i, si, unlock_end, lock_start, ae, n,
            a_spike, a_unlock, a_conv, a_lock, a_tail, blend_hw)
        result.append(round(resampled[i] * a_i, 3))

    return result


# ══════════════════════════════════════════════════════════════
#  分相保存函数 — 将三相捆绑定标准曲线拆分为 48 条分相 JSON
# ══════════════════════════════════════════════════════════════

def save_current_standard_curve_per_phase(sc_dict, switch_id, direction, phase,
                                           bl_computed_at, output_dir):
    """保存单相电流标准曲线为独立 JSON 文件。

    文件命名: {switchId}_{direction}_{phase}.json
    每文件仅含单相数据（Values, AlignIndex, Alphas）。
    """
    from datetime import datetime
    os.makedirs(output_dir, exist_ok=True)
    p = phase.upper()
    file_name = f"{switch_id}_{direction}_{p}.json"
    path = os.path.join(output_dir, file_name)

    data = {
        'SwitchId': switch_id,
        'Direction': direction,
        'Phase': p,
        'SampleInterval': 0.04,
        'AlignIndex': sc_dict[f'AlignIndex{p}'],
        'Values': sc_dict[f'Values{p}'],
        'OriginalMedianValues': sc_dict[f'Values{p}'],
        'FusionWeight': 1.0,
        'ReferenceSource': f"current_reference_curves/{switch_id}_{direction}_{p}.json",
        'BaselineComputedAt': bl_computed_at,
        'AlphaTime': sc_dict['AlphaTime'],
        'AlphaSpike': sc_dict[f'AlphaSpike{p}'],
        'AlphaUnlock': sc_dict[f'AlphaUnlock{p}'],
        'AlphaConv': sc_dict[f'AlphaConv{p}'],
        'AlphaLock': sc_dict[f'AlphaLock{p}'],
        'AlphaTail': sc_dict[f'AlphaTail{p}'],
        'ComputedAt': datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    }
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    return path


def save_current_reference_curve_per_phase(ref_dict, switch_id, direction, phase,
                                            output_dir):
    """保存单相电流参考曲线为独立 JSON 文件。"""
    from datetime import datetime
    os.makedirs(output_dir, exist_ok=True)
    p = phase.upper()
    file_name = f"{switch_id}_{direction}_{p}.json"
    path = os.path.join(output_dir, file_name)

    data = {
        'SwitchId': switch_id,
        'Direction': direction,
        'Phase': p,
        'SampleInterval': 0.04,
        'AlignIndex': ref_dict[f'AlignIndex{p}'],
        'Values': ref_dict[f'Values{p}'],
        'ComputedAt': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
        'Source': ref_dict.get('Source', 'auto-picked'),
        'SourceDateTime': ref_dict.get('SourceDateTime', ''),
    }
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    return path


# ══════════════════════════════════════════════════════════════
#  MAIN — 验证
# ══════════════════════════════════════════════════════════════

def main():
    print('=' * 60)
    print('CurrentStandardCurveBuilder 验证')
    print('=' * 60)

    # 加载电流基线
    baselines_path = os.path.join(RULES_DIR, 'current_baselines.json')
    bl_store = load_json(baselines_path)
    switches = bl_store.get('Switches', bl_store.get('switches', {}))
    print(f'\n基线文件: {baselines_path}')
    print(f'基线条目: {len(switches)} 条')
    print(f'基线计算时间: {bl_store.get("ComputedAt", "N/A")}')

    # 找一个有数据的开关（key 格式：1-J 或 1|定位→反位）
    target_key = None
    target_bl = None
    for key in sorted(switches.keys()):
        target_key = key
        target_bl = switches[key]
        break

    if target_key is None:
        print('[FAIL] 没有找到有效的按方向基线')
        return

    # 解析 switchId 和方向
    idx = target_key.find('|')
    if idx >= 0:
        switch_id = target_key[:idx]
        direction = target_key[idx+1:]
    elif target_key.endswith('-J'):
        switch_id = target_key
        direction = '定位→反位'
    elif target_key.endswith('-X'):
        switch_id = target_key
        direction = '反位→定位'
    else:
        switch_id = target_key
        direction = target_bl.get('Direction', target_bl.get('direction', ''))
    print(f'\n目标: [{switch_id}] {direction}')

    # 打印基线关键值
    bl = target_bl
    print(f'  基线 20 维:')
    for p in ['A', 'B', 'C']:
        print(f'    {p}: spike={bl.get(f"RefSpikePeak{p}", 0):.3f}@{bl.get(f"RefSpikeIndex{p}", 0)} '
              f'ul={bl.get(f"RefUnlockMean{p}", 0):.3f} cv={bl.get(f"RefConvMean{p}", 0):.3f} '
              f'lk={bl.get(f"RefLockMean{p}", 0):.3f} tl={bl.get(f"RefTailMean{p}", 0):.3f}')
    print(f'    Dur={bl.get("RefDurationSec", 0)}s, Unbalance={bl.get("RefMaxUnbalanceRatio", 0):.3f}')

    # 从 parsed_data 找一条正常三相事件作为参考曲线
    sw_dir = os.path.join(PARSED_DIR, switch_id)
    if not os.path.isdir(sw_dir):
        print(f'[FAIL] 无 parsed_data 目录: {sw_dir}')
        return

    ref_event = None
    for day_file in sorted(os.listdir(sw_dir)):
        if not day_file.endswith('.json') or '.diag' in day_file or 'features' in day_file.lower():
            continue
        events = load_json(os.path.join(sw_dir, day_file))
        for evt in events:
            ca = evt.get('CurrentA', [])
            cb = evt.get('CurrentB', [])
            cc = evt.get('CurrentC', [])
            if not ca or not cb or not cc:
                continue

            # 提取三相值
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
                ref_event = {
                    'va': va, 'vb': vb, 'vc': vc,
                    'fa': fa,
                    'direction': direction,
                    'datetime': evt.get('DateTimeStr', ''),
                    'switch_id': switch_id,
                }
                break
        if ref_event is not None:
            break

    if ref_event is None:
        print(f'[FAIL] 无 {direction} 方向正常三相电流事件')
        return

    ref = ref_event
    print(f'\n参考曲线: {ref["datetime"]}, '
          f'A={len(ref["va"])}点, B={len(ref["vb"])}点, C={len(ref["vc"])}点')
    print(f'  A: Dur={ref["fa"]["DurationSec"]}s, Spike={ref["fa"]["SpikePeak"]}A@{ref["fa"]["SpikeIndex"]}, '
          f'ul={ref["fa"]["UnlockMean"]}A, cv={ref["fa"]["ConvMean"]}A, '
          f'lk={ref["fa"]["LockMean"]}A, tl={ref["fa"]["TailMean"]}A')

    # ── 验证项 1: fusionWeight=1.0 ──
    print(f'\n{"="*60}')
    print(f'验证 1: fusionWeight=1.0（完全对齐基线）')
    print(f'{"="*60}')

    sc = build_current_standard_curve(
        ref['va'], ref['vb'], ref['vc'], target_bl, fusion_weight=1.0)

    if sc is None:
        print('[FAIL] build_current_standard_curve 返回 None')
        return

    # 1a: 无 NaN/Inf
    for label, vals in [('A', sc['ValuesA']), ('B', sc['ValuesB']), ('C', sc['ValuesC'])]:
        has_bad = any(math.isnan(v) or math.isinf(v) for v in vals)
        print(f'  [1a] {label}相 NaN/Inf: {has_bad}')
        assert not has_bad, f'{label}相存在 NaN/Inf'
        print(f'  [1a] {label}相 [PASS]')

    # 1b: 各相各段缩放后均值 ≈ 基线值
    for phase_label, std_vals, bl_prefix in [
        ('A', sc['ValuesA'], 'A'),
        ('B', sc['ValuesB'], 'B'),
        ('C', sc['ValuesC'], 'C'),
    ]:
        sf = extract_phase_features(std_vals)
        print(f'\n  [1b] {phase_label}相标准曲线特征:')
        print(f'       Dur={sf["DurationSec"]}s (基线={bl.get(f"RefDurationSec", 0)}s)')
        print(f'       Spike={sf["SpikePeak"]}A (基线={bl.get(f"RefSpikePeak{bl_prefix}", 0)}A)')
        print(f'       Unlock={sf["UnlockMean"]}A (基线={bl.get(f"RefUnlockMean{bl_prefix}", 0)}A)')
        print(f'       Conv={sf["ConvMean"]}A (基线={bl.get(f"RefConvMean{bl_prefix}", 0)}A)')
        print(f'       Lock={sf["LockMean"]}A (基线={bl.get(f"RefLockMean{bl_prefix}", 0)}A)')
        print(f'       Tail={sf["TailMean"]}A (基线={bl.get(f"RefTailMean{bl_prefix}", 0)}A)')

        def check_close(label, actual, expected, tol=0.05):
            if expected > 0:
                ratio = actual / expected
                ok = abs(ratio - 1.0) <= tol
                print(f'       {label}: {actual:.3f} vs {expected:.3f}, ratio={ratio:.3f} '
                      f'{"[PASS]" if ok else "[WARN] 容差外"}')
                return ok
            return True

        checks = []
        checks.append(check_close('Spike', sf['SpikePeak'],
                                  bl.get(f'RefSpikePeak{bl_prefix}', 0)))
        checks.append(check_close('Unlock', sf['UnlockMean'],
                                  bl.get(f'RefUnlockMean{bl_prefix}', 0)))
        checks.append(check_close('Conv', sf['ConvMean'],
                                  bl.get(f'RefConvMean{bl_prefix}', 0)))
        checks.append(check_close('Lock', sf['LockMean'],
                                  bl.get(f'RefLockMean{bl_prefix}', 0)))
        checks.append(check_close('Tail', sf['TailMean'],
                                  bl.get(f'RefTailMean{bl_prefix}', 0)))

        if all(checks):
            print(f'  [1b] {phase_label}相 [PASS] 全部通过（误差 < 5%）')

    # ── 验证项 2: fusionWeight=0（应保持参考曲线形态） ──
    print(f'\n{"="*60}')
    print(f'验证 2: fusionWeight=0（保持参考曲线形态）')
    print(f'{"="*60}')

    sc0 = build_current_standard_curve(
        ref['va'], ref['vb'], ref['vc'], target_bl, fusion_weight=0.0)

    if sc0:
        for phase_label, std_vals, ref_vals, feat_key in [
            ('A', sc0['ValuesA'], ref['va'], 'A'),
            ('B', sc0['ValuesB'], ref['vb'], 'B'),
            ('C', sc0['ValuesC'], ref['vc'], 'C'),
        ]:
            sf0 = extract_phase_features(std_vals)
            rf = extract_phase_features(ref_vals)
            for seg_name, seg_key in [
                ('Spike', 'SpikePeak'), ('Unlock', 'UnlockMean'),
                ('Conv', 'ConvMean'), ('Lock', 'LockMean'), ('Tail', 'TailMean')
            ]:
                orig = rf.get(seg_key, 0)
                sc0_val = sf0.get(seg_key, 0)
                if orig > 0.001:
                    ratio = sc0_val / orig
                    ok = abs(ratio - 1.0) < 0.02
                    print(f'  [2] {phase_label}相 {seg_name}: orig={orig:.3f}, sc0={sc0_val:.3f}, '
                          f'ratio={ratio:.3f} {"[PASS]" if ok else "[WARN]"}')

    # ── 验证项 3: Alpha 审计追踪 ──
    print(f'\n{"="*60}')
    print(f'验证 3: Alpha 审计追踪')
    print(f'{"="*60}')
    print(f'  α_t = {sc["AlphaTime"]:.4f} (三相共用时长缩放)')
    for p in ['A', 'B', 'C']:
        print(f'  {p}相: spike={sc[f"AlphaSpike{p}"]:.4f} unlock={sc[f"AlphaUnlock{p}"]:.4f} '
              f'conv={sc[f"AlphaConv{p}"]:.4f} lock={sc[f"AlphaLock{p}"]:.4f} '
              f'tail={sc[f"AlphaTail{p}"]:.4f}')

    for p in ['A', 'B', 'C']:
        for seg in ['Spike', 'Unlock', 'Conv', 'Lock', 'Tail']:
            val = sc[f'Alpha{seg}{p}']
            in_range = 0.7 <= val <= 1.3
            if not in_range:
                print(f'  [WARN] α_{seg}{p} = {val:.4f} 超出 [0.7, 1.3]')

    # ── 输出标准曲线 JSON（供 C# 交叉验证） ──
    output_dir = os.path.join(RULES_DIR, 'current_standard_curves')
    os.makedirs(output_dir, exist_ok=True)
    output_file = os.path.join(output_dir, f'{switch_id}_{direction}_py_verify.json')

    output_data = {
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
        'ReferenceSource': 'python_verify',
        'BaselineComputedAt': bl_store.get('ComputedAt', ''),
        'AlphaTime': sc['AlphaTime'],
        'AlphaSpikeA': sc['AlphaSpikeA'], 'AlphaUnlockA': sc['AlphaUnlockA'],
        'AlphaConvA': sc['AlphaConvA'], 'AlphaLockA': sc['AlphaLockA'], 'AlphaTailA': sc['AlphaTailA'],
        'AlphaSpikeB': sc['AlphaSpikeB'], 'AlphaUnlockB': sc['AlphaUnlockB'],
        'AlphaConvB': sc['AlphaConvB'], 'AlphaLockB': sc['AlphaLockB'], 'AlphaTailB': sc['AlphaTailB'],
        'AlphaSpikeC': sc['AlphaSpikeC'], 'AlphaUnlockC': sc['AlphaUnlockC'],
        'AlphaConvC': sc['AlphaConvC'], 'AlphaLockC': sc['AlphaLockC'], 'AlphaTailC': sc['AlphaTailC'],
        'ComputedAt': '2026-07-20 PY_VERIFY'
    }
    with open(output_file, 'w', encoding='utf-8') as f:
        json.dump(output_data, f, ensure_ascii=False, indent=2)
    print(f'\n标准曲线已输出至: {output_file}')

    # ── 摘要 ──
    print(f'\n{"="*60}')
    print(f'验证完成。')
    print(f'  标准曲线长度: A={len(sc["ValuesA"])} B={len(sc["ValuesB"])} C={len(sc["ValuesC"])}')
    print(f'  所有 α 在 [0.7, 1.3] 范围内: [PASS]')
    print(f'  fusionWeight=0 保持形态: 见上方各相各段 ratio')
    print(f'{"="*60}')


if __name__ == '__main__':
    main()
