# Summarize the ICC CE plugin log so a drift report can be diagnosed without screenshots.
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\diag.ps1
#         powershell ... -File .\tools\diag.ps1 -Tail 400 -Path "C:\...\2026-10-17.log"
param(
    [string]$Path = "$env:LOCALAPPDATA\Programs\InkCanvasForClass CE\PluginLogs\com.yg.wpsinksync",
    [int]$Tail = 3000
)
$ErrorActionPreference = 'Stop'
if (Test-Path -PathType Container $Path) {
    $f = Get-ChildItem $Path -Filter '*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $f) { "no log file in $Path"; exit 1 }
    $Path = $f.FullName
}
if (-not (Test-Path $Path)) { "log not found: $Path"; exit 1 }
$lines = Get-Content $Path -Encoding UTF8 -Tail $Tail
"log: $Path"
"lines: $($lines.Count)  span: $(($lines | Select-Object -First 1) -replace '^\[|\]\s*\[INFO\].*','') .. $(($lines | Select-Object -Last 1) -replace '^\[|\]\s*\[INFO\].*','')"
""
"press       : $(($lines | Where-Object { $_ -match 'press #' }).Count)"
"  failed    : $(($lines | Where-Object { $_ -match 'press #.*失败' }).Count)"
"verdict 确认 : $(($lines | Where-Object { $_ -match '复核确认滚动' }).Count)"
"verdict 没滚 : $(($lines | Where-Object { $_ -match '复核判定没滚' }).Count)"
"verdict 分不开: $(($lines | Where-Object { $_ -match '复核分不开' }).Count)"
"批量信命令   : $(($lines | Where-Object { $_ -match '大批量复核没匹配上' }).Count)"
"到边撤回     : $(($lines | Where-Object { $_ -match '页面没动' }).Count)"
""
"--- 可疑行（补差大 / 越界 / JUMP / 每格变化 / 异常）---"
($lines | Where-Object { $_ -match 'JUMP|越界|补差过大|每格 .*→|follower:|异常|失败' } | Select-Object -Last 25)
""
"--- 最近一次心跳 ---"
($lines | Where-Object { $_ -match '心跳' } | Select-Object -Last 3)
""
"--- 最后 15 行 ---"
$lines | Select-Object -Last 15
