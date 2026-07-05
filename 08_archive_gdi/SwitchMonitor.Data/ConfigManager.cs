using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 应用程序配置管理器。
    /// 负责读写 config.json 文件，管理报警阈值等持久化配置。
    ///
    /// 使用方式:
    /// <code>
    ///   var manager = new ConfigManager("config.json");
    ///   var thresholds = manager.GetAlarmThresholds();
    ///   thresholds.Current.UpperLimit = 3.0f;
    ///   manager.SaveAlarmThresholds(thresholds);
    /// </code>
    /// </summary>
    public class ConfigManager
    {
        private readonly string _filePath;

        /// <summary>内存中缓存的报警阈值配置</summary>
        private AlarmThresholdConfig _cachedThresholds;

        /// <summary>获取配置文件路径</summary>
        public string FilePath { get { return _filePath; } }

        /// <summary>
        /// 构造 ConfigManager 并指定配置文件路径。
        /// </summary>
        /// <param name="filePath">config.json 文件路径</param>
        public ConfigManager(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException("filePath");
            _filePath = filePath;
        }

        /// <summary>
        /// 获取报警阈值配置。
        /// 首次调用时从文件加载并缓存，后续调用返回缓存值。
        /// </summary>
        public AlarmThresholdConfig GetAlarmThresholds()
        {
            if (_cachedThresholds != null)
                return _cachedThresholds;

            _cachedThresholds = LoadAlarmThresholdsFromFile();
            return _cachedThresholds;
        }

        /// <summary>
        /// 保存报警阈值配置到文件并更新内存缓存。
        /// </summary>
        /// <param name="config">要保存的配置</param>
        public void SaveAlarmThresholds(AlarmThresholdConfig config)
        {
            if (config == null)
                throw new ArgumentNullException("config");

            _cachedThresholds = config.Clone();
            WriteConfigFile(config);
        }

        /// <summary>
        /// 从文件加载完整的 config.json。
        /// </summary>
        private ConfigRoot LoadConfigFile()
        {
            if (!File.Exists(_filePath))
                return new ConfigRoot();

            try
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                var root = JsonConvert.DeserializeObject<ConfigRoot>(json);
                return root ?? new ConfigRoot();
            }
            catch (JsonException)
            {
                return new ConfigRoot();
            }
        }

        /// <summary>
        /// 从文件加载报警阈值配置。
        /// 文件不存在或解析失败时返回默认配置。
        /// </summary>
        private AlarmThresholdConfig LoadAlarmThresholdsFromFile()
        {
            var root = LoadConfigFile();
            return root.AlarmThresholds ?? AlarmThresholdConfig.CreateDefault();
        }

        /// <summary>
        /// 将报警阈值配置写入 config.json 文件。
        /// 保留文件中已有的其他节（非 alarmThresholds 部分）。
        /// </summary>
        private void WriteConfigFile(AlarmThresholdConfig thresholds)
        {
            // 读取现有文件（如果存在），保留其他节
            var root = File.Exists(_filePath)
                ? LoadConfigFile()
                : new ConfigRoot();

            root.AlarmThresholds = thresholds;

            string json = JsonConvert.SerializeObject(root, Formatting.Indented);
            string dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// config.json 的根对象。支持扩展更多配置节。
        /// </summary>
        private class ConfigRoot
        {
            [JsonProperty("alarmThresholds")]
            public AlarmThresholdConfig AlarmThresholds { get; set; }
        }
    }
}
