#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
诊断模块 (Diagnosis) 的 Python 参考实现 — C# 实现的交叉验证基准。

算法规格的权威文字版见 01_docs/diagnosis/CONTEXT.md §3-§5；
本脚本与该规格一一对应，金标准夹具数值即由本脚本生成。
C# 实现（Slice D1-D3）的输出必须与本脚本一致（浮点容差 ±0.002）。

用法:
    python diag_reference_check.py golden   [sanshuibei_csv目录]   # 打印4个金标准夹具特征
    python diag_reference_check.py baseline [sanshuibei_csv目录]   # 打印8台道岔基线值
    python diag_reference_check.py dryrun   [sanshuibei_csv目录]   # 规则R1-R6全量演习触发矩阵
"""
import csv
import statistics
import sys
from pathlib import Path

DEFAULT_DIR = Path(__file__).resolve().parents[2] / "03_raw_data" / "sanshuibei_csv"

# 功率文件 → switchId（对应 config.json 的 switchGroups: 电流文件N ↔ 功率文件N+3）
POWER_FILES = {3: "1-J", 7: "1-X", 11: "3-J", 15: "3-X",
               19: "2-J", 23: "2-X", 27: "4-J", 31: "4-X"}

# 默认阈值（与 Rules/thresholds.json 模板同值）
TH = dict(R1_durOverRefSeconds=3.0, R2_durUnderRefRatio=0.6, R3_maxDeviationSeconds=0.5,
          R4_overRefRatio=1.3, R5_overRefRatio=1.3, R6_maxStepRatio=1.5, R6_minStepRatio=0.67)

GOLDEN = [  # (功率文件号, timestamp, 备注)
    (3, 1770922311, "夹具A 正常J曲线"),
    (7, 1770771323, "夹具B 正常X曲线"),
    (27, 1769618597, "夹具C 超时/卡阻"),
    (31, 1773938685, "夹具D 动作夭折"),
]


def read_power_rows(path):
    """读功率CSV: 每行 timestamp,datetime,phase,s0,s1,... 尾部空列截断。"""
    out = []
    with open(path, encoding='utf-8') as fh:
        rd = csv.reader(fh)
        next(rd)  # 表头
        for r in rd:
            vals = []
            for x in r[3:]:
                if x == '':
                    break
                vals.append(float(x))
            out.append((int(r[0]), r[1], vals))
    return out


def extract(v):
    """特征提取 — CONTEXT.md §3 的权威参考实现。"""
    f = {}
    f['sampleCount'] = len(v)
    f['isFullWindow'] = len(v) >= 780
    peak_all = max(v) if v else 0.0
    f['isValid'] = bool(v) and peak_all > 0.01
    if not f['isValid']:
        return f
    th = max(peak_all * 0.05, 0.01)
    last = 0
    for i, x in enumerate(v):
        if x > th:
            last = i
    f['activeEnd'] = last
    f['durationSec'] = round((last + 1) * 0.04, 2)
    head = v[:15]
    f['spikePeak'] = round(max(head), 3)
    sp = head.index(max(head))
    f['spikeIndex'] = sp
    seg = v[sp + 2:sp + 14]
    f['unlockMean'] = round(statistics.mean(seg), 3) if seg else 0.0
    conv = v[sp + 20:last - 40] if last - 40 > sp + 20 else v[sp + 2:last]
    if not conv:
        conv = v[:last + 1]
    f['convMean'] = round(statistics.mean(conv), 3)
    f['convMax'] = round(max(conv), 3)
    third = len(conv) // 3
    if third >= 5:
        f['stepRatio'] = round(statistics.mean(conv[-third:]) / max(statistics.mean(conv[:third]), 0.01), 3)
    else:
        f['stepRatio'] = 1.0
    lock_seg = v[last - 40:last - 22] if last > 50 else []
    if lock_seg:
        # 退化策略：last-40 < 0 时退化为 [0, last-22)
        if last - 40 < 0:
            lock_seg = v[0:last - 22]
        f['lockMean'] = round(statistics.mean(lock_seg), 3)
    else:
        f['lockMean'] = 0.0
    tail = v[last - 22:last - 2] if last > 30 else []
    f['tailMean'] = round(statistics.mean(tail), 3) if tail else 0.0
    return f


def build_baseline(feats, min_samples=30, direction=None):
    """基线计算 — CONTEXT.md §5。feats 为 extract() 结果列表。
    当 direction 不为 None 时，仅使用匹配方向的特征。
    """
    pool = [f for f in feats if f.get('isValid') and not f['isFullWindow'] and f['durationSec'] >= 2.4]
    if direction is not None:
        pool = [f for f in pool if f.get('direction') == direction]
    if not pool:
        return None
    med = statistics.median([f['durationSec'] for f in pool])
    normal = [f for f in pool if abs(f['durationSec'] - med) < med * 0.15]
    if len(normal) < min_samples:
        return None
    return dict(
        refDurationSec=round(statistics.median([f['durationSec'] for f in normal]), 2),
        refSpikePeak=round(statistics.median([f['spikePeak'] for f in normal]), 3),
        refUnlockMean=round(statistics.median([f['unlockMean'] for f in normal]), 3),
        refConvMean=round(statistics.median([f['convMean'] for f in normal]), 3),
        refLockMean=round(statistics.median([f['lockMean'] for f in normal]), 3),
        refTailMean=round(statistics.median([f['tailMean'] for f in normal]), 3),
        sampleCount=len(normal),
        direction=direction)


def diagnose(f, b):
    """规则引擎 R0-R6 — CONTEXT.md §4（R7/R8 演习无参照值，此处不含）。返回命中规则ID列表。"""
    if not f.get('isValid'):
        return ['R0']
    if b is None:
        return ['R1'] if f['isFullWindow'] else []
    if f['isFullWindow'] or f['durationSec'] > b['refDurationSec'] + TH['R1_durOverRefSeconds']:
        return ['R1']
    if f['durationSec'] < b['refDurationSec'] * TH['R2_durUnderRefRatio']:
        return ['R2']
    hits = []
    if abs(f['durationSec'] - b['refDurationSec']) > TH['R3_maxDeviationSeconds']:
        hits.append('R3')
    if f['spikePeak'] > b['refSpikePeak'] * TH['R4_overRefRatio']:
        hits.append('R4')
    if f['convMean'] > b['refConvMean'] * TH['R5_overRefRatio']:
        hits.append('R5')
    if f['stepRatio'] > TH['R6_maxStepRatio'] or f['stepRatio'] < TH['R6_minStepRatio']:
        hits.append('R6')
    return hits


def cmd_golden(src):
    for pf, ts, label in GOLDEN:
        rows = read_power_rows(src / f"SwitchCurve({pf}).csv")
        match = [(t, dt, v) for t, dt, v in rows if t == ts]
        if not match:
            print(f"[缺失] SwitchCurve({pf}) ts={ts}")
            continue
        t, dt, v = match[0]
        f = extract(v)
        print(f"### SwitchCurve({pf}).csv ts={t} ({dt}) [{label}]")
        for k, val in f.items():
            print(f"    {k}: {val}")
        print()


def cmd_baseline(src):
    DIRS = ["定位→反位", "反位→定位"]
    print(f"{'道岔':6s} {'方向':8s} {'样本':>5s} {'时长s':>7s} {'峰值':>7s} {'解锁':>7s} {'转换':>7s} {'锁闭':>7s} {'缓放':>7s}")
    for pf, sid in POWER_FILES.items():
        rows = read_power_rows(src / f"SwitchCurve({pf}).csv")
        # 按时间戳排序，交替分配方向（与 DataPipeline.cs 一致）
        rows.sort(key=lambda r: r[0])
        feats = []
        for i, (ts, dt, v) in enumerate(rows):
            f = extract(v)
            f['direction'] = DIRS[i % 2]
            f['_ts'] = ts
            feats.append(f)
        for dir_ in DIRS:
            b = build_baseline(feats, direction=dir_)
            if b is None:
                print(f"{sid:6s} {dir_:8s} 样本不足")
                continue
            print(f"{sid:6s} {dir_:8s} {b['sampleCount']:5d} {b['refDurationSec']:7.2f} {b['refSpikePeak']:7.3f} "
                  f"{b['refUnlockMean']:7.3f} {b['refConvMean']:7.3f} {b['refLockMean']:7.3f} {b['refTailMean']:7.3f}")


def cmd_dryrun(src):
    grand = {k: 0 for k in ('R0', 'R1', 'R2', 'R3', 'R4', 'R5', 'R6')}
    total = alarmed = 0
    print(f"{'道岔':6s} {'事件':>5s} | {'R0':>4s} {'R1':>4s} {'R2':>4s} {'R3':>4s} {'R4':>4s} {'R5':>4s} {'R6':>4s} | 触发率")
    for pf, sid in POWER_FILES.items():
        rows = read_power_rows(src / f"SwitchCurve({pf}).csv")
        feats = [extract(v) for _, _, v in rows]
        b = build_baseline(feats)
        cnt = {k: 0 for k in grand}
        fired_events = 0
        for f in feats:
            hits = diagnose(f, b)
            for h in hits:
                cnt[h] += 1
            if hits:
                fired_events += 1
        for k in grand:
            grand[k] += cnt[k]
        total += len(feats)
        alarmed += fired_events
        print(f"{sid:6s} {len(feats):5d} | {cnt['R0']:4d} {cnt['R1']:4d} {cnt['R2']:4d} {cnt['R3']:4d} "
              f"{cnt['R4']:4d} {cnt['R5']:4d} {cnt['R6']:4d} | {fired_events/len(feats)*100:5.2f}%")
    print(f"\n合计 {total} 事件, 触发 {alarmed} ({alarmed/total*100:.2f}%): " +
          ", ".join(f"{k}={v}" for k, v in grand.items()))


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'golden'
    src = Path(sys.argv[2]) if len(sys.argv) > 2 else DEFAULT_DIR
    if not src.exists():
        print(f"数据目录不存在: {src}")
        sys.exit(1)
    {'golden': cmd_golden, 'baseline': cmd_baseline, 'dryrun': cmd_dryrun}[cmd](src)
