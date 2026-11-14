# Activates a top-level window and sends N Tab keystrokes so keyboard focus
# traversal can be captured (PrintWindow renders in-window focus rings).
# Usage: powershell -NoProfile -File tools/focus-cycle.ps1 -Title "Emulator Workspace" -Count 5
# Or send a raw key sequence instead of Tabs: -Keys "^k" (Ctrl+K), "{ESC}" ...
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [int]$Count = 0,
    [string]$Keys = "",
    [string]$ProcessName = ""
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WinFocus {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
"@

[void][WinFocus]::SetThreadDpiAwarenessContext([IntPtr](-4))

$handle = [IntPtr]::Zero
if ($ProcessName.Length -gt 0) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($proc) { $handle = $proc.MainWindowHandle }
}
if ($handle -eq [IntPtr]::Zero) {
    $handle = [WinFocus]::FindWindowW($null, $Title)
}
if ($handle -eq [IntPtr]::Zero) {
    Write-Error "No top-level window found with title '$Title'."
    exit 1
}

[void][WinFocus]::SetForegroundWindow($handle)
Start-Sleep -Milliseconds 300

$shell = New-Object -ComObject WScript.Shell
if ($Keys.Length -gt 0) {
    [void]$shell.SendKeys($Keys)
    Start-Sleep -Milliseconds 200
    Write-Output "sent keys '$Keys' to '$Title'"
    exit 0
}

for ($i = 0; $i -lt $Count; $i++) {
    [void]$shell.SendKeys("{TAB}")
    Start-Sleep -Milliseconds 120
}

Write-Output "sent $Count Tab keystrokes to '$Title'"
