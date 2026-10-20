using System;
using System.Windows.Automation;

namespace WpsInkSync;

/// <summary>
/// PDF 的 WPS(Qt) 外壳没有 Word 那套 Window.RangeFromPoint，但 UI Automation 把
/// PDF 的滚动条暴露成了 KPdfScrollBarWheelFixed，RangeValue 的值就是**屏幕物理像素**的
/// 滚动位置（实测 150%、125% 下单格 UIA 变化 60，截图实测 dy 也是 60；288dpi 会按物理像素放大）。
/// 这比抓整窗截图再匹配稳定得多，也不会被空白页、重复版式骗成 0px。
/// </summary>
sealed class WpsPdfPos : IDisposable
{
    AutomationElement _bar;
    RangeValuePattern _range;
    IntPtr _top;

    public IntPtr Hwnd => _top;

    public static WpsPdfPos TryCreate(IntPtr topHwnd, out string why)
    {
        why = "";
        if (topHwnd == IntPtr.Zero) { why = "窗口为空"; return null; }
        try
        {
            var root = AutomationElement.FromHandle(topHwnd);
            if (root == null) { why = "UIA 根为空"; return null; }
            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "KPdfScrollBarWheelFixed"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar));
            var bar = root.FindFirst(TreeScope.Descendants, cond);
            if (bar == null) { why = "未找到 PDF 滚动条"; return null; }
            if (!bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var p))
            { why = "PDF 滚动条没有 RangeValue"; return null; }
            return new WpsPdfPos(bar, (RangeValuePattern)p, topHwnd);
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name} {ex.Message}";
            return null;
        }
    }

    WpsPdfPos(AutomationElement bar, RangeValuePattern range, IntPtr top)
    {
        _bar = bar; _range = range; _top = top;
    }

    /// <summary>滚动位置，单位=屏幕物理像素；向下滚时数值变大。</summary>
    public bool TryRead(out int value)
    {
        try
        {
            var r = _range;
            if (r == null) { value = 0; return false; }
            value = (int)Math.Round(r.Current.Value);
            return true;
        }
        catch { value = 0; return false; }
    }

    public int Value => TryRead(out var v) ? v : 0;

    public void Dispose()
    {
        _bar = null; _range = null; _top = IntPtr.Zero;
    }
}
