# Send a real keyboard combo to trigger ICC global hotkeys (keybd_event).
# ASCII only: Windows PowerShell 5.1 reads BOM-less files as ANSI, and non-ASCII
# text inside quotes breaks parsing.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\hk.ps1 -Key DOWN -Times 2
param(
    [ValidateSet('UP','DOWN','PGUP','PGDN','K','F','R','M','D')] [string]$Key = 'DOWN',
    [int]$Times = 1,
    [int]$GapMs = 350
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class KB {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
}
"@

$vk = switch ($Key) {
    'UP'   { 0x26 }
    'DOWN' { 0x28 }
    'PGUP' { 0x21 }
    'PGDN' { 0x22 }
    'K'    { 0x4B }
    'F'    { 0x46 }
    'R'    { 0x52 }
    'M'    { 0x4D }
    'D'    { 0x44 }
}
$CTRL = 0x11; $ALT = 0x12; $KEYUP = 0x02

for ($i = 0; $i -lt $Times; $i++) {
    [KB]::keybd_event($CTRL, 0, 0, [IntPtr]::Zero)
    [KB]::keybd_event($ALT, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 30
    [KB]::keybd_event([byte]$vk, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 30
    [KB]::keybd_event([byte]$vk, 0, $KEYUP, [IntPtr]::Zero)
    [KB]::keybd_event($ALT, 0, $KEYUP, [IntPtr]::Zero)
    [KB]::keybd_event($CTRL, 0, $KEYUP, [IntPtr]::Zero)
    Start-Sleep -Milliseconds $GapMs
}
Write-Output ("sent Ctrl+Alt+{0} x{1}" -f $Key, $Times)

