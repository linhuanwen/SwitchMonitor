# 方案A：Python + Flask 本地服务 + 浏览器前端

## 一、架构概览

```
┌─────────────────────────────────────────────────────┐
│                  工控机 (WinXP / 研华610H)              │
│                                                     │
│  ┌──────────┐   ┌──────────────┐   ┌────────────┐  │
│  │ 既有监测  │──▶│ switchcurve/ │──▶│ Python 服务 │  │
│  │ 软件     │   │ (原始.dat)   │   │ (Flask)    │  │
│  └──────────┘   └──────────────┘   └─────┬──────┘  │
│                                          │         │
│                    ┌─────────────────────┘         │
│                    ▼                               │
│           ┌──────────────┐                         │
│           │ parsed_data/ │  自建中间JSON数据        │
│           │ (索引+日数据)│                         │
│           └──────┬───────┘                         │
│                  ▼                                 │
│           ┌──────────────┐                         │
│           │ Flask API    │  端口 127.0.0.1:8800    │
│           │ /api/*       │                         │
│           └──────┬───────┘                         │
│                  ▼                                 │
│  ┌──────────────────────────────────────────┐     │
│  │     Firefox ESR 52 / Chrome 49           │     │
│  │     ┌──────────────────────────────────┐ │     │
│  │     │  HTML + ECharts + Vanilla JS     │ │     │
│  │     │  单页面应用 (SPA)                │ │     │
│  │     └──────────────────────────────────┘ │     │
│  └──────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────┘
```

## 二、技术栈

| 层       | 技术选型                                | 说明                               |
|----------|----------------------------------------|------------------------------------|
| 后端     | Python 3.4.x                            | WinXP最后支持版本                   |
| Web框架  | Flask 1.0.x (兼容Py3.4)                 | 轻量级，无需复杂依赖                |
| 图表     | ECharts 4.x (兼容ES5)                   | Apache 2.0开源，工业曲线最佳实践    |
| 前端     | 原生 HTML + CSS + Vanilla JS            | 无构建工具，适配老浏览器            |
| 数据格式 | JSON (中间层)                           | 从CSM2010 .dat解析而来             |
| 文件监控 | 定时轮询 (os.scandir)                    | 比watchdog在XP上更稳定             |
| 浏览器   | Firefox ESR 52 或 Chrome 49             | 需预装在工控机                      |

## 三、目录结构

```
d:\SwitchMonitor\
├── app.py                  # Flask 主入口，启动服务+打开浏览器
├── config.json             # 系统配置文件
├── requirements.txt        # Python 依赖清单
│
├── parser/                 # 数据解析模块
│   ├── __init__.py
│   ├── dat_reader.py       # CSM2010 .dat 二进制解析器
│   └── indexer.py          # 扫描switchcurve/ → 建立索引+解析JSON
│
├── static/                 # 前端静态资源
│   ├── index.html          # 主页面
│   ├── css/
│   │   └── main.css        # 全局样式（深色主题）
│   ├── js/
│   │   ├── app.js          # 主逻辑（初始化、事件绑定）
│   │   ├── chart.js        # ECharts图表管理（2x2网格）
│   │   ├── sidebar.js      # 左侧面板交互
│   │   └── toolbar.js      # 工具栏交互
│   └── lib/
│       └── echarts.min.js  # ECharts 4.x（离线部署）
│
├── parsed_data/            # 解析后的中间数据（自动生成）
│   ├── index.json          # 全局索引
│   └── {转辙机ID}/
│       └── YYYY-MM-DD.json # 每日数据
│
└── logs/                   # 运行日志
    └── monitor.log
```

## 四、数据流详解

### 4.1 原始数据格式 (CSM2010)

```
.dat 文件二进制头部: "CSM2010" (8字节magic)
CSV 导出后格式（已有素材）:
  timestamp, datetime, phase, s0, s1, ..., s789
  - phase: 16777216(A相), 33554432(B相), 50332416(C相), 0(功率)
  - s0~s789: 790个采样点，约0.04s/点，覆盖0~31.6秒
  - 电流文件: 1000事件/3相 × 3 = 3000行
  - 功率文件: 3000事件 × 1相 = 3000行
  - 电流与功率文件成对出现: SwitchCurve(0)↔SwitchCurve(3)
```

### 4.2 数据管道

```
switchcurve/*.dat                   (既有监测软件写入)
    │
    ▼  定时扫描 (每config.scanInterval秒)
parser/indexer.py
    │  1. 发现新文件 or 修改时间变化
    │  2. 解析.dat → 提取每个拨动事件
    │  3. 按转辙机ID + 日期分组
    │  4. 写入 parsed_data/{转辙机ID}/YYYY-MM-DD.json
    │  5. 更新 parsed_data/index.json
    │
    ▼
Flask API (127.0.0.1:8800)
    │
    ├── GET /api/switch-groups              → 返回转辙机组列表
    ├── GET /api/times?group=1-1&date=2026-06-29 → 返回当天所有拨动时间
    ├── GET /api/curve?group=1-1&time=1776243701 → 返回该拨动的电流+功率数据
    └── GET /api/config                     → 返回系统配置(阈值等)
    │
    ▼
浏览器前端 (Firefox ESR 52)
    │  fetch() API 调用
    │  ECharts 渲染 2x2 图表网格
    │
```

### 4.3 中间JSON数据结构

**index.json:**
```json
{
  "1-1": {
    "2026-06-29": ["1776243701", "1776286259", "1776328918"],
    "2026-06-28": ["1776157301"]
  },
  "1-X": { ... }
}
```

**日数据文件 (YYYY-MM-DD.json):**
```json
[
  {
    "timestamp": 1776243701,
    "datetime": "2026-04-15 17:01:41",
    "direction": "反位到定位",
    "duration": 11.80,
    "sampleInterval": 0.04,
    "phases": {
      "A": [5.647, 1.451, 1.412, ...],
      "B": [5.529, 1.451, 1.412, ...],
      "C": [2.078, 1.490, 1.451, ...]
    },
    "power": [3.020, 0.294, 0.275, ...]
  }
]
```

## 五、核心代码骨架

### 5.1 app.py 启动逻辑

```python
# -*- coding: utf-8 -*-
import json
import threading
import time
import webbrowser
from flask import Flask, jsonify, request, send_from_directory
from parser.indexer import Indexer

app = Flask(__name__, static_folder='static', static_url_path='')

# 加载配置
with open('config.json', 'r', encoding='utf-8') as f:
    config = json.load(f)

indexer = Indexer(config['dataSourceDir'], config['parsedDataDir'])

# ===== API 路由 =====
@app.route('/api/switch-groups')
def api_switch_groups():
    return jsonify(config['switchGroups'])

@app.route('/api/times')
def api_times():
    group = request.args.get('group')
    date = request.args.get('date')
    times = indexer.get_times(group, date)
    return jsonify(times)

@app.route('/api/curve')
def api_curve():
    group = request.args.get('group')
    timestamp = request.args.get('time')
    data = indexer.get_curve(group, int(timestamp))
    return jsonify(data)

@app.route('/api/config')
def api_config():
    return jsonify({
        'alarmThresholds': config['alarmThresholds'],
        'chartColors': config['chartColors']
    })

# ===== 后台扫描线程 =====
def scan_loop():
    while True:
        indexer.scan()
        time.sleep(config['scanInterval'])

if __name__ == '__main__':
    t = threading.Thread(target=scan_loop, daemon=True)
    t.start()
    webbrowser.open('http://127.0.0.1:8800')
    app.run(host='127.0.0.1', port=8800, debug=False)
```

### 5.2 前端 ECharts 图表配置

```javascript
// 单个图表的通用配置
function createChartOption(title, series, thresholdY, xMax) {
    return {
        backgroundColor: '#1a1a2e',
        title: {
            text: title,
            textStyle: { color: '#e0e0e0', fontSize: 12 },
            left: 'center'
        },
        tooltip: {
            trigger: 'axis',
            formatter: function(params) { /* 多系列tooltip */ }
        },
        legend: {
            data: series.map(function(s) { return s.name; }),
            bottom: 0,
            textStyle: { color: '#888' }
        },
        grid: { top: 40, bottom: 40, left: 50, right: 20 },
        xAxis: {
            type: 'value',
            name: '时间(秒)',
            min: 0,
            max: xMax,
            axisLabel: { color: '#888' }
        },
        yAxis: {
            type: 'value',
            name: title.indexOf('功率') >= 0 ? 'KW' : '安培',
            axisLabel: { color: '#888' }
        },
        series: series,
        // 红色阈值线
        markLine: thresholdY ? {
            silent: true,
            symbol: 'none',
            lineStyle: { color: '#FF0000', type: 'dashed' },
            data: [{ yAxis: thresholdY, label: { formatter: thresholdY + 'A' } }]
        } : undefined
    };
}
```

## 六、优势与劣势

### ✅ 优势
- **开发效率高**：Python + JS 生态成熟，ECharts 图表效果专业
- **图表交互强大**：ECharts 原生支持十字光标、tooltip、缩放、图例切换
- **易于调试**：浏览器 F12 开发者工具直接调试
- **跨平台**：未来如果升级工控机到 Win10/Linux，代码几乎不变
- **前后端分离**：数据管道可独立运行，前端可单独迭代

### ❌ 劣势
- **依赖浏览器**：必须在工控机上安装 Firefox ESR 52 或 Chrome 49
- **启动方式**：用户看到的是浏览器而非 .exe 程序，体验不够"原生"
- **Python 环境**：需安装 Python 3.4 + pip 依赖
- **XP 环境退化**：Python 3.4 已 EOL，部分 pip 包可能难以安装
- **浏览器兼容风险**：Firefox 52 不支持 ES6 语法（let/const/箭头函数等），需全量 ES5 编写
- **端口占用风险**：8800 端口可能被其他程序占用

## 七、XP 兼容性清单

| 组件               | 要求               | 状态 |
|--------------------|--------------------|------|
| Python             | 3.4.4 (XP最终版)   | ⚠️ 需从python.org下载离线包 |
| Flask              | 1.0.x (兼容3.4)    | ⚠️ 需离线pip安装 |
| ECharts            | 4.9.x (支持ES5)    | ✅ 单文件JS，直接引用 |
| Firefox ESR        | 52.9.0             | ⚠️ 需预装 |
| ES6 语法           | 不能使用           | ⚠️ 全部代码必须 ES5 |
| CSS Grid           | Firefox 52 支持    | ✅  |
| fetch() API        | Firefox 52 支持    | ✅  |
|箭头函数/let/const  | 不能使用           | ❌ 必须用 var + function |

---

*文档版本: v1.0 | 2026-07-05*
