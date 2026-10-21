# WPS 墨迹同步 · ICC CE 插件

在 [InkCanvasForClass CE](https://github.com/InkCanvasForClass/InkCanvasForClass) 的浮动工具栏里加一排翻页按钮：
按一下让 WPS 滚 N 格，同时把画布上的批注笔迹按**同样的屏幕物理像素**平移。讲课时页面和墨迹始终对齐。

> 下载：见 [Releases](https://github.com/chuyue136/WPSfollow/releases)（`WpsInkSync.icpx`，丢进 ICC 的 `Plugins\com.yg.wpsinksync\` 即可）。
> 修复记录、日志指纹、实测数据都在 [CHANGELOG.md](CHANGELOG.md)，本页只讲怎么用。

- 只跟**插件自己发的滚动**：鼠标滚轮、手型拖动、拖滚动条一律不动笔迹（日志 `外部滚动，不跟`）。
- **不靠截图猜滚动**：docx 读 WPS COM 的正文屏幕坐标，PDF 读 UIA 滚动条数值，两者都是屏幕物理像素。
  截图匹配只留给拿不到上述标尺的旧版 WPS。
- 每格像素**显式标定**：测量/账本一律按**屏幕物理像素**，墨迹按 **DIP** 推，换算只走 `tools/ScrollProbe/Units.cs`。
- 只认 WPS Writer / PDF（进程 `wps` / `wpspdf` / `wpp` / `et`，窗口类 `Qt5*` 或 `OpusApp`）。其它软件、Word 不跟。

精度（本机实测，明细见 [CHANGELOG](CHANGELOG.md#实测记录)）：
docx 6 格 504px / 约 830ms；PDF 125% 单格与 6 格的上下来回 `Δ` 都回到 0；页首页尾实测 0px 不漂。

## 安装

插件目录（`<ICC 安装目录>\Plugins\com.yg.wpsinksync\`，目录名必须等于清单 Id）只需要 4 个文件：

```
manifest.json  WpsInkSync.dll  WpsInkSync.deps.json  WpsInkSync.runtimeconfig.json
```

SDK 类型由宿主提供，不用带依赖。开发时编译部署一条命令（会强制重启 ICC）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy.ps1
```

> 本机只有 Windows PowerShell 5.1 且执行策略默认禁用，所以必须带 `-NoProfile -ExecutionPolicy Bypass`。
> `tools\*.ps1` 一律存成**纯 ASCII 或 UTF-8 带 BOM**，否则 5.1 会按 ANSI 读中文而报"字符串未终止"。

## 怎么用

1. 用 WPS 打开 .docx / .pdf，ICC 在**鼠标模式或画笔模式**下都会响应（批注过程中也能翻页）。
2. ICC 浮动栏出现：`跟随` / `上1` / `下1` / `上6` / `下6`（调试控件打开时还有 `归位` / `打样` / 状态文字）。
   - 上图标下文字，和原生 `鼠标 / 批注 / 白板` 同一套观感（宿主把插件项压到 27px 高，故图标 16×16 + 字号 10.5）。
   - `上1` `下1` = 翻 1 格（约一行）；`上6` `下6` = 翻 6 格（约 1/4 屏）。方向按"你看到的画面往哪走"标的。
   - `跟随` 关掉时压暗，**整排翻页按钮收起**（按了也不动，不占地方）；`归位 / 打样 / 状态` 属调试控件，不受影响。
   - `打样` = 注入 3 条合成笔迹，用来肉眼验收。
3. 演示前想清爽一点：**设置 → 插件设置 → WPS 墨迹同步 设置**，三个开关：
   - **显示插件按钮**：`始终` / `仅当 WPS 运行时` / `关闭`。选 `关闭` 后整块插件停止运行（不探标尺、注销热键），
     工具栏连底壳一起收起；`仅当 WPS 运行时` 每 500ms 探一次，WPS 一关就停、再开自动接回。
   - **是否显示 6 行滚动按钮**：`否` 时只剩 `跟随 / 上1 / 下1`（热键 `PgUp/PgDn` 照旧）。
   - **显示调试控件**：关掉后只剩 `跟随` + 翻页按钮。
   - 另外还有 `打开日志目录` / `复制日志路径` / `复制今日日志` / 内联看今天最近 200 行日志。
   - 开关存在插件目录自己的 `settings.ini`（宿主那份要正常退出才落盘，部署是强杀，存不住）。
4. 热键（讲课时手不用离开笔）：

| 热键 | 作用 |
|---|---|
| `Ctrl+Alt+↑` / `Ctrl+Alt+↓` | 翻 1 格 |
| `Ctrl+Alt+PgUp` / `Ctrl+Alt+PgDn` | 翻 6 格 |
| `Ctrl+Alt+F` | 跟随开关 |
| `Ctrl+Alt+R` | 归位（墨迹平移量清零） |
| `Ctrl+Alt+K` | 打样 |
| `Ctrl+Alt+M` | 日志里打一行 MARK（汇报问题时按一下，据此定位） |
| `Ctrl+Alt+D` | 调试控件开关 |

## 原理

**为什么反过来控制**：WPS 是 Qt5 + Skia 自绘，`GetScrollInfo` 和 UIA `ScrollPattern` 都拿不到滚动量。
抓画面做块匹配一帧要 ~148ms（≈6.7 fps），快速滚轮的帧间位移能冲到 300~400px，追不准。
所以把控制权拿过来：**我们发滚轮，所以我们知道滚了几格**，再用 WPS 自己的坐标把"实际滚了多少"量出来。

一次按键的流程：

1. **先立标尺**（发送前）：
   - **docx**：从文档子窗口 `_WwG` 取活的 WPS `Window`（`OBJID_NATIVEOM` + `IID_IDispatch`），
     `RangeFromPoint` 拿一个正文 `Range`，`GetPoint` 读它在屏幕上的 `Top`。ICC 是 64 位 UIAccess 进程，
     某些版本只暴露 `IDispatch`，所以还有 DISPID 110/112 晚绑定兜底（`WpsDispatch.cs`）。
   - **PDF**：UIA 树里 `KPdfScrollBarWheelFixed` 滚动条的 `RangeValuePattern`，读数就是**屏幕物理像素**，
     150% / 125% / 288dpi 都 1:1。
   - 两个都拿不到（旧版 WPS）才回退截图逐拍。
2. **乐观平移**：先把墨迹按"格数 × 每格物理像素 ÷ dpi 比例"推出去（零延迟，跟手），
   同时把账本 `_pendingPx` 记成物理像素。
3. **发滚轮**：`WM_MOUSEWHEEL` 发给 WPS 的**文档子窗口**（发给顶层窗口无效）。
   **一格一条消息**，`delta` 固定 ±120（WPS 不认大小）。格间不是死等 260ms，而是
   **等页面真的动了再发下一格**（COM：等 `Top` 稳定；PDF：等滚动值变化），所以 6 格能压到 0.38~0.45s。
   发送前还会等修饰键松开，避免 Ctrl+滚轮变成缩放。
4. **这一拍当场结算**：再读一次标尺，`moved = 新读数 − 发送前读数`，
   `need = (预期 − moved) ÷ dpi比例`，差 ≥0.5 DIP 就补一次差，然后清掉这一拍的账本。
   到页首/页尾时 `moved = 0`，正好把这一拍的乐观墨迹撤回，**最终墨迹不动**；连按也不会攒成一批去回滚。
5. **标定每格像素**（不是猜）：只拿"干净样本"——这一拍没有排队的下一拍、`|moved| ≥ 20`、
   方向与命令一致、且 `|moved| ≥ 0.8 × 预期`。每格值 = 文档缩放 × 行高 × dpi，实测同一份 docx：
   96dpi 是 **84px**、288dpi 是 **152px**，差一格就是整篇错位。换缩放 / 换文档 / 换屏都重新标定，
   标定只改每格值，不动墨迹。
6. **没账本 = 不是我们发的滚动**：只换基线，不动墨迹（日志 `外部滚动，不跟`）。

单位铁律（这是稳定性的地基）：抓帧 / 匹配 / 账本 / COM / UIA 一律**屏幕物理像素**；
墨迹 `TransformInkAsync` 一律 **DIP**；两者差 `dpi/96`（希沃八代 288dpi = 3×）。
所有换算集中在 `tools/ScrollProbe/Units.cs`，`InkShift` 是唯一的物理→DIP 出口。

## 出问题怎么查日志

```
<ICC 安装目录>\PluginLogs\com.yg.wpsinksync\<日期>.log
```

日志始终开着，一天一个文件。一键摘要：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\diag.ps1
```

关键行：

- `init: comp=True ink=True ...` —— 加载成功；哪项 False 就去查宿主服务。
- `hotkey Ctrl+Alt+↑ 翻 1 格 = True` —— 热键注册成功；`False` 多半是 ICC 起了两个实例。
- `COM 标尺: 正文Top=...` / `PDF UIA 标尺: 滚动值=...` —— 当前走的是哪条主路径。
- `COM 标尺不可用: ...` / `PDF UIA 标尺不可用: ...` —— 标尺没取到（看 `type=` / `hr=` / UIA 树），会回退截图。
- `press #N 6 格 墨迹(-504.0DIP=504物理px) ... 账本=504物理px ... 命中5/5` —— 第 N 下：命令几格、墨迹推了多少、几格看到页面动。
- `COM 逐拍 实测 504px，预期 504px → 补差(0DIP)` / `PDF UIA 逐拍 实测 ...px` —— 这一拍结算正常。
- `截图逐拍 实测 0px，到边 → 撤回` —— 到页首/页尾，这一拍墨迹退回（如果页面其实动了，说明标尺失效）。
- `每格 60 物理px ... → 84 物理px（COM逐拍(1格)）` —— 标定动作，左边旧值右边新值（`@3x` 就是 288dpi）。
- `外部滚动，不跟` —— 正常：鼠标 / 手型 / 滚动条不跟。
- `补差越界` / `下发超时` —— 异常，需要看上下文，别忽略。

排查"错位"的顺序：先看走的是 `COM 逐拍` / `PDF UIA 逐拍` 还是 `截图逐拍`（走截图就先解决标尺为什么不可用）；
再看 `每格` 是否与文档/缩放匹配；再看 `moved` 与 `expected`（到边时 moved=0 正常）；最后看多次上下后 `Δ` 是否回到 0。
汇报问题时先按 `Ctrl+Alt+M` 打一行 `MARK`，把那一小段日志发出来即可精确定位。

## 已知边界

- **到边空按有一下抖动**：页首/页尾继续按，墨迹先顺势动一下再撤回（约 0.5s），最终位置一定对齐。
- **第一次按键可能先欠一点**：还没标定时，每格初值是 60 物理px（96dpi 的经验值，**不按 DPI 预乘**）。
  288dpi 上首按会先推 60、再补差、再标定到 152。如果首按刚好在页首/页尾（`moved=0`）不会标定，下一按仍按 60。
- **旧版 WPS 的 PDF** 没有 `KPdfScrollBarWheelFixed` 时回退截图逐拍，速度和精度略低，空白页可能误判。
- **多 WPS 窗口/多标签**：窗口按"面积最大的可见 WPS 窗口"选；切标签后标尺 `Hwnd` 变了会丢弃重取，但边界未系统压测。
- **归位按钮只清墨迹累计量**，不保证打断正在结算的那一拍（已知低优先级问题，见 CHANGELOG）。
- 横向平移（`dx`）路径在，但 WPS 这版 PDF 视图不响应 `HWHEEL`，未实测。
- 按钮图标的颜色跟着 ICC 工具栏底色走（深底浅字 / 浅底深字），ICC 切主题后 500ms 内自动改色。

## 代码结构

| 文件 | 作用 |
|---|---|
| `WpsInkSync/WpsInkSyncPlugin.cs` | `[PluginEntrance]` 入口：取服务、浮动栏按钮、热键、设置页、显示策略、主题 |
| `WpsInkSync/WpsFollower.cs` | 主循环：`PressAsync` → 发滚轮 → 逐拍结算（COM / PDF / 截图三条）、标定、旧版对账兜底 |
| `WpsInkSync/WpsScroll.cs` | 找 WPS 窗口/文档子窗口、注入 `WM_MOUSEWHEEL`、等每格动画、等修饰键松开 |
| `WpsInkSync/WpsDocPos.cs` | docx COM 标尺（`RangeFromPoint` + `GetPoint`） |
| `WpsInkSync/WpsDispatch.cs` | 极简 `IDispatch.Invoke`，DISPID 110/112 晚绑定兜底 |
| `WpsInkSync/WpsPdfPos.cs` | PDF UIA 滚动条标尺（`KPdfScrollBarWheelFixed` + `RangeValuePattern`） |
| `WpsInkSync/InkShift.cs` | `TransformInkAsync(Matrix)` 串行化 + 累计位移 + 归位（唯一物理→DIP 出口） |
| `WpsInkSync/WheelTrigger.cs` | `WH_MOUSE_LL` 全局钩子，听到滚轮把跟随器切到高频态 |
| `tools/ScrollProbe/` | 控制台探针：`dump` 窗口/UIA 侦察、`probe`、`shot`、`cmp`、`units` 单位自检；`Capture.cs` 抓帧+匹配（插件复用同一份源码），`Units.cs` 单位换算 |
| `tools/*.ps1` | `diag` 日志摘要、`hk` 真按键触发热键、`wheel`/`seq` 滚轮注入与往返回归、`settings` 用 UIA 打开设置页 |

## License

[MIT](LICENSE) © 2026 chuyue136
