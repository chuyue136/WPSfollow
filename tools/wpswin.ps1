# 枚举 WPS 的顶层/子窗口：类名、标题、矩形、进程，用来判断"当前活动标签是什么文档类型"
# 用法: powershell -File tools\wpswin.ps1            （顶层 + 文档相关子窗口）
#       powershell -File tools\wpswin.ps1 -Full      （所有子窗口）
param([switch]$Full)

if (-not ('WEnum' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WEnum {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public struct RECT { public int L, T, R, B; }
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
  public static string Txt(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
  public static string Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return string.Format("{0}x{1}@({2},{3})", r.R-r.L, r.B-r.T, r.L, r.T); }
  public static List<IntPtr> Tops() { var l = new List<IntPtr>(); EnumWindows(delegate(IntPtr h, IntPtr x) { l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static List<IntPtr> Kids(IntPtr h) { var l = new List<IntPtr>(); EnumChildWindows(h, delegate(IntPtr c, IntPtr x) { l.Add(c); return true; }, IntPtr.Zero); return l; }
}
'@
}

$tops = [WEnum]::Tops() | Where-Object {
  $pid2 = 0; [void][WEnum]::GetWindowThreadProcessId($_, [ref]$pid2)
  $p = Get-Process -Id $pid2 -ErrorAction SilentlyContinue
  $p -and ($p.ProcessName -like 'wps*') -and [WEnum]::IsWindowVisible($_)
}

foreach ($t in $tops) {
  $cls = [WEnum]::Cls($t)
  $tit = [WEnum]::Txt($t)
  $rect = [WEnum]::Rect($t)
  $pid2 = 0; [void][WEnum]::GetWindowThreadProcessId($t, [ref]$pid2)
  $pn = (Get-Process -Id $pid2).ProcessName
  "TOP 0x{0:X} [{1}] '{2}' {3} pid={4}/{5}" -f [int64]$t, $cls, $tit, $rect, $pid2, $pn
  foreach ($c in [WEnum]::Kids($t)) {
    $cc = [WEnum]::Cls($c)
    $ct = [WEnum]::Txt($c)
    $cr = [WEnum]::Rect($c)
    $vis = [WEnum]::IsWindowVisible($c)
    if (-not $Full) {
      if ($cc -notmatch '^(Qt5|_WwG|OpusApp|paneClassDC|K[tT]|ScrollBar)') { continue }
      if (-not $vis) { continue }
    }
    "   kid 0x{0:X} [{1}] '{2}' {3} vis={4}" -f [int64]$c, $cc, $ct, $cr, $vis
  }
}
