# Drags the mouse from a window-relative physical-pixel offset by a delta,
# using SendInput (for testing custom grid header grippers and sliders).
# Usage: powershell -NoProfile -File tools/drag-at.ps1 -ProcessName "EmulatorWorkspace.Desktop" -OffsetX 135 -OffsetY 753 -DeltaX 100
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [int]$OffsetX,
    [int]$OffsetY,
    [int]$DeltaX = 0,
    [int]$DeltaY = 0,
    [string]$ProcessName = ""
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WinDrag {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT {
        public uint type;
        public MOUSEINPUT mi;
    }

    public static void SendMouse(uint flags) {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags } };
        SendInput(1, ref input, Marshal.SizeOf(typeof(INPUT)));
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, ref INPUT input, int size);
}
"@

# Physical-pixel coordinates on scaled displays.
[void][WinDrag]::SetThreadDpiAwarenessContext([IntPtr](-4))

$handle = [IntPtr]::Zero
if ($ProcessName.Length -gt 0) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($proc) { $handle = $proc.MainWindowHandle }
}
if ($handle -eq [IntPtr]::Zero) {
    $handle = [WinDrag]::FindWindowW($null, $Title)
}
if ($handle -eq [IntPtr]::Zero) {
    Write-Error "No top-level window found with title '$Title'."
    exit 1
}

$rect = New-Object WinDrag+RECT
[void][WinDrag]::GetWindowRect($handle, [ref]$rect)
[void][WinDrag]::SetForegroundWindow($handle)
Start-Sleep -Milliseconds 300

$startX = $rect.Left + $OffsetX
$startY = $rect.Top + $OffsetY

[void][WinDrag]::SetCursorPos($startX, $startY)
Start-Sleep -Milliseconds 150

# LEFT_DOWN = 2, MOVE = 1, LEFT_UP = 4.
[void][WinDrag]::SendMouse(2)
Start-Sleep -Milliseconds 120

$steps = [Math]::Max([Math]::Abs($DeltaX), [Math]::Abs($DeltaY))
if ($steps -lt 1) { $steps = 1 }
for ($i = 1; $i -le $steps; $i++) {
    $x = $startX + [int][Math]::Round($DeltaX * $i / $steps)
    $y = $startY + [int][Math]::Round($DeltaY * $i / $steps)
    [void][WinDrag]::SetCursorPos($x, $y)
    [void][WinDrag]::SendMouse(1)
    Start-Sleep -Milliseconds 10
}

Start-Sleep -Milliseconds 120
[void][WinDrag]::SendMouse(4)
Write-Output "dragged from ($startX,$startY) by ($DeltaX,$DeltaY)"
