using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 9: Digit 开关量解析 + 状态时间线 TDD 测试。
    /// Seam 1 测试 DigitParser，Seam 2 测试 DataRepository StatusEvents 写入。
    /// </summary>
    public class Slice9Tests
    {
        static int passed = 0;
        static int failed = 0;
        static string testDigitDir;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 9: Digit 开关量解析 + 状态时间线 测试 ===");
            Console.WriteLine();

            testDigitDir = FindDigitDataDir();

            // === Seam 1: DigitParser ===
            TestDigitParser_ParseRealFile();
            TestDigitParser_EventStructure();
            TestDigitParser_TimestampRange();
            TestDigitParser_PointIdAndStateByte();
            TestDigitParser_InvalidInput();

            // === Seam 2: DataRepository StatusEvents ===
            TestRepo_SaveStatusEvents();
            TestRepo_StatusEventCountCorrect();

            // === Seam 3: 交叉验证（全部 84 个文件） ===
            TestCrossVerify_AllFiles();

            Console.WriteLine();
            Console.WriteLine("=== Slice 9 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // Seam 1: DigitParser
        // ================================================================

        /// <summary>Seam 1.1: 解析真实 Digit(*).dat，验证事件数 > 0</summary>
        static void TestDigitParser_ParseRealFile()
        {
            Console.WriteLine("--- Seam 1.1: 解析 Digit(2026_06_26_00).dat ---");
            try
            {
                string fp = Path.Combine(testDigitDir, "Digit(2026_06_26_00).dat");
                Assert(File.Exists(fp), "测试文件存在");

                byte[] data = File.ReadAllBytes(fp);
                var parser = new DigitParser();
                var events = parser.Parse(data, "Digit(2026_06_26_00).dat");

                Assert(events != null, "返回非 null");
                Assert(events.Count > 0, "解析出至少 1 个事件");
                Console.WriteLine("  解析出 {0} 个开关量事件", events.Count);

                // 第一个文件预期 ~92 个事件（与 Python digit_test3.csv 91 行数据一致，±5%）
                Assert(events.Count >= 85 && events.Count <= 100,
                    string.Format("事件数 {0} 在 [85, 100] 范围", events.Count));

                Console.WriteLine("  [PASS] Seam 1.1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>Seam 1.2: 验证事件结构字段非空</summary>
        static void TestDigitParser_EventStructure()
        {
            Console.WriteLine("--- Seam 1.2: 事件结构验证 ---");
            try
            {
                string fp = Path.Combine(testDigitDir, "Digit(2026_06_26_00).dat");
                var parser = new DigitParser();
                var events = parser.Parse(File.ReadAllBytes(fp), "test.dat");

                foreach (var e in events)
                {
                    Assert(e.FileSource != null, "FileSource 非 null");
                    Assert(e.Timestamp > 1500000000L, string.Format("Timestamp={0} 有效", e.Timestamp));
                    Assert(e.Timestamp < 2000000000L, string.Format("Timestamp={0} < 2B", e.Timestamp));
                    Assert(e.PointId >= 0 && e.PointId <= 255,
                        string.Format("PointId={0} 在 [0,255]", e.PointId));
                    Assert(e.StateByte >= 0 && e.StateByte <= 255,
                        string.Format("StateByte=0x{0:X2} 在 [0,255]", e.StateByte));
                    Assert(e.RawValue >= 0 && e.RawValue <= 65535,
                        string.Format("RawValue=0x{0:X4} 在 [0,65535]", e.RawValue));
                }

                Console.WriteLine("  所有 {0} 个事件结构字段有效", events.Count);
                Console.WriteLine("  [PASS] Seam 1.2");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>Seam 1.3: Timestamp 在 [ts_start-60, ts_end+60] 范围内</summary>
        static void TestDigitParser_TimestampRange()
        {
            Console.WriteLine("--- Seam 1.3: Timestamp 范围验证 ---");
            try
            {
                string fp = Path.Combine(testDigitDir, "Digit(2026_06_26_00).dat");
                byte[] raw = File.ReadAllBytes(fp);

                // 手动读取 ts_start 和 ts_end
                long tsStart = BitConverter.ToUInt32(raw, 12);
                long tsEnd = BitConverter.ToUInt32(raw, 16);
                Console.WriteLine("  ts_start={0}, ts_end={1}", tsStart, tsEnd);

                var parser = new DigitParser();
                var events = parser.Parse(raw, "test.dat");

                foreach (var e in events)
                {
                    Assert(e.Timestamp >= tsStart - 60,
                        string.Format("T={0} >= ts_start-60={1}", e.Timestamp, tsStart - 60));
                    Assert(e.Timestamp <= tsEnd + 60,
                        string.Format("T={0} <= ts_end+60={1}", e.Timestamp, tsEnd + 60));
                }

                Console.WriteLine("  [PASS] Seam 1.3");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>Seam 1.4: PointId 和 StateByte 提取正确</summary>
        static void TestDigitParser_PointIdAndStateByte()
        {
            Console.WriteLine("--- Seam 1.4: PointId/StateByte 提取验证 ---");
            try
            {
                string fp = Path.Combine(testDigitDir, "Digit(2026_06_26_00).dat");
                var parser = new DigitParser();
                var events = parser.Parse(File.ReadAllBytes(fp), "test.dat");

                // 验证 RawValue → PointId + StateByte 关系
                foreach (var e in events)
                {
                    int expectedPointId = e.RawValue & 0xFF;
                    int expectedStateByte = (e.RawValue >> 8) & 0xFF;
                    Assert(e.PointId == expectedPointId,
                        string.Format("PointId={0} == RawValue & 0xFF = {1}", e.PointId, expectedPointId));
                    Assert(e.StateByte == expectedStateByte,
                        string.Format("StateByte=0x{0:X2} == (RawValue>>8) & 0xFF = 0x{1:X2}",
                            e.StateByte, expectedStateByte));
                }

                // 验证存在不同 state_byte 值（0x00 和 0x2f 等常见值）
                var stateBytes = new HashSet<int>();
                foreach (var e in events) stateBytes.Add(e.StateByte);
                Console.WriteLine("  出现的 StateByte 值: {0}", string.Join(", ", stateBytes));
                Assert(stateBytes.Count >= 1, "至少存在 1 种不同的 state_byte");

                Console.WriteLine("  [PASS] Seam 1.4");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        /// <summary>Seam 1.5: 损坏/无效数据不崩溃</summary>
        static void TestDigitParser_InvalidInput()
        {
            Console.WriteLine("--- Seam 1.5: 无效输入处理 ---");
            try
            {
                var parser = new DigitParser();

                // null
                var r0 = parser.Parse(null, "null.dat");
                Assert(r0.Count == 0, "null 输入 → 0 个事件");

                // 空
                var r1 = parser.Parse(new byte[0], "empty.dat");
                Assert(r1.Count == 0, "空输入 → 0 个事件");
                Assert(parser.Errors.Count > 0, "记录错误日志");

                // 过小（< 24 字节 header）
                var r2 = parser.Parse(new byte[20], "small.dat");
                Assert(r2.Count == 0, "过小文件 → 0 个事件");

                // 全是零
                var r3 = parser.Parse(new byte[2000], "zeros.dat");
                Assert(r3.Count == 0, "全零文件 → 0 个事件（无有效记录）");

                Console.WriteLine("  [PASS] Seam 1.5");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Seam 2: DataRepository StatusEvents
        // ================================================================

        static string TempDb() => Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N") + ".db");
        static void CleanDb(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        static List<StatusEvent> MakeStatusEvents()
        {
            return new List<StatusEvent>
            {
                new StatusEvent { FileSource="Digit(0).dat", Timestamp=1782403200L, PointId=25, StateByte=0x00, RawValue=0x0019, SwitchId=null },
                new StatusEvent { FileSource="Digit(0).dat", Timestamp=1782403200L, PointId=26, StateByte=0x85, RawValue=0x851A, SwitchId=null },
                new StatusEvent { FileSource="Digit(0).dat", Timestamp=1782403202L, PointId=248, StateByte=0x00, RawValue=0x00F8, SwitchId=null },
                new StatusEvent { FileSource="Digit(0).dat", Timestamp=1782403202L, PointId=249, StateByte=0x48, RawValue=0x48F9, SwitchId=null },
            };
        }

        /// <summary>Seam 2.1: 构造 StatusEvents → 写入 SQLite → 读回验证</summary>
        static void TestRepo_SaveStatusEvents()
        {
            Console.WriteLine("--- Seam 2.1: StatusEvents 写入并读回验证 ---");
            string dbPath = TempDb();
            try
            {
                var factory = new DatabaseFactory(dbPath);
                var repo = new DataRepository(factory);
                var events = MakeStatusEvents();

                repo.SaveStatusEvents(events);

                // 验证计数
                int count = repo.GetStatusEventCount();
                Assert(count == 4, string.Format("StatusEvent 总数为 4，实际 {0}", count));

                // 通过 QueryService 读回验证
                var qs = new QueryService(dbPath);
                var loaded = qs.GetStatusEvents(1782403199L, 1782403205L);
                Assert(loaded.Count == 4, string.Format("时间范围查询返回 4 条，实际 {0}", loaded.Count));

                // 验证第一条
                var first = loaded[0];
                Assert(first.PointId == 25, string.Format("PointId={0}", first.PointId));
                Assert(first.StateByte == 0x00, string.Format("StateByte=0x{0:X2}", first.StateByte));
                Assert(first.Timestamp == 1782403200L, string.Format("Timestamp={0}", first.Timestamp));
                Assert(first.FileSource == "Digit(0).dat", "FileSource 正确");

                Console.WriteLine("  写入→读回: 所有字段一致");
                Console.WriteLine("  [PASS] Seam 2.1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            finally { CleanDb(dbPath); }
            Console.WriteLine();
        }

        /// <summary>Seam 2.2: 批量写入后记录数正确</summary>
        static void TestRepo_StatusEventCountCorrect()
        {
            Console.WriteLine("--- Seam 2.2: StatusEvent 批量计数 ---");
            string dbPath = TempDb();
            try
            {
                var factory = new DatabaseFactory(dbPath);
                var repo = new DataRepository(factory);
                int expected = 0;

                for (int i = 0; i < 5; i++)
                {
                    var batch = new List<StatusEvent>();
                    for (int j = 0; j < 10; j++)
                    {
                        batch.Add(new StatusEvent
                        {
                            FileSource = string.Format("Digit({0}).dat", i),
                            Timestamp = 1782403200L + i * 3600 + j,
                            PointId = j,
                            StateByte = (j % 2 == 0) ? 0x2F : 0x00,
                            RawValue = ((j % 2 == 0) ? 0x2F : 0x00) << 8 | j,
                        });
                        expected++;
                    }
                    repo.SaveStatusEvents(batch);
                }

                Assert(repo.GetStatusEventCount() == expected,
                    string.Format("StatusEvent 总数 {0} == 期望 {1}", repo.GetStatusEventCount(), expected));
                Console.WriteLine("  [PASS] Seam 2.2");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            finally { CleanDb(dbPath); }
            Console.WriteLine();
        }

        // ================================================================
        // Seam 3: 交叉验证（全部 84 个文件）
        // ================================================================

        /// <summary>Seam 3.1: 全部 84 个 Digit 文件解析 + 事件数交叉验证</summary>
        static void TestCrossVerify_AllFiles()
        {
            Console.WriteLine("--- Seam 3.1: 交叉验证（所有 84 个 Digit 文件） ---");
            try
            {
                var datFiles = Directory.GetFiles(testDigitDir, "Digit(*).dat");
                Assert(datFiles.Length == 84, string.Format("有 84 个 .dat 文件，实际 {0}", datFiles.Length));

                var parser = new DigitParser();
                int totalEvents = 0;
                int filesWithData = 0;
                int filesEmpty = 0;

                foreach (var datFile in datFiles)
                {
                    string fileName = Path.GetFileName(datFile);
                    byte[] data = File.ReadAllBytes(datFile);
                    var events = parser.Parse(data, fileName);

                    if (events.Count > 0)
                    {
                        filesWithData++;
                        totalEvents += events.Count;
                    }
                    else
                    {
                        filesEmpty++;
                    }
                }

                Console.WriteLine("  84 个文件全部解析完成");
                Console.WriteLine("  有数据文件: {0}, 空文件: {1}", filesWithData, filesEmpty);
                Console.WriteLine("  总事件数: {0}", totalEvents);

                // 预期 ~13,667（与 Python 解析器一致，容许 2% 差异）
                int expectedMin = 13300;
                int expectedMax = 14000;
                Assert(totalEvents >= expectedMin && totalEvents <= expectedMax,
                    string.Format("总事件数 {0} 在 [{1}, {2}] 范围内（预期 ~13667, ±2%）",
                        totalEvents, expectedMin, expectedMax));

                Console.WriteLine("  [PASS] Seam 3.1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Helpers
        // ================================================================

        static string FindDigitDataDir()
        {
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "shuju", "本地接收目录扳动")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "shuju", "本地接收目录扳动")),
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;
            throw new DirectoryNotFoundException("找不到 shuju/本地接收目录扳动/");
        }

        static void Assert(bool cond, string msg)
        {
            if (!cond) { Console.WriteLine("    ASSERT FAIL: {0}", msg); throw new Exception(msg); }
        }
    }
}
