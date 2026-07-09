using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// JSON 配置文件管理器
    /// </summary>
    public class ConfigManager
    {
        private static AppConfig _instance;

        /// <summary>当前配置实例（单例）</summary>
        public static AppConfig Instance
        {
            get
            {
                if (_instance == null)
                    throw new InvalidOperationException("ConfigManager 尚未初始化，请先调用 LoadConfig()");
                return _instance;
            }
        }

        /// <summary>
        /// 从指定路径加载 JSON 配置文件
        /// </summary>
        public static AppConfig LoadConfig(string configPath)
        {
            return LoadConfigWithStatus(configPath).Item1;
        }

        /// <summary>
        /// 加载配置并返回是否使用了回退默认配置
        /// </summary>
        public static Tuple<AppConfig, bool> LoadConfigWithStatus(string configPath)
        {
            if (!File.Exists(configPath))
            {
                _instance = CreateDefaultConfig();
                SaveConfig(configPath);
                return Tuple.Create(_instance, true);
            }

            try
            {
                string json = File.ReadAllText(configPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                _instance = serializer.Deserialize<AppConfig>(json);
                return Tuple.Create(_instance, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("配置文件解析失败: " + ex.Message);
                _instance = CreateDefaultConfig();
                return Tuple.Create(_instance, true);
            }
        }

        /// <summary>
        /// 保存配置到文件
        /// </summary>
        public static void SaveConfig(string configPath)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(_instance);
                // 手动格式化 JSON（JavaScriptSerializer 不内置格式化）
                File.WriteAllText(configPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new IOException("保存配置文件失败: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// 创建带默认值的配置
        /// </summary>
        private static AppConfig CreateDefaultConfig()
        {
            return new AppConfig
            {
                SwitchGroups = new System.Collections.Generic.List<SwitchGroup>
                {
                    new SwitchGroup { Id = "1-1", Label = "1-1", DataFileIndex = 0 },
                    new SwitchGroup { Id = "1-X", Label = "1-X", DataFileIndex = 4 },
                    new SwitchGroup { Id = "3-1", Label = "3-1", DataFileIndex = 8 },
                    new SwitchGroup { Id = "3-X", Label = "3-X", DataFileIndex = 12 },
                    new SwitchGroup { Id = "2-1", Label = "2-1", DataFileIndex = 16 },
                    new SwitchGroup { Id = "2-X", Label = "2-X", DataFileIndex = 20 },
                    new SwitchGroup { Id = "4-1", Label = "4-1", DataFileIndex = 24 },
                    new SwitchGroup { Id = "4-X", Label = "4-X", DataFileIndex = 28 }
                },
                DataSourceDir = @".\03_raw_data\sanshuibei",
                ParsedDataDir = @".\parsed_data",
                ScanInterval = 5,
                AlarmThresholds = new AlarmThresholdsConfig
                {
                    Current = new AlarmThreshold { Enabled = true, Value = 2.0, Unit = "A" },
                    Power = new AlarmThreshold { Enabled = true, Value = 1.5, Unit = "KW" }
                },
                ChartColors = new ChartColorsConfig
                {
                    CurrentA = "#55FF55",
                    CurrentB = "#FF5555",
                    CurrentC = "#CC44CC",
                    Power = "#55FF55",
                    ThresholdLine = "#FF4444",
                    Background = "#3c3c3c",
                    GridLine = "#6a6a6a",
                    TextColor = "#CCCCCC",
                    RefCurrentA = "#00FFFF",
                    RefCurrentB = "#FF5555",
                    RefCurrentC = "#FFFF00",
                    RefPower = "#FF5555",
                    LevelWarning = "#FFD54F",
                    LevelAlarm = "#FF9800",
                    LevelFault = "#FF4444"
                },
                Ui = new UiConfig
                {
                    SidebarWidthPercent = 18,
                    DateFormat = "yyyy/MM/dd",
                    XAxisDefaultMax = 14,
                    XAxisExtendedMax = 30
                },
                Diagnosis = new DiagnosisConfig
                {
                    Enabled = true,
                    RulesDir = "Rules"
                }
            };
        }
    }
}
