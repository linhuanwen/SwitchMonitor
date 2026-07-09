# Slice 4: MainForm 壳 + WebBrowser 侧边栏

> **状态（2026-07-08 代码审核）**: ✅ 已实现。偏差：分栏用 SplitContainer 固定 230px，非 TableLayoutPanel 18%/82% 比例（config 的 `sidebarWidthPercent` 未消费）；日期选择升级为日/月/年三级日历（超出规格）。

## Type

AFK

## Blocked by

Slice 1（项目脚手架）

## What to build

构建 WinForms 主窗口（深色主题），含左侧 WebBrowser 侧边栏和右侧图表预留区域。侧边栏 HTML 实现转辙机列表点击、日期选择、时间列表（降序）。C# ↔ JS 双向通信桥接。

### 主窗口布局

```
┌────────────────────────────────────────────────────────┐
│ [模拟量实时值] [开关量实时值] [日曲线] [月曲线] ...   │ ← 顶部Tab (预留，仅画UI)
├──────────┬─────────────────────────────────────────────┤
│ 侧边栏   │                                             │
│ (18%)    │            图表区域 (82%)                    │
│          │                                             │
│ 转辙机   │      (Slice 5 实现，此处放占位 Panel)        │
│ 列表     │                                             │
│          │                                             │
│ 日期     │                                             │
│ [📅]     │                                             │
│          │                                             │
│ 时间列表 │                                             │
│ 降序     │                                             │
└──────────┴─────────────────────────────────────────────┘
```

用 `TableLayoutPanel` 实现 18%/82% 分栏。

### WebBrowser 侧边栏

加载嵌入资源中的 `sidebar.html`，通过 `webBrowser.ObjectForScripting` 暴露 JS→C# 桥接。

#### C# → JS (注入数据)

```csharp
// 更新转辙机列表
webBrowser.Document.InvokeScript("setSwitchGroups", new object[] { groupsJson });
// 更新时间列表
webBrowser.Document.InvokeScript("setTimeList", new object[] { timesJson });
```

#### JS → C# (用户操作)

```csharp
[ComVisible(true)]
public class JSBridge
{
    public void SelectSwitch(string switchId);     // window.external.SelectSwitch("1-1")
    public void SelectTime(string timestamp);       // window.external.SelectTime("1776243701")
    public void ToggleSeries(string name, string visible);
}
```

### sidebar.html 功能

1. **转辙机列表**：从 config.json 加载，点击高亮选中
2. **日期选择**：显示选中转辙机有数据的日期列表，默认选最近一天
3. **时间列表**：选中日期的所有动作时间，**降序排列**（最新在上），首项自动选中
4. **深色主题**：背景 `#1a1a2e`，文字 `#e0e0e0`，选中高亮 `#3a3a5e`

### IE8 兼容

- 所有 JS 使用 ES5 语法（var / function / 不用箭头函数 / 不用模板字符串）
- 禁止 CSS3（无 border-radius / box-shadow / gradient）
- 使用 table 布局或 float 布局（无 flexbox / grid）

## Acceptance criteria

- [ ] MainForm 启动窗口默认 1280×900 或全屏
- [ ] TableLayoutPanel 18%/82% 分栏，窗口缩放时比例保持
- [ ] 侧边栏显示转辙机列表、日期、时间列表三区域
- [ ] 点击转辙机 → C# 收到 `SelectSwitch` 回调 → 日期列表更新
- [ ] 选择日期 → 时间列表降序更新
- [ ] 时间列表首项自动选中
- [ ] 未选转辙机时显示提示文字"请选择转辙机"
- [ ] 窗口标题栏显示"道岔监控数据查看系统"
- [ ] WebBrowser 不显示滚动条、边框（外观融入 WinForms）

## Further notes

- HTML 文件设为**嵌入资源 (Embedded Resource)**，运行时通过 `GetManifestResourceStream` 读取，绕过 IE 跨域限制
- 或在 `DocumentText` 属性中直接设置 HTML 字符串（包含内联 CSS 和 JS）
- 桥接类 `JSBridge` 标记 `[ComVisible(true)]`，MainForm 标记 `[ComVisible(true)]`
- 字体：宋体 12px（工控机标准），颜色以 config.json 中 chartColors 为准
