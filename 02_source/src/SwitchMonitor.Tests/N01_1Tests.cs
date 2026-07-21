using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;
using SwitchMonitor.Storage;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// N01-1 SQLite 存储层测试套件。
    /// 测试 StorageManager 的 CRUD、BLOB 精度、查询性能、数据清理，
    /// 以及 DataMigrator 的 JSON → SQLite 迁移完整性。
    /// </summary>
    public static class N01_1Tests
    {
        private static string _tempDir;
        private static string _dbPath;
        private static StorageManager _storage;

        public static void Run()
        {
            Console.WriteLine("--- N01-1 SQLite Storage Layer Tests ---");
            Console.WriteLine();

            // 每个测试使用独立的临时数据库
            SetupTempDir();

            TestRunner.Test("T01: InsertEvent → GetEvent 往返（所有字段含 BLOB 一致）", T01_InsertGetRoundtrip);
            SetupTempDir();

            TestRunner.Test("T02: BLOB float32 精度不丢失", T02_BlobFloat32Precision);
            SetupTempDir();

            TestRunner.Test("T03: switch_id + timestamp 查询 < 15ms", T03_QueryPerformance);
            SetupTempDir();

            TestRunner.Test("T04: SaveDayData 批量存储与 LoadDayData 读取", T04_BulkSaveLoad);
            SetupTempDir();

            TestRunner.Test("T05: SaveDayDiagnosis / LoadDayDiagnosis 往返", T05_DiagnosisRoundtrip);
            SetupTempDir();

            TestRunner.Test("T06: DeleteEventsOlderThan + VACUUM", T06_DeleteAndVacuum);
            SetupTempDir();

            TestRunner.Test("T07: JSON → SQLite 迁移后数据完全一致", T07_MigrationConsistency);
            SetupTempDir();

            TestRunner.Test("T08: GetAllSwitchIds / GetDates / GetTimestamps 索引查询", T08_IndexQueries);
            SetupTempDir();

            TestRunner.Test("T09: 并发写入安全（多事件同时保存）", T09_ConcurrentWriteSafety);
            SetupTempDir();

            // 清理
            CleanupTempDir();
        }

        private static void SetupTempDir()
        {
            CleanupTempDir();
            _tempDir = TestRunner.TempDir();
            _dbPath = Path.Combine(_tempDir, "test_events.db");
            _storage = new StorageManager(_dbPath);
        }

        private static void CleanupTempDir()
        {
            if (_storage != null)
            {
                try { _storage.Dispose(); } catch { }
                _storage = null;
            }
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }

        // ── T01: InsertEvent → GetEvent 往返 ──

        private static void T01_InsertGetRoundtrip()
        {
            // 构造含所有字段的测试数据
            var samples = GenerateTestSamples(200, 0.04);
            var rec = new EventRecord
            {
                SwitchId = "1-J",
                Timestamp = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 15, 10, 30, 0)),
                Direction = "定位→反位",
                DurationSec = 7.84,
                SampleInterval = 0.04,
                SampleCount = 200,
                CurrentABlob = StorageManager.SerializeCurve(samples),
                CurrentBBlob = StorageManager.SerializeCurve(samples),
                CurrentCBlob = StorageManager.SerializeCurve(samples),
                PowerBlob = StorageManager.SerializeCurve(samples)
            };

            // 写入
            long id = _storage.InsertEvent(rec);

            // 读出
            EventRecord fetched = _storage.GetEvent(id);
            TestRunner.AssertNotNull(fetched, "GetEvent 返回 null");

            // 验证所有标量字段
            TestRunner.AssertEqual(rec.SwitchId, fetched.SwitchId, "SwitchId");
            TestRunner.AssertEqual(rec.Timestamp, fetched.Timestamp, "Timestamp");
            TestRunner.AssertEqual(rec.Direction, fetched.Direction, "Direction");
            TestRunner.AssertEqual(rec.DurationSec, fetched.DurationSec, 0.001, "DurationSec");
            TestRunner.AssertEqual(rec.SampleInterval, fetched.SampleInterval, 0.001, "SampleInterval");
            TestRunner.AssertEqual(rec.SampleCount, fetched.SampleCount, "SampleCount");

            // 验证 BLOB 往返（字节级一致）
            TestRunner.AssertTrue(BytesEqual(rec.CurrentABlob, fetched.CurrentABlob), "CurrentA BLOB 一致");
            TestRunner.AssertTrue(BytesEqual(rec.CurrentBBlob, fetched.CurrentBBlob), "CurrentB BLOB 一致");
            TestRunner.AssertTrue(BytesEqual(rec.CurrentCBlob, fetched.CurrentCBlob), "CurrentC BLOB 一致");
            TestRunner.AssertTrue(BytesEqual(rec.PowerBlob, fetched.PowerBlob), "Power BLOB 一致");

            // 验证反序列化后采样值一致
            var decoded = StorageManager.DeserializeCurve(fetched.CurrentABlob);
            TestRunner.AssertEqual(samples.Count, decoded.Count, "采样点数");
            for (int i = 0; i < samples.Count; i++)
            {
                TestRunner.AssertEqual(samples[i][0], decoded[i][0], 0.001, string.Format("采样{0} time", i));
                TestRunner.AssertEqual(samples[i][1], decoded[i][1], 0.001, string.Format("采样{0} value", i));
            }

            // 验证 DateTimeStr 派生正确
            TestRunner.AssertEqual("2024-06-15 10:30:00", fetched.DateTimeStr, "DateTimeStr");
        }

        // ── T02: BLOB float32 精度 ──

        private static void T02_BlobFloat32Precision()
        {
            // 构造高精度采样值，验证 float32 往返精度（~7位有效数字）
            var samples = new List<double[]>();
            for (int i = 0; i < 100; i++)
            {
                double t = i * 0.04;
                double v = 1.2345678 + Math.Sin(i * 0.1) * 0.5;
                samples.Add(new double[] { t, v });
            }

            byte[] blob = StorageManager.SerializeCurve(samples);
            var decoded = StorageManager.DeserializeCurve(blob);

            TestRunner.AssertEqual(samples.Count, decoded.Count, "采样点数");

            for (int i = 0; i < samples.Count; i++)
            {
                // float32 精度 ≈ 7 位有效数字
                TestRunner.AssertEqual(samples[i][0], decoded[i][0], 0.0001, string.Format("t[{0}]", i));
                TestRunner.AssertEqual(samples[i][1], decoded[i][1], 0.0001, string.Format("v[{0}]", i));
            }
        }

        // ── T03: 查询性能 ──

        private static void T03_QueryPerformance()
        {
            // 插入 500 条事件（覆盖多个 switch_id）
            var refRec = new EventRecord();
            for (int i = 0; i < 500; i++)
            {
                string swId = (i % 10 + 1) + "-J";
                long ts = StorageManager.ToUnixTimestamp(new DateTime(2024, 6, 15, 0, 0, 0).AddMinutes(i * 30));
                var samples = GenerateTestSamples(150, 0.04);

                var rec = new EventRecord
                {
                    SwitchId = swId,
                    Timestamp = ts,
                    Direction = "定位→反位",
                    DurationSec = 5.0 + (i % 10),
                    SampleInterval = 0.04,
                    SampleCount = 150,
                    CurrentABlob = StorageManager.SerializeCurve(samples)
                };
                _storage.InsertEvent(rec);

                if (i == 250)
                    refRec = rec; // 记录中间一条用于精确查找
            }

            // 预热：执行一次查询让 SQLite 缓存热起来
            _storage.GetEventBySwitchAndTimestamp(refRec.SwitchId, refRec.Timestamp);

            // 计时查询
            var sw = Stopwatch.StartNew();
            EventRecord fetched = _storage.GetEventBySwitchAndTimestamp(refRec.SwitchId, refRec.Timestamp);
            sw.Stop();

            TestRunner.AssertNotNull(fetched, "精确查找应找到记录");
            TestRunner.AssertEqual(refRec.Timestamp, fetched.Timestamp, "Timestamp 匹配");
            TestRunner.AssertTrue(sw.ElapsedMilliseconds < 15,
                string.Format("查询耗时 {0}ms < 15ms", sw.ElapsedMilliseconds));
        }

        // ── T04: 批量存储与读取 ──

        private static void T04_BulkSaveLoad()
        {
            // 构造一天的多个事件
            var events = new List<EventRecord>();
            var baseTime = new DateTime(2024, 6, 15, 6, 0, 0);
            for (int i = 0; i < 20; i++)
            {
                long ts = StorageManager.ToUnixTimestamp(baseTime.AddMinutes(i * 45));
                var samples = GenerateTestSamples(180, 0.04);
                events.Add(new EventRecord
                {
                    SwitchId = "3-J",
                    Timestamp = ts,
                    Direction = (i % 2 == 0) ? "定位→反位" : "反位→定位",
                    DurationSec = 6.0 + (i % 8) * 0.5,
                    SampleInterval = 0.04,
                    SampleCount = 180,
                    CurrentABlob = StorageManager.SerializeCurve(samples),
                    CurrentCBlob = StorageManager.SerializeCurve(samples)
                });
            }

            // 批量保存
            _storage.SaveDayData("3-J", "2024-06-15", events);

            // 读取
            var loaded = _storage.GetEventsByDate("3-J", "2024-06-15");
            TestRunner.AssertEqual(events.Count, loaded.Count, "事件数一致");

            // 验证按时间戳降序排列
            for (int i = 0; i < loaded.Count - 1; i++)
                TestRunner.AssertTrue(loaded[i].Timestamp >= loaded[i + 1].Timestamp,
                    string.Format("降序排列 [{0}]", i));

            // 验证每条数据可往返
            var firstLoaded = loaded[loaded.Count - 1]; // 最早的事件
            TestRunner.AssertTrue(firstLoaded.CurrentABlob != null && firstLoaded.CurrentABlob.Length > 0,
                "BLOB 数据非空");

            // 幂等覆盖：再次保存同一天数据
            var fewerEvents = new List<EventRecord> { events[0], events[1] };
            _storage.SaveDayData("3-J", "2024-06-15", fewerEvents);
            var reloaded = _storage.GetEventsByDate("3-J", "2024-06-15");
            TestRunner.AssertEqual(2, reloaded.Count, "覆盖后只有 2 条");
        }

        // ── T05: 诊断存储往返 ──

        private static void T05_DiagnosisRoundtrip()
        {
            // 先插入一条事件
            var samples = GenerateTestSamples(100, 0.04);
            var rec = new EventRecord
            {
                SwitchId = "2-J",
                Timestamp = StorageManager.ToUnixTimestamp(new DateTime(2024, 7, 1, 14, 30, 0)),
                Direction = "反位→定位",
                DurationSec = 8.2,
                SampleInterval = 0.04,
                SampleCount = 100,
                CurrentABlob = StorageManager.SerializeCurve(samples)
            };
            _storage.InsertEvent(rec);

            // 构造诊断 JSON
            var serializer = new JavaScriptSerializer();
            var diag = new EventDiagnosis
            {
                Timestamp = rec.Timestamp,
                Level = "预警",
                Results = new List<DiagnosisItem>
                {
                    new DiagnosisItem
                    {
                        RuleId = "R1",
                        RuleName = "动作超时",
                        Level = "预警",
                        Description = "动作时间 8.2s 超过阈值 7.0s",
                        Value = 8.2,
                        Reference = 7.0
                    }
                }
            };
            string diagJson = serializer.Serialize(diag);

            // 保存诊断
            _storage.SaveDiagnosis("2-J", rec.Timestamp, diagJson);

            // 读取诊断
            string fetchedJson = _storage.GetDiagnosisJson("2-J", rec.Timestamp);
            TestRunner.AssertNotNull(fetchedJson, "诊断 JSON 非空");

            EventDiagnosis fetched = serializer.Deserialize<EventDiagnosis>(fetchedJson);
            TestRunner.AssertEqual("预警", fetched.Level, "诊断级别");
            TestRunner.AssertEqual(1, fetched.Results.Count, "诊断结果数");
            TestRunner.AssertEqual("R1", fetched.Results[0].RuleId, "RuleId");

            // 批量诊断 SaveDayDiagnoses
            var diagRecs = new List<DiagnosisRecord>
            {
                new DiagnosisRecord { Timestamp = rec.Timestamp, Level = "预警", DiagJson = diagJson }
            };
            _storage.SaveDayDiagnoses("2-J", "2024-07-01", diagRecs);

            var jsons = _storage.GetDiagnosisJsonsByDate("2-J", "2024-07-01");
            TestRunner.AssertEqual(1, jsons.Count, "日期诊断数");
        }

        // ── T06: DeleteEventsOlderThan + VACUUM ──

        private static void T06_DeleteAndVacuum()
        {
            // 插入跨多天的事件
            var baseTime = new DateTime(2024, 1, 1, 12, 0, 0);
            for (int i = 0; i < 100; i++)
            {
                var rec = new EventRecord
                {
                    SwitchId = "5-J",
                    Timestamp = StorageManager.ToUnixTimestamp(baseTime.AddDays(i)),
                    Direction = "定位→反位",
                    DurationSec = 6.0,
                    SampleInterval = 0.04,
                    SampleCount = 50,
                    CurrentABlob = StorageManager.SerializeCurve(GenerateTestSamples(50, 0.04))
                };
                _storage.InsertEvent(rec);
            }

            TestRunner.AssertEqual(100, _storage.GetEventCount(), "初始 100 条");

            // 删除 2 月之前的数据（前31天）
            long sizeBefore = _storage.GetFileSize();
            int deleted = _storage.DeleteEventsOlderThan(new DateTime(2024, 2, 1));
            TestRunner.AssertTrue(deleted > 0, "删除了过期行");

            // 验证过期行不可查
            var remaining = _storage.GetEventsByDate("5-J", "2024-01-15");
            TestRunner.AssertEqual(0, remaining.Count, "2024-01-15 无数据");

            // 之后的数据仍在
            var after = _storage.GetEventsByDate("5-J", "2024-03-01");
            TestRunner.AssertEqual(1, after.Count, "2024-03-01 仍有数据");

            // VACUUM
            _storage.Vacuum();
            long sizeAfter = _storage.GetFileSize();
            TestRunner.AssertTrue(sizeAfter <= sizeBefore,
                string.Format("VACUUM 后文件缩小: {0} → {1}", sizeBefore, sizeAfter));
        }

        // ── T07: 迁移一致性 ──

        private static void T07_MigrationConsistency()
        {
            // 准备：创建模拟的 parsed_data JSON 目录结构
            string parsedDataDir = Path.Combine(_tempDir, "parsed_data");
            string switchDir = Path.Combine(parsedDataDir, "1-J");
            Directory.CreateDirectory(switchDir);

            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            // 创建测试事件
            var events = new List<SwitchEvent>();
            var baseTime = new DateTime(2024, 8, 1, 8, 0, 0);
            for (int i = 0; i < 15; i++)
            {
                var samples = GenerateTestSamples(120, 0.04);
                var evt = new SwitchEvent
                {
                    Timestamp = StorageManager.ToUnixTimestamp(baseTime.AddMinutes(i * 60)),
                    DateTimeStr = baseTime.AddMinutes(i * 60).ToString("yyyy-MM-dd HH:mm:ss"),
                    Direction = (i % 2 == 0) ? "定位→反位" : "反位→定位",
                    Duration = 7.0 + i * 0.2,
                    SampleInterval = 0.04,
                    SampleCount = 120,
                    CurrentA = samples,
                    CurrentB = samples,
                    Power = samples
                };
                events.Add(evt);
            }

            // 写入 JSON 模拟文件
            string jsonPath = Path.Combine(switchDir, "2024-08-01.json");
            File.WriteAllText(jsonPath, serializer.Serialize(events), Encoding.UTF8);

            // 创建诊断文件
            var diagnoses = new List<EventDiagnosis>();
            foreach (var evt in events)
            {
                if (evt.Duration > 8.0)
                {
                    diagnoses.Add(new EventDiagnosis
                    {
                        Timestamp = evt.Timestamp,
                        Level = "预警",
                        Results = new List<DiagnosisItem>
                        {
                            new DiagnosisItem
                            {
                                RuleId = "R1", RuleName = "动作超时",
                                Level = "预警", Description = "超时", Value = evt.Duration, Reference = 7.0
                            }
                        }
                    });
                }
            }
            string diagPath = Path.Combine(switchDir, "2024-08-01.diag.json");
            File.WriteAllText(diagPath, serializer.Serialize(diagnoses), Encoding.UTF8);

            // 执行迁移
            var migrator = new DataMigrator(_storage);
            MigrationResult result = migrator.Migrate(parsedDataDir);

            TestRunner.AssertTrue(result.Success, "迁移成功: " + result.Message);
            TestRunner.AssertEqual(events.Count, result.TotalEvents, "迁移事件数");
            TestRunner.AssertTrue(result.TotalDiagnoses > 0, "迁移了诊断数据");

            // 验证迁移后数据一致
            var loadedRecords = _storage.GetEventsByDate("1-J", "2024-08-01");
            TestRunner.AssertEqual(events.Count, loadedRecords.Count, "迁移后事件数一致");

            // 逐条比对
            for (int i = 0; i < events.Count; i++)
            {
                var original = events[i];
                // 查找对应记录
                EventRecord matched = null;
                foreach (var rec in loadedRecords)
                {
                    if (rec.Timestamp == original.Timestamp)
                    {
                        matched = rec;
                        break;
                    }
                }
                TestRunner.AssertNotNull(matched,
                    string.Format("找到 timestamp={0} 的记录", original.Timestamp));
                TestRunner.AssertEqual(original.Direction, matched.Direction, "Direction");
                TestRunner.AssertEqual(original.Duration, matched.DurationSec, 0.01, "Duration");
                TestRunner.AssertEqual(original.SampleCount, matched.SampleCount, "SampleCount");

                // 验证 BLOB 可正确反序列化
                var decodedA = StorageManager.DeserializeCurve(matched.CurrentABlob);
                TestRunner.AssertEqual(original.CurrentA.Count, decodedA.Count, "CurrentA 采样点数");
            }

            // 验证诊断迁移
            var diagJsons = _storage.GetDiagnosisJsonsByDate("1-J", "2024-08-01");
            TestRunner.AssertEqual(diagnoses.Count, diagJsons.Count, "诊断数一致");

            // 验证迁移标记
            TestRunner.AssertTrue(migrator.IsMigrated(), "迁移标记已创建");

            // 再次迁移应跳过
            MigrationResult result2 = migrator.Migrate(parsedDataDir);
            TestRunner.AssertTrue(result2.Skipped, "二次迁移跳过");
        }

        // ── T08: 索引查询 ──

        private static void T08_IndexQueries()
        {
            // 插入多个 switch 的跨日数据
            for (int sw = 1; sw <= 3; sw++)
            {
                string swId = sw + "-J";
                var baseTime = new DateTime(2024, 5, 1, 8, 0, 0);

                // 每天 3 条，跨 5 天（控制在同一天内，不跨午夜）
                for (int day = 0; day < 5; day++)
                {
                    for (int evt = 0; evt < 3; evt++)
                    {
                        long ts = StorageManager.ToUnixTimestamp(
                            baseTime.AddDays(day).AddHours(2 + evt * 6));
                        var samples = GenerateTestSamples(100, 0.04);
                        var rec = new EventRecord
                        {
                            SwitchId = swId,
                            Timestamp = ts,
                            Direction = "定位→反位",
                            DurationSec = 6.0,
                            SampleInterval = 0.04,
                            SampleCount = 100,
                            CurrentABlob = StorageManager.SerializeCurve(samples)
                        };
                        _storage.InsertEvent(rec);
                    }
                }
            }

            // GetAllSwitchIds
            var switchIds = _storage.GetAllSwitchIds();
            TestRunner.AssertEqual(3, switchIds.Count, "3 个 switch");

            // GetDates
            var dates = _storage.GetDates("1-J");
            TestRunner.AssertEqual(5, dates.Count, "5 天");

            // GetTimestamps
            var timestamps = _storage.GetTimestamps("2-J", "2024-05-03");
            TestRunner.AssertEqual(3, timestamps.Count, "每天 3 条");
            // 验证降序
            for (int i = 0; i < timestamps.Count - 1; i++)
                TestRunner.AssertTrue(timestamps[i] >= timestamps[i + 1], "降序");
        }

        // ── T09: 并发写入安全 ──

        private static void T09_ConcurrentWriteSafety()
        {
            // 同时写入多个 switch 的多天数据，验证不抛异常且数据完整
            var exceptions = new List<Exception>();

            Action<string, string, int> writeBatch = (swId, date, count) =>
            {
                try
                {
                    var events = new List<EventRecord>();
                    var baseTime = DateTime.ParseExact(date, "yyyy-MM-dd", null);
                    for (int i = 0; i < count; i++)
                    {
                        long ts = StorageManager.ToUnixTimestamp(baseTime.AddHours(2 + i * 4));
                        var samples = GenerateTestSamples(80, 0.04);
                        events.Add(new EventRecord
                        {
                            SwitchId = swId,
                            Timestamp = ts,
                            Direction = "定位→反位",
                            DurationSec = 5.0,
                            SampleInterval = 0.04,
                            SampleCount = 80,
                            CurrentABlob = StorageManager.SerializeCurve(samples)
                        });
                    }
                    _storage.SaveDayData(swId, date, events);
                }
                catch (Exception ex)
                {
                    lock (exceptions) { exceptions.Add(ex); }
                }
            };

            // 启动多个写入
            var actions = new List<Action>();
            for (int sw = 1; sw <= 5; sw++)
            {
                for (int day = 1; day <= 3; day++)
                {
                    string swId = sw + "-J";
                    string date = string.Format("2024-09-{0:D2}", day);
                    int count = 5;
                    actions.Add(() => writeBatch(swId, date, count));
                }
            }

            // 同步执行（确保 WriteLock 有效）
            foreach (var action in actions)
                action();

            // 验证：无异常
            TestRunner.AssertEqual(0, exceptions.Count,
                string.Format("并发写入异常数: {0}", exceptions.Count));

            // 验证：所有数据都写入成功
            int totalCount = _storage.GetEventCount();
            TestRunner.AssertEqual(75, totalCount,
                string.Format("预期 75 条（5 switch × 3 天 × 5 条）, 实际 {0}", totalCount));

            // 每个 switch 每天都有数据
            for (int sw = 1; sw <= 5; sw++)
            {
                for (int day = 1; day <= 3; day++)
                {
                    var loaded = _storage.GetEventsByDate(
                        sw + "-J",
                        string.Format("2024-09-{0:D2}", day));
                    TestRunner.AssertEqual(5, loaded.Count,
                        string.Format("{0}-J 2024-09-{1:D2} 有 5 条", sw, day));
                }
            }
        }

        // ── 辅助方法 ──

        /// <summary>
        /// 生成测试用采样数据 [[t, v], ...]
        /// </summary>
        private static List<double[]> GenerateTestSamples(int count, double interval)
        {
            var samples = new List<double[]>(count);
            for (int i = 0; i < count; i++)
            {
                double t = i * interval;
                double v = 1.5 + Math.Sin(i * 0.05) * 0.5 + (i % 20) * 0.02;
                samples.Add(new double[] { t, Math.Round(v, 6) });
            }
            return samples;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
