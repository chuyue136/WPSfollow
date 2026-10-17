param([int]$Gestures = 3, [int]$Notch = 2)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace W -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, System.UIntPtr e);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, System.UIntPtr e);
'@
function Shot($path) {
  $bmp = New-Object System.Drawing.Bitmap 1920, 1080
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path (Get-Location) $path), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
# 打样
[W.N]::keybd_event(0x11,0,0,[UIntPtr]::Zero); [W.N]::keybd_event(0x12,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[W.N]::keybd_event(0x4B,0,0,[UIntPtr]::Zero); [W.N]::keybd_event(0x4B,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[W.N]::keybd_event(0x12,0,2,[UIntPtr]::Zero); [W.N]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 700
Shot "seq0.png"
[W.N]::SetCursorPos(960, 516) | Out-Null
Start-Sleep -Milliseconds 150
for ($i = 0; $i -lt $Gestures; $i++) {
  [W.N]::mouse_event(0x0800, 0, 0, -120 * $Notch, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 700
}
Shot "seq1.png"
for ($i = 0; $i -lt $Gestures; $i++) {
  [W.N]::mouse_event(0x0800, 0, 0, 120 * $Notch, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 700
}
Shot "seq2.png"
"$Gestures 次上滚 + $Gestures 次下滚完成：seq0/seq1/seq2.png"
