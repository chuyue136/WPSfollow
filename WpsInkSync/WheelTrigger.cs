using System.Runtime.InteropServices;

namespace WpsInkSync;

/// <summary>
/// 全局低级鼠标钩子，只做一件事：滚轮一动就通知外面（用来把跟随器切到高频抓帧）。
/// 钩子回调必须极快，否则会拖慢系统输入——这里只写一个时间戳。
/// </summary>
sealed class WheelTrigger : IDisposable
{
    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEWHEEL = 0x020A;
    const int WM_MOUSEHWHEEL = 0x020E;
    const uint WM_QUIT = 0x0012;

    readonly Action _onWheel;
    readonly HookProc _proc;        // 必须留字段，否则 GC 回收后回调会崩
    Thread _thread;
    uint _threadId;
    IntPtr _hook;

    public WheelTrigger(Action onWheel)
    {
        _onWheel = onWheel;
        _proc = Callback;
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) return;      // 装不上就只靠 400ms 兜底轮询
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "WpsInkWheelHook" };
        _thread.Start();
    }

    IntPtr Callback(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var msg = w.ToInt32();
            if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
            {
                try { _onWheel(); } catch { /* 钩子里绝不抛 */ }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    public void Dispose()
    {
        if (_threadId != 0 && _thread is { IsAlive: true })
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
}
