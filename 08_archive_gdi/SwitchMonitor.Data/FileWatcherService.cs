using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using NLog;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 后台文件扫描服务。
    /// 使用 FileSystemWatcher 实时监控 + System.Timers.Timer 兜底扫描，
    /// 定时检查数据源目录，发现新的 .dat / .csv 文件后自动触发解析和入库。
    /// 维护 ProcessedFiles 表（SQLite）+ _processed.json（JSON 导出），避免重复处理。
    /// 错误和状态信息通过 NLog 写入日志文件（按日期滚动），同时通过事件通知 UI。
    /// </summary>
    public class FileWatcherService : IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

        private readonly DatabaseFactory _dbFactory;
        private readonly ProcessedFileRepository _repository;
        private Timer _timer;
        private Timer _debounceTimer;
        private FileSystemWatcher _watcher;
        private bool _isRunning;
        private bool _disposed;
        private DateTime? _lastScanTime;
        private readonly object _lock = new object();
        private readonly HashSet<string> _pendingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int PendingDebounceMs = 500;

        /// <summary>
        /// 创建文件扫描服务
        /// </summary>
        /// <param name="dbFactory">数据库工厂</param>
        public FileWatcherService(DatabaseFactory dbFactory)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException("dbFactory");
            _repository = new ProcessedFileRepository(dbFactory);

            ScanPaths = new List<string>();
            FilePattern = "*.dat;*.csv";
            ScanIntervalSeconds = 60;
            ParsedDataDir = null;
        }

        // ==================== 配置 ====================

        /// <summary>要扫描的目录路径列表</summary>
        public List<string> ScanPaths { get; set; }

        /// <summary>文件匹配模式（默认 "*.dat;*.csv"，分号分隔）</summary>
        public string FilePattern { get; set; }

        /// <summary>扫描间隔（秒），默认 60</summary>
        public int ScanIntervalSeconds { get; set; }

        /// <summary>中间 JSON 数据目录（_processed.json 输出位置）</summary>
        public string ParsedDataDir { get; set; }

        /// <summary>
        /// SwitchCurve 文件解析器。
        /// 输入文件路径，返回解析出的道岔动作数据列表。
        /// 由外部（Slice 2）注入。
        /// </summary>
        public Func<string, List<SwitchActionData>> SwitchCurveParser { get; set; }

        /// <summary>
        /// Digit 文件解析器。
        /// 输入文件路径，返回解析出的开关量事件列表。
        /// 由外部（Slice 2）注入。
        /// </summary>
        public Func<string, List<StatusEvent>> DigitParser { get; set; }

        // ==================== 事件 ====================

        /// <summary>发现并解析出新的道岔动作数据时触发</summary>
        public event Action<List<SwitchActionData>> OnNewSwitchActions;

        /// <summary>发现并解析出新的开关量事件时触发</summary>
        public event Action<List<StatusEvent>> OnNewStatusEvents;

        /// <summary>解析完成后触发，传递变更的 switchId 列表（去重）</summary>
        public event Action<List<string>> OnDataUpdated;

        /// <summary>FileSystemWatcher 检测到文件变化时触发（用于测试钩子）</summary>
        public event Action<string> OnFileDetected;

        /// <summary>扫描状态更新（如 "上次扫描: 2026-07-04 15:30 / 已处理 84 个文件"）</summary>
        public event Action<string> OnScanStatus;

        /// <summary>文件处理错误（不阻塞其他文件处理）</summary>
        public event Action<string, Exception> OnFileError;

        // ==================== 状态 ====================

        /// <summary>服务是否正在运行</summary>
        public bool IsRunning
        {
            get { lock (_lock) return _isRunning; }
        }

        /// <summary>FileSystemWatcher 是否已启用</summary>
        public bool IsFileSystemWatcherEnabled
        {
            get { return _watcher != null && _watcher.EnableRaisingEvents; }
        }

        /// <summary>上次扫描时间</summary>
        public DateTime? LastScanTime
        {
            get { lock (_lock) return _lastScanTime; }
        }

        /// <summary>已处理的文件总数</summary>
        public int ProcessedFileCount
        {
            get { return _repository.GetProcessedCount(); }
        }

        // ==================== 控制 ====================

        /// <summary>启动定时扫描 + FileSystemWatcher 监控</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning)
                    return;

                _isRunning = true;
            }

            Logger.Info("FileWatcherService 启动，扫描间隔: {0}秒, 扫描路径: {1}, 文件模式: {2}",
                ScanIntervalSeconds, string.Join("; ", ScanPaths ?? new List<string>()), FilePattern);

            // 启动时立即扫描一次（处理历史文件）
            try { ScanOnce(); }
            catch (Exception ex)
            {
                Logger.Error(ex, "初始扫描失败");
                FireFileError("初始扫描失败", ex);
            }

            // 启动 FileSystemWatcher 实时监控
            StartFileSystemWatcher();

            // 启动兜底定时器（System.Threading.Timer）
            int intervalMs = ScanIntervalSeconds * 1000;
            _timer = new Timer(
                callback: _ => OnTimerTick(),
                state: null,
                dueTime: intervalMs,
                period: intervalMs);

            FireScanStatus("文件扫描服务已启动（FileSystemWatcher + Timer 兜底）");
        }

        /// <summary>停止定时扫描和 FileSystemWatcher</summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;

                _isRunning = false;
            }

            StopFileSystemWatcher();

            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            Logger.Info("FileWatcherService 已停止");
            FireScanStatus("文件扫描服务已停止");
        }

        /// <summary>
        /// 手动执行一次扫描（用于测试和手动触发）。
        /// 扫描所有 ScanPaths 下匹配 FilePattern 的文件。
        /// </summary>
        public void ScanOnce()
        {
            var newActions = new List<SwitchActionData>();
            var newEvents = new List<StatusEvent>();
            var changedSwitchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int processedInThisScan = 0;

            string[] patterns = GetFilePatterns();

            foreach (string scanPath in ScanPaths)
            {
                if (!Directory.Exists(scanPath))
                {
                    FireFileError(string.Format("扫描目录不存在: {0}", scanPath), null);
                    continue;
                }

                var allFiles = new List<string>();
                foreach (string pattern in patterns)
                {
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(scanPath, pattern, SearchOption.TopDirectoryOnly);
                        allFiles.AddRange(files);
                    }
                    catch (Exception ex)
                    {
                        FireFileError(string.Format("无法列出目录文件: {0} (模式: {1})", scanPath, pattern), ex);
                    }
                }

                // 去重（同一文件可能匹配多个 pattern）
                var uniqueFiles = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (string filePath in uniqueFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileName(filePath);
                        string fileType = DetermineFileType(fileName);

                        // 跳过不相关的文件
                        if (fileType == "Unknown")
                            continue;

                        bool isCurrentHour = IsCurrentHourFile(filePath);
                        bool isInWriteWindow = IsWithinWriteWindow(filePath);

                        // CASE A: 当前小时文件 + 在写入窗口内 → 跳过（可能仍在写入）
                        if (isCurrentHour && isInWriteWindow)
                        {
                            Logger.Debug("跳过可能仍在写入的当前小时文件: {0}", fileName);
                            continue;
                        }

                        // CASE B: 当前小时文件 + 不在写入窗口 → 检查修改时间是否变化
                        if (isCurrentHour && !isInWriteWindow)
                        {
                            if (HasFileChangedSinceLastProcess(filePath))
                            {
                                // 文件自上次处理后已更新 → 重新处理
                                _repository.RemoveRecord(filePath);
                                Logger.Info("重新处理已更新的当前小时文件: {0}", fileName);
                            }
                            else if (_repository.IsFileProcessed(filePath))
                            {
                                // 文件未变化且已处理 → 跳过
                                Logger.Debug("跳过未变化的当前小时文件: {0}", fileName);
                                continue;
                            }
                            // 文件未变化但未处理过 → 继续处理
                        }

                        // CASE C: 非当前小时文件 → 正常去重
                        if (!isCurrentHour && _repository.IsFileProcessed(filePath))
                        {
                            Logger.Debug("跳过已处理文件: {0}", fileName);
                            continue;
                        }

                        // 解析文件
                        int rowCount = 0;
                        if ((fileType == "SwitchCurve" || fileType == "SwitchCurveCsv")
                            && SwitchCurveParser != null)
                        {
                            var actions = SwitchCurveParser(filePath);
                            if (actions != null)
                            {
                                newActions.AddRange(actions);
                                rowCount = actions.Count;
                                foreach (var a in actions)
                                {
                                    if (!string.IsNullOrEmpty(a.SwitchId))
                                        changedSwitchIds.Add(a.SwitchId);
                                }
                            }
                        }
                        else if ((fileType == "Digit" || fileType == "DigitCsv") && DigitParser != null)
                        {
                            var events = DigitParser(filePath);
                            if (events != null)
                            {
                                newEvents.AddRange(events);
                                rowCount = events.Count;
                                foreach (var e in events)
                                {
                                    if (!string.IsNullOrEmpty(e.SwitchId))
                                        changedSwitchIds.Add(e.SwitchId);
                                }
                            }
                        }

                        // 记录已处理
                        var fileInfo = new FileInfo(filePath);
                        _repository.MarkAsProcessed(filePath, fileInfo.Length, rowCount, fileType);
                        processedInThisScan++;
                        Logger.Info("文件处理完成: {0}, 类型={1}, 行数={2}, 大小={3}字节",
                            fileName, fileType, rowCount, fileInfo.Length);
                    }
                    catch (Exception ex)
                    {
                        // 错误隔离：单个文件失败不阻塞其他文件
                        string fileName = Path.GetFileName(filePath);
                        var fileInfo = new FileInfo(filePath);
                        string fileType = DetermineFileType(fileName);
                        _repository.MarkAsError(filePath, fileInfo.Length, ex.Message, fileType);

                        Logger.Error(ex, "处理文件失败: {0}, 类型={1}", filePath, fileType);
                        FireFileError(string.Format("处理文件失败: {0}", filePath), ex);
                    }
                }
            }

            // 更新状态
            lock (_lock)
            {
                _lastScanTime = DateTime.Now;
            }

            // 导出 _processed.json（如果配置了 ParsedDataDir）
            if (!string.IsNullOrEmpty(ParsedDataDir))
            {
                try
                {
                    ExportProcessedJson();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "导出 _processed.json 失败");
                }
            }

            // 触发事件
            if (newActions.Count > 0 && OnNewSwitchActions != null)
            {
                OnNewSwitchActions(newActions);
            }

            if (newEvents.Count > 0 && OnNewStatusEvents != null)
            {
                OnNewStatusEvents(newEvents);
            }

            if (changedSwitchIds.Count > 0 && OnDataUpdated != null)
            {
                OnDataUpdated(changedSwitchIds.ToList());
            }

            if (processedInThisScan > 0)
            {
                Logger.Info("扫描完成: 发现 {0} 个新文件, 共 {1} 条新数据, 变更道岔: {2}",
                    processedInThisScan, newActions.Count + newEvents.Count, changedSwitchIds.Count);
            }
            else
            {
                Logger.Debug("扫描完成: 无新文件 (总计已处理 {0} 个文件)", ProcessedFileCount);
            }

            FireScanStatus(string.Format("上次扫描: {0:yyyy-MM-dd HH:mm:ss} / 已处理 {1} 个文件",
                _lastScanTime, ProcessedFileCount));
        }

        /// <summary>
        /// 初始化 FileSystemWatcher（不启动定时器，用于测试）
        /// </summary>
        public void InitializeFileSystemWatcher()
        {
            StartFileSystemWatcher();
        }

        /// <summary>
        /// 模拟 FileSystemWatcher 文件创建事件（用于测试）
        /// </summary>
        public void SimulateFileCreated(string filePath)
        {
            OnFileWatcherCreated(this, new FileSystemEventArgs(WatcherChangeTypes.Created,
                Path.GetDirectoryName(filePath), Path.GetFileName(filePath)));
        }

        // ==================== 私有方法 ====================

        private void StartFileSystemWatcher()
        {
            foreach (string scanPath in ScanPaths)
            {
                if (!Directory.Exists(scanPath))
                    continue;

                try
                {
                    var watcher = new FileSystemWatcher(scanPath)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true,
                    };

                    // 对每种 pattern 设置过滤器
                    // FileSystemWatcher 只支持单个 pattern，用 "*.*" 然后在事件中过滤
                    watcher.Filter = "*.*";

                    watcher.Created += OnFileWatcherCreated;
                    watcher.Changed += OnFileWatcherChanged;

                    // 只保留一个 watcher（最后一个目录的）
                    if (_watcher != null)
                    {
                        _watcher.Dispose();
                    }
                    _watcher = watcher;

                    Logger.Info("FileSystemWatcher 已启动，监控目录: {0}", scanPath);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "FileSystemWatcher 启动失败 (目录: {0})，将仅使用 Timer 兜底", scanPath);
                }
            }
        }

        private void StopFileSystemWatcher()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFileWatcherCreated;
                _watcher.Changed -= OnFileWatcherChanged;
                _watcher.Dispose();
                _watcher = null;
                Logger.Info("FileSystemWatcher 已停止");
            }
        }

        private void OnFileWatcherCreated(object sender, FileSystemEventArgs e)
        {
            OnFileDetected?.Invoke(e.FullPath);

            // 只处理匹配的文件类型
            string fileType = DetermineFileType(e.Name);
            if (fileType == "Unknown")
                return;

            Logger.Info("FileSystemWatcher 检测到新文件: {0}", e.Name);

            // 防抖：收集待处理文件，延迟处理后批量扫描
            lock (_pendingFiles)
            {
                _pendingFiles.Add(e.FullPath);
            }

            // 延迟处理，避免文件还没写完就开始读
            SchedulePendingProcess();
        }

        private void OnFileWatcherChanged(object sender, FileSystemEventArgs e)
        {
            OnFileDetected?.Invoke(e.FullPath);

            string fileType = DetermineFileType(e.Name);
            if (fileType == "Unknown")
                return;

            Logger.Debug("FileSystemWatcher 检测到文件变化: {0}", e.Name);

            lock (_pendingFiles)
            {
                _pendingFiles.Add(e.FullPath);
            }

            SchedulePendingProcess();
        }

        private void SchedulePendingProcess()
        {
            // 释放之前的防抖定时器
            if (_debounceTimer != null)
            {
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }

            // 使用 Timer 延迟处理，防抖
            _debounceTimer = new Timer(
                callback: _ =>
                {
                    string[] files;
                    lock (_pendingFiles)
                    {
                        files = _pendingFiles.ToArray();
                        _pendingFiles.Clear();
                    }

                    if (files.Length > 0)
                    {
                        Logger.Debug("处理 {0} 个待处理文件", files.Length);
                        try { ScanOnce(); }
                        catch (Exception ex) { Logger.Error(ex, "延迟扫描异常"); }
                    }
                },
                state: null,
                dueTime: PendingDebounceMs,
                period: Timeout.Infinite);
        }

        private void OnTimerTick()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;
            }

            try
            {
                ScanOnce();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "定时扫描异常");
                FireFileError("定时扫描异常", ex);
            }
        }

        /// <summary>
        /// 根据文件名判断文件类型
        /// </summary>
        private string DetermineFileType(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "Unknown";

            string upper = fileName.ToUpperInvariant();

            // SwitchCurve(*).dat 二进制文件
            if (upper.StartsWith("SWITCHCURVE") && upper.EndsWith(".DAT"))
                return "SwitchCurve";

            // SwitchCurve(*).csv CSV 文件
            if (upper.StartsWith("SWITCHCURVE") && upper.EndsWith(".CSV"))
                return "SwitchCurveCsv";

            // Digit(*).dat 二进制文件
            if (upper.StartsWith("DIGIT") && upper.EndsWith(".DAT"))
                return "Digit";

            // Digit(*).csv CSV 文件
            if (upper.StartsWith("DIGIT") && upper.EndsWith(".CSV"))
                return "DigitCsv";

            return "Unknown";
        }

        /// <summary>
        /// 判断是否为当前小时文件（文件可能仍在写入中）
        /// </summary>
        private bool IsCurrentHourFile(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                // 如果文件修改时间在最近一小时内，视为"当前小时文件"
                double minutesSinceModified = (DateTime.Now - fileInfo.LastWriteTime).TotalMinutes;
                return minutesSinceModified < 60.0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断文件是否自上次处理后已修改
        /// </summary>
        private bool HasFileChangedSinceLastProcess(string filePath)
        {
            try
            {
                var record = _repository.GetRecord(filePath);
                if (record == null)
                    return true; // 从未处理过

                var fileInfo = new FileInfo(filePath);
                // 比较文件大小或修改时间
                if (fileInfo.Length != record.FileSize)
                    return true;

                // 比较处理时间与文件修改时间
                if (!string.IsNullOrEmpty(record.LastProcessedTime))
                {
                    if (DateTime.TryParse(record.LastProcessedTime, out DateTime lastProcTime))
                    {
                        // 文件修改时间晚于上次处理时间 → 已变更
                        return fileInfo.LastWriteTime > lastProcTime;
                    }
                }

                return false;
            }
            catch
            {
                return true; // 出问题时保守处理
            }
        }

        /// <summary>
        /// 判断文件是否在写入窗口内（修改时间距现在 < 扫描间隔 × 2）
        /// 在此窗口内的文件可能仍在被写入，应延迟处理。
        /// </summary>
        private bool IsWithinWriteWindow(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                double secondsSinceModified = (DateTime.Now - fileInfo.LastWriteTime).TotalSeconds;
                double threshold = ScanIntervalSeconds * 2;
                return secondsSinceModified < threshold;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 导出 _processed.json 文件到 ParsedDataDir
        /// </summary>
        private void ExportProcessedJson()
        {
            if (string.IsNullOrEmpty(ParsedDataDir))
                return;

            if (!Directory.Exists(ParsedDataDir))
                Directory.CreateDirectory(ParsedDataDir);

            string jsonPath = Path.Combine(ParsedDataDir, "_processed.json");

            // 获取所有已处理文件（含 processed 和 error 状态）的记录
            var processedPaths = _repository.GetAllProcessedPaths();
            var records = new List<ProcessedFileRecord>();

            foreach (string path in processedPaths)
            {
                var record = _repository.GetRecord(path);
                if (record != null)
                    records.Add(record);
            }

            string json = JsonConvert.SerializeObject(records, Formatting.Indented);
            File.WriteAllText(jsonPath, json, Encoding.UTF8);

            Logger.Debug("已导出 _processed.json: {0} 条记录", records.Count);
        }

        /// <summary>
        /// 解析 FilePattern（分号分隔的模式列表）
        /// </summary>
        private string[] GetFilePatterns()
        {
            if (string.IsNullOrEmpty(FilePattern))
                return new[] { "*.dat" };

            return FilePattern.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
        }

        private void FireScanStatus(string message)
        {
            try
            {
                OnScanStatus?.Invoke(message);
            }
            catch
            {
                // 事件处理器异常不应影响扫描服务
            }
        }

        private void FireFileError(string message, Exception ex)
        {
            try
            {
                OnFileError?.Invoke(message, ex);
            }
            catch
            {
                // 事件处理器异常不应影响扫描服务
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();

                if (_debounceTimer != null)
                {
                    _debounceTimer.Dispose();
                    _debounceTimer = null;
                }

                _disposed = true;
            }
        }
    }
}
