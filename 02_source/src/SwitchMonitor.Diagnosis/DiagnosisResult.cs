using System;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 诊断级别常量（递增）：正常 < 预警 < 报警 < 故障
    /// </summary>
    public static class DiagnosisLevel
    {
        public const string Normal = "正常";
        public const string Warning = "预警";
        public const string Alarm = "报警";
        public const string Fault = "故障";

        /// <summary>
        /// 返回级别对应的严重程度数值：正常=0, 预警=1, 报警=2, 故障=3
        /// </summary>
        public static int Severity(string level)
        {
            switch (level)
            {
                case Normal: return 0;
                case Warning: return 1;
                case Alarm: return 2;
                case Fault: return 3;
                default: return 0;
            }
        }
    }

    /// <summary>
    /// 诊断结论 POCO。
    /// </summary>
    public class DiagnosisResult
    {
        /// <summary>规则 ID，如 "R1"</summary>
        public string RuleId;

        /// <summary>规则名称，如 "动作超时/未完成"</summary>
        public string RuleName;

        /// <summary>诊断级别，DiagnosisLevel 常量之一</summary>
        public string Level;

        /// <summary>中文结论描述（含数值）</summary>
        public string Description;

        /// <summary>异常值</summary>
        public double Value;

        /// <summary>参考值</summary>
        public double Reference;
    }
}
