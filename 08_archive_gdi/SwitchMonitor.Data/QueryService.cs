using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 数据查询服务——为 UI 层提供类型化的数据访问。
    /// 基于 DatabaseFactory/NativeSqlite 基础设施。
    /// </summary>
    public class QueryService
    {
        private readonly DatabaseFactory _factory;

        // 参考曲线采样数据缓存：按 SwitchId 组织
        private readonly ConcurrentDictionary<string, List<CurveSampleRecord>> _referenceCache =
            new ConcurrentDictionary<string, List<CurveSampleRecord>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 创建查询服务
        /// </summary>
        /// <param name="dbPath">SQLite 数据库文件路径</param>
        public QueryService(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath))
                throw new ArgumentNullException(nameof(dbPath));
            _factory = new DatabaseFactory(dbPath);
        }

        /// <summary>
        /// 获取所有道岔动作记录，按时间倒序
        /// </summary>
        public List<SwitchActionRecord> GetAllActions()
        {
            var result = new List<SwitchActionRecord>();

            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT Id, FileSource, SwitchId, StartTime, EndTime, Direction, PhaseCount, SampleCount, SampleRate " +
                    "FROM SwitchActions ORDER BY StartTime DESC", null);

                foreach (var row in rows)
                {
                    result.Add(RowToActionRecord(row));
                }
            }

            return result;
        }

        /// <summary>
        /// 获取指定动作的全部曲线采样数据
        /// </summary>
        public List<CurveSampleRecord> GetCurveSamples(int actionId)
        {
            var result = new List<CurveSampleRecord>();

            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT Id, ActionId, SampleIndex, Timestamp, Phase, Current, Voltage, Power, RawValue " +
                    "FROM CurveSamples WHERE ActionId = ? ORDER BY Phase, SampleIndex",
                    new object[] { actionId });

                foreach (var row in rows)
                {
                    result.Add(new CurveSampleRecord
                    {
                        Id = ConvertDict<int>(row, "Id"),
                        ActionId = ConvertDict<int>(row, "ActionId"),
                        SampleIndex = ConvertDict<int>(row, "SampleIndex"),
                        Timestamp = ConvertDict<long>(row, "Timestamp"),
                        Phase = ConvertDict<string>(row, "Phase") ?? "",
                        Current = ConvertDict<float>(row, "Current"),
                        Voltage = ConvertDict<float>(row, "Voltage"),
                        Power = ConvertDict<float>(row, "Power"),
                        RawValue = ConvertDict<float>(row, "RawValue"),
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 获取数据库中所有不重复的道岔 ID 列表，按名称排序
        /// </summary>
        public List<string> GetDistinctSwitchIds()
        {
            var result = new List<string>();

            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT DISTINCT SwitchId FROM SwitchActions ORDER BY SwitchId", null);

                foreach (var row in rows)
                {
                    if (row.TryGetValue("SwitchId", out object val) && val != null)
                    {
                        string id = val.ToString();
                        if (!string.IsNullOrEmpty(id))
                            result.Add(id);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 按条件筛选道岔动作记录。
        /// 使用参数化 SQL 防止注入，DateTime 参数转为 Unix 时间戳进行整数比较。
        /// </summary>
        /// <param name="switchId">道岔 ID，为 null 时不过滤道岔</param>
        /// <param name="from">起始时间（含），为 null 时不过滤</param>
        /// <param name="to">结束时间（含），为 null 时不过滤</param>
        /// <param name="limit">最大返回条数，默认 500</param>
        /// <returns>按时间倒序排列的动作记录列表</returns>
        public List<SwitchActionRecord> GetActions(string switchId = null, DateTime? from = null, DateTime? to = null, int limit = 500)
        {
            var result = new List<SwitchActionRecord>();

            using (var db = _factory.OpenDatabase())
            {
                // 动态构建参数化 SQL
                var whereClauses = new List<string>();
                var parameters = new List<object>();

                if (!string.IsNullOrEmpty(switchId))
                {
                    whereClauses.Add("SwitchId = ?");
                    parameters.Add(switchId);
                }

                if (from.HasValue)
                {
                    whereClauses.Add("StartTime >= ?");
                    parameters.Add(DateTimeHelper.ToUnixTimestamp(from.Value));
                }

                if (to.HasValue)
                {
                    whereClauses.Add("StartTime <= ?");
                    parameters.Add(DateTimeHelper.ToUnixTimestamp(to.Value));
                }

                string sql = "SELECT Id, FileSource, SwitchId, StartTime, EndTime, Direction, PhaseCount, SampleCount, SampleRate " +
                             "FROM SwitchActions";

                if (whereClauses.Count > 0)
                {
                    sql += " WHERE " + string.Join(" AND ", whereClauses);
                }

                sql += " ORDER BY StartTime DESC LIMIT ?";
                parameters.Add(limit);

                var rows = db.Query(sql, parameters.ToArray());

                foreach (var row in rows)
                {
                    result.Add(RowToActionRecord(row));
                }
            }

            return result;
        }

        /// <summary>
        /// 获取动作总数
        /// </summary>
        public int GetActionCount()
        {
            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query("SELECT COUNT(*) AS cnt FROM SwitchActions", null);
                if (rows.Count > 0 && rows[0].TryGetValue("cnt", out object cnt) && cnt != null)
                    return Convert.ToInt32(cnt);
            }
            return 0;
        }

        /// <summary>
        /// 获取指定动作的各相采样点数（DISTINCT SampleIndex）
        /// </summary>
        public int GetSampleCount(int actionId)
        {
            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT COUNT(DISTINCT SampleIndex) AS cnt FROM CurveSamples WHERE ActionId = ?",
                    new object[] { actionId });
                if (rows.Count > 0 && rows[0].TryGetValue("cnt", out object cnt) && cnt != null)
                    return Convert.ToInt32(cnt);
            }
            return 0;
        }

        #region StatusEvents Methods

        /// <summary>
        /// 查询指定时间范围内的开关量状态事件
        /// </summary>
        /// <param name="startTime">起始 Unix 时间戳（含）</param>
        /// <param name="endTime">结束 Unix 时间戳（含）</param>
        /// <returns>按时间戳和点号排序的状态事件列表</returns>
        public List<StatusEvent> GetStatusEvents(long startTime, long endTime)
        {
            var result = new List<StatusEvent>();

            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    @"SELECT Id, FileSource, Timestamp, PointId, StateByte, RawValue, SwitchId
                      FROM StatusEvents
                      WHERE Timestamp >= ? AND Timestamp <= ?
                      ORDER BY Timestamp, PointId",
                    new object[] { startTime, endTime });

                foreach (var row in rows)
                {
                    result.Add(new StatusEvent
                    {
                        FileSource = ConvertDict<string>(row, "FileSource") ?? "",
                        Timestamp = ConvertDict<long>(row, "Timestamp"),
                        PointId = ConvertDict<int>(row, "PointId"),
                        StateByte = ConvertDict<int>(row, "StateByte"),
                        RawValue = ConvertDict<int>(row, "RawValue"),
                        SwitchId = ConvertDict<string>(row, "SwitchId"),
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 获取指定时间范围内出现过的所有点号（去重排序）
        /// </summary>
        public List<int> GetDistinctPointIds(long startTime, long endTime)
        {
            var result = new List<int>();

            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    @"SELECT DISTINCT PointId FROM StatusEvents
                      WHERE Timestamp >= ? AND Timestamp <= ?
                      ORDER BY PointId",
                    new object[] { startTime, endTime });

                foreach (var row in rows)
                {
                    if (row.TryGetValue("PointId", out object val) && val != null)
                    {
                        result.Add(Convert.ToInt32(val));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 统计 StatusEvents 表中的总记录数
        /// </summary>
        public int GetStatusEventCount()
        {
            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query("SELECT COUNT(*) AS cnt FROM StatusEvents", null);
                if (rows.Count > 0 && rows[0].TryGetValue("cnt", out object cnt) && cnt != null)
                    return Convert.ToInt32(cnt);
            }
            return 0;
        }

        #endregion

        #region Reference Curve Methods

        /// <summary>
        /// 为指定道岔设置活跃参考曲线。
        /// 自动将同一道岔的其他活跃参考曲线 IsActive 置为 0。
        /// </summary>
        /// <param name="switchId">道岔标识</param>
        /// <param name="actionId">来源动作 ID</param>
        /// <param name="description">用户备注（可选）</param>
        /// <returns>新插入的参考曲线记录 ID</returns>
        public long SetReferenceCurve(string switchId, int actionId, string description)
        {
            if (string.IsNullOrEmpty(switchId))
                throw new ArgumentNullException(nameof(switchId));
            if (actionId <= 0)
                throw new ArgumentException("actionId 必须大于 0", nameof(actionId));

            using (var db = _factory.OpenDatabase())
            {
                long newId = 0;
                db.RunInTransaction(txDb =>
                {
                    // 1. 将同一道岔的所有活跃参考曲线去激活
                    txDb.Execute(
                        "UPDATE ReferenceCurves SET IsActive = 0 WHERE SwitchId = ? AND IsActive = 1",
                        new object[] { switchId });

                    // 2. 插入新的参考曲线记录
                    string setTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    txDb.Execute(
                        @"INSERT INTO ReferenceCurves (SwitchId, ActionId, SetTime, Description, IsActive)
                          VALUES (?, ?, ?, ?, 1)",
                        new object[] { switchId, actionId, setTime, description ?? "" });

                    newId = txDb.LastInsertRowId();
                });

                // 刷新缓存：清除旧缓存，下次访问时重新加载
                _referenceCache.TryRemove(switchId, out _);

                return newId;
            }
        }

        /// <summary>
        /// 清除指定道岔的活跃参考曲线（IsActive 置为 0）。
        /// </summary>
        public void ClearReferenceCurve(string switchId)
        {
            if (string.IsNullOrEmpty(switchId))
                throw new ArgumentNullException(nameof(switchId));

            using (var db = _factory.OpenDatabase())
            {
                db.Execute(
                    "UPDATE ReferenceCurves SET IsActive = 0 WHERE SwitchId = ? AND IsActive = 1",
                    new object[] { switchId });
            }

            // 清除缓存
            _referenceCache.TryRemove(switchId, out _);
        }

        /// <summary>
        /// 获取指定道岔的活跃参考曲线记录。
        /// 如果没有活跃参考曲线，返回 null。
        /// </summary>
        public ReferenceCurveRecord GetActiveReferenceCurve(string switchId)
        {
            if (string.IsNullOrEmpty(switchId))
                throw new ArgumentNullException(nameof(switchId));

            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    @"SELECT r.Id, r.SwitchId, r.ActionId, r.SetTime, r.Description, r.IsActive,
                             a.StartTime AS SourceActionTime
                      FROM ReferenceCurves r
                      LEFT JOIN SwitchActions a ON r.ActionId = a.Id
                      WHERE r.SwitchId = ? AND r.IsActive = 1
                      ORDER BY r.Id DESC LIMIT 1",
                    new object[] { switchId });

                if (rows.Count == 0)
                    return null;

                return RowToReferenceCurveRecord(rows[0]);
            }
        }

        /// <summary>
        /// 获取所有参考曲线记录（包含活跃和已失效的），用于管理窗口。
        /// </summary>
        public List<ReferenceCurveRecord> GetAllReferenceCurves()
        {
            var result = new List<ReferenceCurveRecord>();

            using (var db = _factory.OpenDatabase())
            {
                var rows = db.Query(
                    @"SELECT r.Id, r.SwitchId, r.ActionId, r.SetTime, r.Description, r.IsActive,
                             a.StartTime AS SourceActionTime
                      FROM ReferenceCurves r
                      LEFT JOIN SwitchActions a ON r.ActionId = a.Id
                      ORDER BY r.SwitchId, r.SetTime DESC",
                    null);

                foreach (var row in rows)
                {
                    result.Add(RowToReferenceCurveRecord(row));
                }
            }

            return result;
        }

        /// <summary>
        /// 删除参考曲线（软删除：IsActive 置为 0）。
        /// </summary>
        public void DeleteReferenceCurve(long id)
        {
            if (id <= 0)
                throw new ArgumentException("id 必须大于 0", nameof(id));

            using (var db = _factory.OpenDatabase())
            {
                db.Execute(
                    "UPDATE ReferenceCurves SET IsActive = 0 WHERE Id = ?",
                    new object[] { id });
            }
        }

        /// <summary>
        /// 重新激活指定参考曲线。
        /// 自动将同道岔的其他活跃参考曲线去激活。
        /// </summary>
        public void ReactivateReferenceCurve(long id)
        {
            if (id <= 0)
                throw new ArgumentException("id 必须大于 0", nameof(id));

            using (var db = _factory.OpenDatabase())
            {
                db.RunInTransaction(txDb =>
                {
                    // 先获取该参考曲线的 SwitchId
                    var rows = txDb.Query(
                        "SELECT SwitchId FROM ReferenceCurves WHERE Id = ?",
                        new object[] { id });

                    if (rows.Count == 0)
                        throw new InvalidOperationException(string.Format("参考曲线 Id={0} 不存在", id));

                    string switchId = rows[0]["SwitchId"]?.ToString() ?? "";

                    // 去激活同道岔的其他活跃参考曲线
                    txDb.Execute(
                        "UPDATE ReferenceCurves SET IsActive = 0 WHERE SwitchId = ? AND IsActive = 1",
                        new object[] { switchId });

                    // 激活目标记录
                    txDb.Execute(
                        "UPDATE ReferenceCurves SET IsActive = 1 WHERE Id = ?",
                        new object[] { id });
                });
            }
        }

        /// <summary>
        /// 获取参考曲线的采样数据（带缓存）。
        /// 按 SwitchId 组织缓存，在设定/清除/删除时自动刷新。
        /// </summary>
        public List<CurveSampleRecord> GetCachedReferenceSamples(string switchId)
        {
            if (string.IsNullOrEmpty(switchId))
                return null;

            // 先检查缓存
            if (_referenceCache.TryGetValue(switchId, out var cached))
                return cached;

            // 获取活跃参考曲线
            var activeRef = GetActiveReferenceCurve(switchId);
            if (activeRef == null)
            {
                // 缓存空结果以避免重复查询
                _referenceCache[switchId] = null;
                return null;
            }

            // 加载采样数据
            var samples = GetCurveSamples(activeRef.ActionId);
            _referenceCache[switchId] = samples;
            return samples;
        }

        /// <summary>
        /// 清除指定道岔的参考曲线缓存。
        /// </summary>
        public void InvalidateReferenceCache(string switchId)
        {
            _referenceCache.TryRemove(switchId, out _);
        }

        #endregion

        #region Helpers

        private static SwitchActionRecord RowToActionRecord(Dictionary<string, object> row)
        {
            return new SwitchActionRecord
            {
                Id = ConvertDict<int>(row, "Id"),
                FileSource = ConvertDict<string>(row, "FileSource") ?? "",
                SwitchId = ConvertDict<string>(row, "SwitchId") ?? "",
                StartTime = ConvertDict<long>(row, "StartTime"),
                EndTime = ConvertDict<long>(row, "EndTime"),
                Direction = ConvertDict<string>(row, "Direction") ?? "",
                PhaseCount = ConvertDict<int>(row, "PhaseCount"),
                SampleCount = ConvertDict<int>(row, "SampleCount"),
                SampleRate = ConvertDict<int>(row, "SampleRate"),
            };
        }

        private static T ConvertDict<T>(Dictionary<string, object> row, string key)
        {
            if (row.TryGetValue(key, out object val) && val != null)
            {
                if (val is T tVal) return tVal;
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return default(T); }
            }
            return default(T);
        }

        private static ReferenceCurveRecord RowToReferenceCurveRecord(Dictionary<string, object> row)
        {
            return new ReferenceCurveRecord
            {
                Id = ConvertDict<long>(row, "Id"),
                SwitchId = ConvertDict<string>(row, "SwitchId") ?? "",
                ActionId = ConvertDict<int>(row, "ActionId"),
                SetTime = ConvertDict<string>(row, "SetTime") ?? "",
                Description = ConvertDict<string>(row, "Description") ?? "",
                IsActive = ConvertDict<long>(row, "IsActive") != 0,
                SourceActionTime = ConvertDict<long>(row, "SourceActionTime"),
            };
        }

        #endregion
    }
}
