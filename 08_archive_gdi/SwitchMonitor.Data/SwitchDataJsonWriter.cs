using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SwitchMonitor.Common;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 开关动作事件 JSON 输出格式
    /// </summary>
    public class SwitchEventJson
    {
        [JsonProperty("timestamp")]
        public long Timestamp { get; set; }

        [JsonProperty("datetime")]
        public string Datetime { get; set; }

        [JsonProperty("direction")]
        public string Direction { get; set; }

        [JsonProperty("duration")]
        public double Duration { get; set; }

        [JsonProperty("sampleInterval")]
        public double SampleInterval { get; set; }

        [JsonProperty("sampleCount")]
        public int SampleCount { get; set; }

        [JsonProperty("currentA", NullValueHandling = NullValueHandling.Ignore)]
        public List<double> CurrentA { get; set; }

        [JsonProperty("currentB", NullValueHandling = NullValueHandling.Ignore)]
        public List<double> CurrentB { get; set; }

        [JsonProperty("currentC", NullValueHandling = NullValueHandling.Ignore)]
        public List<double> CurrentC { get; set; }

        [JsonProperty("power", NullValueHandling = NullValueHandling.Ignore)]
        public List<double> Power { get; set; }
    }

    /// <summary>
    /// 将 CSM2010 CSV 数据解析、配对、分组后写入 JSON 中间文件。
    /// </summary>
    public class SwitchDataJsonWriter
    {
        private const double SAMPLE_INTERVAL = 0.04; // 1/25 Hz
        private const int DECIMAL_PLACES = 3;

        /// <summary>解析过程中遇到的警告和错误</summary>
        public List<string> Errors { get; private set; }

        private readonly MappingConfig _mappingConfig;

        public SwitchDataJsonWriter(MappingConfig mappingConfig)
        {
            _mappingConfig = mappingConfig ?? MappingConfig.CreateDefault();
            Errors = new List<string>();
        }

        /// <summary>
        /// 处理沙水北目录下的所有 CSV 文件对。
        /// </summary>
        /// <param name="dataDir">数据源目录（如 shuju/sanshuibei/）</param>
        /// <param name="outputDir">输出目录（如 parsed_data/）</param>
        public void ProcessAll(string dataDir, string outputDir)
        {
            Errors.Clear();

            if (!Directory.Exists(dataDir))
            {
                Errors.Add("数据目录不存在: " + dataDir);
                return;
            }

            // 查找所有 CSV 文件
            var csvFiles = Directory.GetFiles(dataDir, "SwitchCurve(*).csv");
            if (csvFiles.Length == 0)
            {
                Errors.Add("在 " + dataDir + " 中未找到 SwitchCurve CSV 文件");
                return;
            }

            // 构建文件对
            var pairs = BuildFilePairs(csvFiles);
            if (pairs.Count == 0)
            {
                Errors.Add("无法构建文件对，请检查文件命名");
                return;
            }

            // 处理每个文件对
            foreach (var pair in pairs)
            {
                string baseName = Path.GetFileNameWithoutExtension(pair.Item1);
                int idx = ExtractFileIndex(baseName);

                // 用 mapping 获取 SwitchId
                string fileKey = Path.GetFileNameWithoutExtension(pair.Item1); // "SwitchCurve(0)"
                string switchId = _mappingConfig.GetSwitchId(fileKey);
                if (switchId == fileKey)
                {
                    // 未映射时使用数字索引
                    switchId = idx.ToString();
                }

                try
                {
                    var events = ProcessPair(pair.Item1, pair.Item2, switchId);
                    WriteDateFiles(events, outputDir, switchId);
                }
                catch (Exception ex)
                {
                    Errors.Add(string.Format("处理 {0} 失败: {1}", fileKey, ex.Message));
                }
            }

            // 更新索引
            UpdateIndex(outputDir);
        }

        /// <summary>
        /// 处理一对电流+功率 CSV 文件，返回合并后的事件列表。
        /// </summary>
        /// <param name="currentFilePath">电流 CSV 文件路径（偶数索引）</param>
        /// <param name="powerFilePath">功率 CSV 文件路径（奇数索引）</param>
        /// <param name="switchId">道岔标识</param>
        /// <returns>合并后的事件列表</returns>
        public List<SwitchEventJson> ProcessPair(string currentFilePath, string powerFilePath, string switchId)
        {
            Errors.Clear();
            var events = new List<SwitchEventJson>();

            var parser = new CsvCurveParser();

            // 解析电流文件
            var currentGroups = new Dictionary<long, List<CsvRow>>();
            if (!string.IsNullOrEmpty(currentFilePath) && File.Exists(currentFilePath))
            {
                currentGroups = parser.ParseFile(currentFilePath);
                foreach (var err in parser.Errors)
                    Errors.Add(err);
            }
            else
            {
                Errors.Add(string.Format("电流文件不存在: {0}", currentFilePath ?? "(null)"));
            }

            // 解析功率文件
            var powerGroups = new Dictionary<long, List<CsvRow>>();
            if (!string.IsNullOrEmpty(powerFilePath) && File.Exists(powerFilePath))
            {
                parser.Errors.Clear();
                powerGroups = parser.ParseFile(powerFilePath);
                foreach (var err in parser.Errors)
                    Errors.Add(err);
            }
            else
            {
                Errors.Add(string.Format("功率文件不存在: {0}", powerFilePath ?? "(null)"));
            }

            // 收集所有唯一 timestamp
            var allTimestamps = new HashSet<long>();
            foreach (var ts in currentGroups.Keys) allTimestamps.Add(ts);
            foreach (var ts in powerGroups.Keys) allTimestamps.Add(ts);

            // 对每个 timestamp 构造一个 SwitchEventJson
            foreach (long ts in allTimestamps)
            {
                var evt = new SwitchEventJson
                {
                    Timestamp = ts,
                    Direction = "定位↔反位",
                    SampleInterval = SAMPLE_INTERVAL,
                };

                // 从电流文件提取 A/B/C 相数据
                if (currentGroups.TryGetValue(ts, out var currentRows))
                {
                    foreach (var row in currentRows)
                    {
                        string phaseLabel = CsvCurveParser.GetPhaseLabel(row.Phase);
                        var values = RoundSamples(row.Samples);

                        switch (phaseLabel)
                        {
                            case "A":
                                evt.CurrentA = values;
                                break;
                            case "B":
                                evt.CurrentB = values;
                                break;
                            case "C":
                                evt.CurrentC = values;
                                break;
                            case "P":
                                // 电流文件中的功率行 → 放入 power 字段
                                evt.Power = values;
                                break;
                        }

                        // 使用第一个有效行的时间字符串
                        if (string.IsNullOrEmpty(evt.Datetime) && !string.IsNullOrEmpty(row.Datetime))
                            evt.Datetime = row.Datetime;
                    }
                }

                // 从功率文件提取功率数据
                if (powerGroups.TryGetValue(ts, out var powerRows))
                {
                    foreach (var row in powerRows)
                    {
                        // 功率文件中的所有行都视为功率数据
                        var values = RoundSamples(row.Samples);
                        evt.Power = values;

                        if (string.IsNullOrEmpty(evt.Datetime) && !string.IsNullOrEmpty(row.Datetime))
                            evt.Datetime = row.Datetime;
                    }
                }

                // 计算 sampleCount（取所有数组的最大长度）
                int maxCount = 0;
                if (evt.CurrentA != null && evt.CurrentA.Count > maxCount) maxCount = evt.CurrentA.Count;
                if (evt.CurrentB != null && evt.CurrentB.Count > maxCount) maxCount = evt.CurrentB.Count;
                if (evt.CurrentC != null && evt.CurrentC.Count > maxCount) maxCount = evt.CurrentC.Count;
                if (evt.Power != null && evt.Power.Count > maxCount) maxCount = evt.Power.Count;

                evt.SampleCount = maxCount;
                evt.Duration = Round3(maxCount * SAMPLE_INTERVAL);

                // 确保有 datetime
                if (string.IsNullOrEmpty(evt.Datetime))
                {
                    var dt = DateTimeHelper.FromUnixTimestamp(ts);
                    evt.Datetime = dt.ToString("yyyy-MM-dd HH:mm:ss");
                }

                events.Add(evt);
            }

            // 按 timestamp 降序排列
            events.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

            return events;
        }

        /// <summary>
        /// 按日期分组写入 JSON 文件。
        /// </summary>
        /// <param name="events">事件列表</param>
        /// <param name="outputDir">输出根目录</param>
        /// <param name="switchId">道岔标识</param>
        public void WriteDateFiles(List<SwitchEventJson> events, string outputDir, string switchId)
        {
            if (events == null || events.Count == 0)
                return;

            // 按日期分组
            var byDate = new Dictionary<string, List<SwitchEventJson>>();
            foreach (var evt in events)
            {
                string date = GetDateFromDatetime(evt.Datetime, evt.Timestamp);
                if (!byDate.ContainsKey(date))
                    byDate[date] = new List<SwitchEventJson>();
                byDate[date].Add(evt);
            }

            // 创建输出目录
            string switchDir = Path.Combine(outputDir, switchId);
            if (!Directory.Exists(switchDir))
                Directory.CreateDirectory(switchDir);

            // 写入每个日期文件
            foreach (var kvp in byDate)
            {
                string date = kvp.Key;
                var dateEvents = kvp.Value;

                // 确保组内按 timestamp 降序
                dateEvents.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

                string filePath = Path.Combine(switchDir, date + ".json");
                string json = JsonConvert.SerializeObject(dateEvents, Formatting.Indented);
                File.WriteAllText(filePath, json, new UTF8Encoding(false));
            }
        }

        /// <summary>
        /// 更新 parsed_data/index.json 索引文件。
        /// </summary>
        /// <param name="outputDir">输出根目录</param>
        public void UpdateIndex(string outputDir)
        {
            if (!Directory.Exists(outputDir))
                return;

            var index = new Dictionary<string, Dictionary<string, List<string>>>();

            // 遍历所有 switchId 目录
            foreach (var switchDir in Directory.GetDirectories(outputDir))
            {
                string switchId = Path.GetFileName(switchDir);

                var dateDict = new Dictionary<string, List<string>>();

                foreach (var jsonFile in Directory.GetFiles(switchDir, "*.json"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(jsonFile);
                    if (fileName == "index") continue; // 跳过自身

                    string date = fileName; // YYYY-MM-DD

                    // 读取 JSON 文件提取时间戳列表
                    try
                    {
                        string content = File.ReadAllText(jsonFile, new UTF8Encoding(false));
                        var events = JsonConvert.DeserializeObject<List<SwitchEventJson>>(content);
                        if (events != null && events.Count > 0)
                        {
                            var timestamps = new List<string>();
                            foreach (var evt in events)
                                timestamps.Add(evt.Timestamp.ToString());

                            // 降序排列
                            timestamps.Sort((a, b) => long.Parse(b).CompareTo(long.Parse(a)));

                            dateDict[date] = timestamps;
                        }
                    }
                    catch
                    {
                        // 跳过损坏的 JSON 文件
                    }
                }

                if (dateDict.Count > 0)
                    index[switchId] = dateDict;
            }

            // 写入 index.json
            string indexPath = Path.Combine(outputDir, "index.json");
            string indexJson = JsonConvert.SerializeObject(index, Formatting.Indented);
            File.WriteAllText(indexPath, indexJson, new UTF8Encoding(false));
        }

        /// <summary>
        /// 将浮点数保留 3 位小数。
        /// </summary>
        private static double Round3(double value)
        {
            return Math.Round(value, DECIMAL_PLACES, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 将 float 数组转为 double 列表并保留 3 位小数。
        /// </summary>
        private static List<double> RoundSamples(float[] samples)
        {
            if (samples == null) return new List<double>();

            var result = new List<double>(samples.Length);
            for (int i = 0; i < samples.Length; i++)
            {
                result.Add(Round3(samples[i]));
            }
            return result;
        }

        /// <summary>
        /// 从 datetime 字符串中提取日期部分 (YYYY-MM-DD)。
        /// </summary>
        private static string GetDateFromDatetime(string datetime, long timestamp)
        {
            if (!string.IsNullOrEmpty(datetime) && datetime.Length >= 10)
                return datetime.Substring(0, 10);

            // 降级：从 timestamp 推算
            var dt = DateTimeHelper.FromUnixTimestamp(timestamp);
            return dt.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// 从文件名提取索引号，如 "SwitchCurve(0)" → 0
        /// </summary>
        private static int ExtractFileIndex(string baseName)
        {
            int start = baseName.IndexOf('(');
            int end = baseName.IndexOf(')');
            if (start >= 0 && end > start)
            {
                int.TryParse(baseName.Substring(start + 1, end - start - 1), out int idx);
                return idx;
            }
            return -1;
        }

        /// <summary>
        /// 根据 _file_type_summary.csv 的配对规则构建文件对列表。
        /// 电流文件 (偶数索引) → Item1, 功率文件 (奇数索引) → Item2
        /// </summary>
        private static List<Tuple<string, string>> BuildFilePairs(string[] csvFiles)
        {
            var pairs = new List<Tuple<string, string>>();

            var byIndex = new Dictionary<int, string>();
            foreach (var f in csvFiles)
            {
                string baseName = Path.GetFileNameWithoutExtension(f);
                int idx = ExtractFileIndex(baseName);
                if (idx >= 0)
                    byIndex[idx] = f;
            }

            int[] currentIndices = { 0, 4, 8, 12, 16, 20, 24, 28 };
            int[] powerIndices = { 3, 7, 11, 15, 19, 23, 27, 31 };

            for (int i = 0; i < currentIndices.Length; i++)
            {
                int ci = currentIndices[i];
                int pi = powerIndices[i];
                if (byIndex.ContainsKey(ci) && byIndex.ContainsKey(pi))
                {
                    pairs.Add(Tuple.Create(byIndex[ci], byIndex[pi]));
                }
            }

            return pairs;
        }
    }
}
