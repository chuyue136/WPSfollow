param([int]$Notch = 3)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace W -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, System.UIntPtr e);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
'@
function Shot($path) {
  $bmp = New-Object System.Drawing.Bitmap 1920, 1080
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path (Get-Location) $path), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
# Ctrl+Alt+K = 打样（注入 3 条合成笔迹）
[W.N]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)      # Ctrl down
[W.N]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)      # Alt down
Start-Sleep -Milliseconds 60
[W.N]::keybd_event(0x4B, 0, 0, [UIntPtr]::Zero)      # K down
[W.N]::keybd_event(0x4B, 0, 2, [UIntPtr]::Zero)      # K up (KEYEVENTF_KEYUP)
Start-Sleep -Milliseconds 60
[W.N]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
[W.N]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 700
Shot "ink1.png"
# 真实滚轮
[W.N]::SetCursorPos(960, 516) | Out-Null
Start-Sleep -Milliseconds 150
[W.N]::mouse_event(0x0800, 0, 0, -120 * $Notch, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 900
Shot "ink2.png"
"打样+滚轮$Notch格完成：ink1.png（滚前）/ ink2.png（滚后）"
