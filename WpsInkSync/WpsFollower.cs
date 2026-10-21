using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScrollProbe;

namespace WpsInkSync;

/// <summary>
/// WPS 滚动跟随：定时抓 WPS 窗口帧 → 2D 带匹配算出内容位移 → 把同样的位移反向应用到墨迹。
/// 只在鼠标模式（非画笔模式）下工作，所以不会抢画笔的手感。
/// 测量细节见 tools\ScrollProbe\Capture.cs（同一份源码，链接编译）。
/// </summary>
sealed class WpsFollower : IDisposable
{
    // 实测：IdleMs 400 → 700 只省 0.5 个点 CPU（大头是 ICC 自己的界面开销），所以取 400 保跟手
    const int IdleMs = 400;         // 静止时的采样周期（兜底：滚轮钩子没抓到的滚动，比如拖滚动条）
    const int BurstMs = 60;         // 刚检测到画面变化后的连续采样周期（跟手）
    const int BurstHoldMs = 700;    // 变化后保持高频多久
    const int MaxShift = 320;       // 单帧最大位移搜索范围（px）
    const double PrefilterThreshold = 1.5;

    readonly InkShift _shift;
    readonly Func<bool> _enabled;
    readonly Func<bool> _mouseMode;
    readonly Action<string> _log;

    Thread _thread;
    volatile bool _stop;

    IntPtr _hwnd;
    Capture.Img _prev, _cur;        // 双缓冲：轮流抓帧，互不覆盖，也不产生垃圾
    double _dpiScale = 1.0;         // WPS 窗口的 物理px/DIP（96dpi=1，希沃八代 288dpi=3）
    string _lastDiag = "";
    DateTime _burstUntil = DateTime.MinValue;
    WheelTrigger _wheelHook;
    int _misses;                    // 连续认不出画面的次数

    // 控制模式账本：按下按钮时先乐观平移墨迹（零延迟），测量到实际位移后再补差。
    // 单位铁律：_pendingPx 一律是**物理像素**（与抓帧测量同口径），只在推墨迹时换 DIP。
    // 早前把"学到的物理像素"直接当 DIP 推，希沃八代 300% 缩放上墨迹走 3 倍、和页面彻底脱离。
    readonly object _gate = new();
    readonly SemaphoreSlim _sendGate = new(1, 1);
    volatile bool _sending;          // 正在逐格下发滚轮消息：这期间不结算（帧只反映"发到一半"）
    double _pendingPx;
    int _pendingNotch;
    int _batchHits;                 // 本轮发送中"看到页面动"的次数，用来判断是否每格都真滚了
    int _batchPresses;              // 本轮下发了几条命令（每条命令有一处不需要 hit 的最后一格）
    int _gen;                       // 每按一次按钮自增，用来作废"按下之前"抓到的那一帧
    int _still;                     // 连续静止帧数
    int _seq;                       // 按键序号：日志里 press #N 与它的复核结论 #N 对得上
    bool _measuredSample;           // 本次结论来自'直接测量'（不是命令假设），自学习门槛可以放宽
    DateTime _lastPressAt = DateTime.MinValue;   // 最近一次按键时间（用来区分"动画残余"和"外部滚动"）
    // 每格滚轮在**屏幕物理像素**上推动页面的量（文档缩放 × 字号 × DPI 的函数）。
    // 未标定前按 96dpi 的 60 乘上当前缩放猜一个；第一拍单格独立测量出来立刻标定。
    // 按"尺子"分开存三套：docx(COM) 与 PDF(UIA) 的每格步长本来就不同（本机 280% 下 84 vs 60），
    // 共用一个值的话，切文档之后每次按键都按旧步长多推/少推一截（实测 PDF 每次固定补差 144px，
    // 表现为"先冲出去再顿回来"）。截图兜底单独一套，免得它的粗测量污染精确标定值。
    enum Ruler { Shot = 0, Com = 1, Pdf = 2 }
    readonly double[] _stepBy = { Units.GuessStepPhys96, Units.GuessStepPhys96, Units.GuessStepPhys96 };
    readonly bool[] _calBy = new bool[3];       // 这套尺子是否已标定（标定值单位＝物理像素）
    readonly double[] _candBy = new double[3];  // 学习候选：大跳变要连续两次一致才采纳
    double StepOf(Ruler r) => _stepBy[(int)r];
    bool CalOf(Ruler r) => _calBy[(int)r];
    double CandOf(Ruler r) => _candBy[(int)r];
    /// <summary>当前文档在用的尺子：docx=COM，PDF=UIA，都没有=截图兜底。</summary>
    Ruler RulerNow => _docPos != null ? Ruler.Com : _pdfPos != null ? Ruler.Pdf : Ruler.Shot;
    /// <summary>DPI 变了：三套"每格"按新缩放重估，并作废标定（物理比例真的变了）。</summary>
    void ResetSteps()
    {
        for (var i = 0; i < 3; i++) { _stepBy[i] = Units.GuessStepPhys96 * _dpiScale; _calBy[i] = false; _candBy[i] = 0; }
    }
    /// <summary>换窗口/换文档：已标定的槽保留（"每格"是文档缩放×字号的物理事实，
    /// 比 96dpi 的猜值可信），只把没标定过的槽按当前缩放预乘一下。</summary>
    void ScaleGuesses()
    {
        for (var i = 0; i < 3; i++)
            if (!_calBy[i]) { _stepBy[i] = Units.GuessStepPhys96 * _dpiScale; _candBy[i] = 0; }
    }
    DateTime _settleUntil = DateTime.MinValue;   // 翻页动画期间不抓帧（中间帧会把基线带脏）
    // 对账锚点：一轮连续按键开始前的基线帧。一轮结束时（画面静止且静置够久）拿它跟当前帧比
    // 总位移，再跟墨迹这一轮实际走的量对账，差多少补多少。多格批次"测不准只能信命令"时
    // 攒下的误差（实测 10 拍被 WPS 吞一拍 = 一整格 86px）全靠这一步收回来。
    Capture.Img _anchor;
    double _anchorInkY;
    // 优先用 WPS 自己的对象模型量页面位移（物理像素，精确、不受空白页边影响）。
    // 拿不到（PDF / 旧版 WPS / 权限）才回退到截图匹配。
    WpsDocPos _docPos;
    // PDF(Qt) 没有 RangeFromPoint，但 UIA 滚动条 RangeValue 就是物理像素位置；优先用它，不再抓帧。
    WpsPdfPos _pdfPos;
    int _roundNotch;                // 这一轮（自建锚点起）一共下发了多少格（带方向），对账标定用
    int _anchorTries;
    DateTime _zeroAt = DateTime.MinValue;   // 上一次"页面疑似 0px"的时刻（要 300ms 后复核一次才准撤墨迹）

    public long Ticks, Measured, Applied;
    public double LastDyPx, LastDxPx;
    long _cConfirmed, _cNoMove, _cUnclear, _cTrustBig;   // 心跳里的判断计数，用来看"哪种判断在主导"
    DateTime _lastBeat = DateTime.UtcNow;
    public double StepPx => Units.PhysToDip(StepOf(RulerNow), _dpiScale);   // 对外仍报 DIP（状态栏口径）
    double StepDip => Units.PhysToDip(StepOf(RulerNow), _dpiScale);
    // 还没标定时（多半是刚打开 WPS 的第一拍）WPS 首滚启动明显更慢：静置判据多等一会儿，
    // 否则会把"页面还没开始动"误判成"到边没滚"，把刚推出去的墨迹撤回来 —— 首滚差一行就是这么来的。
    int StillGateMs => (CalOf(Ruler.Com) || CalOf(Ruler.Pdf)) ? 900 : 1200;
    // COM 标尺读到的是精确值，WPS 的 docx 滚动基本是瞬移/短动画，450ms 足够定稿；
    // 但刚打开 WPS 的第一拍启动明显慢，仍按未标定多等，避免把"还没开始动"当成到边。
    int ComGateMs => 350;

    string _status = "待机";
    string _lastStatusKind = "";
    /// <summary>状态文本变化（按首词判断类别）时落一条日志，方便事后复盘。</summary>
    public string Status
    {
        get => _status;
        set
        {
            var kind = value.Split(' ')[0];
            if (kind != _lastStatusKind) { _lastStatusKind = kind; _log("status: " + value); }
            _status = value;
        }
    }

    public WpsFollower(InkShift shift, Func<bool> enabled, Func<bool> mouseMode, Action<string> log)
    {
        _shift = shift;
        _enabled = enabled;
        _mouseMode = mouseMode;
        _log = log;
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "WpsInkFollower" };
        _thread.Start();
        // 滚轮一动就进高频态：这样即便平时 400ms 才抓一次帧，滚起来也是跟手的
        _wheelHook = new WheelTrigger(() => _burstUntil = DateTime.UtcNow.AddMilliseconds(BurstHoldMs));
        _wheelHook.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _wheelHook?.Dispose();
        DisposeDocPos();
        DisposePdfPos();
        _prev = null;
    }

    /// <summary>
    /// 控制模式：我们命令 WPS 滚 <paramref name="notches"/> 格（dir=+1 内容上移，-1 下移），
    /// 墨迹立刻按"已知格数 × 每格像素"跟着走，不用等测量。实际位移由 Tick 复核后补差。
    /// </summary>
    public async Task<bool> PressAsync(int notches, int dir)
    {
        if (!_enabled()) { Status = "跟随已关闭，忽略"; return false; }
        if (notches <= 0 || dir == 0) return false;

        // 这一拍要推多少，得等"尺子"定下来才能算（下面是先立尺子，再按这把尺子的每格算）。

        // 首选：从 WPS 对象模型取一个正文 Range 当"页面刻度尺"。
        // 这一拍按下前记下它的屏幕 Top，结算时再读一次，差值就是页面真正滚了多少物理像素。
        // 拿不到（PDF 的 Qt 外壳、旧版 WPS）就保持 null，走下面的截图锚点兜底。
        // 放在乐观平移之前：这一刻页面还没被命令推动。
        if (_docPos == null)
        {
            var doc = WpsScroll.FindDocument(out var top, out var pcx, out var pcy, out _);
            if (doc != IntPtr.Zero)
            {
                var pos = WpsDocPos.TryCreate(doc, pcx, pcy, out var why);
                if (pos != null)
                {
                    lock (_gate)
                    {
                        if (_docPos == null) _docPos = pos;
                        else { pos.Dispose(); pos = null; }
                    }
                    if (pos != null) _log($"COM 标尺: 正文Top={pos.Top}px（{_hwnd.ToInt64():X} → 0x{doc.ToInt64():X}）");
                }
                else if (why.Length != 0) _log($"COM 标尺不可用: {why}");
            }

            // PDF 没有 COM 标尺，但它的 Qt 滚动条在 UIA 里是 KPdfScrollBarWheelFixed，
            // RangeValue 就是屏幕物理像素。拿到它就不用再抓整窗截图做内容匹配。
            if (_pdfPos == null && top != IntPtr.Zero)
            {
                var pp = WpsPdfPos.TryCreate(top, out var pwhy);
                if (pp != null)
                {
                    lock (_gate)
                    {
                        if (_pdfPos == null) _pdfPos = pp;
                        else { pp.Dispose(); pp = null; }
                    }
                    if (pp != null)
                    {
                        pp.TryRead(out var v0);
                        _log($"PDF UIA 标尺: 滚动值={v0}px（0x{top.ToInt64():X}）");
                    }
                }
                else if (pwhy.Length != 0) _log($"PDF UIA 标尺不可用: {pwhy}");
            }
        }

        // 尺子已经定下来了：按**这一把尺子**的每格算这一拍要推多少。
        // 同一份文档里 COM/UIA/截图三把尺子的每格步长并不相同，切文档时必须各用各的，
        // 否则新文档的第一拍会按旧步长冲出去、结算时再顿回来（实测 PDF 每次补差 144px）。
        var pxPhys = StepOf(RulerNow) * notches * dir;          // 账本单位：物理像素
        var inkDip = Units.InkDipFor(pxPhys, _dpiScale);        // 墨迹单位：DIP（内容上移 = 墨迹上移为负）

        // 第一拍如果还没有对账锚点（刚启动/刚换文档），先抓一张"按键之前"的帧。
        // 不抓的话这一拍没有任何基线可比，Tick 只能把它当新基线，首滚的实际位移就永远没人核对——
        // 用户看到的"首次打开 WPS 后下滚差一行"正是这个。只在缺锚点时抓，后续按键零开销。
        Capture.Img pre = null;
        if (_docPos == null && _pdfPos == null && _anchor == null && !(_prev != null && _still >= 2))
            pre = Capture.Grab(_hwnd, false, null);

        // 先在**发送队列之外**把账本记上、把墨迹推出去：这样连按时第二、第三下会立刻看到笔迹移动，
        // 不用排在上一批 6 格的发送后面。页面由下面的单线程发送队列按序追上，最后整批对账一次。
        int seq;
        bool canAnchor;
        double anchorMean = 0;
        lock (_gate)
        {
            _pendingPx += pxPhys;
            _pendingNotch += notches * dir;
            _roundNotch += notches * dir;
            _seq++;
            seq = _seq;
            _gen++;                                                          // 让"按下前抓到的帧"作废

            // 建对账锚点：一轮连续按键只在第一拍克隆基线帧（8MB 一次，之后整轮复用）。
            // 只能用"已经静止两拍"的帧当锚点：按键间隔 500ms 时 _prev 可能还是动画中间帧，
            // 拿它当锚点，一轮结束后怎么都对不上（实测 0.384/0.755 全错）。
            // 必须建在乐观平移**之前**：_anchorInkY 要记录这一拍还没推墨迹时的位置。
            // 而且必须和账本在同一把锁里：两拍并发进来时不能各建一个锚点，
            // 否则后建的锚点会把先建那一拍的期望位移吞掉。
            // 优先用跟随线程已经确认静止的 _prev；还没有的话用刚抓的 pre（首拍专用）。
            var anchorSrc = _prev != null && _still >= 2 ? _prev : pre;
            canAnchor = _anchor == null && anchorSrc != null
                        && anchorSrc.Bgra != null && !anchorSrc.Blank;

            // 时间戳必须早于阻塞下发：发送期间跟随线程不能拿旧锚点下结论。
            var stamp = DateTime.UtcNow;
            _lastPressAt = stamp;
            _still = 0;
            // 发送前就把"动画沉降期"设好：这一批结束前跟随线程不要抓帧。
            // 以前这里按格数算 300~600ms，但 WPS 的滚动动画常比这长；测量偏早会把"半格"当整格标定。
            // 现在用和静止判据同一个窗口（未标定 1200ms / 已标定 900ms），只影响后台复核，不挡按键。
            var settleEarly = (_docPos != null || _pdfPos != null) ? ComGateMs : StillGateMs;
            var untilEarly = stamp.AddMilliseconds(settleEarly);
            if (untilEarly > _settleUntil) _settleUntil = untilEarly;
            _burstUntil = stamp.AddMilliseconds(BurstHoldMs);

            if (canAnchor)
            {
                _anchor = new Capture.Img
                {
                    W = anchorSrc.W, H = anchorSrc.H, How = anchorSrc.How, Mean = anchorSrc.Mean,
                    Bgra = (byte[])anchorSrc.Bgra.Clone()
                };
                _anchorInkY = _shift.Y;
                _anchorTries = 0;
                _roundNotch = notches * dir;   // 本轮从这一拍算起（别把这一拍自己丢掉）
                _batchHits = 0;
                _batchPresses = 0;
                _zeroAt = DateTime.MinValue;
                anchorMean = anchorSrc.Mean;
                // 比对基线也从这张“按键前”帧开始：否则首拍之后的第一个 Tick 若 _prev 还是 null，
                // 它只能把滚动后的帧当基线，这一拍的位移就没人量；再按一下就会把两拍并成一拍算错。
                // 注意接管的是 anchorSrc（pre 或 _prev），不是 _anchor 本身——_cur 会复用缓冲，不能让它改到锚点。
                _prev = anchorSrc;
            }
        }
        if (canAnchor) _log($"锚点: 建在 {Units.Describe(StepOf(RulerNow), _dpiScale)} 墨迹Y={_shift.Y:0} mean={anchorMean:0}");

        // PDF(Qt) 没有 WPS 的 Window.RangeFromPoint，拿不到 COM 标尺，只能在发送前抓一张帧，
        // 发完看画面有没有真的变。docx 走 COM 时不需要这张 8MB 的帧。
        Capture.Img shotBefore = null;

        // 乐观平移放在发送队列里面：连按时第二拍会等上一拍结算完再推。
        // 这样“推墨迹 / 补差 / 下一拍推墨迹”严格按序，不会被排队中的下一拍插进来算错账。
        // 6 格一发正常 300ms 左右，手感仍然是按下去就动，只是连按会排队。
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_docPos == null && _pdfPos == null) shotBefore = Capture.Grab(_hwnd, false, null);
            if (inkDip != 0) await _shift.ApplyAsync(0, inkDip).ConfigureAwait(false);   // 内容上移 = 墨迹上移
            return SendCore(notches, dir, pxPhys, inkDip, seq, shotBefore);
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>只负责把这一拍发出去；测量与纠偏交给跟随线程的整批对账，不再阻塞下一次按键。</summary>
    bool SendCore(int notches, int dir, double pxPhys, double inkDip, int seq, Capture.Img shotBefore)
    {
        // 刻度尺是在按键那一刻取的；如果用户中途切了标签/换文档，旧 Range 已经不属于当前页面，
        // 再用它会量出 0 或旧文档的位移。发现子窗口变了就丢弃，回退截图复核。
        if (_docPos != null)
        {
            var docNow = WpsScroll.FindDocument(out _, out _, out _, out _);
            if (docNow != IntPtr.Zero && docNow != _docPos.Hwnd)
            {
                _log($"COM 标尺失效: 文档窗口 0x{_docPos.Hwnd.ToInt64():X} → 0x{docNow.ToInt64():X}，回退截图");
                DisposeDocPos();
            }
        }
        if (_pdfPos != null)
        {
            var topNow = WpsScroll.Find(out _);
            if (topNow != IntPtr.Zero && topNow != _pdfPos.Hwnd)
            {
                _log($"PDF UIA 标尺失效: 窗口 0x{_pdfPos.Hwnd.ToInt64():X} → 0x{topNow.ToInt64():X}");
                DisposePdfPos();
            }
        }
        // 真正发送前才取刻度尺：上一拍结算时已经把它扔了；如果这里还是 null（上一拍刚结束、
        // 或者刚换文档），现在页面是静止的，取到的 Top 就是这一拍的起点。
        if (_pdfPos == null) EnsureDocPos();
        if (_docPos == null && _pdfPos == null) EnsurePdfPos();
        // 尺子可能在上一段里刚换过（旧标尺失效被丢弃、这里又重建），重新取一次：
        // 之后的日志与"每格"口径都以"发送前这一刻"的尺子为准。
        var ruler = RulerNow;
        _sending = true;
        bool sent;
        string diag;
        int hits;
        // PDF 的 UIA 值是“活”的，必须在发送前记下锚点；发完再读已经是终点，moved 会永远 0。
        var pdfAnchor = 0;
        var pdfAnchorOk = _pdfPos == null || _pdfPos.TryRead(out pdfAnchor);
        try { sent = WpsScroll.Send(notches, dir, _docPos, _pdfPos, out diag, out hits); }
        finally { _sending = false; }

        if (!sent)
        {
            // "连窗口都找不到" = 这条消息根本没有对象，页面不可能动，必须当场把墨迹搬回去。
            if (diag != null && diag.Contains("没有 wps"))
            {
                if (inkDip != 0) _shift.ApplyAsync(0, -inkDip).GetAwaiter().GetResult();
                lock (_gate) { _pendingPx -= pxPhys; _pendingNotch -= notches * dir; _roundNotch -= notches * dir; }
                _log($"press #{seq} {dir * notches} 格没有目标窗口 → 撤回墨迹 {_shift.Status}");
                Status = "没有 WPS 窗口，未动";
                return false;
            }
            // 发不出去不等于 WPS 没动：SendMessageTimeout 超时（WPS 忙着重绘）时消息可能已经送达并被处理。
            // 保留乐观平移，让整批对账去证伪。
            _log($"press #{seq} {dir * notches} 格下发超时/失败: {diag} → 保留乐观平移等复核 {_shift.Status}");
            Status = "下发超时，等复核: " + diag;
            _burstUntil = DateTime.UtcNow.AddMilliseconds(BurstHoldMs);
            _lastPressAt = DateTime.UtcNow;
            _still = 0;
            return false;
        }

        // 这一拍当场结算。发送仍由 _sendGate 串行，所以每拍只测自己这一段的位移；
        // 不会再把连按攒成一大笔账（到边时那笔账会被整批撤回，墨迹直接甩出去）。
        // 墨迹在拿 _sendGate 之后、发送之前推出去；下一拍要等这一拍结算完才会推，
        // 顺序严格是“推→发→补→下一拍推”，连按时不会互相踩账。
        if (_docPos != null) SettleComPress(pxPhys, notches * dir);
        else if (_pdfPos != null) SettlePdfPress(pxPhys, notches * dir, pdfAnchor, pdfAnchorOk);
        else if (shotBefore != null) SettleShotPress(shotBefore, pxPhys, notches * dir);


        _burstUntil = DateTime.UtcNow.AddMilliseconds(BurstHoldMs);   // 让复核尽快跑
        int notchesTotal;
        lock (_gate)
        {
            _batchHits += hits;
            _batchPresses++;
            notchesTotal = Math.Abs(_pendingNotch);
        }
        // 发送结束后再留完整的动画沉降期，然后让跟随线程用"这一整批"的锚点帧测总位移。
        // 这里不按拍结算：连按时每拍都等结算会把下一次发送挡住，正是"按一下要等几百毫秒"的来源。
        var settle = (_docPos != null || _pdfPos != null) ? ComGateMs : StillGateMs;
        var until = DateTime.UtcNow.AddMilliseconds(settle);
        if (until > _settleUntil) _settleUntil = until;
        _lastPressAt = DateTime.UtcNow;
        _still = 0;
        _log($"press #{seq} {dir * notches} 格 墨迹({inkDip:0.0}DIP={pxPhys:0}物理px) {Units.Describe(StepOf(ruler), _dpiScale)} 账本={notchesTotal * StepOf(ruler):0}物理px {diag} {_shift.Status}");
        Status = $"命令 {dir * notches} 格 → 墨迹({inkDip:0}DIP) 每格{StepDip:0}DIP ({(_mouseMode() ? "鼠标" : "画笔")}模式)";
        return true;
    }
    void Loop()
    {
        while (!_stop)
        {
            try { Tick(); }
            catch (Exception ex) { _log("follower: " + ex.GetType().Name + " " + ex.Message); }
            var burst = DateTime.UtcNow < _burstUntil;
            if ((DateTime.UtcNow - _lastBeat).TotalSeconds >= 30)
            {
                _lastBeat = DateTime.UtcNow;
                _log($"心跳 {Units.Describe(StepOf(RulerNow), _dpiScale)}{(CalOf(Ruler.Com) || CalOf(Ruler.Pdf) || CalOf(Ruler.Shot) ? " 已标定" : " 未标定")} 墨迹{_shift.Status} 判断:确认{_cConfirmed}/没滚{_cNoMove}/分不开{_cUnclear}/大批量信命令{_cTrustBig} tick={Ticks}");
            }
            Thread.Sleep(burst ? BurstMs : IdleMs);
        }
    }

    /// <summary>抓帧 → 与上一帧比对 → 匹配 → 平移墨迹，然后把当前帧变成新的基线。</summary>
    void Tick()
    {
        Ticks++;
        if (!_enabled()) { Reset(); Status = "已关闭"; return; }
        if (DateTime.UtcNow < _settleUntil) { _burstUntil = _settleUntil; Status = "翻页动画中…"; return; }

        // 每 20 拍（idle 下约 8s）重找一次窗口：WPS 换文档/换标签会**重建窗口**，
        // 旧句柄还在但已经不可见，继续抓它只会得到黑帧或过期画面（实测 mean=18.7 的黑帧），
        // 拿它去比对就会算出瞎位移。句柄一变立刻重置基线。
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || IsIconic(_hwnd) || !IsWindowVisible(_hwnd) || Ticks % 20 == 0)
        {
            var h2 = WpsScroll.Find(out var diag);
            var changed = h2 != _hwnd;
            _hwnd = h2;
            if (changed) Reset();
            if (_hwnd == IntPtr.Zero)
            {
                Status = "没找到 WPS 窗口";
                if (diag != _lastDiag) { _lastDiag = diag; _log("找窗口: " + diag); }
                return;
            }
            if (changed)
            {
                var dpi = WpsScroll.Dpi(_hwnd);
                _dpiScale = Units.Scale(dpi);
                // 起步值 60 是 96dpi 口径，没标定过的槽按当前缩放预乘。已标定的槽保留：
                // WPS 换文档/换标签会重建窗口，三把尺子（截图/docx/PDF）各自的值不能因为换窗口就丢掉，
                // 否则"docx 标好 80、切到 PDF 又打回 60"的老毛病在真实场景里必然复现。
                ScaleGuesses();
                Status = $"已锁定 WPS 窗口 0x{_hwnd.ToInt64():X} dpi={dpi}";
                _log($"锁定窗口 0x{_hwnd.ToInt64():X} {diag} dpi={dpi}（每格 docx {Units.Describe(StepOf(Ruler.Com), _dpiScale)} / PDF {Units.Describe(StepOf(Ruler.Pdf), _dpiScale)} / 截图 {Units.Describe(StepOf(Ruler.Shot), _dpiScale)}）");
            }
        }

        // DPI 每拍核一次：教室里既有 96dpi 的笔记本，也有 288dpi 的希沃大屏，改缩放/换屏之后
        // 旧比例会把墨迹推歪 3 倍（2026-10-18 希沃八代翻车的根因）。比例一变就作废账本、重新标定。
        if (_hwnd != IntPtr.Zero)
        {
            var sc = Units.Scale(WpsScroll.Dpi(_hwnd));
            if (Math.Abs(sc - _dpiScale) > 0.01)
            {
                _log($"DPI 变化 {_dpiScale * 96:0} → {sc * 96:0} → 作废账本、每格重新标定（旧值 {Units.Describe(StepOf(RulerNow), _dpiScale)}）");
                _dpiScale = sc;
                ResetSteps();
                Reset();
                return;
            }
        }

        // 先看两个"别抓帧"的门，再抓。以前是抓完才看 _sending：发送期间跟随线程每 60ms 抢一次
        // GrabGate，和 WpsScroll.WaitPageMoved 的抓帧互相排队，正好把我们要省的那点时间又赔回去。
        if (_sending) { Status = "下发中，跳过复核"; return; }
        // 按键正在发送/结算（_sendGate 被占）：此时账本里可能是下一拍刚乐观推出去的墨迹，
        // 截图复核会把它误当成页面已经滚了。等这一拍结算完再看。
        if (_sendGate.CurrentCount == 0) { Status = "按键处理中，跳过复核"; return; }
        var settleLeft = (_settleUntil - DateTime.UtcNow).TotalMilliseconds;
        if (settleLeft > 0) { Status = $"沉降期 {settleLeft:0}ms"; return; }

        var gen = _gen;
        // 账本快照必须和 gen 一起取：抓帧之后再取，会把"抓帧之后才按下的那几拍"算进来，
        // 而那几拍的页面位移根本还没出现在这一帧里（连按时会越按越偏）。
        double expPx0; int expN0;
        lock (_gate) { expPx0 = _pendingPx; expN0 = _pendingNotch; }

        // COM 文档的每一次按键，都在 PressAsync 持有 _sendGate 时逐拍结算掉了；
        // 这里不再按账本批量结算，避免“抓快照后新按的一拍”被当成旧账重复算。
        var img = Capture.Grab(_hwnd, false, _cur);
        _cur = img;
        if (img.Bgra == null || img.Blank) { Reset(); Status = "抓帧失败"; return; }
        // 正在一条一条往下发滚轮消息：这期间抓到的帧只反映"发到一半"，
        // 拿它结算会把 168px 当成 6 格的总位移（→ 每格学成 28，墨迹直接错位）。
        if (_sending) { Status = "下发中，跳过复核"; return; }
        // WPS 重绘/换文档时会短暂返回整块黑帧（实测 mean=18.7），拿它当基线或拿去比对都会算出瞎位移。
        if (img.Mean < 40 && _prev != null && _prev.Mean > 100) { Status = $"帧过暗({img.Mean:0}，WPS 重绘中)，跳过"; return; }
        if (gen != _gen) { Status = "帧过期(刚按了按钮)"; return; }   // 这帧是按下之前拍的，别拿它结算

        if (_prev == null || _prev.W != img.W || _prev.H != img.H)
        {
            Swap();
            Status = $"基线 {img.How} {img.W}x{img.H} mean={img.Mean:0}";
            return;
        }

        var diff = Sample(img, _prev);
        if (diff < PrefilterThreshold)   // 画面基本没变，省掉一次完整匹配
        {
            if (gen != _gen) return;     // 刚又按了一下：这帧已经不能代表"这一批的终点"
            // 画面没动，但我们刚按过按钮 → 可能到顶/到底了，页面没滚，把乐观平移撤回。
            // 必须连续两帧都静止才认：按下按钮后 WPS 要过一会儿才开滚动，只凭一帧会把正常滚动误判成"到边"。
            _still++;
            Swap();
            // "静止"不等于"到边"：按下后 WPS 可能还没开始动画。要连续两帧静止、沉降期已过、
            // 且距按键 ≥900ms 才敢认定页面真的没动（否则会把正常滚动误判成到边，把墨迹拉回去）。
            if (_still < 2 || DateTime.UtcNow < _settleUntil ||
                (DateTime.UtcNow - _lastPressAt).TotalMilliseconds < StillGateMs)
            {
                Status = $"静止 diff={diff:0.000} mean={img.Mean:0.0}";
                return;
            }
            // 账本已结清 + 画面静止 = 墨迹和页面此刻是**对齐的**：把这一帧当下一轮的对账锚点。
            // 以前只在按键时(且 _still>=2)才建锚点，连按时永远建不出锚点 → 整轮没有任何对账兜底，
            // 一路"信命令"把误差钉死（实测页首连按 20 次，墨迹被甩到 +6468px，页面一动没动）。
            if (_anchor == null)
            {
                double pend; int pn;
                lock (_gate) { pend = _pendingPx; pn = _pendingNotch; }
                if (pend == 0 && pn == 0)
                {
                    _anchor = new Capture.Img
                    {
                        W = img.W, H = img.H, How = img.How, Mean = img.Mean,
                        Bgra = (byte[])img.Bgra.Clone()
                    };
                    _anchorInkY = _shift.Y;
                    _anchorTries = 0;
                    _roundNotch = 0;
                    _zeroAt = DateTime.MinValue;
                    _log($"锚点: 静置后重建 墨迹Y={_shift.Y:0} mean={img.Mean:0}");
                    return;
                }
            }
            // 有对账锚点时，用"整轮总位移"对账（兜住多格批次只能信命令的误差），
            // 它涵盖"页面没动"的情形，所以不再走下面的账本撤回。
            if (_anchor != null)
            {
                // 只有真下了结论才结账。对账推迟（动画没开始 / 0px 待复核）时必须把账本留着，
                // 否则这笔账再也没人核对。
                if (Reconcile(img)) TakePending(out _, out _);
                return;
            }
            double exp; int expN;
            var seqAt = _seq;
            TakePending(out exp, out expN);
            if (exp != 0)
            {
                var back = Units.PhysToDip(exp, _dpiScale);   // 账本是物理像素，墨迹要 DIP
                _shift.ApplyAsync(0, back).GetAwaiter().GetResult();
                _log($"#{seqAt} 页面没动(到边?) → 撤回墨迹(0,{back:0}DIP) exp={exp:0}物理px {_shift.Status}");
            }
            Status = exp != 0 ? $"页面没动 → 撤回({Units.PhysToDip(exp, _dpiScale):0}px)" : $"静止 diff={diff:0.000} mean={img.Mean:0.0}";
            return;
        }
        _still = 0;
        _burstUntil = DateTime.UtcNow.AddMilliseconds(BurstHoldMs);
        if (gen != _gen) return;         // 匹配/纠偏前再确认一次，别把新按下的那拍算进旧帧

        var sw = Stopwatch.StartNew();
        _measuredSample = false;

        // 控制模式下我们先验优先。关键前提：能走到这里说明画面确实变了（预筛已排除静止），
        // 也就是说页面滚了。所以"没滚(dy=0)"这个假设拿不出证据时**绝不撤回墨迹**——
        // 之前的"一键大跳"就是它造成的：连按 10 下攒出 expected=600px，WPS 实际滚了 900px，
        // 两个固定假设都对不上 → 判"没滚" → 把 600px 全撤回 → 墨迹反向蹦一大步。
        // 现在改成在预期附近扫几格取最像的那个，扫不到就按命令结算（信我们自己的账）。
        Capture.Shift2D m = null;
        var confident = false;
        if (expPx0 != 0)
        {
            var dyMove = (int)Math.Round(expPx0);
            var step = (int)Math.Max(20, Math.Round(StepOf(Ruler.Shot)));   // 物理像素（与 dyMove 同口径）

            // 先判"页面根本没动"（到顶/到底）。这个判据必须极硬，否则把正常滚动误判成没动
            // 就等于把一整批乐观平移撤回，墨迹与页面直接差出一大段（用户踩到的"像素不同步"）。
            // 三道闸：①最后一帧必须在按键 900ms 之后（动画刚开始时画面几乎没变，最容易骗过判据）；
            //        ②预筛差异必须很小（画面几乎没变，允许光标闪烁那种程度）；
            //        ③"停在原地"必须近乎完美且甩开"按预期移动"一大截。
            var quiet = (DateTime.UtcNow - _lastPressAt).TotalMilliseconds >= StillGateMs;
            if (quiet && diff <= 8
                && Capture.ScorePair(_prev, img, dyMove, out var sMove0, out var sStay0)
                && sStay0 >= 0.995 && sMove0 <= 0.95)
            {
                m = new Capture.Shift2D { Dy = 0, Dx = 0, Score = sStay0, SecondScore = sMove0 };
                confident = true;
            }

            if (!confident)
            {
                // 一次批量的账本只有两种真相：滚了 exp，或者到边没滚（dy=0）。
                // 判据必须用同一条带、同一口径比这两个假设，谁赢听谁：
                //   静止假设甩开移动假设 ≥0.02 → 到边，撤回（这一步解决了到顶连按不归位）；
                //   移动假设近乎完美且甩开静止 ≥0.02 → 滚了，按实测结算；
                //   分不开 → 信命令（绝不因"没滚"假设撤回整批，那是一键大跳的来源）。
                var small = Math.Abs(expN0) <= 3;
                var seqAt = _seq;
                // 小批量直接"量"：每格像素随缩放/文档类型变（PDF 300% = 60px，docx 200% ≈ 75px），
                // 只比"0 vs 预期"会把"滚了但每格不是 60"判成"没滚/分不开"（docx 实测踩到，墨迹越按越短）。
                // 量不量只看**重叠够不够**（预期位移不超过小半屏），不按格数卡：
                // 288dpi 下 3 格就有 456 物理px，按格数卡会把最有用的大样本挡在外面。
                Capture.Shift2D meas = null;
                var canMeasure = Math.Abs(dyMove) <= 0.4 * img.H;
                if (canMeasure)
                {
                    var maxShift = (int)Math.Min(1600, Math.Max(260, Math.Abs(dyMove) + step));
                    if (Capture.MatchTwoBands(_prev, img, maxShift, out var mr)) meas = mr;
                    // docx(OpusApp) 的两条固定带经常落在页边空白上 → 两带分数只有 0.86（"分不开"），
                    // 而全帧 2D 带匹配能稳稳量出（实测 1 格 = 84 物理px，0.971 对次优 0.916）。
                    // 所以两带失败时再全帧搜一次，只认"够好且明显甩开次优"的结果。
                    if (meas == null)
                    {
                        var mb = Capture.MatchBand(_prev, img, maxShift);
                        if (mb.Score >= 0.95 && (mb.Score - mb.SecondScore) >= 0.03) meas = mb;
                    }
                }
                var measOk = meas != null;
                // 大批量（>2 格）却量出 0px，是空白页边/页间留白最典型的假零：一整批墨迹会被全额退回。
                // 这种证据不够硬，宁可这轮"信命令"等下一轮，也不撤回整批（用户希沃 540px 回滚就是这么来的）。
                if (measOk && Math.Abs(expPx0) > 2 * StepOf(Ruler.Shot) && Math.Abs(meas.Dy) <= 8 && diff < 8)
                {
                    _log($"#{seqAt} 大批量实测 0px 但账本 {expPx0:0}px → 疑似空白带假零，不撤回");
                    meas = null;
                    measOk = false;
                }
                if (measOk && Math.Abs(meas.Dy) <= 8 && diff < 8)
                {
                    // 量出来就是原地不动（到顶/到底）
                    m = new Capture.Shift2D { Dy = 0, Dx = 0, Score = meas.Score, SecondScore = meas.SecondScore };
                    _cNoMove++; _log($"#{seqAt} 实测没动(到边) {meas.Score:0.000}/{meas.SecondScore:0.000} → 撤回 {dyMove}px exp={expPx0:0} diff={diff:0.0}");
                }
                else if (measOk)
                {
                    m = meas;
                    _measuredSample = true;
                    _cConfirmed++;
                    _log($"#{seqAt} 实测滚动 {meas.Dy}px（{meas.Score:0.000}/{meas.SecondScore:0.000}）命令 {dyMove}px 每格={StepDip:0.0}DIP diff={diff:0.0}");
                }
                else if (small && diff >= 5 && Capture.ScorePair(_prev, img, dyMove, out var sMoveS, out var sStayS))
                {
                    // "没滚"必须证据够硬才敢撤回：光靠领先 0.02 不够——两个假设都只有 0.84 时说明
                    // 根本没能对上任何一条带（页面还在动/翻页缝隙/半透明遮罩），此时领先只是噪声。
                    // 实测踩到：静止 0.840 vs 移动 0.819 被判"没滚"，把一次真实滚动撤了。
                    // docx 又踩到一次更狠的：静止 0.989 vs 移动 0.737（docx 页间留白让"静止"天然高分），
                    // 撤回 180px，页面其实滚了——用户看到的就是"整段跳回错位置"。
                    // 所以这条和上面那道硬闸同一口径：静止必须近乎完美(0.995)且移动假设明显不行(≤0.95)。
                    // 软证据一律不撤回，交给静止分支(diff≈0)和轮末对账去判。
                    if ((sStayS - sMoveS) >= 0.02 && sStayS >= 0.995 && sMoveS <= 0.95)
                    {
                        m = new Capture.Shift2D { Dy = 0, Dx = 0, Score = sStayS, SecondScore = sMoveS };
                        _cNoMove++; _log($"#{seqAt} 复核判定没滚(到边) 静止 {sStayS:0.000} vs 移动 {sMoveS:0.000} → 撤回 {dyMove}px exp={expPx0:0} 每格={StepDip:0.0}DIP diff={diff:0.0}");
                    }
                    else if (sMoveS >= 0.93 && (sMoveS - sStayS) >= 0.02)
                    {
                        m = new Capture.Shift2D { Dy = dyMove, Dx = 0, Score = sMoveS, SecondScore = sStayS };
                        _cConfirmed++; _log($"#{seqAt} 复核确认滚动 {dyMove}px（{sMoveS:0.000}/{sStayS:0.000}）每格={StepDip:0.0}DIP diff={diff:0.0}");
                    }
                    else
                    {
                        // 分不开时**先别结算**：单格只有两种真相，而"按下之前那一帧"还留着当基线，
                        // 多试两拍通常就分得开了（动画走完/重绘完成）。
                        //   页面真滚了 → 下一拍测得出（账本还在，正好照实结算）；
                        //   页面其实没动（WPS 吞了这次滚轮）→ 画面静止，走静止分支把账撤回。
                        // 之前这里立即"信命令"，于是最后一次按键被吞掉时就永久留下一个错位
                        // （实测 12+12 往返，墨迹停在 +60，文档已回到原点）。
                        _misses++;
                        if (_misses < 5)
                        {
                            Status = $"分不开，保持基线重试{_misses} ({sMoveS:0.000}/{sStayS:0.000})";
                            return;
                        }
                        m = new Capture.Shift2D { Dy = dyMove, Dx = 0, Score = sMoveS, SecondScore = sStayS };
                        _cUnclear++; _log($"#{seqAt} 复核分不开（{sMoveS:0.000}/{sStayS:0.000}）→ 信命令 {dyMove}px 每格={StepDip:0.0}DIP diff={diff:0.0} 重试{_misses}");
                    }
                }
                else
                {
                    // 大批量（>3 拍）：先拿"双带"独立量整批位移——账本大、两帧重叠少，量不出再说；
                    // 这一步很值：docx 连按 8 下时 ScorePair 全线对不上（页间留白），只能"信命令"，
                    // 而命令按 60/格 推的，实际每格不是 60 就整段错位。
                    var bigMax = (int)Math.Min(2.0 * img.H, Math.Max(320, Math.Abs(dyMove) + 2 * step));
                    var overlapOk = Math.Abs(dyMove) <= 0.45 * img.H;
                    if (overlapOk && Capture.MatchTwoBands(_prev, img, bigMax, out var mBig))
                    {
                        m = mBig;
                        _measuredSample = true;
                        _cConfirmed++;
                        _log($"#{_seq} 大批量实测 {mBig.Dy}px（{mBig.Score:0.000}/{mBig.SecondScore:0.000}）命令 {dyMove}px 每格={StepDip:0.0}DIP diff={diff:0.0}");
                    }
                    else if (overlapOk)
                    {
                        // 两带在 docx 上常落在页边空白：退到全帧 2D 匹配（实测 504px 也能 0.97）。
                        var mb = Capture.MatchBand(_prev, img, bigMax);
                        if (mb.Score >= 0.95 && (mb.Score - mb.SecondScore) >= 0.02 && Math.Abs(mb.Dy) >= 20)
                        {
                            m = mb;
                            _measuredSample = true;
                            _cConfirmed++;
                            _log($"#{_seq} 大批量 2D 实测 {mb.Dy}px（{mb.Score:0.000}/{mb.SecondScore:0.000}）命令 {dyMove}px 每格={StepDip:0.0}DIP diff={diff:0.0}");
                        }
                    }
                    if (m == null)
                    {
                        // 退一步：在预期附近扫几格取最像的那个（WPS 可能少吃/多吃一格）。
                        var maxDev = Math.Max(2 * step, Math.Abs(dyMove) / 4);
                        foreach (var k in new[] { 0, 1, -1, 2, -2 })
                        {
                            var cand = dyMove + k * step;
                            if (cand == 0 || Math.Abs(cand - dyMove) > maxDev) continue;
                            if (!Capture.ScorePair(_prev, img, cand, out var sMove, out var sStay)) continue;
                            if (sMove - sStay >= 0.03 && sMove >= 0.95)
                            {
                                m = new Capture.Shift2D { Dy = cand, Dx = 0, Score = sMove, SecondScore = sStay };
                                if (cand != dyMove) _log($"#{_seq} 复核修正: 命令 {dyMove}px → 实测 {cand}px ({sMove:0.000}/{sStay:0.000})");
                                break;
                            }
                        }
                        if (m == null)
                        {
                            m = new Capture.Shift2D { Dy = dyMove, Dx = 0, Score = 0.9, SecondScore = 0.9 };
                            _cTrustBig++; _log($"#{_seq} 大批量复核没匹配上 → 信命令 {dyMove}px 每格={StepDip:0.0}DIP diff={diff:0.0}");
                        }
                    }
                }
                confident = true;
            }
            else
            {
                _log($"页面没动(到顶/到底) 内容带 {m.Score:0.000} vs 移动假设 {m.SecondScore:0.000}");
            }
        }

        if (!confident)
        {
            var sincePress = (DateTime.UtcNow - _lastPressAt).TotalMilliseconds;
            if (expPx0 != 0)
            {
                // 有账本但这一拍没测出来（WPS 的滚动动画常比沉降期长）：先保留基线重试两拍，
                // 仍测不出就按命令结算，让正常的结算路径把账本消费掉。
                // 绝不能用"动画残余 → 只换基线"草草了事：账本留着不清，下一拍画面静止时
                // 静止判据会拿这笔账去"撤回"，可页面其实已经滚过了 ——
                // 那正是用户报的"往下按几下，笔迹整段跳回错位置"。
                _misses++;
                if (_misses < 3 && sincePress < 1600)
                {
                    Status = $"复核没测出，重试{_misses} diff={diff:0.0}";
                    return;
                }
                m = new Capture.Shift2D { Dy = (int)Math.Round(expPx0), Dx = 0, Score = 0.9, SecondScore = 0.9 };
                _cTrustBig++;
                _log($"#{_seq} 复核没测出(diff={diff:0.0} 重试{_misses}) → 信命令 {expPx0:0}px，按命令结算");
                confident = true;
            }
            else if (sincePress < 1200)
            {
                // 刚按过键、画面还在动，但没有账本可结：多半是上一批的动画残余，只换基线不动墨迹。
                Swap();
                Status = "动画残余，仅更新基线";
                return;
            }
            else
            {
                // 没有账本 = 不是我们下发的翻页（鼠标滚轮 / 手型拖动 / 拖滚动条 都走这条）。
                // 以前这里用自由搜索去追页面，量不准就会把墨迹推到完全错位的位置；
                // 现在的口径是：**只有我们自己的翻页命令才带墨迹**，其它滚动一律不动笔迹。
                Swap();
                Status = "外部滚动，不跟";
                return;
            }
        }
        sw.Stop();
        Measured++;

        // 多格批次的"修正"不可信：连按时 WPS 的滚动动画常常还没走完，匹配读到的是中间帧
        // （实测 5 拍命令 300px 被改成 240px，那 60px 就永久留在墨迹上，成了用户说的"往下按几下就跑偏"）。
        // 多格只认"与命令相符"的结果；要改每个格的像素，靠单格样本自学习就够了。
        if (expN0 != 0 && Math.Abs(expN0) > 1 && Math.Abs(m.Dy - Math.Round(expPx0)) > 8)
        {
            // 例外：测量近乎满分且明显甩开次优时，现实说了算——WPS 在 docx 上会**吞格**
            // （6 格连发只走 3~4 格，实测 252px vs 命令 360px，相似度 1.000/0.887）。
            // 这时候"按命令"就是把 108px 的错位钉死，正是用户报的 docx 累积错位。
            var strong = _measuredSample && m.Score >= 0.95 && (m.Score - m.SecondScore) >= 0.10;
            if (strong)
            {
                _log($"#{_seq} 多格实测 {m.Dy}px ≠ 命令 {expPx0:0}px（{m.Score:0.000}/{m.SecondScore:0.000}）→ 强匹配，按实测");
            }
            else
            {
                _log($"#{_seq} 多格实测 {m.Dy}px ≠ 命令 {expPx0:0}px（{m.Score:0.000}/{m.SecondScore:0.000}）→ 不改，按命令");
                m = new Capture.Shift2D { Dy = (int)Math.Round(expPx0), Dx = 0, Score = m.Score, SecondScore = m.SecondScore };
            }
        }

        if (!confident)
        {
            // 认不出来先别换基线：下一帧继续拿"滚动前那一帧"当参照再试，免得这段位移永久丢失。
            // 连丢 6 次（≈0.5s 都对不上）才是真的翻页/换文档，那时才重置基线。
            _misses++;
            if (_misses >= 6)
            {
                _misses = 0;
                TakePending(out _, out _);   // 复核不了就把账本丢掉，别让它越积越大
                Swap();
                Status = $"不确信{_misses}→重置基线 {m} ({sw.ElapsedMilliseconds}ms)";
            }
            else
            {
                Status = $"不确信，重试{_misses} {m} ({sw.ElapsedMilliseconds}ms)";
            }
            return;
        }
        _misses = 0;

        LastDyPx = m.Dy;
        LastDxPx = m.Dx;

        // 账本结算：墨迹总共该平移 -m.Dy，按按钮时已经乐观平移了 -expected，这里只补差额。
        // 到顶/到底时页面不动（m.Dy=0）而 expected≠0，补差会把墨迹拉回去——正好自动纠偏。
        // **只消费这一拍抓帧之前的那部分账本（快照）**：抓帧之后又按下的那几拍，
        // 画面里根本还没体现，硬算进来就等于把它们刚做的乐观平移当成"多走的"撤销掉。
        // 这就是"往下连按几下，墨迹越按越落后"的真凶（实测 10 下：文档走 599px，墨迹只走 359px）。
        double expected; int expNotch;
        lock (_gate)
        {
            expected = expPx0; expNotch = expN0;
            _pendingPx -= expPx0;
            _pendingNotch -= expN0;
            if (Math.Abs(_pendingPx) < 0.5) _pendingPx = 0;
        }

        // 标定 / 自学习"每格物理像素"：这是**唯一**能让墨迹和页面产生固定比例误差的地方
        // （每格 72 而不是 60，按十下就偏 120px），所以入口必须极窄：
        // ①多格批次只在"还没标定"时用来标定（除以格数），标定之后只收单格样本
        //    （多格会把 WPS 吃格/多滚混进来）；②必须是强匹配；③与当前值差 ≤8% 就当没变；
        // ④真要改（用户改了缩放）必须连续两次给出同一个新值；
        // ⑤**首次标定必须来自独立测量**（双带 MatchTwoBands），不接受"命令假设"当标定值。
        var sampleOk = _measuredSample ? (m.Score >= 0.95 && (m.Score - m.SecondScore) >= 0.03)
                                      : (m.Score >= 0.985 && (m.Score - m.SecondScore) >= 0.05);
        var notchAbs = Math.Abs(expNotch);
        // 这段的样本来自"截图比对"，只写截图那把尺子的槽：它的粗测量不该覆盖 COM/UIA 的精确标定值。
        var R = Ruler.Shot;
        var perNotchOk = notchAbs == 1 || (_measuredSample && !CalOf(R));
        if (expNotch != 0 && m.Dy != 0 && Math.Sign(m.Dy) == Math.Sign(expNotch)
            && perNotchOk && notchAbs > 0 && sampleOk && (CalOf(R) || _measuredSample))
        {
            var cand = (double)Math.Abs(m.Dy) / notchAbs;   // 每格＝总位移/格数（物理像素）
            var candMax = 4.0 * img.H;                   // 不设死上限：4K/深缩放下真实值可以很大
            if (cand >= 20 && cand <= candMax)
            {
                // 首次标定：一条独立测量就够（前面 sampleOk 已经卡了相似度与领先量）。
                // 之前要求"两次一致"，结果 docx 永远停在错误的 60 上（真值 84），
                // 每按一下差 24px，十下就是一整行——这才是 docx 累积错位的主因。
                if (!CalOf(R) && _measuredSample) { LearnStep(cand, "首次标定", R); _candBy[(int)R] = 0; }
                else if (Math.Abs(cand - StepOf(R)) / StepOf(R) <= 0.08) { _candBy[(int)R] = 0; if (!CalOf(R)) LearnStep(cand, "首次标定", R); }
                else if (CandOf(R) > 0 && Math.Abs(cand - CandOf(R)) <= 2)
                {
                    LearnStep(cand, CalOf(R) ? "连续两次一致" : "首次标定(两次一致)", R);
                    _candBy[(int)R] = 0;
                }
                else if (m.Score >= 0.99 && (m.Score - m.SecondScore) >= 0.03
                         && (!CalOf(R) || Math.Abs(cand - StepOf(R)) <= 0.5 * StepOf(R)))
                {
                    // 双带一致 + 近满分：这种样本不该再等第二次。换文档/改缩放后第一下就要把
                    // 每格像素改对，否则后面每一拍都按老每格走，误差会累积成一整行
                    // （实测 docx：老每格 60、真值 86，down 12 拍就差了 312px）。
                    // 但**倍数级跳变**（>1.5 倍或 <0.67 倍）必须先等第二次一致：那多半不是"每格变了"，
                    // 而是一帧里攒了多拍（帧间隔 60~200ms vs 按键间隔 150~300ms），
                    // 直接采纳就把每格写成 3 倍（实测 `每格 60px → 180px（强匹配立即学）`，十拍甩出 900px）。
                    LearnStep(cand, CalOf(R) ? "强匹配立即学" : "首次标定(强匹配)", R);
                    _candBy[(int)R] = 0;
                }
                else _candBy[(int)R] = cand;
            }
        }

        var needDy = Units.CorrectionDip(expected, m.Dy, _dpiScale);   // expected/m.Dy 都是物理像素
        // 横向先不跟：WPS 这版没有能触发横向滚动的操作，实测搜出来的 dx 全是噪声（曾出现 -204px 瞎跳）
        const double needDx = 0;
        // 合法的补差最多是"把这次乐观平移全撤回"，再大就是坏匹配。用账本本身当上限，不用魔法数。
        if (Math.Abs(needDy) > Math.Abs(expected) / _dpiScale + 100)
        {
            Swap();
            _log($"补差越界({needDy:0}px) → 丢弃，重置基线 实测={m.Dy}px 预期={expected:0}px {m.Score:0.000}/{m.SecondScore:0.000}");
            Status = $"补差越界 → 跳过({needDy:0}px)";
            return;
        }
        if (Math.Abs(needDy) > 700)
        {
            Swap();
            _log($"补差过大({needDy:0}px) → 丢弃，重置基线 实测={m.Dy}px 预期={expected:0}px {m.Score:0.000}");
            Status = $"补差过大 → 跳过({needDy:0}px)";
            return;
        }
        if (needDy == 0 && needDx == 0)
        {
            Swap();
            Status = $"无位移 ({sw.ElapsedMilliseconds}ms)";
            return;
        }

        var ok = _shift.ApplyAsync(needDx, needDy).GetAwaiter().GetResult();
        if (ok) Applied++;
        // 到边撤回（EDGE）是正常行为，别算进 JUMP 告警
        var tag = m.Dy == 0 && expected != 0 ? "EDGE " : (Math.Abs(needDy) >= 300 ? "JUMP " : "");
        _log($"{tag}apply dy={m.Dy}px dx={m.Dx}px 预期={expected:0}px 补差=({needDx:0},{needDy:0}) 相似度={m.Score:0.000}/{m.SecondScore:0.000} {Units.Describe(StepOf(Ruler.Shot), _dpiScale)} 账本={expNotch}拍 diff={diff:0.0} {sw.ElapsedMilliseconds}ms {_shift.Status}");
        Swap();
        Status = $"实测 dy={m.Dy}px 补差({needDx:0},{needDy:0}) 每格{StepDip:0}DIP {sw.ElapsedMilliseconds}ms";
    }

    /// <summary>取一次 WPS 对象模型刻度尺；发送前调用，保证锚点在这拍页面还没动时。</summary>
    void EnsureDocPos()
    {
        if (_docPos != null) return;
        var doc = WpsScroll.FindDocument(out _, out var x, out var y, out _);
        if (doc == IntPtr.Zero) return;
        var pos = WpsDocPos.TryCreate(doc, x, y, out var why);
        if (pos != null)
        {
            _docPos = pos;
            _log($"COM 标尺: 正文Top={pos.Top}px（{_hwnd.ToInt64():X} → 0x{doc.ToInt64():X}）");
        }
        else if (why.Length != 0) _log($"COM 标尺不可用: {why}");
    }

    /// <summary>PDF UIA 刻度尺：找不到就回退截图。</summary>
    void EnsurePdfPos()
    {
        if (_pdfPos != null) return;
        var top = WpsScroll.Find(out _);
        if (top == IntPtr.Zero) return;
        var pp = WpsPdfPos.TryCreate(top, out var why);
        if (pp != null)
        {
            _pdfPos = pp;
            pp.TryRead(out var v0);
            _log($"PDF UIA 标尺: 滚动值={v0}px（0x{top.ToInt64():X}）");
        }
        else if (why.Length != 0) _log($"PDF UIA 标尺不可用: {why}");
    }

    /// <summary>
    /// PDF 逐拍结算：UIA 滚动条的 RangeValue 直接就是物理像素位置。
    /// 发完等它变化并稳定，读净位移，只补这一拍；到边读到不变就把乐观墨迹撤回。
    /// </summary>
    void SettlePdfPress(double expected, int expNotch, int anchor, bool anchorOk)
    {
        var pos = _pdfPos;
        if (pos == null) return;
        if (!anchorOk)
        {
            var back0 = Units.InkDipFor(expected, _dpiScale);
            if (back0 != 0) _shift.ApplyAsync(0, -back0).GetAwaiter().GetResult();
            lock (_gate)
            {
                _pendingPx -= expected; _pendingNotch -= expNotch;
                if (Math.Abs(_pendingPx) < 0.5) _pendingPx = 0;
            }
            _log("PDF UIA 读数失败 → 撤回这一拍");
            DisposePdfPos();
            _anchor = null; _anchorTries = 0; _roundNotch = 0; _zeroAt = DateTime.MinValue; _prev = null;
            return;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(CalOf(Ruler.Pdf) ? 700 : 1200);
        var moved = 0;
        var seen = false;
        var stable = 0;
        var last = anchor;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            if (!pos.TryRead(out var v)) break;
            moved = v - anchor;
            if (Math.Abs(moved) >= 2) seen = true;
            if (seen && v == last) stable++; else stable = 0;
            last = v;
            if (seen && stable >= 2) break;
        }

        double leftPx; int leftN;
        lock (_gate)
        {
            _pendingPx -= expected;
            _pendingNotch -= expNotch;
            if (Math.Abs(_pendingPx) < 0.5) _pendingPx = 0;
            leftPx = _pendingPx; leftN = _pendingNotch;
        }

        var need = Units.CorrectionDip(expected, moved, _dpiScale);
        var limit = Math.Abs(expected) / _dpiScale + 100;
        if (Math.Abs(need) > limit)
        {
            _log($"PDF UIA 补差越界({need:0}px > {limit:0}) → 跳过 实测={moved}px 预期={expected:0}px");
            Status = $"PDF UIA 补差越界({need:0}px)";
        }
        else if (Math.Abs(need) >= 0.5)
        {
            _shift.ApplyAsync(0, need).GetAwaiter().GetResult();
            Applied++;
            _log($"PDF UIA 逐拍 实测 {moved}px，预期 {expected:0}px → 补差({need:0}DIP) {Units.Describe(StepOf(Ruler.Pdf), _dpiScale)} {_shift.Status}");
            Status = $"PDF UIA dy={moved}px 补差({need:0}) 每格{StepDip:0}DIP";
        }
        else
        {
            _log($"PDF UIA 逐拍 实测 {moved}px，与预期 {expected:0}px 一致");
            Status = $"PDF UIA 一致 dy={moved}px";
        }

        // 到边时页面只走了一部分，不能拿它当“每格步长”。
        if (Math.Abs(leftPx) < 0.5 && expNotch != 0 && Math.Abs(moved) >= 20
            && Math.Sign(moved) == Math.Sign(expNotch)
            && Math.Abs(expected) > 0.5 && Math.Abs(moved) >= 0.8 * Math.Abs(expected))
        {
            var cand = Math.Abs((double)moved) / Math.Abs(expNotch);
            if (cand >= 20 && cand <= 4000 && Math.Abs(cand - StepOf(Ruler.Pdf)) / StepOf(Ruler.Pdf) > 0.02)
                CalibrateStep(cand, $"PDF UIA逐拍({Math.Abs(expNotch)}格)", Ruler.Pdf);
        }

        _anchor = null; _anchorTries = 0; _roundNotch = 0; _zeroAt = DateTime.MinValue;
        _prev = null;
    }

    void DisposePdfPos()
    {
        var p = _pdfPos;
        _pdfPos = null;
        try { p?.Dispose(); } catch { }
    }

    /// <summary>
    /// 单拍结算：等这一拍页面动完（到边就等满窗口），读 COM Top 的净位移，只补这一拍的差。
    /// 调用时持有 _sendGate，所以不会和下一拍的发送/测量交错；下一拍的墨迹已经先推出去了。
    /// </summary>
    /// <summary>
    /// PDF(Qt) 没有 COM 标尺时的逐拍兜底：发完看画面是否真的变了。
    /// 变了就信命令（能匹配上时再用匹配结果补差）；没变就是到边，当场撤回这一拍的乐观平移。
    /// </summary>
    void SettleShotPress(Capture.Img before, double expected, int expNotch)
    {
        if (before == null || before.Bgra == null) return;
        var deadline = DateTime.UtcNow.AddMilliseconds(CalOf(Ruler.Shot) ? 900 : 1400);
        var prev = before;
        Capture.Img last = null;
        var moved = false;
        var movedAt = DateTime.MinValue;
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(90);
            var cur = Capture.Grab(_hwnd, false, null);
            if (cur == null || cur.Bgra == null) break;
            last = cur;
            if (!moved)
            {
                // PDF 单格 180px 在一整页白纸上可能只改变不到 1% 的像素；原来用 0.01 会把
                // “真的滚了”误判成“到边”，然后撤乐观墨迹，页面却已经走了——用户看到的就是回弹。
                if (Capture.DiffRatio(before, cur) >= 0.002)
                {
                    moved = true;
                    movedAt = DateTime.UtcNow;
                }
            }
            else
            {
                if (Capture.DiffRatio(prev, cur) < 0.002) stable++;
                else stable = 0;
                // 看到变化后再等 180ms，且连续两帧稳定，才算这一拍的动画走完。
                if ((DateTime.UtcNow - movedAt).TotalMilliseconds >= 180 && stable >= 2) break;
            }
            prev = cur;
        }

        if (!moved)
        {
            var back = Units.InkDipFor(expected, _dpiScale);
            if (back != 0) _shift.ApplyAsync(0, -back).GetAwaiter().GetResult();
            lock (_gate)
            {
                _pendingPx -= expected;
                _pendingNotch -= expNotch;
                _roundNotch -= expNotch;
                if (Math.Abs(_pendingPx) < 0.5) _pendingPx = 0;
            }
            _log($"截图逐拍 实测 0px，到边 → 撤回 {back:0}DIP {_shift.Status}");
            Status = "截图逐拍 到边，已撤回";
            _anchor = null; _anchorTries = 0; _roundNotch = 0; _zeroAt = DateTime.MinValue;
            _prev = null;
            return;
        }

        // 画面变了：默认信命令；如果双带匹配置信度高，再用它补差（PDF 文字通常够用）。
        var actual = expected;
        if (last != null)
        {
            var maxShift = (int)Math.Min(2400, Math.Abs(expected) + 240);
            if (Capture.MatchTwoBands(before, last, maxShift, out var m) && m.Score >= 0.95)
                actual = m.Dy;
            else
            {
                var m2 = Capture.MatchBand(before, last, maxShift);
                if (m2.Score >= 0.95) actual = m2.Dy;
            }
        }
        // 已经看到画面变了，却匹配出 0px：这是 PDF 空白/重复版式上的假零。
        // 此时绝不能按 0 补差，否则会把刚推出去的墨迹原路拉回，正是“向下闪一下又回去”。
        if (Math.Abs(actual) < 20) actual = expected;

        var need = Units.CorrectionDip(expected, actual, _dpiScale);
        if (Math.Abs(need) >= 1)
        {
            _shift.ApplyAsync(0, need).GetAwaiter().GetResult();
            Applied++;
            _log($"截图逐拍 实测 {actual:0}px，预期 {expected:0}px → 补差({need:0}DIP) {_shift.Status}");
            Status = $"截图逐拍 dy={actual:0}px 补差({need:0})";
        }
        else
        {
            _log($"截图逐拍 实测 {actual:0}px，与预期 {expected:0}px 一致");
            Status = $"截图逐拍 dy={actual:0}px";
        }
        double leftPx;
        lock (_gate)
        {
            _pendingPx -= expected;
            _pendingNotch -= expNotch;
            _roundNotch -= expNotch;
            if (Math.Abs(_pendingPx) < 0.5) _pendingPx = 0;
            leftPx = _pendingPx;
        }
        // 只在这一拍是“干净样本”时标定：没有排队的下一拍、真的动了、方向一致。
        // 必须放在扣账本之后：之前放在扣账本之前，_pendingPx 永远等于 expected，
        // 条件永远不成立，PDF 的每格值永远停在 152/60，下一拍继续按错的预期推。
        if (Math.Abs(leftPx) < 0.5 && expNotch != 0 && Math.Abs(actual) >= 20
            && Math.Sign(actual) == Math.Sign(expNotch))
        {
            var cand = Math.Abs(actual) / Math.Abs(expNotch);
            if (cand >= 20 && cand <= 4000 && Math.Abs(cand - StepOf(Ruler.Shot)) / StepOf(Ruler.Shot) > 0.02)
                CalibrateStep(cand, $"截图逐拍({Math.Abs(expNotch)}格)", Ruler.Shot);
        }
        _anchor = null; _anchorTries = 0; _roundNotch = 0; _zeroAt = DateTime.MinValue;
        _prev = null;
    }

    void SettleComPress(double expected, int expNotch)
    {
        var pos = _docPos;
        if (pos == null) return;
        var anchor = pos.Top;
        var deadline = DateTime.UtcNow.AddMilliseconds(CalOf(Ruler.Com) ? 700 : 1400);
        var moved = 0;
        var seen = false;
        var stable = 0;
        var lastTop = anchor;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(15);
            if (!pos.TryReadTop(out var top)) break;
            moved = anchor - top;
            if (Math.Abs(moved) >= 2) seen = true;
            if (seen && top == lastTop) stable++; else stable = 0;
            lastTop = top;
            if (seen && stable >= 2) break;   // 连续两次读数不变 = 这一拍的动画走完了
        }

        double leftPx; int leftN;
        lock (_gate)
        {
            _pendingPx -= expected;
            _pendingNotch -= expNotch;
            if (Math.Abs(_pendingPx) < 0.5) _pendingPx = 0;
            leftPx = _pendingPx; leftN = _pendingNotch;
        }

        var need = Units.CorrectionDip(expected, moved, _dpiScale);
        var limit = Math.Abs(expected) / _dpiScale + 100;
        if (Math.Abs(need) > limit)
        {
            _log($"COM 逐拍补差越界({need:0}px > {limit:0}) → 跳过 实测={moved}px 预期={expected:0}px");
            Status = $"COM 补差越界({need:0}px)";
        }
        else if (Math.Abs(need) >= 0.5)
        {
            _shift.ApplyAsync(0, need).GetAwaiter().GetResult();
            Applied++;
            _log($"COM 逐拍 实测 {moved}px，预期 {expected:0}px → 补差({need:0}DIP) {Units.Describe(StepOf(Ruler.Com), _dpiScale)} {_shift.Status}");
            Status = $"COM 逐拍 dy={moved}px 补差({need:0}) 每格{StepDip:0}DIP";
        }
        else
        {
            _log($"COM 逐拍 实测 {moved}px，与预期 {expected:0}px 一致（差 {need:0}DIP）");
            Status = $"COM 一致 dy={moved}px";
        }

        // 只在这一拍是“干净样本”时标定：没有排队的下一拍、真的动了、方向一致。
        // 到边时页面只走了一部分（例如 6 格只落 4 格），不能拿它当“每格步长”，必须跳过。
        if (Math.Abs(leftPx) < 0.5 && expNotch != 0 && Math.Abs(moved) >= 20
            && Math.Sign(moved) == Math.Sign(expNotch)
            && Math.Abs(expected) > 0.5 && Math.Abs(moved) >= 0.8 * Math.Abs(expected))
        {
            var cand = Math.Abs((double)moved) / Math.Abs(expNotch);
            if (cand >= 20 && cand <= 4000 && Math.Abs(cand - StepOf(Ruler.Com)) / StepOf(Ruler.Com) > 0.02)
                CalibrateStep(cand, $"COM逐拍({Math.Abs(expNotch)}格)", Ruler.Com);
        }

        DisposeDocPos();
        _anchor = null; _anchorTries = 0; _roundNotch = 0; _zeroAt = DateTime.MinValue;
        _batchHits = 0; _batchPresses = 0;
        _prev = null;
    }

    void DisposeDocPos()
    {
        var p = _docPos;
        _docPos = null;
        try { p?.Dispose(); } catch { }
    }

    /// <summary>改"每格物理像素"，并把还没被复核的那几拍按新每格重算（否则误差会累积成一整行）。</summary>
    void LearnStep(double cand, string why, Ruler r = Ruler.Shot)
    {
        var old = StepOf(r);
        if (Math.Abs(cand - old) < 1 && CalOf(r)) return;
        _stepBy[(int)r] = cand;
        _calBy[(int)r] = true;
        int pendNotch;
        lock (_gate) pendNotch = _pendingNotch;
        var deltaPhys = pendNotch * (cand - old);            // 物理像素
        if (Math.Abs(deltaPhys) >= 2)
        {
            lock (_gate) _pendingPx += deltaPhys;
            var dip = Units.InkDipFor(deltaPhys, _dpiScale);
            _shift.ApplyAsync(0, dip).GetAwaiter().GetResult();
            _log($"每格 {Units.Describe(old, _dpiScale)} → {Units.Describe(cand, _dpiScale)}（{why}）：剩余 {pendNotch} 拍重算，墨迹补 {dip:0.0}DIP");
            return;
        }
        _log($"每格 {Units.Describe(old, _dpiScale)} → {Units.Describe(cand, _dpiScale)}（{why}）");
    }

    /// <summary>把刚抓到的帧变成新基线，旧基线缓冲留作下次抓帧复用。</summary>
    void Swap() { var t = _prev; _prev = _cur; _cur = t; }

    /// <summary>
    /// 一轮按键结束后的对账：拿"按键开始前的锚点帧"与现在这帧比总位移，
    /// 再跟这一轮墨迹实际走过的量对齐，差多少补多少。
    /// 存在的理由：多格批次连按时 WPS 动画没走完，匹配只能读到中间帧，
    /// 于是"测不准就按命令结算"，WPS 若吞掉一拍就永久差一格（实测 86px）。
    /// 静止时一次比对能把整轮误差归零。
    /// </summary>
    bool Reconcile(Capture.Img img)
    {
        var a = _anchor;
        var ink = _shift.Y - _anchorInkY;      // 这一轮墨迹走了多少（负=向上）

        // 黑帧/空帧不能当“页面没动”的证据：WPS 重绘或 PrintWindow 抽风时两帧会一样黑，
        // DiffRatio≈0 会把一整轮真实滚动判成到边、把墨迹全额退回（用户踩到的 540px 回滚）。
        if (a == null || a.Bgra == null || a.Blank || a.Mean < 40
            || img == null || img.Bgra == null || img.Blank || img.Mean < 40)
        {
            _log($"对账跳过: 帧无效(锚点 mean={a?.Mean:0} blank={a?.Blank} 当前 mean={img?.Mean:0} blank={img?.Blank})");
            return false;
        }

        // 账本为 0 = 这轮还没有"待核对的位移"。什么都不做，尤其不要消耗三次重试预算：
        // 以前空闲的每一拍都在跑匹配、三次失败就把锚点丢了，等用户真按键时已经没锚点可用
        // （实测 docx 连按时每轮都在"锚点: 静置后重建 → 对账测不出 → 放弃锚点"里打转）。
        if (Math.Abs(ink) * _dpiScale < Math.Max(20.0, StepOf(Ruler.Shot) * 0.5))
            return true;

        // 一轮滚了超过 6 个窗口高，两帧之间已经没有共同内容可比，别浪费一秒去测。
        // 阈值按**窗口高**算（不是写死 900 DIP）：288dpi 下 900 DIP = 2700 物理px，写死会把"早就没法比"的
        // 情形当成还能测，白等三次；96dpi 上又太早放弃。
        if (Math.Abs(ink) * _dpiScale > 6.0 * a.H)
        {
            // 账本大得离谱，正常匹配量不出来。但如果锚点帧和现在**一模一样**，那就是"页面一动不动"
            // （文档到顶/到底了还在空按），这笔账必须全额退回：保留墨迹等于把笔迹永久甩在错位上
            // （实测在页首连按 20 次 6 格，墨迹被甩到 +6780px，页面一动没动）。
            if (Capture.DiffRatio(a, img) < 0.002)
            {
                _shift.ApplyAsync(0, -ink).GetAwaiter().GetResult();
                _log($"对账(超大账本): 墨迹走了 {ink:0}px，锚点帧未变(diff≈0) → 修正 {-ink:0}px {_shift.Status}");
                Status = $"对账修正超大账本({-ink:0}px)";
            }
            else
            {
                _log($"对账: 账本 {ink:0}px 超过窗口高 6 倍且画面已变 → 放弃锚点，保留墨迹");
            }
            _anchor = null; _anchorTries = 0; _zeroAt = DateTime.MinValue; _roundNotch = 0;
            return true;   // 账本可以结掉
        }

        // 闸门一：距最后一次按键不足 900ms 不下结论。
        // 连按时的"停手"往往发生在 WPS 动画起步之前 —— 那时画面是真静止（锚点帧与当前帧逐像素相同），
        // 于是"整轮位移 0px"算得又对又没用：页面不是没动，是还没开始动。等它动起来再说。
        var now = DateTime.UtcNow;
        var sincePress = (now - _lastPressAt).TotalMilliseconds;
        if (sincePress < StillGateMs)
        {
            Status = $"对账等待动画({sincePress:0}ms)";
            return false;   // 锚点和账本都留着，下一拍再试
        }

        double corr;
        string note;
        var decided = false;
        Capture.Shift2D rr = null;

        // 双带一致最可信；退一步只认"单带但位移非零"——单带读出 0 的情况太危险：
        // 200% 缩放下页间留白本身就是静止的，单带在空白上必然给出 0，会把真实滚动误判成
        // "页面没动"而把墨迹撤回去（实测出现过 上带(0) 的假零）。
        // 搜索范围必须跟着账本走：一轮 6 格 × 84px × 3 次 = 1512px，早就超过写死的 1600 上限，
        // 于是"大批量"永远测不出（这是 docx 连按后对账全程失灵的直接原因）。
        var recMax = (int)Math.Min(3.0 * a.H, Math.Max(600.0, Math.Abs(ink) * _dpiScale + 4 * StepOf(Ruler.Shot)));
        if (Capture.MatchTwoBandsLoose(a, img, recMax, out var rr2)) { rr = rr2; decided = true; }
        else if (Capture.MatchAnyBandLoose(a, img, recMax, out var rr1) && rr1.Dy != 0) { rr = rr1; decided = true; }
        else
        {
            // 一条带都不可靠时（docx 大量页边留白、单轮超过半屏），再上全帧 2D 匹配：
            // 实测 1920x1032 上 6 格=504px 也能给出 1.000（次优 0.903），比"放弃锚点"强得多。
            var fb = Capture.MatchBand(a, img, recMax);
            if (fb.Score >= 0.95 && (fb.Score - fb.SecondScore) >= 0.012 && Math.Abs(fb.Dy) >= 20) { rr = fb; decided = true; }
        }

        // 一格 = 多少：阈值分两套口径 —— rr.Dy 是物理像素，ink/corr 是 DIP。
        var stepMinPhys = Math.Max(20.0, StepOf(Ruler.Shot) * 0.6);
        var stepMin = Units.PhysToDip(stepMinPhys, _dpiScale);
        var zeroVerdict = false;
        if (decided)
        {
            corr = Units.ReconcileDip(rr.Dy, ink, _dpiScale);   // rr.Dy 物理 − ink DIP，必须换算
            note = $"实测页面 {rr.Dy}px（{rr.Note}）";
            zeroVerdict = Math.Abs(rr.Dy) < stepMinPhys;
        }
        // 这里原本还有一条 `ScorePair(a, img, -ink)` 的"自证"分支：拿墨迹自己当页面测量，
        // 只要墨迹和"以墨迹为假设的图"对得上就判"与墨迹一致"。单位混用那段时间它永远报一致
        // （错的墨迹量出的页面正好自洽），希沃日志整轮 `差 0px` 就是这么来的。已删除：
        // 对账只接受**独立测量**（双带 / 全帧匹配），或"锚点帧根本没变"（diff≈0）这种像素级证据。
        else if (Math.Abs(ink) >= stepMin && Capture.DiffRatio(a, img) < 0.002)
        {
            // 兜底判据：锚点帧和当前帧几乎逐像素相同 → 这一轮页面确实一下都没动（大多是到顶/到底），
            // 墨迹全额退回锚点位置。不靠带匹配，所以"页面全是空白/整页一张图"测不出来时也有效。
            // 不给这条，页尾连按时三次"测不出"就放弃锚点、把墨迹留在错位处（实测踩到）。
            corr = -ink;
            note = "锚点帧未变(diff≈0)";
            decided = true;
            zeroVerdict = true;
        }
        else { corr = 0; note = "测不出"; }

        // ①独立的双带测量才是"页面真的走了多少"的唯一证据（ScorePair 那条是拿墨迹自证，
        //    3 倍误差下数值还恰好相等，2026-10-18 希沃日志就是这么一路"对账一致"过去的）。
        var independent = rr != null;

        if (!decided)
        {
            if (++_anchorTries >= 3)
            {
                _anchor = null; _anchorTries = 0; _zeroAt = DateTime.MinValue; _roundNotch = 0;
                _log($"对账: {note}，三次都测不出 → 放弃锚点，保留墨迹");
                return true;
            }
            return false;
        }

        // 标定：这一轮一共下了 _roundNotch 格、页面实测走了 rr.Dy 物理px → 每格 = 总位移/格数。
        // 这是"未标定"状态下最干净的一次独立测量（比单拍照实，因为整轮位移远大于噪声）。
        // 必须在下面结账之前做：CalibrateStep 只改每格值，不动墨迹（此时墨迹即将按 corr 对齐）。
        if (independent && !CalOf(Ruler.Shot) && _roundNotch != 0 && Math.Abs(rr.Dy) >= 20
            && Math.Sign(rr.Dy) == Math.Sign(_roundNotch))
        {
            var cand = Math.Abs((double)rr.Dy) / Math.Abs(_roundNotch);
            if (cand >= 20 && cand <= 4.0 * a.H) CalibrateStep(cand, $"对账标定({Math.Abs(_roundNotch)} 格)", Ruler.Shot);
        }

        // 闸门二："页面 0px"这种结论要 300ms 后再确认一次。
        // 它一旦判错就要把已经推好的墨迹退回去，代价是一整格永久错位；而正确的情形（真到边）
        // 再等 300ms 也还是 0，多等一下没有成本。
        if (zeroVerdict && Math.Abs(ink) >= stepMin)
        {
            if (_zeroAt == DateTime.MinValue) _zeroAt = now;
            if ((now - _zeroAt).TotalMilliseconds < 300)
            {
                _log($"对账待确认: {note} ink={ink:0}px → 300ms 后再判");
                Status = $"对账待确认({note})";
                return false;
            }
            _log($"对账确认: {note} 两次都是 0px，承认页面没动");
        }
        _zeroAt = DateTime.MinValue;

        _anchor = null; _anchorTries = 0; _roundNotch = 0;
        var lim = Math.Max(Units.PhysToDip(StepOf(Ruler.Shot) * 3, _dpiScale), Math.Abs(ink) + Units.PhysToDip(StepOf(Ruler.Shot), _dpiScale));
        if (Math.Abs(corr) < stepMin)
        {
            _log($"对账: 墨迹走了 {ink:0}px，{note} → 差 {corr:0}px 不足一格，不动 {_shift.Status}");
            Status = $"对账一致 {note}";
            return true;
        }
        // 夹逼是为了防"一次错误判定把十几格一起推翻"，但"锚点帧根本没变(diff≈0)"是**物理证据**：
        // 页面确实一动没动，该退多少就退多少。夹到 3 格会让 6 格的空按只退回一半，留下永久错位。
        if (!zeroVerdict) corr = Math.Clamp(corr, -lim, lim);
        _shift.ApplyAsync(0, corr).GetAwaiter().GetResult();
        _log($"对账: 墨迹走了 {ink:0}px，{note} → 修正 {corr:0}px {_shift.Status}");
        Status = $"对账修正({corr:0}px) {note}";
        return true;
    }

    /// <summary>
    /// 纯标定：只改"每格物理像素"，不动墨迹、不碰账本。
    /// 给"对账时整轮独立测量"用 —— 那一刻墨迹马上就要按对账结果对齐，再顺手推它一下会把账算乱。
    /// </summary>
    void CalibrateStep(double cand, string why, Ruler? ruler = null)
    {
        // 只改这个样本所属的那把尺子：docx 的 COM 标定值和 PDF 的 UIA 标定值
        // 是两份独立的物理事实，互相覆盖会让切文档之后的第一拍按错步长推。
        var r = ruler ?? RulerNow;
        var old = StepOf(r);
        if (Math.Abs(cand - old) < 0.01 && CalOf(r)) return;
        _stepBy[(int)r] = cand;
        _calBy[(int)r] = true;
        _candBy[(int)r] = 0;
        _log($"每格{SlotName(r)} {Units.Describe(old, _dpiScale)} → {Units.Describe(cand, _dpiScale)}（{why}）");
    }

    static string SlotName(Ruler r) => r switch { Ruler.Com => "(docx)", Ruler.Pdf => "(PDF)", _ => "(截图)" };
    /// <summary>取走并清零"已乐观平移但还没被实测确认"的账本。</summary>
    void TakePending(out double px, out int notch)
    {
        lock (_gate) { px = _pendingPx; notch = _pendingNotch; _pendingPx = 0; _pendingNotch = 0; }
    }

    static bool Confident(Capture.Shift2D m) =>
        m.Score >= 0.85 && ((m.Score - m.SecondScore) >= 0.02 || m.Score >= 0.93);

    void Reset()
    {
        DisposeDocPos();
        DisposePdfPos();
        _prev = null; _cur = null; _still = 0; _anchor = null; _anchorTries = 0; _zeroAt = DateTime.MinValue; _roundNotch = 0;
        lock (_gate) { _pendingPx = 0; _pendingNotch = 0; _batchHits = 0; _batchPresses = 0; }
    }

    /// <summary>廉价预筛：抽样比较两帧，避免静止时也跑完整匹配。</summary>
    static double Sample(Capture.Img a, Capture.Img b)
    {
        if (a.Bgra == null || b.Bgra == null || a.Bgra.Length != b.Bgra.Length) return 255;
        const int n = 4000;
        var stride = Math.Max(4, a.Bgra.Length / n / 4 * 4);
        double sum = 0; var cnt = 0;
        for (var i = 0; i + 3 < a.Bgra.Length; i += stride)
        {
            // 只比亮度，够用
            var la = 0.114 * a.Bgra[i] + 0.587 * a.Bgra[i + 1] + 0.299 * a.Bgra[i + 2];
            var lb = 0.114 * b.Bgra[i] + 0.587 * b.Bgra[i + 1] + 0.299 * b.Bgra[i + 2];
            sum += Math.Abs(la - lb); cnt++;
        }
        return cnt > 0 ? sum / cnt : 255;
    }

    // ---------- 窗口 ----------

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
}
