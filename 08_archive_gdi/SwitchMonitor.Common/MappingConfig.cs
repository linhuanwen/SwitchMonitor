using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SwitchMonitor.Common
{
    /// <summary>
    /// 文件到道岔的映射条目
    /// </summary>
    public class FileMappingEntry
    {
        /// <summary>道岔标识（如 "SW_01"）</summary>
        [JsonProperty("switchId")]
        public string SwitchId { get; set; }

        /// <summary>道岔可读名称（如 "1#道岔"）</summary>
        [JsonProperty("switchName")]
        public string SwitchName { get; set; }

        /// <summary>描述/备注</summary>
        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary>方向提示</summary>
        [JsonProperty("directionHint")]
        public string DirectionHint { get; set; }
    }

    /// <summary>
    /// 点号映射条目
    /// </summary>
    public class PointIdMappingEntry
    {
        /// <summary>配置名称（如 "537GH"）</summary>
        [JsonProperty("configName")]
        public string ConfigName { get; set; }

        /// <summary>关联道岔标识（可为 null）</summary>
        [JsonProperty("switchId")]
        public string SwitchId { get; set; }

        /// <summary>描述/备注</summary>
        [JsonProperty("description")]
        public string Description { get; set; }
    }

    /// <summary>
    /// 方向映射条目
    /// </summary>
    public class DirectionMappingEntry
    {
        /// <summary>方向含义</summary>
        [JsonProperty("meaning")]
        public string Meaning { get; set; }

        /// <summary>备注</summary>
        [JsonProperty("note")]
        public string Note { get; set; }
    }

    /// <summary>
    /// 道岔映射配置——管理数据源文件与实际道岔的映射关系。
    ///
    /// 使用方式:
    /// <code>
    ///   var config = MappingConfig.Load("Config/switch_mapping.json");
    ///   string name = config.GetSwitchName("SwitchCurve(0)");  // "1#道岔"
    ///   string point = config.GetPointConfigName(184);         // "537GH"
    /// </code>
    ///
    /// 热加载:
    /// <code>
    ///   config.Reload();  // 重新读取 JSON 文件
    /// </code>
    /// </summary>
    public class MappingConfig
    {
        /// <summary>配置版本号</summary>
        [JsonProperty("version")]
        public string Version { get; set; }

        /// <summary>车站标识</summary>
        [JsonProperty("stationId")]
        public string StationId { get; set; }

        /// <summary>车站名称</summary>
        [JsonProperty("stationName")]
        public string StationName { get; set; }

        /// <summary>文件到道岔的映射字典。Key = 文件名（如 "SwitchCurve(0)"），Value = 映射条目</summary>
        [JsonProperty("fileMapping")]
        public Dictionary<string, FileMappingEntry> FileMapping { get; set; }

        /// <summary>点号到配置名称的映射字典。Key = 点号字符串（如 "184"），Value = 映射条目</summary>
        [JsonProperty("pointIdMapping")]
        public Dictionary<string, PointIdMappingEntry> PointIdMapping { get; set; }

        /// <summary>方向代码映射字典。Key = 方向代码（如 "H"/"B"），Value = 方向含义</summary>
        [JsonProperty("directionMapping")]
        public Dictionary<string, DirectionMappingEntry> DirectionMapping { get; set; }

        /// <summary>配置文件路径（用于热加载）</summary>
        [JsonIgnore]
        private string _filePath;

        /// <summary>
        /// 创建带默认值的空配置
        /// </summary>
        public MappingConfig()
        {
            Version = "1.0";
            StationId = "DEFAULT";
            StationName = "未命名车站";
            FileMapping = new Dictionary<string, FileMappingEntry>(StringComparer.OrdinalIgnoreCase);
            PointIdMapping = new Dictionary<string, PointIdMappingEntry>();
            DirectionMapping = new Dictionary<string, DirectionMappingEntry>(StringComparer.OrdinalIgnoreCase);
        }

        // ================================================================
        // 工厂方法
        // ================================================================

        /// <summary>
        /// 从 JSON 字符串反序列化 MappingConfig。
        /// 如果反序列化失败，返回默认配置。
        /// </summary>
        /// <param name="json">JSON 字符串</param>
        /// <returns>反序列化后的配置（永远不会返回 null）</returns>
        public static MappingConfig LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return CreateDefault();

            try
            {
                var config = JsonConvert.DeserializeObject<MappingConfig>(json);
                if (config == null)
                    return CreateDefault();

                // 确保字典不为 null，且保留 OrdinalIgnoreCase 比较器。
                // Newtonsoft.Json 反序列化时会创建默认比较器的字典，
                // 必须重新包装以保持大小写不敏感的查找行为。
                config.FileMapping = config.FileMapping != null
                    ? new Dictionary<string, FileMappingEntry>(config.FileMapping, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, FileMappingEntry>(StringComparer.OrdinalIgnoreCase);
                config.PointIdMapping = config.PointIdMapping ?? new Dictionary<string, PointIdMappingEntry>();
                config.DirectionMapping = config.DirectionMapping != null
                    ? new Dictionary<string, DirectionMappingEntry>(config.DirectionMapping, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, DirectionMappingEntry>(StringComparer.OrdinalIgnoreCase);

                // 确保标量字段有默认值
                if (string.IsNullOrEmpty(config.Version)) config.Version = "1.0";
                if (string.IsNullOrEmpty(config.StationId)) config.StationId = "DEFAULT";
                if (string.IsNullOrEmpty(config.StationName)) config.StationName = "未命名车站";

                return config;
            }
            catch (JsonException)
            {
                return CreateDefault();
            }
        }

        /// <summary>
        /// 从 JSON 文件加载 MappingConfig。
        /// 如果文件不存在或解析失败，返回默认配置（不抛异常）。
        /// </summary>
        /// <param name="path">JSON 文件路径</param>
        /// <returns>配置对象（永远不会返回 null）</returns>
        public static MappingConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path))
                return CreateDefault();

            if (!File.Exists(path))
                return CreateDefault();

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var config = LoadFromJson(json);
                config._filePath = path;
                return config;
            }
            catch (Exception)
            {
                return CreateDefault();
            }
        }

        /// <summary>
        /// 创建带安全默认值的配置（全降级模式）
        /// </summary>
        public static MappingConfig CreateDefault()
        {
            return new MappingConfig
            {
                Version = "1.0",
                StationId = "DEFAULT",
                StationName = "未命名车站",
                FileMapping = new Dictionary<string, FileMappingEntry>(StringComparer.OrdinalIgnoreCase),
                PointIdMapping = new Dictionary<string, PointIdMappingEntry>(),
                DirectionMapping = new Dictionary<string, DirectionMappingEntry>(StringComparer.OrdinalIgnoreCase),
            };
        }

        // ================================================================
        // 查询方法
        // ================================================================

        /// <summary>
        /// 根据文件来源获取可读的道岔名称。
        /// 已映射 → 返回 switchName（如 "1#道岔"）
        /// 未映射 → 返回文件名本身（如 "SwitchCurve(0)"）
        /// null/空 → 返回 "(未知)"
        /// </summary>
        /// <param name="fileSource">来源文件名</param>
        /// <returns>可读的道岔名称</returns>
        public string GetSwitchName(string fileSource)
        {
            if (string.IsNullOrEmpty(fileSource))
                return "(未知)";

            // 1. 精确匹配
            if (FileMapping != null && FileMapping.TryGetValue(fileSource, out var entry))
            {
                if (!string.IsNullOrEmpty(entry.SwitchName))
                    return entry.SwitchName;
            }

            // 2. 裸数字后备匹配：DB 中可能存 "0" 而映射键为 "SwitchCurve(0)"
            if (FileMapping != null)
            {
                int dummy;
                if (int.TryParse(fileSource, out dummy))
                {
                    string altKey = "SwitchCurve(" + fileSource + ")";
                    if (FileMapping.TryGetValue(altKey, out var altEntry))
                    {
                        if (!string.IsNullOrEmpty(altEntry.SwitchName))
                            return altEntry.SwitchName;
                    }
                }
            }

            // 降级：返回文件名本身
            return fileSource;
        }

        /// <summary>
        /// 根据文件来源获取对应的 SwitchId。
        /// 已映射 → 返回 switchId（如 "SW_01"）
        /// 未映射 → 返回文件名本身
        /// </summary>
        /// <param name="fileSource">来源文件名</param>
        /// <returns>道岔标识</returns>
        public string GetSwitchId(string fileSource)
        {
            if (string.IsNullOrEmpty(fileSource))
                return "(未知)";

            // 1. 精确匹配
            if (FileMapping != null && FileMapping.TryGetValue(fileSource, out var entry))
            {
                if (!string.IsNullOrEmpty(entry.SwitchId))
                    return entry.SwitchId;
            }

            // 2. 裸数字后备匹配
            if (FileMapping != null)
            {
                int dummy;
                if (int.TryParse(fileSource, out dummy))
                {
                    string altKey = "SwitchCurve(" + fileSource + ")";
                    if (FileMapping.TryGetValue(altKey, out var altEntry))
                    {
                        if (!string.IsNullOrEmpty(altEntry.SwitchId))
                            return altEntry.SwitchId;
                    }
                }
            }

            return fileSource;
        }

        /// <summary>
        /// 根据点号获取配置名称。
        /// 已映射 → 返回 configName（如 "537GH"）
        /// 未映射 → 返回 "点号{id}(未映射)"
        /// </summary>
        /// <param name="pointId">采集点号</param>
        /// <returns>可读的点号标签</returns>
        public string GetPointConfigName(int pointId)
        {
            string key = pointId.ToString();

            if (PointIdMapping != null && PointIdMapping.TryGetValue(key, out var entry))
            {
                if (!string.IsNullOrEmpty(entry.ConfigName))
                    return entry.ConfigName;
            }

            // 降级：显示点号
            return string.Format("点号{0}(未映射)", pointId);
        }

        /// <summary>
        /// 获取点号的完整显示标签（包含 configName 如果有的话）。
        /// 格式: "点号184 537GH" 或 "点号999(未映射)"
        /// </summary>
        /// <param name="pointId">采集点号</param>
        /// <returns>完整的点号显示标签</returns>
        public string GetPointDisplayLabel(int pointId)
        {
            string key = pointId.ToString();

            if (PointIdMapping != null && PointIdMapping.TryGetValue(key, out var entry))
            {
                if (!string.IsNullOrEmpty(entry.ConfigName))
                    return string.Format("点号{0} {1}", pointId, entry.ConfigName);
            }

            return string.Format("点号{0}(未映射)", pointId);
        }

        /// <summary>
        /// 获取方向代码的含义。
        /// 已映射 → 返回 meaning
        /// 未映射 → 返回代码本身
        /// </summary>
        /// <param name="directionCode">方向代码（如 "H"/"B"）</param>
        /// <returns>方向含义</returns>
        public string GetDirectionMeaning(string directionCode)
        {
            if (string.IsNullOrEmpty(directionCode))
                return "(未知)";

            if (DirectionMapping != null && DirectionMapping.TryGetValue(directionCode, out var entry))
            {
                if (!string.IsNullOrEmpty(entry.Meaning))
                    return entry.Meaning;
            }

            return directionCode;
        }

        // ================================================================
        // 热加载
        // ================================================================

        /// <summary>
        /// 重新加载配置文件。如果从未从文件加载过，则无操作。
        /// 加载失败时保留当前配置不变。
        /// </summary>
        public void Reload()
        {
            if (string.IsNullOrEmpty(_filePath))
                return;

            if (!File.Exists(_filePath))
                return;

            try
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                var newConfig = LoadFromJson(json);

                // 逐字段更新（保留 _filePath）
                Version = newConfig.Version;
                StationId = newConfig.StationId;
                StationName = newConfig.StationName;
                FileMapping = newConfig.FileMapping;
                PointIdMapping = newConfig.PointIdMapping;
                DirectionMapping = newConfig.DirectionMapping;
            }
            catch (Exception)
            {
                // 加载失败，保留当前配置不变
            }
        }
    }
}
