using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SwitchMonitor.Station
{
    /// <summary>
    /// DC.ini 解析器 — 从 CSM 站机配置文件提取通道→道岔→文件索引映射。
    ///
    /// DC.ini 是 GBK 编码的 INI 文件，结构与 digit.ini 类似：
    ///   [模拟量数目]
    ///   模拟量数目=1
    ///   [模拟量1]
    ///   名称=道岔动作电流
    ///   总路数=64
    ///   1  =1-J1-A        ,          2,    0 ,      0  ,   0,     10, ...
    ///   2  =1-J1-B        ,          2,    1 ,      0  ,   0,     10, ...
    ///   ...
    ///
    /// 数据行格式：序号 = 通道名, 类型, 文件索引, 分组索引, ...
    /// 通道名命名规则：{道岔号}-{J/X}{子号?}-{相位}
    ///   例: "1-J1-A" → 道岔1, 尖轨1, A相电流
    ///       "3-X-P"  → 道岔3, 芯轨,  功率
    ///
    /// 关键：头部 section 是 GBK 中文（在非中文系统上会乱码），
    /// 但数据行全是 ASCII 字符，不受编码影响。
    /// </summary>
    public static class DcIniParser
    {
        // 匹配 DC.ini 数据行：
        //   N  =NAME    ,    type,  file_idx,  group_idx,  ...
        private static readonly Regex ChannelLineRegex = new Regex(
            @"^\s*(\d+)\s*=\s*([^,]+?)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)",
            RegexOptions.Compiled);

        // 通道名解析，支持两种格式：
        //   标准格式: {道岔号}-{J/X}{子号?}-{相位}
        //     "1-J1-A" → groups: 1, J, 1, A
        //     "3-X-P"  → groups: 3, X, ,  P
        //   单机格式: {道岔号}-{相位}（无 J/X 标识）
        //     "9-A"    → groups: 9, ,  ,  A
        private static readonly Regex ChannelNameRegex = new Regex(
            @"^(\d+)-(?:([JX])(\d*)-)?([ABCP])$",
            RegexOptions.Compiled);

        /// <summary>
        /// 解析 DC.ini 文件，返回所有模拟量通道
        /// </summary>
        /// <param name="filePath">DC.ini 绝对路径</param>
        /// <returns>通道列表；文件不存在或解析失败返回空列表</returns>
        public static List<DcChannel> Parse(string filePath)
        {
            var channels = new List<DcChannel>();

            if (!File.Exists(filePath))
                return channels;

            string[] lines;
            try
            {
                // 优先 GBK，失败回退系统默认编码
                Encoding enc;
                try { enc = Encoding.GetEncoding("GBK"); }
                catch { enc = Encoding.Default; }

                lines = File.ReadAllLines(filePath, enc);
            }
            catch
            {
                // 编码失败 → 用 latin-1（字节保留）读 ASCII 行
                try { lines = File.ReadAllLines(filePath, Encoding.GetEncoding("iso-8859-1")); }
                catch { return channels; }
            }

            foreach (string line in lines)
            {
                var match = ChannelLineRegex.Match(line);
                if (!match.Success)
                    continue;

                string name = match.Groups[2].Value.Trim();

                // 解析通道名
                var nameMatch = ChannelNameRegex.Match(name);
                if (!nameMatch.Success)
                    continue;

                int chanNum;
                int analogType;
                int fileIdx;
                int groupIdx;
                if (!int.TryParse(match.Groups[1].Value, out chanNum)) continue;
                if (!int.TryParse(match.Groups[3].Value, out analogType)) continue;
                if (!int.TryParse(match.Groups[4].Value, out fileIdx)) continue;
                if (!int.TryParse(match.Groups[5].Value, out groupIdx)) continue;

                string switchNo = nameMatch.Groups[1].Value;
                string beamType = nameMatch.Groups[2].Value;   // J 或 X（单机格式为空）
                string subId = nameMatch.Groups[3].Value;      // "1", "2", 或 ""
                char phase = nameMatch.Groups[4].Value[0];     // A/B/C/P

                // 单机格式 "9-A" → machineId="9"；标准格式 "1-J-A" → machineId="1-J"
                string machineId = string.IsNullOrEmpty(beamType)
                    ? switchNo
                    : switchNo + "-" + beamType + subId;

                channels.Add(new DcChannel
                {
                    ChannelNumber = chanNum,
                    Name = name,
                    AnalogType = analogType,
                    FileIndex = fileIdx,
                    GroupIndex = groupIdx,
                    MachineId = machineId,
                    Phase = phase
                });
            }

            return channels;
        }

        /// <summary>
        /// 从通道列表推导转辙机组。
        ///
        /// 规则：
        ///   - 同一 machineId 的 A/B/C 通道 → 一组电流，取 min(fileIndex) 作为 dataFileIndex
        ///   - P 通道 → 功率文件（通常 = dataFileIndex + 3）
        ///   - ZYJ7: 每道岔 2 台机器 (J, X)，每台 4 文件 → 8 组
        ///   - ZDJ9: 每道岔 4 台机器 (J1,J2,X1,X2)，每台 4 文件 → 16 组
        /// </summary>
        public static List<SwitchGroupDef> DeriveSwitchGroups(List<DcChannel> channels)
        {
            // 按机器 ID 分组
            var machineChannels = new Dictionary<string, List<DcChannel>>();
            foreach (var ch in channels)
            {
                if (string.IsNullOrEmpty(ch.MachineId))
                    continue;

                List<DcChannel> list;
                if (!machineChannels.TryGetValue(ch.MachineId, out list))
                {
                    list = new List<DcChannel>();
                    machineChannels[ch.MachineId] = list;
                }
                list.Add(ch);
            }

            var groups = new List<SwitchGroupDef>();

            foreach (var kvp in machineChannels)
            {
                string machineId = kvp.Key;
                var chs = kvp.Value;

                // 找电流通道的最小 fileIndex
                int minCurrentIdx = int.MaxValue;
                bool hasCurrent = false;
                foreach (var ch in chs)
                {
                    if (ch.IsCurrent && ch.FileIndex < minCurrentIdx)
                    {
                        minCurrentIdx = ch.FileIndex;
                        hasCurrent = true;
                    }
                }

                if (!hasCurrent)
                    continue; // 只有功率、没有电流的通道 → 跳过

                groups.Add(new SwitchGroupDef
                {
                    Id = machineId,
                    Label = machineId,
                    DataFileIndex = minCurrentIdx
                });
            }

            // 按 dataFileIndex 升序排列（保证显示顺序与 DC.ini 定义一致）
            groups.Sort((a, b) => a.DataFileIndex.CompareTo(b.DataFileIndex));

            return groups;
        }

        /// <summary>
        /// 一步到位：解析 DC.ini 并推导 switchGroups
        /// </summary>
        public static List<SwitchGroupDef> ParseAndDerive(string filePath)
        {
            var channels = Parse(filePath);
            return DeriveSwitchGroups(channels);
        }

        /// <summary>
        /// 判断通道是否为电流类型（兼容两种编码：1/2=电流）
        /// </summary>
        private static bool IsCurrentType(int analogType)
        {
            return analogType == 1 || analogType == 2;
        }
    }
}
