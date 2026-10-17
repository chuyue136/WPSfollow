using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ScrollProbe;

/// <summary>
/// 滚动量测量探针：对指定的 WPS 窗口同时用 UIA / Win32 滚动条 / 消息注入三种手段取数，
/// 判定「哪个能拿到随滚动变化的量、精度多少」。
///   ScrollProbe dump           列出窗口 & 所有候选读数
///   ScrollProbe probe [notch]  注入滚轮前后各取一次，打印差量
/// </summary>
internal static class Program
{
    const int WM_MOUSEWHEEL = 0x020A;
    const int SB_HORZ = 0, SB_VERT = 1;
    const uint SIF_ALL = 0x17;
    const int OBJID_HSCROLL = -6, OBJID_VSCROLL = -5;

    delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
    [DllImport("user32.dll")] static extern bool GetScrollBarInfo(IntPtr h, int obj, ref SCROLLBARINFO sbi);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct SCROLLINFO
    { public uint cbSize, fMask; public int nMin, nMax; public uint nPage; public int nPos, nTrackPos; }
    [StructLayout(LayoutKind.Sequential)] struct SCROLLBARINFO
    { public uint cbSize; public RECT rcScrollBar; public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved; public uint rgstate0; }

    sealed class Cand
    {
        public string Kind = "";
        public string Detail = "";
        public double Num;
        public bool HasNum;
        public override string ToString() => $"{Kind} {Detail} = {(HasNum ? Num.ToString("0.###") : "-")}";
    }

    static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0] : "dump";
        var notch = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 3;
        Console.OutputEncoding = Encoding.UTF8;

        if (cmd == "cmp")
        {
            // cmp a.bmp b.bmp [maxShift] —— 离线量两张整屏 BMP 的位移（b 相对 a）
            if (args.Length < 3) { Console.WriteLine("用法: cmp a.bmp b.bmp [maxShift]"); return 2; }
            var a = Capture.Load(args[1]);
            var b = Capture.Load(args[2]);
            if (a == null || b == null) { Console.WriteLine("BMP 读不出来（要 32bpp）"); return 2; }
            var max = args.Length > 3 ? int.Parse(args[3]) : 320;
            var ms = Capture.MatchBand(a, b, max);
            Console.WriteLine($"diff={Capture.DiffRatio(a, b):0.000} {ms}");
            return 0;
        }

        var targets = FindWpsWindows();
        if (targets.Count == 0) { Console.WriteLine("没找到 WPS 窗口（wps/wpspdf/et/wpp 进程的可见顶层窗口）"); return 2; }

        Console.WriteLine($"=== 找到 {targets.Count} 个 WPS 顶层窗口 ===");
        foreach (var t in targets) Console.WriteLine($"  {t}");

        var pick = targets.OrderByDescending(t => (t.R.R - t.R.L) * (t.R.B - t.R.T)).First();
        Console.WriteLine($"\n=== 选中 {pick} ===");

        if (cmd == "dump")
        {
            DumpTree(pick.Hwnd);
            Console.WriteLine("\n--- Win32 滚动条 ---");
            foreach (var c in Win32Scroll(pick.Hwnd)) Console.WriteLine("  " + c);
            Console.WriteLine("\n--- UIA 可滚动/滚动条元素 ---");
            var uia = UiaScan(pick.Hwnd);
            foreach (var c in uia) Console.WriteLine("  " + c);
            if (uia.Count == 0) Console.WriteLine("  (UIA 无任何 ScrollPattern / ScrollBar 元素 — Qt 可访问性未暴露)");
            return 0;
        }

        if (cmd == "probe")
        {
            var beforeU = UiaScan(pick.Hwnd);
            var beforeW = Win32Scroll(pick.Hwnd);
            Console.WriteLine($"\n--- 注入前 ---");
            beforeU.ForEach(c => Console.WriteLine("  " + c));
            beforeW.ForEach(c => Console.WriteLine("  " + c));

            var cx = (pick.R.L + pick.R.R) / 2;
            var cy = (pick.R.T + pick.R.B) / 2;
            Console.WriteLine($"\n--- 向 ({cx},{cy}) 注入 {notch} 格滚轮 ---");
            var lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
            var wp = (IntPtr)((-120 * notch) << 16);
            SendMessageTimeoutW(pick.Hwnd, WM_MOUSEWHEEL, wp, lp, 2, 1500, out _);
            Thread.Sleep(600);

            var afterU = UiaScan(pick.Hwnd);
            var afterW = Win32Scroll(pick.Hwnd);

            Console.WriteLine($"\n--- 差量（能变的候选就是可用测量源） ---");
            Report(beforeU, afterU, "UIA");
            Report(beforeW, afterW, "Win32");
            return 0;
        }

        if (cmd == "shot")
        {
            var useScreen = args.Contains("screen");
            var img = Capture.Grab(pick.Hwnd, useScreen);
            Console.WriteLine($"  {img.How} {img.W}x{img.H} mean={img.Mean:0.0} blank={img.Blank}");
            var p = Path.Combine(Environment.CurrentDirectory, $"shot_{(useScreen ? "screen" : "pw")}.bmp");
            Capture.Save(img, p);
            Console.WriteLine("  保存 " + p);
            return 0;
        }

        if (cmd == "shift")
        {
            var useScreen = args.Contains("screen");
            var a = Capture.Grab(pick.Hwnd, useScreen);
            Console.WriteLine($"\nA: {a.How} {a.W}x{a.H} mean={a.Mean:0.0} blank={a.Blank}");
            if (a.Blank && !useScreen)
            {
                Console.WriteLine("  PrintWindow 黑图 → 改用屏幕 BitBlt");
                a = Capture.Grab(pick.Hwnd, true);
                Console.WriteLine($"A': {a.How} {a.W}x{a.H} mean={a.Mean:0.0}");
            }

            var cx = (pick.R.L + pick.R.R) / 2;
            var cy = (pick.R.T + pick.R.B) / 2;
            var child = ChildAtPoint(pick.Hwnd, cx, cy);
            var target = child == IntPtr.Zero ? pick.Hwnd : child;
            Console.WriteLine($"点 ({cx},{cy}) 命中的 WPS 子窗口: 0x{target.ToInt64():X} {ClassOf(target)}");

            // 尝试 1：向后代窗口投递 WM_MOUSEWHEEL
            var lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
            var wp = (IntPtr)((-120 * notch) << 16);
            SendMessageTimeoutW(target, WM_MOUSEWHEEL, wp, lp, 2, 1500, out _);
            Thread.Sleep(700);
            var b = Capture.Grab(pick.Hwnd, a.How.StartsWith("BitBlt"));
            var d1 = Capture.DiffRatio(a, b);
            Console.WriteLine($"尝试1 消息注入 {notch} 格 → 差异 {d1:P1}");

            // 尝试 2：真实输入（鼠标移到窗口中心滚轮，再移回）
            if (d1 < 0.005)
            {
                GetCursorPos(out var save);
                SetCursorPos(cx, cy);
                Thread.Sleep(150);
                mouse_event(0x0800, 0, 0, -120 * notch, UIntPtr.Zero);
                Thread.Sleep(800);
                SetCursorPos(save.X, save.Y);
                b = Capture.Grab(pick.Hwnd, a.How.StartsWith("BitBlt"));
                d1 = Capture.DiffRatio(a, b);
                Console.WriteLine($"尝试2 真实滚轮 → 差异 {d1:P1}");
            }

            Console.WriteLine($"差异像素比例 = {d1:P1}  (>2% 说明确实滚动了)");

            // 2D 带匹配：唯一峰值，直接给出内容位移
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var m = Capture.MatchBand(a, b, 800);
            sw.Stop();
            Console.WriteLine($"2D 匹配: {m}  ({sw.ElapsedMilliseconds}ms)");
            Console.WriteLine($"→ 墨迹应平移 dx={-m.Dx} dy={-m.Dy} DIP");

            var dir = Environment.CurrentDirectory;
            Capture.Save(a, Path.Combine(dir, "shift_A.bmp"));
            Capture.Save(b, Path.Combine(dir, "shift_B.bmp"));
            Console.WriteLine("帧已存 shift_A.bmp / shift_B.bmp");
            return 0;
        }

        Console.WriteLine("用法: ScrollProbe dump | probe [notch] | shot [screen] | shift [notch] [screen]");
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, 256);
        return sb.ToString();
    }

    /// <summary>点所在的最深 WPS 子窗口（面积最小者）。</summary>
    static IntPtr ChildAtPoint(IntPtr root, int x, int y)
    {
        var best = IntPtr.Zero;
        var bestArea = long.MaxValue;
        EnumChildWindows(root, (h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowRect(h, out var r);
            if (x < r.L || x >= r.R || y < r.T || y >= r.B) return true;
            var area = (long)(r.R - r.L) * (r.B - r.T);
            if (area < bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>列剖面：每列在 [y0,y1) 内的暗像素个数（横向位移检测用）。</summary>
    static double[] ColumnProfile(Capture.Img img, double y0Frac, double y1Frac)
    {
        var y0 = Math.Max(0, (int)(img.H * y0Frac));
        var y1 = Math.Min(img.H, (int)(img.H * y1Frac));
        if (y1 <= y0) { y0 = 0; y1 = img.H; }
        var prof = new double[img.W];
        for (var x = 0; x < img.W; x++)
        {
            var dark = 0;
            for (var y = y0; y < y1; y += 2)
            {
                var i = (y * img.W + x) * 4;
                var lum = 0.114 * img.Bgra[i] + 0.587 * img.Bgra[i + 1] + 0.299 * img.Bgra[i + 2];
                if (lum < 170) dark++;
            }
            prof[x] = dark;
        }
        return prof;
    }

    static void Report(List<Cand> before, List<Cand> after, string tag)
    {
        var n = Math.Min(before.Count, after.Count);
        var changed = 0;
        for (var i = 0; i < n; i++)
        {
            var b = before[i];
            var a = after[i];
            if (!b.HasNum || !a.HasNum) continue;
            if (Math.Abs(a.Num - b.Num) < 1e-9) continue;
            changed++;
            Console.WriteLine($"  [{tag}] 变化 {b.Kind} {b.Detail}: {b.Num:0.###} -> {a.Num:0.###}");
        }
        if (changed == 0) Console.WriteLine($"  [{tag}] 全部无变化");
    }

    // ---------- 窗口枚举 ----------

    sealed class Target
    {
        public IntPtr Hwnd; public string Proc = ""; public string Cls = ""; public string Title = ""; public RECT R;
        public override string ToString() =>
            $"hwnd=0x{Hwnd.ToInt64():X} proc={Proc,-10} class={Cls,-24} rect=({R.L},{R.T},{R.R - R.L}x{R.B - R.T}) title={Title}";
    }

    static readonly string[] WpsProcs = { "wps", "wpspdf", "wpscenter", "wpsoffice", "et", "wpp" };

    static List<Target> FindWpsWindows()
    {
        var list = new List<Target>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
            catch { return true; }
            if (!WpsProcs.Contains(proc)) return true;
            GetWindowRect(h, out var r);
            if (r.R - r.L < 200 || r.B - r.T < 200) return true;
            var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
            var cb = new StringBuilder(256); GetClassNameW(h, cb, 256);
            list.Add(new Target { Hwnd = h, Proc = proc, Cls = cb.ToString(), Title = sb.ToString(), R = r });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static void DumpTree(IntPtr root)
    {
        Console.WriteLine("\n--- 子窗口树（含滚动条状态）---");
        var depth = new Dictionary<IntPtr, int> { [root] = 0 };
        EnumChildWindows(root, (h, _) =>
        {
            GetWindowRect(h, out var r);
            var cb = new StringBuilder(256); GetClassNameW(h, cb, 256);
            var style = GetWindowLongW(h, -16);
            var hasScroll = (style & 0x00200000) != 0 || (style & 0x00100000) != 0;
            var siV = Scroll(h, SB_VERT);
            var sbH = Scroll(h, SB_HORZ);
            var vis = IsWindowVisible(h) ? "vis" : "hid";
            Console.WriteLine($"  {vis} class={cb,-24} rect=({r.L},{r.T},{r.R - r.L}x{r.B - r.T}) styleScroll={hasScroll} v={siV} h={sbH}");
            return true;
        }, IntPtr.Zero);
    }

    // ---------- Win32 滚动条 ----------

    static string Scroll(IntPtr h, int bar)
    {
        var si = new SCROLLINFO { cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(), fMask = SIF_ALL };
        if (!GetScrollInfo(h, bar, ref si)) return "none";
        return $"{si.nPos}[{si.nMin}..{si.nMax} p{si.nPage}]";
    }

    static List<Cand> Win32Scroll(IntPtr root)
    {
        var list = new List<Cand>();
        void Take(IntPtr h, string who)
        {
            var si = new SCROLLINFO { cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(), fMask = SIF_ALL };
            if (GetScrollInfo(h, SB_VERT, ref si))
                list.Add(new Cand { Kind = "GetScrollInfo/V", Detail = $"{who}.nPos", Num = si.nPos, HasNum = true });
            var bi = new SCROLLBARINFO { cbSize = (uint)Marshal.SizeOf<SCROLLBARINFO>() };
            if (GetScrollBarInfo(h, OBJID_VSCROLL, ref bi))
                list.Add(new Cand { Kind = "ScrollBarInfo/V", Detail = $"{who}.xyThumbTop", Num = bi.xyThumbTop, HasNum = true });
        }
        Take(root, "top");
        var i = 0;
        EnumChildWindows(root, (h, _) => { Take(h, $"child{i++}"); return true; }, IntPtr.Zero);
        if (list.All(c => !c.HasNum)) list.Add(new Cand { Kind = "Win32", Detail = "无任何滚动条信息" });
        return list;
    }

    // ---------- UIA ----------

    static List<Cand> UiaScan(IntPtr root)
    {
        var list = new List<Cand>();
        try
        {
            var el = AutomationElement.FromHandle(root);
            if (el == null) return list;
            var stack = new Stack<(AutomationElement e, int d)>();
            stack.Push((el, 0));
            var visited = 0;
            while (stack.Count > 0 && visited < 3000)
            {
                var (e, d) = stack.Pop();
                visited++;
                string ct = Safe(() => e.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""));
                string name = Safe(() => e.Current.Name);
                string cls = Safe(() => e.Current.ClassName);

                if (e.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp))
                {
                    var s = (ScrollPattern)sp;
                    list.Add(new Cand { Kind = $"UIA/Scroll", Detail = $"{ct} '{Trim(name)}' cls={cls} vScrollable={s.Current.VerticallyScrollable} view={s.Current.VerticalViewSize:0.#}", Num = s.Current.VerticalScrollPercent, HasNum = true });
                }
                if (ct == "ScrollBar" && e.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp))
                {
                    var rv = (RangeValuePattern)rp;
                    list.Add(new Cand { Kind = "UIA/RangeValue", Detail = $"{ct} '{Trim(name)}' cls={cls}", Num = rv.Current.Value, HasNum = true });
                }

                if (d < 14)
                {
                    AutomationElementCollection kids = null;
                    try { kids = e.FindAll(TreeScope.Children, Condition.TrueCondition); } catch { }
                    if (kids != null)
                        foreach (AutomationElement k in kids) stack.Push((k, d + 1));
                }
            }
            Console.WriteLine($"  (UIA 遍历 {visited} 个元素)");
        }
        catch (Exception ex) { Console.WriteLine("  UIA 失败: " + ex.GetType().Name + " " + ex.Message); }
        return list;
    }

    static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return "?"; } }
    static string Trim(string s) => s == null ? "" : (s.Length > 28 ? s[..28] : s);
}
