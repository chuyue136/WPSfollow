using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class WEnum
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public struct RECT { public int L, T, R, B; }

    public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static string Txt(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
    public static string Rect(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        return string.Format("{0}x{1}@({2},{3})", r.R - r.L, r.B - r.T, r.L, r.T);
    }
    public static List<IntPtr> Tops()
    {
        var l = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr x) { l.Add(h); return true; }, IntPtr.Zero);
        return l;
    }
    public static List<IntPtr> Kids(IntPtr h)
    {
        var l = new List<IntPtr>();
        EnumChildWindows(h, delegate(IntPtr c, IntPtr x) { l.Add(c); return true; }, IntPtr.Zero);
        return l;
    }
}
