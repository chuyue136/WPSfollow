using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScrollProbe;

namespace WpsInkSync;

/// <summary>
/// WPS 窗口定位 + 滚轮注入。
/// 控制模式的核心：翻页由我们下发命令，所以滚动量不需要测量——只知道"发了几格"就够了。
/// 目标窗口必须发给文档子窗口（Qt 把文档区做成子窗口，发给顶层没反应，已实测）。
/// </summary>
static class WpsScroll
{
    const int WM_MOUSEWHEEL = 0x020A;
    const int WheelDelta = 120;      // 一格 = 120；负值 = 向前滚 = 内容上移
    // 不再固定等 260ms：每发一格，等页面真的动一下（最多 180ms）就发下一格。
    // WPS 能吃多快就吃多快，慢机器/首滚也不会因为间隔太短而吞格。
    const int NotchPollMs = 25;      // 抓帧轮询间隔
    const int NotchMaxWaitMs = 280;  // 后续格：等这一格动画走完的上限
    const int NotchFirstWaitMs = 420; // 首格：WPS 刚打开/刚从后台回来时启动更慢，多给一点
    const int NotchFallbackMs = 100; // 抓帧不可用（黑帧）时的固定间隔
    const int SendBudgetMs = 700;    // 一批最多花这么久等画面；到顶/到底时不能一格一格干等
    // docx 快速道（0.2.2）：以前每格都要等动画完全停下（≥80ms）再睡 80ms，6 格固定 0.83s。
    // 现在只等"读数变了"就发下一格，被吐掉的格当场补发，发完再按总位移核对一次。
    const int ComNotchGapMs = 15;    // 这一格读数变化后再喘一口，避免下一格被并进同一段动画
    const int ComNotchWaitMs = 60;   // 每格最多等这么久读数变化，超时就算被吞、当场补发
    const int ComResendMax = 2;      // 一格里最多补发几次

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public static uint Dpi(IntPtr h) => GetDpiForWindow(h);

    /// <summary>挑面积最大的、可见的 wps/wpspdf/wpp/et 主窗口（PDF 是 Qt5QWindowIcon，.docx 是 OpusApp）。</summary>
    public static IntPtr Find(out string diag)
    {
        var best = IntPtr.Zero;
        long bestArea = 0;
        var seen = 0; var cls = "";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wps", "wpspdf", "wpp", "et" };
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0) return true;
            string proc;
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; }
            catch { return true; }
            if (!names.Contains(proc)) return true;

            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, 256);
            var c = sb.ToString();
            // 同一个 WPS 里 PDF 走 Qt 外壳（Qt5QWindowIcon），.doc(x) 走 Word 兼容外壳（OpusApp）。
            // 只认 Qt5 会让 .docx 直接"没找到窗口"（用户实测踩到）。
            if (!c.StartsWith("Qt5", StringComparison.Ordinal) && c != "OpusApp") return true;

            GetWindowRect(h, out var r);
            var area = (long)(r.R - r.L) * (r.B - r.T);
            var title = new StringBuilder(512);
            GetWindowTextW(h, title, 512);
            if (title.Length == 0) return true;
            seen++;
            cls = $"{proc}/{c} '{title}' {r.R - r.L}x{r.B - r.T}";
            if (area <= bestArea) return true;
            bestArea = area; best = h;
            return true;
        }, IntPtr.Zero);
        found = best;
        diag = seen == 0 ? "没有 wps/wpspdf 的 Qt5/OpusApp 可见窗口" : $"候选 {seen} 个，选中 {cls}";
        return found;
    }

    /// <summary>找当前文档子窗口和窗口中心点（物理屏幕坐标）。给 COM 测量和滚轮下发共用。</summary>
    public static IntPtr FindDocument(out IntPtr top, out int cx, out int cy, out string diag)
    {
        cx = cy = 0;
        var h = Find(out diag);
        top = h;
        if (h == IntPtr.Zero) return IntPtr.Zero;
        GetWindowRect(h, out var r);
        cx = (r.L + r.R) / 2;
        cy = (r.T + r.B) / 2;
        var target = ChildAtPoint(h, cx, cy);
        return target == IntPtr.Zero ? h : target;
    }

    /// <summary>往 WPS 发 <paramref name="notches"/> 格滚轮。<paramref name="dir"/> = +1 内容上移，-1 内容下移。</summary>
    public static bool Send(int notches, int dir, out string diag) => Send(notches, dir, null, null, out diag, out _);

    public static bool Send(int notches, int dir, WpsDocPos pos, out string diag) => Send(notches, dir, pos, null, out diag, out _);

    public static bool Send(int notches, int dir, WpsDocPos pos, out string diag, out int hits) => Send(notches, dir, pos, null, out diag, out hits);

    public static bool Send(int notches, int dir, WpsPdfPos pdf, out string diag, out int hits) => Send(notches, dir, null, pdf, out diag, out hits);

    public static bool Send(int notches, int dir, WpsDocPos pos, WpsPdfPos pdf, out string diag, out int hits)
    {
        hits = 0;
        if (notches <= 0 || dir == 0) { diag = "参数无效"; return false; }
        var target = FindDocument(out var h, out var cx, out var cy, out diag);
        if (target == IntPtr.Zero) return false;
        // 刻度尺可能是在上一个标签/文档上取的：窗口一变就不能再用，宁可回退截图。
        if (pos != null && pos.Hwnd != target) pos = null;
        // PDF 的 UIA 滚动条挂在顶层 Qt 窗口下，不是文档子窗口。
        if (pdf != null && pdf.Hwnd != h) pdf = null;
        var pdfVal = 0;
        var pdfOk = pdf != null && pdf.TryRead(out pdfVal);

        var one = -WheelDelta * dir;
        var wp = (IntPtr)(one << 16);
        var lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
        // 关键：Ctrl 被按住时 WPS 把滚轮当"缩放"（Ctrl+滚轮），我们的热键本身就是 Ctrl+Alt+方向键。
        // WPS(Qt) 处理滚轮时看的是当时的修饰键状态，所以不仅要"等到松开"，还要"松开后静默一小段"：
        // 连按热键时下一轮的 Ctrl 会跟我们的 SendMessage 撞在一起 → 偶发缩放。
        if (!WaitModifiersQuiet(120, 900))
        {
            diag = "修饰键未松开，未发送";
            return false;
        }
        // 一格一条消息，别把 |delta| 乘上去：WPS 的 docx(OpusApp) **不认 delta 大小**，
        // 一条消息只滚一格（实测 1 格=84px、6 格合发也是 84px），而 PDF(Qt) 认。
        // 乘着发就会在 docx 上"按 6 下页面只走 1 格"→ 墨迹冲出去 6 倍，正是用户报的整段错位。
        // 拆开发 + 自适应间隔：每格等页面真的动一下再发下一格。
        // 关键：基准帧必须在 SendMessage **之前**抓。WPS 的 docx 是瞬移不是动画，
        // 发完再抓两张会得到两张一样的"已经跳完"的图，永远等不到变化。
        var comTop = 0;
        var comOk = pos != null && pos.TryReadTop(out comTop);
        // docx 走快速道：只等读数变化 + 被吞的格当场补发 + 发完核对总量（见 SendComFast）。
        if (comOk) return SendComFast(target, pos, notches, cx, cy, wp, lp, out diag, out hits);
        Capture.Img a = null, b = null;
        var canWatch = false;
        if (!comOk && !pdfOk)
        {
            a = Capture.Grab(h, false, null);
            b = Capture.Grab(h, false, null);
            canWatch = a != null && a.Bgra != null && !a.Blank && b != null && b.Bgra != null;
        }
        var sw = Stopwatch.StartNew();
        var waits = 0;
        var noMove = 0;
        for (var i = 0; i < notches; i++)
        {
            if (pdfOk) pdf.TryRead(out pdfVal);       // 这一格发出前的位置
            if (comOk) pos.TryReadTop(out comTop);   // 记下这一格发出前的位置
            SendMessageTimeoutW(target, WM_MOUSEWHEEL, wp, lp, 2, 1500, out _);
            if (i + 1 < notches)
            {
                if (pdfOk)
                {
                    var waitPdf = i == 0 ? NotchFirstWaitMs : (noMove >= 2 ? 40 : NotchMaxWaitMs);
                    if (WaitPdfMoved(pdf, waitPdf, ref pdfVal))
                    {
                        waits++; noMove = 0;
                        Thread.Sleep(40);
                    }
                    else
                    {
                        noMove++;
                        Thread.Sleep(20);
                        // 第一格就确认“页面没动”（读数正常且值没变）→ 多半已到边，
                        // 剩下 5 条消息不用再发，免得在页首/页尾白等 900ms。
                        if (i == 0 && pdf.TryRead(out var chk) && chk == pdfVal) break;
                    }
                    continue;
                }
                if (comOk)
                {
                    // 已由上面的 SendComFast 处理，这里只做保底（正常走不到）。
                    var wait = i == 0 ? NotchFirstWaitMs : (noMove >= 2 ? 40 : NotchMaxWaitMs);
                    if (WaitComMoved(pos, wait, ref comTop))
                    {
                        waits++; noMove = 0;
                        Thread.Sleep(ComNotchGapMs);
                    }
                    else
                    {
                        noMove++;
                        Thread.Sleep(20);
                    }
                    continue;
                }
                if (!canWatch || sw.ElapsedMilliseconds >= SendBudgetMs) { Thread.Sleep(40); continue; }
                var wait2 = i == 0 ? NotchFirstWaitMs : NotchMaxWaitMs;
                var moved = WaitPageMoved(h, ref a, b, wait2);
                if (moved) waits++;
                (a, b) = (b, a);
                Thread.Sleep(moved ? 20 : NotchFallbackMs);
            }
        }
        var how = comOk ? $"COM间隔≤{NotchMaxWaitMs}ms" : pdfOk ? $"PDF UIA间隔≤{NotchMaxWaitMs}ms" : $"自适应间隔≤{NotchMaxWaitMs}ms";
        hits = waits;
        diag = $"→0x{target.ToInt64():X} ({cx},{cy}) {notches} 格×delta={one} {how} 命中{waits}/{Math.Max(1, notches - 1)} 用时{sw.ElapsedMilliseconds}ms";
        return true;
    }

    /// <summary>
    /// docx(COM 标尺) 的快车道：一条消息一格，只等"读数变了"就发下一格；被动画吞掉的格当场补发；
    /// 发完再用首格位移核对总量，少了的格补上。
    /// 旧逻辑每格都要等动画完全停下（≥80ms）再喘 80ms，6 格固定 0.83s，用户反馈"卡"。
    /// </summary>
    static bool SendComFast(IntPtr target, WpsDocPos pos, int notches, int cx, int cy, IntPtr wp, IntPtr lp, out string diag, out int hits)
    {
        var sw = Stopwatch.StartNew();
        var waits = 0;
        var sent = 0;
        var resend = 0;
        if (!pos.TryReadTop(out var start)) { diag = "COM 读数失败"; hits = 0; return false; }
        var cur = start;
        var px1 = 0;
        for (var i = 0; i < notches; i++)
        {
            var before = cur;
            SendMessageTimeoutW(target, WM_MOUSEWHEEL, wp, lp, 2, 1500, out _);
            sent++;
            var moved = WaitComChanged(pos, i == 0 ? Math.Min(NotchFirstWaitMs, 220) : ComNotchWaitMs, ref cur);
            var maxR = i == 0 ? 1 : ComResendMax;
            for (var r = 0; !moved && r < maxR; r++)
            {
                resend++;
                SendMessageTimeoutW(target, WM_MOUSEWHEEL, wp, lp, 2, 1500, out _);
                sent++;
                moved = WaitComChanged(pos, ComNotchWaitMs, ref cur);
            }
            if (moved)
            {
                waits++;
                if (px1 == 0) px1 = Math.Abs(cur - before);
                Thread.Sleep(ComNotchGapMs);
            }
            else
            {
                Thread.Sleep(20);
                // 第一格就完全没动 → 多半已到页首/页尾，别硬发剩下的。
                if (i == 0 && px1 == 0) break;
            }
        }
        // 总量核对：按首格位移估应有位移，少了的格补发；到边时读数不再变化就停。
        var want = notches * px1;
        while (px1 > 0 && sw.ElapsedMilliseconds < SendBudgetMs && Math.Abs(cur - start) < want - px1 / 2)
        {
            SendMessageTimeoutW(target, WM_MOUSEWHEEL, wp, lp, 2, 1500, out _);
            sent++;
            if (!WaitComChanged(pos, ComNotchWaitMs, ref cur)) break;
            resend++;
            waits++;
            Thread.Sleep(ComNotchGapMs);
        }
        hits = waits;
        diag = $"→0x{target.ToInt64():X} ({cx},{cy}) {notches} 格 COM快发 {sent}发/补{resend} 位移{Math.Abs(cur - start)}px 用时{sw.ElapsedMilliseconds}ms";
        return true;
    }

    /// <summary>只等"读数变了"（不等动画停下）：最多等 maxMs，一变立刻返回。</summary>
    static bool WaitComChanged(WpsDocPos pos, int maxMs, ref int last)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            Thread.Sleep(5);
            if (!pos.TryReadTop(out var t)) return false;
            if (t != last) { last = t; return true; }
        }
        return false;
    }

    /// <summary>
    /// 等这一格滚完：先等 Top 变化，再等它连续几次读数不变（动画结束）。
    /// 只等“变了一下”不够——WPS 的翻页动画还在走时就发下一格，后面的消息会被动画吞掉，
    /// 实测 6 格快发只落了 1 格（96px），然后第二按才补上 504px，第一拍就少测一大截。
    /// </summary>
    static bool WaitComMoved(WpsDocPos pos, int maxMs, ref int last)
    {
        var sw = Stopwatch.StartNew();
        var seen = false;
        var stable = 0;
        var cur = last;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            Thread.Sleep(5);
            if (!pos.TryReadTop(out var t)) return false;
            if (!seen)
            {
                if (t != last) { seen = true; cur = t; stable = 0; }
                continue;
            }
            if (t == cur) stable++;
            else { cur = t; stable = 0; }
            // 连续 2 次（约 10ms）读数不变，且至少已经走了 25ms，就认为这一格动画结束。
            // 这是旧路径（快速道之外）的稳定门；快速道用 WaitComChanged 只等"变了一下"。
            if (stable >= 2 && sw.ElapsedMilliseconds >= 25)
            {
                last = cur;
                return true;
            }
        }
        if (seen) { last = cur; return true; }
        return false;
    }

    /// <summary>PDF UIA 滚动条版本：等这一格数值变化并稳定，再发下一格。</summary>
    static bool WaitPdfMoved(WpsPdfPos pdf, int maxMs, ref int last)
    {
        var sw = Stopwatch.StartNew();
        var seen = false;
        var stable = 0;
        var cur = last;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            Thread.Sleep(5);
            if (!pdf.TryRead(out var v)) return false;
            if (!seen)
            {
                if (v != last) { seen = true; cur = v; stable = 0; }
                continue;
            }
            if (v == cur) stable++;
            else { cur = v; stable = 0; }
            if (stable >= 2 && sw.ElapsedMilliseconds >= 30)
            {
                last = cur;
                return true;
            }
        }
        if (seen) { last = cur; return true; }
        return false;
    }

    /// <summary>等页面画面相对 before 发生变化。变了返回 true，并把 before 更新成变化后的帧；没变返回 false。</summary>
    static bool WaitPageMoved(IntPtr h, ref Capture.Img before, Capture.Img scratch, int maxMs)
    {
        try
        {
            var b = Capture.Grab(h, false, scratch);
            if (b == null || b.Bgra == null) return false;
            if (before.W == b.W && before.H == b.H && Capture.DiffRatio(before, b) >= 0.004)
            {
                before = b;
                return true;
            }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxMs)
            {
                Thread.Sleep(NotchPollMs);
                b = Capture.Grab(h, false, b);
                if (b == null || b.Bgra == null) return false;
                if (before.W == b.W && before.H == b.H && Capture.DiffRatio(before, b) >= 0.004)
                {
                    before = b;
                    return true;
                }
            }
        }
        catch { /* 抓帧失败就退回固定间隔，别影响滚轮下发 */ }
        return false;
    }

    /// <summary>等 Ctrl/Alt/Shift/Win 连续 <paramref name="quietMs"/> 都松开。超时返回 false。</summary>
    static bool WaitModifiersQuiet(int quietMs, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        var upSince = -1;
        // 鼠标点工具栏时通常一个修饰键都没按：没必要白等完整的 120ms。
        // 只要这期间真的没有修饰键按下，50ms 就够；一旦看到按下，立刻回到完整的静默窗口。
        var need = AnyModifierDown() ? quietMs : 50;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (AnyModifierDown()) { upSince = -1; need = quietMs; }
            else
            {
                if (upSince < 0) upSince = (int)sw.ElapsedMilliseconds;
                else if (sw.ElapsedMilliseconds - upSince >= need) return true;
            }
            System.Threading.Thread.Sleep(8);
        }
        return false;
    }

    static bool AnyModifierDown() => Down(0x11) || Down(0x12) || Down(0x10) || Down(0x5B) || Down(0x5C);

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>点所在的最深可见子窗口（面积最小者）。</summary>
    static IntPtr ChildAtPoint(IntPtr root, int x, int y)
    {
        var best = IntPtr.Zero;
        var bestArea = long.MaxValue;
        EnumChildWindows(root, (h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (!GetWindowRect(h, out var r)) return true;
            if (x < r.L || x >= r.R || y < r.T || y >= r.B) return true;
            var area = (long)(r.R - r.L) * (r.B - r.T);
            if (area >= bestArea) return true;
            bestArea = area; best = h;
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
