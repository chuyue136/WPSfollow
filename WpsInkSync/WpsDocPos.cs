using System.Runtime.InteropServices;

namespace WpsInkSync;

/// <summary>
/// 用 WPS 自己的对象模型量“页面滚了多少”：从文档子窗口拿到活的 Window 对象，
/// RangeFromPoint 取一个正文 Range，再用 GetPoint 读它在屏幕上的 Top。
/// 页面滚动时这个 Top 会精确变化同样的物理像素数——不依赖截图、不受空白页边/翻页动画影响。
/// 只对 .docx（OpusApp）有效；PDF 的 Qt 外壳拿不到这个对象，调用方回退到截图测量。
/// </summary>
sealed class WpsDocPos : IDisposable
{
    const uint OBJID_NATIVEOM = 0xFFFFFFF0;
    static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    // WPS 的 Word 兼容 Window 接口（类型库 wpsapi.dll，IID 00020962-...）。
    // 只声明用得到的三个成员，DISPID 来自类型库：
    //   RangeFromPoint=110、GetPoint=112、VerticalPercentScrolled=24、Hwnd=39、Caption=0。
    [ComImport, Guid("00020962-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IWpsWindow
    {
        [DispId(110)] object RangeFromPoint(int x, int y);

        [DispId(112)] void GetPoint(out int left, out int top, out int width, out int height,
            [MarshalAs(UnmanagedType.IDispatch)] object range);
    }

    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

    readonly IWpsWindow _window;
    readonly IDispatchRaw _disp;
    readonly object _range;
    readonly IntPtr _hwnd;
    int _top;
    bool _disposed;

    WpsDocPos(IWpsWindow window, object range, int top, IntPtr hwnd)
    {
        _window = window;
        _range = range;
        _top = top;
        _hwnd = hwnd;
    }

    WpsDocPos(IDispatchRaw disp, object range, int top, IntPtr hwnd)
    {
        _disp = disp;
        _range = range;
        _top = top;
        _hwnd = hwnd;
    }

    /// <summary>刻度尺绑定的文档子窗口；换文档/换标签后调用方要丢弃它。</summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>取锚点那一刻的正文屏幕 Top（物理像素）。失败返回 null，调用方走截图兜底。</summary>
    public static WpsDocPos TryCreate(IntPtr documentHwnd, int screenX, int screenY, out string why)
    {
        why = "";
        object com = null;
        try
        {
            var iid = IID_IDispatch;
            var hr = AccessibleObjectFromWindow(documentHwnd, OBJID_NATIVEOM, ref iid, out com);
            if (hr != 0 || com == null)
            {
                why = $"AccessibleObjectFromWindow hr=0x{hr:X8}";
                return null;
            }
            if (com is not IWpsWindow w)
            {
                // ICC 是 UIAccess 进程，部分 WPS 版本只把这个对象暴露成 IDispatch，
                // 强转 IWpsWindow 会 QI 失败。退回按 DISPID 晚绑定（PowerShell 走的就是这条路）。
                if (com is IDispatchRaw d)
                {
                    var lateRange = WpsDispatch.RangeFromPoint(d, screenX, screenY);
                    if (lateRange == null) { why = "RangeFromPoint(晚绑定) 返回空"; return null; }
                    var lateTop = WpsDispatch.GetTop(d, lateRange);
                    return new WpsDocPos(d, lateRange, lateTop, documentHwnd);
                }
                why = $"不是 WPS Window 接口 type={com.GetType().FullName}";
                return null;
            }
            var range = w.RangeFromPoint(screenX, screenY);
            if (range == null)
            {
                why = "RangeFromPoint 返回空";
                return null;
            }
            if (!TryReadTop(w, range, out var top))
            {
                why = "GetPoint 失败";
                return null;
            }
            return new WpsDocPos(w, range, top, documentHwnd);
        }
        catch (Exception ex)
        {
            why = ex.GetType().Name + " " + ex.Message;
            return null;
        }
        finally
        {
            // 失败时把 RCW 放掉；成功时对象由 WpsDocPos 持有，Dispose 再放。
            if (why.Length != 0 && com != null)
            {
                try { Marshal.ReleaseComObject(com); } catch { }
            }
        }
    }

    public int Top => _top;

    /// <summary>同一个 Range 现在的屏幕 Top。页面滚了 N 物理px，它就变化 N 物理px。</summary>
    public bool TryReadTop(out int top)
    {
        top = 0;
        try
        {
            if (_disp != null) { top = WpsDispatch.GetTop(_disp, _range); return true; }
            return TryReadTop(_window, _range, out top);
        }
        catch { return false; }
    }

    static bool TryReadTop(IWpsWindow w, object range, out int top)
    {
        top = 0;
        try
        {
            w.GetPoint(out _, out top, out _, out _, range);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_range != null) Marshal.ReleaseComObject(_range); } catch { }
        try { if (_window != null) Marshal.ReleaseComObject(_window); } catch { }
        try { if (_disp != null) Marshal.ReleaseComObject(_disp); } catch { }
    }
}
