using System;
using System.Collections.Generic;

namespace SwitchMonitor.DataForwarder
{
    /// <summary>
    /// DataForwarder 配置 POCO。从 config.json (AppConfig) 提取所需字段。
    /// </summary>
    public class ForwarderConfig
    {
        public string StationId { get; set; }
        public string StationName { get; set; }
        public int ListenPort { get; set; }
        public List<string> Subscribers { get; set; }
        public int MergeWindowMs { get; set; }
        public string DbPath { get; set; }
        public string SyncStatePath { get; set; }

        public ForwarderConfig()
        {
            StationId = "";
            StationName = "";
            ListenPort = 9000;
            Subscribers = new List<string>();
            MergeWindowMs = 1000;
            DbPath = "switch_events.db";
            SyncStatePath = ".sync_state.json";
        }

        /// <summary>
        /// 从 AppConfig 创建 ForwarderConfig。
        /// </summary>
        public static ForwarderConfig FromAppConfig(SwitchMonitor.Data.AppConfig appConfig, string dbPath)
        {
            var config = new ForwarderConfig
            {
                StationId = appConfig.StationId ?? "",
                StationName = appConfig.StationName ?? "",
                ListenPort = appConfig.ListenPort > 0 ? appConfig.ListenPort : 9000,
                Subscribers = appConfig.Subscribers ?? new List<string>(),
                MergeWindowMs = appConfig.MergeWindowMs > 0 ? appConfig.MergeWindowMs : 1000,
                DbPath = dbPath,
                SyncStatePath = ".sync_state.json"
            };

            // stationId/stationName 回退：从 TeamStations/Sites 推断
            if (string.IsNullOrEmpty(config.StationId) && appConfig.TeamStations != null && appConfig.TeamStations.Count > 0)
                config.StationId = appConfig.TeamStations[0].Id ?? "";
            if (string.IsNullOrEmpty(config.StationName) && appConfig.TeamStations != null && appConfig.TeamStations.Count > 0)
                config.StationName = appConfig.TeamStations[0].Name ?? "";
            if (string.IsNullOrEmpty(config.StationId) && appConfig.Sites != null && appConfig.Sites.Count > 0)
                config.StationId = appConfig.Sites[0].Id ?? "";
            if (string.IsNullOrEmpty(config.StationName) && appConfig.Sites != null && appConfig.Sites.Count > 0)
                config.StationName = appConfig.Sites[0].Name ?? "";

            return config;
        }
    }
}
