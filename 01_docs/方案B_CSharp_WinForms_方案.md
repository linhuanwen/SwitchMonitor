# 方案B：C# .NET 4.0 WinForms + WebBrowser 内嵌方案（修订版 v2.0）

## 变更记录

| 版本 | 日期       | 变更内容 |
|------|------------|----------|
| v1.0 | 2026-07-05 | 初版：三阶段路线（ECharts→Hybrid→GDI+） |
| v2.0 | 2026-07-05 | **方案变更**：Phase 1 图表库从 ECharts 改为 Highcharts 2.x（项目已有，IE8 VML 兼容），大幅缩短 MVP 周期 |

---

## 一、架构概览

```
┌──────────────────────────────────────────────────────────┐
│               工控机 (WinXP / 研华610H)                     │
│                                                          │
│  ┌──────────┐   ┌──────────────┐                         │
│  │ 既有监测  │──▶│ switchcurve/ │                         │
│  │ 软件     │   │ (原始.dat)   │                         │
│  └──────────┘   └──────┬───────┘                         │
│                        │                                │
│  ┌─────────────────────┘                                │
│  │                                                      │
│  ▼                                                      │
│  ┌──────────────────────────────────────────────────┐   │
│  │         SwitchMonitor.exe  (.NET 4.0 WinForms)    │   │
│  │                                                  │   │
│  │  ┌────────────────┐  ┌────────────────────────┐  │   │
│  │  │  DataParser.dll │  │  WebBrowser Control     │  │   │
│  │  │  (C# 数据解析)  │  │  (内嵌IE8引擎)          │  │   │
│  │  │                │  │                        │  │   │
│  │  │ • CSM2010解析  │  │  ┌──────────────────┐  │  │   │
│  │  │ • 索引管理     │  │  │  HTML + JS       │  │  │   │
│  │  │ • 定时扫描     │  │  │  (Highcharts 2.x)│  │  │   │
│  │  │ • JSON导出     │  │  │  折线图 + Tooltip │  │  │   │
│  │  └────────────────┘  │  └──────────────────┘  │  │   │
│  │                      └────────────────────────┘  │   │
│  │                                                  │   │
│  │  ┌────────────────────────────────────────────┐  │   │
│  │  │  C# ↔ JS 桥接 (ObjectForScripting)         │  │   │
│  │  │  JS调用: window.external.LoadCurve(id,ts)  │  │   │
│  │  │  C#注入: webBrowser.Document.InvokeScript() │  │   │
│  │  └────────────────────────────────────────────┘  │   │
│  └──────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────┘
```

## 二、技术栈

| 层         | 技术选型                       | 说明                                  |
|------------|-------------------------------|---------------------------------------|
| 框架       | .NET Framework 4.0            | WinXP原生支持，预装或免费分发          |
| 语言       | C# 4.0                        | Visual Studio 2010 兼容               |
| UI容器     | WinForms Form                 | 经典Windows窗口                       |
| 图表渲染   | WebBrowser Control + Highcharts 2.x | **项目已有**，IE8 VML 降级渲染 |
| 图表备选   | GDI+ 自绘曲线                 | 如果Highcharts效果不满足则启用         |
| 数据解析   | C# BinaryReader               | 解析CSM2010 .dat                      |
| 数据格式   | JSON (中间层)                 | 使用Newtonsoft.Json或内置JavaScriptSerializer |
| 文件监控   | FileSystemWatcher             | .NET原生，XP上稳定                    |
| 配置管理   | config.json                   | JSON配置文件                          |

## 三、核心决策：图表渲染方案（已确定）

### ✅ 确定方案：IE8 + Highcharts 2.x

经过对现有项目的检查，**Highcharts 2.2.1 (2012-03-15) 已在既有监测软件中使用**，位于 `本地接收目录/DataFile/Station_*/js/highcharts.js`，并在 `samplevbar.html` 中验证了柱状图渲染。

| 判断项 | 结论 |
|--------|------|
| IE8 兼容性 | ✅ Highcharts 2.x 支持 IE8，通过 **VML** (Vector Markup Language) 降级渲染 |
| SVG 支持 | ❌ IE8 不支持 SVG，Highcharts 自动 fallback 到 VML |
| 折线图 | ✅ `type: 'spline'` 或 `type: 'line'` 均可 |
| Tooltip | ✅ `tooltip: { formatter: ... }` 支持自定义 |
| 图例切换 | ✅ `legend: { ... }` 点击图例即可显隐系列 |
| 十字光标 | ⚠️ 需要自行扩展，通过 `mousemove` + `plotX` 实现 |
| 阈值线 | ✅ `yAxis.plotLines` 或 `series.markLine` 可实现红色虚线 |
| 现有素材 | ✅ 项目已有 highcharts.js + jquery.js，可直接嵌入 WebBrowser |

### Highcharts 2.x 在 WebBrowser 中的关键用法

```javascript
// 折线图（电流曲线示例）
new Highcharts.Chart({
    chart: {
        renderTo: 'chart1',
        type: 'spline',           // 平滑曲线，也可用 'line'
        backgroundColor: '#1a1a2e',
        animation: false           // 工控机上关闭动画，保证流畅
    },
    title: { text: '道岔动作电流曲线', style: { color: '#e0e0e0', fontSize: '12px' } },
    xAxis: {
        title: { text: '时间(秒)' },
        min: 0,
        max: 14,                   // 动态：有超14s数据时扩展到30s
        labels: { style: { color: '#888' } },
        gridLineColor: '#333355'
    },
    yAxis: {
        title: { text: '电流(A)' },
        min: 0,
        labels: { style: { color: '#888' } },
        gridLineColor: '#333355',
        plotLines: [{              // 报警阈值线
            color: '#FF0000',
            dashStyle: 'dash',
            value: 2.0,
            width: 1,
            label: { text: '2.0A', style: { color: '#FF0000' } }
        }]
    },
    tooltip: {
        shared: true,
        crosshairs: true,          // IE8 下可能退化为简单竖线
        formatter: function() {
            var s = '<b>' + this.x.toFixed(2) + '秒</b>';
            $.each(this.points, function(i, pt) {
                s += '<br/><span style="color:' + pt.series.color + '">' +
                     pt.series.name + '</span>: ' + pt.y.toFixed(3);
            });
            return s;
        }
    },
    legend: {
        align: 'center',
        layout: 'horizontal',
        style: { color: '#888' }
    },
    plotOptions: {
        spline: {
            lineWidth: 1.5,
            marker: { enabled: false }  // 790个点，关闭标记点提升性能
        }
    },
    series: [{
        name: 'A相电流',
        color: '#FF4444',
        data: [[0, 5.6], [0.04, 1.4], ...]  // [[x, y], ...]
    }, {
        name: 'B相电流',
        color: '#44FF44',
        data: [...]
    }, {
        name: 'C相电流',
        color: '#4488FF',
        data: [...]
    }]
});
```

### 备选：GDI+ 自绘（仅在 Highcharts 不满足时启用）

触发条件：
- IE8 VML 渲染性能不足（数据点过多时卡顿）
- Highcharts 2.x 不支持某些必须的交互（如十字光标精度不够）
- 用户对图表外观有更高要求

## 四、目录结构

```
d:\SwitchMonitor\
├── SwitchMonitor.sln                    # VS2010 解决方案
├── config.json                          # 系统配置文件
│
├── SwitchMonitor.UI\                    # WinForms 主项目
│   ├── Program.cs                       # 入口
│   ├── MainForm.cs                      # 主窗口 (布局管理)
│   ├── MainForm.Designer.cs
│   ├── Controls\
│   │   ├── SidebarControl.cs            # 左侧面板 (WebBrowser)
│   │   ├── ChartPanelHost.cs            # 图表容器 (WebBrowser，嵌入 HTML)
│   │   └── ToolbarControl.cs            # 顶部工具栏 (WebBrowser)
│   ├── Html\
│   │   ├── sidebar.html                 # 侧边栏HTML
│   │   ├── charts.html                  # 2x2 图表页面 (Highcharts)
│   │   ├── toolbar.html                 # 工具栏HTML
│   │   └── styles.css                   # 深色主题样式
│   ├── Js\
│   │   ├── jquery.js                    # jQuery 1.x (项目已有)
│   │   └── highcharts.js               # Highcharts 2.2.1 (项目已有)
│   └── Resources\
│       └── app.ico
│
├── SwitchMonitor.Data\                  # 数据层
│   ├── DatParser.cs                     # CSM2010 二进制解析
│   ├── CurveData.cs                     # 数据模型
│   ├── IndexManager.cs                  # 索引+中间JSON
│   └── FileWatcherService.cs            # 文件监控服务
│
├── parsed_data\                         # 中间数据 (运行时生成)
│   ├── index.json
│   └── {转辙机ID}\
│       └── YYYY-MM-DD.json
│
└── output\                              # 编译输出
    └── SwitchMonitor.exe
```

> **注**：相比 v1.0，移除了 `SwitchMonitor.Charts/` 项目（GDI+ 图表引擎），Phase 1 全部图表由 Highcharts 渲染。GDI+ 图表相关代码（CurveChart.cs, AxisRenderer.cs 等）推迟到 Phase 2（仅在必要时）。

## 五、核心类设计

### 5.1 数据模型

```csharp
// CurveData.cs
public class SwitchEvent
{
    public long Timestamp { get; set; }
    public string DateTimeStr { get; set; }
    public string Direction { get; set; }      // "反位到定位" / "定位到反位"
    public double Duration { get; set; }        // 秒
    public double SampleInterval { get; set; }  // 采样间隔(~0.04s)
    public List<double> CurrentA { get; set; }
    public List<double> CurrentB { get; set; }
    public List<double> CurrentC { get; set; }
    public List<double> Power { get; set; }
}

public class DayIndex
{
    public string SwitchId { get; set; }
    public string Date { get; set; }
    public List<long> Timestamps { get; set; }
}
```

### 5.2 C# ↔ JS 桥接

```csharp
// MainForm.cs
[ComVisible(true)]
public class JSBridge
{
    private MainForm form;

    public JSBridge(MainForm form) { this.form = form; }

    // JS 调用 C#: window.external.SelectSwitch("1-1")
    public void SelectSwitch(string switchId)
    {
        form.Invoke(new Action(() => form.OnSwitchSelected(switchId)));
    }

    // JS 调用 C#: window.external.SelectTime("1776243701")
    public void SelectTime(string timestamp)
    {
        form.Invoke(new Action(() => form.OnTimeSelected(long.Parse(timestamp))));
    }

    // JS 调用 C#: window.external.ToggleSeries("currentA", "true")
    public void ToggleSeries(string seriesName, string visible)
    {
        form.Invoke(new Action(() =>
            form.OnSeriesToggled(seriesName, visible == "true")));
    }
}

// 初始化
webBrowser.ObjectForScripting = new JSBridge(this);

// C# 调用 JS: 向侧边栏推送时间列表
webBrowser.Document.InvokeScript("setTimeList", new object[] { timesJson });

// C# 调用 JS: 向图表注入曲线数据
webBrowser.Document.InvokeScript("loadChartData", new object[] { chartDataJson });
```

### 5.3 数据管道

```csharp
// 数据流：
// switchcurve/*.dat → DatParser → IndexManager → JSON → WebBrowser JS
//
// MainForm 分发逻辑：
//   1. 用户点击"1-1" → OnSwitchSelected("1-1")
//   2. 读取 parsed_data/1-1/index.json → 返回日期列表
//   3. 用户选择日期 → 读取 YYYY-MM-DD.json → 返回时间列表
//   4. 自动选中最近时间 → 读取该时间 + 上一时间的曲线数据
//   5. 调用 JS loadChartData() → Highcharts 渲染 2x2 图表
```

### 5.4 FileSystemWatcher 监控

```csharp
// FileWatcherService.cs
public class FileWatcherService
{
    private FileSystemWatcher watcher;
    private Timer fallbackTimer;  // 定时轮询兜底（XP上FSN有时丢事件）

    public void Start(string path)
    {
        watcher = new FileSystemWatcher(path, "*.dat");
        watcher.Created += OnNewFile;
        watcher.Changed += OnFileChanged;
        watcher.EnableRaisingEvents = true;

        // 兜底：每60秒全量扫描一次
        fallbackTimer = new Timer(_ => FullScan(), null, 60000, 60000);
    }

    private void OnNewFile(object sender, FileSystemEventArgs e)
    {
        // 解析新.dat → 更新 parsed_data/
        // BeginInvoke 通知主线程刷新UI
    }
}
```

## 六、数据流

```
switchcurve/SwitchCurve(*).dat        ← 既有监测软件持续写入
    │  FileSystemWatcher + 定时轮询兜底
    ▼
DatParser.Parse(path)
    │  读取CSM2010二进制 → 解析 event 记录
    │  识别 phase bitmask → 分拣 A/B/C相电流 和 功率
    │  每事件 790 采样点 × 4 通道
    ▼
IndexManager.Update(eventList)
    │  按 转辙机ID + 日期 分组
    │  写入 parsed_data/{switchId}/YYYY-MM-DD.json
    │  更新 parsed_data/index.json
    ▼
MainForm.LoadDay(switchId, date)
    │  读取 parsed_data/{switchId}/YYYY-MM-DD.json
    │  → 构建 List<SwitchEvent>，按时间降序
    ▼
MainForm 分发数据到 WebBrowser
    ├── 侧边栏: InvokeScript("setTimeList", timesJson)
    │     时间列表降序展示，首项自动选中
    └── 图表区: InvokeScript("loadChartData", chartDataJson)
          包含：currentEvent、prevEvent 的电流+功率数据
          JS 端创建 4 个 Highcharts 实例渲染 2x2 网格
```

## 七、配置文件 (config.json)

```json
{
  "switchGroups": [
    {"id": "1-1", "label": "1-1", "dataFileIndex": 0},
    {"id": "1-X", "label": "1-X", "dataFileIndex": 4},
    {"id": "3-1", "label": "3-1", "dataFileIndex": 8},
    {"id": "3-X", "label": "3-X", "dataFileIndex": 12},
    {"id": "2-1", "label": "2-1", "dataFileIndex": 16},
    {"id": "2-X", "label": "2-X", "dataFileIndex": 20},
    {"id": "4-1", "label": "4-1", "dataFileIndex": 24},
    {"id": "4-X", "label": "4-X", "dataFileIndex": 28}
  ],
  "dataSourceDir": "C:\\监测数据\\switchcurve",
  "parsedDataDir": ".\\parsed_data",
  "scanInterval": 5,
  "alarmThresholds": {
    "current": { "enabled": true, "value": 2.0, "unit": "A" },
    "power":   { "enabled": true, "value": 1.5, "unit": "KW" }
  },
  "chartColors": {
    "currentA": "#FF4444",
    "currentB": "#44FF44",
    "currentC": "#4488FF",
    "power": "#44FF44",
    "thresholdLine": "#FF0000",
    "background": "#1a1a2e",
    "gridLine": "#333355",
    "textColor": "#888888"
  },
  "ui": {
    "sidebarWidthPercent": 18,
    "dateFormat": "yyyy/MM/dd",
    "xAxisDefaultMax": 14,
    "xAxisExtendedMax": 30
  }
}
```

## 八、主窗口布局

```
┌────────────────────────────────────────────────────────┐
│ [模拟量实时值] [开关量实时值] [日曲线] [月曲线] ...   │ ← 顶部Tab (预留)
├──────────┬─────────────────────────────────────────────┤
│ 转辙机   │ ┌──────────┬──────────┬─────────────────┐  │
│ 列表     │ │□报警上限  │□报警下限  │□光标 □量程自适应│  │
│ ┌──────┐ │ │□图例     │[自动计算标准曲线]          │  │
│ │ 1-1  │ │ ├──────────┴──────────┴─────────────────┤  │
│ │ 1-X  │ │ │□电流A □电流B □电流C □功率            │  │
│ │ 3-1  │ │ │□电流A标准 □电流B标准 □电流C标准 ...  │  │
│ │ 3-X  │ │ ├────────────────────────────────────────┤  │
│ │ 2-1  │ │ │[道岔动作曲线] [道岔动作时长曲线]      │  │
│ │ 2-X  │ │ ├──────────────┬─────────────────────────┤  │
│ │ 4-1  │ │ │  左上:电流    │    右上:功率            │  │
│ │ 4-X  │ │ │  (上一时间)   │    (上一时间)           │  │
│ └──────┘ │ ├──────────────┼─────────────────────────┤  │
│          │ │  左下:电流    │    右下:功率            │  │
│ 日期:    │ │  (当前选中)   │    (当前选中)           │  │
│ [📅]     │ ├──────────────┴─────────────────────────┤  │
│          │ │                         [打印] [导出]   │  │
│ 时间列表 │ └─────────────────────────────────────────┘  │
│ 07:18:56 │                                              │
│ 06:16:30 │                                              │
│ 05:42:15 │                                              │
└──────────┴──────────────────────────────────────────────┘
```

> 注：与 v1.0 布局一致，但所有渲染区域（侧边栏、工具栏、图表）均由 WebBrowser 控件承载。每个图表是 `charts.html` 中的一个 `<div>` + 独立 Highcharts 实例。

## 九、Highcharts IE8 兼容设计要点

### 9.1 必须遵守的 JS 规范

```javascript
// ✅ 正确：ES5 语法
var data = [];  // 不用 let
function render() {}  // 不用箭头函数
for (var i = 0; i < len; i++) {}  // 不用 for...of
$.each(arr, function(i, v) {});  // jQuery 兜底

// ❌ 禁止：ES6+ 语法
let x = 1;          // IE8 不支持
const y = 2;        // IE8 不支持
arr => arr[0];      // 箭头函数
`template ${var}`;  // 模板字符串
class Chart {}      // class
```

### 9.2 Highcharts VML 性能优化

- **关闭动画**：`animation: false`（VML 渲染动画极慢）
- **关闭数据点标记**：`marker: { enabled: false }`（790 个点 × 3 系列 = 2370 个标记，VML 无法承受）
- **关闭阴影**：`shadow: false`
- **数据降采样**：如果 790 点仍卡，在前端做 stride-N 降采样（如每 4 点取 1 点 = 198 点）
- **tooltip 延迟**：使用 `setTimeout` 防抖，避免快速移动鼠标触发频繁重绘

### 9.3 已知限制

| 限制项 | 影响 | 缓解措施 |
|--------|------|----------|
| VML 渲染速度 | 初始渲染 790 点可能 1-2 秒 | 关闭动画和标记点 |
| 十字光标精度 | IE8 下 `crosshairs` 可能不精确 | 自行 mousemove 绘制竖线 |
| 无 SVG 导出 | 导出图片分辨率低 | 打印功能推迟，或走 C# 端 GDI+ 打印 |
| CSS3 不支持 | 侧边栏无圆角/阴影 | 接受平面外观，工控机不追求视觉 |

## 十、优势与劣势

### ✅ 优势
- **真正的 .exe 程序**：双击运行，用户习惯好
- **Highcharts 已有素材**：项目已包含 highcharts.js，无需从零引入
- **IE8 兼容已验证**：现有 `samplevbar.html` 在 IE8 上已验证可用
- **开发周期大幅缩短**：∆ 因 Highcharts 接手图表渲染，省去 8-12 天 GDI+ 开发
- **图表交互够用**：tooltip、图例切换、阈值线 Highcharts 原生支持
- **部署简单**：一个 .exe + 嵌入资源，复制即用，无需安装浏览器
- **内存可控**：WinForms 30-50MB，远低于外部浏览器
- **FileSystemWatcher 稳定**：.NET 4.0 + XP 久经考验

### ❌ 劣势
- **VML 渲染性能有限**：需做降采样优化，图表交互不如原生流畅
- **JS 必须全量 ES5**：开发效率和代码可读性受限
- **Highcharts 2.x 功能老旧**：部分高级交互（缩放、平移）不支持
- **UI 调整需重编译**：WinForms 布局修改需编译，不如 HTML 灵活
- **仅限 Windows**：无法跨平台

## 十一、开发工作量估算（修订）

| 模块                       | 工作量   | 说明                              |
|----------------------------|----------|-----------------------------------|
| DatParser (CSM2010解析)    | 3-5天    | 需逆向二进制格式                   |
| IndexManager               | 1-2天    | JSON读写+索引维护                  |
| FileWatcherService         | 1天      | FileSystemWatcher + 定时回退       |
| MainForm 布局              | 2-3天    | TableLayoutPanel 精确分割          |
| Sidebar HTML+JS            | 2天      | 转辙机列表、日期、时间列表          |
| charts.html + Highcharts   | 3-4天    | 2x2 网格、tooltip、阈值线、系列切换 |
| Toolbar HTML+JS            | 1-2天    | 复选框、按钮                       |
| C#↔JS 桥接集成             | 2-3天    | ObjectForScripting + 数据注入       |
| 交互联动逻辑               | 2-3天    | 左侧→图表，复选框→系列显隐         |
| 配置文件管理               | 1天      | JSON序列化/反序列化                |
| XP 实体机适配调试          | 3-5天    | VML性能调优、IE8兼容修复            |
| **总计**                   | **21-31天** | ↓ 比 v1.0 省 7-6 天              |

> 对比：v1.0 预估 **28-37天**（含 GDI+ 图表引擎）  
> 方案A 预估 **12-18天**（Python+Flask+ECharts，但需装 Firefox）

## 十二、实施路线

### Phase 1（唯一交付版本）：WebBrowser + Highcharts 全量

```
Week 1: 数据层
  ├── DatParser.cs — CSM2010 二进制解析
  ├── CurveData.cs — 数据模型
  └── IndexManager.cs — JSON 索引 + 日数据

Week 2: UI 框架
  ├── MainForm.cs — TableLayoutPanel 主布局
  ├── Sidebar HTML+JS — 转辙机列表、日期、时间列表
  ├── Charts HTML+JS — 2x2 Highcharts 图表
  └── Toolbar HTML+JS — 复选框、按钮

Week 3: 集成
  ├── C#↔JS 桥接 — ObjectForScripting + InvokeScript
  ├── 联动逻辑 — 点击→加载→渲染
  └── FileWatcherService — 监控+兜底扫描

Week 4+: 测试 & 调优
  ├── XP 实体机兼容测试
  ├── VML 性能优化（降采样、关闭标记）
  ├── 边界情况（无数据、异常数据）
  └── 打包部署
```

### Phase 2（预留，仅必要时）：GDI+ 图表引擎

触发条件：Phase 1 在 XP 上 VML 性能无法接受。

工作内容：开发 `SwitchMonitor.Charts/` 项目，包含 CurveChart.cs、AxisRenderer.cs、TooltipRenderer.cs 等。预估额外 8-12 天。

## 十三、XP 部署清单

| 组件                   | 要求                              | 状态 |
|------------------------|-----------------------------------|------|
| .NET Framework 4.0     | XP SP3                            | ✅ XP 预装或免费安装 |
| WebBrowser (IE8)       | XP 内置                           | ✅ 无需额外安装 |
| Highcharts 2.x         | 嵌入资源，随 exe 分发             | ✅ 项目已有 |
| jQuery 1.x             | 嵌入资源，随 exe 分发             | ✅ 项目已有 |
| 文件系统权限           | 读写 switchcurve/ + parsed_data/  | ✅ 本地运行 |
| 屏幕分辨率             | 最低 1024×768 (建议 1280×1024+)   | ⚠️ 需确认 |

---

*文档版本: v2.0 | 2026-07-05 | 变更：图表方案从 ECharts→Highcharts 2.x（IE8 兼容，项目已有）*
