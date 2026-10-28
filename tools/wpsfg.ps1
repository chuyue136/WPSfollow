# 看"当前前台/激活窗口"的类名与标题，用于判断 WPS 当前活动标签是什么文档类型
param([switch]$All)

Add-Type -Path (Join-Path $PSScriptRoot 'WinEnum.cs')

$fg = [WEnum]::GetForegroundWindow()
$root = [WEnum]::GetAncestor($fg, 2)   # GA_ROOT
$fp = 0; [void][WEnum]::GetWindowThreadProcessId($fg, [ref]$fp)
$fpn = (Get-Process -Id $fp -ErrorAction SilentlyContinue).ProcessName

Write-Output ("FG   0x{0:X} [{1}] '{2}' {3} pid={4}/{5}" -f [int64]$fg, [WEnum]::Cls($fg), [WEnum]::Txt($fg), [WEnum]::Rect($fg), $fp, $fpn)
Write-Output ("ROOT 0x{0:X} [{1}] '{2}' {3}" -f [int64]$root, [WEnum]::Cls($root), [WEnum]::Txt($root), [WEnum]::Rect($root))

Write-Output "PARENT chain:"
$h = $fg
for ($i = 0; $i -lt 6 -and $h -ne [IntPtr]::Zero; $i++) {
    Write-Output ("  L{0} 0x{1:X} [{2}] '{3}' vis={4}" -f $i, [int64]$h, [WEnum]::Cls($h), [WEnum]::Txt($h), [WEnum]::IsWindowVisible($h))
    $h = [WEnum]::GetParent($h)
}

Write-Output "--- visible wps top windows ---"
foreach ($t in [WEnum]::Tops()) {
    $tp = 0; [void][WEnum]::GetWindowThreadProcessId($t, [ref]$tp)
    $proc = Get-Process -Id $tp -ErrorAction SilentlyContinue
    if (-not $proc) { continue }
    if ($proc.ProcessName -notlike 'wps*') { continue }
    if (-not [WEnum]::IsWindowVisible($t)) { continue }
    Write-Output ("W 0x{0:X} [{1}] '{2}' {3} pid={4}" -f [int64]$t, [WEnum]::Cls($t), [WEnum]::Txt($t), [WEnum]::Rect($t), $tp)
    if ($true) {
        foreach ($c in [WEnum]::Kids($t)) {
            Write-Output ("   kid 0x{0:X} [{1}] '{2}' {3} vis={4}" -f [int64]$c, [WEnum]::Cls($c), [WEnum]::Txt($c), [WEnum]::Rect($c), [WEnum]::IsWindowVisible($c))
        }
    }
}
