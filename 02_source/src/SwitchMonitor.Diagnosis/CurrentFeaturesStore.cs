using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SwitchMonitor.Data;

namespace SwitchMonitor.Diagnosis
{
    /// <summary>
    /// current_features.json 列式存储 POCO 与读写器。
    /// 格式: {"columns": ["timestamp","durationSec",...], "rows": [[...], ...]}
    /// 参照 FeaturesStore，列式格式体积为逐行对象格式的 1/3-1/4。
    /// </summary>
    public class CurrentFeaturesStore
    {
        public List<string> Columns = new List<string>();
        public List<List<double>> Rows = new List<List<double>>();

        /// <summary>默认列定义（26 列，含过滤元数据 + 方向）</summary>
        public static readonly List<string> DefaultColumns = new List<string>
        {
            "timestamp", "durationSec", "maxUnbalanceRatio",
            "spikePeakA", "spikeIndexA", "unlockMeanA", "convMeanA", "lockMeanA", "tailMeanA",
            "spikePeakB", "spikeIndexB", "unlockMeanB", "convMeanB", "lockMeanB", "tailMeanB",
            "spikePeakC", "spikeIndexC", "unlockMeanC", "convMeanC", "lockMeanC", "tailMeanC",
            "isValid", "isFullWindow", "sampleCount", "activeEnd", "direction"
        };

        /// <summary>
        /// 从 CurrentFeatures 和 timestamp 创建一行（按 DefaultColumns 顺序）
        /// </summary>
        public static List<double> RowFromCurrentFeatures(long timestamp, CurrentFeatures f)
        {
            return new List<double>
            {
                (double)timestamp,
                f.DurationSec,
                f.MaxUnbalanceRatio,
                f.SpikePeakA, (double)f.SpikeIndexA, f.UnlockMeanA, f.ConvMeanA, f.LockMeanA, f.TailMeanA,
                f.SpikePeakB, (double)f.SpikeIndexB, f.UnlockMeanB, f.ConvMeanB, f.LockMeanB, f.TailMeanB,
                f.SpikePeakC, (double)f.SpikeIndexC, f.UnlockMeanC, f.ConvMeanC, f.LockMeanC, f.TailMeanC,
                f.IsValid ? 1.0 : 0.0,
                f.IsFullWindow ? 1.0 : 0.0,
                (double)f.SampleCount,
                (double)f.ActiveEnd,
                FeaturesStore.EncodeDirection(f.Direction)
            };
        }

        /// <summary>
        /// 读取 current_features.json 文件
        /// </summary>
        public static CurrentFeaturesStore Load(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var store = serializer.Deserialize<CurrentFeaturesStore>(json);
                return store ?? new CurrentFeaturesStore();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存 current_features.json（全量覆盖）
        /// </summary>
        public static void Save(string parsedDir, string switchId, CurrentFeaturesStore store)
        {
            string dir = Path.Combine(parsedDir, switchId);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string filePath = Path.Combine(dir, "current_features.json");
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(store);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 追加一行到 current_features.json（增量写入）。
        /// 如果文件不存在则创建；已存在则读取后追加再写回。
        /// 即使 f.IsValid == false 也写入（保留审计痕迹）。
        /// </summary>
        public static void Append(string parsedDir, string switchId, long timestamp, CurrentFeatures f)
        {
            string dir = Path.Combine(parsedDir, switchId);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string filePath = Path.Combine(dir, "current_features.json");
            var serializer = new JavaScriptSerializer();

            CurrentFeaturesStore store;
            if (File.Exists(filePath))
            {
                store = Load(filePath);
                if (store == null || store.Columns == null || store.Columns.Count == 0)
                {
                    store = new CurrentFeaturesStore { Columns = new List<string>(DefaultColumns) };
                }
            }
            else
            {
                store = new CurrentFeaturesStore { Columns = new List<string>(DefaultColumns) };
            }

            if (store.Rows == null)
                store.Rows = new List<List<double>>();

            store.Rows.Add(RowFromCurrentFeatures(timestamp, f));

            string json = serializer.Serialize(store);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 使用指定 parsedDataDir 回填 current_features.json（从日JSON实时提取）。
        /// 返回回填行数。
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
                    var features = CurrentFeatureExtractor.Extract(evt);
                    // 所有行都写入（包括无效行，保留审计痕迹）
                    allRows.Add(RowFromCurrentFeatures(evt.Timestamp, features));
                }
            }

            // 按 timestamp 升序
            allRows.Sort((a, b) => a[0].CompareTo(b[0]));

            var store = new CurrentFeaturesStore
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
