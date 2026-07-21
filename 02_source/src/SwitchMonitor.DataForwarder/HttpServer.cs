using System;
using System.Net;
using System.Threading;
using SwitchMonitor.Storage;

namespace SwitchMonitor.DataForwarder
{
    /// <summary>
    /// HTTP 服务器 — HttpListener 封装。
    /// 异步处理请求，分发到 ApiHandlers。
    /// </summary>
    public class HttpServer : IDisposable
    {
        private readonly ForwarderConfig _config;
        private readonly StorageManager _storage;
        private readonly HttpListener _listener;
        private readonly Thread _listenThread;
        private bool _running;
        private bool _disposed;

        public HttpServer(ForwarderConfig config, StorageManager storage)
        {
            _config = config ?? throw new ArgumentNullException("config");
            _storage = storage ?? throw new ArgumentNullException("storage");
            _listener = new HttpListener();
            _listenThread = new Thread(ListenLoop);
            _listenThread.IsBackground = true;
        }

        /// <summary>启动 HTTP 监听。</summary>
        public void Start()
        {
            string prefix = string.Format("http://+:{0}/", _config.ListenPort);

            try
            {
                _listener.Prefixes.Add(prefix);
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // 如果 + 通配符不可用（无管理员权限），尝试 localhost
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _config.ListenPort));
                _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", _config.ListenPort));
                _listener.Start();
            }

            _running = true;
            _listenThread.Start();
        }

        /// <summary>停止 HTTP 监听。</summary>
        public void Stop()
        {
            _running = false;
            try
            {
                _listener.Stop();
            }
            catch { }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var ctxResult = _listener.BeginGetContext(OnRequest, _listener);
                    // 等待异步操作完成（阻塞当前线程直到有请求到达或超时）
                    ctxResult.AsyncWaitHandle.WaitOne(1000);
                    // 等待句柄在超时后泄漏，直接等待下一个循环重建即可
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("HttpServer listen error: " + ex.Message);
                }
            }
        }

        private void OnRequest(IAsyncResult ar)
        {
            HttpListener listener = (HttpListener)ar.AsyncState;
            HttpListenerContext ctx = null;

            try
            {
                ctx = listener.EndGetContext(ar);
            }
            catch
            {
                return;
            }

            try
            {
                string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

                if (path == "/api/status" && ctx.Request.HttpMethod == "GET")
                {
                    ApiHandlers.HandleStatus(ctx, _storage, _config);
                }
                else if (path == "/api/events" && ctx.Request.HttpMethod == "GET")
                {
                    ApiHandlers.HandleEvents(ctx, _storage, _config);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("HttpServer request error: " + ex.Message);
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
                if (_listener != null)
                {
                    try { ((IDisposable)_listener).Dispose(); } catch { }
                }
            }
        }
    }
}
