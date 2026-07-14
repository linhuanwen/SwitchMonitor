using System;
using System.Collections.Generic;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 一次道岔动作的完整曲线数据
    /// </summary>
    public class SwitchEvent
    {
        /// <summary>Unix 时间戳（秒）</summary>
        public long Timestamp { get; set; }

        /// <summary>可读时间字符串 "yyyy-MM-dd HH:mm:ss"</summary>
        public string DateTimeStr { get; set; }

        /// <summary>动作方向描述（如"定位↔反位"）</summary>
        public string Direction { get; set; }

        /// <summary>动作持续时间（秒）</summary>
        public double Duration { get; set; }

        /// <summary>采样间隔（秒），通常 0.04s</summary>
        public double SampleInterval { get; set; }

        /// <summary>采样点数</summary>
        public int SampleCount { get; set; }

        /// <summary>A 相电流采样值列表 [[t, v], ...]</summary>
        public List<double[]> CurrentA { get; set; }

        /// <summary>B 相电流采样值列表 [[t, v], ...]</summary>
        public List<double[]> CurrentB { get; set; }

        /// <summary>C 相电流采样值列表 [[t, v], ...]</summary>
        public List<double[]> CurrentC { get; set; }

        /// <summary>功率采样值列表 [[t, v], ...]</summary>
        public List<double[]> Power { get; set; }

        public SwitchEvent()
        {
            CurrentA = new List<double[]>();
            CurrentB = new List<double[]>();
            CurrentC = new List<double[]>();
            Power = new List<double[]>();
            Direction = "未知";
        }
    }

    /// <summary>
    /// 某转辙机某天的所有动作时间索引
    /// </summary>
    public class DayIndex
    {
        /// <summary>转辙机标识（如 "1-J"）</summary>
        public string SwitchId { get; set; }

        /// <summary>日期 "yyyy-MM-dd"</summary>
        public string Date { get; set; }

        /// <summary>该天所有动作的 Unix 时间戳，降序排列</summary>
        public List<long> Timestamps { get; set; }

        public DayIndex()
        {
            Timestamps = new List<long>();
        }
    }

    /// <summary>
    /// 转辙机组配置项
    /// </summary>
    public class SwitchGroup
    {
        /// <summary>转辙机标识（如 "1-J"）</summary>
        public string Id { get; set; }

        /// <summary>显示标签</summary>
        public string Label { get; set; }

        /// <summary>对应的 dataFileIndex，映射到 SwitchCurve 文件编号</summary>
        public int DataFileIndex { get; set; }

        // ── digit 点号配置（从 digit.ini 解析，null=未配置） ──

        /// <summary>DB 定位表示继电器点号（可为 null）</summary>
        public int? DbPointId { get; set; }

        /// <summary>FB 反位表示继电器点号（可为 null）</summary>
        public int? FbPointId { get; set; }

        /// <summary>1DQJ 启动继电器点号（可为 null）</summary>
        public int? DqjPointId { get; set; }
    }

    /// <summary>
    /// 报警阈值配置
    /// </summary>
    public class AlarmThreshold
    {
        /// <summary>是否启用</summary>
        public bool Enabled { get; set; }

        /// <summary>阈值</summary>
        public double Value { get; set; }

        /// <summary>单位</summary>
        public string Unit { get; set; }
    }

    /// <summary>
    /// 图表配色配置
    /// </summary>
    public class ChartColorsConfig
    {
        public string CurrentA { get; set; }
        public string CurrentB { get; set; }
        public string CurrentC { get; set; }
        public string Power { get; set; }
        public string ThresholdLine { get; set; }
        public string Background { get; set; }
        public string GridLine { get; set; }
        public string TextColor { get; set; }
        public string RefCurrentA { get; set; }
        public string RefCurrentB { get; set; }
        public string RefCurrentC { get; set; }
        public string RefPower { get; set; }

        // D5: 诊断级别颜色
        public string LevelWarning { get; set; }
        public string LevelAlarm { get; set; }
        public string LevelFault { get; set; }
    }

    /// <summary>
    /// UI 参数配置
    /// </summary>
    public class UiConfig
    {
        public int SidebarWidthPercent { get; set; }
        public string DateFormat { get; set; }
        public int XAxisDefaultMax { get; set; }
        public int XAxisExtendedMax { get; set; }
    }

    /// <summary>
    /// 阈值配置（电流 + 功率）
    /// </summary>
    public class AlarmThresholdsConfig
    {
        public AlarmThreshold Current { get; set; }
        public AlarmThreshold Power { get; set; }
    }

    /// <summary>
    /// 诊断配置节
    /// </summary>
    public class DiagnosisConfig
    {
        /// <summary>是否启用自动诊断</summary>
        public bool Enabled { get; set; }

        /// <summary>Rules 目录路径（相对或绝对）</summary>
        public string RulesDir { get; set; }

        public DiagnosisConfig()
        {
            Enabled = true;
            RulesDir = "Rules";
        }
    }

    /// <summary>
    /// 单条诊断结果条目（存储/序列化用 POCO，Data 项目定义，Diagnosis 项目填充）。
    /// 与 Diagnosis.DiagnosisResult 平行，避免 Data → Diagnosis 反向依赖。
    /// </summary>
    public class DiagnosisItem
    {
        /// <summary>规则 ID，如 "R1"</summary>
        public string RuleId { get; set; }

        /// <summary>规则名称，如 "动作超时/未完成"</summary>
        public string RuleName { get; set; }

        /// <summary>诊断级别："正常"/"预警"/"报警"/"故障"</summary>
        public string Level { get; set; }

        /// <summary>中文结论描述（含数值）</summary>
        public string Description { get; set; }

        /// <summary>异常值</summary>
        public double Value { get; set; }

        /// <summary>参考值</summary>
        public double Reference { get; set; }
    }

    /// <summary>
    /// 一次事件诊断的完整结果（存储 POCO，Data 项目定义）。
    /// 直接序列化为 .diag.json 格式，供 UI 读取。
    /// </summary>
    public class EventDiagnosis
    {
        /// <summary>Unix 时间戳（秒）</summary>
        public long Timestamp { get; set; }

        /// <summary>综合诊断级别</summary>
        public string Level { get; set; }

        /// <summary>命中的规则列表（正常事件为空）</summary>
        public List<DiagnosisItem> Results { get; set; }

        public EventDiagnosis()
        {
            Results = new List<DiagnosisItem>();
        }
    }

    /// <summary>
    /// 系统主配置
    /// </summary>
    public class AppConfig
    {
        public List<SwitchGroup> SwitchGroups { get; set; }
        public string DataSourceDir { get; set; }
        public string ParsedDataDir { get; set; }
        public int ScanInterval { get; set; }
        public AlarmThresholdsConfig AlarmThresholds { get; set; }
        public ChartColorsConfig ChartColors { get; set; }
        public UiConfig Ui { get; set; }
        public DiagnosisConfig Diagnosis { get; set; }

        /// <summary>digit.ini 配置文件路径（可为空，为空时不解析 digit 配置）</summary>
        public string DigitIniPath { get; set; }

        /// <summary>Digit(*).dat 开关量数据目录（可为空，为空时不读取开关量）</summary>
        public string DigitDataDir { get; set; }

        public AppConfig()
        {
            SwitchGroups = new List<SwitchGroup>();
            ScanInterval = 5;
            Diagnosis = new DiagnosisConfig();
            DigitIniPath = "";
            DigitDataDir = "";
        }
    }
}
