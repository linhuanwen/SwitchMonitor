#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
HBF 聚焦分析 — 直接读取数据块原始hex，定位实际曲线数据
参考: 三水北站 功率曲线 0→spike(3-4KW)→steady(0.2-0.4KW)→0, 40ms采样, ~300点
"""
import struct, sys, os
from datetime import datetime
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HBF_POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
HBF_CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_real_entries(filepath):
    """只解析真正的目录项（F4在后256KB内的）"""
    data = read_at(filepath, 0, 0x28000)
    file_size = os.path.getsize(filepath)
    entries = []
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        pos = 0
        while True:
            p = data.find(pattern, pos)
            if p == -1 or p >= 0x28000:
                break
            entry = data[p:p+256]
            if len(entry) < 256:
                break
            # 检查这是不是一个真正的目录项: offset 0x70 处的 descriptor 应该有意义
            desc_start = p + 0x70
            desc = data[desc_start:desc_start+52]
            fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]

            f4 = fields[4]
            f7 = fields[7]
            f6 = fields[6]

            # 验证: F4应在合理范围内，F7/F6应合理
            # F11 通常是 256
            if not (0 < f7 < 2000 and 0 < f6 < 100000):
                pos = p + 1
                continue

            entries.append({
                'offset': p, 'switch_id': sw_id,
                'F3': fields[3], 'F4': f4, 'F5': fields[5],
                'F6': f6, 'F7': f7,
                'F8': fields[8], 'F11': fields[11],
            })
            pos = p + 1

    entries.sort(key=lambda e: e['offset'])
    return entries

def dump_f4_block(filepath, f4, f6, f7, switches, file_size):
    """详细dump一个F4数据块"""
    print(f"\n{'='*70}")
    print(f"Block @ 0x{f4:09x}: switches={switches} F6={f6} F7={f7}")

    # 读取数据块
    read_size = min(f6 + 16384, file_size - f4, 262144)
    raw = read_at(filepath, f4, read_size)

    if len(raw) < 32:
        print(f"  数据不足")
        return None

    # 检查是否是全零
    non_zero_count = sum(1 for b in raw[:min(4096, len(raw))] if b != 0)
    if non_zero_count == 0:
        print(f"  ❌ 全零数据块")
        return None

    print(f"  非零字节数: {non_zero_count}/{min(4096, len(raw))}")

    # 查找 0x1227 标记
    marker_positions = []
    for i in range(0, len(raw) - 4, 4):
        if raw[i:i+4] == b'\x27\x12\x00\x00':
            marker_positions.append(i)

    print(f"  0x1227 标记出现 {len(marker_positions)} 次, 位置: {[f'0x{f4+i:x}' for i in marker_positions[:10]]}")

    # 打印前面的原始数据
    print(f"\n  原始数据 (前512B, 每行32B):")
    for i in range(0, min(512, len(raw)), 32):
        line = raw[i:i+32]
        # 显示hex
        hex_part = ' '.join(f'{b:02x}' for b in line[:16])
        hex_part2 = ' '.join(f'{b:02x}' for b in line[16:])

        # 尝试解析为 uint32
        u32s = [struct.unpack('<I', line[j:j+4])[0] for j in range(0, min(32, len(line)), 4)]
        u32_str = ' | '.join(f'{v:>12d}' for v in u32s)

        marker = " ⭐" if line[:4] == b'\x27\x12\x00\x00' else ""
        print(f"    +{i:4d}: {hex_part}  {hex_part2}{marker}")
        if i < 128 or any(v != 0 for v in u32s[:3]):
            print(f"           {u32_str}")

    # 如果有0x1227标记，详细分析
    if marker_positions:
        mp = marker_positions[0]
        print(f"\n  首个0x1227标记 @ +{mp}:")

        # 该标记之后的32B子索引记录
        sub = raw[mp:mp+32]
        print(f"    子索引记录:")
        for j in range(0, 32, 16):
            print(f"      {' '.join(f'{b:02x}' for b in sub[j:j+16])}")
        u32s = [struct.unpack('<I', sub[j:j+4])[0] for j in range(0, 32, 4)]
        for j, v in enumerate(u32s):
            desc = ""
            if 1_780_000_000 < v < 1_790_000_000:
                desc = f" ← {datetime.fromtimestamp(v).strftime('%Y-%m-%d %H:%M:%S')}"
            elif 1_700_000_000 < v < 1_800_000_000:
                desc = f" ← {datetime.fromtimestamp(v)}"
            elif 50 < v < 2000:
                desc = f" ← 可能采样数={v}"
            print(f"      u32[{j}]={v}{desc}")

        # 找下一个标记
        if len(marker_positions) > 1:
            next_mp = marker_positions[1]
            between = raw[mp+32:next_mp]
            print(f"\n    到下一个标记间距: {next_mp - mp} bytes (子索引间数据: {len(between)} bytes)")
            print(f"    中间数据(前256B):")
            for i in range(0, min(256, len(between)), 32):
                print(f"      {' '.join(f'{b:02x}' for b in between[i:i+32])}")

            # 尝试解读中间数据
            # 如果是每采样32字节，F7个采样
            data_size = len(between)
            if f7 > 0:
                bps = data_size // f7
                if bps > 0:
                    print(f"\n    数据大小={data_size}, F7={f7}, 每采样={bps}bytes")

                    # 尝试 float32 编码 (取每32B的第0个float32)
                    if bps >= 4:
                        vals = []
                        for i in range(min(f7, 800)):
                            off = i * bps
                            if off + 4 <= len(between):
                                v = struct.unpack('<f', between[off:off+4])[0]
                                vals.append(v)

                        print(f"    float32[0] (前60): {[round(v,3) for v in vals[:60]]}")
                        print(f"    float32[0] (中段): {[round(v,3) for v in vals[60:120]]}")
                        print(f"    float32[0] (尾20): {[round(v,3) for v in vals[-20:]]}")

                        # 检查是否匹配道岔曲线
                        z10 = sum(1 for v in vals[:10] if abs(v) < 0.05)
                        peaks = [v for v in vals[:30] if v > 1.0]
                        steady = [v for v in vals[20:len(vals)-10] if 0.1 < v < 1.0]
                        t0 = sum(1 for v in vals[-10:] if abs(v) < 0.05)
                        score = (z10>=4)*1 + (len(peaks)>=1)*2 + (len(steady)>10)*2 + (t0>=3)*1
                        print(f"    曲线匹配: z10={z10} peaks={len(peaks)} steady={len(steady)} t0={t0} score={score}")

        # 子索引后面的数据
        sub_count = 0
        for i in range(0, len(raw), 32):
            if raw[i:i+4] == b'\x27\x12\x00\x00':
                sub_count += 1
            elif sub_count > 0:
                break

        data_start = sub_count * 32
        print(f"\n  子索引记录数: {sub_count}")
        print(f"  数据区起始: +{data_start}")

        curve_data = raw[data_start:data_start + min(f6 * 2, 65536)]
        print(f"  数据区大小: {len(curve_data)} bytes")
        print(f"  数据区(前256B):")
        for i in range(0, min(256, len(curve_data)), 32):
            print(f"    +{i:4d}: {' '.join(f'{b:02x}' for b in curve_data[i:i+32])}")

    return raw

def scan_for_power_curves(filepath, label):
    """全局扫描，寻找功率曲线的 '指纹' (连续非零段)"""
    print(f"\n{'='*70}")
    print(f"全局扫描: {os.path.basename(filepath)} ({label})")

    file_size = os.path.getsize(filepath)

    # 策略: 每64KB采样4KB，找非零密集段
    segments = []
    step = 0x10000  # 64KB
    sample = 0x1000  # 4KB

    for start in range(0, file_size, step):
        chunk = read_at(filepath, start, sample)
        nz = sum(1 for b in chunk if b != 0)
        if nz > len(chunk) * 0.25:  # 超过25%非零
            segments.append((start, nz / len(chunk)))

    print(f"  非零段数: {len(segments)}")

    # 重点: 在非零密集区找曲线
    for abs_off, density in segments[:20]:
        ctx = read_at(filepath, abs_off, min(2048, file_size - abs_off))

        # 尝试 float32 滑动窗口
        for byte_off in range(0, len(ctx) - 400, 4):
            # 前10个接近0?
            lead = [struct.unpack('<f', ctx[byte_off+i*4:byte_off+i*4+4])[0] for i in range(10)]
            if sum(1 for v in lead if abs(v) < 0.05) < 7:
                continue

            # 找尖峰
            vals = [struct.unpack('<f', ctx[byte_off+i*4:byte_off+i*4+4])[0] for i in range(min(300, (len(ctx)-byte_off)//4))]
            peaks = [(i, v) for i, v in enumerate(vals[:40]) if v > 1.5]
            if not peaks:
                continue

            # 检查平稳段
            steady_start = peaks[-1][0] + 10
            steady_vals = vals[steady_start:len(vals)-15]
            if not steady_vals:
                continue
            mean_steady = sum(steady_vals) / len(steady_vals)
            tail = vals[-10:]

            if 0.1 < mean_steady < 0.8 and sum(1 for v in tail if abs(v) < 0.05) >= 5:
                abs_data = abs_off + byte_off
                print(f"\n  ✅ 候选曲线 @ 0x{abs_data:09x} (密度={density:.1%})")
                print(f"    前10: {[round(v,3) for v in vals[:10]]}")
                print(f"    尖峰: {[(i, round(v,2)) for i,v in peaks[:3]]}")
                print(f"    平稳段均值: {mean_steady:.3f} (len={len(steady_vals)})")
                print(f"    尾部: {[round(v,3) for v in tail]}")
                print(f"    前50: {[round(v,3) for v in vals[:50]]}")
                return abs_data  # 找到一个就返回

    print(f"  未找到匹配的曲线")
    return None

def main():
    # 功率文件
    pf_1 = os.path.join(HBF_POWER_DIR, '1.hbf')
    pf_2 = os.path.join(HBF_POWER_DIR, '2.hbf')
    # 电流文件
    cf_1 = os.path.join(HBF_CURRENT_DIR, '1.hbf')
    cf_2 = os.path.join(HBF_CURRENT_DIR, '2.hbf')
    cf_3 = os.path.join(HBF_CURRENT_DIR, '3.hbf')

    print("HBF 聚焦分析 - 直接定位曲线数据")
    print("="*70)

    # 步骤1: 解析目录项 (只保留有效的)
    for fpath, label in [(pf_1, "功率1.hbf"), (cf_1, "电流1.hbf")]:
        entries = parse_real_entries(fpath)
        file_size = os.path.getsize(fpath)
        print(f"\n{label}: {len(entries)} 个有效目录项, 文件大小={file_size:,}")

        # 按F4分组
        by_f4 = defaultdict(list)
        for e in entries:
            by_f4[e['F4']].append(e)

        for f4, group in sorted(by_f4.items()):
            switches = [e['switch_id'] for e in group]
            f7s = [e['F7'] for e in group]
            f6s = [e['F6'] for e in group]
            # 仅分析有非零数据的块
            dump_f4_block(fpath, f4, min(f6s), f7s[0], switches, file_size)

    # 步骤2: 全局扫描功率文件
    scan_for_power_curves(pf_1, "功率1.hbf")
    scan_for_power_curves(pf_2, "功率2.hbf")

    # 步骤3: 扫描电流文件
    scan_for_power_curves(cf_1, "电流1.hbf")
    scan_for_power_curves(cf_2, "电流2.hbf")
    scan_for_power_curves(cf_3, "电流3.hbf")

    print("\n✅ 聚焦分析完成")

if __name__ == '__main__':
    main()
