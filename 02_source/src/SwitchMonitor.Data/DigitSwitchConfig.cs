using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SwitchMonitor.Data
{
    /// <summary>
    /// 单个道岔的 digit 开关量点号配置（对应 digit.ini 中的一组 1DQJ/DB/FB）
    /// </summary>
    public class DigitPointIds
    {
        /// <summary>DB 定位表示继电器点号</summary>
        public int db_point_id;
        /// <summary>FB 反位表示继电器点号</summary>
        public int fb_point_id;
        /// <summary>1DQJ 启动继电器点号</summary>
        public int dqj_point_id;
    }

    /// <summary>
    /// digit.ini 道岔点号注册表。
    /// Key = 代码中使用的 switch ID（如 "4-X", "1-J"），Value = 三键点号配置。
    ///
    /// 两种加载方式：
    /// 1. LoadFromIni(path) — 直接解析 GBK 编码的 digit.ini（监测终端使用）
    /// 2. Load(jsonPath)  — 从预生成的 JSON 加载（开发/回退使用）
    /// </summary>
    public class DigitSwitchRegistry
    {
        /// <summary>配置版本号</summary>
        public string version;
        /// <summary>车站标识</summary>
        public string station_id;
        /// <summary>来源文件</summary>
        public string source_file;
        /// <summary>生成时间</summary>
        public string generated_at;
        /// <summary>道岔 → 点号映射</summary>
        public Dictionary<string, DigitPointIds> switches;

        public DigitSwitchRegistry()
        {
            switches = new Dictionary<string, DigitPointIds>();
        }

        /// <summary>查询某道岔的点号配置</summary>
        public bool TryGetConfig(string switchId, out DigitPointIds config)
        {
            return switches.TryGetValue(switchId, out config);
        }

        /// <summary>
        /// 从 digit.ini (GBK 编码) 直接解析。
        /// 扫描每一行，匹配 Name 中包含 -DB / -FB / -1DQJ 的条目，
        /// 按道岔分组收集三个点号。
        /// </summary>
        public static DigitSwitchRegistry LoadFromIni(string iniPath)
        {
            var registry = new DigitSwitchRegistry
            {
                version = "1.0",
                source_file = Path.GetFileName(iniPath),
                generated_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            };

            // 正则: PointID=SwitchName-Type, ...
            // 例: 15586=4-X-DB             ,     15585,        8,    0,    9
            var regex = new Regex(
                @"^(\d+)\s*=\s*(\d+-[JX])-(1DQJ|DB|FB)\s*,",
                RegexOptions.Compiled);

            using (var reader = new StreamReader(iniPath, Encoding.GetEncoding("GBK")))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var m = regex.Match(line);
                    if (!m.Success)
                        continue;

                    int pointId = int.Parse(m.Groups[1].Value);
                    string switchName = m.Groups[2].Value;  // e.g. "4-X", "1-J"
                    string relayType = m.Groups[3].Value;    // "1DQJ", "DB", "FB"

                    // digit.ini uses standard naming (e.g. "1-J", "4-X"), matching the codebase.
                    string switchId = switchName;

                    if (!registry.switches.ContainsKey(switchId))
                        registry.switches[switchId] = new DigitPointIds();

                    var entry = registry.switches[switchId];
                    switch (relayType)
                    {
                        case "1DQJ": entry.dqj_point_id = pointId; break;
                        case "DB":   entry.db_point_id = pointId; break;
                        case "FB":   entry.fb_point_id = pointId; break;
                    }
                }
            }

            // 提取 station_id（尝试从文件路径推断）
            registry.station_id = "SSB";

            return registry;
        }

        /// <summary>
        /// 从预生成的 JSON 文件加载。
        /// 用于 digit.ini 不可用时的回退方案。
        /// </summary>
        public static DigitSwitchRegistry Load(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                return null;

            try
            {
                string json = File.ReadAllText(jsonPath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var registry = serializer.Deserialize<DigitSwitchRegistry>(json);
                if (registry != null && registry.switches == null)
                    registry.switches = new Dictionary<string, DigitPointIds>();
                return registry;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 收集所有道岔的 DB + FB 点号集合（用于 digit 数据过滤）
        /// </summary>
        public HashSet<int> GetAllPointIds()
        {
            var ids = new HashSet<int>();
            foreach (var kv in switches)
            {
                if (kv.Value.db_point_id > 0)
                    ids.Add(kv.Value.db_point_id);
                if (kv.Value.fb_point_id > 0)
                    ids.Add(kv.Value.fb_point_id);
            }
            return ids;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // DigitEvent — 单个开关量状态变化记录
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Digit(*).dat 中解析出的单个开关量状态变化事件
    /// </summary>
    public class DigitEvent
    {
        /// <summary>Unix 时间戳（秒）</summary>
        public long Timestamp;
        /// <summary>采集点号（低字节）</summary>
        public int PointId;
        /// <summary>状态字节（高字节），0x2f=动作/吸起，0x00=落下</summary>
        public int StateByte;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DirectionResolver — DB/FB 状态 → 方向
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 根据 DB/FB 开关量状态判定道岔动作方向。
    ///
    /// 逻辑：
    ///   DB=1(吸起) + FB=0(落下) → 当前位置=定位，动作方向=定位→反位
    ///   DB=0(落下) + FB=1(吸起) → 当前位置=反位，动作方向=反位→定位
    ///   其他（同为0/同为1/无数据）  → null（上层转为"未知"）
    ///
    /// 流式查找：对按时间升序排列的事件顺序调用 Resolve()，
    /// 内部维护指针，整体复杂度 O(T + E)。
    /// </summary>
    public class DirectionResolver
    {
        private readonly int _dbPointId;
        private readonly int _fbPointId;
        private readonly List<DigitEvent> _timeline;
        private int _pos;              // 当前时间线指针
        private int _lastDbState;      // -1 = unknown
        private int _lastFbState;      // -1 = unknown

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="dbPointId">DB 继电器点号</param>
        /// <param name="fbPointId">FB 继电器点号</param>
        /// <param name="timeline">按时间戳升序排列的开关量时间线</param>
        public DirectionResolver(int dbPointId, int fbPointId, List<DigitEvent> timeline)
        {
            _dbPointId = dbPointId;
            _fbPointId = fbPointId;
            _timeline = timeline ?? new List<DigitEvent>();
            _pos = 0;
            _lastDbState = -1;
            _lastFbState = -1;
        }

        /// <summary>
        /// 判定指定时间戳对应的动作方向。
        /// 调用方保证 timestamp 按升序调用（流式）。
        /// </summary>
        /// <returns>"定位→反位" / "反位→定位" / null</returns>
        public string Resolve(long timestamp)
        {
            if (_timeline.Count == 0)
                return null;

            // 推进指针到 timestamp
            while (_pos < _timeline.Count && _timeline[_pos].Timestamp <= timestamp)
            {
                var evt = _timeline[_pos];
                if (evt.PointId == _dbPointId)
                    _lastDbState = (evt.StateByte == 0x2f) ? 1 : 0;
                else if (evt.PointId == _fbPointId)
                    _lastFbState = (evt.StateByte == 0x2f) ? 1 : 0;
                _pos++;
            }

            return ResolveFromStates(_lastDbState, _lastFbState);
        }

        /// <summary>
        /// 纯函数：根据 DB/FB 状态判定方向，不依赖时间线。
        /// 用于已有 DB/FB 值的场景。
        /// </summary>
        /// <param name="dbState">DB 状态: 1=吸起(定位), 0=落下, -1=未知</param>
        /// <param name="fbState">FB 状态: 1=吸起(反位), 0=落下, -1=未知</param>
        /// <returns>方向字符串或 null</returns>
        public static string ResolveFromStates(int dbState, int fbState)
        {
            if (dbState == 1 && fbState == 0)
                return "定位→反位";
            if (dbState == 0 && fbState == 1)
                return "反位→定位";
            return null;
        }

        /// <summary>
        /// 重置流式查找状态（用于处理新的时间线或新的一组事件）
        /// </summary>
        public void Reset()
        {
            _pos = 0;
            _lastDbState = -1;
            _lastFbState = -1;
        }
    }
}
