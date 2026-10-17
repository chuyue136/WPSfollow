using System.IO;
using System.Runtime.InteropServices;

namespace ScrollProbe;

/// <summary>最小 GDI 抓帧 + 行剖面互相关测纵向位移。不引入任何图像库。</summary>
internal static class Capture
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public BITMAPINFOHEADER h; public uint c0, c1, c2; }

    public sealed class Img
    {
        public int W, H;
        public byte[] Bgra;   // top-down
        public string How = "";
        public double Mean;
        public bool Blank => Mean < 3;
    }

    /// <summary>
    /// fromScreen=true 用屏幕 BitBlt（会含其它覆盖窗口），false 用 PrintWindow（只含目标窗口）。
    /// reuse：传入上一次的 Img 就会复用它的像素缓冲（尺寸一致时），高频抓帧时省掉 8MB/次的分配。
    /// </summary>
    public static Img Grab(IntPtr hwnd, bool fromScreen, Img reuse = null)
    {
        GetWindowRect(hwnd, out var r);
        var w = r.R - r.L; var h = r.B - r.T;
        var img = (reuse != null && reuse.W == w && reuse.H == h && reuse.Bgra != null && reuse.Bgra.Length == w * h * 4)
            ? reuse
            : new Img { W = w, H = h, Bgra = new byte[w * h * 4] };
        img.W = w; img.H = h;
        if (w <= 0 || h <= 0) return img;

        var sdc = fromScreen ? GetDC(IntPtr.Zero) : GetDC(hwnd);
        var mdc = CreateCompatibleDC(sdc);
        var bmp = CreateCompatibleBitmap(sdc, w, h);
        var old = SelectObject(mdc, bmp);
        bool ok;
        if (fromScreen)
            ok = BitBlt(mdc, 0, 0, w, h, sdc, r.L, r.T, 0x00CC0020);
        else
            ok = PrintWindow(hwnd, mdc, 2 /*PW_RENDERFULLCONTENT*/);

        var bi = new BITMAPINFO();
        bi.h.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bi.h.biWidth = w; bi.h.biHeight = -h; bi.h.biPlanes = 1; bi.h.biBitCount = 32;
        var buf = img.Bgra;
        var lines = GetDIBits(mdc, bmp, 0, (uint)h, buf, ref bi, 0);

        SelectObject(mdc, old); DeleteObject(bmp); DeleteDC(mdc);
        ReleaseDC(fromScreen ? IntPtr.Zero : hwnd, sdc);

        img.Bgra = buf;
        img.How = $"{(fromScreen ? "BitBlt" : "PrintWindow")} ok={ok} lines={lines}";
        double sum = 0;
        for (var i = 0; i < buf.Length; i += 4 * 97) sum += buf[i + 2];  // 采样 R 通道
        img.Mean = sum / (buf.Length / (4.0 * 97));
        return img;
    }

    /// <summary>
    /// 行剖面：每行在 [x0,x1) 内的「暗像素个数」。
    /// 不用亮度均值——白底文档页均值差不到 5/255，会被静态 UI 淹没；暗像素计数才有尖峰。
    /// </summary>
    public static double[] RowProfile(Img img, double x0Frac, double x1Frac, double y0Frac = 0, double y1Frac = 1)
    {
        var x0 = Math.Max(0, (int)(img.W * x0Frac));
        var x1 = Math.Min(img.W, (int)(img.W * x1Frac));
        if (x1 <= x0) { x0 = 0; x1 = img.W; }
        var ya = Math.Max(0, (int)(img.H * y0Frac));
        var yb = Math.Min(img.H, (int)(img.H * y1Frac));
        if (yb <= ya) { ya = 0; yb = img.H; }
        var prof = new double[yb - ya];
        for (var y = ya; y < yb; y++)
        {
            var rowBase = y * img.W * 4;
            var dark = 0;
            for (var x = x0; x < x1; x += 2)
            {
                var i = rowBase + x * 4;
                var lum = 0.114 * img.Bgra[i] + 0.587 * img.Bgra[i + 1] + 0.299 * img.Bgra[i + 2];
                if (lum < 170) dark++;
            }
            prof[y - ya] = dark;
        }
        return prof;
    }

    public sealed class ShiftResult
    {
        public int Best;
        public double Score;
        public int SecondBest;
        public double SecondScore;
        public override string ToString() =>
            $"dy={Best} ncc={Score:0.0000} 次优 dy={SecondBest} ncc={SecondScore:0.0000} 区分度={Score - SecondScore:0.000}";
    }

    /// <summary>在 ±maxShift 内找使 profile B 相对 A 平移后最相似的位移（归一化互相关）。</summary>
    public static ShiftResult Correlate(double[] a, double[] b, int maxShift)
    {
        var res = new ShiftResult();
        var best = double.NegativeInfinity; var bestS = 0;
        var scores = new List<(int s, double v)>();
        for (var s = -maxShift; s <= maxShift; s++)
        {
            var y0 = Math.Max(0, -s);
            var y1 = Math.Min(a.Length, b.Length - s);
            if (y1 - y0 < a.Length / 3) continue;   // 重叠太少不采信

            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; var n = 0;
            for (var y = y0; y < y1; y++)
            {
                double va = a[y], vb = b[y + s];
                sa += va; sb += vb; saa += va * va; sbb += vb * vb; sab += va * vb; n++;
            }
            var cov = sab / n - (sa / n) * (sb / n);
            var va2 = saa / n - (sa / n) * (sa / n);
            var vb2 = sbb / n - (sb / n) * (sb / n);
            var den = Math.Sqrt(Math.Max(va2, 1e-9) * Math.Max(vb2, 1e-9));
            var ncc = cov / den;
            scores.Add((s, ncc));
            if (ncc > best) { best = ncc; bestS = s; }
        }
        res.Best = bestS; res.Score = best;
        var second = scores.Where(x => Math.Abs(x.s - bestS) > 3).OrderByDescending(x => x.v).FirstOrDefault();
        res.SecondBest = second.s; res.SecondScore = second.v;
        return res;
    }

    /// <summary>
    /// 2D 带匹配：取 B 中「暗像素最密」的一条横带，在 A 的 ±maxShift 行里找最相似的落点。
    /// 比行剖面 NCC 可靠——文字行间距是准周期的，1D 剖面会给出多个几乎等高的峰；
    /// 带上整条带的横向细节后峰值唯一。
    /// 返回 Dy&gt;0 表示内容相对上一帧上移了 Dy 像素（B[y] == A[y+Dy]）。
    /// </summary>
    public static Shift2D MatchBand(Img a, Img b, int maxShift, double x0Frac = 0.12, double x1Frac = 0.885,
                                    double y0Frac = 0.14, double y1Frac = 0.93)    {
        var res = new Shift2D();
        if (a.Bgra == null || b.Bgra == null || a.W != b.W || a.H != b.H) return res;

        const int F = 4;                       // 粗搜索降采样倍数
        var ga = Downscale(a, F);
        var gb = Downscale(b, F);

        var bandH = Math.Max(8, gb.H / 6);
        var x0 = Math.Max(0, (int)(gb.W * x0Frac));
        var x1 = Math.Min(gb.W, (int)(gb.W * x1Frac));
        var yLo = Math.Max(0, (int)(gb.H * y0Frac));
        var yHi = Math.Min(gb.H, (int)(gb.H * y1Frac));

        // 1) 选带：文档页区域内暗像素最多的窗口（避开工具栏/状态栏这些静态 UI，否则相关性钉在 dy=0）
        double bestDark = -1; var bandQ = yLo;
        for (var y = yLo; y + bandH <= yHi; y += 2)
        {
            double s = 0;
            for (var yy = y; yy < y + bandH; yy++)
                for (var x = x0; x < x1; x += 2)
                    if (gb.Px[yy * gb.W + x] < 170) s++;
            if (s > bestDark) { bestDark = s; bandQ = y; }
        }
        if (bestDark <= 0) return res;         // 整条带都是白纸，没法定位
        res.BandY = bandQ * F;
        res.BandH = bandH * F;

        // 2) 粗搜索：降采样域逐位移求归一化 SAD
        var maxQ = maxShift / F;
        var scores = new List<(int s, double v)>();
        for (var s = -maxQ; s <= maxQ; s++)
        {
            var ay = bandQ + s;
            if (ay < 0 || ay + bandH > ga.H) continue;
            double sum = 0; long n = 0;
            for (var y = 0; y < bandH; y++)
            {
                var offA = (ay + y) * ga.W;
                var offB = (bandQ + y) * gb.W;
                for (var x = x0; x < x1; x++)
                {
                    sum += Math.Abs(ga.Px[offA + x] - gb.Px[offB + x]);
                    n++;
                }
            }
            if (n > 0) scores.Add((s * F, sum / (n * 255.0)));
        }
        if (scores.Count == 0) return res;
        var bestEntry = scores.OrderBy(v => v.v).First();
        var second = scores.Where(v => Math.Abs(v.s - bestEntry.s) > 3 * F).OrderBy(v => v.v).FirstOrDefault();
        res.SecondDy = second.s;
        res.SecondScore = 1.0 - second.v;

        // 3) 精修：全分辨率逐像素 SAD。注意不能拿"粗搜索分数"跟"精修分数"比大小
        //    （两者采样密度/亮度口径不同），所以次优候选也用同一口径精修一遍再比。
        var coarse = bestEntry.s;
        var cands = new List<int> { coarse };
        if (second.v <= bestEntry.v + 0.05) cands.Add(second.s);

        // 在指定带上算 dy 位移后的归一化 SAD（越小越像）
        double SadAt(int bandY, int bandH, int dy)
        {
            var ay = bandY + dy;
            if (ay < 0 || ay + bandH > a.H) return double.MaxValue;
            double sum = 0; long n = 0;
            for (var y = 0; y < bandH; y += 2)
            {
                var offA = ((ay + y) * a.W) * 4;
                var offB = ((bandY + y) * b.W) * 4;
                for (var x = x0 * F; x < x1 * F; x += 2)
                {
                    sum += Math.Abs(Lum(a.Bgra, offA + x * 4) - Lum(b.Bgra, offB + x * 4));
                    n++;
                }
            }
            return n == 0 ? double.MaxValue : sum / (n * 255.0);
        }

        (int Dy, double S) Refine(int center)
        {
            var bdy = center; var bs = double.MaxValue;
            for (var dy = center - F; dy <= center + F; dy++)
            {
                var v = SadAt(res.BandY, res.BandH, dy);
                if (v < bs) { bs = v; bdy = dy; }
            }
            return (bdy, bs);
        }

        var results = new List<(int Dy, double S)>();
        foreach (var c in cands) results.Add(Refine(c));
        results.Sort((p, q) => p.S.CompareTo(q.S));

        var top = results[0];
        res.Dy = top.Dy;
        res.Score = 1.0 - top.S;

        var secondFine = second.v;
        foreach (var r in results)
        {
            if (Math.Abs(r.Dy - top.Dy) > 3 * F) { secondFine = r.S; break; }
        }

        // 3b) 两位候选咬得很紧（文字行间距准周期，隔一行看几乎一样）时，换一块内容再判一次。
        //     这是唯一能解决"相似度差 0.003、位移却差 15px"的便宜办法。
        if (results.Count > 1 && secondFine - top.S < 0.02)
        {
            var pageTop = (int)(b.H * y0Frac);
            var pageBottom = (int)(b.H * y1Frac);
            var altH = Math.Max(60, res.BandH);
            // 复核带必须容得下位移：内容往下走(dy<0)用页面底部那块，反之用顶部那块
            var altY = results[0].Dy < 0 ? pageBottom - altH : pageTop;
            if (altY < pageTop) altY = pageTop;

            var s0 = SadAt(altY, altH, results[0].Dy);
            var s1 = SadAt(altY, altH, results[1].Dy);
            if (s1 < s0 - 0.003)
            {
                (results[0], results[1]) = (results[1], results[0]);
                res.Dy = results[0].Dy;
                res.Score = 1.0 - results[0].S;
                secondFine = results[1].S;
                res.Note += $" →复核y={altY}({s0:0.000}/{s1:0.000})";
            }
            else
            {
                res.Note += $" →复核y={altY}({s0:0.000}/{s1:0.000} 维持)";
            }
        }
        res.SecondScore = 1.0 - secondFine;

        // 4) 横向：固定 dy 后在降采样域搜 dx（pdf 左右移动/翻页）
        double bx = double.MaxValue; var bnx = 0;
        for (var s = -maxQ; s <= maxQ; s++)
        {
            var ax = x0 + s;
            if (ax < 0 || x1 + s > ga.W) continue;
            double sum = 0; long n = 0;
            for (var y = 0; y < bandH; y++)
            {
                var offA = (bandQ + res.Dy / F + y) * ga.W;
                var offB = (bandQ + y) * gb.W;
                for (var x = x0; x < x1; x++)
                {
                    sum += Math.Abs(ga.Px[offA + x + s] - gb.Px[offB + x]);
                    n++;
                }
            }
            if (n == 0) continue;
            var v = sum / (n * 255.0);
            if (s == 0) res.DxAtZero = 1.0 - v;
            if (v < bx) { bx = v; bnx = s; }
        }
        res.Dx = bnx * F;
        res.DxScore = bx == double.MaxValue ? 0 : 1.0 - bx;
        // 横带上的文字行横向自相似，dx 的 SAD 曲线常常几乎是平的：只有明显优于 dx=0 才采信
        if (res.DxScore - res.DxAtZero < 0.02) res.Dx = 0;
        return res;
    }

    /// <summary>
    /// 上下两块**互不重叠**的内容带各测一次，两块都够自信且位移一致才认。
    /// 单带匹配在 docx 这种"页间大片留白 + 行距准周期"的版面上会给出高分但错误的位移
    /// （实测同一批按键一次说 60px、一次说 174px，墨迹就跟着跑错），双带一致能把这票否掉。
    /// </summary>
    public static bool MatchTwoBands(Img a, Img b, int maxShift, out Shift2D res)
    {
        res = MatchBand(a, b, maxShift, 0.12, 0.885, 0.15, 0.55);
        var r2 = MatchBand(a, b, maxShift, 0.12, 0.885, 0.55, 0.95);
        bool Ok(Shift2D r) => r.Score >= 0.95 && (r.Score - r.SecondScore) >= 0.03 && r.Dx == 0;
        if (!Ok(res) || !Ok(r2)) return false;
        if (Math.Abs(res.Dy - r2.Dy) > 2) return false;
        res.Note = $"{res.Note} 双带一致({res.Dy}/{r2.Dy} @{res.BandY}/{r2.BandY})".Trim();
        return true;
    }

    /// <summary>
    /// 对账用的宽松版双带：docx 200% 下 WPS 每次重绘都会让相似度天花板停在 0.92~0.97，
    /// 严格门槛（0.95/0.03）永远过不了。仍然要求两块带各自成立且位移一致，
    /// 免得单带在准周期文本上给出高分错位。
    /// </summary>
    public static bool MatchTwoBandsLoose(Img a, Img b, int maxShift, out Shift2D res)
    {
        res = MatchBand(a, b, maxShift, 0.12, 0.885, 0.15, 0.55);
        var r2 = MatchBand(a, b, maxShift, 0.12, 0.885, 0.55, 0.95);
        bool Ok(Shift2D r) => r.Score >= 0.90 && (r.Score - r.SecondScore) >= 0.05 && r.Dx == 0;
        if (!Ok(res) || !Ok(r2) || Math.Abs(res.Dy - r2.Dy) > 4) return false;
        res.Note = $"{res.Note} 双带松({res.Dy}/{r2.Dy} @{res.BandY}/{r2.BandY})".Trim();
        return true;
    }

    /// <summary>
    /// 对账用：锚点到当前的整轮位移可能比一屏还大，上半带的内容会整体滚出画面，
    /// 所以"两块带都必须成立"太苛刻。这里下带优先（内容上移时它还在画面里），
    /// 单带成立要求更高的分差（0.08）以防准周期文本错位。
    /// </summary>
    public static bool MatchAnyBandLoose(Img a, Img b, int maxShift, out Shift2D res)
    {
        var low = MatchBand(a, b, maxShift, 0.12, 0.885, 0.55, 0.95);
        var up = MatchBand(a, b, maxShift, 0.12, 0.885, 0.15, 0.55);
        res = low;
        bool Ok(Shift2D r) => r.Score >= 0.90 && (r.Score - r.SecondScore) >= 0.08 && r.Dx == 0;
        var okLow = Ok(low); var okUp = Ok(up);
        if (okLow && okUp && Math.Abs(low.Dy - up.Dy) <= 4)
        {
            res = low;
            res.Note = $"{low.Note} 双带({low.Dy}/{up.Dy})".Trim();
            return true;
        }
        if (okLow) { res = low; res.Note = $"{low.Note} 下带({low.Dy})".Trim(); return true; }
        if (okUp) { res = up; res.Note = $"{up.Note} 上带({up.Dy})".Trim(); return true; }
        return false;
    }

    /// <summary>
    /// 同时评估两个假设：页面滚了 <paramref name="dy"/> 与没滚（dy=0）。
    /// 两个假设各自用"自己最合适的带"（dy 大时能同时放下的带只剩页顶空白，会误判），
    /// 但都用同一口径（全分辨率归一化 SAD 的 1-x），所以分数仍然可比。
    /// 返回 false 表示找不到带，或者带里几乎没有内容（白对白，任何位移都像）。
    /// </summary>
    public static bool ScorePair(Img a, Img b, int dy, out double sMove, out double sStay,
                                 double x0Frac = 0.12, double x1Frac = 0.885,
                                 double y0Frac = 0.14, double y1Frac = 0.93)
    {
        sMove = sStay = 0;
        if (a.Bgra == null || b.Bgra == null || a.W != b.W || a.H != b.H) return false;

        const int F = 4;
        var gb = Downscale(b, F);
        var bandHQ = Math.Max(8, gb.H / 6);
        var x0 = Math.Max(0, (int)(gb.W * x0Frac));
        var x1 = Math.Min(gb.W, (int)(gb.W * x1Frac));
        var yLo = Math.Max(0, (int)(gb.H * y0Frac));
        var yHi = Math.Min(gb.H, (int)(gb.H * y1Frac));
        var px = F;

        // 在满足 ok(y) 的带里挑暗像素最多的那条（文字最多 = 最好用的参照）
        int FindBand(Func<int, bool> ok, out double dark)
        {
            dark = -1; var best = -1;
            for (var y = yLo; y + bandHQ <= yHi; y += 2)
            {
                if (!ok(y)) continue;
                double s = 0;
                for (var yy = y; yy < y + bandHQ; yy++)
                    for (var x = x0; x < x1; x += 2)
                        if (gb.Px[yy * gb.W + x] < 170) s++;
                if (s > dark) { dark = s; best = y; }
            }
            return best;
        }

        var bStay = FindBand(_ => true, out var darkStay);
        var bMove = FindBand(y => y * px + dy >= 0 && y * px + dy + bandHQ * px <= a.H, out var darkMove);
        if (bStay < 0 || bMove < 0) return false;

        var sampled = (double)bandHQ * ((x1 - x0) / 2.0);
        // 整条带几乎都是白纸时，任何位移都对得上（白对白），必须拒答
        if (sampled > 0 && Math.Min(darkStay, darkMove) / sampled < 0.03) return false;

        double Sad(int bandQ, int shift)
        {
            var bandY = bandQ * px;
            var bandH = bandHQ * px;
            var ay = bandY + shift;
            if (ay < 0 || ay + bandH > a.H) return double.MaxValue;
            double sum = 0; long n = 0;
            for (var y = 0; y < bandH; y += 2)
            {
                var offA = ((ay + y) * a.W) * 4;
                var offB = ((bandY + y) * b.W) * 4;
                for (var x = x0 * px; x < x1 * px; x += 2)
                {
                    sum += Math.Abs(Lum(a.Bgra, offA + x * 4) - Lum(b.Bgra, offB + x * 4));
                    n++;
                }
            }
            return n == 0 ? double.MaxValue : sum / (n * 255.0);
        }

        sMove = 1.0 - Sad(bMove, dy);
        sStay = 1.0 - Sad(bStay, 0);
        return true;
    }

    /// <summary>
    /// 只算"指定 dy"下的相似度（1 - 归一化 SAD），不搜索。
    /// 控制模式用它做二选一复核：页面要么按预期滚了 expected，要么到边没滚。
    /// </summary>
    public static double ScoreAt(Img a, Img b, int dy, double x0Frac = 0.12, double x1Frac = 0.885,
                                 double y0Frac = 0.14, double y1Frac = 0.93)
    {
        if (a.Bgra == null || b.Bgra == null || a.W != b.W || a.H != b.H) return 0;

        const int F = 4;
        var gb = Downscale(b, F);
        var bandHQ = Math.Max(8, gb.H / 6);
        var x0 = Math.Max(0, (int)(gb.W * x0Frac));
        var x1 = Math.Min(gb.W, (int)(gb.W * x1Frac));
        var yLo = Math.Max(0, (int)(gb.H * y0Frac));
        var yHi = Math.Min(gb.H, (int)(gb.H * y1Frac));

        double bestDark = -1; var bandQ = yLo;
        for (var y = yLo; y + bandHQ <= yHi; y += 2)
        {
            // 带必须在 a 帧里两个假设都放得下：dy=0 与 dy 各自对应的行区间都不能越界
            if (y * F + dy + bandHQ * F > a.H || y * F + dy < 0) continue;
            double s = 0;
            for (var yy = y; yy < y + bandHQ; yy++)
                for (var x = x0; x < x1; x += 2)
                    if (gb.Px[yy * gb.W + x] < 170) s++;
            if (s > bestDark) { bestDark = s; bandQ = y; }
        }
        if (bestDark <= 0) return 0;

        var bandY = bandQ * F;
        var bandH = bandHQ * F;
        var ay = bandY + dy;
        if (ay < 0 || ay + bandH > a.H) return 0;

        double sum = 0; long n = 0;
        for (var y = 0; y < bandH; y += 2)
        {
            var offA = ((ay + y) * a.W) * 4;
            var offB = ((bandY + y) * b.W) * 4;
            for (var x = x0 * F; x < x1 * F; x += 2)
            {
                sum += Math.Abs(Lum(a.Bgra, offA + x * 4) - Lum(b.Bgra, offB + x * 4));
                n++;
            }
        }
        return n == 0 ? 0 : 1.0 - sum / (n * 255.0);
    }

    /// <summary>整幅灰度降采样（F×F 块取均值），粗搜索用。</summary>
    public static Gray Downscale(Img img, int f)
    {
        var w = img.W / f; var h = img.H / f;
        var g = new Gray { W = w, H = h, Px = new byte[w * h] };
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < f; dy++)
                {
                    var row = ((y * f + dy) * img.W + x * f) * 4;
                    for (var dx = 0; dx < f; dx++)
                    {
                        var i = row + dx * 4;
                        sum += (int)(0.114 * img.Bgra[i] + 0.587 * img.Bgra[i + 1] + 0.299 * img.Bgra[i + 2]);
                    }
                }
                g.Px[y * w + x] = (byte)(sum / (f * f));
            }
        }
        return g;
    }

    public sealed class Gray { public int W, H; public byte[] Px; }

    static double Lum(byte[] bgra, int i) =>
        0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];

    public sealed class Shift2D
    {
        public int Dy, Dx;
        public double Score, SecondScore, DxScore, DxAtZero;
        public int SecondDy;
        public int BandY, BandH;
        public string Note = "";
        public override string ToString() =>
            $"dy={Dy} 相似度={Score:0.000} (次优 dy={SecondDy} {SecondScore:0.000})  dx={Dx} 相似度={DxScore:0.000} (dx=0 时 {DxAtZero:0.000})  带 y={BandY} h={BandH}{Note}";
    }

    /// <summary>两帧差异像素比例（判断"到底动没动"）。</summary>
    public static double DiffRatio(Img a, Img b)
    {
        if (a.Bgra == null || b.Bgra == null || a.Bgra.Length != b.Bgra.Length) return 1;
        long diff = 0, n = 0;
        for (var i = 0; i < a.Bgra.Length; i += 4 * 16)
        {
            n++;
            if (Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) > 12) diff++;
        }
        return n == 0 ? 1 : (double)diff / n;
    }

    public static Img Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var off = BitConverter.ToInt32(bytes, 10);
        var w = BitConverter.ToInt32(bytes, 18);
        var h0 = BitConverter.ToInt32(bytes, 22);
        var bpp = BitConverter.ToInt16(bytes, 28);
        if (bpp != 32) return null;
        var h = Math.Abs(h0);
        var img = new Img { W = w, H = h, Bgra = new byte[w * h * 4] };
        Buffer.BlockCopy(bytes, off, img.Bgra, 0, img.Bgra.Length);
        if (h0 > 0)      // bottom-up：翻成 top-down
        {
            var row = w * 4;
            var tmp = new byte[row];
            for (int y = 0; y < h / 2; y++)
            {
                Buffer.BlockCopy(img.Bgra, y * row, tmp, 0, row);
                Buffer.BlockCopy(img.Bgra, (h - 1 - y) * row, img.Bgra, y * row, row);
                Buffer.BlockCopy(tmp, 0, img.Bgra, (h - 1 - y) * row, row);
            }
        }
        return img;
    }

    public static void Save(Img img, string path)
    {
        // 无第三方库：写 BMP（32bpp 无需调色板），够肉眼核查
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        var size = 54 + img.Bgra.Length;
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(size); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(img.W); bw.Write(-img.H); bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(img.Bgra.Length); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(img.Bgra);
    }
}
