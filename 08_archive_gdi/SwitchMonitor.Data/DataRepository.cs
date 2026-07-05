using System;
using System.Collections.Generic;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 数据仓储——负责将解析后的道岔动作数据写入 SQLite 并读取。
    /// </summary>
    public class DataRepository
    {
        private readonly DatabaseFactory _factory;

        public DataRepository(DatabaseFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException("factory");
        }

        /// <summary>
        /// 保存单个 SwitchActionData 及其 CurveSamples 到数据库（在事务中）
        /// </summary>
        /// <returns>新插入的 ActionId</returns>
        public long SaveAction(SwitchActionData action)
        {
            using (var db = _factory.OpenDatabase())
            {
                long actionId = 0;
                db.RunInTransaction(txDb =>
                {
                    // INSERT SwitchActions
                    txDb.Execute(
                        @"INSERT INTO SwitchActions (FileSource, SwitchId, StartTime, EndTime, Direction, PhaseCount, SampleCount, SampleRate)
                          VALUES (?, ?, ?, ?, ?, ?, ?, ?);",
                        new object[]
                        {
                            action.FileSource,
                            action.SwitchId,
                            action.StartTime,
                            action.EndTime,
                            action.Direction,
                            action.PhaseCount,
                            action.SampleCount,
                            action.SampleRate
                        });

                    actionId = txDb.LastInsertRowId();

                    // INSERT CurveSamples 批量写入
                    if (action.Samples != null && action.Samples.Count > 0)
                    {
                        foreach (var sample in action.Samples)
                        {
                            txDb.Execute(
                                @"INSERT INTO CurveSamples (ActionId, SampleIndex, Timestamp, Phase, Current, Voltage, Power, RawValue)
                                  VALUES (?, ?, ?, ?, ?, ?, ?, ?);",
                                new object[]
                                {
                                    actionId,
                                    sample.Index,
                                    sample.Timestamp,
                                    sample.Phase,
                                    sample.Current,
                                    sample.Voltage,
                                    sample.Power,
                                    sample.RawValue
                                });
                        }
                    }
                });

                return actionId;
            }
        }

        /// <summary>
        /// 批量保存多个 SwitchActionData（在单一事务中）
        /// </summary>
        public void SaveActions(List<SwitchActionData> actions)
        {
            if (actions == null || actions.Count == 0)
                return;

            using (var db = _factory.OpenDatabase())
            {
                db.RunInTransaction(txDb =>
                {
                    foreach (var action in actions)
                    {
                        txDb.Execute(
                            @"INSERT INTO SwitchActions (FileSource, SwitchId, StartTime, EndTime, Direction, PhaseCount, SampleCount, SampleRate)
                              VALUES (?, ?, ?, ?, ?, ?, ?, ?);",
                            new object[]
                            {
                                action.FileSource,
                                action.SwitchId,
                                action.StartTime,
                                action.EndTime,
                                action.Direction,
                                action.PhaseCount,
                                action.SampleCount,
                                action.SampleRate
                            });

                        long actionId = txDb.LastInsertRowId();

                        if (action.Samples != null && action.Samples.Count > 0)
                        {
                            foreach (var sample in action.Samples)
                            {
                                txDb.Execute(
                                    @"INSERT INTO CurveSamples (ActionId, SampleIndex, Timestamp, Phase, Current, Voltage, Power, RawValue)
                                      VALUES (?, ?, ?, ?, ?, ?, ?, ?);",
                                    new object[]
                                    {
                                        actionId,
                                        sample.Index,
                                        sample.Timestamp,
                                        sample.Phase,
                                        sample.Current,
                                        sample.Voltage,
                                        sample.Power,
                                        sample.RawValue
                                    });
                            }
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 统计 SwitchActions 表中的记录数
        /// </summary>
        public int GetActionCount()
        {
            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query("SELECT COUNT(*) AS cnt FROM SwitchActions;", null);
                if (rows.Count > 0 && rows[0].TryGetValue("cnt", out object cnt))
                {
                    return Convert.ToInt32(cnt);
                }
                return 0;
            }
        }

        /// <summary>
        /// 统计 CurveSamples 表中的记录数
        /// </summary>
        public int GetSampleCount()
        {
            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query("SELECT COUNT(*) AS cnt FROM CurveSamples;", null);
                if (rows.Count > 0 && rows[0].TryGetValue("cnt", out object cnt))
                {
                    return Convert.ToInt32(cnt);
                }
                return 0;
            }
        }

        /// <summary>
        /// 获取所有 SwitchAction 记录（用于验证）
        /// </summary>
        public List<Dictionary<string, object>> GetAllActions()
        {
            using (var db = _factory.OpenDatabase())
            {
                return db.Query("SELECT * FROM SwitchActions ORDER BY StartTime;", null);
            }
        }

        // ================================================================
        // StatusEvents 操作
        // ================================================================

        /// <summary>
        /// 批量保存开关量状态事件（在单一事务中）
        /// </summary>
        public void SaveStatusEvents(List<StatusEvent> events)
        {
            if (events == null || events.Count == 0)
                return;

            using (var db = _factory.OpenDatabase())
            {
                db.RunInTransaction(txDb =>
                {
                    foreach (var e in events)
                    {
                        txDb.Execute(
                            @"INSERT INTO StatusEvents (FileSource, Timestamp, PointId, StateByte, RawValue, SwitchId)
                              VALUES (?, ?, ?, ?, ?, ?);",
                            new object[]
                            {
                                e.FileSource,
                                e.Timestamp,
                                e.PointId,
                                e.StateByte,
                                e.RawValue,
                                e.SwitchId
                            });
                    }
                });
            }
        }

        /// <summary>
        /// 统计 StatusEvents 表中的记录数
        /// </summary>
        public int GetStatusEventCount()
        {
            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query("SELECT COUNT(*) AS cnt FROM StatusEvents;", null);
                if (rows.Count > 0 && rows[0].TryGetValue("cnt", out object cnt))
                {
                    return Convert.ToInt32(cnt);
                }
                return 0;
            }
        }
    }
}
