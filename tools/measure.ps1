param([int]$Notch = 3, [switch]$NoScroll)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace W -Name N -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, System.UIntPtr e);
'@
function Shot($path) {
  $bmp = New-Object System.Drawing.Bitmap 1920, 1080
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path (Get-Location) $path), [System.Drawing.Imaging.ImageFormat]::Bmp)
  $g.Dispose(); $bmp.Dispose()
}
Shot "m1.bmp"
if (-not $NoScroll) {
  [W.N]::SetCursorPos(960, 516) | Out-Null
  Start-Sleep -Milliseconds 150
  [W.N]::mouse_event(0x0800, 0, 0, -120 * $Notch, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 1200
}
Shot "m2.bmp"
& '.\tools\ScrollProbe\bin\Debug\net10.0-windows10.0.19041.0\ScrollProbe.exe' cmp m1.bmp m2.bmp 320
