using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// 单条诊断规则的阈值配置（可反序列化 thresholds.json 的 rules 字典条目）。
    /// R0 不可配置，恒启用、恒"报警"。
    /// 为兼容 JavaScriptSerializer 的扁平反序列化，T1/P1 专用字段也定义在此。
    /// </summary>
    public class RuleThreshold
    {
        public bool enabled = true;
        public string level = "预警";

        // R1: 超时判据 — dur > refDur + durOverRefSeconds
        public double durOverRefSeconds = 3.0;

        // R2: 夭折判据 — dur < refDur × durUnderRefRatio
        public double durUnderRefRatio = 0.6;

        // R3: 时长偏差 — |dur - refDur| > maxDeviationSeconds
        public double maxDeviationSeconds = 0.5;

        // R4/R5/R7: 通用偏高 — value > ref × overRefRatio
        public double overRefRatio = 1.3;

        // R6: 台阶比 — stepRatio > maxStepRatio || stepRatio < minStepRatio
        public double maxStepRatio = 1.5;
        public double minStepRatio = 0.67;

        // R8: 缓放段 — |tailMean - refTailMean| / refTailMean > deviationRatio 或 tailMean==0
        public double deviationRatio = 0.3;

        // ── T1 渐变劣化专用 ──
        /// <summary>T1: 趋势比例阈值，最近值 > 基线值 × (1 + trendRatio) — 默认 0.15</summary>
        public double trendRatio = 0.15;

        /// <summary>T1: 趋势检测天数 — 默认 7</summary>
        public int trendDays = 7;

        // ── P1 逐点对比专用 ──
        /// <summary>P1: 面积偏差比阈值 — 默认 0.25</summary>
        public double areaDiffRatioThreshold = 0.25;

        /// <summary>P1: 最大绝对偏差 / refConvMean 阈值 — 默认 1.0</summary>
        public double maxAbsDevRatio = 1.0;
    }

    /// <summary>
    /// thresholds.json 存储容器。
    /// 从 JSON 文件反序列化，缺失/损坏时使用内置默认阈值。
    /// </summary>
    public class ThresholdStore
    {
        public int version = 1;
        public Dictionary<string, RuleThreshold> rules;

        public ThresholdStore()
        {
            rules = new Dictionary<string, RuleThreshold>();
        }

        /// <summary>
        /// 内置默认阈值（与 thresholds.json 模板同值），代码常量保证删掉 JSON 仍可工作。
        /// </summary>
        public static ThresholdStore CreateDefaults()
        {
            var store = new ThresholdStore();
            store.version = 1;
            store.rules = new Dictionary<string, RuleThreshold>
            {
                { "R1", new RuleThreshold { enabled = true, level = "故障", durOverRefSeconds = 3.0 } },
                { "R2", new RuleThreshold { enabled = true, level = "报警", durUnderRefRatio = 0.6 } },
                { "R3", new RuleThreshold { enabled = true, level = "预警", maxDeviationSeconds = 0.5 } },
                { "R4", new RuleThreshold { enabled = true, level = "预警", overRefRatio = 1.3 } },
                { "R5", new RuleThreshold { enabled = true, level = "预警", overRefRatio = 1.3 } },
                { "R6", new RuleThreshold { enabled = true, level = "报警", maxStepRatio = 1.5, minStepRatio = 0.67 } },
                { "R7", new RuleThreshold { enabled = true, level = "预警", overRefRatio = 1.3 } },
                { "R8", new RuleThreshold { enabled = true, level = "预警", deviationRatio = 0.3 } },
                { "R9", new RuleThreshold { enabled = true, level = "预警", deviationRatio = 0.3 } },
                { "T1", new RuleThreshold { enabled = true, level = "预警", trendRatio = 0.15, trendDays = 7 } },
                { "P1", new RuleThreshold { enabled = true, level = "预警", areaDiffRatioThreshold = 0.25, maxAbsDevRatio = 1.0 } }
            };
            return store;
        }

        /// <summary>
        /// 从 JSON 文件加载。文件缺失/损坏 → 返回内置默认值 + Logger.Warning。
        /// </summary>
        public static ThresholdStore Load(string filePath)
        {
            if (!File.Exists(filePath))
            {
                SwitchMonitor.Data.Logger.Warning(
                    "thresholds.json 缺失: " + filePath + "，使用内置默认阈值");
                return CreateDefaults();
            }

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var store = serializer.Deserialize<ThresholdStore>(json);
                if (store == null || store.rules == null || store.rules.Count == 0)
                {
                    SwitchMonitor.Data.Logger.Warning(
                        "thresholds.json 内容为空或格式错误: " + filePath + "，使用内置默认阈值");
                    return CreateDefaults();
                }
                return store;
            }
            catch (Exception ex)
            {
                SwitchMonitor.Data.Logger.Warning(
                    "thresholds.json 解析失败: " + filePath + " (" + ex.Message + ")，使用内置默认阈值");
                return CreateDefaults();
            }
        }

        /// <summary>
        /// 获取规则阈值，缺失时返回 null
        /// </summary>
        public RuleThreshold Get(string ruleId)
        {
            RuleThreshold t;
            if (rules.TryGetValue(ruleId, out t))
                return t;
            return null;
        }
    }
}
