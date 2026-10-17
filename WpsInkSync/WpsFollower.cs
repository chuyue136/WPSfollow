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
    const int SettleMs = 300;       // 翻页动画的沉降时间，这期间不抓帧
    const double PrefilterThreshold = 1.5;

    readonly InkShift _shift;
    readonly Func<bool> _enabled;
    readonly Func<bool> _mouseMode;
    readonly Action<string> _log;

    Thread _thread;
    volatile bool _stop;

    IntPtr _hwnd;
    Capture.Img _prev, _cur;        // 双缓冲：轮流抓帧，互不覆盖，也不产生垃圾
    double _dpiScale = 1.0;
    string _lastDiag = "";
    DateTime _burstUntil = DateTime.MinValue;
    WheelTrigger _wheelHook;
    int _misses;                    // 连续认不出画面的次数

    // 控制模式账本：按下按钮时先乐观平移墨迹（零延迟），测量到实际位移后再补差。
    // _pendingPx = 已乐观平移的"内容位移"（内容上移为正），_pendingNotch = 对应的格数（带方向）。
    readonly object _gate = new();
    double _pendingPx;
    int _pendingNotch;
    int _gen;                       // 每按一次按钮自增，用来作废"按下之前"抓到的那一帧
    int _still;                     // 连续静止帧数
    int _seq;                       // 按键序号：日志里 press #N 与它的复核结论 #N 对得上
    int _pendingSeq;                // 账本最新一笔的序号
    bool _measuredSample;           // 本次结论来自'直接测量'（不是命令假设），自学习门槛可以放宽
    DateTime _lastPressAt = DateTime.MinValue;   // 最近一次按键时间（用来区分"动画残余"和"外部滚动"）
    double _stepPx = 60;            // 每格滚轮的内容位移（实测 300% 缩放 = 60px），按需自学习
    double _stepCand;               // 学习候选：大跳变要连续两次一致才采纳
    DateTime _settleUntil = DateTime.MinValue;   // 翻页动画期间不抓帧（中间帧会把基线带脏）
    // 对账锚点：一轮连续按键开始前的基线帧。一轮结束时（画面静止且静置够久）拿它跟当前帧比
    // 总位移，再跟墨迹这一轮实际走的量对账，差多少补多少。多格批次"测不准只能信命令"时
    // 攒下的误差（实测 10 拍被 WPS 吞一拍 = 一整格 86px）全靠这一步收回来。
    Capture.Img _anchor;
    double _anchorInkY;
    int _anchorTries;
    DateTime _zeroAt = DateTime.MinValue;   // 上一次"页面疑似 0px"的时刻（要 300ms 后复核一次才准撤墨迹）

    public long Ticks, Measured, Applied;
    public double LastDyPx, LastDxPx;
    long _cConfirmed, _cNoMove, _cUnclear, _cTrustBig;   // 心跳里的判断计数，用来看"哪种判断在主导"
    DateTime _lastBeat = DateTime.UtcNow;
    public double StepPx => _stepPx;

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
        _prev = null;
    }

    /// <summary>
    /// 控制模式：我们命令 WPS 滚 <paramref name="notches"/> 格（dir=+1 内容上移，-1 下移），
    /// 墨迹立刻按"已知格数 × 每格像素"跟着走，不用等测量。实际位移由 Tick 复核后补差。
    /// </summary>
    public async Task<bool> PressAsync(int notches, int dir)
    {
        if (!_enabled()) { Status = "跟随已关闭，忽略"; return false; }
        var px = _stepPx * notches * dir;
        // 账本累加：每一拍的乐观平移都必须有人核对——之前"只记最近一次"会丢掉前几拍的账，
        // 到顶连按两下就有一拍永远回不来。"攒超过两拍就丢弃"同样会丢账（连按五下只撤回一拍）。
        // 现在纯累加：撤回只发生在"连续两帧完全一致 + 静置 ≥900ms"时，那时页面确实没动，全额撤回才是对的。
        lock (_gate)
        {
            _pendingPx += px;
            _pendingNotch += notches * dir;
            _seq++;
            _pendingSeq = _seq;
        }
        var seq = _pendingSeq;
        _gen++;                                                              // 让"按下前抓到的帧"作废

        // 建对账锚点：一轮连续按键只在第一拍克隆基线帧（8MB 一次，之后整轮复用）。
        // 只能用"已经静止两拍"的帧当锚点：按键间隔 500ms 时 _prev 可能还是动画中间帧，
        // 拿它当锚点，一轮结束后怎么都对不上（实测 0.384/0.755 全错）。
        // 必须建在乐观平移**之前**：_anchorInkY 要记录这一拍还没推墨迹时的位置，
        // 否则 ink 少算一拍，对账每轮都多补一格（实测每轮 +86px 的净漂移就是这个）。
        // 也必须在下面那批时间戳**之前**：那里会把 _still 清零，而建锚点要读它。
        if (_anchor == null && _prev != null && _still >= 2)
        {
            _anchor = new Capture.Img
            {
                W = _prev.W, H = _prev.H, How = _prev.How, Mean = _prev.Mean,
                Bgra = (byte[])_prev.Bgra.Clone()
            };
            _anchorInkY = _shift.Y;
            _anchorTries = 0;
            _zeroAt = DateTime.MinValue;
            _log($"锚点: 建在 step={_stepPx:0} 墨迹Y={_shift.Y:0} mean={_prev.Mean:0}");
        }

        // 时间戳必须早于下面那次阻塞下发。WpsScroll.Send 是 SendMessageTimeout（实测 130~300ms），
        // 早前把 _lastPressAt/_settleUntil 写在它后面，于是发消息那段时间里跟随线程还"以为很久没按键"：
        // 它拿上一轮的对账锚点下结论、把刚推出去的墨迹按一格退回去，而 WPS 恰好在这之后才开始滚 →
        // 墨迹永久少一格。日志实证：6 次"对账修正"全部落在 `翻页动画中` 之前 63~70ms
        // （2026-10-17 17:49:32 / 17:49:59 / 17:50:02 / 17:50:17 / 17:50:18 / 17:50:22）。
        var stamp = DateTime.UtcNow;
        _lastPressAt = stamp;
        _still = 0;
        var settleEarly = Math.Min(600.0, SettleMs + 40.0 * Math.Max(1, Math.Abs(notches)));
        var untilEarly = stamp.AddMilliseconds(settleEarly);
        if (untilEarly > _settleUntil) _settleUntil = untilEarly;
        _burstUntil = stamp.AddMilliseconds(BurstHoldMs);

        if (px != 0) await _shift.ApplyAsync(0, -px).ConfigureAwait(false);   // 内容上移 = 墨迹上移

        if (!WpsScroll.Send(notches, dir, out var diag))
        {
            // 发不出去不等于 WPS 没动：SendMessageTimeout 超时（WPS 忙着重绘）时消息可能已经送达并被处理。
            // 之前这里无条件把墨迹搬回去，实测 docx 连按 12+12 之后墨迹多出 466px —— 就是几次超时被
            // "回滚"了而页面其实滚了。现在保留乐观平移，让画面自己去证伪：真没滚的话
            // 静止判据会把账撤回（那是它存在的意义）。
            _log($"press #{seq} {dir * notches} 格下发超时/失败: {diag} → 保留乐观平移等复核 {_shift.Status}");
            Status = "下发超时，等复核: " + diag;
            _burstUntil = DateTime.UtcNow.AddMilliseconds(BurstHoldMs);
            _lastPressAt = DateTime.UtcNow;
            _still = 0;
            return false;
        }

        _burstUntil = DateTime.UtcNow.AddMilliseconds(BurstHoldMs);   // 让复核尽快跑
        // 动画沉降期按格数放宽，但要封顶：用户连按时不能让"待复核账本"无限攒着，
        // 否则一次错误判定就把十几格一起推翻（墨迹与页面差出一大段）。
        int notchesTotal;
        lock (_gate) notchesTotal = Math.Abs(_pendingNotch);
        var settle = Math.Min(600.0, SettleMs + 40.0 * notchesTotal);
        var until = DateTime.UtcNow.AddMilliseconds(settle);
        if (until > _settleUntil) _settleUntil = until;
        _lastPressAt = DateTime.UtcNow;
        _still = 0;
        _log($"press #{seq} {dir * notches} 格 墨迹({-px:0}) step={_stepPx:0} 账本={notchesTotal * _stepPx:0}px {diag} {_shift.Status}");
        Status = $"命令 {dir * notches} 格 → 墨迹({-px:0}px) 每格{_stepPx:0}px ({( _mouseMode() ? "鼠标" : "画笔")}模式)";
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
                _log($"心跳 step={_stepPx:0} 墨迹{_shift.Status} 判断:确认{_cConfirmed}/没滚{_cNoMove}/分不开{_cUnclear}/大批量信命令{_cTrustBig} tick={Ticks}");
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
                _dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
                Status = $"已锁定 WPS 窗口 0x{_hwnd.ToInt64():X} dpi={_dpiScale * 96:0}";
                _log($"锁定窗口 0x{_hwnd.ToInt64():X} {diag} dpi={_dpiScale * 96:0}");
            }
        }

        var gen = _gen;
        var img = Capture.Grab(_hwnd, false, _cur);
        _cur = img;
        if (img.Bgra == null || img.Blank) { Reset(); Status = "抓帧失败"; return; }
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
            // 画面没动，但我们刚按过按钮 → 可能到顶/到底了，页面没滚，把乐观平移撤回。
            // 必须连续两帧都静止才认：按下按钮后 WPS 要过一会儿才开滚动，只凭一帧会把正常滚动误判成"到边"。
            _still++;
            Swap();
            // "静止"不等于"到边"：按下后 WPS 可能还没开始动画。要连续两帧静止、沉降期已过、
            // 且距按键 ≥900ms 才敢认定页面真的没动（否则会把正常滚动误判成到边，把墨迹拉回去）。
            if (_still < 2 || DateTime.UtcNow < _settleUntil ||
                (DateTime.UtcNow - _lastPressAt).TotalMilliseconds < 900)
            {
                Status = $"静止 diff={diff:0.000} mean={img.Mean:0.0}";
                return;
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
            var seqAt = _pendingSeq;
            TakePending(out exp, out expN);
            if (exp != 0)
            {
                var back = exp / _dpiScale;
                _shift.ApplyAsync(0, back).GetAwaiter().GetResult();
                _log($"#{seqAt} 页面没动(到边?) → 撤回墨迹(0,{back:0}) exp={exp:0}px {_shift.Status}");
            }
            Status = exp != 0 ? $"页面没动 → 撤回({exp / _dpiScale:0}px)" : $"静止 diff={diff:0.000} mean={img.Mean:0.0}";
            return;
        }
        _still = 0;
        _burstUntil = DateTime.UtcNow.AddMilliseconds(BurstHoldMs);

        var sw = Stopwatch.StartNew();
        _measuredSample = false;
        double expPx0; int expN0;
        lock (_gate) { expPx0 = _pendingPx; expN0 = _pendingNotch; }

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
            var step = Math.Max(20, (int)Math.Round(_stepPx));

            // 先判"页面根本没动"（到顶/到底）。这个判据必须极硬，否则把正常滚动误判成没动
            // 就等于把一整批乐观平移撤回，墨迹与页面直接差出一大段（用户踩到的"像素不同步"）。
            // 三道闸：①最后一帧必须在按键 900ms 之后（动画刚开始时画面几乎没变，最容易骗过判据）；
            //        ②预筛差异必须很小（画面几乎没变，允许光标闪烁那种程度）；
            //        ③"停在原地"必须近乎完美且甩开"按预期移动"一大截。
            var quiet = (DateTime.UtcNow - _lastPressAt).TotalMilliseconds >= 900;
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
                var seqAt = _pendingSeq;
                // 小批量直接"量"：每格像素随缩放/文档类型变（PDF 300% = 60px，docx 200% ≈ 75px），
                // 只比"0 vs 预期"会把"滚了但每格不是 60"判成"没滚/分不开"（docx 实测踩到，墨迹越按越短）。
                Capture.Shift2D meas = null;
                if (small && Capture.MatchTwoBands(_prev, img, 260, out var mr)) meas = mr;
                var measOk = meas != null;
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
                    _log($"#{seqAt} 实测滚动 {meas.Dy}px（{meas.Score:0.000}/{meas.SecondScore:0.000}）命令 {dyMove}px step={_stepPx:0} diff={diff:0.0}");
                }
                else if (small && diff >= 5 && Capture.ScorePair(_prev, img, dyMove, out var sMoveS, out var sStayS))
                {
                    // "没滚"必须证据够硬才敢撤回：光靠领先 0.02 不够——两个假设都只有 0.84 时说明
                    // 根本没能对上任何一条带（页面还在动/翻页缝隙/半透明遮罩），此时领先只是噪声。
                    // 实测踩到：静止 0.840 vs 移动 0.819 被判"没滚"，把一次真实滚动撤了。
                    if ((sStayS - sMoveS) >= 0.02 && sStayS >= 0.95)
                    {
                        m = new Capture.Shift2D { Dy = 0, Dx = 0, Score = sStayS, SecondScore = sMoveS };
                        _cNoMove++; _log($"#{seqAt} 复核判定没滚(到边) 静止 {sStayS:0.000} vs 移动 {sMoveS:0.000} → 撤回 {dyMove}px exp={expPx0:0} step={_stepPx:0} diff={diff:0.0}");
                    }
                    else if (sMoveS >= 0.93 && (sMoveS - sStayS) >= 0.02)
                    {
                        m = new Capture.Shift2D { Dy = dyMove, Dx = 0, Score = sMoveS, SecondScore = sStayS };
                        _cConfirmed++; _log($"#{seqAt} 复核确认滚动 {dyMove}px（{sMoveS:0.000}/{sStayS:0.000}）step={_stepPx:0} diff={diff:0.0}");
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
                        _cUnclear++; _log($"#{seqAt} 复核分不开（{sMoveS:0.000}/{sStayS:0.000}）→ 信命令 {dyMove}px step={_stepPx:0} diff={diff:0.0} 重试{_misses}");
                    }
                }
                else
                {
                    // 大批量（>3 拍）：在预期附近扫几格取最像的那个（WPS 可能少吃/多吃一格）。
                    var maxDev = Math.Max(2 * step, Math.Abs(dyMove) / 4);
                    foreach (var k in new[] { 0, 1, -1, 2, -2 })
                    {
                        var cand = dyMove + k * step;
                        if (cand == 0 || Math.Abs(cand - dyMove) > maxDev) continue;
                        if (!Capture.ScorePair(_prev, img, cand, out var sMove, out var sStay)) continue;
                        if (sMove - sStay >= 0.03 && sMove >= 0.95)
                        {
                            m = new Capture.Shift2D { Dy = cand, Dx = 0, Score = sMove, SecondScore = sStay };
                            if (cand != dyMove) _log($"#{seqAt} 复核修正: 命令 {dyMove}px → 实测 {cand}px ({sMove:0.000}/{sStay:0.000})");
                            break;
                        }
                    }
                    if (m == null)
                    {
                        m = new Capture.Shift2D { Dy = dyMove, Dx = 0, Score = 0.9, SecondScore = 0.9 };
                        _cTrustBig++; _log($"#{seqAt} 大批量复核没匹配上 → 信命令 {dyMove}px step={_stepPx:0} diff={diff:0.0}");
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
                _log($"#{_pendingSeq} 复核没测出(diff={diff:0.0} 重试{_misses}) → 信命令 {expPx0:0}px，按命令结算");
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
            _log($"#{_pendingSeq} 多格实测 {m.Dy}px ≠ 命令 {expPx0:0}px（{m.Score:0.000}/{m.SecondScore:0.000}）→ 不改，按命令");
            m = new Capture.Shift2D { Dy = (int)Math.Round(expPx0), Dx = 0, Score = m.Score, SecondScore = m.SecondScore };
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

        // 自学习"每格像素"：这是**唯一**能让墨迹和页面产生固定比例误差的地方（每格 72 而不是 60，
        // 按十下就偏 120px），所以入口必须极窄：
        // ①只收单格样本（多格会把 WPS 吃格/多滚混进来）；②必须是强匹配；③与当前值差 ≤8% 就当没变；
        // ④真要改（用户改了缩放）必须连续两次给出同一个新值。
        var sampleOk = _measuredSample ? (m.Score >= 0.95 && (m.Score - m.SecondScore) >= 0.03)
                                      : (m.Score >= 0.985 && (m.Score - m.SecondScore) >= 0.05);
        if (expNotch != 0 && m.Dy != 0 && Math.Sign(m.Dy) == Math.Sign(expNotch)
            && Math.Abs(expNotch) == 1 && sampleOk)
        {
            var cand = (double)Math.Abs(m.Dy);
            if (cand >= 30 && cand <= 200)
            {
                if (Math.Abs(cand - _stepPx) / _stepPx <= 0.08) _stepCand = 0;
                else if (_stepCand > 0 && Math.Abs(cand - _stepCand) <= 2)
                {
                    LearnStep(cand, "连续两次一致");
                    _stepCand = 0;
                }
                else if (m.Score >= 0.99 && (m.Score - m.SecondScore) >= 0.03)
                {
                    // 双带一致 + 近满分：这种样本不该再等第二次。换文档/改缩放后第一下就要把
                    // 每格像素改对，否则后面每一拍都按老每格走，误差会累积成一整行
                    // （实测 docx：老每格 60、真值 86，down 12 拍就差了 312px）。
                    LearnStep(cand, "强匹配立即学");
                    _stepCand = 0;
                }
                else _stepCand = cand;
            }
        }

        var needDy = (expected - m.Dy) / _dpiScale;
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
        _log($"{tag}apply dy={m.Dy}px dx={m.Dx}px 预期={expected:0}px 补差=({needDx:0},{needDy:0}) 相似度={m.Score:0.000}/{m.SecondScore:0.000} 每格={_stepPx:0}px 账本={expNotch}拍 diff={diff:0.0} {sw.ElapsedMilliseconds}ms {_shift.Status}");
        Swap();
        Status = $"实测 dy={m.Dy}px 补差({needDx:0},{needDy:0}) 每格{_stepPx:0}px {sw.ElapsedMilliseconds}ms";
    }

    /// <summary>改"每格像素"，并把还没被复核的那几拍按新每格重算（否则误差会累积成一整行）。</summary>
    void LearnStep(double cand, string why)
    {
        var old = _stepPx;
        if (Math.Abs(cand - old) < 1) return;
        _stepPx = cand;
        int pendNotch;
        lock (_gate) pendNotch = _pendingNotch;
        var delta = pendNotch * (_stepPx - old);
        if (Math.Abs(delta) >= 2)
        {
            lock (_gate) _pendingPx += delta;
            _shift.ApplyAsync(0, -delta).GetAwaiter().GetResult();
            _log($"每格 {old:0}px → {cand:0}px（{why}）：剩余 {pendNotch} 拍重算，墨迹补 {-delta:0}px");
            return;
        }
        _log($"每格 {old:0}px → {cand:0}px（{why}）");
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
        if (Math.Abs(ink) > 900)
        {
            // 一轮滚了快一千像素，两帧之间已经没有共同内容可比，别浪费一秒去测。
            _anchor = null; _anchorTries = 0; _zeroAt = DateTime.MinValue;
            return true;   // 结论=放弃锚点，账本可以结掉
        }

        // 闸门一：距最后一次按键不足 900ms 不下结论。
        // 连按时的"停手"往往发生在 WPS 动画起步之前 —— 那时画面是真静止（锚点帧与当前帧逐像素相同），
        // 于是"整轮位移 0px"算得又对又没用：页面不是没动，是还没开始动。等它动起来再说。
        var now = DateTime.UtcNow;
        var sincePress = (now - _lastPressAt).TotalMilliseconds;
        if (sincePress < 900)
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
        if (Capture.MatchTwoBandsLoose(a, img, 1600, out var rr2)) { rr = rr2; decided = true; }
        else if (Capture.MatchAnyBandLoose(a, img, 1600, out var rr1) && rr1.Dy != 0) { rr = rr1; decided = true; }

        var stepMin = Math.Max(20.0, _stepPx * 0.6);
        var zeroVerdict = false;
        if (decided)
        {
            corr = -rr.Dy - ink;
            note = $"实测页面 {rr.Dy}px（{rr.Note}）";
            zeroVerdict = Math.Abs(rr.Dy) < stepMin;
        }
        else if (Capture.ScorePair(a, img, (int)Math.Round(-ink), out var sMove, out var sStay)
                 && (Math.Max(sMove, sStay) >= 0.95 && Math.Abs(sMove - sStay) >= 0.05))
        {
            corr = sStay > sMove ? -ink : 0;   // 页面没动 → 墨迹退回锚点；否则视为与墨迹一致
            note = (sStay > sMove ? "页面没动" : "与墨迹一致") + $"({sMove:0.000}/{sStay:0.000})";
            decided = true;
            zeroVerdict = sStay > sMove;
        }
        else { corr = 0; note = "测不出"; }

        if (!decided)
        {
            if (++_anchorTries >= 3)
            {
                _anchor = null; _anchorTries = 0; _zeroAt = DateTime.MinValue;
                _log($"对账: {note}，三次都测不出 → 放弃锚点，保留墨迹");
                return true;
            }
            return false;
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

        _anchor = null; _anchorTries = 0;
        var lim = _stepPx * 3;
        if (Math.Abs(corr) < stepMin)
        {
            _log($"对账: 墨迹走了 {ink:0}px，{note} → 差 {corr:0}px 不足一格，不动 {_shift.Status}");
            Status = $"对账一致 {note}";
            return true;
        }
        corr = Math.Clamp(corr, -lim, lim);
        _shift.ApplyAsync(0, corr).GetAwaiter().GetResult();
        _log($"对账: 墨迹走了 {ink:0}px，{note} → 修正 {corr:0}px {_shift.Status}");
        Status = $"对账修正({corr:0}px) {note}";
        return true;
    }

    /// <summary>取走并清零"已乐观平移但还没被实测确认"的账本。</summary>
    void TakePending(out double px, out int notch)
    {
        lock (_gate) { px = _pendingPx; notch = _pendingNotch; _pendingPx = 0; _pendingNotch = 0; }
    }

    static bool Confident(Capture.Shift2D m) =>
        m.Score >= 0.85 && ((m.Score - m.SecondScore) >= 0.02 || m.Score >= 0.93);

    void Reset()
    {
        _prev = null; _cur = null; _still = 0; _anchor = null; _anchorTries = 0; _zeroAt = DateTime.MinValue;
        lock (_gate) { _pendingPx = 0; _pendingNotch = 0; }
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
