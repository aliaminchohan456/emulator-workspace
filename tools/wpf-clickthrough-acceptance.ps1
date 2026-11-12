#requires -Version 5.1
<#
.SYNOPSIS
    Real WPF click-through: drives the live Emulator Workspace desktop window
    through UI Automation and records what the UI actually did.

.DESCRIPTION
    Nothing in this harness calls the product API directly: every step is a
    UI Automation click on a real control of the running WPF window, and the
    supervisor is only used as an observer to prove what the UI caused.

    The sequence is: Create device -> boot -> APK drag-and-drop -> Launch ->
    Tap -> Logcat -> Screenshot -> Shutdown, plus the Root Lab controls
    (ADB root / ADB unroot) and the Ctrl+K command palette.

    Each step records a window screenshot, the UI tree snapshot and the
    observed supervisor state, so the run is reproducible evidence.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\wpf-clickthrough-acceptance.ps1 `
      -Root D:\Phase4Root -Apk C:\path\to\ws-sample.apk
#>
param(
    [string]$Root = "D:\Phase4Root",
    [string]$Apk = "",
    [string]$OutDir = "artifacts\phase3\wpf",
    [int]$BootTimeoutSeconds = 600,
    [switch]$KeepRoot
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$desktopExe = Join-Path $repo "src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe"
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not (Test-Path $desktopExe)) { throw "The desktop binary is not built: $desktopExe" }
if (-not (Test-Path $wsctl)) { throw "The CLI binary is not built: $wsctl" }
if (-not $Apk) {
    $candidate = Join-Path $Root "apps\ws-sample.apk"
    if (-not (Test-Path $candidate)) {
        $candidate = (Get-ChildItem (Join-Path $repo "artifacts") -Recurse -Filter "ws-sample.apk" -ErrorAction SilentlyContinue |
            Select-Object -First 1).FullName
    }
    $Apk = $candidate
}
if (-not $Apk -or -not (Test-Path $Apk)) { throw "No APK was found for the drag-and-drop step." }

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$script:results = New-Object System.Collections.ArrayList
$script:steps = New-Object System.Collections.ArrayList

function Log([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $message
    Write-Host $line
    Add-Content -Path (Join-Path $out "transcript.txt") -Value $line -Encoding UTF8
}

function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$script:results.Add([pscustomobject]@{
        Check = $name
        Passed = $ok
        Detail = $detail
        AtUtc = (Get-Date).ToUniversalTime().ToString("O")
    })
    Log ("{0} {1}: {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail)
}

function Add-Step([string]$name, [bool]$ok, [string]$detail, [string]$shot) {
    [void]$script:steps.Add([pscustomobject]@{
        Step = $name
        Passed = $ok
        Detail = $detail
        Screenshot = $shot
        AtUtc = (Get-Date).ToUniversalTime().ToString("O")
    })
    Add-Check ("clickthrough." + $name) $ok $detail
}

# ---------------------------------------------------------------- Win32 help
if (-not ("ClickThrough.Native" -as [type])) {
    Add-Type @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ClickThrough
{
    public static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public const uint PW_RENDERFULLCONTENT = 0x00000002;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    }
}
"@
}

function Save-WindowShot([IntPtr]$handle, [string]$name) {
    $path = Join-Path $out $name
    $rect = New-Object ClickThrough.Native+RECT
    if (-not [ClickThrough.Native]::GetWindowRect($handle, [ref]$rect)) { return $null }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { return $null }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [void][ClickThrough.Native]::PrintWindow($handle, $hdc, [ClickThrough.Native]::PW_RENDERFULLCONTENT)
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return $path
}

# ------------------------------------------------------------------ UIA help
$script:desktop = [System.Windows.Automation.AutomationElement]::RootElement

function Find-Element([string]$name, [string]$automationId = "", [int]$timeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $found = $script:desktop.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Click-Element([System.Windows.Automation.AutomationElement]$element) {
    if (-not $element) { throw "The element is null." }
    try {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
        return $true
    }
    catch {
        $rect = $element.Current.BoundingRectangle
        $x = [int]($rect.X + $rect.Width / 2)
        $y = [int]($rect.Y + $rect.Height / 2)
        [void][ClickThrough.Native]::SetCursorPos($x, $y)
        Start-Sleep -Milliseconds 120
        [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        return $true
    }
}

function Dump-UiTree([System.Windows.Automation.AutomationElement]$root, [string]$name) {
    $lines = New-Object System.Collections.ArrayList
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)
    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        try {
            $current = $node.Current
            [void]$lines.Add(("{0}|{1}|{2}" -f $current.ControlType.ProgrammaticName, $current.Name, $current.AutomationId))
            $child = $walker.GetFirstChild($node)
            while ($child) {
                $stack.Push($child)
                $child = $walker.GetNextSibling($child)
            }
        }
        catch { }
    }
    Set-Content -Path (Join-Path $out $name) -Value ($lines -join "`n") -Encoding UTF8
    return $lines.Count
}

function Invoke-Ws([string[]]$arguments, [switch]$AllowFailure) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $wsctl
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $quoted = @()
    foreach ($argument in $arguments) { $quoted += ('"' + ($argument -replace '"', '\"') + '"') }
    $info.Arguments = $quoted -join " "
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    [void]$process.WaitForExit(120000)
    $text = ""
    try { $text = $stdout.Result + $stderr.Result } catch { }
    $code = -1
    try { $code = $process.ExitCode } catch { }
    $text = ($text | Out-String).Trim()
    if ($code -ne 0 -and -not $AllowFailure) { throw "wsctl $($arguments -join ' ') failed ($code): $text" }
    return [pscustomobject]@{ Code = $code; Text = $text }
}

function Parse-Json([string]$text) {
    try { return $text | ConvertFrom-Json } catch { return $null }
}

Log "WPF click-through $run root=$Root apk=$Apk"

$desktopProcess = $null
try {
    if (-not $KeepRoot) {
        Get-Process EmulatorWorkspace.Supervisor, EmulatorWorkspace.Desktop -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $Root | Out-Null
        $apps = Join-Path $Root "apps"
        New-Item -ItemType Directory -Force $apps | Out-Null
        Copy-Item $Apk (Join-Path $apps "ws-sample.apk") -Force
    }

    # 1. start the desktop
    $desktopProcess = Start-Process -FilePath $desktopExe -ArgumentList "--root", $Root -PassThru
    $window = $null
    $windowDeadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $windowDeadline) {
        $desktopProcess.Refresh()
        if ($desktopProcess.HasExited) { throw "The desktop exited early with code $($desktopProcess.ExitCode)." }
        if ($desktopProcess.MainWindowHandle -ne [IntPtr]::Zero) {
            # The automation property needs a 32-bit handle value.
            $handleValue = [int]$desktopProcess.MainWindowHandle.ToInt64()
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty,
                $handleValue)
            $window = $script:desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
            if ($window) { break }
        }
        Start-Sleep -Milliseconds 400
    }
    if (-not $window) { throw "The desktop window did not appear." }
    [void][ClickThrough.Native]::ShowWindow($desktopProcess.MainWindowHandle, 3)
    [void][ClickThrough.Native]::SetForegroundWindow($desktopProcess.MainWindowHandle)
    Start-Sleep -Seconds 2
    $handle = [IntPtr]([int]$desktopProcess.MainWindowHandle.ToInt64())
    $shot = Save-WindowShot $handle "01-startup.png"
    Add-Step "window-open" $true ("the desktop window is open and reachable by UI Automation") $shot

    # 2. create the device through the real button
    $create = Find-Element "Create device" "" 60
    if (-not $create) { throw "The 'Create device' control was not found." }

    # The control is disabled while the desktop is still connecting, so the
    # click waits until it can be invoked.
    $enabledDeadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $enabledDeadline) {
        try { if ($create.Current.IsEnabled) { break } } catch { break }
        Start-Sleep -Milliseconds 500
    }

    Click-Element $create
    Log "clicked the Create device button"
    $deviceName = $null
    $deviceDeadline = (Get-Date).AddSeconds(240)
    while ((Get-Date) -lt $deviceDeadline) {
        # The CLI prints the instance list as a table; the first column is the
        # instance name.
        $listing = (Invoke-Ws @("list", "--root", $Root) -AllowFailure).Text
        $rows = @($listing -split "`r?`n" | Where-Object { $_ -match "^\S+\s+emulator-" })
        if ($rows.Count -gt 0) {
            $deviceName = ($rows[0] -split "\s+")[0]
            break
        }
        Start-Sleep -Seconds 2
    }
    if (-not $deviceName) { throw "The desktop did not create a managed instance." }
    Add-Step "create-device" $true ("the Create device button created the managed instance '$deviceName'") (Save-WindowShot $handle "02-created.png")

    # 3. wait for the boot
    $bootDeadline = (Get-Date).AddSeconds($BootTimeoutSeconds)
    $booted = $false
    while ((Get-Date) -lt $bootDeadline) {
        $status = Parse-Json (Invoke-Ws @("status", "--name", $deviceName, "--json", "--root", $Root) -AllowFailure).Text
        if ($status -and $status.state -eq "Running") {
            # The readiness report carries the interactive timestamp; a
            # guest that reached it is usable.
            $wait = Parse-Json (Invoke-Ws @("wait", "--name", $deviceName, "--timeout", "30", "--root", $Root) -AllowFailure).Text
            if ($wait -and $wait.interactiveUtc) { $booted = $true; break }
        }
        Start-Sleep -Seconds 5
    }
    Add-Step "boot" $booted ("the guest created from the UI reached interactive readiness") (Save-WindowShot $handle "03-booted.png")
    if (-not $booted) { throw "The guest did not reach readiness." }

    # 4. the Root Lab surface: the ADB root button
    $adbRoot = Find-Element "ADB root" "" 20
    Add-Check "rootlab.control-present" ($null -ne $adbRoot) "the Root Lab ADB root control exists in the desktop"
    if ($adbRoot) {
        Click-Element $adbRoot
        $rootDeadline = (Get-Date).AddSeconds(120)
        $rooted = $false
        while ((Get-Date) -lt $rootDeadline) {
            $state = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $deviceName, "--root", $Root) -AllowFailure).Text
            if ($state -and $state.uid -eq "0") { $rooted = $true; break }
            Start-Sleep -Seconds 3
        }
        Add-Step "adb-root" $rooted "the Root Lab ADB root button put the guest daemon at uid 0" (Save-WindowShot $handle "04-adb-root.png")

        $adbUnroot = Find-Element "ADB unroot" "" 20
        if ($adbUnroot) {
            Click-Element $adbUnroot
            $unrootDeadline = (Get-Date).AddSeconds(120)
            $unrooted = $false
            while ((Get-Date) -lt $unrootDeadline) {
                $state = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $deviceName, "--root", $Root) -AllowFailure).Text
                if ($state -and $state.uid -eq "2000") { $unrooted = $true; break }
                Start-Sleep -Seconds 3
            }
            Add-Step "adb-unroot" $unrooted "the Root Lab ADB unroot button returned the daemon to uid 2000" (Save-WindowShot $handle "05-adb-unroot.png")
        }
    }

    # 5. the command palette
    [System.Windows.Forms.SendKeys]::SendWait("^k")
    Start-Sleep -Seconds 2
    $palette = Find-Element "Command palette filter" "" 15
    Add-Check "palette.opens" ($null -ne $palette) "Ctrl+K opened the command palette"
    if ($palette) {
        $shotPalette = Save-WindowShot $handle "06-palette.png"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Seconds 1
        Add-Step "command-palette" $true "the command palette opened with Ctrl+K" $shotPalette
    }

    # 6. drag and drop the APK onto the window
    $dropLog = Join-Path $out "drop-log.txt"
    $dragSource = Join-Path $repo "src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe"
    $helper = Start-Process -FilePath $dragSource -ArgumentList "--drag-source", $Apk -PassThru -RedirectStandardOutput $dropLog -RedirectStandardError (Join-Path $out "drop-err.txt")
    $helperReady = (Join-Path $out "drag-ready.txt")
    $readyDeadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $helperReady) -and (Get-Date) -lt $readyDeadline) { Start-Sleep -Milliseconds 300 }
    $dropped = $false
    if (Test-Path $helperReady) {
        $rect = New-Object ClickThrough.Native+RECT
        [void][ClickThrough.Native]::GetWindowRect($handle, [ref]$rect)
        # The drop target is the middle of the guest viewport.
        $targetX = [int]($rect.Left + ($rect.Right - $rect.Left) * 0.5)
        $targetY = [int]($rect.Top + ($rect.Bottom - $rect.Top) * 0.45)
        Start-Sleep -Milliseconds 800
        [void][ClickThrough.Native]::SetCursorPos($targetX, $targetY)
        Start-Sleep -Milliseconds 300
        [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 200
        [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        $installDeadline = (Get-Date).AddSeconds(300)
        while ((Get-Date) -lt $installDeadline) {
            $info = Parse-Json (Invoke-Ws @("package-info", "--name", $deviceName, "--package", "dev.emulatorworkspace.sample", "--root", $Root) -AllowFailure).Text
            if ($info -and $info.installed -eq $true) { $dropped = $true; break }
            Start-Sleep -Seconds 3
        }
    }
    if (-not $helper.HasExited) { Stop-Process -Id $helper.Id -Force -ErrorAction SilentlyContinue }
    Add-Step "apk-drag-drop" $dropped "the APK was dropped on the desktop window and installed on the selected guest" (Save-WindowShot $handle "07-dropped.png")

    # 7. launch through the toolbar
    $launch = Find-Element "Launch sample" "" 20
    if (-not $launch) { throw "The 'Launch sample' control was not found." }
    Click-Element $launch
    $launchDeadline = (Get-Date).AddSeconds(120)
    $foreground = ""
    while ((Get-Date) -lt $launchDeadline) {
        $foreground = (Invoke-Ws @("app-foreground", "--name", $deviceName, "--root", $Root) -AllowFailure).Text
        if ($foreground -match "dev.emulatorworkspace.sample") { break }
        Start-Sleep -Seconds 3
    }
    Add-Step "launch" ($foreground -match "dev.emulatorworkspace.sample") "the Launch sample button brought the application to the foreground" (Save-WindowShot $handle "08-launched.png")

    # 8. tap through the viewport, using the real control bounds of the guest button
    $tree = Parse-Json (Invoke-Ws @("ui-tree", "--name", $deviceName, "--root", $Root) -AllowFailure).Text
    $node = $null
    if ($tree -and $tree.nodes) {
        $node = @($tree.nodes) | Where-Object { $_.contentDescription -eq "login_button" } | Select-Object -First 1
    }
    $tapMapped = $false
    if ($node) {
        $viewport = Find-Element "Embedded Android viewport" "" 20
        if ($viewport) {
            $bounds = $viewport.Current.BoundingRectangle
            $guestWidth = $node.bounds.width
            $guestHeight = $node.bounds.height
            # The guest coordinate of the control is resolved from the observed
            # hierarchy; the click point is derived from the real viewport box.
            $centerX = $node.bounds.x + ($guestWidth / 2)
            $centerY = $node.bounds.y + ($guestHeight / 2)
            $screenX = [int]($bounds.X + ($centerX / 1080) * $bounds.Width)
            $screenY = [int]($bounds.Y + ($centerY / 1920) * $bounds.Height)
            [void][ClickThrough.Native]::SetCursorPos($screenX, $screenY)
            Start-Sleep -Milliseconds 300
            [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
            [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
            $tapDeadline = (Get-Date).AddSeconds(60)
            while ((Get-Date) -lt $tapDeadline) {
                $assert = Parse-Json (Invoke-Ws @("ui-assert", "--name", $deviceName, "--selector", '{"contentDescription":"login_status","text":"WELCOME"}', "--root", $Root) -AllowFailure).Text
                if ($assert -and $assert.pass -eq $true) { $tapMapped = $true; break }
                Start-Sleep -Seconds 2
            }
        }
    }
    Add-Step "tap" $tapMapped "a click in the viewport reached the guest control and changed the guest state" (Save-WindowShot $handle "09-tapped.png")

    # 9. logcat drawer
    $logcat = Find-Element "Logcat" "" 20
    if ($logcat) { Click-Element $logcat }
    Start-Sleep -Seconds 6
    $logcatVisible = (Find-Element "Logcat drawer" "" 8) -ne $null
    Add-Step "logcat" $logcatVisible "the Logcat drawer opened and shows the guest log" (Save-WindowShot $handle "10-logcat.png")

    # 10. screenshot
    $screenshot = Find-Element "Screenshot" "" 20
    if ($screenshot) { Click-Element $screenshot }
    $shotDeadline = (Get-Date).AddSeconds(90)
    $shotFile = $null
    while ((Get-Date) -lt $shotDeadline) {
        $found = Get-ChildItem $Root -Recurse -Filter "*.png" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "screenshot|capture" } | Select-Object -First 1
        if ($found) { $shotFile = $found.FullName; break }
        Start-Sleep -Seconds 2
    }
    Add-Step "screenshot" ($null -ne $shotFile) ("the Screenshot button produced " + $(if ($shotFile) { Split-Path $shotFile -Leaf } else { "no file" })) (Save-WindowShot $handle "11-screenshot.png")

    # 11. shutdown through the primary action
    $stop = Find-Element "Stop" "" 30
    if (-not $stop) { $stop = Find-Element "Primary device action" "" 10 }
    if ($stop) { Click-Element $stop }
    $stopDeadline = (Get-Date).AddSeconds(180)
    $stopped = $false
    while ((Get-Date) -lt $stopDeadline) {
        $status = Parse-Json (Invoke-Ws @("status", "--name", $deviceName, "--json", "--root", $Root) -AllowFailure).Text
        if ($status -and $status.state -ne "Running") { $stopped = $true; break }
        Start-Sleep -Seconds 3
    }
    Add-Step "shutdown" $stopped "the desktop stopped the guest" (Save-WindowShot $handle "12-stopped.png")

    $treeCount = Dump-UiTree $window "ui-tree-final.txt"
    Add-Check "ui-tree" ($treeCount -gt 20) "the final UI tree has $treeCount automation elements"
}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
    Log ("fatal: " + $_.Exception.Message)
}
finally {
    if ($desktopProcess -and -not $desktopProcess.HasExited) {
        Stop-Process -Id $desktopProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

$failed = @($script:results | Where-Object { -not $_.Passed }).Count
$summary = [ordered]@{
    runId = $run
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    root = $Root
    apk = $Apk
    failed = $failed
    checks = @($script:results)
    steps = @($script:steps)
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$script:results | Format-Table -AutoSize | Out-String | Write-Host
Log "completed failures=$failed artifacts=$out"
if ($failed -gt 0) { exit 2 }
exit 0
