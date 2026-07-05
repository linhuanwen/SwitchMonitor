using System;
using System.Collections.Generic;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// ProcessedFiles 表的 CRUD 操作。
    /// 追踪哪些数据文件已经被处理过，避免重复解析。
    /// </summary>
    public class ProcessedFileRepository
    {
        private readonly DatabaseFactory _dbFactory;

        public ProcessedFileRepository(DatabaseFactory dbFactory)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException("dbFactory");
        }

        /// <summary>
        /// 检查文件是否已被成功处理过
        /// </summary>
        public bool IsFileProcessed(string filePath)
        {
            using (var db = _dbFactory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT Id FROM ProcessedFiles WHERE FilePath = ? AND Status = 'processed' LIMIT 1;",
                    new object[] { filePath });
                return rows.Count > 0;
            }
        }

        /// <summary>
        /// 获取文件的处理记录（如果存在）
        /// </summary>
        public ProcessedFileRecord GetRecord(string filePath)
        {
            using (var db = _dbFactory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT * FROM ProcessedFiles WHERE FilePath = ? LIMIT 1;",
                    new object[] { filePath });

                if (rows.Count == 0)
                    return null;

                return RowToRecord(rows[0]);
            }
        }

        /// <summary>
        /// 记录文件已成功处理
        /// </summary>
        public void MarkAsProcessed(string filePath, long fileSize, int rowCount, string fileType)
        {
            using (var db = _dbFactory.OpenDatabase())
            {
                var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                db.Execute(
                    @"INSERT OR REPLACE INTO ProcessedFiles (FilePath, FileSize, LastProcessedTime, RowCount, Status, ErrorMessage, FileType, CreatedAt)
                      VALUES (?, ?, ?, ?, 'processed', NULL, ?,
                              COALESCE((SELECT CreatedAt FROM ProcessedFiles WHERE FilePath = ?), datetime('now','localtime')));",
                    new object[] { filePath, fileSize, nowStr, rowCount, fileType, filePath });
            }
        }

        /// <summary>
        /// 记录文件处理失败
        /// </summary>
        public void MarkAsError(string filePath, long fileSize, string errorMessage, string fileType)
        {
            using (var db = _dbFactory.OpenDatabase())
            {
                var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                db.Execute(
                    @"INSERT OR REPLACE INTO ProcessedFiles (FilePath, FileSize, LastProcessedTime, RowCount, Status, ErrorMessage, FileType, CreatedAt)
                      VALUES (?, ?, ?, 0, 'error', ?, ?,
                              COALESCE((SELECT CreatedAt FROM ProcessedFiles WHERE FilePath = ?), datetime('now','localtime')));",
                    new object[] { filePath, fileSize, nowStr, errorMessage, fileType, filePath });
            }
        }

        /// <summary>
        /// 删除文件的处理记录（用于重新处理）
        /// </summary>
        public void RemoveRecord(string filePath)
        {
            using (var db = _dbFactory.OpenDatabase())
            {
                db.Execute(
                    "DELETE FROM ProcessedFiles WHERE FilePath = ?;",
                    new object[] { filePath });
            }
        }

        /// <summary>
        /// 获取所有已处理文件的路径集合
        /// </summary>
        public HashSet<string> GetAllProcessedPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var db = _dbFactory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT FilePath FROM ProcessedFiles WHERE Status = 'processed';",
                    null);

                foreach (var row in rows)
                {
                    if (row.TryGetValue("FilePath", out object pathObj) && pathObj != null)
                        paths.Add(pathObj.ToString());
                }
            }
            return paths;
        }

        /// <summary>
        /// 获取已成功处理的文件总数
        /// </summary>
        public int GetProcessedCount()
        {
            using (var db = _dbFactory.OpenDatabase())
            {
                var rows = db.Query(
                    "SELECT COUNT(*) AS cnt FROM ProcessedFiles WHERE Status = 'processed';",
                    null);
                if (rows.Count > 0 && rows[0].TryGetValue("cnt", out object cntObj) && cntObj != null)
                    return Convert.ToInt32(cntObj);
                return 0;
            }
        }

        private ProcessedFileRecord RowToRecord(Dictionary<string, object> row)
        {
            return new ProcessedFileRecord
            {
                Id = row.TryGetValue("Id", out object id) && id != null ? Convert.ToInt32(id) : 0,
                FilePath = GetString(row, "FilePath"),
                FileSize = row.TryGetValue("FileSize", out object fs) && fs != null ? Convert.ToInt64(fs) : 0,
                LastProcessedTime = GetString(row, "LastProcessedTime"),
                RowCount = row.TryGetValue("RowCount", out object rc) && rc != null ? Convert.ToInt32(rc) : 0,
                Status = GetString(row, "Status") ?? "processed",
                ErrorMessage = GetString(row, "ErrorMessage"),
                FileType = GetString(row, "FileType") ?? "Unknown",
                CreatedAt = GetString(row, "CreatedAt"),
            };
        }

        private static string GetString(Dictionary<string, object> row, string key)
        {
            if (row.TryGetValue(key, out object val) && val != null)
            {
                if (val is string s)
                    return s;
                return val.ToString();
            }
            return null;
        }
    }
}
