using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// Slice 8: 参考曲线管理 TDD 测试。
    /// 测试 ReferenceCurveRecord 模型、QueryService CRUD 方法、缓存行为。
    /// </summary>
    public class Slice8Tests
    {
        static int passed = 0;
        static int failed = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== Slice 8: 参考曲线管理 测试 ===");
            Console.WriteLine();

            // 模型测试
            TestReferenceCurveRecord_Creation();

            // QueryService CRUD 测试
            TestSetReferenceCurve();
            TestGetActiveReferenceCurve();
            TestClearReferenceCurve();
            TestAutoDeactivateOldReference();
            TestGetAllReferenceCurves();
            TestDeleteAndReactivateReference();
            TestNoActiveReferenceCurve();
            TestReferenceCurveCache();

            Console.WriteLine();
            Console.WriteLine("=== Slice 8 结果汇总 ===");
            Console.WriteLine("通过: {0}, 失败: {1}", passed, failed);
            return (passed, failed);
        }

        // ================================================================
        // Cycle 1: ReferenceCurveRecord 模型创建
        // ================================================================

        /// <summary>
        /// Cycle 1: 创建 ReferenceCurveRecord 并验证字段。
        /// </summary>
        static void TestReferenceCurveRecord_Creation()
        {
            Console.WriteLine("--- Cycle 1: ReferenceCurveRecord 模型创建 ---");
            try
            {
                var record = new ReferenceCurveRecord
                {
                    Id = 1,
                    SwitchId = "SW_01",
                    ActionId = 42,
                    SetTime = "2026-06-15 10:30:00",
                    Description = "检修后",
                    IsActive = true
                };

                Assert(record.Id == 1, "Id == 1");
                Assert(record.SwitchId == "SW_01", "SwitchId == SW_01");
                Assert(record.ActionId == 42, "ActionId == 42");
                Assert(record.SetTime == "2026-06-15 10:30:00", "SetTime 正确");
                Assert(record.Description == "检修后", "Description 正确");
                Assert(record.IsActive == true, "IsActive == true");

                // 格式化的显示文本
                string display = record.ToString();
                Assert(display.Contains("SW_01"), "ToString 包含 SwitchId");
                Assert(display.Contains("检修后"), "ToString 包含 Description");

                Console.WriteLine("  记录: {0}", record);
                Console.WriteLine("  [PASS] Cycle 1");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 2: SetReferenceCurve — 设置活跃参考曲线
        // ================================================================

        /// <summary>
        /// Cycle 2: 设置参考曲线后，GetActiveReferenceCurve 返回对应记录。
        /// </summary>
        static void TestSetReferenceCurve()
        {
            Console.WriteLine("--- Cycle 2: SetReferenceCurve ---");
            try
            {
                var svc = new QueryService(GetDbPath());

                // 获取一个道岔和动作
                var allIds = svc.GetDistinctSwitchIds();
                Assert(allIds.Count > 0, "至少有一个道岔");

                string switchId = allIds[0];
                var actions = svc.GetActions(switchId: switchId);
                Assert(actions.Count > 0, "目标道岔有动作记录");

                int actionId = actions[0].Id;

                // 先清空该道岔的活跃参考曲线（确保测试干净）
                svc.ClearReferenceCurve(switchId);

                // 设置参考曲线
                string description = "测试参考曲线";
                svc.SetReferenceCurve(switchId, actionId, description);

                // 查询活跃参考曲线
                var active = svc.GetActiveReferenceCurve(switchId);
                Assert(active != null, "设置后 GetActiveReferenceCurve 返回非 null");
                Assert(active.SwitchId == switchId,
                    string.Format("SwitchId 匹配: {0} == {1}", active.SwitchId, switchId));
                Assert(active.ActionId == actionId,
                    string.Format("ActionId 匹配: {0} == {1}", active.ActionId, actionId));
                Assert(active.Description == description,
                    string.Format("Description 匹配: {0}", active.Description));
                Assert(active.IsActive == true, "IsActive == true");
                Assert(!string.IsNullOrEmpty(active.SetTime), "SetTime 不为空");

                Console.WriteLine("  活跃参考曲线: SwitchId={0}, ActionId={1}, SetTime={2}, Desc={3}",
                    active.SwitchId, active.ActionId, active.SetTime, active.Description);

                // 清理
                svc.ClearReferenceCurve(switchId);

                Console.WriteLine("  [PASS] Cycle 2");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 3: GetActiveReferenceCurve — 获取活跃参考曲线
        // ================================================================

        /// <summary>
        /// Cycle 3: 获取活跃参考曲线包含 SetTime 和 Description。
        /// </summary>
        static void TestGetActiveReferenceCurve()
        {
            Console.WriteLine("--- Cycle 3: GetActiveReferenceCurve ---");
            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();
                Assert(allIds.Count > 0, "至少有一个道岔");

                string switchId = allIds[0];
                var actions = svc.GetActions(switchId: switchId);

                // 清空旧的
                svc.ClearReferenceCurve(switchId);

                // 设置两条参考曲线（第二条应该自动变为活跃）
                svc.SetReferenceCurve(switchId, actions[0].Id, "第一次设定");
                svc.SetReferenceCurve(switchId, actions[0].Id, "第二次设定-当前活跃");

                var active = svc.GetActiveReferenceCurve(switchId);
                Assert(active != null, "活跃参考曲线存在");
                Assert(active.Description == "第二次设定-当前活跃",
                    string.Format("活跃的是最新设定的: {0}", active.Description));
                Assert(active.IsActive == true, "IsActive == true");

                Console.WriteLine("  活跃: {0}", active);

                // 清理
                svc.ClearReferenceCurve(switchId);

                Console.WriteLine("  [PASS] Cycle 3");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 4: ClearReferenceCurve — 清除活跃参考曲线
        // ================================================================

        /// <summary>
        /// Cycle 4: 清除后 IsActive 变为 false，GetActiveReferenceCurve 返回 null。
        /// </summary>
        static void TestClearReferenceCurve()
        {
            Console.WriteLine("--- Cycle 4: ClearReferenceCurve ---");
            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();
                string switchId = allIds[0];
                var actions = svc.GetActions(switchId: switchId);

                // 先清空并设置
                svc.ClearReferenceCurve(switchId);
                svc.SetReferenceCurve(switchId, actions[0].Id, "待清除的参考曲线");

                // 确认活跃
                var before = svc.GetActiveReferenceCurve(switchId);
                Assert(before != null, "清除前有活跃参考曲线");

                // 清除
                svc.ClearReferenceCurve(switchId);

                // 确认已清除
                var after = svc.GetActiveReferenceCurve(switchId);
                Assert(after == null, "清除后 GetActiveReferenceCurve 返回 null");

                Console.WriteLine("  清除前: {0}", before);
                Console.WriteLine("  [PASS] Cycle 4");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 5: 自动去激活旧参考曲线
        // ================================================================

        /// <summary>
        /// Cycle 5: 同一道岔设新参考时，旧参考的 IsActive 自动置为 0。
        /// </summary>
        static void TestAutoDeactivateOldReference()
        {
            Console.WriteLine("--- Cycle 5: 自动去激活旧参考 ---");
            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();
                string switchId = allIds[0];
                var actions = svc.GetActions(switchId: switchId);

                // 清空先
                svc.ClearReferenceCurve(switchId);

                // 设置第一条
                svc.SetReferenceCurve(switchId, actions[0].Id, "旧参考");
                long firstId = 0;
                var first = svc.GetActiveReferenceCurve(switchId);
                if (first != null) firstId = first.Id;

                // 设置第二条（同一道岔，不同时间）
                System.Threading.Thread.Sleep(1100); // 确保 SetTime 不同
                svc.SetReferenceCurve(switchId, actions[0].Id, "新参考");

                // 验证：只有一条活跃
                var active = svc.GetActiveReferenceCurve(switchId);
                Assert(active != null, "有新活跃参考曲线");
                Assert(active.Description == "新参考", "活跃的是新参考");

                // 查询所有参考曲线，验证旧的那条 IsActive = false
                var all = svc.GetAllReferenceCurves();
                var oldOne = all.Find(r => r.Id == firstId);
                Assert(oldOne != null, "旧参考曲线仍在列表中（未删除）");
                Assert(oldOne.IsActive == false, "旧参考曲线 IsActive == false");

                Console.WriteLine("  旧参考 Id={0} IsActive={1}, 新参考 Id={2} IsActive={3}",
                    oldOne.Id, oldOne.IsActive, active.Id, active.IsActive);

                // 清理
                svc.ClearReferenceCurve(switchId);

                Console.WriteLine("  [PASS] Cycle 5");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 6: GetAllReferenceCurves — 查看所有参考曲线
        // ================================================================

        /// <summary>
        /// Cycle 6: GetAllReferenceCurves 返回所有已设定的参考曲线列表。
        /// </summary>
        static void TestGetAllReferenceCurves()
        {
            Console.WriteLine("--- Cycle 6: GetAllReferenceCurves ---");
            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();

                // 确保至少有两个道岔的数据
                if (allIds.Count >= 2)
                {
                    string sw1 = allIds[0], sw2 = allIds[1];
                    var actions1 = svc.GetActions(switchId: sw1);
                    var actions2 = svc.GetActions(switchId: sw2);

                    // 清空
                    svc.ClearReferenceCurve(sw1);
                    svc.ClearReferenceCurve(sw2);

                    // 为两个道岔各设一条参考曲线
                    svc.SetReferenceCurve(sw1, actions1[0].Id, "SW1 参考");
                    svc.SetReferenceCurve(sw2, actions2[0].Id, "SW2 参考");

                    var all = svc.GetAllReferenceCurves();
                    Assert(all != null, "GetAllReferenceCurves 返回非 null");
                    Assert(all.Count >= 2, string.Format("至少有 2 条参考曲线 (实际={0})", all.Count));

                    // 验证列表中的记录有正确的字段
                    foreach (var r in all)
                    {
                        Assert(!string.IsNullOrEmpty(r.SwitchId), "SwitchId 不为空");
                        Assert(r.ActionId > 0, "ActionId > 0");
                        Assert(!string.IsNullOrEmpty(r.SetTime), "SetTime 不为空");
                    }

                    Console.WriteLine("  共 {0} 条参考曲线记录", all.Count);
                    foreach (var r in all)
                    {
                        Console.WriteLine("    {0}: SwitchId={1}, ActionId={2}, Active={3}, Desc={4}",
                            r.Id, r.SwitchId, r.ActionId, r.IsActive, r.Description);
                    }

                    // 清理
                    svc.ClearReferenceCurve(sw1);
                    svc.ClearReferenceCurve(sw2);
                }
                else
                {
                    Console.WriteLine("  (跳过 — 需要至少 2 个道岔，当前只有 {0} 个)", allIds.Count);
                }

                Console.WriteLine("  [PASS] Cycle 6");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 7: 删除和重新激活参考曲线
        // ================================================================

        /// <summary>
        /// Cycle 7: DeleteReferenceCurve（IsActive=0）和 ReactivateReferenceCurve（IsActive=1）。
        /// </summary>
        static void TestDeleteAndReactivateReference()
        {
            Console.WriteLine("--- Cycle 7: 删除和重新激活 ---");
            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();
                string switchId = allIds[0];
                var actions = svc.GetActions(switchId: switchId);

                // 清空
                svc.ClearReferenceCurve(switchId);

                // 设置参考曲线
                svc.SetReferenceCurve(switchId, actions[0].Id, "测试删除和激活");
                var active = svc.GetActiveReferenceCurve(switchId);
                Assert(active != null, "活跃参考曲线存在");

                // 删除（软删除：IsActive=0）
                svc.DeleteReferenceCurve(active.Id);
                var afterDelete = svc.GetActiveReferenceCurve(switchId);
                Assert(afterDelete == null, "删除后 GetActiveReferenceCurve 返回 null");

                // 重新激活
                svc.ReactivateReferenceCurve(active.Id);
                var afterReactivate = svc.GetActiveReferenceCurve(switchId);
                Assert(afterReactivate != null, "重新激活后 GetActiveReferenceCurve 返回非 null");
                Assert(afterReactivate.Id == active.Id, "重新激活的记录 ID 一致");
                Assert(afterReactivate.IsActive == true, "IsActive == true");

                // 验证：重新激活会将旧活跃的去激活
                svc.SetReferenceCurve(switchId, actions[0].Id, "另一条");
                var beforeReactivateOld = svc.GetActiveReferenceCurve(switchId);
                Assert(beforeReactivateOld.Description == "另一条", "新设定的是活跃的");

                svc.ReactivateReferenceCurve(active.Id);
                var afterSwitch = svc.GetActiveReferenceCurve(switchId);
                Assert(afterSwitch.Id == active.Id, "重新激活旧记录后它变为活跃");
                Assert(afterSwitch.Description == "测试删除和激活",
                    "旧记录被重新激活");

                // 清理
                svc.ClearReferenceCurve(switchId);

                Console.WriteLine("  [PASS] Cycle 7");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 8: 无活跃参考曲线时返回 null
        // ================================================================

        /// <summary>
        /// Cycle 8: 不存在活跃参考曲线时 GetActiveReferenceCurve 返回 null。
        /// </summary>
        static void TestNoActiveReferenceCurve()
        {
            Console.WriteLine("--- Cycle 8: 无活跃参考曲线 ---");
            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();

                // 确保某个道岔没有活跃参考曲线
                string switchId = allIds[0];
                svc.ClearReferenceCurve(switchId);

                var active = svc.GetActiveReferenceCurve(switchId);
                Assert(active == null, "无活跃参考曲线时返回 null");

                // 不存在的道岔也应返回 null
                var nonexistent = svc.GetActiveReferenceCurve("NONEXIST_SW_999");
                Assert(nonexistent == null, "不存在的道岔返回 null");

                Console.WriteLine("  无活跃参考曲线: 返回 null ✓");
                Console.WriteLine("  [PASS] Cycle 8");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Cycle 9: 参考曲线数据缓存
        // ================================================================

        /// <summary>
        /// Cycle 9: GetCachedReferenceSamples 从缓存获取参考曲线采样数据。
        /// </summary>
        static void TestReferenceCurveCache()
        {
            Console.WriteLine("--- Cycle 9: 参考曲线缓存 ---");
            try
            {
                var svc = new QueryService(GetDbPath());
                var allIds = svc.GetDistinctSwitchIds();
                string switchId = allIds[0];
                var actions = svc.GetActions(switchId: switchId);

                // 清空并设置参考曲线
                svc.ClearReferenceCurve(switchId);
                svc.SetReferenceCurve(switchId, actions[0].Id, "缓存测试");

                // 第一次加载（从数据库读取并缓存）
                var samples1 = svc.GetCachedReferenceSamples(switchId);
                Assert(samples1 != null, "GetCachedReferenceSamples 返回非 null");
                Assert(samples1.Count > 0, "缓存中有采样数据");

                // 验证采样数据结构
                foreach (var s in samples1)
                {
                    Assert(s.SampleIndex >= 0, "SampleIndex >= 0");
                    Assert(!string.IsNullOrEmpty(s.Phase), "Phase 不为空");
                    // 至少有一个非零数据字段
                    bool hasData = s.Current != 0 || s.Voltage != 0 || s.Power != 0 || s.RawValue != 0;
                }

                Console.WriteLine("  缓存数据: {0} 条采样记录", samples1.Count);
                var phases = new HashSet<string>();
                foreach (var s in samples1) phases.Add(s.Phase);
                Console.WriteLine("  相别: {0}", string.Join(", ", phases));

                // 清除参考曲线后，缓存应失效
                svc.ClearReferenceCurve(switchId);
                var samples2 = svc.GetCachedReferenceSamples(switchId);
                Assert(samples2 == null || samples2.Count == 0,
                    "清除参考曲线后缓存返回 null 或空列表");

                Console.WriteLine("  [PASS] Cycle 9");
                passed++;
            }
            catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); failed++; }
            Console.WriteLine();
        }

        // ================================================================
        // Helpers
        // ================================================================

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
