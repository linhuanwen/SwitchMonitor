using System;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 诊断结论（诊断引擎的输出）
    /// </summary>
    public class DiagnosisResult
    {
        /// <summary>诊断规则名称</summary>
        public string RuleName { get; set; }

        /// <summary>诊断级别: "正常" / "预警" / "报警" / "故障"</summary>
        public string Level { get; set; }

        /// <summary>可读的诊断描述</summary>
        public string Description { get; set; }

        /// <summary>异常值（可选）</summary>
        public float? AbnormalValue { get; set; }

        /// <summary>参考值/阈值（可选）</summary>
        public float? ReferenceValue { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}] {1}: {2} (异常值={3}, 参考值={4})",
                Level, RuleName, Description, AbnormalValue, ReferenceValue);
        }
    }

    /// <summary>
    /// 诊断级别常量
    /// </summary>
    public static class DiagnosisLevel
    {
        public const string Normal = "正常";
        public const string Warning = "预警";
        public const string Alarm = "报警";
        public const string Fault = "故障";
    }
}
