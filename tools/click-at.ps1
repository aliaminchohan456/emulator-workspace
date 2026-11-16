# Clicks a point inside a window, in window-relative physical pixels, with
# SendInput. The offset is measured from the window's top-left corner, so the
# same command works on every scaled display.
#
# Usage:
#   powershell -NoProfile -File tools/click-at.ps1 -Title "Emulator Workspace" -OffsetX 500 -OffsetY 725
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][int]$OffsetX,
    [Parameter(Mandatory = $true)][int]$OffsetY,
    [string]$ProcessName = "",
    [int]$SettleMilliseconds = 250
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WinClick {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
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
[void][WinClick]::SetThreadDpiAwarenessContext([IntPtr](-4))

$handle = [IntPtr]::Zero
if ($ProcessName.Length -gt 0) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($proc) { $handle = $proc.MainWindowHandle }
}
if ($handle -eq [IntPtr]::Zero) {
    $handle = [WinClick]::FindWindowW($null, $Title)
}
if ($handle -eq [IntPtr]::Zero) {
    Write-Error "No top-level window found with title '$Title'."
    exit 1
}

$rect = New-Object WinClick+RECT
[void][WinClick]::GetWindowRect($handle, [ref]$rect)

# A synthetic click only reaches the window that owns the foreground. Windows
# refuses a foreground change from a background process, so the request is made
# while the ALT key is down: that is the documented way to be allowed to set
# the foreground. The check afterwards is the evidence that the click will
# land in the intended window.
[void][WinClick]::ShowWindow($handle, 9)
[void][WinClick]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)
[void][WinClick]::SetForegroundWindow($handle)
[void][WinClick]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
[void][WinClick]::BringWindowToTop($handle)
Start-Sleep -Milliseconds 400

$foreground = [WinClick]::GetForegroundWindow()
if ($foreground -ne $handle) {
    Write-Output "WARNING: the target window is not the foreground window ($foreground); the click may land elsewhere."
}

$x = $rect.Left + $OffsetX
$y = $rect.Top + $OffsetY
[void][WinClick]::SetCursorPos($x, $y)
Start-Sleep -Milliseconds 150

# LEFT_DOWN = 2, LEFT_UP = 4.
[void][WinClick]::SendMouse(2)
Start-Sleep -Milliseconds $SettleMilliseconds
[void][WinClick]::SendMouse(4)
Write-Output "clicked ($x,$y) - window offset ($OffsetX,$OffsetY)"
