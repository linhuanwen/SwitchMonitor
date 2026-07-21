using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;
using SwitchMonitor.Station;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// N01-5 配置模型升级测试套件
    /// 验证: AppConfig 新字段反序列化 / 向后兼容 / StationManifest 新字段
    /// </summary>
    public static class N01_5Tests
    {
        public static void Run()
        {
            // ═══ Slice 1: AppConfig 新字段反序列化 ═══
            TestRunner.Test("AppConfig 新字段全部正确解析", Test_AppConfig_NewFields_Deserialize);
            TestRunner.Test("AppConfig 默认值正确", Test_AppConfig_Defaults);

            // ═══ Slice 2: 向后兼容 — 旧版 config.json ═══
            TestRunner.Test("旧版 config.json 加载不报错", Test_BackwardCompat_OldConfig_Loads);
            TestRunner.Test("旧版 config.json 新字段退化到默认值", Test_BackwardCompat_DefaultsApplied);

            // ═══ Slice 3: role 特定行为 ═══
            TestRunner.Test("role=central 时 stations 正确解析", Test_CentralRole_StationsParsed);
            TestRunner.Test("role=station 时 teamStations 正确解析", Test_StationRole_TeamStationsParsed);

            // ═══ Slice 4: SiteConfig 新字段 ═══
            TestRunner.Test("SiteConfig Ip/Port 正确解析", Test_SiteConfig_IpPort);
            TestRunner.Test("SiteConfig Ip 缺失时不参与网络", Test_SiteConfig_NoIp);

            // ═══ Slice 5: SwitchGroup.SwitchType ═══
            TestRunner.Test("SwitchGroup switchType 正确解析", Test_SwitchGroup_SwitchType);
            TestRunner.Test("SwitchGroup 无 switchType 时为 null", Test_SwitchGroup_NoSwitchType);

            // ═══ Slice 6: StationManifest / SwitchGroupDef 新字段 ═══
            TestRunner.Test("StationManifest vendorType 属性存在", Test_StationManifest_VendorType);
            TestRunner.Test("SwitchGroupDef switchType 属性存在", Test_SwitchGroupDef_SwitchType);

            // ═══ Slice 7: ConfigManager 兼容处理 ═══
            TestRunner.Test("role 缺失默认 station", Test_ConfigManager_RoleDefault);
            TestRunner.Test("vendorType 缺失默认 huihuang", Test_ConfigManager_VendorTypeDefault);
            TestRunner.Test("ConfigManager 保存再加载新字段不丢失", Test_ConfigManager_RoundTrip);
        }

        // ──────────────────────────────────────────────
        // Slice 1: AppConfig 新字段反序列化
        // ──────────────────────────────────────────────

        static void Test_AppConfig_NewFields_Deserialize()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                string json = @"{
                    ""role"": ""station"",
                    ""vendorType"": ""bangcheng"",
                    ""listenPort"": 9000,
                    ""subscribers"": [""192.168.1.11:9000"", ""192.168.1.100:9000""],
                    ""mergeWindowMs"": 500,
                    ""dataRetentionDays"": 90,
                    ""teamStations"": [
                        {""id"": ""SSB"", ""name"": ""三水北"", ""ip"": ""127.0.0.1"", ""port"": 9000},
                        {""id"": ""DHD"", ""name"": ""大湖东"", ""ip"": ""192.168.1.11"", ""port"": 9000}
                    ],
                    ""switchGroups"": [
                        {""id"": ""1-J"", ""dataFileIndex"": 0, ""switchType"": ""ZYJ7""}
                    ],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);

                TestRunner.AssertEqual("station", config.Role, "role");
                TestRunner.AssertEqual("bangcheng", config.VendorType, "vendorType");
                TestRunner.AssertEqual(9000, config.ListenPort, "listenPort");
                TestRunner.AssertNotNull(config.Subscribers, "subscribers 非null");
                TestRunner.AssertEqual(2, config.Subscribers.Count, "subscribers 数量");
                TestRunner.AssertEqual("192.168.1.11:9000", config.Subscribers[0], "subscriber[0]");
                TestRunner.AssertEqual(500, config.MergeWindowMs, "mergeWindowMs");
                TestRunner.AssertEqual(90, config.DataRetentionDays, "dataRetentionDays");

                // teamStations
                TestRunner.AssertNotNull(config.TeamStations, "teamStations 非null");
                TestRunner.AssertEqual(2, config.TeamStations.Count, "teamStations 数量");
                TestRunner.AssertEqual("SSB", config.TeamStations[0].Id, "teamStations[0].Id");
                TestRunner.AssertEqual("三水北", config.TeamStations[0].Name, "teamStations[0].Name");
                TestRunner.AssertEqual("127.0.0.1", config.TeamStations[0].Ip, "teamStations[0].Ip");
                TestRunner.AssertEqual(9000, config.TeamStations[0].Port, "teamStations[0].Port");

                // switchGroups[].switchType
                TestRunner.AssertEqual(1, config.SwitchGroups.Count, "switchGroups 数量");
                TestRunner.AssertEqual("ZYJ7", config.SwitchGroups[0].SwitchType, "switchGroups[0].switchType");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_AppConfig_Defaults()
        {
            var config = new AppConfig();

            TestRunner.AssertEqual("station", config.Role, "默认 role");
            TestRunner.AssertEqual("huihuang", config.VendorType, "默认 vendorType");
            TestRunner.AssertEqual(9000, config.ListenPort, "默认 listenPort");
            TestRunner.AssertNotNull(config.Subscribers, "默认 subscribers 非null");
            TestRunner.AssertEqual(0, config.Subscribers.Count, "默认 subscribers 为空");
            TestRunner.AssertEqual(1000, config.MergeWindowMs, "默认 mergeWindowMs");
            TestRunner.AssertEqual(0, config.DataRetentionDays, "默认 dataRetentionDays");
            TestRunner.AssertNotNull(config.TeamStations, "默认 teamStations 非null");
            TestRunner.AssertEqual(0, config.TeamStations.Count, "默认 teamStations 为空");
            TestRunner.AssertNotNull(config.Stations, "默认 stations 非null");
            TestRunner.AssertEqual(0, config.Stations.Count, "默认 stations 为空");
        }

        // ──────────────────────────────────────────────
        // Slice 2: 向后兼容
        // ──────────────────────────────────────────────

        static void Test_BackwardCompat_OldConfig_Loads()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                // 旧版 config.json — 无 role, vendorType, listenPort 等新字段
                string json = @"{
                    ""switchGroups"": [
                        {""id"": ""1-J"", ""label"": ""1-J"", ""dataFileIndex"": 0}
                    ],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                // 不应抛异常
                var config = ConfigManager.LoadConfig(configPath);
                TestRunner.AssertNotNull(config, "配置加载成功");
                TestRunner.AssertEqual(1, config.SwitchGroups.Count, "switchGroups 解析正确");
                TestRunner.AssertEqual("1-J", config.SwitchGroups[0].Id, "旧字段仍可读");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_BackwardCompat_DefaultsApplied()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                // 旧版 config.json 无任何新字段
                string json = @"{
                    ""switchGroups"": [],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);

                // 所有新字段应退化到默认值
                TestRunner.AssertEqual("station", config.Role, "role 默认 station");
                TestRunner.AssertEqual("huihuang", config.VendorType, "vendorType 默认 huihuang");
                TestRunner.AssertEqual(9000, config.ListenPort, "listenPort 默认 9000");
                TestRunner.AssertNotNull(config.Subscribers, "subscribers 非null");
                TestRunner.AssertEqual(0, config.Subscribers.Count, "subscribers 为空列表");
                TestRunner.AssertEqual(1000, config.MergeWindowMs, "mergeWindowMs 默认 1000");
                TestRunner.AssertEqual(0, config.DataRetentionDays, "dataRetentionDays 默认 0");
                TestRunner.AssertNotNull(config.TeamStations, "teamStations 非null");
                TestRunner.AssertNotNull(config.Stations, "stations 非null");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 3: role 特定行为
        // ──────────────────────────────────────────────

        static void Test_CentralRole_StationsParsed()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                string json = @"{
                    ""role"": ""central"",
                    ""listenPort"": 9000,
                    ""dataRetentionDays"": 365,
                    ""stations"": [
                        {""id"": ""SSB"", ""name"": ""三水北站"", ""ip"": ""192.168.1.10"", ""port"": 9000},
                        {""id"": ""DHD"", ""name"": ""大湖东站"", ""ip"": ""192.168.1.11"", ""port"": 9000}
                    ],
                    ""switchGroups"": [],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);

                TestRunner.AssertEqual("central", config.Role, "role=central");
                TestRunner.AssertEqual(365, config.DataRetentionDays, "dataRetentionDays=365");
                TestRunner.AssertNotNull(config.Stations, "stations 非null");
                TestRunner.AssertEqual(2, config.Stations.Count, "stations 数量=2");
                TestRunner.AssertEqual("SSB", config.Stations[0].Id, "stations[0].Id");
                TestRunner.AssertEqual("192.168.1.10", config.Stations[0].Ip, "stations[0].Ip");
                TestRunner.AssertEqual(9000, config.Stations[0].Port, "stations[0].Port");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_StationRole_TeamStationsParsed()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                string json = @"{
                    ""role"": ""station"",
                    ""stationId"": ""SSB"",
                    ""vendorType"": ""huihuang"",
                    ""listenPort"": 9000,
                    ""teamStations"": [
                        {""id"": ""SSB"", ""name"": ""三水北站"", ""ip"": ""127.0.0.1"", ""port"": 9000},
                        {""id"": ""DHD"", ""name"": ""大湖东站"", ""ip"": ""192.168.1.11"", ""port"": 9000}
                    ],
                    ""switchGroups"": [],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);

                TestRunner.AssertEqual("station", config.Role, "role=station");
                TestRunner.AssertNotNull(config.TeamStations, "teamStations 非null");
                TestRunner.AssertEqual(2, config.TeamStations.Count, "teamStations 数量=2");
                // 第一项是本站（ip=127.0.0.1）
                TestRunner.AssertEqual("127.0.0.1", config.TeamStations[0].Ip, "本站 ip=127.0.0.1");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 4: SiteConfig 新字段
        // ──────────────────────────────────────────────

        static void Test_SiteConfig_IpPort()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                string json = @"{
                    ""role"": ""central"",
                    ""stations"": [
                        {""id"": ""SSB"", ""name"": ""三水北"", ""ip"": ""192.168.1.10"", ""port"": 9000}
                    ],
                    ""switchGroups"": [],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);

                TestRunner.AssertEqual(1, config.Stations.Count, "stations 数量");
                TestRunner.AssertEqual("192.168.1.10", config.Stations[0].Ip, "Ip");
                TestRunner.AssertEqual(9000, config.Stations[0].Port, "Port");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_SiteConfig_NoIp()
        {
            var site = new SiteConfig
            {
                Id = "test",
                Name = "测试站"
            };

            // Ip 缺失时应为 null，不参与网络通信
            TestRunner.AssertTrue(string.IsNullOrEmpty(site.Ip), "Ip 默认为空");
            TestRunner.AssertEqual(0, site.Port, "Port 默认 0");
        }

        // ──────────────────────────────────────────────
        // Slice 5: SwitchGroup.SwitchType
        // ──────────────────────────────────────────────

        static void Test_SwitchGroup_SwitchType()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                string json = @"{
                    ""switchGroups"": [
                        {""id"": ""1-J1"", ""dataFileIndex"": 0, ""switchType"": ""ZDJ9""},
                        {""id"": ""5-J"",  ""dataFileIndex"": 16, ""switchType"": ""ZYJ7""}
                    ],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);

                TestRunner.AssertEqual(2, config.SwitchGroups.Count, "switchGroups 数量");
                TestRunner.AssertEqual("ZDJ9", config.SwitchGroups[0].SwitchType, "1-J1 switchType=ZDJ9");
                TestRunner.AssertEqual("ZYJ7", config.SwitchGroups[1].SwitchType, "5-J switchType=ZYJ7");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_SwitchGroup_NoSwitchType()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                // 旧版 switchGroups 无 switchType
                string json = @"{
                    ""switchGroups"": [
                        {""id"": ""1-J"", ""label"": ""1-J"", ""dataFileIndex"": 0}
                    ],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);

                TestRunner.AssertEqual(1, config.SwitchGroups.Count, "switchGroups 数量");
                // 无 switchType 时应为 null（不抛异常）
                TestRunner.AssertTrue(config.SwitchGroups[0].SwitchType == null, "无 switchType 时为 null");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ──────────────────────────────────────────────
        // Slice 6: StationManifest / SwitchGroupDef 新字段
        // ──────────────────────────────────────────────

        static void Test_StationManifest_VendorType()
        {
            var manifest = new StationManifest
            {
                Id = "test",
                Name = "测试站",
                VendorType = "huihuang"
            };

            TestRunner.AssertEqual("huihuang", manifest.VendorType, "VendorType 可读写");
        }

        static void Test_SwitchGroupDef_SwitchType()
        {
            var def = new SwitchGroupDef
            {
                Id = "1-J1",
                Label = "1-J1",
                DataFileIndex = 0,
                SwitchType = "ZDJ9"
            };

            TestRunner.AssertEqual("ZDJ9", def.SwitchType, "SwitchType 可读写");
        }

        // ──────────────────────────────────────────────
        // Slice 7: ConfigManager 兼容处理
        // ──────────────────────────────────────────────

        static void Test_ConfigManager_RoleDefault()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                // role 字段缺失
                string json = @"{
                    ""switchGroups"": [],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);
                TestRunner.AssertEqual("station", config.Role, "role 缺失 → station");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_ConfigManager_VendorTypeDefault()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                // vendorType 字段缺失
                string json = @"{
                    ""role"": ""station"",
                    ""switchGroups"": [],
                    ""dataSourceDir"": ""./data"",
                    ""parsedDataDir"": ""./parsed"",
                    ""scanInterval"": 5
                }";

                File.WriteAllText(configPath, json, Encoding.UTF8);

                var config = ConfigManager.LoadConfig(configPath);
                TestRunner.AssertEqual("huihuang", config.VendorType, "vendorType 缺失 → huihuang");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        static void Test_ConfigManager_RoundTrip()
        {
            string tempDir = TestRunner.TempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");

                // 创建含全部新字段的配置
                var original = new AppConfig
                {
                    Role = "station",
                    VendorType = "bangcheng",
                    ListenPort = 8080,
                    Subscribers = new List<string> { "192.168.1.1:8080" },
                    MergeWindowMs = 2000,
                    DataRetentionDays = 30,
                    TeamStations = new List<SiteConfig>
                    {
                        new SiteConfig { Id = "S1", Name = "站1", Ip = "127.0.0.1", Port = 8080 }
                    },
                    Stations = new List<SiteConfig>(),
                    SwitchGroups = new List<SwitchGroup>
                    {
                        new SwitchGroup { Id = "1-J", DataFileIndex = 0, SwitchType = "ZYJ7" }
                    },
                    DataSourceDir = "./data",
                    ParsedDataDir = "./parsed",
                    ScanInterval = 5
                };

                // 保存
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(original);
                File.WriteAllText(configPath, json, Encoding.UTF8);

                // 重新加载
                var loaded = ConfigManager.LoadConfig(configPath);

                TestRunner.AssertEqual("station", loaded.Role, "roundtrip role");
                TestRunner.AssertEqual("bangcheng", loaded.VendorType, "roundtrip vendorType");
                TestRunner.AssertEqual(8080, loaded.ListenPort, "roundtrip listenPort");
                TestRunner.AssertEqual(1, loaded.Subscribers.Count, "roundtrip subscribers");
                TestRunner.AssertEqual(2000, loaded.MergeWindowMs, "roundtrip mergeWindowMs");
                TestRunner.AssertEqual(30, loaded.DataRetentionDays, "roundtrip dataRetentionDays");
                TestRunner.AssertEqual(1, loaded.TeamStations.Count, "roundtrip teamStations");
                TestRunner.AssertEqual("S1", loaded.TeamStations[0].Id, "roundtrip teamStation id");
                TestRunner.AssertEqual("127.0.0.1", loaded.TeamStations[0].Ip, "roundtrip teamStation ip");
                TestRunner.AssertEqual(1, loaded.SwitchGroups.Count, "roundtrip switchGroups");
                TestRunner.AssertEqual("ZYJ7", loaded.SwitchGroups[0].SwitchType, "roundtrip switchType");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
