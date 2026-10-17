param(
  [string]$Pick = '',          # settings list item caption, e.g. 'WPS 墨迹同步 设置'
  [string]$Shot = '',          # screenshot path
  [switch]$Open                # open the settings window first (tools -> settings)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

function Find-Window([string]$cls, [string]$titlePart) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ClassNameProperty, $cls)
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    if (-not $titlePart -or $w.Current.Name -like "*$titlePart*") { return $w }
  }
  return $null
}

function Click-Element($e) {
  $r = $e.Current.BoundingRectangle
  $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
  [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
  Start-Sleep -Milliseconds 120
  Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,int e);'
  [W.U]::mouse_event(0x0002, 0, 0, 0, 0); Start-Sleep -Milliseconds 40
  [W.U]::mouse_event(0x0004, 0, 0, 0, 0)
  return "$x,$y"
}

function Shot([string]$path) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}

$icc = Find-Window 'InkCanvasForClassMainWindow' ''
if (-not $icc) { $icc = Find-Window 'HwndWrapper[InkCanvasForClass.exe;;' '' }
if (-not $icc) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $procCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    (Get-Process InkCanvasForClass | Sort-Object StartTime | Select-Object -Last 1).Id)
  $icc = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
}
if (-not $icc) { throw 'ICC window not found' }
Write-Host "ICC window: '$($icc.Current.Name)' class=$($icc.Current.ClassName)"

if ($Open) {
  # toolbar: 工具 (ToolbarImageButton) then 设置
  $btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
  $tools = $null
  foreach ($b in $icc.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    if ($b.Current.Name -eq '工具') { $tools = $b; break }
  }
  if (-not $tools) { throw '工具 button not found' }
  Write-Host "click 工具 @ $(Click-Element $tools)"
  Start-Sleep -Milliseconds 900
  $set = $null
  foreach ($b in $icc.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    if ($b.Current.Name -eq '设置') { $set = $b; break }
  }
  if ($set) { Write-Host "click 设置 @ $(Click-Element $set)" } else { Write-Host '设置 button not found (maybe already open)' }
  Start-Sleep -Milliseconds 1200
}

if ($Pick) {
  $itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
  $hit = $null
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    $items = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
    foreach ($i in $items) {
      if ($i.Current.Name -like "*$Pick*") { $hit = $i; break }
    }
    if ($hit) { Write-Host "settings window: '$($w.Current.Name)'"; break }
  }
  if (-not $hit) { throw "list item '$Pick' not found" }
  $sel = $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sel.Select()
  Start-Sleep -Milliseconds 900
  Write-Host "selected '$($hit.Current.Name)'"
}

if ($Shot) { Shot $Shot; Write-Host "shot -> $Shot" }
