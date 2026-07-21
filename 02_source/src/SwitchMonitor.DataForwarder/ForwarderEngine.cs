using System;
using SwitchMonitor.Storage;

namespace SwitchMonitor.DataForwarder
{
    /// <summary>
    /// DataForwarder 核心引擎 — 组装并管理所有子组件。
    /// </summary>
    public class ForwarderEngine : IDisposable
    {
        private readonly ForwarderConfig _config;
        private readonly StorageManager _storage;
        private readonly SyncStateManager _syncState;
        private readonly HttpServer _httpServer;
        private readonly PushEngine _pushEngine;
        private bool _started;
        private bool _disposed;

        public ForwarderEngine(ForwarderConfig config)
        {
            _config = config ?? throw new ArgumentNullException("config");

            // 初始化 SQLite（只读模式，避免与主进程锁冲突）
            _storage = new StorageManager(config.DbPath);

            // 初始化同步状态
            _syncState = SyncStateManager.Load(config.SyncStatePath);

            // 初始化 HTTP 服务器
            _httpServer = new HttpServer(config, _storage);

            // 初始化推送引擎
            _pushEngine = new PushEngine(config, _storage, _syncState);
        }

        /// <summary>启动所有服务（HTTP 监听 + 推送轮询）。</summary>
        public void Start()
        {
            if (_started)
                return;
            _started = true;

            _httpServer.Start();
            _pushEngine.Start(1000);
        }

        /// <summary>停止所有服务。</summary>
        public void Stop()
        {
            _httpServer.Stop();
            _pushEngine.Stop();
        }

        /// <summary>公开组件引用（测试用）。</summary>
        internal HttpServer HttpServer { get { return _httpServer; } }
        internal PushEngine PushEngine { get { return _pushEngine; } }
        internal SyncStateManager SyncState { get { return _syncState; } }
        internal StorageManager Storage { get { return _storage; } }
        internal ForwarderConfig Config { get { return _config; } }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
                if (_pushEngine != null) _pushEngine.Dispose();
                if (_httpServer != null) _httpServer.Dispose();
                if (_storage != null) _storage.Dispose();
            }
        }
    }
}
