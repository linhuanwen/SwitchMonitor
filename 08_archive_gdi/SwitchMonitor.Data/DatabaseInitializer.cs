using System;
using System.Collections.Generic;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 数据库初始化器——创建 SQLite 表和索引。
    /// 使用 NativeSqlite 直接操作，不依赖外部 ADO.NET 提供程序。
    /// </summary>
    public class DatabaseInitializer
    {
        // 所有建表 DDL
        public static readonly string[] AllTableDDL = new string[]
        {
            @"CREATE TABLE IF NOT EXISTS SwitchActions (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FileSource      TEXT NOT NULL,
                SwitchId        TEXT NOT NULL,
                StartTime       INTEGER NOT NULL,
                EndTime         INTEGER,
                Direction       TEXT,
                PhaseCount      INTEGER,
                SampleCount     INTEGER,
                SampleRate      INTEGER DEFAULT 25,
                CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
            );",

            @"CREATE TABLE IF NOT EXISTS CurveSamples (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ActionId        INTEGER NOT NULL REFERENCES SwitchActions(Id),
                SampleIndex     INTEGER NOT NULL,
                Timestamp       INTEGER NOT NULL,
                Phase           TEXT NOT NULL,
                Current         REAL,
                Voltage         REAL,
                Power           REAL,
                RawValue        REAL,
                UNIQUE(ActionId, SampleIndex, Phase)
            );",

            @"CREATE TABLE IF NOT EXISTS StatusEvents (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FileSource      TEXT NOT NULL,
                Timestamp       INTEGER NOT NULL,
                PointId         INTEGER NOT NULL,
                StateByte       INTEGER,
                RawValue        INTEGER,
                SwitchId        TEXT,
                CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
            );",

            @"CREATE TABLE IF NOT EXISTS ReferenceCurves (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                SwitchId        TEXT NOT NULL,
                ActionId        INTEGER REFERENCES SwitchActions(Id),
                SetTime         TEXT NOT NULL,
                Description     TEXT,
                IsActive        INTEGER DEFAULT 1
            );",

            @"CREATE TABLE IF NOT EXISTS DiagnosisLog (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ActionId        INTEGER REFERENCES SwitchActions(Id),
                RuleName        TEXT NOT NULL,
                Level           TEXT NOT NULL,
                Description     TEXT NOT NULL,
                AbnormalValue   REAL,
                ReferenceValue  REAL,
                CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
            );",

            @"CREATE TABLE IF NOT EXISTS ProcessedFiles (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                FilePath        TEXT NOT NULL UNIQUE,
                FileSize        INTEGER,
                LastProcessedTime TEXT,
                RowCount        INTEGER DEFAULT 0,
                Status          TEXT DEFAULT 'processed',
                ErrorMessage    TEXT,
                FileType        TEXT DEFAULT 'Unknown',
                CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
            );",
        };

        public static readonly string[] CreateIndexes = new string[]
        {
            "CREATE INDEX IF NOT EXISTS idx_actions_switch_time ON SwitchActions(SwitchId, StartTime);",
            "CREATE INDEX IF NOT EXISTS idx_samples_action ON CurveSamples(ActionId);",
            "CREATE INDEX IF NOT EXISTS idx_status_time ON StatusEvents(Timestamp);",
            "CREATE INDEX IF NOT EXISTS idx_status_point ON StatusEvents(PointId, Timestamp);",
        };

        /// <summary>在指定数据库上创建所有表和索引</summary>
        public void Initialize(NativeSqlite db)
        {
            if (db == null)
                throw new ArgumentNullException("db");

            foreach (var ddl in AllTableDDL)
            {
                db.Execute(ddl);
            }

            foreach (var indexDdl in CreateIndexes)
            {
                db.Execute(indexDdl);
            }
        }

        /// <summary>验证数据库结构完整性</summary>
        public string[] VerifySchema(NativeSqlite db)
        {
            if (db == null)
                throw new ArgumentNullException("db");

            var expectedTables = new string[]
            {
                "SwitchActions", "CurveSamples", "StatusEvents",
                "ReferenceCurves", "DiagnosisLog", "ProcessedFiles"
            };

            var rows = db.Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;", null);
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row.TryGetValue("name", out object nameObj) && nameObj != null)
                    existing.Add(nameObj.ToString());
            }

            var missing = new List<string>();
            foreach (var table in expectedTables)
            {
                if (!existing.Contains(table))
                    missing.Add(table);
            }

            return missing.ToArray();
        }
    }
}
