using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Station;

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
                // N01-5: 向后兼容 — 缺失字段应用默认值
                ApplyBackwardCompatDefaults(_instance);
                // 加载 digit.ini 配置填充 SwitchGroup 点号
                LoadDigitPointConfig(_instance);

                // 自动发现新站点（已有站点不覆盖）
                try
                {
                    string rawDataDir = FindRawDataDir(AppDomain.CurrentDomain.BaseDirectory);
                    IntegrateDiscoveredSites(_instance, rawDataDir);
                }
                catch
                {
                    // 发现失败不影响启动
                }

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
        /// 从 digit.ini / switch_digit_config.json 加载 DB/FB/1DQJ 点号，
        /// 填充到各 SwitchGroup 的 DbPointId/FbPointId/DqjPointId 字段。
        /// 加载失败时静默回退，各字段保持 null。
        /// </summary>
        private static void LoadDigitPointConfig(AppConfig config)
        {
            DigitSwitchRegistry registry = null;

            // 1. 优先：从 digit.ini 直接解析（如果路径已配置且文件存在）
            if (!string.IsNullOrEmpty(config.DigitIniPath) && File.Exists(config.DigitIniPath))
            {
                try
                {
                    registry = DigitSwitchRegistry.LoadFromIni(config.DigitIniPath);
                }
                catch { }
            }

            // 2. 回退：从 switch_digit_config.json 读取
            if (registry == null)
            {
                try
                {
                    // 2a. 若 DigitIniPath 已配置，优先在同目录查找
                    if (!string.IsNullOrEmpty(config.DigitIniPath))
                    {
                        string dir = Path.GetDirectoryName(config.DigitIniPath);
                        string jsonPath = Path.Combine(dir ?? ".", "switch_digit_config.json");
                        if (File.Exists(jsonPath))
                            registry = DigitSwitchRegistry.Load(jsonPath);
                    }

                    // 2b. 在应用程序目录查找（部署时放在 exe 同目录即可）
                    if (registry == null)
                    {
                        string appDir = AppDomain.CurrentDomain.BaseDirectory;
                        string jsonPath = Path.Combine(appDir, "switch_digit_config.json");
                        if (File.Exists(jsonPath))
                            registry = DigitSwitchRegistry.Load(jsonPath);
                    }
                }
                catch { }
            }

            if (registry == null)
                return;

            // 填充到各 SwitchGroup
            foreach (var group in config.SwitchGroups)
            {
                DigitPointIds ptIds;
                if (registry.TryGetConfig(group.Id, out ptIds))
                {
                    group.DbPointId = ptIds.db_point_id > 0 ? ptIds.db_point_id : (int?)null;
                    group.FbPointId = ptIds.fb_point_id > 0 ? ptIds.fb_point_id : (int?)null;
                    group.DqjPointId = ptIds.dqj_point_id > 0 ? ptIds.dqj_point_id : (int?)null;
                }
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
            var config = new AppConfig
            {
                SwitchGroups = new List<SwitchGroup>(),
                DataSourceDir = @".\03_raw_data",
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
                },
                Sites = new List<SiteConfig>(),
                SelectedSiteId = ""
            };

            // 自动发现站点（从 BaseDirectory 向上搜索 03_raw_data/）
            try
            {
                string rawDataDir = FindRawDataDir(AppDomain.CurrentDomain.BaseDirectory);
                IntegrateDiscoveredSites(config, rawDataDir);
            }
            catch
            {
                // 发现失败不影响启动，使用空站点列表
            }

            return config;
        }

        /// <summary>
        /// N01-5 向后兼容：缺失的新字段应用默认值。
        /// JavaScriptSerializer 对缺失字段保持属性默认值，
        /// 但引用类型（List/string）可能被反序列化为 null。
        /// </summary>
        private static void ApplyBackwardCompatDefaults(AppConfig config)
        {
            if (string.IsNullOrEmpty(config.StationId))
                config.StationId = "";
            if (string.IsNullOrEmpty(config.StationName))
                config.StationName = "";
            if (string.IsNullOrEmpty(config.Role))
                config.Role = "station";
            if (string.IsNullOrEmpty(config.VendorType))
                config.VendorType = "huihuang";
            if (config.Subscribers == null)
                config.Subscribers = new List<string>();
            if (config.TeamStations == null)
                config.TeamStations = new List<SiteConfig>();
            if (config.Stations == null)
                config.Stations = new List<SiteConfig>();
            if (config.ListenPort == 0)
                config.ListenPort = 9000;
            if (config.MergeWindowMs == 0)
                config.MergeWindowMs = 1000;
            // DataRetentionDays: 0 = 不限（有意为之，无需覆盖）
        }

        /// <summary>
        /// 从指定目录向上搜索，定位 03_raw_data/ 目录。
        /// 解决从 06_deploy/release/ 运行时找不到上层 03_raw_data/ 的问题。
        /// </summary>
        private static string FindRawDataDir(string startDir)
        {
            string current = startDir;
            for (int i = 0; i < 5; i++)
            {
                string candidate = Path.Combine(current, "03_raw_data");
                if (Directory.Exists(candidate))
                    return candidate; // 返回绝对路径，避免相对路径在不同工作目录下失效

                try
                {
                    var parent = Directory.GetParent(current);
                    if (parent == null) break;
                    current = parent.FullName;
                }
                catch
                {
                    break;
                }
            }

            // 回退：若实在找不到，用相对路径（StationManager 会基于 BaseDirectory 解析）
            return @".\03_raw_data";
        }

        /// <summary>
        /// 扫描 03_raw_data/ 自动发现站点，集成到 AppConfig。
        /// 已有配置的站点（同 Id）保持不变，新站点自动追加。
        /// </summary>
        public static void IntegrateDiscoveredSites(AppConfig config, string rawDataDir)
        {
            var manifest = StationManager.DiscoverStations(rawDataDir);
            if (manifest.Count == 0)
                return;

            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.Sites != null)
            {
                foreach (var s in config.Sites)
                    existingIds.Add(s.Id ?? "");
            }

            foreach (var m in manifest)
            {
                // 转换发现的 SwitchGroupDef → SwitchGroup
                var groups = new List<SwitchGroup>();
                if (m.SwitchGroups != null)
                {
                    foreach (var d in m.SwitchGroups)
                    {
                        groups.Add(new SwitchGroup
                        {
                            Id = d.Id,
                            Label = d.Label,
                            DataFileIndex = d.DataFileIndex,
                            SwitchType = d.SwitchType
                        });
                    }
                }

                if (existingIds.Contains(m.Id))
                {
                    // 已存在：SwitchGroups 始终用 DC.ini / site.json 刷新
                    // （DC.ini 和 site.json 是权威来源，config.json 中的 SwitchGroups 仅作缓存）
                    if (groups.Count > 0)
                    {
                        var existingSite = config.Sites.Find(s =>
                            string.Equals(s.Id, m.Id, StringComparison.OrdinalIgnoreCase));
                        if (existingSite != null)
                        {
                            existingSite.SwitchGroups = groups;
                        }
                    }
                    continue;
                }

                config.Sites.Add(new SiteConfig
                {
                    Id = m.Id,
                    Name = m.Name,
                    DataSourceDir = StationManager.ToRelativePath(m.DataSourceDir),
                    ParsedDataDir = m.ParsedDataDir ?? (".\\parsed_data\\" + m.Id),
                    SwitchGroups = groups.Count > 0 ? groups : null
                });

                existingIds.Add(m.Id);
            }

            // 无选中站点时默认选第一个
            if (string.IsNullOrEmpty(config.SelectedSiteId) && config.Sites.Count > 0)
                config.SelectedSiteId = config.Sites[0].Id;
        }
    }
}
