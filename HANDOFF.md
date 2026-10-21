# ICC CE × WPS 墨迹同步插件：项目交接文档

> 生成时间：2026-10-20（Asia/Shanghai）  
> 当前版本：**v0.2.0 预发布**  
> 仓库：<https://github.com/chuyue136/WPSfollow>  
> 许可证：MIT，作者署名 **chuyue136**  
> 本文档是给“下一个接手的人 / AI”看的完整交接。先读第 0、1、10、11 节，再按需查代码。

---

## 0. 三十秒摘要

这是一个 **ICC CE（InkCanvasForClass CE）插件**：WPS 打开 `.docx` / `.pdf` 时，教师在 ICC 浮动栏按“上1 / 下1 / 上6 / 下6”，插件向 WPS 发滚轮消息，同时把 ICC 白板上的批注笔迹按**同样的屏幕物理像素**平移。

核心设计是“**控制模式**”：滚动由插件自己发，所以插件知道滚了几格；再用 WPS 自己的对象模型测量实际位移，逐拍补差。**不再靠截图猜页面有没有滚动**。

当前状态：

- **v0.2.0 已发布为 GitHub 预发布**，包含 docx COM 标尺 + PDF UIA 标尺 + 逐拍结算。
- **PluginIndex PR #16 已创建、open、mergeable=clean，等待维护者合并**；合并后市场才会重新出现正确名称和 0.2.0。
- **希沃八代 288dpi 尚未用 v0.2.0 复测**，这是发布后最重要的验证项。
- README 还是 v0.1.2 时代的描述，**已落后于代码**，需要更新（见 §10.1）。

---

## 1. 项目坐标

| 项 | 值 |
|---|---|
| 本地工作区 | `C:\Users\Admin\Desktop\VibeCoding\PVPer NPC\ICC CE WPS联动` |
| GitHub | `chuyue136/WPSfollow`（SSH 可用） |
| 当前 tag | `v0.2.0` → commit `1aa58bf` |
| Release | <https://github.com/chuyue136/WPSfollow/releases/tag/v0.2.0> |
| Release 资产 | `WpsInkSync.icpx`，46,380 B，SHA256 `688164fcfe9e3a8f6f64797fcba8fc65bd2178569a3ab2c66ca6ed548870a742` |
| ICC 安装目录 | `C:\Users\Admin\AppData\Local\Programs\InkCanvasForClass CE` |
| 插件安装目录 | `<ICC>\Plugins\com.yg.wpsinksync` |
| 日志目录 | `<ICC>\PluginLogs\com.yg.wpsinksync\yyyy-MM-dd.log` |
| 插件设置文件 | `<插件目录>\settings.ini` |
| 技术栈 | C# / .NET 10 / WPF；宿主 SDK `InkCanvas.PluginSdk 1.8.0.8-g90c1bad8f3` |
| 目标框架 | `net10.0-windows10.0.19041.0` |
| 市场索引 PR | <https://github.com/InkCanvasForClass/PluginIndex/pull/16> |

### 版本历史（对外）

| 版本 | 状态 | 说明 |
|---|---|---|
| v0.1.0 | 已发布 | 首个可用版；市场 PR #14 曾合并，后因 YAML 乱码被移除 |
| v0.1.1 | 已发布 | UI / 主题 / 工具栏外观修复 |
| v0.1.2 | 已发布 | 物理像素 / DIP 单位铁律、一拍一结算 |
| v0.1.3 / v0.1.4 / v0.1.5 | **内部迭代，未打 tag** | COM 标尺、ICC 晚绑定、逐拍、PDF 截图兜底 |
| **v0.2.0** | **当前预发布** | docx COM + PDF UIA，docx/PDF 全部脱离截图判定 |

---

## 2. 现在做到哪了

### 2.1 已完成

- [x] WPS `.docx` 跟随：COM `RangeFromPoint` + `GetPoint` 读正文屏幕 Top，逐拍精确补差。
- [x] WPS `.pdf` 跟随：UIA 滚动条 `KPdfScrollBarWheelFixed` 的 `RangeValuePattern`，读数与物理像素 1:1。
- [x] 两种文档都**逐拍结算**：每按一次只测自己这一拍，到边只撤回这一拍，不会整批回滚。
- [x] 连按不甩飞：页首/页尾连续按，实测 0px 时墨迹原地不动。
- [x] 性能：docx 6 格约 0.83s；PDF 6 格发送约 0.38–0.45s，整拍约 0.9s。
- [x] 高 DPI 单位铁律：测量/账本用物理 px，`TransformInkAsync` 用 DIP，换算只走 `Units.cs`。
- [x] 显示策略三档、6 行开关、debug 开关、主题自适应、日志路径按钮、热键。
- [x] GitHub Actions 自动发布预发布包（tag 触发，`GITHUB_TOKEN`）。
- [x] PluginIndex 修正分支已推送，PR #16 已创建。

### 2.2 待办 / 风险

- [ ] **PR #16 等维护者合并**；合并后确认市场索引出现 `WPS 墨迹同步` / `0.2.0` / 正确中文。
- [ ] **希沃八代 288dpi 用 v0.2.0 复测**：docx/PDF、100%/125%/150%、多次上下、到边。
- [ ] **README 更新到 v0.2.0**（当前仍写 260ms、截图匹配、旧架构）。
- [ ] **“归位”按钮与跟随器账本不同步**（已知低优先级 bug，见 §10.2）。
- [ ] 多 WPS 窗口 / 多标签切换的边界未系统测试。
- [ ] 旧版 WPS（无 PDF UIA 滚动条）只能回退截图兜底，速度和精度略低。

---

## 3. 产品目标与硬性行为规则

### 3.1 功能目标

WPS 打开 `.docx` / `.pdf` 时，教师通过 ICC 浮动栏按钮或热键翻页，白板上的批注笔迹跟随页面移动，保证“笔迹始终压在原来的文字/位置上”。

### 3.2 行为规则（验收口径）

| 场景 | 正确行为 |
|---|---|
| 插件按钮 / 热键翻页 | 页面滚 N 格，墨迹同步移动 N 格对应的物理像素 |
| 鼠标滚轮 | **不跟随**，日志 `外部滚动，不跟` |
| 手型拖动 / 拖滚动条 | **不跟随** |
| 页面到顶 / 到底 | 继续按，页面不动，墨迹也不动（可短暂乐观移动后撤回，最终必须回原位） |
| 批注过程中（画笔模式） | 按钮/热键翻页仍然生效；不是只有鼠标模式才响应 |
| 切换文档 / 标签 | 重新取标尺；旧标尺失效不能量错 |
| 换缩放 / 换屏 | 每格像素重新标定 |

> 关键口径：**只有插件自己下发的滚动才带墨迹**。外部滚动没有账本，只换基线，不动墨迹。  
> `_mouseMode` 目前只用于状态文字；跟随器在画笔模式下也运行，所以批注时也能响应。

### 3.3 UI / 设置硬要求

- 按钮大小、图标下文字与 ICC 原生按钮一致（实现：46×30，图标 16×16，字号 10.5，行高 12）。
- 设置页必须有：
  - `显示插件按钮`：`始终` / `仅当 WPS 运行时` / `关闭`。
  - `是否显示 6 行滚动按钮`：是 / 否。
  - `显示调试控件（归位 / 打样 / 状态文字）`。
  - 打开日志目录、复制日志路径、复制今日日志、内联最近 200 行日志。
- `关闭` 时插件完全停止：工具栏连宿主外壳一起隐藏，不留残底；跟随器停止、热键注销。
- `仅当 WPS 运行时`：每 500ms 探测，WPS 关闭就停，再打开自动接回。
- `6 行` 关闭：只隐藏 `上6/下6`；`跟随` 关闭时四个翻页按钮全部收起；调试按钮和状态保留。
- 跟随系统浅色/深色：文字、图标、按钮前景自动切换。

### 3.4 质量要求

- 必须在不同 DPI（尤其希沃八代 288dpi = 3×）、不同电脑、不同文档上不错位。
- 不能出现“按几下整段跳回 / 甩出几屏 / 向下闪一下又回去”。
- 功能稳定性优先于“看起来快”；但单次按键不应再固定等几百毫秒。

---

## 4. 总体架构（v0.2.0）

### 4.1 控制流

```
用户点按钮 / 按 Ctrl+Alt+↑↓ / PgUp/PgDn
        │
        ▼
WpsInkSyncPlugin.Press(notches, dir)
        │  Task.Run
        ▼
WpsFollower.PressAsync(notches, dir)
        │
        ├─ 选标尺：
        │    docx  → WpsDocPos（COM RangeFromPoint + GetPoint）
        │    PDF   → WpsPdfPos（UIA 滚动条 RangeValue）
        │    都没有 → 截图兜底（旧 WPS / 特殊版本）
        │
        ├─ 账本 + 锚点（物理 px），记录 _pendingPx/_pendingNotch
        │
        ├─ 等 _sendGate（串行）
        │    ├─ 乐观平移墨迹：InkShift.ApplyAsync(0, -pxPhys/dpiScale)
        │    └─ WpsScroll.Send：一格一条 WM_MOUSEWHEEL，等每格动画
        │
        ├─ 逐拍结算：
        │    SettleComPress / SettlePdfPress / SettleShotPress
        │    读实际位移 → CorrectionDip → 补差 → 清账本/锚点
        │
        └─ 释放 _sendGate，下一次按键才能进入
```

### 4.2 三条测量链

#### A. docx：WPS COM 标尺（主路径）

- 从文档子窗口 `_WwG` 用 `AccessibleObjectFromWindow(OBJID_NATIVEOM=0xFFFFFFF0, IID_IDispatch)` 取活的 WPS `Window` 对象。
- `RangeFromPoint(screenX, screenY)` 取一个正文 `Range`；`GetPoint` 读它在屏幕上的 `Top`。
- 页面滚动 N 物理 px，同一个 Range 的 `Top` 就变化 N 物理 px；不依赖截图、不受空白页边影响。
- ICC 是 64 位 **UIAccess** 进程，某些 WPS 版本只把对象暴露成 `IDispatch`，强转手写接口会失败；因此 `WpsDispatch.cs` 用 **DISPID 110/112 晚绑定**兜底。
- 每个按键在发送前取标尺，结算后 `Dispose`；换文档/换标签时 `Hwnd` 不匹配就丢弃。

关键接口：

```csharp
[ComImport, Guid("00020962-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
interface IWpsWindow {
  [DispId(110)] object RangeFromPoint(int x, int y);
  [DispId(112)] void GetPoint(out int left, out int top,
      out int width, out int height,
      [MarshalAs(UnmanagedType.IDispatch)] object range);
}
```

#### B. PDF：UIA 滚动条标尺（主路径）

- WPS PDF 是 Qt 外壳（`Qt5QWindowIcon`）。
- UIA 树里有 `ClassName=KPdfScrollBarWheelFixed`、`ControlType=ScrollBar` 的滚动条，支持 `RangeValuePattern`。
- `RangeValue.Current.Value` **就是屏幕物理像素滚动位置**（实测 150%、125%、288dpi 都 1:1）。
- 发送前记 `anchor`，每格等数值真正变化再发下一格；结算 `moved = value - anchor`。
- 如果 UIA 取不到（旧版 WPS），回退截图逐拍。

#### C. 截图兜底（只给旧 WPS / 特殊版本）

- `Capture.Grab` 用 `PrintWindow(PW_RENDERFULLCONTENT=2)` 抓整窗，`GetDIBits` 拷贝；失败回退屏幕 BitBlt。
- `DiffRatio` 判断画面是否变化；`MatchBand` / `MatchTwoBands` 做竖直带匹配。
- 只在 `_docPos == null && _pdfPos == null` 时使用；正常 docx/PDF 不抓帧。
- 这条路径**不能**作为新功能主路径；用户明确要求“不要截图猜”。

### 4.3 单位铁律

> 抓帧 / 匹配 / 账本 / COM / UIA：一律 **屏幕物理像素**。  
> 墨迹 `TransformInkAsync`：一律 **DIP**。  
> 两者差 `dpiScale = dpi/96`；希沃八代 288dpi = 3×。

- 换算唯一出口：`tools/ScrollProbe/Units.cs`。
  - `PhysToDip(phys, scale) = phys / scale`
  - `DipToPhys(dip, scale) = dip * scale`
  - `InkDipFor(contentPhys, scale) = -contentPhys / scale`（内容上移 = 墨迹上移为负）
  - `CorrectionDip(expectedPhys, measuredPhys, scale) = (expectedPhys - measuredPhys) / scale`
- `InkShift` 是唯一物理→DIP 出口；不要在别处写裸的 `* dpiScale` / `/ dpiScale`。
- 账本 `_pendingPx` 永远是物理像素；`InkShift.X/Y` 永远是 DIP。

### 4.4 逐拍结算（v0.2.0 的核心）

每按一次：

1. 计算 `pxPhys = _stepPhys * notches * dir`（物理 px）和 `inkDip = Units.InkDipFor(pxPhys, dpiScale)`。
2. 先记“乐观账”：`_pendingPx += pxPhys`、`_pendingNotch += notches*dir`。
3. 拿 `_sendGate` 后，先 `ApplyAsync(0, inkDip)` 推墨迹，再发滚轮。
4. 发完立刻结算这一拍：
   - `moved` = 实际页面位移（物理 px）。
   - `need = (expected - moved) / dpiScale` DIP。
   - `|need| >= 0.5` 就 `ApplyAsync(0, need)` 补差。
5. 清掉这一拍的账本/锚点，`Dispose` 标尺；下一拍再来。

这样做的原因：

- 旧版把连按攒成“一批”再结算，页首/页尾会把几千 px 的账一次性撤回，墨迹直接甩飞。
- 现在每拍只测自己这一拍，到边只撤回这一拍，**不会累积错位**。
- 发送仍由 `_sendGate` 串行，保证“推墨迹 → 发 → 补 → 下一拍推”严格按序。

### 4.5 每格像素标定

- 未标定初值：`Units.GuessStepPhys96 = 60`（96dpi 经验值，单位物理 px）。
- 标定不是猜，是“干净样本”测量：
  - 这一拍没有排队的下一拍（`leftPx == 0`）；
  - `|moved| >= 20`，方向与命令一致；
  - COM/PDF 还要求 `|moved| >= 0.8 * |expected|`（到边只走一部分时不能拿来当每格）；
  - 候选值 20..4000，变化 >2% 才采纳。
- 标定只改 `_stepPhys`，不动墨迹；`CalibrateStep` 不碰账本。
- 换缩放 / 换文档 / 换屏：重新用新样本覆盖旧值。

> 注意：初值 60 没有按 DPI 预乘。第一次按键如果实际是 152/180，会先补差，再在干净样本上标定；这是有意的，避免用错误模型直接推 3 倍。

### 4.6 到边 / 外部滚动

- 到边：实测 `moved = 0`，`need = expected/scale` 正好把这一拍的乐观墨迹撤回；最终墨迹不动。
- 外部滚动：`Tick` 发现没有账本（`_pendingPx == 0`）时，只换基线，日志 `外部滚动，不跟`，不动墨迹。
- 画笔模式：跟随器不因 `_penMode` 停；按钮/热键仍工作。`_mouseMode` 仅用于状态文字。
- `WheelTrigger` 只是低级鼠标钩子，滚轮一动把跟随器切到高频采样；它不决定是否跟随。

---

## 5. 代码地图

| 文件 | 行数（约） | 职责 |
|---|---:|---|
| `WpsInkSync/WpsInkSyncPlugin.cs` | 943 | `[PluginEntrance]` 入口；服务注入、工具栏、热键、设置页、显示策略、主题 |
| `WpsInkSync/WpsFollower.cs` | 1414 | 主循环；`PressAsync`、`SendCore`、三种逐拍结算、标定、旧版对账兜底 |
| `WpsInkSync/WpsScroll.cs` | 338 | 找 WPS 窗口/文档子窗口；发 `WM_MOUSEWHEEL`；等每格动画；等修饰键松开 |
| `WpsInkSync/WpsDocPos.cs` | 151 | docx COM 标尺（`RangeFromPoint` + `GetPoint`，含 IDispatch 晚绑定） |
| `WpsInkSync/WpsDispatch.cs` | 105 | 极简 `IDispatch.Invoke`，DISPID 110/112 晚绑定 |
| `WpsInkSync/WpsPdfPos.cs` | 68 | PDF UIA 滚动条标尺（`KPdfScrollBarWheelFixed` + `RangeValuePattern`） |
| `WpsInkSync/InkShift.cs` | 46 | `TransformInkAsync(Matrix)` 串行化 + 累计位移 + 归位 |
| `WpsInkSync/WheelTrigger.cs` | 75 | `WH_MOUSE_LL` 钩子，滚轮时切高频采样 |
| `tools/ScrollProbe/Capture.cs` | 586 | 抓帧、降采样、带匹配、`DiffRatio`（插件链接同一份源码） |
| `tools/ScrollProbe/Units.cs` | 36 | 单位换算唯一出口 |
| `tools/ScrollProbe/Program.cs` | 412 | 控制台探针：`dump` / `probe` / `shot` / `shift` / `cmp` / `units` |
| `deploy.ps1` | 55 | **Debug** 构建 → 拷贝 4 文件 → 杀/重启 ICC → 打印日志（开发用） |
| `.github/workflows/release.yml` | 22 | tag `v*` 触发预发布，上传 `icpx` |
| `docs/releases/v0.2.0.md` | 28 | 当前发布说明 |
| `marketplace/com.yg.wpsinksync.yml` | 17 | PluginIndex 市场条目 |

### 5.1 入口与 UI（`WpsInkSyncPlugin.cs`）

- `[PluginEntrance] public sealed class WpsInkSyncPlugin : PluginBase`，`Order => 100`。
- `Initialize` 取 `ICanvasCompositionService` / `ICanvasInkService` / `IEventService` / `ISettingsService` / `IHotkeyService`。
- 工具栏 item id：`com.yg.wpsinksync.shift`。
- 按钮：
  - `跟随`（`IconSync`）
  - `上1` / `下1`
  - `上6` / `下6`
  - debug：`归位` / `打样` / 状态 `TextBlock`
- 设置键：
  - `com.yg.wpsinksync.debugUi`（bool，默认 true）
  - `com.yg.wpsinksync.uiMode`（`always` / `wps` / `off`，默认 always）
  - `com.yg.wpsinksync.sixRows`（bool，默认 true）
- `settings.ini` 存在插件目录，宿主 `ISettingsService` 也写一份（宿主那份要正常退出才落盘，强杀会丢，所以自己存一份）。
- `ApplyUiMode`：隐藏面板 + 收起宿主包装层（最多 4 层“独生孩子”），停跟随器，注销热键。
- `ApplySixRows` / `ApplyFollowUi`：见 §3.3。
- 主题：从面板向上找第一个不透明纯色背景，按亮度决定深/浅前景；拿不到就退回注册表 `AppsUseLightTheme`。
- `Press` 是 `async void`，内部 `Task.Run(() => _follower.PressAsync(...))`，避免阻塞 UI。

### 5.2 `WpsFollower.cs`

关键符号：

- `PressAsync(int notches, int dir)`：入口。
- `SendCore(...)`：发这一拍；失败处理。
- `SettleComPress(double expected, int expNotch)`：docx 逐拍。
- `SettlePdfPress(double expected, int expNotch, int anchor, bool anchorOk)`：PDF 逐拍。
- `SettleShotPress(Capture.Img before, double expected, int expNotch)`：截图兜底逐拍。
- `EnsureDocPos()` / `EnsurePdfPos()` / `DisposeDocPos()` / `DisposePdfPos()`。
- `CalibrateStep(cand, why)`：只改每格值，不动墨迹。
- `Reset()`：清账本/锚点/标尺（注意：目前热键“归位”没调用它，见 §10.2）。
- 旧版 `Reconcile` / `Tick` 批量对账仍在，但正常逐拍路径会把账本清空，它主要兜底下发超时/测不出。

常量：

| 常量 | 值 | 含义 |
|---|---:|---|
| `IdleMs` | 400 | 静止采样周期 |
| `BurstMs` | 60 | 滚轮后高频采样周期 |
| `BurstHoldMs` | 700 | 高频保持时长 |
| `StillGateMs` | 900/1200 | 截图兜底沉降窗口（已标定/未标定） |
| `ComGateMs` | 350 | COM/PDF 发送后预沉降 |
| `MaxShift` | 320 | 单帧最大搜索位移 |
| `PrefilterThreshold` | 1.5 | 预筛阈值 |

### 5.3 `WpsScroll.cs`

- `Find(out diag)`：枚举可见顶层窗口，进程名 `wps/wpspdf/wpp/et`，类名 `Qt5*` 或 `OpusApp`，选面积最大且有标题的。
- `FindDocument(out top, out cx, out cy, out diag)`：`ChildAtPoint` 找窗口中心最深的可见子窗口（docx 是 `_WwG`，PDF 是 Qt 文档子窗口）。
- `Send(...)`：重载支持 `WpsDocPos` / `WpsPdfPos`；核心流程：
  1. 找目标；校验标尺 `Hwnd` 是否匹配，不匹配丢掉。
  2. `WaitModifiersQuiet(120, 900)`：无修饰键只等 50ms；看到 Ctrl/Alt 按下就等满 120ms，避免 Ctrl+滚轮缩放。
  3. 每格一条 `SendMessageTimeoutW(target, WM_MOUSEWHEEL, ...)`；delta 固定 ±120，**不乘格数**（WPS docx 不认 delta 大小）。
  4. 格间等待：
     - COM：`WaitComMoved`（首格 420ms，后续 280ms；连续两格没动降到 40ms；稳定 6 次约 30ms 且 ≥80ms）
     - PDF：`WaitPdfMoved`（首格 420ms，后续 280ms；稳定 2 次且 ≥30ms；首格确认没动就直接 break）
     - 截图：`WaitPageMoved`（diff≥0.004，预算 700ms）
  5. `hits` 返回“看到页面动”的格数，用于日志/标定判断。
- 发送失败：
  - `diag` 含 `"没有 wps"`：当场撤回这一拍墨迹和账本。
  - 超时/其它：保留乐观平移，等复核（消息可能已送达）。

### 5.4 `WpsDocPos.cs` / `WpsDispatch.cs`

- `WpsDocPos.TryCreate(documentHwnd, screenX, screenY, out why)`：
  - `AccessibleObjectFromWindow(..., OBJID_NATIVEOM, IID_IDispatch)`。
  - 强转 `IWpsWindow` 成功：`RangeFromPoint` + `GetPoint`。
  - 失败且对象是 `IDispatchRaw`：`WpsDispatch.RangeFromPoint` + `WpsDispatch.GetTop`。
- `Top` 是创建时读到的缓存值；`TryReadTop` 用同一个 Range 重新读。
- `Hwnd` 用来检测换文档。
- 失败原因会写日志：`AccessibleObjectFromWindow hr=...` / `不是 WPS Window 接口 type=...` / `RangeFromPoint 返回空` / `GetPoint 失败`。

### 5.5 `WpsPdfPos.cs`

- `TryCreate(topHwnd, out why)`：`AutomationElement.FromHandle` → `FindFirst(Descendants, AndCondition(ClassName=KPdfScrollBarWheelFixed, ControlType=ScrollBar))` → `RangeValuePattern`。
- `TryRead(out int value)`：`Math.Round(RangeValue.Current.Value)`。
- `Value` 单位是物理像素；PDF 向下滚数值变大。
- 只对 Qt PDF 有效；docx 不会走这里。

### 5.6 `InkShift.cs` / `WheelTrigger.cs`

- `InkShift.ApplyAsync(dx, dy)`：`SemaphoreSlim` 串行，`_comp.TransformInkAsync(new Matrix(1,0,0,1,dx,dy))`；累计 `X/Y/Moves`。
- `ResetAsync`：`ApplyAsync(-X, -Y)`。
- `WheelTrigger`：`WH_MOUSE_LL` 低级钩子，只写时间戳 `_burstUntil`；回调必须极快，不做事。

---

## 6. 设置、热键、日志

### 6.1 热键

| 热键 | 作用 |
|---|---|
| `Ctrl+Alt+↑` / `Ctrl+Alt+↓` | 翻 1 格 |
| `Ctrl+Alt+PgUp` / `Ctrl+Alt+PgDn` | 翻 6 格 |
| `Ctrl+Alt+F` | 跟随开关 |
| `Ctrl+Alt+R` | 归位（只清 `InkShift`，见 §10.2） |
| `Ctrl+Alt+K` | 打样（注入 3 条合成笔迹） |
| `Ctrl+Alt+M` | 在日志里打 `MARK` |
| `Ctrl+Alt+D` | 调试控件开关 |

### 6.2 当前本机设置

`<插件目录>\settings.ini` 当前：

```ini
com.yg.wpsinksync.debugUi=true
com.yg.wpsinksync.sixRows=true
```

没有 `uiMode` 行 → 默认 `always`。

### 6.3 日志

- 始终开启，一天一个文件；日志目录见 §1。
- 典型行：

```text
press #7 6 格 墨迹(-504.0DIP=504物理px) 84 物理px = 84.0 DIP @1x 账本=504物理px →0x30930 ... COM间隔≤280ms 命中5/5 用时...
COM 逐拍 实测 504px，预期 504px → 补差(0DIP) ...
PDF UIA 标尺: 滚动值=...
PDF UIA 逐拍 实测 360px，与预期 360px 一致
截图逐拍 实测 0px，到边 → 撤回 ...
外部滚动，不跟
每格 60 物理px = 60.0 DIP @1x → 84 物理px = 84.0 DIP @1x（COM逐拍(1格)）
```

- `Ctrl+Alt+M` 打 `MARK`，用户汇报“这里偏了”时可精确定位时间段。
- 设置页可直接看最近 200 行，也可复制路径。

---

## 7. 构建 / 安装 / 发布

### 7.1 Release 构建

```powershell
cd "C:\Users\Admin\Desktop\VibeCoding\PVPer NPC\ICC CE WPS联动"
dotnet build WpsInkSync\WpsInkSync.csproj -c Release -t:Rebuild --nologo
```

产物：

```text
WpsInkSync\bin\Release\net10.0-windows10.0.19041.0\WpsInkSync.dll
WpsInkSync\icpx\WpsInkSync.icpx
```

`icpx` 是 zip，内含 4 个文件：

```text
manifest.json
WpsInkSync.deps.json
WpsInkSync.dll
WpsInkSync.runtimeconfig.json
```

构建前确认 `WpsInkSync/manifest.json` 的 `Version` 已改；`icpx` 会拷入当前 manifest。

### 7.2 安装到 ICC

1. 关闭 ICC（可多轮杀进程，ICC 可能自拉子实例）。
2. 解压 `WpsInkSync.icpx` 到：

```text
C:\Users\Admin\AppData\Local\Programs\InkCanvasForClass CE\Plugins\com.yg.wpsinksync
```

3. 确认 4 个文件覆盖；重启 ICC。
4. 日志出现 `init: ...` 和 `hotkey ... = True` 即加载成功。

> `.icpx` 扩展名不是 `.zip`，PowerShell `Expand-Archive` 可能拒绝；先复制成 `.zip` 或用 `tar -xf` / 7-Zip。

### 7.3 开发用部署脚本

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy.ps1
```

注意：`deploy.ps1` 构建的是 **Debug**，拷贝 4 个文件，强杀/重启 ICC，并清空当天日志。**不要拿它当发版包**。

### 7.4 GitHub 预发布

- Workflow：`.github/workflows/release.yml`。
- 触发：push tag `v*`。
- 权限：`contents: write`（仓库自带 `GITHUB_TOKEN`）。
- 动作：`softprops/action-gh-release@v2`，`prerelease: true`，`name: ${tag} 测试版`，`body_path: docs/releases/${tag}.md`，`files: WpsInkSync/icpx/WpsInkSync.icpx`。
- 注意：
  - `docs/releases/vX.Y.Z.md` 必须存在，否则 workflow 失败。
  - `.gitignore` 忽略 `*.icpx`；发布前必须 `git add -f WpsInkSync/icpx/WpsInkSync.icpx`，否则 Actions checkout 没有资产。
  - tag 一旦推送就不要改；资产错了就 bump 版本重发。

发布检查清单：

```text
[ ] manifest Version = X.Y.Z
[ ] marketplace/com.yg.wpsinksync.yml version = X.Y.Z
[ ] docs/releases/vX.Y.Z.md 写好
[ ] CHANGELOG 顶部写 X.Y.Z（含日志指纹/实测）
[ ] dotnet build -c Release -t:Rebuild
[ ] 解压 icpx 检查 manifest 是 X.Y.Z
[ ] 本机安装 + 热键自测
[ ] git add -f WpsInkSync/icpx/WpsInkSync.icpx
[ ] commit + push main
[ ] git tag vX.Y.Z && git push origin vX.Y.Z
[ ] 等 Actions 成功
[ ] 校验 Release 资产大小/SHA256
```

### 7.5 PluginIndex / 市场

- 市场条目：`marketplace/com.yg.wpsinksync.yml`。
- 关键字段：`id: com.yg.wpsinksync`、`name: WPS 墨迹同步`、`version`、`downloadPath: releases/download/v{version}/WpsInkSync.icpx`、`readmePath: README.md`、`permissions: [Hotkeys, FileSystem]`。
- 历史坑：v0.1.1 的 bump 提交把 YAML 从 UTF-8 写成 GBK 乱码 + BOM，市场显示 `WPS 澧ㄨ抗鍚屾`；维护者删除了条目。
- 当前修复：PR #16，分支 `chuyue136:fix/wpsinksync-utf8-v0.2.0`，YAML 537 B、UTF-8 无 BOM、`name` 正确、`version: 0.2.0`。
- PR 合并后，PluginIndex 的 `gen-index` 会重建 `index.json`；市场应显示正确中文和 0.2.0。

---

## 8. 测试资产与实测基线

### 8.1 测试文档（桌面）

```text
C:\Users\Admin\Desktop\10月10日语文作业_孔子世家(1).docx
C:\Users\Admin\Desktop\10月10日语文作业_孔子世家(1).pdf
C:\Users\Admin\Desktop\必修二 U3 词组复习.docx
C:\Users\Admin\Desktop\图文表格混合示例文档.docx
C:\Users\Admin\Desktop\CNNIC第57次互联网发展报告.pdf
```

可选：`C:\Users\Admin\AppData\Local\Temp\wpsperf\perftest.docx`（40 页，含 `PAGE/Pxx-Lxx` 文本）。

### 8.2 已验证数据

| 场景 | 结果 | 环境 |
|---|---|---|
| docx 冷启动 6 格 | 504px，约 830ms | 本机 1920×1032，WPS 12.1.0.29161，150% |
| docx 2×6 下 + 2×6 上 | 最终累计差 1px | 本机 |
| docx 页首连按上键 | 实测 0px，墨迹不动 | 本机 |
| 桌面 docx 3×单格 | 253px，打样目视对齐 | 本机 |
| PDF 125% 6 格 | 发送 0.38–0.45s，整拍约 0.9s，往返回 0 | 本机 |
| PDF 页首/页尾 | 实测 0px，不漂 | 本机 |
| PDF 150% 6 格 | 546px，约 1.2s（v0.1.4 截图兜底） | 本机 |
| 希沃八代 288dpi docx | v0.1.2 已改善；v0.2.0 待复测 | 3840×2040 |
| 希沃八代 PDF | v0.1.4 有错位/回弹；v0.2.0 待复测 | 3840×2040 |

### 8.3 自测方式（不依赖用户）

1. 用 WPS 打开测试文档，确认窗口可见、文档区有焦点。
2. 确认 ICC 已装当前包并重启。
3. 用 PowerShell `keybd_event` 发 `Ctrl+Alt+PgDn` / `Ctrl+Alt+PgUp`，等价于用户按热键。
4. `Ctrl+Alt+K` 打样，截图（`ScrollProbe shot screen`）目视笔迹是否压在同一行。
5. 读日志：`press #N` → `COM/PDF UIA 逐拍` → `Δ`；连续上下后 `Δ` 应回到 0 或只差 1px。
6. 到边测试：页首连续按上键，日志应 `实测 0px`，墨迹不漂。

> 重要教训：独立探针进程直接向 WPS 发 `WM_MOUSEWHEEL`，在本机 WPS 12.1.0.29161 上可能完全无效（输入桌面/完整性差异）；但 ICC 插件内正常。**不要用“探针发消息没动”直接判断插件回归**，要在 ICC 里按热键自测。

---

## 9. 已知问题 / 坑（按优先级）

### 9.1 README 过期（高优先级，文档债）

`README.md` 仍写：

- “每条消息间隔 260ms”；
- “抓帧 + 块匹配”作为主路径；
- 旧的一拍一结算/整轮对账描述；
- 代码结构表缺 `WpsDocPos/WpsDispatch/WpsPdfPos`。

v0.2.0 实际是 COM/UIA 逐拍。**发布后应重写 README 的“原理 / 已知边界 / 代码结构”**，保持精简，细节留在 CHANGELOG。

### 9.2 “归位”按钮可能被跟随器拉回（低优先级）

- `Ctrl+Alt+R` / 归位按钮调用的是 `_shift.ResetAsync()`，只把墨迹累计位移清零。
- `WpsFollower` 里的 `_pendingPx`、`_anchor`、`_prev` 没有一起清；如果此时还有一拍在结算/复核，可能按旧账把墨迹又拉回去。
- 表现：归位后墨迹短暂回零，随后又偏。
- 建议修法（需用户同意）：
  1. 给 `WpsFollower` 加 `public void ResetState()`，清账本/锚点/标尺；
  2. 归位时先 `await _sendGate.WaitAsync()`，再 `_shift.ResetAsync()` + `follower.ResetState()`；
  3. 或把归位做成 follower 的方法，确保顺序。
- 难度：小；影响：低（演示时偶尔遇到）。

### 9.3 多 WPS 窗口 / 多标签

- `WpsScroll.Find` 选“面积最大的可见 WPS 窗口”；多文档并排/多标签时，可能选错目标。
- 切换标签后 `_docPos.Hwnd` 会变，代码会丢弃旧标尺并回退/重取；但同一窗口内换标签的边界未系统测试。
- 如果用户报“切标签后按了没反应/错位”，先看日志 `COM 标尺失效` / `没有 wps`。

### 9.4 旧版 WPS 的 PDF

- 没有 `KPdfScrollBarWheelFixed` 时，`WpsPdfPos` 失败，回退截图逐拍。
- 回退路径较慢（900/1400ms 窗口），且空白页可能误判；v0.2.0 已加 0.002 阈值和“看到变化后 actual<20 则信命令”的保护，但仍不如 UIA。
- 不要为了旧版把主路径改回截图。

### 9.5 高 DPI 初值

- 未标定 `_stepPhys=60` 是 96dpi 经验值，不按 DPI 预乘。
- 288dpi 第一次按键可能先按 60 推、再补差、再标定到 152/180；如果第一按刚好在页首/页尾（moved=0），不会标定，下一按仍按 60。
- 这是可接受的启动行为，但希沃复测要观察“第一次 6 格”是否最终对齐。

### 9.6 其它

- `ComGateMs` 实际是 **350**，不是注释里可能出现的 450；以代码为准。
- `WpsFollower.SendCore` 上方仍有“整批对账”旧注释；实际是逐拍，别被注释带偏。
- `deploy.ps1` 是 Debug，不是 Release。
- 仓库 `.gitignore` 忽略 `*.icpx`、`*.bmp`、`bin/obj`、`*.log`；发布资产要 force-add。
- 本机 WPS 32 位，ICC 64 位 UIAccess；COM 晚绑定是必需的，不要删 `WpsDispatch`。
- 不要用 `dynamic` 调 `GetPoint`（会报 `Could not convert argument 4`）；必须用 DISPID 接口/晚绑定。
- 横向 `dx` 路径存在但未实测；WPS 这版不响应 `HWHEEL`。
- 只认 WPS Writer / PDF；Word / 其它阅读器不跟。

---

## 10. 故障排查 playbook

### 10.1 先看日志关键字

| 日志 | 含义 / 处理 |
|---|---|
| `init: comp=True ink=True ...` | 插件加载；若 False 检查宿主服务 |
| `hotkey ... = False` | 热键没注册（多半 ICC 双实例）；杀掉多余实例 |
| `COM 标尺: 正文Top=...` | docx 标尺正常 |
| `COM 标尺不可用: ...` | docx COM 失败；看 `type=` / `hr=`；会回退 PDF UIA/截图 |
| `PDF UIA 标尺: 滚动值=...` | PDF 标尺正常 |
| `PDF UIA 标尺不可用: ...` | 旧版 WPS 或 UIA 未暴露；回退截图 |
| `press #N ... 命中x/y` | 发送了几格、几格看到页面动 |
| `COM 逐拍 实测 ... → 补差` | docx 正常结算 |
| `PDF UIA 逐拍 实测 ...` | PDF 正常结算 |
| `截图逐拍 实测 0px，到边 → 撤回` | 截图兜底到边；若频繁且页面其实动了，说明旧版/误判 |
| `外部滚动，不跟` | 正常：鼠标/手型/滚动条不跟 |
| `每格 60 ... → 84/152/180 ...` | 标定发生；看是否合理 |
| `没有 WPS 窗口` | `Find` 没找到；检查 WPS 是否最小化/标题为空/类名变化 |
| `修饰键未松开` | 热键 Ctrl/Alt 还按着；等松开或重试 |
| `下发超时/失败` | 消息可能已送达；保留乐观平移等复核 |
| `补差越界` | 异常大位移；应调查，不要忽略 |

### 10.2 快速定位“错位”

1. 确认日志里是 `COM 逐拍` / `PDF UIA 逐拍`，还是 `截图逐拍`。
2. 如果走截图：先解决为什么 COM/UIA 不可用（PDF 版本、窗口类、UIA 树）。
3. 看 `每格` 是否与文档/缩放匹配（docx 84/152；PDF 60/84/180 等）。
4. 看 `moved` 与 `expected`：到边时 moved=0 正常；非到边差很多说明标尺选错/窗口切了。
5. 看 `Δ` 是否在多次上下后回到 0；不回说明有账未清/标定错。
6. 用 `Ctrl+Alt+M` 打 MARK，把日志段发给下一个接手人。

---

## 11. 用户沟通习惯与协作规则（很重要）

用户是非技术背景的教师/开发者混合角色，重视**结果稳定**和**被尊重的决策权**。

### 11.1 必须遵守

1. **修 bug 前先报告原因和修复难度，经用户同意后再动手。**
   - 用户原话（m00001）：“先报告原因和修复难度，经我同意后再动手修；小概率 / 低影响问题可标注‘以后再说’。”
   - 用户 m02438 再次强调：“先定位 bug，改之前要经过我同意。”
2. **每次改完 bug，自觉把新版本安装到 ICC CE 并自测**。
   - 用户 m02234：“每次改完 bug 自觉在 ICC CE 里安装好你改好的版本。”
   - 用户 m01919 对“只让用户测”很不满：“你为啥不自己测试……我之前另一个 AI 一直是他自己测的。”
3. **日志始终开启**；让用户回传日志时，按日志指纹定位，不要空泛问“再试试”。
4. **技术方案自主判断**；只有产品取舍/需要用户拍板的才问。
5. **README 保持精简**；bug 修复记录、日志指纹、实测数据写进 `CHANGELOG.md`。
6. **版本以预发布（test）形式上传**；不推正式 latest。
7. **作者署名 / 许可证**：MIT，chuyue136；不要改。
8. **不要overclaim**：报告“实测数据 + 残余风险”，不要说“完全修复”除非真的验证过。

### 11.2 沟通风格

- 中文，直接，先结论后原因；用户不喜欢长时间沉默。
  - 用户 m02231 曾问“你思考了很长时间”，所以长任务要中途报进度。
- 用户会自己实测并回传日志/截图；要尊重他的现场观察，先复现再解释。
- 用户对“你又让我测”很敏感；能自己用 ICC 热键/打样/截图测的，先自己测。
- 用户会明确说“先定位，别改”；此时**绝对不要先改代码**。
- 用户接受“以后再说”的低优先级问题，但要明确标注，不要偷偷忽略。
- 用户喜欢看实际效果：打样笔迹 + 截图对比，比纯日志更有说服力。

### 11.3 关键原话（供新会话理解语气）

- “技术方案你自主判断；需要我定夺的决策再问我。”
- “修 bug 流程：先报告原因和修复难度，经我同意后再动手修。”
- “每次改完 bug 自觉在 ICC CE 里安装好你改好的版本。”
- “你为啥不自己测试……上下滚动也应该是命令就能完成的。”
- “先定位 bug，改之前要经过我同意。”
- “README 保持精简（bug 修复记录写进更新日志），版本以预发布（test）形式上传。”

---

## 12. 下一步建议（按顺序）

1. **等 PR #16 合并**；合并后检查市场索引/名称/版本。
2. **希沃八代 288dpi 复测 v0.2.0**：
   - docx 6 格冷启动、连续上下、到边；
   - PDF 100%/125%/150% 6 格、连续上下、到边；
   - 观察 `每格` 标定、`Δ` 是否回 0。
3. **更新 README 到 v0.2.0**（保持精简）。
4. **修“归位”与 follower 账本不同步**（先报告，等用户同意）。
5. 视情况处理多窗口/多标签边界。
6. 后续版本继续走“预发布 + Actions + PluginIndex PR”流程。

---

## 13. 新会话提示词（可直接复制）

```text
你是 ICC CE（InkCanvasForClass CE）WPS 联动插件的接手开发者。请先完整阅读工作区根目录的 HANDOFF.md，再开始任何操作。

项目：C:\Users\Admin\Desktop\VibeCoding\PVPer NPC\ICC CE WPS联动
仓库：chuyue136/WPSfollow（MIT，作者 chuyue136）
当前版本：v0.2.0 已发布为 GitHub 预发布；PluginIndex PR #16 已创建、等待维护者合并。

请遵守用户的硬性规则：
1. 修 bug 前先报告原因和修复难度，经用户同意后再改代码；小概率/低影响问题标注“以后再说”。
2. 每次改完 bug，自觉构建 Release、安装到 ICC CE、自己用热键/打样/截图实测，不要只让用户测。
3. 日志始终开启；需要用户回传时，按 HANDOFF.md 的日志指纹定位。
4. 技术方案自主判断，只有产品取舍才问用户；README 保持精简，修复记录和实测数据写进 CHANGELOG；版本用预发布（test）。
5. 不要回退到“截图猜滚动”作为 docx/PDF 主路径；docx 用 COM，PDF 用 UIA，旧版才回退截图。
6. 不要混用物理像素和 DIP；换算只走 tools/ScrollProbe/Units.cs。
7. 发布用 tag 触发 GitHub Actions；.icpx 被 gitignore，记得 git add -f。

当前优先事项：
- 等 PR #16 合并后核对插件市场名称/简介/0.2.0；
- 希沃八代 288dpi 用 v0.2.0 复测 docx/PDF、100%/125%/150%、连续上下、到边；
- 更新过期的 README；
- 向用户报告“归位按钮可能被 follower 账本拉回”的已知问题，等同意后再修。

用户非技术背景，沟通用中文、先结论后原因；长任务中途报进度，不要长时间沉默。
```

---

## 14. 附：关键 commit / 文件校验

```text
1aa58bf  ci: tag 触发预发布并上传 icpx 资产
467e4db  release: v0.2.0 PDF 改用 UIA 标尺，docx/PDF 全部脱离截图判定
0e67e67  docs(market): 插件市场索引版本更新到 0.1.2
c6b4a46  fix: 统一物理像素/DIP 单位并改为一拍一结算（v0.1.2）
```

```text
WpsInkSync.icpx
46,380 B
SHA256 688164fcfe9e3a8f6f64797fcba8fc65bd2178569a3ab2c66ca6ed548870a742
```

```text
Release: https://github.com/chuyue136/WPSfollow/releases/tag/v0.2.0
PR:      https://github.com/InkCanvasForClass/PluginIndex/pull/16
```
