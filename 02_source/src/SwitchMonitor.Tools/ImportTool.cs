using System;
using System.IO;
using SwitchMonitor.Data;

namespace SwitchMonitor.Tools
{
    /// <summary>
    /// 命令行数据导入工具
    /// 用法: ImportTool.exe [config.json路径]
    /// </summary>
    static class ImportTool
    {
        static void Main(string[] args)
        {
            string configPath = args.Length > 0 ? args[0] : "config.json";

            Console.WriteLine("=== 道岔监控 CSV 数据导入工具 ===");
            Console.WriteLine("配置文件: " + Path.GetFullPath(configPath));

            // 加载配置
            var configResult = ConfigManager.LoadConfigWithStatus(configPath);
            AppConfig config = configResult.Item1;

            if (configResult.Item2)
                Console.WriteLine("[警告] 配置文件有问题，已使用默认值");

            // 初始化索引管理器
            string parsedDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.ParsedDataDir);
            Console.WriteLine("输出目录: " + Path.GetFullPath(parsedDataDir));

            var indexManager = new IndexManager(parsedDataDir);
            indexManager.Initialize();

            // 运行导入
            var pipeline = new DataPipeline(config, indexManager);
            pipeline.OnProgress += (msg, pct) =>
            {
                Console.WriteLine("  [{0}%] {1}", pct, msg);
            };

            try
            {
                pipeline.ImportAll();
                Console.WriteLine();
                Console.WriteLine("导入完成！共 {0} 个动作事件", pipeline.TotalEventsImported);
                Console.WriteLine("数据已保存到: " + Path.GetFullPath(parsedDataDir));
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("导入失败: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }
    }
}
