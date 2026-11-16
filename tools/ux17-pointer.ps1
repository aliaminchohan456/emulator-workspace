# UX-17 evidence helper: real pointer input on a window's caption button, so
# hover, pressed and the Windows 11 Snap Layouts flyout can be captured.
#
# Usage:
#   powershell -NoProfile -File tools/ux17-pointer.ps1 -Element Maximize -Hover
#   powershell -NoProfile -File tools/ux17-pointer.ps1 -Element Close -Press
#   powershell -NoProfile -File tools/ux17-pointer.ps1 -Element Close -Release
#   powershell -NoProfile -File tools/ux17-pointer.ps1 -Away -Release
#
# -Hover parks the cursor on the button centre; -Press holds the left button down
# (the caller captures, then -Release or -Away releases it somewhere harmless so a
# press on Close never actually closes the window).
param(
    [string]$Element = "Maximize",
    [string]$WindowTitle = "Emulator Workspace",
    [string]$ProcessName = "EmulatorWorkspace.Desktop",
    [switch]$Hover,
    [switch]$Press,
    [switch]$Release,
    [switch]$Away
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class Ux17Pointer {
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public MOUSEINPUT mi; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, ref INPUT input, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    public const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoActivate = 0x0010;
    public static void Topmost(IntPtr hwnd, bool on) { SetWindowPos(hwnd, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate); }
    public static void Mouse(uint flags) {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, ref input, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $proc) { Write-Error "Process '$ProcessName' is not running."; exit 1 }
[void][Ux17Pointer]::SetForegroundWindow($proc.MainWindowHandle)
# UX-17: a terminal/agent window can sit over the app on this host; the caption
# hover and the shell-drawn Snap flyout only exist when the app is really on top.
if ($Hover -or $Press) { [Ux17Pointer]::Topmost($proc.MainWindowHandle, $true) }
if ($Away) { [Ux17Pointer]::Topmost($proc.MainWindowHandle, $false) }
Start-Sleep -Milliseconds 250

$window = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Element)
# A page can carry a same-named control (a drawer's Close); the caption button is
# the match in the title strip, so the top-most rectangle wins.
$matches = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
$button = $null
foreach ($candidate in $matches) {
    if (-not $button -or $candidate.Current.BoundingRectangle.Y -lt $button.Current.BoundingRectangle.Y) {
        $button = $candidate
    }
}
if (-not $button -and $Element -eq "Maximize") {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "Restore")
    $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
if (-not $button) { Write-Error "Element '$Element' not found."; exit 1 }

$r = $button.Current.BoundingRectangle
$x = [int]($r.X + $r.Width / 2)
$y = [int]($r.Y + $r.Height / 2)

if ($Away) {
    [void][Ux17Pointer]::SetCursorPos(4, 600)
    Start-Sleep -Milliseconds 150
}
if ($Hover -or $Press) {
    [void][Ux17Pointer]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 200
}
if ($Press) {
    # 2 = MOUSEEVENTF_LEFTDOWN
    [Ux17Pointer]::Mouse(2)
    Start-Sleep -Milliseconds 150
}
if ($Release) {
    # 4 = MOUSEEVENTF_LEFTUP
    [Ux17Pointer]::Mouse(4)
    Start-Sleep -Milliseconds 150
}
Write-Output ("pointer element=$Element at ($x,$y) hover=$Hover press=$Press release=$Release away=$Away")
