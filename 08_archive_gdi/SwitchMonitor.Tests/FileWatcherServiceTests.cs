using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// FileWatcherService 集成测试（Slice 3）。
    /// 每个测试使用临时目录和临时 SQLite 数据库，测试后自动清理。
    /// </summary>
    public static class FileWatcherServiceTests
    {
        static int passed;
        static int failed;

        public static (int, int) RunAll()
        {
            passed = 0;
            failed = 0;

            Console.WriteLine("=== FileWatcherService 集成测试 ===");
            Console.WriteLine();

            Test_ScanFindsNewFiles();
            Test_SkipProcessedFiles();
            Test_ErrorIsolation();
            Test_StartStopLifecycle();
            Test_CrossSessionRecovery();
            Test_FileSystemWatcherIntegration();
            Test_CsvFileSupport();
            Test_OnDataUpdatedEvent();
            Test_ProcessedJsonExport();
            Test_CurrentHourFileReprocess();

            Console.WriteLine();
            Console.WriteLine("=== FileWatcherService 测试结果 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            Console.WriteLine();

            return (passed, failed);
        }

        // ==================== 测试方法 ====================

        /// <summary>
        /// 测试 1: 扫描发现新文件并触发处理
        /// </summary>
        static void Test_ScanFindsNewFiles()
        {
            Console.WriteLine("--- 测试 1: 扫描发现新文件 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string testFile = Path.Combine(tempDir, "SwitchCurve(0).dat");
                File.WriteAllText(testFile, "FAKE_CSM2010_DATA");
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-5));  // 避免被 IsCurrentHourFile 跳过

                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.FilePattern = "*.dat";

                    bool parserCalled = false;
                    service.SwitchCurveParser = (filePath) =>
                    {
                        parserCalled = true;
                        Assert(filePath == testFile, "解析器收到正确的文件路径");
                        return new List<SwitchActionData>();
                    };

                    service.ScanOnce();

                    Assert(parserCalled, "SwitchCurve 解析器被调用");

                    var repo = new ProcessedFileRepository(dbFactory);
                    Assert(repo.IsFileProcessed(testFile), "文件被标记为已处理");
                    Assert(repo.GetProcessedCount() == 1, "已处理文件计数为 1");
                }

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 1 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 1 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 2: 已处理文件不重复解析
        /// </summary>
        static void Test_SkipProcessedFiles()
        {
            Console.WriteLine("--- 测试 2: 已处理文件不重复解析 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string testFile = Path.Combine(tempDir, "SwitchCurve(1).dat");
                File.WriteAllText(testFile, "FAKE_CSM2010_DATA");
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-5));

                int parseCount = 0;

                // 第一次扫描
                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.SwitchCurveParser = (filePath) =>
                    {
                        parseCount++;
                        return new List<SwitchActionData>();
                    };
                    service.ScanOnce();
                }
                Assert(parseCount == 1, "第一次扫描：解析器被调用 1 次");

                // 第二次扫描（新实例）
                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.SwitchCurveParser = (filePath) =>
                    {
                        parseCount++;
                        return new List<SwitchActionData>();
                    };
                    service.ScanOnce();
                }
                Assert(parseCount == 1, "第二次扫描：解析器不再被调用（跳过已处理）");

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 2 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 2 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 3: 错误隔离 - 损坏文件不崩溃
        /// </summary>
        static void Test_ErrorIsolation()
        {
            Console.WriteLine("--- 测试 3: 错误隔离 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string badFile = Path.Combine(tempDir, "SwitchCurve(0).dat");
                string goodFile = Path.Combine(tempDir, "Digit(0).dat");
                File.WriteAllText(badFile, "CORRUPT");
                File.SetLastWriteTime(badFile, DateTime.Now.AddMinutes(-5));
                File.WriteAllText(goodFile, "NORMAL");
                File.SetLastWriteTime(goodFile, DateTime.Now.AddMinutes(-5));

                int goodParsed = 0;
                var errors = new List<string>();

                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.OnFileError += (msg, ex) => errors.Add(msg);

                    service.SwitchCurveParser = (filePath) =>
                    {
                        throw new InvalidDataException("格式错误：无效的魔数");
                    };
                    service.DigitParser = (filePath) =>
                    {
                        goodParsed++;
                        return new List<StatusEvent>();
                    };

                    service.ScanOnce();
                }

                Assert(goodParsed == 1, "正常文件被处理");
                Assert(errors.Count >= 1, "错误事件被触发");

                using (var dbFactory = new DatabaseFactory(dbPath))
                {
                    var repo = new ProcessedFileRepository(dbFactory);
                    var record = repo.GetRecord(badFile);
                    Assert(record != null, "损坏文件有处理记录");
                    Assert(record.Status == "error", string.Format("损坏文件状态为 error，实际: {0}", record.Status));
                    Assert(!string.IsNullOrEmpty(record.ErrorMessage), "错误信息已记录");
                }

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 3 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 3 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 4: Start/Stop 生命周期
        /// </summary>
        static void Test_StartStopLifecycle()
        {
            Console.WriteLine("--- 测试 4: 启动/停止生命周期 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string testFile = Path.Combine(tempDir, "SwitchCurve(0).dat");
                File.WriteAllText(testFile, "TEST");
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-5));

                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.ScanIntervalSeconds = 1;
                    service.SwitchCurveParser = (filePath) => new List<SwitchActionData>();

                    Assert(!service.IsRunning, "初始状态 IsRunning = false");
                    Assert(service.LastScanTime == null, "初始状态 LastScanTime = null");

                    service.Start();
                    Assert(service.IsRunning, "启动后 IsRunning = true");

                    Thread.Sleep(200);
                    Assert(service.LastScanTime != null, "启动后 LastScanTime 不为 null");

                    var repo = new ProcessedFileRepository(dbFactory);
                    Assert(repo.IsFileProcessed(testFile), "启动时自动执行了初始扫描");

                    service.Stop();
                    Assert(!service.IsRunning, "停止后 IsRunning = false");
                }

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 4 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 4 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 5: 跨会话恢复
        /// </summary>
        static void Test_CrossSessionRecovery()
        {
            Console.WriteLine("--- 测试 5: 跨会话恢复 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string testFile = Path.Combine(tempDir, "SwitchCurve(0).dat");
                File.WriteAllText(testFile, "PERSISTENT_DATA");
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-5));

                int parseCount = 0;

                // 会话 1
                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.SwitchCurveParser = (filePath) =>
                    {
                        parseCount++;
                        return new List<SwitchActionData>();
                    };
                    service.ScanOnce();
                }
                Assert(parseCount == 1, "会话 1：文件被处理 1 次");

                // 会话 2（模拟重启）
                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.SwitchCurveParser = (filePath) =>
                    {
                        parseCount++;
                        return new List<SwitchActionData>();
                    };
                    service.ScanOnce();
                }
                Assert(parseCount == 1, "会话 2（重启后）：解析器不再被调用");

                using (var dbFactory = new DatabaseFactory(dbPath))
                {
                    var repo = new ProcessedFileRepository(dbFactory);
                    Assert(repo.IsFileProcessed(testFile), "文件处理记录跨会话持久化");
                    Assert(repo.GetProcessedCount() == 1, "只有一条处理记录");
                }

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 5 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 5 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 6: FileSystemWatcher 主监控 + Timer 兜底
        /// </summary>
        static void Test_FileSystemWatcherIntegration()
        {
            Console.WriteLine("--- 测试 6: FileSystemWatcher 主监控 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.FilePattern = "*.dat;*.csv";
                    service.ScanIntervalSeconds = 60;

                    // 验证默认扫描间隔为 60 秒
                    Assert(service.ScanIntervalSeconds == 60, "默认扫描间隔为 60 秒");

                    // 验证 FilePattern 支持 CSV
                    Assert(service.FilePattern.Contains("csv"), "FilePattern 包含 csv");

                    // 构建 FileSystemWatcher（不启动定时器）
                    service.InitializeFileSystemWatcher();

                    // 验证 FileSystemWatcher 已启用
                    Assert(service.IsFileSystemWatcherEnabled, "FileSystemWatcher 已启用");

                    // 模拟 FileSystemWatcher 检测到新文件
                    bool watcherFired = false;
                    service.OnFileDetected += (filePath) =>
                    {
                        watcherFired = true;
                    };

                    // 写入一个新 .csv 文件
                    string csvFile = Path.Combine(tempDir, "SwitchCurve(0).csv");
                    File.WriteAllText(csvFile, "timestamp,datetime,phase,s0,s1\n1776243701,2026-04-15 17:01:41,16777216,5.6,1.4\n");
                    File.SetLastWriteTime(csvFile, DateTime.Now.AddMinutes(-5));

                    // 手动触发 FileSystemWatcher 的 Created 事件模拟
                    service.SimulateFileCreated(csvFile);
                    Assert(watcherFired, "FileSystemWatcher 检测到新文件并触发事件");

                    service.Stop();
                }

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 6 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 6 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 7: CSV 文件支持
        /// </summary>
        static void Test_CsvFileSupport()
        {
            Console.WriteLine("--- 测试 7: CSV 文件支持 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string csvFile = Path.Combine(tempDir, "SwitchCurve(0).csv");
                File.WriteAllText(csvFile, "timestamp,datetime,phase,s0,s1\n1776243701,2026-04-15 17:01:41,16777216,5.6,1.4\n");
                File.SetLastWriteTime(csvFile, DateTime.Now.AddMinutes(-5));

                bool csvParsed = false;

                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.FilePattern = "*.dat;*.csv";

                    service.SwitchCurveParser = (filePath) =>
                    {
                        csvParsed = true;
                        Assert(filePath.EndsWith(".csv"), string.Format("CSV 文件被解析: {0}", filePath));
                        return new List<SwitchActionData>
                        {
                            new SwitchActionData { SwitchId = "SW_01", StartTime = 1776243701 }
                        };
                    };

                    service.ScanOnce();

                    Assert(csvParsed, "CSV 文件被正确解析");

                    var repo = new ProcessedFileRepository(dbFactory);
                    Assert(repo.IsFileProcessed(csvFile), "CSV 文件被标记为已处理");
                }

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 7 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 7 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 8: OnDataUpdated 事件传递变更的 switchId 列表
        /// </summary>
        static void Test_OnDataUpdatedEvent()
        {
            Console.WriteLine("--- 测试 8: OnDataUpdated 事件 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string testFile = Path.Combine(tempDir, "SwitchCurve(0).dat");
                File.WriteAllText(testFile, "TEST_DATA");
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-5));

                List<string> receivedSwitchIds = null;

                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };

                    service.OnDataUpdated += (switchIds) =>
                    {
                        receivedSwitchIds = switchIds;
                    };

                    service.SwitchCurveParser = (filePath) =>
                    {
                        return new List<SwitchActionData>
                        {
                            new SwitchActionData { SwitchId = "SW_01", StartTime = 1776243701 },
                            new SwitchActionData { SwitchId = "SW_02", StartTime = 1776243702 }
                        };
                    };

                    service.ScanOnce();
                }

                Assert(receivedSwitchIds != null, "OnDataUpdated 事件被触发");
                Assert(receivedSwitchIds.Count == 2, string.Format("收到 2 个 switchId (实际: {0})", receivedSwitchIds != null ? receivedSwitchIds.Count : 0));
                Assert(receivedSwitchIds.Contains("SW_01"), "包含 SW_01");
                Assert(receivedSwitchIds.Contains("SW_02"), "包含 SW_02");
                // 验证去重
                Assert(receivedSwitchIds.Count == receivedSwitchIds.Distinct().Count(), "switchId 列表无重复");

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 8 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 8 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 9: _processed.json 导出
        /// </summary>
        static void Test_ProcessedJsonExport()
        {
            Console.WriteLine("--- 测试 9: _processed.json 导出 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");
                string parsedDataDir = Path.Combine(tempDir, "parsed_data");
                Directory.CreateDirectory(parsedDataDir);

                string testFile = Path.Combine(tempDir, "SwitchCurve(0).dat");
                File.WriteAllText(testFile, "TEST");
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-5));

                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.ParsedDataDir = parsedDataDir;
                    service.SwitchCurveParser = (filePath) => new List<SwitchActionData>
                    {
                        new SwitchActionData { SwitchId = "SW_01" }
                    };

                    service.ScanOnce();
                }

                // 验证 _processed.json 文件存在
                string processedJsonPath = Path.Combine(parsedDataDir, "_processed.json");
                Assert(File.Exists(processedJsonPath), "_processed.json 文件已创建: " + processedJsonPath);

                // 验证 JSON 内容
                string jsonContent = File.ReadAllText(processedJsonPath, Encoding.UTF8);
                Console.WriteLine("  _processed.json 内容: {0}", jsonContent);

                var records = JsonConvert.DeserializeObject<List<ProcessedFileRecord>>(jsonContent);
                Assert(records != null, "_processed.json 可反序列化");
                Assert(records.Count >= 1, "_processed.json 至少 1 条记录");

                var record = records.Find(r => r.FilePath == testFile);
                Assert(record != null, "找到测试文件的记录");
                Assert(record.Status == "processed", string.Format("状态为 processed (实际: {0})", record.Status));
                Assert(!string.IsNullOrEmpty(record.LastProcessedTime), "有处理时间");
                Assert(record.FileSize > 0, "文件大小 > 0");

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 9 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 9 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 10: 当前小时文件重新处理策略
        /// </summary>
        static void Test_CurrentHourFileReprocess()
        {
            Console.WriteLine("--- 测试 10: 当前小时文件重新处理 ---");
            try
            {
                string tempDir = CreateTempDir();
                string dbPath = Path.Combine(tempDir, "test.db");

                string testFile = Path.Combine(tempDir, "SwitchCurve(0).dat");
                File.WriteAllText(testFile, "DATA_V1");
                // 设置为"当前小时文件"但不在写入窗口内（修改时间 > 扫描间隔×2）
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-5));

                int parseCount = 0;

                // 第一次扫描：当前小时文件（不在写入窗口）应该被处理
                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.ScanIntervalSeconds = 60; // 写入窗口 = 120s
                    service.SwitchCurveParser = (filePath) =>
                    {
                        parseCount++;
                        return new List<SwitchActionData>
                        {
                            new SwitchActionData { SwitchId = "SW_01", StartTime = 1776243701 }
                        };
                    };

                    service.ScanOnce();
                }

                Assert(parseCount == 1,
                    string.Format("当前小时文件（不在写入窗口）被处理，解析次数: {0}", parseCount));

                // 更新文件内容（模拟持续写入 - 修改时间变化）
                File.WriteAllText(testFile, "DATA_V2_UPDATED");
                File.SetLastWriteTime(testFile, DateTime.Now.AddMinutes(-3)); // 仍在当前小时但不在写入窗口

                // 第二次扫描：当前小时文件重新处理（覆盖旧数据）
                using (var dbFactory = new DatabaseFactory(dbPath))
                using (var service = new FileWatcherService(dbFactory))
                {
                    service.ScanPaths = new List<string> { tempDir };
                    service.ScanIntervalSeconds = 60;
                    service.SwitchCurveParser = (filePath) =>
                    {
                        parseCount++;
                        return new List<SwitchActionData>
                        {
                            new SwitchActionData { SwitchId = "SW_01", StartTime = 1776243701 }
                        };
                    };

                    service.ScanOnce();
                }

                Assert(parseCount == 2,
                    string.Format("当前小时文件更新后被重新处理，解析次数: {0}", parseCount));

                CleanupTempDir(tempDir);
                Console.WriteLine("  [PASS] 测试 10 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 10 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        // ==================== 辅助方法 ====================

        static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "SwitchMonitor_Tests_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }

        static void CleanupTempDir(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { /* 忽略清理错误 */ }
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                Console.WriteLine("    ASSERT FAIL: {0}", message);
                throw new Exception(message);
            }
        }
    }
}
