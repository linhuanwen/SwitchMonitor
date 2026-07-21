using System.Collections.Generic;

namespace SwitchMonitor.Station
{
    /// <summary>
    /// DC.ini 中的单条模拟量通道定义
    /// </summary>
    public class DcChannel
    {
        /// <summary>通道序号（1-based）</summary>
        public int ChannelNumber { get; set; }

        /// <summary>通道名，如 "1-J1-A", "3-X-P"</summary>
        public string Name { get; set; }

        /// <summary>
        /// 模拟量类型：
        ///   1 或 2 = 电流通道
        ///   0 或 6 = 功率通道
        /// </summary>
        public int AnalogType { get; set; }

        /// <summary>SwitchCurve(N) 文件编号</summary>
        public int FileIndex { get; set; }

        /// <summary>分组索引</summary>
        public int GroupIndex { get; set; }

        /// <summary>所属转辙机 ID，如 "1-J1"（从通道名剥离相位后缀）</summary>
        public string MachineId { get; set; }

        /// <summary>相位：'A' / 'B' / 'C' / 'P'</summary>
        public char Phase { get; set; }

        /// <summary>是否为功率通道</summary>
        public bool IsPower
        {
            get { return Phase == 'P'; }
        }

        /// <summary>是否为电流通道</summary>
        public bool IsCurrent
        {
            get { return Phase == 'A' || Phase == 'B' || Phase == 'C'; }
        }
    }

    /// <summary>
    /// 转辙机组定义（DC.ini 推导或 site.json 指定）
    /// </summary>
    public class SwitchGroupDef
    {
        /// <summary>转辙机 ID，如 "1-J1"</summary>
        public string Id { get; set; }

        /// <summary>显示标签</summary>
        public string Label { get; set; }

        /// <summary>电流文件的起始索引（SwitchCurve(N)）</summary>
        public int DataFileIndex { get; set; }

        /// <summary>功率文件索引（= DataFileIndex + 3）</summary>
        public int PowerFileIndex
        {
            get { return DataFileIndex + 3; }
        }

        /// <summary>转辙机型号（"ZYJ7" / "ZDJ9"，手动配置，可为 null）</summary>
        public string SwitchType { get; set; }
    }

    /// <summary>
    /// 站点清单 — site.json 或自动发现的结果
    /// </summary>
    public class StationManifest
    {
        /// <summary>站点标识（目录名），如 "sanshuibei"</summary>
        public string Id { get; set; }

        /// <summary>站点显示名，如 "三水北站"</summary>
        public string Name { get; set; }

        /// <summary>监测数据格式："CSM2010" / "shiqi"</summary>
        public string DataFormat { get; set; }

        /// <summary>转辙机类型："ZYJ7" / "ZDJ9"</summary>
        public string SwitchType { get; set; }

        /// <summary>厂商类型："huihuang" | "bangcheng" | "tonghao"</summary>
        public string VendorType { get; set; }

        /// <summary>每组道岔的转辙机台数（2=ZYJ7, 4=ZDJ9）</summary>
        public int MachinesPerSwitch { get; set; }

        /// <summary>道岔组数</summary>
        public int SwitchCount { get; set; }

        /// <summary>数据源目录（CSV 文件所在）</summary>
        public string DataSourceDir { get; set; }

        /// <summary>解析后 JSON 数据目录</summary>
        public string ParsedDataDir { get; set; }

        /// <summary>该站点的转辙机组列表</summary>
        public List<SwitchGroupDef> SwitchGroups { get; set; }

        /// <summary>站点目录中是否存在 DC.ini</summary>
        public bool HasDcIni { get; set; }

        /// <summary>CSV 文件数量</summary>
        public int CsvFileCount { get; set; }

        /// <summary>site.json 中的 switchGroups（仅当 DC.ini 不可用时的回退）</summary>
        public List<SwitchGroupDef> ManualSwitchGroups { get; set; }

        public StationManifest()
        {
            SwitchGroups = new List<SwitchGroupDef>();
            ManualSwitchGroups = new List<SwitchGroupDef>();
        }
    }
}
