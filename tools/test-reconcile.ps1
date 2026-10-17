# Regression check for the 2026-10-17 reconcile fix.
# A burst of fast presses must NOT produce any "reconcile correction" (the old bug:
# reconcile decided "page moved 0px" before WPS had even started its scroll animation,
# then reverted the ink by one notch -> permanent one-notch misalignment).
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test-reconcile.ps1
param(
    [int]$Times = 6,
    [int]$GapMs = 120
)

$app = Join-Path $env:LOCALAPPDATA 'Programs\InkCanvasForClass CE'
$log = Join-Path $app ("PluginLogs\com.yg.wpsinksync\" + (Get-Date -Format 'yyyy-MM-dd') + '.log')
$hk  = Join-Path $PSScriptRoot 'hk.ps1'

# Chinese markers, built from code points so this file stays pure ASCII.
$pFix = [string][char]0x5BF9 + [char]0x8D26 + [char]0x4FEE + [char]0x6B63   # dui-zhang-xiu-zheng
$pOk  = [string][char]0x5BF9 + [char]0x8D26 + [char]0x786E + [char]0x8BA4   # dui-zhang-que-ren

if (-not (Test-Path $log)) { Write-Output "SKIP: no plugin log at $log"; exit 2 }
if (-not (Get-Process InkCanvasForClass -ErrorAction SilentlyContinue)) { Write-Output 'SKIP: ICC not running'; exit 2 }

# 1) make sure we are not already sitting at the bottom edge: go to the top first.
& powershell -NoProfile -ExecutionPolicy Bypass -File $hk -Key UP -Times 40 -GapMs 90 | Out-Null
Start-Sleep -Seconds 3

$before = @(Get-Content $log -Encoding UTF8).Count

# 2) fast burst down: the cadence that used to break reconcile
& powershell -NoProfile -ExecutionPolicy Bypass -File $hk -Key DOWN -Times $Times -GapMs $GapMs | Out-Null
Start-Sleep -Seconds 4

$new = @(Get-Content $log -Encoding UTF8 | Select-Object -Skip $before)
$fix = @($new | Where-Object { $_ -match $pFix })
$ok  = @($new | Where-Object { $_ -match $pOk })

Write-Output ("new log lines: {0}" -f $new.Count)
Write-Output ("reconcile corrections: {0}" -f $fix.Count)
foreach ($l in $fix) { Write-Output ('  ' + $l) }
foreach ($l in $ok)  { Write-Output ('  0px-confirm: ' + $l) }

if ($new.Count -eq 0) { Write-Output 'SKIP: no new log lines (plugin not logging?)'; exit 2 }
if ($fix.Count -gt 0) {
    Write-Output 'FAIL: reconcile corrected the ink during a fast burst (page was mid-document, nothing should be reverted)'
    exit 1
}
Write-Output 'PASS: no premature reconcile correction'
exit 0
