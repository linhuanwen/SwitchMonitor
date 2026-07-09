using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 诊断结果聚合器。
    /// 取结果列表中最高级别；空列表 → "正常"。
    /// </summary>
    public static class DiagnosisAggregator
    {
        /// <summary>
        /// 取诊断结果列表中的最高严重级别。
        /// </summary>
        /// <param name="results">诊断结果列表（可能为空或 null）</param>
        /// <returns>级别字符串：正常/预警/报警/故障</returns>
        public static string OverallLevel(List<DiagnosisResult> results)
        {
            if (results == null || results.Count == 0)
                return DiagnosisLevel.Normal;

            int maxSeverity = 0;
            foreach (var r in results)
            {
                int s = DiagnosisLevel.Severity(r.Level);
                if (s > maxSeverity)
                    maxSeverity = s;
            }

            switch (maxSeverity)
            {
                case 3: return DiagnosisLevel.Fault;
                case 2: return DiagnosisLevel.Alarm;
                case 1: return DiagnosisLevel.Warning;
                default: return DiagnosisLevel.Normal;
            }
        }
    }
}
