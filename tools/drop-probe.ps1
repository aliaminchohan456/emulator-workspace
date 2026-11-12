#requires -Version 5.1
<#
.SYNOPSIS
    Focused probe of the WPF file drop: it starts the drag source, presses the
    handle, moves the pointer over the desktop window and releases.

.DESCRIPTION
    The probe isolates the drop so the drag can be debugged without the whole
    click-through. It reports whether the desktop window saw the drag and
    whether the drop arrived.
#>
param(
    [string]$Root = "D:\Phase7Root",
    [string]$Apk = ""
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$desktop = Join-Path $repo "src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe"
if (-not $Apk) {
    $Apk = (Get-ChildItem (Join-Path $repo "artifacts") -Recurse -Filter "ws-sample.apk" -ErrorAction SilentlyContinue |
        Select-Object -First 1).FullName
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ("DropProbe.Native" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace DropProbe
{
    public static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        public const uint LEFTDOWN = 0x0002;
        public const uint LEFTUP = 0x0004;
    }
}
"@
}

$automationRoot = [System.Windows.Automation.AutomationElement]::RootElement

function Find-ByName([string]$name, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $found = $automationRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

New-Item -ItemType Directory -Force $Root | Out-Null
Remove-Item (Join-Path $Root "drag-ready.txt") -ErrorAction SilentlyContinue

$target = Start-Process -FilePath $desktop -ArgumentList "--root", $Root -PassThru
Start-Sleep -Seconds 12
$target.Refresh()
Write-Host "desktop window: $($target.MainWindowHandle)"

# The drag source runs in its own root so that its log is separate from the
# desktop log.
$sourceRoot = Join-Path $Root "source"
New-Item -ItemType Directory -Force $sourceRoot | Out-Null
$source = Start-Process -FilePath $desktop -ArgumentList "--root", $sourceRoot, "--drag-source", $Apk -PassThru
$readyDeadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path (Join-Path $sourceRoot "drag-ready.txt")) -and (Get-Date) -lt $readyDeadline) {
    Start-Sleep -Milliseconds 300
}
$source.Refresh()
Write-Host "drag source window: $($source.MainWindowHandle) ready=$(Test-Path (Join-Path $Root 'drag-ready.txt'))"

$handle = Find-ByName "Drag source handle" 30
if (-not $handle) { Write-Host "the handle was not found"; Stop-Process -Id $source.Id -Force; Stop-Process -Id $target.Id -Force; exit 2 }

[void][DropProbe.Native]::ShowWindow($source.MainWindowHandle, 9)
[void][DropProbe.Native]::SetForegroundWindow($source.MainWindowHandle)
Start-Sleep -Seconds 2

$handle = Find-ByName "Drag source handle" 20
$rect = $handle.Current.BoundingRectangle

# Which window actually owns the point where the handle is reported?
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Wfp
{
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
"@ -ErrorAction SilentlyContinue
$probePoint = New-Object Wfp+POINT
$probePoint.X = [int]($rect.X + $rect.Width / 2)
$probePoint.Y = [int]($rect.Y + $rect.Height / 2)
$owner = [Wfp]::WindowFromPoint($probePoint)
$ownerPid = 0
[void][Wfp]::GetWindowThreadProcessId($owner, [ref]$ownerPid)
Write-Host "the window under the handle is pid $ownerPid (drag source pid $($source.Id), desktop pid $($target.Id))"
$x = [int]($rect.X + $rect.Width / 2)
$y = [int]($rect.Y + $rect.Height / 2)
Write-Host ("handle at {0},{1} ({2}x{3})" -f $x, $y, [int]$rect.Width, [int]$rect.Height)

[void][DropProbe.Native]::SetCursorPos($x, $y)
Start-Sleep -Milliseconds 700
$point = New-Object DropProbe.Native+POINT
[void][DropProbe.Native]::GetCursorPos([ref]$point)
Write-Host "cursor is at $($point.X),$($point.Y)"

[DropProbe.Native]::mouse_event([DropProbe.Native]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 2
[void][DropProbe.Native]::GetCursorPos([ref]$point)
Write-Host "after the press the cursor is at $($point.X),$($point.Y)"

$targetRect = $handle.Current.BoundingRectangle
$desktopElement = $null
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty,
    [int]$target.MainWindowHandle.ToInt64())
$desktopElement = $automationRoot.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
$desktopRect = $desktopElement.Current.BoundingRectangle
$targetX = [int]($desktopRect.X + $desktopRect.Width * 0.5)
$targetY = [int]($desktopRect.Y + $desktopRect.Height * 0.5)

for ($step = 1; $step -le 20; $step++) {
    $mx = [int]($x + ($targetX - $x) * $step / 20)
    $my = [int]($y + ($targetY - $y) * $step / 20)
    [void][DropProbe.Native]::SetCursorPos($mx, $my)
    Start-Sleep -Milliseconds 150
}
[void][DropProbe.Native]::GetCursorPos([ref]$point)
Write-Host "before the release the cursor is at $($point.X),$($point.Y), target was $targetX,$targetY"
Start-Sleep -Seconds 2
[DropProbe.Native]::mouse_event([DropProbe.Native]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 5

Write-Host "--- the desktop log ---"
Get-Content (Join-Path $Root "logs\desktop.log") -ErrorAction SilentlyContinue |
    Select-String -Pattern "drag over|drop " | Select-Object -First 6 | ForEach-Object { $_.Line }
Write-Host "--- the drag source log ---"
Get-Content (Join-Path $sourceRoot "logs\desktop.log") -ErrorAction SilentlyContinue |
    Select-String -Pattern "drag " | Select-Object -First 6 | ForEach-Object { $_.Line }

Stop-Process -Id $source.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
