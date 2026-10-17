# WPS 墨迹同步 · ICC CE 插件

在 [InkCanvasForClass CE](https://github.com/InkCanvasForClass/InkCanvasForClass) 的浮动工具栏里加一排翻页按钮：
按一下让 WPS 滚 N 格，同时把画布上的批注笔迹按**同样的像素**平移。讲课时页面和墨迹永远对齐。

> 下载：见 [Releases](https://github.com/chuyue136/WPSfollow/releases)（`WpsInkSync.icpx`，丢进 ICC 的 `Plugins\com.yg.wpsinksync\` 即可）。

- 只跟**插件自己发的滚动**：鼠标滚轮、手型拖动、拖滚动条一律不动笔迹（避免抓帧量不准把墨迹推歪）。
- 每格像素自学习：PDF 300% ≈ 60px、docx 200% ≈ 87px，换文档/改缩放后头一两下自动校准。
- 支持 WPS PDF（窗口类 `Qt5*`）与 .docx（`OpusApp`）。其它软件不动。

精度（本机实测，明细见 [CHANGELOG](CHANGELOG.md#实测记录)）：
单格 `↓`×10 / `↑`×10 往返、6 格往返、到顶到顶连按 2/3/5/8 下，截图逐点差全部 **0%**（残留仅重排抗锯齿）。

## 安装

插件目录（`<ICC 安装目录>\Plugins\com.yg.wpsinksync\`，目录名必须等于清单 Id）只需要 4 个文件：

```
manifest.json  WpsInkSync.dll  WpsInkSync.deps.json  WpsInkSync.runtimeconfig.json
```

SDK 类型由宿主提供，不用带依赖。编译部署一条命令（会强制重启 ICC）：

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
   - **显示插件按钮**：`始终` / `仅当 WPS 运行时` / `关闭`。选 `关闭` 后整块插件停止运行（不抓帧、注销热键），
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
抓画面做块匹配一帧要 ~148ms（≈6.7 fps），快速滚轮的帧间位移就冲到 300~400px，追不准。
所以把控制权拿过来：**我们发滚轮，所以我们知道滚了多少**。

一轮的流程：

1. 按下按钮立刻 `TransformInkAsync(0, ∓stepPx×格数)` 平移墨迹（零延迟，跟手）。
2. 同时 `SendMessageTimeout` 把 `WM_MOUSEWHEEL` 发给 WPS 的**文档子窗口**（发给顶层窗口无效）。
3. 等沉降期（`300ms + 40ms×格数`）抓帧复核。核心不变量：**画面变了就说明页面滚了，绝不撤回墨迹**。
   一次批量只有两种真相——"滚了预期"或"到边没滚"，就比这两个假设；分不开时信命令（不撤回）。
   只有"画面完全没变"（连续 2 帧、距按键 ≥900ms）才认定到顶/到底 → 撤回全额账本。
4. **对账（收尾）**：一轮第一下按键时克隆锚点帧，停手且画面静止后比**整轮总位移**，
   与墨迹实际走过的量对齐，差 ≥0.6 格才补（单次夹 ±3 格）。专门兜"多格批次只能信命令"的漏账。
5. 没有账本 = 不是我们发的滚动 → 只换基线，不动墨迹（日志 `外部滚动，不跟`）。

## 出问题怎么查日志

```
<ICC 安装目录>\PluginLogs\com.yg.wpsinksync\<日期>.log
```

日志始终开着，一天一个文件，重度测试一天约 100KB / 1000 行。一键摘要：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\diag.ps1
```

关键行：

- `press #N 1 格 墨迹(-60) step=60 账本=60px Δ=(0,-60)` —— 第 N 下，命令几格，墨迹累计偏置。
- `#N 实测滚动 60px（1.000/0.969）step=60` —— 被证实滚了，括号内是"移动/静止"两个假设的相似度。
- `#N 复核判定没滚(到边)` / `复核分不开 → 信命令` / `复核修正: 命令 -180px → 实测 -240px`
- `对账: 墨迹走了 -60px，实测页面 60px（下带(60)） → 差 0px 不足一格，不动`
- `心跳 step=60 墨迹Δ=(0,-60) 判断:确认12/没滚0/分不开0/大批量信命令0` —— 每 30 秒一行。

## 已知边界

- **单格偶尔被 WPS 吃掉**：那一下页面没动、复核也分不开时靠收尾对账补回；若三次都测不出（大片留白/正好页缝），
  墨迹会永久差**最多一格**，按一次反向键或 `Ctrl+Alt+R` 归位即可。
- **一批连按 > ~10 格（600px）复核不了**：单帧比较不了这么大的位移，会扫完候选后信命令；WPS 少吃/多吃几格就偏几行。
- **到文档首/尾时按钮"空按"**：墨迹先跟随动一下再撤回（约 0.5s 抖动），这是没有滚动条位置可读的代价，最终位置一定对齐。
- **改缩放后**每格像素变了：`_stepPx` 会自学习回来，但缩放那一下墨迹不跟着缩放（带 scale 的矩阵跟随没做）。
- 横向平移（`dx`）路径在，但 WPS 这版 PDF 视图不响应 `HWHEEL`，未实测。
- 只认 WPS Writer / PDF（进程 `wps` / `wpspdf` / `wpp` / `et`，窗口类 `Qt5*` 或 `OpusApp`）。

## 代码结构

| 文件 | 作用 |
|---|---|
| `WpsInkSync/WpsInkSyncPlugin.cs` | `[PluginEntrance]` 入口：取服务、浮动栏按钮、热键、设置页、显示策略 |
| `WpsInkSync/WpsFollower.cs` | 主循环：门控 → 抓帧 → 复核 → 补差/撤回；`PressAsync`（命令滚动 + 乐观平移） |
| `WpsInkSync/WpsScroll.cs` | 找 WPS 窗口/文档子窗口、注入 `WM_MOUSEWHEEL`、等修饰键松开 |
| `WpsInkSync/InkShift.cs` | `TransformInkAsync(Matrix)` 串行化 + 累计位移 + 归位 |
| `WpsInkSync/WheelTrigger.cs` | `WH_MOUSE_LL` 全局钩子，听到滚轮把跟随器切到高频态 |
| `tools/ScrollProbe/` | 控制台探针：`dump`（窗口/UIA 侦察）、`probe`、`shot`、`cmp a.bmp b.bmp`；`Capture.cs` 抓帧+匹配，插件复用同一份源码 |
| `tools/*.ps1` | `diag` 日志摘要、`hk` 真按键触发热键、`wheel`/`seq` 滚轮注入与往返回归、`settings` 用 UIA 打开设置页、`test-reconcile` 连按压测 |

## License

[MIT](LICENSE) © 2026 chuyue136
