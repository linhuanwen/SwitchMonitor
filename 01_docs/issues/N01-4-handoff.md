# N01-4 启动提示（复制到新窗口）

---

/tdd

SwitchMonitor 是铁路道岔监测系统（WinForms + .NET 4.0 + WinXP 工控机）。正在做多站组网功能。

## 本窗口只做一件事

**UI 集成**——把网络功能接到用户界面上：
1. 侧边栏增加一级"站点选择"下拉菜单，切换站点 = 切换 SQLite 连接
2. 站点在线状态指示灯（绿/红/灰）
3. 离线气泡告警
4. 手动补拉按钮 + 进度对话框
5. 数据清理对话框（工具菜单 → 清理历史数据）

**前提**：N01-1（SQLite）、N01-2（DataForwarder）、N01-3（接收端+探测）已完成。

## 先读

- 架构概览：[01_docs/design/多站组网架构设计.md](01_docs/design/多站组网架构设计.md)
- 详细需求：[01_docs/issues/N01-4-ui-integration.md](01_docs/issues/N01-4-ui-integration.md)

## 测试接缝

UI 层接缝是用户可见行为。测试打在：
- 切换站点 → 侧边栏刷新转辙机列表，已有 SelectedSiteId 链路复用
- 在线站点绿点，离线站点红点
- 模拟离线 → 气泡弹窗告警，恢复后自动消除
- 点补拉按钮 → 进度对话框显示"正在补拉XX站…已拉N条"
- 清理对话框显示各站数据量和最早记录 → 执行清理 → 数据不可查

## 要改的代码

**修改** `SwitchMonitor.UI/MainForm.cs`：
- 侧边栏增加站点下拉控件
- 站点切换 → 更新 SQLite 连接 → 刷新转辙机列表
- 订阅 StationMonitor 的状态变更事件 → 更新指示灯 + 气泡告警
- 补拉按钮 → 调 DataCatcher → 进度对话框
- 清理菜单项 → DataCleanupDialog

**新增** `CleanupDialog.cs` — WinForms 对话框，站点多选 + 保留天数 + 确认

## 约束

.NET 4.0, x86, WinXP。侧边栏是 WebBrowser + HTML/JS，ES5 + IE8 兼容。气泡用 NotifyIcon.BalloonTip。进度用 BackgroundWorker。站点切换复用现有 OnSwitchSelected 链路。
编译：`dotnet build -c Release`
