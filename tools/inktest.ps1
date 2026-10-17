param([string]$Out = "ink.png")
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace W -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, System.UIntPtr e);
'@
function Shot($path) {
  $bmp = New-Object System.Drawing.Bitmap 1920, 1080
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path (Get-Location) $path), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
function Click($x, $y) {
  [W.N]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 120
  [W.N]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # LEFTDOWN
  Start-Sleep -Milliseconds 60
  [W.N]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # LEFTUP
  Start-Sleep -Milliseconds 300
}
# 1) 点 ICC 工具条的「打样」注入合成笔迹
Click 1211 1013
Start-Sleep -Milliseconds 500
Shot "inkA.png"
# 2) 真实滚轮 3 格（会走全局钩子 → 高频态）
[W.N]::SetCursorPos(960, 516) | Out-Null
Start-Sleep -Milliseconds 150
[W.N]::mouse_event(0x0800, 0, 0, -360, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 700
Shot $Out
"已注入笔迹并滚动，inkA.png / $Out"
