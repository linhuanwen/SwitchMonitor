#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Final approach: 用32B记录去交错方式提取 2.hbf 的完整功率曲线
"""
import struct
import sys
import os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线"
CURRENT_DIR = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作电流曲线"

def read_at(path, offset, size):
    with open(path, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def extract_32b_channels(raw, channel=4, max_records=5000):
    """
    从32B记录格式中提取指定通道的数据
    每个记录=8×float32(32B), channel 4(offset16-19)通常是功率
    """
    n = min(max_records, len(raw) // 32)
    values = [struct.unpack('<f', raw[j*32+channel*4:j*32+channel*4+4])[0] for j in range(n)]
    return values

def extract_all_channels(raw, max_records=5000):
    """提取全部8个通道"""
    n = min(max_records, len(raw) // 32)
    channels = []
    for ch in range(8):
        vals = [struct.unpack('<f', raw[j*32+ch*4:j*32+ch*4+4])[0] for j in range(n)]
        channels.append(vals)
    return channels

def describe_curve(vals):
    """描述曲线特征"""
    nz = [(i, v) for i, v in enumerate(vals) if abs(v) > 0.01]
    if not nz:
        return "全零"

    start = nz[0][0]
    end = nz[-1][0]
    peak = max(vals)
    peak_idx = vals.index(peak)

    # 上升/下降特征
    pre_peak = vals[max(0,peak_idx-30):peak_idx]
    post_peak = vals[peak_idx+1:min(len(vals), peak_idx+50)]

    rising = all(pre_peak[i] <= pre_peak[i+1] + 0.05 for i in range(len(pre_peak)-1)) if len(pre_peak)>1 else False
    falling = all(post_peak[i] >= post_peak[i+1] - 0.05 for i in range(len(post_peak)-1)) if len(post_peak)>1 else False

    return {
        'nz_start': start,
        'nz_end': end,
        'nz_len': end - start + 1,
        'peak': peak,
        'peak_idx': peak_idx,
        'gradual_rise': rising,
        'gradual_fall': falling,
        'first_nz10': [round(v,2) for v in vals[start:start+10]],
        'around_peak': [round(v,2) for v in vals[max(0,peak_idx-10):peak_idx+10]],
        'last_nz10': [round(v,2) for v in vals[end-10:end+1]],
    }


def main():
    fpath = os.path.join(POWER_DIR, '2.hbf')
    file_size = os.path.getsize(fpath)

    print("2.hbf 32B记录去交错曲线提取")
    print("="*70)

    # 读目录项
    data = read_at(fpath, 0, 0x200000)
    SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
                  '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
                  '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

    entries = []
    for sw_id in SWITCH_IDS:
        pos = data.find(sw_id.encode('ascii'))
        if pos == -1:
            continue
        block = data[pos:pos+256]
        sid = block[:8].rstrip(b'\x00').decode('ascii', errors='ignore')
        if sid != sw_id:
            continue
        f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
        f4, f5, f7, f9 = f[4], f[5], f[7], f[9]
        if 0 < f4 < file_size and 0 < f9 < file_size:
            entries.append((sw_id, f4, f5, f7, f9))

    print(f"有效目录项: {len(entries)}")

    # 对每个道岔，用32B格式提取F9数据
    print(f"\n{'='*70}")
    print(f"32B记录/通道4(功率) 曲线提取")
    print(f"{'='*70}")

    switch_curves = {}

    for sw_id, f4, f5, f7, f9 in entries:
        # 先确定实际数据大小: 用 F4 子索引中的实际事件数 × 块大小
        sub_raw = read_at(fpath, f4, min(f7 * 32, 0x200000))

        # 统计实际事件
        actual_events = 0
        event_ptrs = []
        for i in range(f7):
            off = i * 32
            u32s = [struct.unpack_from('<I', sub_raw, off + j*4)[0] for j in range(8)]
            if u32s[0] == 0x1227:  # Type A
                dp = u32s[7]
                if dp > 0:
                    actual_events += 1
                    event_ptrs.append((i, dp, 'A'))
            elif 1_700_000_000 < u32s[0] < 1_800_000_000:  # Type B
                dp = u32s[6]
                if dp > 0:
                    actual_events += 1
                    event_ptrs.append((i, dp, 'B'))

        # 用32B记录格式读F9处数据
        # 读足够大的数据块
        read_size = min(512 * 1024, file_size - f9)
        raw_data = read_at(fpath, f9, read_size)

        # 尝试不同通道
        best_ch = None
        best_desc = None
        for ch in range(8):
            vals = extract_32b_channels(raw_data, channel=ch, max_records=2000)
            desc = describe_curve(vals)
            if desc != "全零" and desc['nz_len'] > 30:
                if best_ch is None or desc['nz_len'] > best_desc['nz_len']:
                    best_ch = ch
                    best_desc = desc

        # 同时也试 sequential float32
        f32 = [struct.unpack('<f', raw_data[j*4:(j+1)*4])[0] for j in range(min(2000, len(raw_data)//4))]
        f32_desc = describe_curve(f32)

        has_32b = best_ch is not None and best_desc['nz_len'] > 30
        has_f32 = f32_desc != "全零" and f32_desc['nz_len'] > 30

        if has_32b or has_f32:
            # 确定用哪种格式
            if has_f32 and not has_32b:
                use_format = 'float32_seq'
                desc = f32_desc
            elif has_32b and not has_f32:
                use_format = f'32B_ch{best_ch}'
                desc = best_desc
            else:
                # 两种都有 — 选更好的
                if f32_desc['peak'] > best_desc['peak'] * 1.5 or f32_desc['nz_len'] > best_desc['nz_len'] * 2:
                    use_format = 'float32_seq'
                    desc = f32_desc
                else:
                    use_format = f'32B_ch{best_ch}'
                    desc = best_desc

            print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} 实际事件={actual_events}")
            print(f"    格式={use_format}  非零点={desc['nz_len']}  峰值={desc['peak']:.3f} @ idx={desc['peak_idx']}")
            print(f"    前段10: {desc['first_nz10']}")
            print(f"    峰值附近: {desc['around_peak']}")
            print(f"    尾段10: {desc['last_nz10']}")
            print(f"    渐变上升: {desc['gradual_rise']}  渐变下降: {desc['gradual_fall']}")

            switch_curves[sw_id] = {
                'format': use_format,
                'f9': f9,
                'desc': desc,
                'actual_events': actual_events,
                'f7': f7,
            }
        else:
            print(f"\n  [{sw_id}] F9=0x{f9:x} F7={f7} — 无有效数据")

    # 统计
    print(f"\n{'='*70}")
    print(f"汇总")
    print(f"{'='*70}")
    with_data = [(sw, c) for sw, c in switch_curves.items()]
    print(f"有数据的道岔: {len(with_data)}/30")
    for sw, c in with_data:
        print(f"  {sw}: {c['format']}  {c['desc']['nz_len']}点 峰值{c['desc']['peak']:.2f} "
              f"实件={c['actual_events']}/{c['f7']} "
              f"渐升={c['desc']['gradual_rise']} 渐降={c['desc']['gradual_fall']}")

    # 如果一个开关都没有好的曲线数据，尝试电流文件
    if len(with_data) < 5:
        print(f"\n⚠️ 功率数据不足，尝试电流文件...")
        for cf_name in ['1.hbf', '2.hbf', '3.hbf']:
            cf_path = os.path.join(CURRENT_DIR, cf_name)
            if not os.path.exists(cf_path):
                continue
            cf_size = os.path.getsize(cf_path)
            cf_data = read_at(cf_path, 0, 0x200000)
            print(f"\n{cf_name}:")
            for sw_id in ['1-J', '3-J', '5-J', '7-J']:
                pos = cf_data.find(sw_id.encode('ascii'))
                if pos == -1:
                    continue
                block = cf_data[pos:pos+256]
                f = [struct.unpack_from('<I', block[0x70:0x70+52], j*4)[0] for j in range(13)]
                f9 = f[9]
                if 0 < f9 < cf_size:
                    raw = read_at(cf_path, f9, 16384)
                    # 检查是否有非零数据
                    nz = sum(1 for b in raw if b != 0)
                    if nz > 0:
                        print(f"  {sw_id}: F9=0x{f9:x} 非零字节={nz}/{len(raw)}")
                        # 各种格式
                        f32_data = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(100, len(raw)//4))]
                        ch4 = [struct.unpack('<f', raw[j*32+16:j*32+20])[0] for j in range(min(100, len(raw)//32))]
                        print(f"    f32: {[round(v,3) for v in f32_data[:30]]}")
                        print(f"    32B/ch4: {[round(v,3) for v in ch4[:30]]}")


if __name__ == '__main__':
    main()
