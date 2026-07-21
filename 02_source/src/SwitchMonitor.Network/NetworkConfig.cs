using System.Collections.Generic;

namespace SwitchMonitor.Network
{
    /// <summary>
    /// 站机连接信息 — 用于 StationMonitor 和 DataCatcher。
    /// </summary>
    public class StationInfo
    {
        /// <summary>站点标识（如 "SSB"）</summary>
        public string Id { get; set; }

        /// <summary>站点显示名称（如 "三水北站"）</summary>
        public string Name { get; set; }

        /// <summary>站机 IP 地址</summary>
        public string Ip { get; set; }

        /// <summary>站机 HTTP 端口</summary>
        public int Port { get; set; }

        /// <summary>本站存储该站点数据的 SQLite 路径</summary>
        public string DbPath { get; set; }

        public StationInfo()
        {
            Id = "";
            Name = "";
            Ip = "127.0.0.1";
            Port = 9000;
        }

        /// <summary>HTTP 基地址</summary>
        public string BaseUrl
        {
            get { return string.Format("http://{0}:{1}", Ip, Port); }
        }
    }

    /// <summary>
    /// 网络层配置 — 接收端 + 监控 + 补拉所需配置。
    /// </summary>
    public class NetworkConfig
    {
        /// <summary>本地 HTTP 监听端口</summary>
        public int ListenPort { get; set; }

        /// <summary>已解析数据根目录（如 .\parsed_data）</summary>
        public string ParsedDataDir { get; set; }

        /// <summary>需要监控/接收数据的目标站点列表</summary>
        public List<StationInfo> Stations { get; set; }

        /// <summary>主动探测间隔（毫秒），默认 120000（2 分钟）</summary>
        public int ProbeIntervalMs { get; set; }

        /// <summary>HTTP 请求超时（毫秒），默认 10000（10 秒）</summary>
        public int HttpTimeoutMs { get; set; }

        /// <summary>连续失败次数阈值，达到后标记离线</summary>
        public int OfflineThreshold { get; set; }

        /// <summary>补拉进度通知间隔（条数），每收到 N 条触发一次 ProgressChanged</summary>
        public int CatchupProgressInterval { get; set; }

        public NetworkConfig()
        {
            ListenPort = 9000;
            ParsedDataDir = ".\\parsed_data";
            Stations = new List<StationInfo>();
            ProbeIntervalMs = 120000;
            HttpTimeoutMs = 10000;
            OfflineThreshold = 2;
            CatchupProgressInterval = 10;
        }
    }
}
