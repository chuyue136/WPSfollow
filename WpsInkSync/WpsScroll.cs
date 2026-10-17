using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    /// <summary>往 WPS 发 <paramref name="notches"/> 格滚轮。<paramref name="dir"/> = +1 内容上移，-1 内容下移。</summary>
    public static bool Send(int notches, int dir, out string diag)
    {
        var h = Find(out var find);
        if (h == IntPtr.Zero) { diag = find; return false; }
        if (notches <= 0 || dir == 0) { diag = "参数无效"; return false; }

        GetWindowRect(h, out var r);
        var cx = (r.L + r.R) / 2;
        var cy = (r.T + r.B) / 2;
        var target = ChildAtPoint(h, cx, cy);
        if (target == IntPtr.Zero) target = h;

        var delta = -WheelDelta * notches * dir;
        var wp = (IntPtr)(delta << 16);
        var lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
        // 关键：Ctrl 被按住时 WPS 把滚轮当"缩放"（Ctrl+滚轮），我们的热键本身就是 Ctrl+Alt+方向键。
        // WPS(Qt) 处理滚轮时看的是当时的修饰键状态，所以不仅要"等到松开"，还要"松开后静默一小段"：
        // 连按热键时下一轮的 Ctrl 会跟我们的 SendMessage 撞在一起 → 偶发缩放。
        if (!WaitModifiersQuiet(120, 900))
        {
            diag = "修饰键未松开，未发送";
            return false;
        }
        SendMessageTimeoutW(target, WM_MOUSEWHEEL, wp, lp, 2, 1500, out _);
        diag = $"→0x{target.ToInt64():X} ({cx},{cy}) delta={delta}";
        return true;
    }

    /// <summary>等 Ctrl/Alt/Shift/Win 连续 <paramref name="quietMs"/> 都松开。超时返回 false。</summary>
    static bool WaitModifiersQuiet(int quietMs, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        var upSince = -1;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (AnyModifierDown()) { upSince = -1; }
            else
            {
                if (upSince < 0) upSince = (int)sw.ElapsedMilliseconds;
                else if (sw.ElapsedMilliseconds - upSince >= quietMs) return true;
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
