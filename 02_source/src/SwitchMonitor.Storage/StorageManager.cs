using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace SwitchMonitor.Storage
{
    /// <summary>
    /// SQLite 存储管理器 — 建表、读写、查询、清理。
    /// 使用 System.Data.SQLite ADO.NET 提供程序，WAL 模式。
    /// BLOB 列（电流/功率曲线）使用 BinaryWriter/BinaryReader + float32 编码。
    /// 不依赖 SwitchMonitor.Data，避免循环引用。
    /// </summary>
    public class StorageManager : IDisposable
    {
        private readonly string _connectionString;
        private readonly object _writeLock = new object();
        private bool _disposed;

        /// <summary>数据库文件路径</summary>
        public string DbPath { get; private set; }

        // ── 建表 DDL ──

        private const string CreateEventsTable = @"
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                switch_id TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                direction TEXT,
                duration_sec REAL,
                sample_interval REAL DEFAULT 0.04,
                sample_count INTEGER,
                current_a BLOB,
                current_b BLOB,
                current_c BLOB,
                power BLOB,
                diag_json TEXT,
                created_at TEXT DEFAULT (datetime('now','localtime'))
            );";

        private const string CreateIndexSwitchTime =
            "CREATE INDEX IF NOT EXISTS idx_events_switch_time ON events(switch_id, timestamp);";

        private const string CreateIndexTimestamp =
            "CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events(timestamp);";

        private const string CreateUniqueIndexSwitchTimestamp =
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_events_unique ON events(switch_id, timestamp);";

        // ── 构造函数 ──

        /// <summary>
        /// 创建 StorageManager 实例并初始化数据库。
        /// </summary>
        /// <param name="dbPath">SQLite 数据库文件路径</param>
        public StorageManager(string dbPath)
        {
            DbPath = dbPath ?? throw new ArgumentNullException("dbPath");

            // 确保目录存在
            string dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // WAL 模式：读写并发更好，适合工控机长期运行
            _connectionString = string.Format(
                "Data Source={0};Version=3;Journal Mode=WAL;Pooling=True;Max Pool Size=5;",
                dbPath);

            InitializeDatabase();
        }

        /// <summary>
        /// 创建表和索引
        /// </summary>
        private void InitializeDatabase()
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = CreateEventsTable;
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = CreateIndexSwitchTime;
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = CreateIndexTimestamp;
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = CreateUniqueIndexSwitchTimestamp;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ── BLOB 序列化（公开，供 Data 层使用） ──

        /// <summary>
        /// 将 List{double[]} 曲线数据编码为 float32 对的 BLOB。
        /// 每个采样点占用 8 字节：[time(float32), value(float32)]。
        /// </summary>
        public static byte[] SerializeCurve(List<double[]> samples)
        {
            if (samples == null || samples.Count == 0)
                return null;

            using (var ms = new MemoryStream(samples.Count * 8))
            using (var bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                foreach (var sample in samples)
                {
                    if (sample != null && sample.Length >= 2)
                    {
                        bw.Write((float)sample[0]); // time offset
                        bw.Write((float)sample[1]); // sample value
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// 从 BLOB 解码为 List{double[]} 曲线数据。
        /// </summary>
        public static List<double[]> DeserializeCurve(byte[] blob)
        {
            var result = new List<double[]>();
            if (blob == null || blob.Length == 0)
                return result;

            using (var ms = new MemoryStream(blob))
            using (var br = new BinaryReader(ms, Encoding.UTF8))
            {
                while (ms.Position + 8 <= ms.Length)
                {
                    float t = br.ReadSingle();
                    float v = br.ReadSingle();
                    result.Add(new double[] { Math.Round(t, 6), Math.Round(v, 6) });
                }
            }
            return result;
        }

        // ── 事件 CRUD ──

        /// <summary>
        /// 插入一条道岔动作事件，返回自增 ID。
        /// </summary>
        public long InsertEvent(EventRecord rec)
        {
            if (rec == null)
                throw new ArgumentNullException("rec");
            if (string.IsNullOrEmpty(rec.SwitchId))
                throw new ArgumentNullException("rec.SwitchId");

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT INTO events (switch_id, timestamp, direction, duration_sec,
                            sample_interval, sample_count, current_a, current_b, current_c, power)
                        VALUES (@switch_id, @timestamp, @direction, @duration_sec,
                            @sample_interval, @sample_count, @current_a, @current_b, @current_c, @power);
                        SELECT last_insert_rowid();";

                    cmd.Parameters.AddWithValue("@switch_id", rec.SwitchId);
                    cmd.Parameters.AddWithValue("@timestamp", rec.Timestamp);
                    cmd.Parameters.AddWithValue("@direction", (object)rec.Direction ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@duration_sec", rec.DurationSec);
                    cmd.Parameters.AddWithValue("@sample_interval", rec.SampleInterval);
                    cmd.Parameters.AddWithValue("@sample_count", rec.SampleCount);
                    AddBlobParam(cmd, "@current_a", rec.CurrentABlob);
                    AddBlobParam(cmd, "@current_b", rec.CurrentBBlob);
                    AddBlobParam(cmd, "@current_c", rec.CurrentCBlob);
                    AddBlobParam(cmd, "@power", rec.PowerBlob);

                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
        }

        /// <summary>
        /// 插入或忽略一条道岔动作事件（基于 switch_id + timestamp 唯一约束）。
        /// 重复数据返回 -1，成功返回自增 ID。
        /// 用于网络接收端防止重复写入（INSERT OR IGNORE）。
        /// </summary>
        public long InsertOrIgnoreEvent(EventRecord rec)
        {
            if (rec == null)
                throw new ArgumentNullException("rec");
            if (string.IsNullOrEmpty(rec.SwitchId))
                throw new ArgumentNullException("rec.SwitchId");

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        INSERT OR IGNORE INTO events (switch_id, timestamp, direction, duration_sec,
                            sample_interval, sample_count, current_a, current_b, current_c, power)
                        VALUES (@switch_id, @timestamp, @direction, @duration_sec,
                            @sample_interval, @sample_count, @current_a, @current_b, @current_c, @power);";

                    cmd.Parameters.AddWithValue("@switch_id", rec.SwitchId);
                    cmd.Parameters.AddWithValue("@timestamp", rec.Timestamp);
                    cmd.Parameters.AddWithValue("@direction", (object)rec.Direction ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@duration_sec", rec.DurationSec);
                    cmd.Parameters.AddWithValue("@sample_interval", rec.SampleInterval);
                    cmd.Parameters.AddWithValue("@sample_count", rec.SampleCount);
                    AddBlobParam(cmd, "@current_a", rec.CurrentABlob);
                    AddBlobParam(cmd, "@current_b", rec.CurrentBBlob);
                    AddBlobParam(cmd, "@current_c", rec.CurrentCBlob);
                    AddBlobParam(cmd, "@power", rec.PowerBlob);

                    int rowsAffected = cmd.ExecuteNonQuery();

                    if (rowsAffected > 0)
                    {
                        cmd.CommandText = "SELECT last_insert_rowid();";
                        return Convert.ToInt64(cmd.ExecuteScalar());
                    }
                    return -1;
                }
            }
        }

        /// <summary>
        /// 按主键 ID 获取事件记录。
        /// </summary>
        public EventRecord GetEvent(long id)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM events WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadRecordFromRow(reader);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 按 switch_id + timestamp 精确查找一条事件。
        /// 性能要求：&lt; 15ms（利用 idx_events_switch_time 索引）。
        /// </summary>
        public EventRecord GetEventBySwitchAndTimestamp(string switchId, long timestamp)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT * FROM events
                        WHERE switch_id = @switch_id AND timestamp = @timestamp
                        LIMIT 1;";
                    cmd.Parameters.AddWithValue("@switch_id", switchId);
                    cmd.Parameters.AddWithValue("@timestamp", timestamp);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return ReadRecordFromRow(reader);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 查询某转辙机在指定时间范围内的事件列表。
        /// </summary>
        public List<EventRecord> GetEventsBySwitch(string switchId, long? fromTimestamp = null, long? toTimestamp = null)
        {
            var result = new List<EventRecord>();
            var sql = new StringBuilder("SELECT * FROM events WHERE switch_id = @switch_id");

            if (fromTimestamp.HasValue)
                sql.Append(" AND timestamp >= @from_ts");
            if (toTimestamp.HasValue)
                sql.Append(" AND timestamp < @to_ts");
            sql.Append(" ORDER BY timestamp DESC;");

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql.ToString();
                    cmd.Parameters.AddWithValue("@switch_id", switchId);
                    if (fromTimestamp.HasValue)
                        cmd.Parameters.AddWithValue("@from_ts", fromTimestamp.Value);
                    if (toTimestamp.HasValue)
                        cmd.Parameters.AddWithValue("@to_ts", toTimestamp.Value);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Add(ReadRecordFromRow(reader));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 按日期查询某转辙机的所有事件。
        /// </summary>
        public List<EventRecord> GetEventsByDate(string switchId, string date)
        {
            DateTime dateStart;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out dateStart))
                return new List<EventRecord>();

            long startTs = ToUnixTimestamp(dateStart);
            long endTs = ToUnixTimestamp(dateStart.AddDays(1));

            return GetEventsBySwitch(switchId, startTs, endTs);
        }

        /// <summary>
        /// 批量保存某转辙机某天的所有事件（在事务中，先删后插）。
        /// </summary>
        public void SaveDayData(string switchId, string date, List<EventRecord> records)
        {
            if (string.IsNullOrEmpty(switchId))
                throw new ArgumentNullException("switchId");
            if (string.IsNullOrEmpty(date))
                throw new ArgumentNullException("date");

            DateTime dateStart;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out dateStart))
                throw new ArgumentException("日期格式无效，需为 yyyy-MM-dd: " + date);

            long startTs = ToUnixTimestamp(dateStart);
            long endTs = ToUnixTimestamp(dateStart.AddDays(1));

            // 写操作串行化，避免并发冲突
            lock (_writeLock)
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var txn = conn.BeginTransaction())
                    {
                        // 先删除该日已有数据，再批量插入（幂等覆盖）
                        using (var delCmd = conn.CreateCommand())
                        {
                            delCmd.Transaction = txn;
                            delCmd.CommandText = @"
                                DELETE FROM events
                                WHERE switch_id = @switch_id
                                  AND timestamp >= @start_ts AND timestamp < @end_ts;";
                            delCmd.Parameters.AddWithValue("@switch_id", switchId);
                            delCmd.Parameters.AddWithValue("@start_ts", startTs);
                            delCmd.Parameters.AddWithValue("@end_ts", endTs);
                            delCmd.ExecuteNonQuery();
                        }

                        // 逐条插入
                        if (records != null && records.Count > 0)
                        {
                            foreach (var rec in records)
                            {
                                InsertRecordInTransaction(conn, txn, rec);
                            }
                        }

                        txn.Commit();
                    }
                }
            }
        }

        /// <summary>
        /// 在已有事务中插入事件记录（内部方法）。
        /// </summary>
        private void InsertRecordInTransaction(SQLiteConnection conn, SQLiteTransaction txn, EventRecord rec)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = txn;
                cmd.CommandText = @"
                    INSERT INTO events (switch_id, timestamp, direction, duration_sec,
                        sample_interval, sample_count, current_a, current_b, current_c, power)
                    VALUES (@switch_id, @timestamp, @direction, @duration_sec,
                        @sample_interval, @sample_count, @current_a, @current_b, @current_c, @power);";

                cmd.Parameters.AddWithValue("@switch_id", rec.SwitchId);
                cmd.Parameters.AddWithValue("@timestamp", rec.Timestamp);
                cmd.Parameters.AddWithValue("@direction", (object)rec.Direction ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@duration_sec", rec.DurationSec);
                cmd.Parameters.AddWithValue("@sample_interval", rec.SampleInterval);
                cmd.Parameters.AddWithValue("@sample_count", rec.SampleCount);
                AddBlobParam(cmd, "@current_a", rec.CurrentABlob);
                AddBlobParam(cmd, "@current_b", rec.CurrentBBlob);
                AddBlobParam(cmd, "@current_c", rec.CurrentCBlob);
                AddBlobParam(cmd, "@power", rec.PowerBlob);

                cmd.ExecuteNonQuery();
            }
        }

        // ── 诊断存储 ──

        /// <summary>
        /// 更新某条事件的诊断结果（存入 diag_json 列）。
        /// </summary>
        public void SaveDiagnosis(string switchId, long timestamp, string diagJson)
        {
            if (string.IsNullOrEmpty(diagJson))
                return;

            lock (_writeLock)
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            UPDATE events SET diag_json = @diag_json
                            WHERE switch_id = @switch_id AND timestamp = @timestamp;";
                        cmd.Parameters.AddWithValue("@diag_json", diagJson);
                        cmd.Parameters.AddWithValue("@switch_id", switchId);
                        cmd.Parameters.AddWithValue("@timestamp", timestamp);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// 批量保存某天所有事件的诊断结果（多条 UPDATE 在事务中）。
        /// </summary>
        public void SaveDayDiagnoses(string switchId, string date, List<DiagnosisRecord> diagnoses)
        {
            if (diagnoses == null || diagnoses.Count == 0)
                return;

            lock (_writeLock)
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var txn = conn.BeginTransaction())
                    {
                        foreach (var diag in diagnoses)
                        {
                            if (string.IsNullOrEmpty(diag.DiagJson))
                                continue;

                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.Transaction = txn;
                                cmd.CommandText = @"
                                    UPDATE events SET diag_json = @diag_json
                                    WHERE switch_id = @switch_id AND timestamp = @timestamp;";
                                cmd.Parameters.AddWithValue("@diag_json", diag.DiagJson);
                                cmd.Parameters.AddWithValue("@switch_id", switchId);
                                cmd.Parameters.AddWithValue("@timestamp", diag.Timestamp);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        txn.Commit();
                    }
                }
            }
        }

        /// <summary>
        /// 读取某条事件的诊断 JSON。
        /// </summary>
        public string GetDiagnosisJson(string switchId, long timestamp)
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT diag_json FROM events
                        WHERE switch_id = @switch_id AND timestamp = @timestamp
                        AND diag_json IS NOT NULL LIMIT 1;";
                    cmd.Parameters.AddWithValue("@switch_id", switchId);
                    cmd.Parameters.AddWithValue("@timestamp", timestamp);

                    object result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        return result.ToString();
                }
            }
            return null;
        }

        /// <summary>
        /// 读取某天所有有诊断结果的事件诊断 JSON 列表。
        /// </summary>
        public List<string> GetDiagnosisJsonsByDate(string switchId, string date)
        {
            var result = new List<string>();

            DateTime dateStart;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out dateStart))
                return result;

            long startTs = ToUnixTimestamp(dateStart);
            long endTs = ToUnixTimestamp(dateStart.AddDays(1));

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT diag_json FROM events
                        WHERE switch_id = @switch_id
                          AND timestamp >= @start_ts AND timestamp < @end_ts
                          AND diag_json IS NOT NULL
                        ORDER BY timestamp;";
                    cmd.Parameters.AddWithValue("@switch_id", switchId);
                    cmd.Parameters.AddWithValue("@start_ts", startTs);
                    cmd.Parameters.AddWithValue("@end_ts", endTs);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            object val = reader["diag_json"];
                            if (val != null && val != DBNull.Value)
                                result.Add(val.ToString());
                        }
                    }
                }
            }
            return result;
        }

        // ── 索引查询 ──

        /// <summary>
        /// 获取所有已存储的转辙机 ID 列表。
        /// </summary>
        public List<string> GetAllSwitchIds()
        {
            var result = new List<string>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT switch_id FROM events ORDER BY switch_id;";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Add(reader["switch_id"].ToString());
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 获取某转辙机所有有数据的日期列表（降序）。
        /// </summary>
        public List<string> GetDates(string switchId)
        {
            var result = new List<string>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT DISTINCT timestamp FROM events
                        WHERE switch_id = @switch_id
                        ORDER BY timestamp DESC;";
                    cmd.Parameters.AddWithValue("@switch_id", switchId);

                    var seenDates = new HashSet<string>();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long ts = Convert.ToInt64(reader["timestamp"]);
                            string dt = UnixTimestampToDate(ts);
                            if (seenDates.Add(dt))
                                result.Add(dt);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 获取某转辙机某天的所有时间戳（降序）。
        /// </summary>
        public List<long> GetTimestamps(string switchId, string date)
        {
            var result = new List<long>();

            DateTime dateStart;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out dateStart))
                return result;

            long startTs = ToUnixTimestamp(dateStart);
            long endTs = ToUnixTimestamp(dateStart.AddDays(1));

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT timestamp FROM events
                        WHERE switch_id = @switch_id
                          AND timestamp >= @start_ts AND timestamp < @end_ts
                        ORDER BY timestamp DESC;";
                    cmd.Parameters.AddWithValue("@switch_id", switchId);
                    cmd.Parameters.AddWithValue("@start_ts", startTs);
                    cmd.Parameters.AddWithValue("@end_ts", endTs);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Add(Convert.ToInt64(reader["timestamp"]));
                    }
                }
            }
            return result;
        }

        // ── DataForwarder 查询 ──

        /// <summary>
        /// 获取所有转辙机在指定时间戳之后的事件，按时间升序排列。
        /// DataForwarder 轮询用。可选限制返回行数。
        /// </summary>
        /// <param name="sinceTimestamp">起始时间戳（不含）</param>
        /// <param name="limit">最大返回行数，null 不限</param>
        public List<EventRecord> GetEventsSince(long sinceTimestamp, int? limit = null)
        {
            var result = new List<EventRecord>();
            string sql = "SELECT * FROM events WHERE timestamp > @since ORDER BY timestamp ASC";
            if (limit.HasValue && limit.Value > 0)
                sql += " LIMIT " + limit.Value;
            sql += ";";

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@since", sinceTimestamp);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            result.Add(ReadRecordFromRow(reader));
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 获取数据库中最大的时间戳。DataForwarder 初始化同步状态用。
        /// 空库返回 0。
        /// </summary>
        public long GetMaxTimestamp()
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(MAX(timestamp), 0) FROM events;";
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
        }

        // ── 维护 ──

        /// <summary>
        /// 删除指定时间之前的所有事件。
        /// </summary>
        /// <returns>删除的行数</returns>
        public int DeleteEventsOlderThan(DateTime cutoff)
        {
            long cutoffTs = ToUnixTimestamp(cutoff);

            lock (_writeLock)
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM events WHERE timestamp < @cutoff_ts;";
                        cmd.Parameters.AddWithValue("@cutoff_ts", cutoffTs);
                        return cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// 执行 VACUUM 回收磁盘空间。
        /// </summary>
        public void Vacuum()
        {
            lock (_writeLock)
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "VACUUM;";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// 获取 events 表总行数（用于测试验证）。
        /// </summary>
        public int GetEventCount()
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM events;";
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        /// <summary>
        /// 获取数据库文件大小（字节）。
        /// </summary>
        public long GetFileSize()
        {
            if (File.Exists(DbPath))
                return new FileInfo(DbPath).Length;
            return 0;
        }

        // ── 内部辅助方法 ──

        /// <summary>
        /// 从 DataReader 当前行构建 EventRecord。
        /// </summary>
        private EventRecord ReadRecordFromRow(SQLiteDataReader reader)
        {
            var rec = new EventRecord
            {
                Id = Convert.ToInt64(reader["id"]),
                SwitchId = reader["switch_id"].ToString(),
                Timestamp = Convert.ToInt64(reader["timestamp"]),
                Direction = reader["direction"] as string ?? "未知",
                DurationSec = reader["duration_sec"] != DBNull.Value
                    ? Convert.ToDouble(reader["duration_sec"]) : 0.0,
                SampleInterval = reader["sample_interval"] != DBNull.Value
                    ? Convert.ToDouble(reader["sample_interval"]) : 0.04,
                SampleCount = reader["sample_count"] != DBNull.Value
                    ? Convert.ToInt32(reader["sample_count"]) : 0,
                DateTimeStr = UnixTimestampToDateTimeStr(Convert.ToInt64(reader["timestamp"])),
                CurrentABlob = reader["current_a"] as byte[],
                CurrentBBlob = reader["current_b"] as byte[],
                CurrentCBlob = reader["current_c"] as byte[],
                PowerBlob = reader["power"] as byte[],
                DiagJson = reader["diag_json"] != DBNull.Value ? reader["diag_json"].ToString() : null
            };

            // 读取 created_at
            object createdAt = reader["created_at"];
            if (createdAt != null && createdAt != DBNull.Value)
                rec.CreatedAt = createdAt.ToString();

            return rec;
        }

        private static void AddBlobParam(SQLiteCommand cmd, string name, byte[] value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.DbType = DbType.Binary;
            param.Value = (object)value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }

        /// <summary>
        /// DateTime → Unix 时间戳（秒）。
        /// </summary>
        public static long ToUnixTimestamp(DateTime dt)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(dt.ToUniversalTime() - epoch).TotalSeconds;
        }

        /// <summary>
        /// Unix 时间戳 → "yyyy-MM-dd" 日期字符串（本地时间）。
        /// </summary>
        public static string UnixTimestampToDate(long ts)
        {
            DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(ts).ToLocalTime();
            return dt.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// Unix 时间戳 → "yyyy-MM-dd HH:mm:ss" 字符串（本地时间）。
        /// </summary>
        public static string UnixTimestampToDateTimeStr(long ts)
        {
            DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(ts).ToLocalTime();
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // ── IDisposable ──

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                // System.Data.SQLite 连接池由 .NET 运行时管理
            }
        }
    }
}
