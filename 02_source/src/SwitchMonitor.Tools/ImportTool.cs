using System;
using System.Collections.Generic;
using System.IO;
using SwitchMonitor.Data;
using SwitchMonitor.Diagnosis;

namespace SwitchMonitor.Tools
{
    /// <summary>
    /// 命令行数据导入工具（含 D4 诊断管道集成）
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

            // ── D4: 装配诊断管道 ──
            bool diagnosisEnabled = config.Diagnosis != null && config.Diagnosis.Enabled;

            if (diagnosisEnabled)
            {
                try
                {
                    pipeline.DiagnoseHook = DiagnosisRunner.CreateHook(config.Diagnosis, config.ParsedDataDir);
                    Console.WriteLine("[诊断] 已启用自动诊断");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[诊断] 初始化失败，诊断已禁用: " + ex.Message);
                    diagnosisEnabled = false;
                }
            }
            else
            {
                Console.WriteLine("[诊断] 已禁用 (diagnosis.enabled=false)");
            }

            // ── 执行导入 ──
            try
            {
                pipeline.ImportAll();
                Console.WriteLine();
                Console.WriteLine("导入完成！共 {0} 个动作事件", pipeline.TotalEventsImported);
                Console.WriteLine("数据已保存到: " + Path.GetFullPath(parsedDataDir));

                // D4: 打印报警汇总
                if (diagnosisEnabled)
                {
                    PrintAlarmSummary(indexManager, config.SwitchGroups);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("导入失败: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// 打印每台道岔的故障/报警/预警计数汇总（导入完成后）。
        /// </summary>
        private static void PrintAlarmSummary(IndexManager indexManager, List<SwitchGroup> groups)
        {
            var alarmsIndex = indexManager.LoadAlarmsIndex();

            Console.WriteLine();
            Console.WriteLine("=== 诊断报警汇总 ===");
            Console.WriteLine("{0,-6} {1,5} {2,5} {3,5} {4,6}",
                "道岔", "故障", "报警", "预警", "合计");

            int totalFault = 0, totalAlarm = 0, totalWarning = 0;

            foreach (var group in groups)
            {
                int fault = 0, alarm = 0, warning = 0;

                if (alarmsIndex.ContainsKey(group.Id))
                {
                    foreach (var dateKvp in alarmsIndex[group.Id])
                    {
                        var counts = dateKvp.Value;
                        if (counts.ContainsKey("故障")) fault += counts["故障"];
                        if (counts.ContainsKey("报警")) alarm += counts["报警"];
                        if (counts.ContainsKey("预警")) warning += counts["预警"];
                    }
                }

                int sum = fault + alarm + warning;
                if (sum > 0)
                {
                    Console.WriteLine("{0,-6} {1,5} {2,5} {3,5} {4,6}",
                        group.Id, fault, alarm, warning, sum);
                }

                totalFault += fault;
                totalAlarm += alarm;
                totalWarning += warning;
            }

            int totalSum = totalFault + totalAlarm + totalWarning;
            Console.WriteLine(new string('-', 32));
            Console.WriteLine("{0,-6} {1,5} {2,5} {3,5} {4,6}",
                "合计", totalFault, totalAlarm, totalWarning, totalSum);
        }
    }
}
