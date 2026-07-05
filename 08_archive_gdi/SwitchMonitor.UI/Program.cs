using System;
using System.IO;
using System.Windows.Forms;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.UI
{
    /// <summary>
    /// 道岔监测系统 - 主程序入口
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// 应用程序主入口点
        /// </summary>
        [STAThread]
        public static void Main()
        {
            // 全局异常捕获 — 将未处理异常写入日志文件
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.WriteAllText(logPath, "ThreadException:\n" + e.Exception.ToString());
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.WriteAllText(logPath, "UnhandledException:\n" + (e.ExceptionObject?.ToString() ?? "(null)"));
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 查找数据库文件
            string dbPath = FindDatabase();
            if (string.IsNullOrEmpty(dbPath))
            {
                MessageBox.Show(
                    "找不到数据库文件 switch_test.db。\n\n"
                    + "请先运行: python scripts/create_test_db.py\n\n"
                    + "或手动将数据库文件放到 Data/ 目录下。",
                    "SwitchMonitor - 数据库未找到",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // 加载道岔映射配置（缺失或损坏时使用全降级模式，不阻塞启动）
            string mappingPath = FindMappingConfig();
            var mappingConfig = MappingConfig.Load(mappingPath);
            if (!string.IsNullOrEmpty(mappingPath))
            {
                System.Diagnostics.Debug.WriteLine("映射配置已加载: {0} (车站={1})",
                    mappingPath, mappingConfig.StationName);
            }

            // 查找或创建 config.json
            string configPath = FindConfigJson();
            var configManager = new ConfigManager(configPath);

            Application.Run(new MainForm(dbPath, mappingConfig));
        }

        private static string FindMappingConfig()
        {
            var candidates = new[]
            {
                // net40 输出目录 (bin/x86/Debug/net40/) 向上 5 级到项目根
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Config", "switch_mapping.json")),
                // net8.0-windows 输出目录 向上 4 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Config", "switch_mapping.json")),
                // bin/Debug/net8.0-windows 向上 1 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Config", "switch_mapping.json")),
                // bin 目录
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "switch_mapping.json")),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
            return null; // 找不到时 MappingConfig.Load 会返回默认值
        }

        private static string FindDatabase()
        {
            var candidates = new[]
            {
                // net40 输出目录 (bin/x86/Debug/net40/) 向上 5 级到项目根
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Data", "switch_test.db")),
                // net8.0-windows 输出目录 向上 4 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Data", "switch_test.db")),
                // bin/Debug/net8.0-windows 向上 1 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "Data", "switch_test.db")),
                // bin 目录
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "switch_test.db")),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private static string FindConfigJson()
        {
            var candidates = new[]
            {
                // net40 输出目录 (bin/x86/Debug/net40/) 向上 5 级到项目根
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "config.json")),
                // net8.0-windows 输出目录 向上 4 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "config.json")),
                // bin 目录向上 1 级
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "config.json")),
                // bin 目录
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json")),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }

            // 默认使用第一个路径（文件不存在时 ConfigManager 会创建）
            return candidates[0];
        }
    }
}
