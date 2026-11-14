# Captures a top-level window by exact title as a PNG (PrintWindow, render-full-content).
# Usage: powershell -NoProfile -File tools/capture-window.ps1 -Title "Emulator Workspace" -Out shot.png
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ProcessName = ""
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WinShot {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

Add-Type -AssemblyName System.Drawing

# Make coordinate APIs return physical pixels on scaled displays.
[void][WinShot]::SetThreadDpiAwarenessContext([IntPtr](-4))

$handle = [IntPtr]::Zero
if ($ProcessName.Length -gt 0) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($proc) { $handle = $proc.MainWindowHandle }
}
if ($handle -eq [IntPtr]::Zero) {
    $handle = [WinShot]::FindWindowW($null, $Title)
}
if ($handle -eq [IntPtr]::Zero) {
    Write-Error "No top-level window found with title '$Title'."
    exit 1
}

$rect = New-Object WinShot+RECT
if (-not [WinShot]::GetWindowRect($handle, [ref]$rect)) {
    Write-Error "GetWindowRect failed for '$Title'."
    exit 1
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    Write-Error "Window '$Title' has no visible area."
    exit 1
}

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
try {
    $captured = [WinShot]::PrintWindow($handle, $hdc, 2) # PW_RENDERFULLCONTENT
}
finally {
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
}
if (-not $captured) {
    $bitmap.Dispose()
    Write-Error "PrintWindow failed for '$Title'."
    exit 1
}

$directory = Split-Path -Parent $Out
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Output "saved $Out (${width}x${height})"
