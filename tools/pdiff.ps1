param([string]$A, [string]$B)
# Pixel diff of two PNG screenshots over the WPS document area. ASCII-only on purpose.
Add-Type -AssemblyName System.Drawing
$pa = (Resolve-Path $A).Path
$pb = (Resolve-Path $B).Path
$ba = New-Object System.Drawing.Bitmap 1,1
$bb = New-Object System.Drawing.Bitmap 1,1
$ba.Dispose(); $bb.Dispose()
$fa = New-Object System.IO.FileStream $pa, ([System.IO.FileMode]::Open), ([System.IO.FileAccess]::Read)
$fb = New-Object System.IO.FileStream $pb, ([System.IO.FileMode]::Open), ([System.IO.FileAccess]::Read)
$ia = [System.Drawing.Image]::FromStream($fa)
$ib = [System.Drawing.Image]::FromStream($fb)
$ga = New-Object System.Drawing.Bitmap $ia
$gb = New-Object System.Drawing.Bitmap $ib
$ia.Dispose(); $ib.Dispose(); $fa.Dispose(); $fb.Dispose()
$n = 0; $d = 0
for ($y = 150; $y -lt 940; $y += 4) {
  for ($x = 80; $x -lt 1870; $x += 4) {
    $n++
    if ($ga.GetPixel($x, $y).ToArgb() -ne $gb.GetPixel($x, $y).ToArgb()) { $d++ }
  }
}
$ga.Dispose(); $gb.Dispose()
"{0} vs {1}: {2} / {3} = {4}%" -f $A, $B, $d, $n, [math]::Round(100.0 * $d / $n, 3)
