# N01-6: 集成测试 — 端到端连通性验证

> **状态**: 待实现
> **依赖**: N01-1, N01-2, N01-3, N01-4, N01-5
> **阻塞**: 无（最后一道）

## Type

Test

## What to build

验证完整的网络栈在工作站环境下正确运行。

### 测试场景

#### T1: 单站机 DataForwarder API
- 启动 DataForwarder
- `GET /api/status` → 验证 stationId, lastTimestamp
- 往 SQLite 插入测试数据 → 等 2 秒 → `GET /api/events?since=0` → 验证返回正确事件

#### T2: 推送 → 接收 端到端
- 启动站机 DataForwarder（订阅 localhost 接收端）
- 启动接收端 HttpListener
- 往站机 SQLite 插入测试数据
- 验证接收端 10 秒内收到数据并正确写入 SQLite

#### T3: 同班组全互联（2 台模拟）
- 启动两个 DataForwarder 互相订阅
- 在 A 的 SQLite 中插入数据
- 验证 B 收到 A 的推送
- 在 B 的 SQLite 中插入数据
- 验证 A 收到 B 的推送

#### T4: 离线检测 + 自动补拉
- 启动站机和总终端
- 往站机插入 5 条数据 → 验证推送成功
- 停掉站机 DataForwarder
- 验证 4 分钟后总终端标记站机离线
- 往站机 SQLite 插入 3 条数据（模拟离线期间新产生的数据）
- 重启站机 DataForwarder
- 验证总终端检测到恢复 → 自动补拉 3 条数据

#### T5: 重试 → 放弃
- 启动站机 DataForwarder（订阅一个不存在的 IP）
- 往 SQLite 插入数据
- 验证重试 3 次后放弃，日志记录失败
- 验证其他正常 subscriber 不受影响

#### T6: 手动补拉
- 模拟数据缺口（总终端 lastTimestamp 回退）
- 点击 UI 补拉按钮
- 验证补拉完成后数据完整

#### T7: SQLite 并发安全
- SwitchMonitor 写入事件的同时，DataForwarder 扫描同表
- 验证不出现锁冲突或数据损坏

#### T8: gzip 往返
- 原始 SwitchEvent JSON → gzip 压缩 → POST → 解压 → 反序列化
- 验证所有字段值一致

## Acceptance criteria

- [ ] 8 个测试场景全部通过
- [ ] 测试可以在单台开发机上用 localhost 完成（不依赖真实多机器环境）
- [ ] 长时间运行（1 小时持续插入数据）不出现内存泄漏或崩溃
- [ ] 推送延迟（插入 → 接收端落地）≤ 10 秒

## Further notes

- 测试脚本用 C# NUnit 或 Python，推荐 C# 以复用现有测试基础设施
- 测试 SQLite 数据库使用临时目录，测试结束清理
- DataForwarder 用 `Process.Start` 启动和 `Process.Kill` 停止来模拟启停场景
