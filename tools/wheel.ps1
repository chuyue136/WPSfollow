param([int]$Notch = 3, [int]$X = 960, [int]$Y = 516, [string]$Out = "wheel.png")
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace W -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, System.UIntPtr e);
'@
[W.N]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds 150
# MOUSEEVENTF_WHEEL = 0x0800, 每格 120
[W.N]::mouse_event(0x0800, 0, 0, -120 * $Notch, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 400
$bmp = New-Object System.Drawing.Bitmap 1920, 1080
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"滚轮 $Notch 格已发出（真实输入，会走全局钩子），截图 $Out"
