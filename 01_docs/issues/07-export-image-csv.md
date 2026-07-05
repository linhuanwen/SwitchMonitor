# Slice 7: 导出图片 + CSV

## Type

AFK

## Blocked by

Slice 5（图表网格）

## What to build

在图表区工具栏增加导出按钮，支持将当前曲线导出为 PNG 图片和 CSV 数据文件。使用 `SaveFileDialog` 让用户选择保存路径。

### 导出 PNG 图片

- **触发方式**：图表区工具栏"导出图片"按钮
- **实现**：C# 端通过 `WebBrowser.Document.InvokeScript` 调用 Highcharts 的 `chart.exportChart()` 方法
  - Highcharts 2.x 的 `exportChart()` 走 `exporting.src` 配置 → 可配置为自定义服务器或无服务器模式
  - **备选方案**：C# 端对整个 WebBrowser 控件做 `DrawToBitmap()`，截取图表区域的像素
- **默认文件名**：`{switchId}_{datetime}_曲线.png`
- **文件类型过滤**：`PNG 图片 (*.png)|*.png`

### 导出 CSV 数据

- **触发方式**：图表区工具栏"导出 CSV"按钮
- **实现**：C# 端直接从内存中已加载的 `SwitchEvent` 对象生成 CSV 文本
- **CSV 格式**：
  ```
  Time(s),CurrentA(A),CurrentB(A),CurrentC(A),Power(KW)
  0.00,5.647,5.529,2.078,3.020
  0.04,1.451,1.451,1.490,0.294
  ...
  ```
- **编码**：UTF-8 with BOM（确保 Excel 正确识别）
- **默认文件名**：`{switchId}_{datetime}_数据.csv`
- **数值精度**：保留 3 位小数

### 导出范围

- 导出当前显示的 4 个图表对应的数据
- PNG 导出 4 张（每个图表一张），CSV 导出 2 个（电流 + 功率 各一文件，含两条曲线时间的数据）
- 或简化为：仅导出当前选中动作（左下+右下）的图和数据

### UI 交互

- 工具栏增加 2 个按钮：📷导出图片 / 📄导出CSV（文字即可，不用图标）
- 按钮点击 → `SaveFileDialog` 弹出
- 保存成功后状态栏显示提示
- 保存取消或失败：静默处理，记录日志
- 无数据时：按钮灰显（禁用）

## Acceptance criteria

- [ ] PNG 导出：图表区域可辨识（含曲线、坐标轴、标题）
- [ ] CSV 导出：采样数据完整，格式正确，Excel 能打开且中文正常
- [ ] CSV 数值精度与屏幕显示一致（3 位小数）
- [ ] 无数据时导出按钮禁用
- [ ] 导出操作不阻塞 UI

## Further notes

- 优先用 Highcharts 2.x 内置导出（如果可用），否则用 `WebBrowser.DrawToBitmap`
- CSV 用 `StreamWriter` 逐行写入
- 不用第三方库，纯 .NET
