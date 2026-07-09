using System;
using System.Collections.Generic;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// T1 渐变劣化趋势分析器。
    /// 对 convMean / durationSec 两个指标，按天聚合中位数后做 7 天滑动中位数序列，
    /// 检测持续单调不减趋势。
    /// </summary>
    public static class TrendAnalyzer
    {
        /// <summary>
        /// 对 features.json 的指定指标执行 T1 趋势分析。
        /// </summary>
        /// <param name="store">特征存储</param>
        /// <param name="baselineValue">基线值（如 refConvMean）</param>
        /// <param name="trendRatio">趋势比例阈值，默认 0.15</param>
        /// <param name="trendDays">趋势检测天数，默认 7</param>
        /// <param name="metricCol">指标列名，默认 "convMean"</param>
        /// <returns>触发时返回 DiagnosisResult，否则 null</returns>
        public static DiagnosisResult AnalyzeT1(FeaturesStore store,
            double baselineValue, double trendRatio = 0.15, int trendDays = 7,
            string metricCol = "convMean")
        {
            if (store == null || store.Rows == null || store.Rows.Count == 0)
                return null;

            int tsIdx = store.ColumnIndex("timestamp");
            int valIdx = store.ColumnIndex(metricCol);
            if (tsIdx < 0 || valIdx < 0)
                return null;

            // 1. 按日期聚合为每日中位数列表
            var dailyMedians = DailyMedians(store, tsIdx, valIdx);

            // 2. 需要足够的天数
            if (dailyMedians.Count < trendDays)
                return null;

            // 3. 计算 7 天滑动中位数序列（最后 N 天）
            int n = dailyMedians.Count;
            var slidingMedians = new List<double>();
            for (int i = 0; i < n; i++)
            {
                int windowStart = Math.Max(0, i - trendDays + 1);
                double median = MedianOfRange(dailyMedians, windowStart, i + 1);
                slidingMedians.Add(median);
            }

            // 4. 取最近值
            double recentValue = dailyMedians[n - 1];
            double baseline = MedianOfRange(dailyMedians, 0, n); // 全期中位数

            // 5. 检查趋势条件：
            //    a) 最近值 > 基线值 × (1 + trendRatio)
            //    b) 最近 trendDays 天的日值单调不减
            if (recentValue > baselineValue * (1.0 + trendRatio) &&
                IsMonotonicNonDecreasing(dailyMedians, n - trendDays, n))
            {
                double pct = (recentValue - baselineValue) / baselineValue * 100.0;
                string metricName = metricCol == "convMean" ? "转换段功率(convMean)" : "动作时长(durationSec)";
                string suggestion = metricCol == "convMean"
                    ? "建议检查滑床板润滑"
                    : "建议检查道岔机械阻力";

                return new DiagnosisResult
                {
                    RuleId = "T1",
                    RuleName = "渐变劣化预警",
                    Level = "预警",
                    Description = string.Format(
                        "{0}呈持续上升趋势（最近{1}天），从{2:F3}升至{3:F3}（+{4:F1}%），{5}",
                        metricName, trendDays, baselineValue, recentValue, pct, suggestion),
                    Value = Math.Round(recentValue, 3),
                    Reference = Math.Round(baselineValue, 3)
                };
            }

            return null;
        }

        /// <summary>
        /// 按天聚合为中位数列表（升序）
        /// </summary>
        private static List<double> DailyMedians(FeaturesStore store, int tsIdx, int valIdx)
        {
            // 使用 SortedDictionary 按日期分组
            var dayGroups = new SortedDictionary<string, List<double>>();

            foreach (var row in store.Rows)
            {
                if (row == null || row.Count <= Math.Max(tsIdx, valIdx)) continue;

                long ts = (long)row[tsIdx];
                double val = row[valIdx];

                // Unix timestamp → 日期字符串
                DateTime dt = UnixTimestampToDateTime(ts);
                string dateKey = dt.ToString("yyyy-MM-dd");

                if (!dayGroups.ContainsKey(dateKey))
                    dayGroups[dateKey] = new List<double>();
                dayGroups[dateKey].Add(val);
            }

            // 每日中位数
            var result = new List<double>();
            foreach (var kvp in dayGroups)
            {
                result.Add(Median(kvp.Value));
            }

            return result;
        }

        /// <summary>
        /// 检查 values 在 [start, end) 区间是否单调不减
        /// </summary>
        private static bool IsMonotonicNonDecreasing(List<double> values, int start, int end)
        {
            if (start < 0) start = 0;
            if (end > values.Count) end = values.Count;
            if (end - start < 2) return false;

            for (int i = start + 1; i < end; i++)
            {
                if (values[i] < values[i - 1])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 计算 [start, end) 范围的中位数
        /// </summary>
        private static double MedianOfRange(List<double> values, int start, int end)
        {
            if (start < 0) start = 0;
            if (end > values.Count) end = values.Count;
            if (end <= start) return 0.0;

            int len = end - start;
            var segment = new List<double>(len);
            for (int i = start; i < end; i++)
                segment.Add(values[i]);

            return Median(segment);
        }

        /// <summary>
        /// 中位数计算（偶数取两中均值）
        /// </summary>
        private static double Median(List<double> values)
        {
            int n = values.Count;
            if (n == 0) return 0.0;

            var sorted = new List<double>(values);
            sorted.Sort();

            if (n % 2 == 1)
                return sorted[n / 2];
            else
                return (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }

        /// <summary>
        /// Unix 时间戳 → DateTime（UTC+8 北京时间）
        /// </summary>
        private static DateTime UnixTimestampToDateTime(long unixTimestamp)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(unixTimestamp).AddHours(8); // UTC → 北京时间
        }
    }
}
