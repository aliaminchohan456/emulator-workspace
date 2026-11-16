# UX-17 part B evidence: measures the three caption buttons and proves the
# HTMAXBUTTON rectangle really covers the maximize button at the current DPI.
#
# Usage: powershell -NoProfile -File tools/ux17-caption-probe.ps1 -Title "Emulator Workspace" -Scale 1.5
#
# The measurement comes from UI Automation (physical pixels), the hit test from
# a real WM_NCHITTEST sent to the window, which is exactly the message Windows 11
# uses to decide whether to show the Snap Layouts flyout. On Windows 10 the hook
# is not installed, so HTMAXBUTTON is not expected there.
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][double]$Scale,
    [string]$ProcessName = "EmulatorWorkspace.Desktop"
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class CaptionProbe {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    // GetProcess().MainWindowHandle can resolve to a tooltip popup that happens
    // to be visible, so the shell window is matched by its exact caption.
    public static IntPtr FindByTitle(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner != pid) { return true; }
            var sb = new System.Text.StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static int HitTest(IntPtr hwnd, int x, int y) {
        var packed = ((IntPtr)((y << 16) | (x & 0xFFFF)));
        return SendMessage(hwnd, 0x0084, IntPtr.Zero, packed).ToInt32();
    }
}
"@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $proc) { Write-Error "Process '$ProcessName' is not running."; exit 1 }

$handle = [CaptionProbe]::FindByTitle([uint32]$proc.Id, $Title)
if ($handle -eq [IntPtr]::Zero) { $handle = $proc.MainWindowHandle }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
if (-not $window) { Write-Error "No window for '$ProcessName'."; exit 1 }

function Find-Named([string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

$min = Find-Named "Minimize"
$max = Find-Named "Maximize"
if (-not $max) { $max = Find-Named "Restore" }
$close = Find-Named "Close"
if (-not $min -or -not $max -or -not $close) {
    Write-Error "Could not find all three caption buttons (Minimize/Maximize/Close)."
    exit 1
}

function Dip([double]$pixels) { return [Math]::Round($pixels / $Scale, 1) }

foreach ($pair in @(@("Minimize", $min), @("Maximize", $max), @("Close", $close))) {
    $rect = $pair[1].Current.BoundingRectangle
    Write-Output ("{0,-9} physical {1}x{2} at ({3},{4})  = {5} x {6} DIP" -f `
        $pair[0], [int]$rect.Width, [int]$rect.Height, [int]$rect.X, [int]$rect.Y, (Dip $rect.Width), (Dip $rect.Height))
}

# The maximize button must answer HTMAXBUTTON (9) across its whole rectangle and
# no further: four points 2 px inside each corner and the centre, then 3 px
# outside the left and right edges (the caption gap, which is drag surface).
$rect = $max.Current.BoundingRectangle
$rx = [int]$rect.X
$ry = [int]$rect.Y
$rw = [int]$rect.Width
$rh = [int]$rect.Height
$midX = $rx + [int]($rw / 2)
$midY = $ry + [int]($rh / 2)
$points = @(
    ,@(($rx + 2), ($ry + 2))
    ,@(($rx + $rw - 3), ($ry + 2))
    ,@(($rx + 2), ($ry + $rh - 3))
    ,@(($rx + $rw - 3), ($ry + $rh - 3))
    ,@($midX, $midY)
)

$allMax = $true
foreach ($p in $points) {
    $hit = [CaptionProbe]::HitTest($handle, [int]$p[0], [int]$p[1])
    if ($hit -ne 9) { $allMax = $false }
    Write-Output ("hit test inside max button at ({0},{1}) -> {2}" -f $p[0], $p[1], $hit)
}

$outsideLeft = [CaptionProbe]::HitTest($handle, ($rx - 3), $midY)
$outsideRight = [CaptionProbe]::HitTest($handle, ($rx + $rw + 3), $midY)
Write-Output ("hit test left of max button  -> {0}" -f $outsideLeft)
Write-Output ("hit test right of max button -> {0}" -f $outsideRight)

if ($allMax -and $outsideLeft -ne 9 -and $outsideRight -ne 9) {
    Write-Output "HTMAXBUTTON aligns with the maximize button"
    exit 0
}

Write-Output "HTMAXBUTTON does NOT align with the maximize button"
exit 2
