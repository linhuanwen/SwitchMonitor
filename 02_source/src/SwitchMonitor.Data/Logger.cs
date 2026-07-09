using System;
using System.IO;
using System.Text;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 操作日志记录器
    /// 记录用户操作和系统事件到日志文件，支持事后复盘
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logDir;

        /// <summary>
        /// 获取或设置日志目录（默认为应用程序目录下的 logs/）
        /// </summary>
        public static string LogDir
        {
            get
            {
                if (string.IsNullOrEmpty(_logDir))
                {
                    _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                }
                return _logDir;
            }
            set { _logDir = value; }
        }

        /// <summary>
        /// 获取今天的日志文件路径
        /// </summary>
        public static string TodayLogPath
        {
            get
            {
                return Path.Combine(LogDir, "SwitchMonitor_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            }
        }

        /// <summary>
        /// 获取日志目录下所有日志文件（按时间倒序）
        /// </summary>
        public static string[] GetLogFiles()
        {
            if (!Directory.Exists(LogDir))
                return new string[0];

            var files = Directory.GetFiles(LogDir, "SwitchMonitor_*.log");
            Array.Sort(files, (a, b) => b.CompareTo(a)); // 降序
            return files;
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public static void Info(string message)
        {
            WriteLog("INFO", message);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public static void Warning(string message)
        {
            WriteLog("WARN", message);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public static void Error(string message)
        {
            WriteLog("ERROR", message);
        }

        /// <summary>
        /// 记录错误日志（带异常信息）
        /// </summary>
        public static void Error(string message, Exception ex)
        {
            string fullMsg = message;
            if (ex != null)
            {
                fullMsg += " | 异常: " + ex.GetType().Name + ": " + ex.Message;
                if (ex.InnerException != null)
                    fullMsg += " | 内部异常: " + ex.InnerException.Message;
            }
            WriteLog("ERROR", fullMsg);
        }

        /// <summary>
        /// 记录诊断日志到 diag.log（单文件追加，不做轮转）。
        /// 与诊断计算同步完成，不产生额外 IO 开销。
        /// </summary>
        public static void LogDiagnosis(string text)
        {
            try
            {
                string dir = LogDir;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, text);

                string logPath = Path.Combine(dir, "diag.log");
                lock (_lock)
                {
                    File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志记录失败不应影响主程序运行
            }
        }

        /// <summary>
        /// 写入日志
        /// </summary>
        private static void WriteLog(string level, string message)
        {
            try
            {
                string dir = LogDir;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
                    DateTime.Now, level, message);

                lock (_lock)
                {
                    File.AppendAllText(TodayLogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志记录失败不应影响主程序运行，静默忽略
            }
        }
    }
}
