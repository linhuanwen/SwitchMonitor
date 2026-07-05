using System;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// SQLite 数据库工厂。
    /// 负责创建 NativeSqlite 实例并确保表结构就绪。
    /// </summary>
    public class DatabaseFactory : IDisposable
    {
        private readonly string _dbPath;
        private readonly DatabaseInitializer _initializer;
        private bool _initialized;

        public DatabaseFactory(string databasePath)
        {
            if (string.IsNullOrEmpty(databasePath))
                throw new ArgumentNullException("databasePath");

            _dbPath = databasePath;
            _initializer = new DatabaseInitializer();
        }

        /// <summary>创建并打开数据库，自动初始化表结构</summary>
        public NativeSqlite OpenDatabase()
        {
            var db = new NativeSqlite();
            db.Open(_dbPath);

            if (!_initialized)
            {
                _initializer.Initialize(db);
                _initialized = true;
            }

            return db;
        }

        public string DatabasePath
        {
            get { return _dbPath; }
        }

        public void Dispose()
        {
            // 不持有连接，由调用方管理
        }
    }
}
