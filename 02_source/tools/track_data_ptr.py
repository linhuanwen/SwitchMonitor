#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
全新策略：
1. 解析目录项，获取每个道岔的 F4(数据块偏移), F7(采样点数), F6(数据字节)
2. F6 = F7 * 32，所以每采样32字节
3. data_ptr = N * 0x1227，追踪这些指针的实际位置
4. 检查 data_ptr 指向的内容到底在哪里
"""
import struct, sys, os
from datetime import datetime

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POWER_1 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\1.hbf"
POWER_2 = r"D:\Vibe coding\04 DCjiance\SwitchMonitor\03_raw_data\panyu\道岔动作功率曲线\2.hbf"

SWITCH_IDS = ['1-J','1-X','3-J','3-X','5-J','5-X','7-J','7-X','9-J','9-X',
              '11-J','11-X','13-J','13-X','15-J','15-X','17-J','17-X','19-J','19-X',
              '21-J','21-X','2-J','2-X','4-J','4-X','6-J','6-X','8-J','8-X']

def read_at(filepath, offset, size):
    with open(filepath, 'rb') as f:
        f.seek(offset)
        return f.read(size)

def parse_dir(filepath):
    """解析完整目录"""
    data = read_at(filepath, 0, 0x30000)
    entries = {}
    for sw_id in SWITCH_IDS:
        pattern = sw_id.encode('ascii')
        p = data.find(pattern)
        if p == -1 or p >= 0x28000:
            continue
        desc = data[p+0x70:p+0x70+52]
        fields = [struct.unpack('<I', desc[i:i+4])[0] for i in range(0, 52, 4)]
        f4, f7, f6 = fields[4], fields[7], fields[6]
        # 放宽条件
        if 0 < f7 < 5000 and f6 > 0:
            entries[sw_id] = {
                'offset': p, 'F0': fields[0], 'F1': fields[1],
                'F2': fields[2], 'F3': fields[3], 'F4': f4,
                'F5': fields[5], 'F6': f6, 'F7': f7,
                'F8': fields[8], 'F9': fields[9],
                'F10': fields[10], 'F11': fields[11], 'F12': fields[12],
            }
    return entries

def parse_records_at_f4(filepath, f4, f7, max_records=20):
    """读取F4偏移处的32B记录，识别真实记录（不是标记内的假匹配）"""
    raw = read_at(filepath, f4, max_records * 40 + 256)

    # 找真实的32B对齐记录：u32[0]=0x1227, u32[4]=0, u32[5]递增
    records = []
    for i in range(0, min(len(raw) - 32, max_records * 40), 4):
        rec = raw[i:i+32]
        if len(rec) < 32:
            continue
        u32s = struct.unpack('<8I', rec)

        # 验证: 首字段是标记(0x1227或0x3277), 第5字段为0, 第6字段递增
        if u32s[0] not in (0x1227, 0x3277):
            continue
        if u32s[4] != 0:
            continue
        seq = u32s[5]
        if seq < 1 or seq > 100000:
            continue

        # 检查前一条记录的seq
        if records and seq != records[-1]['seq'] + 1:
            continue

        records.append({
            'pos': f4 + i,
            'seq': seq,
            'u32s': u32s,
            'marker': u32s[0],
            'data_ptr': u32s[7],
            'const_field': u32s[6],  # 可能是采样点数
            'ts_high': u32s[2],
            'ts_low': u32s[3],
        })

        if len(records) >= max_records:
            break

    return records

def follow_data_ptr(filepath, records, f4):
    """追踪data_ptr指向的位置，检查是否有实际数据"""
    for rec in records[:5]:
        data_ptr = rec['data_ptr']
        const = rec['const_field']

        # data_ptr 可能是相对偏移或绝对偏移
        # 尝试作为相对于F4的偏移
        abs_data = f4 + data_ptr

        print(f"\n  记录 seq={rec['seq']}: data_ptr=0x{data_ptr:x} const=0x{const:x}({const})")
        print(f"    尝试 绝对偏移 0x{abs_data:x}")

        # 读取该位置的数据
        raw = read_at(filepath, abs_data, 1024)
        nz = sum(1 for b in raw[:256] if b != 0)
        print(f"    非零字节: {nz}/256")

        if nz > 30:
            print(f"    Hex前128:")
            for j in range(0, min(128, len(raw)), 16):
                hex_str = ' '.join(f'{b:02x}' for b in raw[j:j+16])
                print(f"      {j:4d}: {hex_str}")

            # 尝试float32
            f32 = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(60, len(raw)//4))]
            valid = [v for v in f32 if abs(v) < 1e6 and abs(v) > 1e-10]
            print(f"    float32有效值: {[round(v,3) for v in valid[:30]]}")

        # 也尝试 data_ptr 作为相对于块开始的偏移
        # 如果数据区在索引记录之后
        data_after_idx = f4 + len(records) * 32
        rel_data = data_after_idx + data_ptr
        print(f"    尝试 索引后+data_ptr 0x{rel_data:x}")

        raw2 = read_at(filepath, rel_data, 1024)
        nz2 = sum(1 for b in raw2[:256] if b != 0)
        print(f"    非零字节: {nz2}/256")

        if nz2 > 30:
            f32 = [struct.unpack('<f', raw2[j*4:(j+1)*4])[0] for j in range(min(60, len(raw2)//4))]
            valid = [v for v in f32 if abs(v) < 1e6 and abs(v) > 1e-10]
            print(f"    float32有效值: {[round(v,3) for v in valid[:30]]}")

def main():
    print("HBF 目录解析 + data_ptr 追踪")
    print("=" * 70)

    for fpath, label in [(POWER_1, "功率1.hbf"), (POWER_2, "功率2.hbf")]:
        print(f"\n{'='*70}")
        print(f"{label}")
        print(f"{'='*70}")

        entries = parse_dir(fpath)
        print(f"解析到 {len(entries)} 个目录项")

        # 显示所有目录项的关键字段
        print(f"\n{'道岔':>6} {'F4(偏移)':>12} {'F7(采样数)':>12} {'F6(字节)':>12} {'F6/F7':>8} {'F3':>12} {'F8':>12}")
        print("-" * 80)
        for sw_id, e in sorted(entries.items()):
            ratio = e['F6'] / e['F7'] if e['F7'] > 0 else 0
            print(f"{sw_id:>6} 0x{e['F4']:010x} {e['F7']:>12} {e['F6']:>12} {ratio:>8.1f} 0x{e['F3']:010x} 0x{e['F8']:010x}")

        # 选几个 F7>0 的道岔深入分析
        valid_switches = [(sw, e) for sw, e in entries.items() if e['F7'] > 50 and e['F6'] > 1000]
        print(f"\n深入分析 {len(valid_switches)} 个有效道岔:")

        for sw_id, e in valid_switches[:5]:
            f4, f7, f6 = e['F4'], e['F7'], e['F6']
            print(f"\n{'─'*60}")
            print(f"道岔 {sw_id}: F4=0x{f4:x} F7={f7} F6={f6} (每采样{f6/f7:.1f}字节)")

            records = parse_records_at_f4(fpath, f4, f7, max_records=10)
            if not records:
                print(f"  ❌ 无法解析记录")
                # dump raw hex
                raw = read_at(fpath, f4, 256)
                print(f"  F4处原始hex:")
                for j in range(0, min(256, len(raw)), 16):
                    hex_str = ' '.join(f'{b:02x}' for b in raw[j:j+16])
                    print(f"    {j:4d}: {hex_str}")
                continue

            print(f"  解析到 {len(records)} 条记录")
            print(f"  const_field: 0x{records[0]['const_field']:x} = {records[0]['const_field']}")
            print(f"  marker: 0x{records[0]['marker']:04x} ({'功率' if records[0]['marker']==0x1227 else '电流'})")

            # 显示前5条记录
            for r in records[:5]:
                ts_combined = (r['ts_low'] << 32) | r['ts_high']
                print(f"    seq={r['seq']:4d} data_ptr=0x{r['data_ptr']:06x} "
                      f"u32[1]=0x{r['u32s'][1]:08x} ts=0x{r['ts_high']:08x}:0x{r['ts_low']:08x}")

            # 追踪 data_ptr
            follow_data_ptr(fpath, records, f4)

    # 额外：检查是否有数据存储在完全不同的偏移
    print(f"\n{'='*70}")
    print("全局搜索：找不含0x1227标记但有连续非零的非索引数据区")
    print(f"{'='*70}")

    for fpath, label in [(POWER_1, "功率1")]:
        fsize = os.path.getsize(fpath)
        # 检查几个特定偏移范围
        test_offsets = [
            0x1227,      # data_ptr的基准值
            0x244e,      # 2*0x1227
            0x9c0c,     # 之前看到的块起始
            0x9c14,     # 记录起始+8
        ]
        for off in test_offsets:
            if off >= fsize:
                continue
            raw = read_at(fpath, off, 256)
            nz = sum(1 for b in raw if b != 0)
            print(f"\n0x{off:x} ({off}): nz={nz}/256")
            if nz > 20:
                for j in range(0, min(256, len(raw)), 16):
                    hex_str = ' '.join(f'{b:02x}' for b in raw[j:j+16])
                    print(f"  {j:4d}: {hex_str}")
                f32 = [struct.unpack('<f', raw[j*4:(j+1)*4])[0] for j in range(min(30, len(raw)//4))]
                print(f"  f32: {[round(v,3) for v in f32[:20]]}")

    print("\n✅ 分析完成")

if __name__ == '__main__':
    main()
