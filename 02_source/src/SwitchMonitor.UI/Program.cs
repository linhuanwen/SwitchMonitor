using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using SwitchMonitor.Data;

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

            // 初始化数据索引
            string parsedDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.ParsedDataDir);
            var indexManager = new IndexManager(parsedDataDir);
            indexManager.Initialize();

            // 启动主窗口
            Application.Run(new MainForm(config, indexManager));
        }
    }
}
