# 在物理屏幕坐标上点一下（左键）。用法: powershell -File tools\click.ps1 -X 738 -Y 133
param([int]$X, [int]$Y)

if (-not ('MouseIn' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MouseIn {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // left down
        System.Threading.Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // left up
    }
}
'@
}

[MouseIn]::Click($X, $Y)
Write-Output ("clicked {0},{1}" -f $X, $Y)
