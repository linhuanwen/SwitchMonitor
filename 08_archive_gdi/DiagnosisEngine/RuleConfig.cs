using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DiagnosisEngine
{
    /// <summary>
    /// 规则配置文件根对象。
    /// JSON 格式: { "rules": [ ... ] }
    /// </summary>
    public class RuleConfigCollection
    {
        /// <summary>规则列表</summary>
        public List<RuleConfig> Rules { get; set; }

        /// <summary>
        /// 从 JSON 字符串加载规则配置。
        /// </summary>
        public static RuleConfigCollection LoadFromJson(string json)
        {
            return JsonConvert.DeserializeObject<RuleConfigCollection>(json);
        }

        /// <summary>
        /// 从指定目录加载所有 .json 规则配置文件，合并为一个配置集合。
        /// </summary>
        public static RuleConfigCollection LoadFromDirectory(string directoryPath)
        {
            var allRules = new List<RuleConfig>();

            if (!Directory.Exists(directoryPath))
                throw new DirectoryNotFoundException(string.Format("规则配置目录不存在: {0}", directoryPath));

            string[] jsonFiles = Directory.GetFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly);

            if (jsonFiles.Length == 0)
                throw new FileNotFoundException(string.Format("规则配置目录中没有 .json 文件: {0}", directoryPath));

            foreach (string filePath in jsonFiles)
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var collection = JsonConvert.DeserializeObject<RuleConfigCollection>(json);
                if (collection != null && collection.Rules != null)
                {
                    allRules.AddRange(collection.Rules);
                }
            }

            return new RuleConfigCollection { Rules = allRules };
        }
    }

    /// <summary>
    /// 单条诊断规则的配置定义。
    /// </summary>
    public class RuleConfig
    {
        /// <summary>规则内部名称（如 "conversion_time"）</summary>
        public string Name { get; set; }

        /// <summary>规则的显示名称（如 "转换时间异常"）</summary>
        public string DisplayName { get; set; }

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; }

        /// <summary>规则类型: "threshold" / "morphology" / "segment"</summary>
        public string Type { get; set; }

        /// <summary>规则参数，键值对形式。数字值以 JsonElement 存储。</summary>
        public Dictionary<string, object> Parameters { get; set; }

        public RuleConfig()
        {
            Parameters = new Dictionary<string, object>();
        }

        /// <summary>
        /// 从参数中获取 float 值。
        /// 处理 JToken（Newtonsoft.Json 默认反序列化）和直接数值类型。
        /// </summary>
        public float GetFloatParam(string key, float defaultValue = 0f)
        {
            if (Parameters == null || !Parameters.TryGetValue(key, out var value))
                return defaultValue;

            return ConvertToFloat(value, defaultValue);
        }

        /// <summary>
        /// 从参数中获取 int 值。
        /// </summary>
        public int GetIntParam(string key, int defaultValue = 0)
        {
            if (Parameters == null || !Parameters.TryGetValue(key, out var value))
                return defaultValue;

            return ConvertToInt(value, defaultValue);
        }

        #region Static Helpers

        /// <summary>
        /// 将参数值转换为 float（处理 JToken 和各种数值类型）。
        /// </summary>
        public static float ConvertToFloat(object value, float defaultValue = 0f)
        {
            if (value == null) return defaultValue;

            if (value is JToken jt)
            {
                if (jt.Type == JTokenType.Float || jt.Type == JTokenType.Integer)
                    return (float)jt;
                if (jt.Type == JTokenType.String && float.TryParse((string)jt, out var fp))
                    return fp;
                return defaultValue;
            }

            if (value is double d) return (float)d;
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is long l) return l;
            if (value is decimal m) return (float)m;

            return defaultValue;
        }

        /// <summary>
        /// 将参数值转换为 int（处理 JToken 和各种数值类型）。
        /// </summary>
        public static int ConvertToInt(object value, int defaultValue = 0)
        {
            if (value == null) return defaultValue;

            if (value is JToken jt)
            {
                if (jt.Type == JTokenType.Integer || jt.Type == JTokenType.Float)
                    return (int)jt;
                if (jt.Type == JTokenType.String && int.TryParse((string)jt, out var ip))
                    return ip;
                return defaultValue;
            }

            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (value is double d) return (int)d;
            if (value is float f) return (int)f;
            if (value is decimal m) return (int)m;

            return defaultValue;
        }

        #endregion
    }
}
