using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SwitchMonitor.Data;
using SwitchMonitor.Storage;

namespace SwitchMonitor.DataForwarder
{
    /// <summary>
    /// DataForwarder 主程序入口。
    /// 独立后台进程：轮询 SQLite 推送数据 + 响应 HTTP 请求。
    /// 编译为 Windows 应用程序（WinExe），隐藏窗口，系统托盘图标。
    /// </summary>
    public static class Program
    {
        private static ForwarderEngine _engine;
        private static NotifyIcon _notifyIcon;

        /// <summary>
        /// 主入口。支持命令行参数：
        ///   --config path  指定 config.json 路径
        ///   --db path      指定 SQLite 数据库路径
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                // 解析命令行参数
                string configPath = "config.json";
                string dbPath = null;

                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--config" && i + 1 < args.Length)
                        configPath = args[++i];
                    else if (args[i] == "--db" && i + 1 < args.Length)
                        dbPath = args[++i];
                }

                // 解析相对路径
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!Path.IsPathRooted(configPath))
                    configPath = Path.Combine(baseDir, configPath);

                // 加载配置
                AppConfig appConfig;
                bool isDefaultConfig;
                try
                {
                    var result = ConfigManager.LoadConfigWithStatus(configPath);
                    appConfig = result.Item1;
                    isDefaultConfig = result.Item2;
                }
                catch
                {
                    appConfig = new AppConfig();
                    isDefaultConfig = true;
                }

                // 确定数据库路径
                if (string.IsNullOrEmpty(dbPath))
                {
                    // 从 config 中推断 parsedDataDir → {stationId}.db
                    string parsedDataDir = appConfig.ParsedDataDir ?? ".\\parsed_data";
                    if (!Path.IsPathRooted(parsedDataDir))
                        parsedDataDir = Path.Combine(baseDir, parsedDataDir);

                    string stationId = appConfig.StationId;
                    if (string.IsNullOrEmpty(stationId) && appConfig.TeamStations != null && appConfig.TeamStations.Count > 0)
                        stationId = appConfig.TeamStations[0].Id;
                    if (string.IsNullOrEmpty(stationId) && appConfig.Sites != null && appConfig.Sites.Count > 0)
                        stationId = appConfig.Sites[0].Id;
                    if (string.IsNullOrEmpty(stationId))
                        stationId = "station";

                    dbPath = Path.Combine(parsedDataDir, stationId + ".db");
                }

                if (!File.Exists(dbPath))
                {
                    System.Diagnostics.Debug.WriteLine("DataForwarder: 数据库不存在: " + dbPath);
                    // 创建空数据库
                    Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
                }

                // 构建 ForwarderConfig
                ForwarderConfig fwdConfig = ForwarderConfig.FromAppConfig(appConfig, dbPath);

                // 启动引擎
                _engine = new ForwarderEngine(fwdConfig);
                _engine.Start();

                // 系统托盘图标
                SetupTrayIcon(fwdConfig);

                // 消息循环
                Application.Run();

                return 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("DataForwarder fatal: " + ex);
                return 1;
            }
        }

        private static void SetupTrayIcon(ForwarderConfig config)
        {
            _notifyIcon = new NotifyIcon
            {
                Text = string.Format("DataForwarder - {0} ({1}:{2})",
                    config.StationName, config.StationId, config.ListenPort),
                Visible = true
            };

            // 使用默认图标（无资源文件时的回退）
            try
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            catch { }

            var contextMenu = new ContextMenu(new MenuItem[]
            {
                new MenuItem("状态: 运行中", (s, e) => { }) { Enabled = false },
                new MenuItem("-"),
                new MenuItem("退出", (s, e) =>
                {
                    _notifyIcon.Visible = false;
                    _engine.Stop();
                    Application.Exit();
                })
            });

            _notifyIcon.ContextMenu = contextMenu;
        }
    }
}
