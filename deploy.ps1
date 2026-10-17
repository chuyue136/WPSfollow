# 编译 → 部署 → 重启 ICC → 校验热键 → 打印插件日志
# 用法: .\deploy.ps1            编译并部署，重启 ICC 后等 15s 打印日志
#       .\deploy.ps1 -NoRun     只编译并部署，不动 ICC
#       .\deploy.ps1 -Test      部署后顺手跑一遍连按压测（tools\test-reconcile.ps1）
param([switch]$NoRun, [switch]$Test)

$ErrorActionPreference = 'Stop'
$app = 'C:\Users\Admin\AppData\Local\Programs\InkCanvasForClass CE'
$id  = 'com.yg.wpsinksync'
$dst = Join-Path $app "Plugins\$id"
$bin = Join-Path $PSScriptRoot 'WpsInkSync\bin\Debug\net10.0-windows10.0.19041.0'
$logDir = Join-Path $app "PluginLogs\$id"

dotnet build (Join-Path $PSScriptRoot 'WpsInkSync\WpsInkSync.csproj') -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

# ICC 会自拉一个子实例，杀一轮可能还剩一个；而提权实例可能杀不掉
for ($i = 0; $i -lt 6; $i++) {
    $p = Get-Process InkCanvasForClass -ErrorAction SilentlyContinue
    if (-not $p) { break }
    $p | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700
}
if (Get-Process InkCanvasForClass -ErrorAction SilentlyContinue) {
    Write-Warning 'ICC 还在跑（提权实例杀不掉），请手动关一下再重跑'
}

New-Item -ItemType Directory -Force -Path $dst | Out-Null
foreach ($f in 'manifest.json', 'WpsInkSync.dll', 'WpsInkSync.deps.json', 'WpsInkSync.runtimeconfig.json') {
    Copy-Item (Join-Path $bin $f) $dst -Force
}

if ($NoRun) { Write-Host "已部署到 $dst"; return }

Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue
Start-Process -FilePath (Join-Path $app 'InkCanvasForClass.exe') -WorkingDirectory $app
Start-Sleep -Seconds 15

$log = Join-Path $logDir ((Get-Date).ToString('yyyy-MM-dd') + '.log')
if (Test-Path $log) {
    $lines = Get-Content $log -Tail 80
    # 热键注册失败会让整个插件"按了没反应"，这里明确报出来，不用每次翻日志
    $bad = @($lines | Select-String 'hotkey .*= False')
    if ($bad.Count -gt 0) {
        Write-Warning "有 $($bad.Count) 个热键没注册上（多半是 ICC 跑了两个实例）"
        $bad | ForEach-Object { Write-Warning $_.Line }
    } else {
        Write-Host '热键全部注册成功' -ForegroundColor Green
    }
    $lines | ForEach-Object { Write-Host $_ }
}

if ($Test) {
    & (Join-Path $PSScriptRoot 'tools\test-reconcile.ps1')
}
