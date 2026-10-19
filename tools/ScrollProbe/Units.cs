namespace ScrollProbe;

/// <summary>
/// 单位换算的唯一出口。铁律（2026-10-18 希沃八代实测翻车后立的）：
///   测量侧（抓帧、带匹配、账本）一律是**屏幕物理像素**；
///   墨迹侧（ICC 白板 TransformInk）一律是 **DIP**（1/96 英寸）。
/// 两者差一个 dpiScale（物理px / DIP）。混用会让 300% 缩放的机器上墨迹走 3 倍：
/// 希沃八代 dpi=288，每格 152 物理px 被当成 152 DIP 推出去 = 456 物理px，笔迹直接和页面脱离。
/// 换算只允许在这里做，别处出现裸的 * / dpiScale 就是 bug。
/// </summary>
public static class Units
{
    /// <summary>未标定前的猜测：96dpi 下一页一格大约 60 物理px（老版本把它当 DIP 用也能凑巧对上）。</summary>
    public const double GuessStepPhys96 = 60.0;

    /// <summary>GetDpiForWindow 的返回值（96=100%, 144=150%, 192=200%, 288=300%）→ 物理px/DIP。</summary>
    public static double Scale(uint dpi) => dpi > 0 ? dpi / 96.0 : 1.0;

    public static double PhysToDip(double phys, double scale) => scale > 0 ? phys / scale : phys;

    public static double DipToPhys(double dip, double scale) => dip * scale;

    /// <summary>内容位移（物理px，内容上移为正）→ 墨迹该走的位移（DIP，上移为负）。</summary>
    public static double InkDipFor(double contentPhys, double scale) => -PhysToDip(contentPhys, scale);

    /// <summary>补差：乐观平移的预期位移（物理）与实际测到的位移（物理）之差，换成墨迹 DIP。</summary>
    public static double CorrectionDip(double expectedPhys, double measuredPhys, double scale)
        => PhysToDip(expectedPhys - measuredPhys, scale);

    /// <summary>对账：页面实测总位移（物理）与墨迹实际走过的量（DIP）之差，得到墨迹还要再走的 DIP。</summary>
    public static double ReconcileDip(double pagePhys, double inkDip, double scale)
        => -PhysToDip(pagePhys, scale) - inkDip;

    public static string Describe(double stepPhys, double scale)
        => $"{stepPhys:0} 物理px = {PhysToDip(stepPhys, scale):0.0} DIP @{scale:0.##}x";
}
