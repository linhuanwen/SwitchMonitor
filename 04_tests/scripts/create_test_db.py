#!/usr/bin/env python3
"""
创建测试用 SQLite 数据库。
用于 Slice 4 开发和测试——包含 SwitchActions 和 CurveSamples 表。
"""
import sqlite3
import os
import time

DB_PATH = os.path.join(os.path.dirname(__file__), '..', 'Data', 'switch_test.db')

def create_db():
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()

    # 创建表
    c.executescript("""
    CREATE TABLE IF NOT EXISTS SwitchActions (
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
    );

    CREATE TABLE IF NOT EXISTS CurveSamples (
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
    );

    CREATE INDEX IF NOT EXISTS idx_actions_switch_time ON SwitchActions(SwitchId, StartTime);
    CREATE INDEX IF NOT EXISTS idx_samples_action ON CurveSamples(ActionId);

    CREATE TABLE IF NOT EXISTS StatusEvents (
        Id              INTEGER PRIMARY KEY AUTOINCREMENT,
        FileSource      TEXT NOT NULL,
        Timestamp       INTEGER NOT NULL,
        PointId         INTEGER NOT NULL,
        StateByte       INTEGER,
        RawValue        INTEGER,
        SwitchId        TEXT,
        CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
    );

    CREATE TABLE IF NOT EXISTS ReferenceCurves (
        Id              INTEGER PRIMARY KEY AUTOINCREMENT,
        SwitchId        TEXT NOT NULL,
        ActionId        INTEGER REFERENCES SwitchActions(Id),
        SetTime         TEXT NOT NULL,
        Description     TEXT,
        IsActive        INTEGER DEFAULT 1
    );

    CREATE TABLE IF NOT EXISTS DiagnosisLog (
        Id              INTEGER PRIMARY KEY AUTOINCREMENT,
        ActionId        INTEGER REFERENCES SwitchActions(Id),
        RuleName        TEXT NOT NULL,
        Level           TEXT NOT NULL,
        Description     TEXT NOT NULL,
        AbnormalValue   REAL,
        ReferenceValue  REAL,
        CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
    );

    CREATE TABLE IF NOT EXISTS ProcessedFiles (
        Id              INTEGER PRIMARY KEY AUTOINCREMENT,
        FilePath        TEXT NOT NULL UNIQUE,
        FileSize        INTEGER,
        LastProcessedTime TEXT,
        RowCount        INTEGER DEFAULT 0,
        Status          TEXT DEFAULT 'processed',
        ErrorMessage    TEXT,
        CreatedAt       TEXT DEFAULT (datetime('now','localtime'))
    );
    """)

    # 插入测试道岔动作数据（模拟 sanshuibei 数据）
    base_time = 1700000000
    actions = [
        (1, "SwitchCurve(0).dat", "SW_01", base_time, base_time + 6, "定位→反位", 3, 150, 25),
        (2, "SwitchCurve(4).dat", "SW_02", base_time + 1000, base_time + 1007, "反位→定位", 3, 175, 25),
        (3, "SwitchCurve(8).dat", "SW_03", base_time + 2000, base_time + 2005, "定位→反位", 3, 125, 25),
    ]

    c.executemany(
        "INSERT INTO SwitchActions (Id, FileSource, SwitchId, StartTime, EndTime, Direction, PhaseCount, SampleCount, SampleRate) VALUES (?,?,?,?,?,?,?,?,?)",
        actions
    )

    # 插入曲线采样数据（模拟三相电流/电压/功率）
    import math

    sample_id = 0
    for action_id, switch_id, sample_count in [(1, "SW_01", 150), (2, "SW_02", 175), (3, "SW_03", 125)]:
        for i in range(sample_count):
            t = base_time + (action_id - 1) * 1000 + i * 40  # 25Hz = 40ms interval
            # 模拟道岔动作曲线：0-20% 解锁段，20-80% 转换段，80-100% 锁闭段
            progress = i / sample_count
            for phase in ["A", "B", "C"]:
                # 三相略有差异的电流曲线
                phase_offset = {"A": 0, "B": 0.1, "C": -0.05}[phase]
                if progress < 0.2:
                    # 解锁段：电流快速上升
                    current = 0.5 + progress / 0.2 * 2.0 + phase_offset + 0.1 * math.sin(i * 0.3)
                elif progress < 0.8:
                    # 转换段：电流稳定
                    current = 2.5 + phase_offset + 0.15 * math.sin(i * 0.2)
                else:
                    # 锁闭段：电流下降
                    current = 2.5 * (1.0 - (progress - 0.8) / 0.2) + phase_offset + 0.1 * math.sin(i * 0.25)

                current = max(0, current)
                voltage = 380.0 + phase_offset * 10 + 2 * math.sin(i * 0.1)
                power = current * voltage / 1000.0  # kW

                c.execute(
                    "INSERT INTO CurveSamples (ActionId, SampleIndex, Timestamp, Phase, Current, Voltage, Power) VALUES (?,?,?,?,?,?,?)",
                    (action_id, i, t, phase, round(current, 2), round(voltage, 1), round(power, 2))
                )
            sample_id += 1

    conn.commit()

    # 验证
    for table in ["SwitchActions", "CurveSamples", "StatusEvents", "ReferenceCurves", "DiagnosisLog", "ProcessedFiles"]:
        c.execute(f"SELECT COUNT(*) FROM {table}")
        count = c.fetchone()[0]
        print(f"  {table}: {count} rows")

    conn.close()
    print(f"\n数据库已创建: {DB_PATH}")
    print(f"大小: {os.path.getsize(DB_PATH):,} bytes")

if __name__ == "__main__":
    create_db()
