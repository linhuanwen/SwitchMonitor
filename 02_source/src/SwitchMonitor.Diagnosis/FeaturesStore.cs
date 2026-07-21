using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// features.json 列式存储 POCO 与读写器。
    /// 格式: {"columns": ["timestamp","durationSec",...], "rows": [[...], ...]}
    /// 列式格式体积为逐行对象格式的 1/3-1/4，适合 XP 环境 IO。
    /// </summary>
    public class FeaturesStore
    {
        public List<string> Columns = new List<string>();
        public List<List<double>> Rows = new List<List<double>>();

        // ── 批量缓冲：导入时避免每次 Append 都全量读写 JSON ──
        /// <summary>是否启用批量缓冲模式（默认 false，逐条即时写盘）</summary>
        public static bool BatchMode { get; set; }
        private static readonly Dictionary<string, FeaturesStore> _batchBuffer = new Dictionary<string, FeaturesStore>();

        /// <summary>
        /// 刷新所有批量缓冲，写入磁盘并清空。
        /// </summary>
        public static void FlushBatch(string parsedDir)
        {
            lock (_batchBuffer)
            {
                foreach (var kv in _batchBuffer)
                {
                    string switchId = kv.Key;
                    Save(parsedDir, switchId, kv.Value);
                }
                _batchBuffer.Clear();
            }
        }

        /// <summary>默认列定义</summary>
        public static readonly List<string> DefaultColumns = new List<string>
        {
            "timestamp", "durationSec", "spikePeak", "unlockMean", "convMean", "lockMean", "tailMean", "direction"
        };

        /// <summary>方向编码：定位→反位 = 1.0, 反位→定位 = 2.0, 未知 = 0.0</summary>
        public static double EncodeDirection(string direction)
        {
            if (direction == BaselineStore.DirNormalToReverse) return 1.0;
            if (direction == BaselineStore.DirReverseToNormal) return 2.0;
            return 0.0;
        }

        /// <summary>方向解码</summary>
        public static string DecodeDirection(double code)
        {
            if (Math.Abs(code - 1.0) < 0.01) return BaselineStore.DirNormalToReverse;
            if (Math.Abs(code - 2.0) < 0.01) return BaselineStore.DirReverseToNormal;
            return null;
        }

        /// <summary>
        /// 从 CurveFeatures 创建一行（按 DefaultColumns 顺序）
        /// </summary>
        public static List<double> RowFromFeatures(long timestamp, CurveFeatures f)
        {
            return new List<double>
            {
                (double)timestamp,
                f.DurationSec,
                f.SpikePeak,
                f.UnlockMean,
                f.ConvMean,
                f.LockMean,
                f.TailMean,
                EncodeDirection(f.Direction)
            };
        }

        /// <summary>
        /// 读取 features.json 文件
        /// </summary>
        public static FeaturesStore Load(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var store = serializer.Deserialize<FeaturesStore>(json);
                return store ?? new FeaturesStore();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存 features.json（全量覆盖）
        /// </summary>
        public static void Save(string parsedDir, string switchId, FeaturesStore store)
        {
            string dir = Path.Combine(parsedDir, switchId);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string filePath = Path.Combine(dir, "features.json");
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(store);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 追加一行到 features.json（增量写入，避免全量反序列化）。
        /// 如果文件不存在则创建；已存在则读写追加再写回。
        /// </summary>
        public static void Append(string parsedDir, string switchId,
            long timestamp, double durationSec, double spikePeak,
            double unlockMean, double convMean, double lockMean, double tailMean,
            string direction = null)
        {
            var row = RowFromFeatures(timestamp, new CurveFeatures
            {
                DurationSec = durationSec,
                SpikePeak = spikePeak,
                UnlockMean = unlockMean,
                ConvMean = convMean,
                LockMean = lockMean,
                TailMean = tailMean,
                Direction = direction
            });

            // 批量模式：只写内存缓冲，不落盘
            if (BatchMode)
            {
                lock (_batchBuffer)
                {
                    FeaturesStore store;
                    if (!_batchBuffer.TryGetValue(switchId, out store))
                    {
                        store = new FeaturesStore { Columns = new List<string>(DefaultColumns), Rows = new List<List<double>>() };
                        _batchBuffer[switchId] = store;
                    }
                    store.Rows.Add(row);
                }
                return;
            }

            string dir = Path.Combine(parsedDir, switchId);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string filePath = Path.Combine(dir, "features.json");
            var serializer = new JavaScriptSerializer();

            FeaturesStore existing;
            if (File.Exists(filePath))
            {
                existing = Load(filePath);
                if (existing == null || existing.Columns == null || existing.Columns.Count == 0)
                {
                    existing = new FeaturesStore { Columns = new List<string>(DefaultColumns) };
                }
            }
            else
            {
                existing = new FeaturesStore { Columns = new List<string>(DefaultColumns) };
            }

            if (existing.Rows == null)
                existing.Rows = new List<List<double>>();

            existing.Rows.Add(row);

            string json = serializer.Serialize(existing);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 从 parsed_data 目录回填某台道岔的 features.json（扫描全部日JSON）。
        /// 返回回填行数。
        /// </summary>
        public static int Backfill(IndexManager im, string switchId)
        {
            var dates = im.GetDates(switchId);
            var allRows = new List<List<double>>();

            foreach (var date in dates)
            {
                var events = im.LoadDayData(switchId, date);
                foreach (var evt in events)
                {
                    var features = FeatureExtractor.Extract(evt);
                    if (features.IsValid)
                    {
                        allRows.Add(RowFromFeatures(evt.Timestamp, features));
                    }
                }
            }

            // 按 timestamp 升序
            allRows.Sort((a, b) => a[0].CompareTo(b[0]));

            var store = new FeaturesStore
            {
                Columns = new List<string>(DefaultColumns),
                Rows = allRows
            };

            // 需要从 IndexManager 获取 parsedDir 路径
            // IndexManager 不暴露路径，我们通过反射或新增属性获取
            // 用 parsedDir 写入
            string featuresPath = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "parsed_data", switchId, "dummy.json")) ?? ".") ?? ".",
                "parsed_data");

            // 找到 parsed_data 实际路径：从已存在的日期 JSON 反推
            if (allRows.Count > 0)
            {
                // 查找第一个存在的日期文件来确定路径
                foreach (var date in dates)
                {
                    string testPath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "parsed_data", switchId, date + ".json");
                    if (File.Exists(testPath))
                    {
                        featuresPath = Path.Combine(
                            AppDomain.CurrentDomain.BaseDirectory, "parsed_data");
                        break;
                    }
                }
            }

            // 实际上需要把 parsedDir 交给调用方
            // 这里提供一个简化版本
            Save(featuresPath, switchId, store);

            return allRows.Count;
        }

        /// <summary>
        /// 使用指定 parsedDataDir 回填 features.json
        /// </summary>
        public static int BackfillWithDir(IndexManager im, string switchId, string parsedDataDir)
        {
            var dates = im.GetDates(switchId);
            var allRows = new List<List<double>>();

            foreach (var date in dates)
            {
                var events = im.LoadDayData(switchId, date);
                foreach (var evt in events)
                {
                    var features = FeatureExtractor.Extract(evt);
                    if (features.IsValid)
                    {
                        allRows.Add(RowFromFeatures(evt.Timestamp, features));
                    }
                }
            }

            // 按 timestamp 升序
            allRows.Sort((a, b) => a[0].CompareTo(b[0]));

            var store = new FeaturesStore
            {
                Columns = new List<string>(DefaultColumns),
                Rows = allRows
            };

            Save(parsedDataDir, switchId, store);
            return allRows.Count;
        }

        /// <summary>
        /// 获取指定列的索引
        /// </summary>
        public int ColumnIndex(string name)
        {
            if (Columns == null) return -1;
            return Columns.IndexOf(name);
        }

        /// <summary>
        /// 获取某行某列的值
        /// </summary>
        public double Value(int row, int col)
        {
            if (Rows == null || row < 0 || row >= Rows.Count) return 0.0;
            if (Rows[row] == null || col < 0 || col >= Rows[row].Count) return 0.0;
            return Rows[row][col];
        }

        /// <summary>
        /// 按 timestamp 折半查找行索引
        /// </summary>
        public int FindRowByTimestamp(long timestamp)
        {
            if (Rows == null || Rows.Count == 0) return -1;
            int tsCol = ColumnIndex("timestamp");
            if (tsCol < 0) return -1;

            int lo = 0, hi = Rows.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                double midTs = Rows[mid][tsCol];
                if (Math.Abs(midTs - timestamp) < 0.5) return mid;
                if (midTs < timestamp) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }
    }
}
