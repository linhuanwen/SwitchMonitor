# N01-6 启动提示（复制到新窗口）

---

/tdd

SwitchMonitor 是铁路道岔监测系统（WinForms + .NET 4.0 + WinXP 工控机）。多站组网功能的全部代码已实现。

## 本窗口只做一件事

**集成测试**——在 localhost 上验证全流程端到端连通。不写新功能代码，只写测试。

**前提**：N01-1 ~ N01-4 全部完成。

## 先读

- 架构概览：[01_docs/design/多站组网架构设计.md](01_docs/design/多站组网架构设计.md)
- 详细需求：[01_docs/issues/N01-6-integration-tests.md](01_docs/issues/N01-6-integration-tests.md)

## 8 个测试场景

| # | 场景 | 验证点 |
|---|------|--------|
| T1 | 单站机 API | status + events 端点正确 |
| T2 | 推送→接收 | 插入数据 10 秒内到达接收端 |
| T3 | 两台全互联 | A 推给 B，B 推给 A |
| T4 | 离线检测+补拉 | 离线 4 分钟→恢复→自动补拉 |
| T5 | 重试→放弃 | 3 次重试后放弃，不阻塞其他 |
| T6 | 手动补拉 | 按钮触发的补拉完整 |
| T7 | 并发安全 | SwitchMonitor 写 + DataForwarder 读无锁冲突 |
| T8 | gzip 往返 | 压缩→解压→反序列化字段一致 |

## 测试方式

- 用 C# NUnit（复用 `SwitchMonitor.Tests` 项目）
- 全部在 localhost 上跑，不依赖真实多机器
- DataForwarder 用 `Process.Start`/`Process.Kill` 模拟启停
- SQLite 用临时目录，测试结束清理
- 长时间运行测试（1 小时持续插入）验证无内存泄漏

## 约束

.NET 4.0, x86。测试项目已有的 NUnit 框架（`08_archive_gdi/SwitchMonitor.Tests/` 可作为参考）。不需要在 XP 上跑——开发机 .NET 4.0 即可。
编译：`dotnet build -c Release`
