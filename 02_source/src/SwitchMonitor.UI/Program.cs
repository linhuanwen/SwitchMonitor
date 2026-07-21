using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using SwitchMonitor.Data;
using SwitchMonitor.Network;

namespace SwitchMonitor.UI
{
    static class Program
    {
        /// <summary>
        /// 设置 WebBrowser 控件 IE 版本（至少 IE8 才支持 JSON.parse 和 VML 图表）
        /// </summary>
        static void SetBrowserEmulation()
        {
            try
            {
                string appName = Path.GetFileName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string regPath = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";
                using (var key = Registry.CurrentUser.CreateSubKey(regPath))
                {
                    if (key != null)
                    {
                        key.SetValue(appName, 8888, RegistryValueKind.DWord); // IE8 Standards Mode
                    }
                }
            }
            catch { } // 权限不足时静默忽略
        }

        /// <summary>
        /// 根据配置中的 SelectedSiteId 解析数据目录。
        /// 若站点配置存在则使用站点专属目录，否则回退到全局 ParsedDataDir。
        /// </summary>
        static string ResolveParsedDataDir(AppConfig config)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 查找选中站点的配置
            if (config.Sites != null && config.Sites.Count > 0 && !string.IsNullOrEmpty(config.SelectedSiteId))
            {
                foreach (var site in config.Sites)
                {
                    if (site.Id == config.SelectedSiteId)
                    {
                        string dir = site.ParsedDataDir ?? config.ParsedDataDir;
                        if (!Path.IsPathRooted(dir))
                            dir = Path.Combine(baseDir, dir);
                        return dir;
                    }
                }
            }

            // 回退：使用全局 ParsedDataDir
            string fallbackDir = config.ParsedDataDir ?? ".\\parsed_data";
            if (!Path.IsPathRooted(fallbackDir))
                fallbackDir = Path.Combine(baseDir, fallbackDir);
            return fallbackDir;
        }

        /// <summary>
        /// 应用程序主入口点
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 设置 WebBrowser IE 版本
            SetBrowserEmulation();

            // 加载配置
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            var configLoadResult = ConfigManager.LoadConfigWithStatus(configPath);
            AppConfig config = configLoadResult.Item1;
            bool configFallback = configLoadResult.Item2;

            if (configFallback)
            {
                MessageBox.Show("配置文件 config.json 不存在或损坏，已使用默认配置。\n\n请检查数据源路径等设置是否正确。",
                    "配置提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // ── 启动加载窗口（独立 STA 线程，避免迁移期间"无响应"） ──
            var splash = new SplashForm();
            var splashThread = new Thread(() => Application.Run(splash));
            splashThread.SetApartmentState(ApartmentState.STA);
            splashThread.IsBackground = true;
            splashThread.Start();
            while (!splash.Created) Thread.Sleep(30); // 等待窗口句柄就绪

            IndexManager indexManager = null;
            try
            {
                splash.UpdateStatus("正在初始化配置...");

                // 初始化数据索引（根据 SelectedSiteId 选择站点数据目录）
                string parsedDataDir = ResolveParsedDataDir(config);
                indexManager = new IndexManager(parsedDataDir);

                // N01-1: JSON → SQLite 一次性数据迁移（仅首次运行）
                var migrator = new DataMigrator(indexManager.Storage);
                if (!migrator.IsMigrated())
                {
                    splash.UpdateStatus("正在迁移数据（首次运行，请耐心等待）...");
                    try
                    {
                        var migResult = migrator.Migrate(parsedDataDir);
                        if (!migResult.Success && !migResult.Skipped)
                        {
                            MessageBox.Show("数据迁移失败:\n" + migResult.Message,
                                "迁移错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    catch (Exception migEx)
                    {
                        MessageBox.Show("数据迁移异常:\n" + migEx.Message + "\n\n程序将继续启动。",
                            "迁移错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                splash.UpdateStatus("正在加载索引...");
                indexManager.Initialize();
                splash.UpdateStatus("启动完成");
            }
            finally
            {
                splash.BeginInvoke(new Action(splash.Close));
                splashThread.Join(3000);
            }

            // ── N01-3: 网络层初始化（总终端/班组终端角色） ──
            ReceiveEndpoint receiveEndpoint = null;
            StationMonitor stationMonitor = null;
            DataCatcher dataCatcher = null;

            if (config.Role == "central" || (config.TeamStations != null && config.TeamStations.Count > 0))
            {
                try
                {
                    // 构建网络配置
                    var netConfig = new NetworkConfig
                    {
                        ListenPort = config.ListenPort > 0 ? config.ListenPort : 9000,
                        ParsedDataDir = config.ParsedDataDir ?? ".\\parsed_data",
                        ProbeIntervalMs = 120000,
                        HttpTimeoutMs = 10000,
                        OfflineThreshold = 2
                    };

                    // 添加站点列表
                    var stationList = (config.Role == "central")
                        ? (config.Stations ?? new System.Collections.Generic.List<SiteConfig>())
                        : (config.TeamStations ?? new System.Collections.Generic.List<SiteConfig>());

                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    foreach (var site in stationList)
                    {
                        if (string.IsNullOrEmpty(site.Ip)) continue;

                        string dbPath = Path.Combine(
                            Path.IsPathRooted(config.ParsedDataDir ?? ".\\parsed_data")
                                ? config.ParsedDataDir
                                : Path.Combine(baseDir, config.ParsedDataDir ?? ".\\parsed_data"),
                            site.Id + ".db");

                        netConfig.Stations.Add(new StationInfo
                        {
                            Id = site.Id,
                            Name = site.Name,
                            Ip = site.Ip,
                            Port = site.Port > 0 ? site.Port : 9000,
                            DbPath = dbPath
                        });
                    }

                    if (netConfig.Stations.Count > 0)
                    {
                        // 初始化补拉状态
                        string catchupPath = Path.Combine(
                            Path.IsPathRooted(config.ParsedDataDir ?? ".\\parsed_data")
                                ? config.ParsedDataDir
                                : Path.Combine(baseDir, config.ParsedDataDir ?? ".\\parsed_data"),
                            "catchup_state.json");
                        var catchupState = CatchupState.Load(catchupPath);

                        // 启动接收端点
                        receiveEndpoint = new ReceiveEndpoint(netConfig, catchupState);
                        receiveEndpoint.OnDataReceived += (stationId, count) =>
                        {
                            System.Diagnostics.Debug.WriteLine(
                                string.Format("收到 {0} 推送: {1} 条事件", stationId, count));
                        };
                        receiveEndpoint.OnError += (msg) =>
                        {
                            System.Diagnostics.Debug.WriteLine("接收端点错误: " + msg);
                        };
                        receiveEndpoint.Start();

                        // 启动站机监控
                        stationMonitor = new StationMonitor(netConfig, catchupState);
                        stationMonitor.OnError += (msg) =>
                        {
                            System.Diagnostics.Debug.WriteLine("站机监控错误: " + msg);
                        };
                        stationMonitor.Start();

                        // 启动补拉器
                        dataCatcher = new DataCatcher(netConfig, catchupState);
                        dataCatcher.ProgressChanged += (sender, args) =>
                        {
                            System.Diagnostics.Debug.WriteLine(
                                string.Format("补拉 {0}: {1}/{2} {3}",
                                    args.StationName, args.ReceivedCount, args.TotalCount,
                                    args.IsComplete ? "完成" : "进行中"));
                        };

                        // 离线→在线自动触发补拉
                        stationMonitor.StationStateChanged += (sender, args) =>
                        {
                            if (args.OldStatus == StationStatus.Offline && args.NewStatus == StationStatus.Online)
                            {
                                var station = netConfig.Stations.Find(s => s.Id == args.StationId);
                                if (station != null)
                                    dataCatcher.CatchupAsync(station);
                            }
                        };

                        System.Diagnostics.Debug.WriteLine(
                            string.Format("N01-3 网络层已启动: 监听端口 {0}, 监控 {1} 个站机",
                                netConfig.ListenPort, netConfig.Stations.Count));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("N01-3 网络层初始化失败: " + ex.Message);
                }
            }

            // 启动主窗口
            Application.Run(new MainForm(config, indexManager));

            // ── 清理网络组件 ──
            if (receiveEndpoint != null)
            {
                try { receiveEndpoint.Dispose(); } catch { }
            }
            if (stationMonitor != null)
            {
                try { stationMonitor.Dispose(); } catch { }
            }
            if (dataCatcher != null)
            {
                try { dataCatcher.Dispose(); } catch { }
            }
        }
    }
}
