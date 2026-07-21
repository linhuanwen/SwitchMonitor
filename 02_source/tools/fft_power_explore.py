#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
FFT 功率曲线频域探索模块 - 独立测试脚本

对单台转辙机的定位->反位功率曲线做傅里叶变换，探索频域特征。

用法:
    python fft_power_explore.py
    python fft_power_explore.py --switch 2-1
    python fft_power_explore.py --switch 1-1 --limit 500

依赖: numpy scipy matplotlib
"""

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
from scipy import signal
from scipy.fft import rfft, rfftfreq
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

# ---------------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------------
PRODUCTION_DATA_DIR = Path(r"D:\tool\SwitchMonitor\05_production_data")
PARSED_DATA_DIR = PRODUCTION_DATA_DIR / "parsed_data"
RULES_DIR = PRODUCTION_DATA_DIR / "Rules"

DEFAULT_SWITCH = "1-1"
DEFAULT_DIRECTION = "定位→反位"
DEFAULT_OUTPUT_DIR = Path(__file__).resolve().parent.parent.parent / "06_deploy" / "fft_explore"

# 频段定义 (名称, 范围Hz, 颜色, 填充色)
BANDS_CN = [
    ("超低频\n(0.04-0.5Hz)",  0.04, 0.5,  "#c8e6c9", "#a5d6a7"),
    ("低频\n(0.5-2Hz)",       0.5,  2.0,  "#ffe0b2", "#ffcc80"),
    ("中频\n(2-5Hz)",         2.0,  5.0,  "#bbdefb", "#90caf9"),
    ("高频\n(5-12.5Hz)",      5.0,  12.5, "#f8bbd0", "#f48fb1"),
]

# 五个物理阶段
PHASES = [
    (0, 0.8,   "启动尖峰",       "#e74c3c"),
    (0.8, 2.5, "解锁段",         "#f39c12"),
    (2.5, 9.5, "转换段（主体）", "#2ecc71"),
    (9.5, 10.8,"锁闭凹槽",       "#3498db"),
    (10.8, 99, "尾部释放",       "#9b59b6"),
]

# ---------------------------------------------------------------------------
# 字体
# ---------------------------------------------------------------------------
def setup_chinese():
    available = {f.name for f in fm.fontManager.ttflist}
    for font in ["Microsoft YaHei", "SimHei", "Noto Sans SC", "KaiTi"]:
        if font in available:
            plt.rcParams["font.sans-serif"] = [font, "DejaVu Sans"]
            plt.rcParams["axes.unicode_minus"] = False
            print(f"[字体] {font}")
            return
    plt.rcParams["font.sans-serif"] = ["DejaVu Sans"]
    plt.rcParams["axes.unicode_minus"] = False
    print("[字体] 未找到中文字体")

# ---------------------------------------------------------------------------
# 数据加载
# ---------------------------------------------------------------------------
def load_power_curves(switch_id, direction, limit=0):
    switch_dir = PARSED_DATA_DIR / switch_id
    if not switch_dir.is_dir():
        print(f"[错误] 目录不存在: {switch_dir}")
        sys.exit(1)

    curves = []
    json_files = sorted(switch_dir.glob("*.json"))
    json_files = [f for f in json_files
                  if f.name != "index.json" and not f.name.endswith(".diag.json")]

    for fp in json_files:
        try:
            with open(fp, encoding="utf-8") as fh:
                events = json.load(fh)
        except (json.JSONDecodeError, OSError):
            continue
        for evt in events:
            if evt.get("Direction") != direction:
                continue
            power = evt.get("Power", [])
            if not power or len(power) < 50:
                continue
            times = np.array([p[0] for p in power], dtype=np.float64)
            values = np.array([p[1] for p in power], dtype=np.float64)
            curves.append(dict(
                timestamp=evt["Timestamp"],
                datetime=evt.get("DateTimeStr", ""),
                duration=evt.get("Duration", 0.0),
                sample_count=len(power),
                power_times=times,
                power_values=values,
            ))
            if limit and len(curves) >= limit:
                break
        if limit and len(curves) >= limit:
            break

    print(f"[加载] 转辙机={switch_id}  方向={direction}  共 {len(curves)} 条曲线")
    return curves


def load_baselines(switch_id):
    bp = RULES_DIR / "baselines.json"
    if not bp.is_file():
        return None
    with open(bp, encoding="utf-8-sig") as fh:
        data = json.load(fh)
    return data.get("Switches", {}).get(switch_id, None)


# ---------------------------------------------------------------------------
# FFT / STFT
# ---------------------------------------------------------------------------
def compute_fft(values, sample_rate=25.0):
    n = len(values)
    signal_dc = values - np.mean(values)
    window = np.hanning(n)
    windowed = signal_dc * window
    spectrum = rfft(windowed)
    freqs = rfftfreq(n, d=1.0 / sample_rate)
    mag = np.abs(spectrum)
    mag = mag / (n / 2)
    mag[0] = mag[0] / 2
    mag = mag / np.mean(window)
    return freqs, mag


def compute_stft(values, sample_rate=25.0, nperseg=64, noverlap=48):
    f, t, Zxx = signal.stft(
        values, fs=sample_rate, nperseg=nperseg, noverlap=noverlap,
        window="hann", boundary=None)
    mag_db = 20 * np.log10(np.abs(Zxx) + 1e-12)
    return f, t, mag_db


def band_energy(freqs, mag):
    total = np.sum(mag ** 2)
    result = {}
    for name, flo, fhi, _, _ in BANDS_CN:
        mask = (freqs >= flo) & (freqs < fhi)
        e = np.sum(mag[mask] ** 2)
        result[name] = dict(energy=e, ratio=e / total if total > 0 else 0.0)
    return result


# ---------------------------------------------------------------------------
# 图1: 频谱叠加
# ---------------------------------------------------------------------------
def plot_spectrum_overlay(curves, output_path, max_curves=200):
    if len(curves) > max_curves:
        rng = np.random.RandomState(42)
        idx = rng.choice(len(curves), max_curves, replace=False)
        subset = [curves[i] for i in idx]
        print(f"[绘图] 频谱叠加: 抽样 {max_curves}/{len(curves)} 条")
    else:
        subset = curves

    all_spec = []
    durs = []
    for c in subset:
        f, m = compute_fft(c["power_values"])
        all_spec.append((f, m))
        durs.append(c["duration"])

    fig, axes = plt.subplots(2, 2, figsize=(16, 12))
    fig.suptitle(
        f"图1：频谱叠加 -- 转辙机 {DEFAULT_SWITCH}（{DEFAULT_DIRECTION}）\n"
        f"共 {len(curves)} 条曲线 | 采样率 25Hz | 最高可检测频率 12.5Hz",
        fontsize=14, fontweight="bold")

    # 子图1: 全叠加
    ax = axes[0, 0]
    for f, m in all_spec:
        ax.plot(f, m, alpha=0.12, linewidth=0.4, color="steelblue")
    for name, flo, fhi, _, fc in BANDS_CN:
        ax.axvspan(flo, fhi, alpha=0.10, color=fc)
        ax.text((flo + fhi) / 2, ax.get_ylim()[1] * 0.96 if ax.get_ylim()[1] > 0 else 1,
                name.split("\n")[0], ha="center", fontsize=7.5, color="#555")
    ax.set_xlabel("频率 (Hz)")
    ax.set_ylabel("幅度")
    ax.set_title("子图1：全部频谱叠加（半透明）\n-> 观察频谱形状是否集中、是否一致")
    ax.set_xlim(0, 12.5)
    ax.grid(True, alpha=0.3)

    # 子图2: 均值+-1sigma
    ax = axes[0, 1]
    common_f = np.linspace(0, 12.5, 300)
    interp = np.array([np.interp(common_f, f, m) for f, m in all_spec])
    mean_m = np.mean(interp, axis=0)
    std_m = np.std(interp, axis=0)
    ax.fill_between(common_f, mean_m - std_m, mean_m + std_m,
                    alpha=0.3, color="steelblue", label="正常波动范围（±1σ）")
    ax.plot(common_f, mean_m, color="darkblue", linewidth=1.5, label="均值频谱")
    ax.set_xlabel("频率 (Hz)")
    ax.set_ylabel("幅度")
    ax.set_title(f"子图2：均值频谱 ± 1σ（N={len(all_spec)}）\n-> 蓝带=正常范围，超出蓝带的曲线可标记为异常")
    ax.set_xlim(0, 12.5)
    ax.legend(fontsize=8)
    ax.grid(True, alpha=0.3)
    for name, flo, fhi, _, fc in BANDS_CN:
        ax.axvspan(flo, fhi, alpha=0.08, color=fc)

    # 子图3: 按时长分组
    ax = axes[1, 0]
    durs_arr = np.array(durs)
    p25, p75 = np.percentile(durs_arr, [25, 75])
    groups = [
        ([i for i, d in enumerate(durs) if d <= p25],
         f"短时长组（≤{p25:.1f}s）", "#27ae60", "--"),
        ([i for i, d in enumerate(durs) if p25 < d <= p75],
         f"中等时长组（{p25:.1f}-{p75:.1f}s）", "#2980b9", "-"),
        ([i for i, d in enumerate(durs) if d > p75],
         f"长时长组（>{p75:.1f}s）", "#e74c3c", "-."),
    ]
    for idxs, label, color, ls in groups:
        if not idxs:
            continue
        mags = np.array([np.interp(common_f, all_spec[i][0], all_spec[i][1]) for i in idxs])
        ax.plot(common_f, np.mean(mags, axis=0), color=color, linestyle=ls,
                linewidth=1.5, label=f"{label}（N={len(idxs)}）")
    ax.set_xlabel("频率 (Hz)")
    ax.set_ylabel("幅度")
    ax.set_title("子图3：按时长分组的均值频谱\n-> 看不同时长的曲线，频谱是否有系统性差异")
    ax.set_xlim(0, 12.5)
    ax.legend(fontsize=8)
    ax.grid(True, alpha=0.3)

    # 子图4: 箱线图
    ax = axes[1, 1]
    ratios_by_band = defaultdict(list)
    for f, m in all_spec:
        be = band_energy(f, m)
        for name, _, _, _, _ in BANDS_CN:
            ratios_by_band[name].append(be[name]["ratio"] * 100)
    bp = ax.boxplot(
        [ratios_by_band[name] for name, _, _, _, _ in BANDS_CN],
        tick_labels=[name.split("\n")[0] for name, _, _, _, _ in BANDS_CN],
        patch_artist=True)
    for patch, (_, _, _, color, _) in zip(bp["boxes"], BANDS_CN):
        patch.set_facecolor(color)
    ax.set_ylabel("能量占比 (%)")
    ax.set_title("子图4：各频段能量占比分布（箱线图）\n-> 箱子越矮=越稳定；高度波动大=不稳定；圆点=异常值")
    ax.grid(True, alpha=0.3, axis="y")
    for i, (name, _, _, _, _) in enumerate(BANDS_CN):
        med = np.median(ratios_by_band[name])
        ax.annotate(f"中位数 {med:.1f}%", xy=(i + 1, med), fontsize=7,
                    ha="center", va="bottom", color="#333")

    fig.tight_layout()
    fig.savefig(output_path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"[保存] {output_path}")


# ---------------------------------------------------------------------------
# 图2: STFT 时频谱
# ---------------------------------------------------------------------------
def plot_stft_examples(curves, output_path, n_examples=4):
    if len(curves) <= n_examples:
        samples = curves
    else:
        durs = [c["duration"] for c in curves]
        idx_sorted = np.argsort(durs)
        step = max(1, len(curves) // n_examples)
        samples = [curves[idx_sorted[int(i * step)]] for i in range(n_examples)]

    fig, axes = plt.subplots(len(samples), 1, figsize=(16, 4.2 * len(samples)))
    if len(samples) == 1:
        axes = [axes]
    fig.suptitle(
        f"图2：STFT 时频谱 -- 转辙机 {DEFAULT_SWITCH}（{DEFAULT_DIRECTION}）\n"
        f"窗长 2.56s | 75%重叠 | 颜色=频率能量强度(dB) | 青色虚线=原始功率波形",
        fontsize=14, fontweight="bold")

    for i, (c, ax) in enumerate(zip(samples, axes)):
        values = c["power_values"]
        times = c["power_times"]
        f, t, mag_db = compute_stft(values)

        vmax_val = np.percentile(mag_db, 95)
        if vmax_val <= -60:
            vmax_val = -50
        im = ax.pcolormesh(t + times[0], f, mag_db, shading="gouraud",
                           cmap="inferno", vmin=-60, vmax=vmax_val)
        ax.set_ylabel("频率 (Hz)")

        dur_note = ""
        if c["duration"] > 13:
            dur_note = " 【偏长】"
        elif c["duration"] < 10.5:
            dur_note = " 【偏短】"

        ax.set_title(
            f"事件 {i+1}：{c['datetime']}  |  时长={c['duration']:.1f}s  |  "
            f"采样点={c['sample_count']}{dur_note}", fontsize=10)

        # 功率曲线叠加
        ax2 = ax.twinx()
        ax2.plot(times, values, color="cyan", alpha=0.55, linewidth=0.8, linestyle="--")
        ax2.set_ylabel("功率 (kW)", color="cyan", alpha=0.7)
        ax2.tick_params(axis="y", labelcolor="cyan")

        for flo in [0.5, 2.0, 5.0]:
            ax.axhline(y=flo, color="white", linestyle=":", alpha=0.45, linewidth=0.8)

        for t_start, t_end, label, color in PHASES:
            t_end_real = min(t_end, c["duration"])
            if t_start >= t_end_real:
                continue
            ax.axvspan(t_start, t_end_real, alpha=0.10, color=color)
            ax.text((t_start + t_end_real) / 2, f[-1] * 0.90, label,
                    ha="center", fontsize=7.5, color=color, alpha=0.85,
                    bbox=dict(boxstyle="round,pad=0.15", facecolor="white", alpha=0.75))

    axes[-1].set_xlabel("时间 (s)")
    fig.subplots_adjust(right=0.92)
    cbar_ax = fig.add_axes([0.94, 0.08, 0.015, 0.84])
    cbar = fig.colorbar(im, cax=cbar_ax)
    cbar.set_label("能量强度 (dB)", fontsize=9)

    fig.savefig(output_path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"[保存] {output_path}")


# ---------------------------------------------------------------------------
# 图3: 正常 vs 异常
# ---------------------------------------------------------------------------
def plot_normal_vs_abnormal(curves, baselines, output_path):
    if not baselines:
        print("[跳过] 无基线数据")
        return

    ref_dur = baselines.get("RefDurationSec", 12.0)
    normal, abnormal = [], []
    for c in curves:
        if abs(c["duration"] - ref_dur) < 1.0:
            normal.append(c)
        else:
            abnormal.append(c)

    if len(normal) < 5 or len(abnormal) < 5:
        print(f"[跳过] 正常={len(normal)} 异常={len(abnormal)} 数量不足")
        return

    rng = np.random.RandomState(42)
    if len(normal) > 150:
        normal = [normal[i] for i in rng.choice(len(normal), 150, replace=False)]
    if len(abnormal) > 150:
        abnormal = [abnormal[i] for i in rng.choice(len(abnormal), 150, replace=False)]

    fig, axes = plt.subplots(2, 3, figsize=(20, 11))
    fig.suptitle(
        f"图3：正常 vs 异常频谱对比 -- 转辙机 {DEFAULT_SWITCH}（{DEFAULT_DIRECTION}）\n"
        f"参考时长={ref_dur:.1f}s | 正常={len(normal)}条 | 异常={len(abnormal)}条",
        fontsize=13, fontweight="bold")

    common_f = np.linspace(0, 12.5, 300)

    # 子图1: 正常叠加
    ax = axes[0, 0]
    for c in normal:
        f, m = compute_fft(c["power_values"])
        ax.plot(f, m, alpha=0.18, linewidth=0.4, color="#27ae60")
    ax.set_title(f"子图1：正常曲线频谱叠加（N={len(normal)}）\n-> 绿色=正常，看正常状态下的频域特征")
    ax.set_xlabel("频率 (Hz)")
    ax.set_ylabel("幅度")
    ax.set_xlim(0, 12.5)
    ax.grid(True, alpha=0.3)

    # 子图2: 异常叠加
    ax = axes[0, 1]
    for c in abnormal:
        f, m = compute_fft(c["power_values"])
        ax.plot(f, m, alpha=0.18, linewidth=0.4, color="#e74c3c")
    ax.set_title(f"子图2：异常曲线频谱叠加（N={len(abnormal)}）\n-> 红色=异常，与上图对比看分布是否不同")
    ax.set_xlabel("频率 (Hz)")
    ax.set_ylabel("幅度")
    ax.set_xlim(0, 12.5)
    ax.grid(True, alpha=0.3)

    # 子图3: 均值对比
    ax = axes[0, 2]
    for group, label, color in [(normal, "正常", "#27ae60"), (abnormal, "异常", "#e74c3c")]:
        mags = np.array([np.interp(common_f, *compute_fft(c["power_values"])) for c in group])
        mean_m = np.mean(mags, axis=0)
        std_m = np.std(mags, axis=0)
        ax.fill_between(common_f, mean_m - std_m, mean_m + std_m, alpha=0.2, color=color)
        ax.plot(common_f, mean_m, color=color, linewidth=1.5, label=label)
    ax.set_title("子图3：均值频谱 ± 1σ 对比\n-> 绿带和红带分开的地方=能区分正常/异常的特征频段")
    ax.set_xlabel("频率 (Hz)")
    ax.legend(fontsize=9)
    ax.set_xlim(0, 12.5)
    ax.grid(True, alpha=0.3)

    # 子图4: 频段能量柱状图
    ax = axes[1, 0]
    x = np.arange(len(BANDS_CN))
    width = 0.32
    nr, ar = [], []
    for c in normal:
        be = band_energy(*compute_fft(c["power_values"]))
        nr.append([be[name]["ratio"] * 100 for name, _, _, _, _ in BANDS_CN])
    for c in abnormal:
        be = band_energy(*compute_fft(c["power_values"]))
        ar.append([be[name]["ratio"] * 100 for name, _, _, _, _ in BANDS_CN])
    nr, ar = np.array(nr), np.array(ar)

    ax.bar(x - width/2, nr.mean(axis=0), width, yerr=nr.std(axis=0),
           color="#27ae60", alpha=0.75, label="正常", capsize=3, error_kw=dict(linewidth=0.8))
    ax.bar(x + width/2, ar.mean(axis=0), width, yerr=ar.std(axis=0),
           color="#e74c3c", alpha=0.75, label="异常", capsize=3, error_kw=dict(linewidth=0.8))
    ax.set_xticks(x)
    ax.set_xticklabels([name.split("\n")[0] for name, _, _, _, _ in BANDS_CN], fontsize=8.5)
    ax.set_ylabel("能量占比 (%)")
    ax.set_title("子图4：各频段能量占比：正常 vs 异常\n-> 柱子差距大的频段=关键差异频段")
    ax.legend(fontsize=8)
    ax.grid(True, alpha=0.3, axis="y")

    # 子图5: 差异频谱
    ax = axes[1, 1]
    nm = np.array([np.interp(common_f, *compute_fft(c["power_values"])) for c in normal])
    am = np.array([np.interp(common_f, *compute_fft(c["power_values"])) for c in abnormal])
    diff = np.mean(am, axis=0) - np.mean(nm, axis=0)
    ax.plot(common_f, diff, color="#8e44ad", linewidth=1.5)
    ax.fill_between(common_f, 0, diff, where=(diff > 0),
                    color="#e74c3c", alpha=0.35, label="异常偏高（异常能量 > 正常）")
    ax.fill_between(common_f, 0, diff, where=(diff < 0),
                    color="#27ae60", alpha=0.35, label="异常偏低（异常能量 < 正常）")
    ax.axhline(y=0, color="black", linestyle="--", alpha=0.5)
    ax.set_xlabel("频率 (Hz)")
    ax.set_title("子图5：★ 差异频谱（异常均值 - 正常均值）★\n-> 红色山峰=异常偏高的频率，可能是故障特征频率")
    ax.set_xlim(0, 12.5)
    ax.legend(fontsize=8)
    ax.grid(True, alpha=0.3)
    peak_i = np.argmax(np.abs(diff))
    ax.annotate(f"最大差异点\n{common_f[peak_i]:.1f}Hz",
                xy=(common_f[peak_i], diff[peak_i]),
                fontsize=8, color="#8e44ad", fontweight="bold",
                arrowprops=dict(arrowstyle="->", color="#8e44ad", lw=1.2))

    # 子图6: 时域对照
    ax = axes[1, 2]
    common_t = np.linspace(0, 13.5, 400)
    for group, label, color in [(normal, "正常", "#27ae60"), (abnormal, "异常", "#e74c3c")]:
        iv = np.array([np.interp(common_t, c["power_times"], c["power_values"], left=0, right=0)
                       for c in group])
        mv = np.mean(iv, axis=0)
        sv = np.std(iv, axis=0)
        ax.fill_between(common_t, mv - sv, mv + sv, alpha=0.2, color=color)
        ax.plot(common_t, mv, color=color, linewidth=1.5, label=label)
    ax.set_xlabel("时间 (s)")
    ax.set_ylabel("功率 (kW)")
    ax.set_title("子图6：时域均值曲线对比（对照参考）\n-> 传统时域视角，用于和频域对比印证")
    ax.legend(fontsize=8)
    ax.grid(True, alpha=0.3)

    fig.tight_layout()
    fig.savefig(output_path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"[保存] {output_path}")


# ---------------------------------------------------------------------------
# 图4: 单曲线深度剖析
# ---------------------------------------------------------------------------
def plot_single_curve_deep_dive(curves, output_path, event_index=0):
    if event_index >= len(curves):
        print(f"[跳过] event_index={event_index} 超出范围")
        return

    c = curves[event_index]
    values = c["power_values"]
    times = c["power_times"]
    freqs, mag = compute_fft(values)
    f_stft, t_stft, mag_db = compute_stft(values)
    be = band_energy(freqs, mag)

    fig = plt.figure(figsize=(18, 11))
    fig.suptitle(
        f"图4：单曲线深度剖析 -- 转辙机 {DEFAULT_SWITCH}  |  {c['datetime']}\n"
        f"时长={c['duration']:.1f}s  |  采样点={c['sample_count']}  |  采样率 25Hz",
        fontsize=13, fontweight="bold")

    # 子图1: 时域
    ax1 = fig.add_subplot(2, 3, 1)
    ax1.plot(times, values, color="#2c3e50", linewidth=0.8)
    ax1.fill_between(times, 0, values, alpha=0.08, color="#2980b9")
    ax1.set_xlabel("时间 (s)")
    ax1.set_ylabel("功率 (kW)")
    ax1.set_title("子图1：原始功率曲线（时域）\n-> 这是我们熟悉的传统视角")
    ax1.grid(True, alpha=0.3)
    pi = np.argmax(values)
    ax1.annotate(f"峰值 {values[pi]:.2f}kW",
                 xy=(times[pi], values[pi]),
                 fontsize=8, color="#e74c3c", fontweight="bold",
                 arrowprops=dict(arrowstyle="->", color="#e74c3c", lw=1))

    # 子图2: FFT
    ax2 = fig.add_subplot(2, 3, 2)
    ax2.stem(freqs, mag, linefmt="#3498db", markerfmt=" ", basefmt="#bbb")
    ax2.set_xlabel("频率 (Hz)")
    ax2.set_ylabel("幅度")
    ax2.set_title("子图2：FFT 频谱（频域）\n-> 每根茎=该频率的正弦波有多强")
    ax2.set_xlim(0, 12.5)
    ax2.grid(True, alpha=0.3)
    for name, flo, fhi, _, fc in BANDS_CN:
        ax2.axvspan(flo, fhi, alpha=0.10, color=fc)
        ymax = ax2.get_ylim()[1] if ax2.get_ylim()[1] > 0 else 0.5
        ax2.text((flo + fhi) / 2, ymax * 0.92, name.split("\n")[0],
                 ha="center", fontsize=7, alpha=0.65)
    for pi in np.argsort(mag)[-3:]:
        if freqs[pi] > 0.01:
            ax2.annotate(f"{freqs[pi]:.1f}Hz", xy=(freqs[pi], mag[pi]),
                         fontsize=7, color="#e74c3c")

    # 子图3: 饼图
    ax3 = fig.add_subplot(2, 3, 3)
    labels = [name.split("\n")[0] for name, _, _, _, _ in BANDS_CN]
    sizes = [be[name]["ratio"] * 100 for name, _, _, _, _ in BANDS_CN]
    colors_pie = [c for _, _, _, c, _ in BANDS_CN]
    wedges, texts, autotexts = ax3.pie(
        sizes, labels=labels, autopct="%1.1f%%", colors=colors_pie,
        startangle=90, pctdistance=0.55, explode=(0, 0, 0, 0.05))
    for at in autotexts:
        at.set_fontsize(9.5)
        at.set_fontweight("bold")
    ax3.set_title("子图3：频段能量占比\n-> 哪个扇区大=哪个频段能量占主导")

    # 子图4: STFT
    ax4 = fig.add_subplot(2, 1, 2)
    vmax_val = np.percentile(mag_db, 95)
    if vmax_val <= -60:
        vmax_val = -50
    im = ax4.pcolormesh(t_stft + times[0], f_stft, mag_db, shading="gouraud",
                        cmap="inferno", vmin=-60, vmax=vmax_val)
    ax4.set_xlabel("时间 (s)")
    ax4.set_ylabel("频率 (Hz)")
    ax4.set_title(
        "子图4：STFT 时频谱\n"
        "-> 横轴=时间 | 纵轴=频率 | 颜色=能量强度 | 青色虚线=功率波形")

    for flo in [0.5, 2.0, 5.0]:
        ax4.axhline(y=flo, color="white", linestyle=":", alpha=0.5, linewidth=0.8)

    ax4b = ax4.twinx()
    ax4b.plot(times, values, color="cyan", alpha=0.5, linewidth=0.9, linestyle="--")
    ax4b.set_ylabel("功率 (kW)", color="cyan", alpha=0.7)
    ax4b.tick_params(axis="y", labelcolor="cyan")

    for t_start, t_end, label, color in PHASES:
        t_end_real = min(t_end, c["duration"])
        if t_start >= t_end_real:
            continue
        ax4.axvspan(t_start, t_end_real, alpha=0.08, color=color)
        ax4.text((t_start + t_end_real) / 2, f_stft[-1] * 0.92, label,
                 ha="center", fontsize=7.5, color=color, alpha=0.85,
                 bbox=dict(boxstyle="round,pad=0.15", facecolor="white", alpha=0.75))

    fig.tight_layout()
    fig.savefig(output_path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"[保存] {output_path}")


# ---------------------------------------------------------------------------
# 图5: 长期趋势
# ---------------------------------------------------------------------------
def plot_band_energy_time_series(curves, output_path):
    if len(curves) < 5:
        print("[跳过] 曲线太少")
        return

    sorted_c = sorted(curves, key=lambda c: c["timestamp"])
    records = []
    for c in sorted_c:
        f, m = compute_fft(c["power_values"])
        be = band_energy(f, m)
        records.append(dict(
            timestamp=c["timestamp"], datetime=c["datetime"],
            duration=c["duration"],
            ratios={name: be[name]["ratio"] * 100 for name, _, _, _, _ in BANDS_CN},
            energies={name: be[name]["energy"] for name, _, _, _, _ in BANDS_CN},
        ))

    from datetime import datetime as dt
    dts = [dt.strptime(r["datetime"], "%Y-%m-%d %H:%M:%S") for r in records]
    band_names = [name for name, _, _, _, _ in BANDS_CN]
    band_short = [name.split("\n")[0] for name in band_names]

    fig, axes = plt.subplots(3, 1, figsize=(18, 13), sharex=True)
    fig.suptitle(
        f"图5：频段能量长期趋势 -- 转辙机 {DEFAULT_SWITCH}（{DEFAULT_DIRECTION}）\n"
        f"共 {len(records)} 条曲线 | {records[0]['datetime']} -> {records[-1]['datetime']}",
        fontsize=14, fontweight="bold")

    # 子图1: 堆叠面积
    ax = axes[0]
    y_data = np.array([[r["ratios"][name] for name in band_names] for r in records])
    colors_stack = [c for _, _, _, c, _ in BANDS_CN]
    ax.stackplot(dts, y_data.T, labels=band_short, colors=colors_stack, alpha=0.85)
    ax.set_ylabel("能量占比 (%)")
    ax.set_title(
        "子图1：各频段能量占比随时间变化（堆叠面积图）\n"
        "-> 某个颜色变厚=该频段占比升高，变薄=降低")
    ax.legend(loc="upper right", fontsize=8, ncol=4)
    ax.set_ylim(0, 105)
    ax.grid(True, alpha=0.3, axis="y")

    # 子图2: 各频段能量
    ax = axes[1]
    line_colors = ["#27ae60", "#e67e22", "#2980b9", "#e74c3c"]
    for name, color in zip(band_names, line_colors):
        energies = [r["energies"][name] for r in records]
        w = min(7, max(3, len(energies) // 4))
        if w >= 3:
            smoothed = np.convolve(energies, np.ones(w)/w, mode="same")
        else:
            smoothed = energies
        ax.plot(dts, smoothed, color=color, linewidth=0.9, alpha=0.85,
                label=band_short[band_names.index(name)])
    ax.set_ylabel("频段能量")
    ax.set_title(
        "子图2：各频段绝对能量（7点滑动均值）\n"
        "-> 红线（高频）持续上升=杂波越来越多，可能是机械老化的信号")
    ax.legend(loc="upper right", fontsize=8, ncol=4)
    ax.grid(True, alpha=0.3)

    # 子图3: 时长对照
    ax = axes[2]
    durations = [r["duration"] for r in records]
    ax.scatter(dts, durations, s=3, alpha=0.3, color="#7f8c8d")
    w = min(15, max(5, len(durations) // 3))
    if w >= 3:
        smoothed_d = np.convolve(durations, np.ones(w)/w, mode="same")
        ax.plot(dts, smoothed_d, color="#8e44ad", linewidth=1.2, alpha=0.85,
                label=f"{w}点滑动均值")
    ax.axhline(y=np.median(durations), color="#e74c3c", linestyle="--", alpha=0.5,
               label=f"中位数 {np.median(durations):.1f}s")
    ax.set_ylabel("时长 (s)")
    ax.set_xlabel("日期")
    ax.set_title("子图3：动作时长变化（对照参考）\n-> 如果时长和频段能量同步变化，可能同源")
    ax.legend(fontsize=8)
    ax.grid(True, alpha=0.3)

    fig.tight_layout()
    fig.savefig(output_path, dpi=150, bbox_inches="tight")
    plt.close(fig)
    print(f"[保存] {output_path}")


# ---------------------------------------------------------------------------
# 主入口
# ---------------------------------------------------------------------------
def main():
    global DEFAULT_SWITCH, DEFAULT_DIRECTION

    parser = argparse.ArgumentParser(description="FFT 功率曲线频域探索")
    parser.add_argument("--switch", default=DEFAULT_SWITCH)
    parser.add_argument("--direction", default=DEFAULT_DIRECTION)
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--output-dir", default=str(DEFAULT_OUTPUT_DIR))
    args = parser.parse_args()

    DEFAULT_SWITCH = args.switch
    DEFAULT_DIRECTION = args.direction

    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    setup_chinese()

    print("=" * 60)
    print(f"FFT 功率曲线频域探索")
    print(f"  转辙机: {args.switch}  方向: {args.direction}")
    print(f"  输出目录: {output_dir}")
    print("=" * 60)

    curves = load_power_curves(args.switch, args.direction, args.limit)
    if not curves:
        print("[错误] 未加载到曲线")
        sys.exit(1)

    print(f"\n[统计] 时长: 最短={min(c['duration'] for c in curves):.1f}s  "
          f"中位数={np.median([c['duration'] for c in curves]):.1f}s  "
          f"最长={max(c['duration'] for c in curves):.1f}s")
    print(f"[统计] 采样点数范围: {min(c['sample_count'] for c in curves)} ~ "
          f"{max(c['sample_count'] for c in curves)}")
    print(f"[统计] 时间跨度: {curves[0]['datetime']} -> {curves[-1]['datetime']}")

    baselines = load_baselines(args.switch)
    if baselines:
        print(f"[基线] 参考时长={baselines.get('RefDurationSec','N/A')}s  "
              f"基线样本数={baselines.get('SampleCount','N/A')}")

    prefix = f"{args.switch.replace('-','_')}_{args.direction.replace('→','to')}"

    print("\n>>> 图1：频谱叠加...")
    plot_spectrum_overlay(curves, output_dir / f"{prefix}_01_spectrum_overlay.png")

    print(">>> 图2：STFT 时频谱...")
    plot_stft_examples(curves, output_dir / f"{prefix}_02_stft_examples.png")

    print(">>> 图3：正常 vs 异常对比...")
    plot_normal_vs_abnormal(curves, baselines,
                            output_dir / f"{prefix}_03_normal_vs_abnormal.png")

    print(">>> 图4：单曲线深度剖析...")
    med_dur = np.median([c["duration"] for c in curves])
    best_i = min(range(len(curves)),
                 key=lambda i: abs(curves[i]["duration"] - med_dur))
    plot_single_curve_deep_dive(curves, output_dir / f"{prefix}_04_deep_dive.png",
                                event_index=best_i)

    print(">>> 图5：频段能量趋势...")
    plot_band_energy_time_series(curves,
                                 output_dir / f"{prefix}_05_band_trend.png")

    print(f"\n{'=' * 60}")
    print(f"完成! 5 张图表已保存到 {output_dir}")
    print(f"{'=' * 60}")


if __name__ == "__main__":
    main()
