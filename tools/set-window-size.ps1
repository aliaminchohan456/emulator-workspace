# Resizes a top-level window to the requested outer size, given in DIP at the
# supplied DPI scale (defaults to the 125% scaling of this host), so the design
# grid sizes from the plan can be checked on a scaled display.
# Usage: powershell -NoProfile -File tools/set-window-size.ps1 -Title "Emulator Workspace" -Width 980 -Height 620 [-Scale 1.25] [-Left 100] [-Top 100]
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][int]$Width,
    [Parameter(Mandatory = $true)][int]$Height,
    [double]$Scale = 1.25,
    [int]$Left = -1,
    [int]$Top = -1,
    [string]$ProcessName = ""
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WinSize {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

# Physical-pixel coordinates on scaled displays.
[void][WinSize]::SetThreadDpiAwarenessContext([IntPtr](-4))

$handle = [IntPtr]::Zero
if ($ProcessName.Length -gt 0) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($proc) { $handle = $proc.MainWindowHandle }
}
if ($handle -eq [IntPtr]::Zero) {
    $handle = [WinSize]::FindWindowW($null, $Title)
}
if ($handle -eq [IntPtr]::Zero) {
    Write-Error "No top-level window found with title '$Title'."
    exit 1
}

$rect = New-Object WinSize+RECT
[void][WinSize]::GetWindowRect($handle, [ref]$rect)

$x = if ($Left -ge 0) { $Left } else { $rect.Left }
$y = if ($Top -ge 0) { $Top } else { $rect.Top }
$w = [int][Math]::Round($Width * $Scale)
$h = [int][Math]::Round($Height * $Scale)

if (-not [WinSize]::MoveWindow($handle, $x, $y, $w, $h, $true)) {
    Write-Error "MoveWindow failed for '$Title'."
    exit 1
}

[void][WinSize]::GetWindowRect($handle, [ref]$rect)
$actualW = $rect.Right - $rect.Left
$actualH = $rect.Bottom - $rect.Top
Write-Output "moved '$Title' to ${Width}x${Height} DIP (scale $Scale) -> physical ${actualW}x${actualH} at ($($rect.Left),$($rect.Top))"
