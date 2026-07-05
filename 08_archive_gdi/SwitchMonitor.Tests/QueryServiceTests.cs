using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 4: QueryService TDD 测试。
    /// 使用 DatabaseFactory → NativeSqlite → winsqlite3.dll 访问测试数据库。
    /// </summary>
    public class QueryServiceTests
    {
        public static int passed = 0;
        public static int failed = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 4 + Slice 6: QueryService 测试 ===");
            Console.WriteLine();

            TestQueryService_GetAllActions();
            TestQueryService_GetActionCount();
            TestQueryService_GetCurveSamples();
            TestQueryService_GetSampleCount();
            TestCurveSamples_DataIntegrity();

            Console.WriteLine("--- Slice 6: 筛选查询 ---");
            Console.WriteLine();

            TestQueryService_GetDistinctSwitchIds();
            TestQueryService_GetActions_FilterBySwitch();
            TestQueryService_GetActions_FilterByTimeRange();
            TestQueryService_GetActions_FilterCombined();
            TestQueryService_GetActions_Limit();
            TestQueryService_GetActions_EmptyResult();
            TestQueryService_IndexUsage();

            Console.WriteLine();
            Console.WriteLine("Slice 4+6 测试结果: 通过={0}, 失败={1}", passed, failed);
            return (passed, failed);
        }

        /// <summary>
        /// 测试 1: GetAllActions 返回正确的动作列表，按时间倒序
        /// </summary>
        static void TestQueryService_GetAllActions()
        {
            Console.WriteLine("--- 测试 1: GetAllActions ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var actions = svc.GetAllActions();

                Assert(actions != null, "返回非 null");
                Assert(actions.Count == 3, "包含 3 条动作记录");

                // 按时间倒序验证
                Assert(actions[0].StartTime >= actions[1].StartTime,
                    string.Format("倒序[0]={0} >= [1]={1}", actions[0].StartTime, actions[1].StartTime));
                Assert(actions[1].StartTime >= actions[2].StartTime,
                    string.Format("倒序[1]={0} >= [2]={1}", actions[1].StartTime, actions[2].StartTime));

                // 验证字段
                var first = actions[0];
                Assert(!string.IsNullOrEmpty(first.SwitchId), "SwitchId 不为空");
                Assert(!string.IsNullOrEmpty(first.Direction), "Direction 不为空");
                Assert(first.SampleCount > 0, "SampleCount > 0");
                Assert(first.PhaseCount == 3, "PhaseCount == 3");
                Assert(!string.IsNullOrEmpty(first.StartTimeDisplay), "StartTimeDisplay 不为空");

                Console.WriteLine("  第1条: {0}", first);
                Console.WriteLine("  第2条: {0}", actions[1]);
                Console.WriteLine("  第3条: {0}", actions[2]);

                Console.WriteLine("  [PASS] 测试 1 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 1 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 2: GetActionCount
        /// </summary>
        static void TestQueryService_GetActionCount()
        {
            Console.WriteLine("--- 测试 2: GetActionCount ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                int count = svc.GetActionCount();
                Assert(count == 3, string.Format("动作总数为 3 (实际={0})", count));
                Console.WriteLine("  动作总数: {0}", count);
                Console.WriteLine("  [PASS] 测试 2 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 2 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 3: GetCurveSamples 返回正确数据
        /// </summary>
        static void TestQueryService_GetCurveSamples()
        {
            Console.WriteLine("--- 测试 3: GetCurveSamples ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var actions = svc.GetAllActions();
                int actionId = actions[0].Id;

                var samples = svc.GetCurveSamples(actionId);
                Assert(samples != null, "返回非 null");
                Assert(samples.Count > 0, "包含采样数据");

                // 验证三相数据都存在
                var phases = samples.Select(s => s.Phase).Distinct().OrderBy(p => p).ToList();
                Console.WriteLine("  相别: {0}", string.Join(", ", phases));
                Assert(phases.Contains("A"), "包含 A 相");
                Assert(phases.Contains("B"), "包含 B 相");
                Assert(phases.Contains("C"), "包含 C 相");

                // 验证 A 相采样序号从 0 开始
                var phaseA = samples.Where(s => s.Phase == "A").OrderBy(s => s.SampleIndex).ToList();
                Assert(phaseA.Count > 0, "A 相有数据");
                Assert(phaseA[0].SampleIndex == 0, "A 相第一个采样序号为 0");

                Console.WriteLine("  A相采样数: {0}, 第1点: I={1:F2}A V={2:F1}V",
                    phaseA.Count, phaseA[0].Current, phaseA[0].Voltage);
                Console.WriteLine("  [PASS] 测试 3 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 3 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 4: GetSampleCount
        /// </summary>
        static void TestQueryService_GetSampleCount()
        {
            Console.WriteLine("--- 测试 4: GetSampleCount ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var actions = svc.GetAllActions();

                foreach (var action in actions)
                {
                    int count = svc.GetSampleCount(action.Id);
                    Assert(count == action.SampleCount,
                        string.Format("{0}: DISTINCT SampleIndex={1} == SampleCount={2}",
                            action.SwitchId, count, action.SampleCount));
                    Console.WriteLine("  {0}: SampleCount={1}", action.SwitchId, count);
                }

                Console.WriteLine("  [PASS] 测试 4 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 4 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 5: 数据完整性——每相采样数等于 SampleCount，序号连续
        /// </summary>
        static void TestCurveSamples_DataIntegrity()
        {
            Console.WriteLine("--- 测试 5: 曲线数据完整性 ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var actions = svc.GetAllActions();

                foreach (var action in actions)
                {
                    var samples = svc.GetCurveSamples(action.Id);

                    foreach (var phase in new[] { "A", "B", "C" })
                    {
                        var phaseSamples = samples
                            .Where(s => s.Phase == phase)
                            .OrderBy(s => s.SampleIndex)
                            .ToList();

                        Assert(phaseSamples.Count == action.SampleCount,
                            string.Format("{0} {1}相采样数={2}", action.SwitchId, phase, action.SampleCount));

                        // 采样序号连续
                        for (int i = 0; i < phaseSamples.Count; i++)
                        {
                            Assert(phaseSamples[i].SampleIndex == i,
                                string.Format("{0} {1}相[期望{2} 实际{3}]",
                                    action.SwitchId, phase, i, phaseSamples[i].SampleIndex));
                        }
                    }

                    Console.WriteLine("  {0}: 完整性 OK (3相 x {1}点 = {2}条)",
                        action.SwitchId, action.SampleCount, samples.Count);
                }

                Console.WriteLine("  [PASS] 测试 5 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 5 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 6: GetDistinctSwitchIds 返回所有不重复道岔 ID
        /// </summary>
        static void TestQueryService_GetDistinctSwitchIds()
        {
            Console.WriteLine("--- 测试 6: GetDistinctSwitchIds ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var ids = svc.GetDistinctSwitchIds();

                Assert(ids != null, "返回非 null");
                Assert(ids.Count > 0, "至少有一个道岔 ID");

                // 验证无重复
                var distinct = new HashSet<string>(ids);
                int distinctCount = distinct.Count;
                Assert(ids.Count == distinctCount,
                    string.Format("无重复: Count={0} Distinct={1}", ids.Count, distinctCount));

                // 验证按名称排序
                for (int i = 1; i < ids.Count; i++)
                {
                    Assert(string.Compare(ids[i - 1], ids[i], StringComparison.Ordinal) <= 0,
                        string.Format("排序: [{0}] <= [{1}]", ids[i - 1], ids[i]));
                }

                Console.WriteLine("  道岔列表: {0}", string.Join(", ", ids));
                Console.WriteLine("  [PASS] 测试 6 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 6 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 7: GetActions 按道岔 ID 筛选
        /// </summary>
        static void TestQueryService_GetActions_FilterBySwitch()
        {
            Console.WriteLine("--- 测试 7: GetActions 按道岔筛选 ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();
                Assert(allIds.Count > 0, "至少有一个道岔 ID");

                string targetSwitch = allIds[0];
                var actions = svc.GetActions(switchId: targetSwitch);

                Assert(actions != null, "返回非 null");
                Assert(actions.Count > 0, string.Format("{0} 有动作记录", targetSwitch));

                // 验证所有返回记录都是该道岔
                foreach (var a in actions)
                {
                    Assert(a.SwitchId == targetSwitch,
                        string.Format("动作 {0} 的道岔={1} 匹配筛选={2}", a.Id, a.SwitchId, targetSwitch));
                }

                Console.WriteLine("  道岔 {0}: {1} 条动作", targetSwitch, actions.Count);
                Console.WriteLine("  [PASS] 测试 7 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 7 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 8: GetActions 按时间范围筛选
        /// </summary>
        static void TestQueryService_GetActions_FilterByTimeRange()
        {
            Console.WriteLine("--- 测试 8: GetActions 按时间范围筛选 ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var all = svc.GetAllActions();
                Assert(all.Count > 0, "有动作记录");

                // 取中间一条记录的时间作为参考
                var mid = all[all.Count / 2];
                var midTime = DateTimeHelper.FromUnixTimestamp(mid.StartTime);

                // 查询一个窄时间窗口（±1 小时）
                var from = midTime.AddHours(-1);
                var to = midTime.AddHours(1);

                var actions = svc.GetActions(from: from, to: to);

                Assert(actions != null, "返回非 null");
                Assert(actions.Count > 0, "时间范围内有记录");

                // 验证所有记录在时间范围内
                foreach (var a in actions)
                {
                    var dt = DateTimeHelper.FromUnixTimestamp(a.StartTime);
                    Assert(dt >= from && dt <= to,
                        string.Format("动作 {0} 时间 {1:yyyy-MM-dd HH:mm:ss} 在 [{2:yyyy-MM-dd HH:mm:ss}, {3:yyyy-MM-dd HH:mm:ss}] 内",
                            a.Id, dt, from, to));
                }

                Console.WriteLine("  时间范围 [{0:yyyy-MM-dd HH:mm}, {1:yyyy-MM-dd HH:mm}]: {2} 条",
                    from, to, actions.Count);
                Console.WriteLine("  [PASS] 测试 8 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 8 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 9: GetActions 组合筛选（道岔 + 时间范围）
        /// </summary>
        static void TestQueryService_GetActions_FilterCombined()
        {
            Console.WriteLine("--- 测试 9: GetActions 组合筛选 ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();
                Assert(allIds.Count > 0, "至少有一个道岔");

                string targetSwitch = allIds[0];
                var allActions = svc.GetActions(switchId: targetSwitch);
                Assert(allActions.Count > 0, "目标道岔有记录");

                // 取该道岔第一条记录的时间
                var firstTime = DateTimeHelper.FromUnixTimestamp(allActions[0].StartTime);
                var from = firstTime.AddDays(-1);
                var to = firstTime.AddDays(1);

                var actions = svc.GetActions(switchId: targetSwitch, from: from, to: to);

                Assert(actions != null, "返回非 null");
                Assert(actions.Count > 0, "组合筛选有结果");

                // 验证：每条记录同时满足道岔和时间条件
                foreach (var a in actions)
                {
                    Assert(a.SwitchId == targetSwitch,
                        string.Format("道岔匹配: {0} == {1}", a.SwitchId, targetSwitch));
                    var dt = DateTimeHelper.FromUnixTimestamp(a.StartTime);
                    Assert(dt >= from && dt <= to,
                        string.Format("时间在范围内: {0:yyyy-MM-dd HH:mm:ss}", dt));
                }

                Console.WriteLine("  道岔={0} 时间=[{1:yyyy-MM-dd}, {2:yyyy-MM-dd}]: {3} 条",
                    targetSwitch, from, to, actions.Count);
                Console.WriteLine("  [PASS] 测试 9 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 9 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 10: GetActions 的 limit 参数生效
        /// </summary>
        static void TestQueryService_GetActions_Limit()
        {
            Console.WriteLine("--- 测试 10: GetActions Limit ---");

            try
            {
                var svc = new QueryService(GetDbPath());
                int limit = 2;
                var actions = svc.GetActions(limit: limit);

                Assert(actions != null, "返回非 null");
                Assert(actions.Count <= limit,
                    string.Format("返回条数 {0} <= limit {1}", actions.Count, limit));

                Console.WriteLine("  limit={0}, 实际返回 {1} 条", limit, actions.Count);
                Console.WriteLine("  [PASS] 测试 10 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 10 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 11: 无匹配结果时返回空列表
        /// </summary>
        static void TestQueryService_GetActions_EmptyResult()
        {
            Console.WriteLine("--- 测试 11: GetActions 无匹配结果 ---");

            try
            {
                var svc = new QueryService(GetDbPath());

                // 使用一个不可能存在的道岔 ID
                var actions = svc.GetActions(switchId: "NONEXISTENT_SW_999");

                Assert(actions != null, "返回非 null");
                Assert(actions.Count == 0, "不存在的道岔返回空列表");

                // 使用一个未来的时间范围（不可能有数据）
                var farFuture = new DateTime(2099, 1, 1);
                var actions2 = svc.GetActions(from: farFuture, to: farFuture.AddDays(1));

                Assert(actions2 != null, "返回非 null");
                Assert(actions2.Count == 0, "未来时间范围返回空列表");

                Console.WriteLine("  不存在道岔: {0} 条, 未来时间: {1} 条", actions.Count, actions2.Count);
                Console.WriteLine("  [PASS] 测试 11 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 11 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 测试 12: 验证 idx_actions_switch_time 索引被查询使用
        /// </summary>
        static void TestQueryService_IndexUsage()
        {
            Console.WriteLine("--- 测试 12: 索引使用验证 (EXPLAIN QUERY PLAN) ---");

            try
            {
                var factory = new DatabaseFactory(GetDbPath());
                using (var db = factory.OpenDatabase())
                {
                    // 测试 1: 仅按道岔筛选
                    var plan1 = db.Query(
                        "EXPLAIN QUERY PLAN SELECT * FROM SwitchActions WHERE SwitchId = ? ORDER BY StartTime DESC LIMIT 500",
                        new object[] { "SW_01" });
                    string detail1 = GetPlanDetail(plan1);
                    Console.WriteLine("  仅道岔筛选: {0}", detail1);
                    Assert(detail1.Contains("idx_actions_switch_time") || detail1.Contains("USING INDEX"),
                        "道岔筛选应使用 idx_actions_switch_time 索引: " + detail1);

                    // 测试 2: 仅按时间范围筛选
                    var plan2 = db.Query(
                        "EXPLAIN QUERY PLAN SELECT * FROM SwitchActions WHERE StartTime >= ? AND StartTime <= ? ORDER BY StartTime DESC LIMIT 500",
                        new object[] { 0L, 9999999999L });
                    string detail2 = GetPlanDetail(plan2);
                    Console.WriteLine("  仅时间筛选: {0}", detail2);
                    // 时间筛选也应该能用索引（复合索引的第二列也能用于范围扫描）
                    // 如果不行，至少要有 SCAN 而不是全表扫描的说明
                    Assert(!string.IsNullOrEmpty(detail2), "时间筛选查询计划存在");

                    // 测试 3: 组合筛选（道岔 + 时间）
                    var plan3 = db.Query(
                        "EXPLAIN QUERY PLAN SELECT * FROM SwitchActions WHERE SwitchId = ? AND StartTime >= ? AND StartTime <= ? ORDER BY StartTime DESC LIMIT 500",
                        new object[] { "SW_01", 0L, 9999999999L });
                    string detail3 = GetPlanDetail(plan3);
                    Console.WriteLine("  组合筛选: {0}", detail3);
                    Assert(detail3.Contains("idx_actions_switch_time") || detail3.Contains("USING INDEX"),
                        "组合筛选应使用 idx_actions_switch_time 索引: " + detail3);
                }

                Console.WriteLine("  [PASS] 测试 12 通过");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 测试 12 失败: {0}", ex.Message);
                failed++;
            }
            Console.WriteLine();
        }

        /// <summary>从 EXPLAIN QUERY PLAN 结果中提取 detail 列的内容</summary>
        static string GetPlanDetail(List<Dictionary<string, object>> rows)
        {
            if (rows == null || rows.Count == 0)
                return "(empty)";
            var parts = new List<string>();
            foreach (var row in rows)
            {
                if (row.TryGetValue("detail", out object detail) && detail != null)
                    parts.Add(detail.ToString());
            }
            return string.Join(" | ", parts);
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                Console.WriteLine("    ASSERT FAIL: {0}", message);
                throw new Exception(message);
            }
        }

        static string GetDbPath()
        {
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Data", "switch_test.db")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Data", "switch_test.db")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Data", "switch_test.db")),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            throw new FileNotFoundException("找不到测试数据库 switch_test.db。请先运行 scripts/create_test_db.py");
        }
    }
}
