using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 精简的 SQLite P/Invoke 封装，基于项目自带的 sqlite3.dll。
    /// 提供执行 SQL 和查询的基本功能，支持参数化查询。
    /// </summary>
    public class NativeSqlite : IDisposable
    {
        private IntPtr _db;
        private bool _disposed;

        // SQLite result codes
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_INTEGER = 1;
        private const int SQLITE_FLOAT = 2;
        private const int SQLITE_TEXT = 3;
        private const int SQLITE_BLOB = 4;
        private const int SQLITE_NULL = 5;

        // SQLite text encoding
        private const int SQLITE_UTF8 = 1;
        private const int SQLITE_TRANSIENT = -1;

        #region P/Invoke declarations

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open(byte[] filename, out IntPtr ppDb);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_exec(IntPtr db, string sql, IntPtr callback, IntPtr arg, out IntPtr errMsg);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_prepare_v2(IntPtr db, string sql, int nByte, out IntPtr ppStmt, out IntPtr pzTail);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_int(IntPtr stmt, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr stmt, int iCol);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int(IntPtr stmt, int idx, int val);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long val);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_double(IntPtr stmt, int idx, double val);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_null(IntPtr stmt, int idx);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_text(IntPtr stmt, int idx, byte[] val, int n, IntPtr destructor);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_last_insert_rowid(IntPtr db);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_changes(IntPtr db);

        [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_free(IntPtr ptr);

        #endregion

        /// <summary>打开或创建 SQLite 数据库文件</summary>
        public void Open(string dbPath)
        {
            ThrowIfDisposed();
            if (_db != IntPtr.Zero)
                throw new InvalidOperationException("数据库已打开");

            byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
            int rc = sqlite3_open(pathBytes, out _db);
            if (rc != SQLITE_OK)
                throw new Exception(string.Format("无法打开数据库 {0}: {1}", dbPath, GetErrorMessage()));

            // WAL 模式提升写入性能（某些 SQLite 构建版本不支持 WAL，回退使用默认模式）
            try { Execute("PRAGMA journal_mode=WAL;"); } catch { /* WAL 不可用，使用默认模式 */ }
        }

        /// <summary>关闭数据库连接</summary>
        public void Close()
        {
            if (_db != IntPtr.Zero)
            {
                sqlite3_close(_db);
                _db = IntPtr.Zero;
            }
        }

        /// <summary>执行非查询 SQL（DDL 等，无参数）</summary>
        public void Execute(string sql)
        {
            ThrowIfDisposed();
            int rc = sqlite3_exec(_db, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr errMsg);
            if (rc != SQLITE_OK)
            {
                string err = errMsg != IntPtr.Zero ? Marshal.PtrToStringAnsi(errMsg) : "未知错误";
                sqlite3_free(errMsg);
                throw new Exception(string.Format("SQL 执行失败: {0}\nSQL: {1}", err, sql));
            }
        }

        /// <summary>执行参数化非查询 SQL（INSERT/UPDATE/DELETE）</summary>
        public void Execute(string sql, object[] parameters)
        {
            ThrowIfDisposed();
            IntPtr stmt = PrepareAndBind(sql, parameters);
            try
            {
                int rc = sqlite3_step(stmt);
                if (rc != SQLITE_DONE && rc != SQLITE_ROW)
                    throw new Exception(string.Format("SQL 执行失败: {0}", GetErrorMessage()));
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        /// <summary>获取最后插入的行 ID</summary>
        public long LastInsertRowId()
        {
            ThrowIfDisposed();
            return sqlite3_last_insert_rowid(_db);
        }

        /// <summary>获取受影响的行数</summary>
        public int Changes()
        {
            ThrowIfDisposed();
            return sqlite3_changes(_db);
        }

        /// <summary>执行参数化查询，返回对象数组列表（每行是一个 object[]）</summary>
        public List<object[]> QueryRows(string sql, object[] parameters)
        {
            ThrowIfDisposed();
            var results = new List<object[]>();
            IntPtr stmt = PrepareAndBind(sql, parameters);
            try
            {
                int colCount = sqlite3_column_count(stmt);

                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var row = new object[colCount];
                    for (int i = 0; i < colCount; i++)
                    {
                        row[i] = GetColumnValue(stmt, i);
                    }
                    results.Add(row);
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }

            return results;
        }

        /// <summary>执行查询，返回带列名的字典列表</summary>
        public List<Dictionary<string, object>> Query(string sql, object[] parameters)
        {
            ThrowIfDisposed();
            var results = new List<Dictionary<string, object>>();
            IntPtr stmt = PrepareAndBind(sql, parameters);
            try
            {
                int colCount = sqlite3_column_count(stmt);

                // 获取列名
                var colNames = new string[colCount];
                for (int i = 0; i < colCount; i++)
                {
                    IntPtr namePtr = sqlite3_column_name(stmt, i);
                    colNames[i] = namePtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(namePtr) : string.Format("col{0}", i);
                }

                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < colCount; i++)
                    {
                        row[colNames[i]] = GetColumnValue(stmt, i);
                    }
                    results.Add(row);
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }

            return results;
        }

        /// <summary>在事务中运行一组操作</summary>
        public void RunInTransaction(Action<NativeSqlite> action)
        {
            Execute("BEGIN TRANSACTION;");
            try
            {
                action(this);
                Execute("COMMIT;");
            }
            catch
            {
                try { Execute("ROLLBACK;"); } catch { /* 忽略 rollback 错误 */ }
                throw;
            }
        }

        #region Private helpers

        private IntPtr PrepareAndBind(string sql, object[] parameters)
        {
            int rc = sqlite3_prepare_v2(_db, sql, -1, out IntPtr stmt, out IntPtr pzTail);
            if (rc != SQLITE_OK)
                throw new Exception(string.Format("SQL 准备失败: {0}", GetErrorMessage()));

            if (parameters != null)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    int idx = i + 1; // SQLite 参数索引从 1 开始
                    BindValue(stmt, idx, parameters[i]);
                }
            }

            return stmt;
        }

        private void BindValue(IntPtr stmt, int idx, object value)
        {
            if (value == null || value == DBNull.Value)
            {
                sqlite3_bind_null(stmt, idx);
            }
            else if (value is int intVal)
            {
                sqlite3_bind_int(stmt, idx, intVal);
            }
            else if (value is long longVal)
            {
                sqlite3_bind_int64(stmt, idx, longVal);
            }
            else if (value is float floatVal)
            {
                sqlite3_bind_double(stmt, idx, (double)floatVal);
            }
            else if (value is double doubleVal)
            {
                sqlite3_bind_double(stmt, idx, doubleVal);
            }
            else if (value is string strVal)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(strVal);
                sqlite3_bind_text(stmt, idx, bytes, bytes.Length, (IntPtr)(-1)); // SQLITE_TRANSIENT
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value.ToString());
                sqlite3_bind_text(stmt, idx, bytes, bytes.Length, (IntPtr)(-1));
            }
        }

        private object GetColumnValue(IntPtr stmt, int col)
        {
            int colType = sqlite3_column_type(stmt, col);
            switch (colType)
            {
                case SQLITE_INTEGER:
                    return sqlite3_column_int64(stmt, col);
                case SQLITE_FLOAT:
                    return sqlite3_column_double(stmt, col);
                case SQLITE_TEXT:
                    IntPtr textPtr = sqlite3_column_text(stmt, col);
                    int bytes = sqlite3_column_bytes(stmt, col);
                    if (textPtr != IntPtr.Zero && bytes > 0)
                    {
                        byte[] buffer = new byte[bytes];
                        Marshal.Copy(textPtr, buffer, 0, bytes);
                        return Encoding.UTF8.GetString(buffer);
                    }
                    return string.Empty;
                case SQLITE_NULL:
                    return null;
                default:
                    return null;
            }
        }

        private string GetErrorMessage()
        {
            if (_db == IntPtr.Zero) return "数据库未打开";
            IntPtr ptr = sqlite3_errmsg(_db);
            return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : "未知错误";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("NativeSqlite");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
            }
        }

        #endregion
    }
}
