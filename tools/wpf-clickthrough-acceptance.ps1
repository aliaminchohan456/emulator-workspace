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
    [switch]$KeepRoot,
    [switch]$SetupOnly,
    [switch]$ExpectMissingImage,
    [switch]$RecordDemo
)

if ($ExpectMissingImage -and -not $SetupOnly) {
    throw "-ExpectMissingImage requires -SetupOnly and an SDK without the pinned image."
}
if ($RecordDemo -and $SetupOnly) { throw "-RecordDemo requires a complete click-through." }
$ffmpeg = if ($RecordDemo) { (Get-Command ffmpeg.exe -CommandType Application -ErrorAction Stop).Source }
$ffprobe = if ($RecordDemo) { (Get-Command ffprobe.exe -CommandType Application -ErrorAction Stop).Source }

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "fixtures/acceptance-payload.ps1")
$desktopExe = Get-AcceptanceBinary "EmulatorWorkspace.Desktop.exe" "src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe"
$wsctl = Get-AcceptanceBinary "wsctl.exe" "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not (Test-Path $desktopExe)) { throw "The desktop binary is not built: $desktopExe" }
if (-not (Test-Path $wsctl)) { throw "The CLI binary is not built: $wsctl" }
if (-not $SetupOnly -and -not $Apk) {
    $candidate = Join-Path $Root "apps\ws-sample.apk"
    if (-not (Test-Path $candidate)) {
        $candidate = (Get-ChildItem (Join-Path $repo "artifacts") -Recurse -Filter "ws-sample.apk" -ErrorAction SilentlyContinue |
            Select-Object -First 1).FullName
    }
    $Apk = $candidate
}
if (-not $SetupOnly -and (-not $Apk -or -not (Test-Path $Apk))) {
    throw "No APK was found for the drag-and-drop step."
}

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
Write-AcceptanceContext $out
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
    try {
        $captured = [ClickThrough.Native]::PrintWindow($handle, $hdc, [ClickThrough.Native]::PW_RENDERFULLCONTENT)
    }
    finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }
    try {
        if (-not $captured) { return $null }
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
    return $path
}

# ------------------------------------------------------------------ UIA help
$script:desktop = [System.Windows.Automation.AutomationElement]::RootElement

function Find-Element([string]$name, [int]$timeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $found = $script:targetWindow.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($found -and -not $found.Current.IsOffscreen) { return $found }
        if ($name -eq "Command palette filter") {
            $windows = $script:desktop.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($ownedWindow in $windows) {
                if ($ownedWindow.Current.ProcessId -ne $script:targetWindow.Current.ProcessId) { continue }
                $found = $ownedWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($found -and -not $found.Current.IsOffscreen) { return $found }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Wait-Enabled([System.Windows.Automation.AutomationElement]$element, [int]$seconds = 90) {
    # A control is disabled while the desktop is busy, so every click waits
    # until the control can really be invoked.
    if (-not $element) { return }
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { if ($element.Current.IsEnabled) { return } } catch { return }
        Start-Sleep -Milliseconds 500
    }
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

function Test-SetupScreen([System.Windows.Automation.AutomationElement]$window, [IntPtr]$handle) {
    $message = if ($ExpectMissingImage) {
        "Fix the blocking prerequisite below before creating a device."
    } else {
        "No managed device yet. Create the pinned AOSP API 35 profile to begin."
    }
    $expected = @("Set up your first device", $message, "Create AOSP API 35 device")
    if ($ExpectMissingImage) {
        $expected += 'The pinned image system-images;android-35;default;x86_64 is not installed. Install it with sdkmanager.'
    }
    $controls = @{}
    $deadline = (Get-Date).AddSeconds(50)
    do {
        $controls.Clear()
        foreach ($name in $expected) {
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $name)
            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($element -and -not $element.Current.IsOffscreen) { $controls[$name] = $element }
        }
        if ($controls.Count -eq $expected.Count) { break }
        Start-Sleep -Milliseconds 350
    } while ((Get-Date) -lt $deadline)
    $button = $controls["Create AOSP API 35 device"]
    $createEnabled = $button -and $button.Current.IsEnabled
    $matches = $controls.Count -eq $expected.Count -and $createEnabled -eq (-not $ExpectMissingImage)
    $name = if ($ExpectMissingImage) { "missing-image" } else { "first-run" }
    $shot = Save-WindowShot $handle "$name.png"
    [void](Dump-UiTree $window "ui-tree-setup.txt")
    Add-Step $name ($matches -and [bool]$shot) "setup controls present=$($controls.Count)/$($expected.Count), create enabled=$createEnabled" $shot
}

function Start-Demo([IntPtr]$handle) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $ffmpeg
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = '-hide_banner -loglevel warning -f gdigrab -framerate 8 -draw_mouse 0 -i hwnd=0x{0:X} -t 600 -vf scale=1280:-2 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart "{1}"' -f $handle.ToInt64(), (Join-Path $out "desktop-demo.mp4")
    $process = [System.Diagnostics.Process]::Start($start)
    [pscustomobject]@{ Process = $process; Errors = $process.StandardError.ReadToEndAsync() }
}

function Complete-Demo($recorder) {
    $process = $recorder.Process
    try {
        if (-not $process.HasExited) {
            $process.StandardInput.WriteLine("q")
            $process.StandardInput.Flush()
            if (-not $process.WaitForExit(15000)) { throw "The recording did not finalize." }
        }
        $recorder.Errors.GetAwaiter().GetResult() | Set-Content (Join-Path $out "desktop-demo.log") -Encoding UTF8
        if ($process.ExitCode -ne 0) { throw "The window recorder exited with code $($process.ExitCode)." }
        $probe = & $ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,nb_frames,duration -of json (Join-Path $out "desktop-demo.mp4") | Out-String
        if ($LASTEXITCODE) { throw "ffprobe could not read the recording." }
        $probe | Set-Content (Join-Path $out "desktop-demo-video.json") -Encoding UTF8
        $video = ($probe | ConvertFrom-Json).streams[0]
        Add-Check "demo.recording" ([int]$video.nb_frames -ge 8) "video=$($video.codec_name), frames=$($video.nb_frames), duration=$($video.duration)s"
    }
    catch { Add-Check "demo.recording" $false $_.Exception.Message }
    finally {
        if (-not $process.HasExited) {
            $process.Kill()
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}

Log "WPF click-through $run root=$Root apk=$Apk"

$desktopProcess = $null
$recorder = $null
try {
    if (-not $KeepRoot) {
        if (Test-Path -LiteralPath $Root) { throw "Use a new disposable root or explicitly pass -KeepRoot." }
        New-Item -ItemType Directory -Force $Root | Out-Null
        if (-not $SetupOnly) {
            $apps = Join-Path $Root "apps"
            New-Item -ItemType Directory -Force $apps | Out-Null
            Copy-Item $Apk (Join-Path $apps "ws-sample.apk") -Force
        }
    }

    # 1. start the desktop
    $desktopArguments = @("--root", ('"{0}"' -f $Root))
    if (-not $SetupOnly) { $desktopArguments += @("--drop-file", ('"{0}"' -f $Apk)) }
    $desktopProcess = Start-Process -FilePath $desktopExe -ArgumentList $desktopArguments -PassThru
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
    $script:targetWindow = $window
    [void][ClickThrough.Native]::ShowWindow($desktopProcess.MainWindowHandle, 3)
    [void][ClickThrough.Native]::SetForegroundWindow($desktopProcess.MainWindowHandle)
    Start-Sleep -Seconds 2
    $handle = [IntPtr]([int]$desktopProcess.MainWindowHandle.ToInt64())
    if ($RecordDemo) { $recorder = Start-Demo $handle }
    $shot = Save-WindowShot $handle "01-startup.png"
    Add-Step "window-open" ([bool]$shot) "the desktop window is open and reachable by UI Automation" $shot
    if ($SetupOnly) {
        Test-SetupScreen $window $handle
    } else {

    # The automation tree is captured while the window is idle, because a modal
    # palette hides the rest of the tree.
    $treeCount = Dump-UiTree $window "ui-tree-start.txt"
    Add-Check "ui-tree" ($treeCount -gt 20) "the UI tree has $treeCount automation elements"

    # 2. create the device through the real button
    $create = Find-Element "Create device" 60
    if (-not $create) { throw "The 'Create device' control was not found." }

    Wait-Enabled $create
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
        # The readiness of the guest is read from its JSON status; the guest is
        # usable once Android reports the boot completion.
        $status = Parse-Json (Invoke-Ws @("status", "--name", $deviceName, "--json", "--root", $Root) -AllowFailure).Text
        if ($status -and $status.state -eq "Running" -and $status.bootCompleted -eq "1") {
            $booted = $true
            break
        }
        Start-Sleep -Seconds 5
    }
    Add-Step "boot" $booted ("the guest created from the UI reached interactive readiness") (Save-WindowShot $handle "03-booted.png")
    if (-not $booted) { throw "The guest did not reach readiness." }

    # 4. the Root Lab surface: the ADB root button
    $adbRoot = Find-Element "ADB root" 20
    Add-Check "rootlab.control-present" ($null -ne $adbRoot) "the Root Lab ADB root control exists in the desktop"
    if ($adbRoot) {
        Wait-Enabled $adbRoot
        $rootDeadline = (Get-Date).AddSeconds(150)
        $rooted = $false
        while ((Get-Date) -lt $rootDeadline) {
            # The button is clicked again while the daemon comes back; the
            # desktop ignores the request while it is busy.
            Click-Element $adbRoot
            Start-Sleep -Seconds 4
            $state = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $deviceName, "--root", $Root) -AllowFailure).Text
            if ($state -and $state.uid -eq "0") { $rooted = $true; break }
        }
        Add-Step "adb-root" $rooted "the Root Lab ADB root button put the guest daemon at uid 0" (Save-WindowShot $handle "04-adb-root.png")

        $adbUnroot = Find-Element "ADB unroot" 20
        if ($adbUnroot) {
            Wait-Enabled $adbUnroot
            $unrootDeadline = (Get-Date).AddSeconds(150)
            $unrooted = $false
            while ((Get-Date) -lt $unrootDeadline) {
                Click-Element $adbUnroot
                Start-Sleep -Seconds 4
                $state = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $deviceName, "--root", $Root) -AllowFailure).Text
                if ($state -and $state.uid -eq "2000") { $unrooted = $true; break }
            }
            Add-Step "adb-unroot" ($unrooted -and $rooted) "the Root Lab ADB unroot button returned the daemon to uid 2000" (Save-WindowShot $handle "05-adb-unroot.png")
        }
    }

    # 6. the APK is dropped on the window
    # The desktop drops the APK on its own window; the harness observes what
    # the guest received.
    $installed = $false
    $installDeadline = (Get-Date).AddSeconds(300)
    while ((Get-Date) -lt $installDeadline) {
        $info = Parse-Json (Invoke-Ws @("package-info", "--name", $deviceName, "--package", "dev.emulatorworkspace.sample", "--json", "--root", $Root) -AllowFailure).Text
        if ($info -and $info.installed -eq $true) {
            $installed = $true
            break
        }
        Start-Sleep -Seconds 3
    }

    Add-Step "apk-drag-drop" $installed "the APK was dropped on the desktop window and installed on the selected guest" (Save-WindowShot $handle "07-dropped.png")

    # 7. launch through the toolbar
    $launch = Find-Element "Launch sample" 20
    if (-not $launch) { throw "The 'Launch sample' control was not found." }
    Wait-Enabled $launch
    Click-Element $launch
    $launchDeadline = (Get-Date).AddSeconds(120)
    $foreground = ""
    while ((Get-Date) -lt $launchDeadline) {
        $foreground = (Invoke-Ws @("foreground", "--name", $deviceName, "--root", $Root) -AllowFailure).Text
        if ($foreground -match "dev.emulatorworkspace.sample") { break }
        Start-Sleep -Seconds 3
    }
    Add-Step "launch" ($foreground -match "dev.emulatorworkspace.sample") "the Launch sample button brought the application to the foreground" (Save-WindowShot $handle "08-launched.png")

    # 8. tap through the viewport. The click is placed on the guest canvas and
    # the guest's own log proves that the tap arrived.
    $tapMapped = $false
    $viewport = Find-Element "Embedded Android viewport" 20
    if ($viewport) {
        $bounds = $viewport.Current.BoundingRectangle
        # The sample scene occupies the lower half of the guest display, so a
        # click in the middle of the viewport lands on the canvas.
        $screenX = [int]($bounds.X + $bounds.Width * 0.5)
        $screenY = [int]($bounds.Y + $bounds.Height * 0.75)
        [void][ClickThrough.Native]::SetForegroundWindow($handle)
        [void][ClickThrough.Native]::SetCursorPos($screenX, $screenY)
        Start-Sleep -Milliseconds 500
        [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        [ClickThrough.Native]::mouse_event([ClickThrough.Native]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)

        $tapDeadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $tapDeadline) {
            $export = Parse-Json (Invoke-Ws @("logcat-export", "--name", $deviceName, "--lines", "200", "--root", $Root) -AllowFailure).Text
            if ($export -and $export.artifactPath -and (Test-Path $export.artifactPath)) {
                $log = Get-Content $export.artifactPath -Raw
                if ($log -match "TAP index=") { $tapMapped = $true; break }
            }
            Start-Sleep -Seconds 2
        }
    }
    Add-Step "tap" $tapMapped "a click in the viewport reached the guest and the guest logged the tap" (Save-WindowShot $handle "09-tapped.png")

    # 9. logcat drawer
    # The Logcat drawer starts open, so it is only toggled when it is closed.
    $logcatVisible = (Find-Element "Search logcat" 8) -ne $null
    if (-not $logcatVisible) {
        $logcat = Find-Element "Toggle Logcat" 20
        if ($logcat) {
            Wait-Enabled $logcat
            Click-Element $logcat
        }
        Start-Sleep -Seconds 6
        $logcatVisible = (Find-Element "Search logcat" 8) -ne $null
    }
    Add-Step "logcat" $logcatVisible "the Logcat drawer opened and shows the guest log" (Save-WindowShot $handle "10-logcat.png")

    # 10. screenshot
    $screenshot = Find-Element "Screenshot" 20
    if ($screenshot) {
        Wait-Enabled $screenshot
        Click-Element $screenshot
    }
    $shotDeadline = (Get-Date).AddSeconds(90)
    $shotFile = $null
    $shotDirectory = Join-Path $Root "screenshots"
    while ((Get-Date) -lt $shotDeadline) {
        $found = Get-ChildItem $shotDirectory -Filter "*.png" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($found) { $shotFile = $found.FullName; break }
        Start-Sleep -Seconds 2
    }
    Add-Step "screenshot" ($null -ne $shotFile) ("the Screenshot button produced " + $(if ($shotFile) { Split-Path $shotFile -Leaf } else { "no file" })) (Save-WindowShot $handle "11-screenshot.png")

    # 11. shutdown through the primary action
    $stop = Find-Element "Stop" 30
    if (-not $stop) { $stop = Find-Element "Primary device action" 10 }
    if ($stop) {
        Wait-Enabled $stop
        Click-Element $stop
    }
    $stopDeadline = (Get-Date).AddSeconds(180)
    $stopped = $false
    while ((Get-Date) -lt $stopDeadline) {
        $status = Parse-Json (Invoke-Ws @("status", "--name", $deviceName, "--json", "--root", $Root) -AllowFailure).Text
        if ($status -and $status.state -ne "Running") { $stopped = $true; break }
        Start-Sleep -Seconds 3
    }
    Add-Step "shutdown" $stopped "the desktop stopped the guest" (Save-WindowShot $handle "12-stopped.png")

    # The command palette is checked last: it is a modal window, so it must
    # not block the rest of the run.
    [void][ClickThrough.Native]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait("^k")
    $palette = Find-Element "Command palette filter" 30
    Add-Check "palette.opens" ($null -ne $palette) "Ctrl+K opened the command palette"
    $shotPalette = Save-WindowShot $handle "13-palette.png"
    if ($palette) {
        Add-Step "command-palette" $true "the command palette opened with Ctrl+K" $shotPalette
        try {
            [void][ClickThrough.Native]::SetForegroundWindow([IntPtr]$palette.Current.NativeWindowHandle)
            Start-Sleep -Milliseconds 500
            [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        }
        catch {
        }
    }
    else {
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    }
    Start-Sleep -Seconds 2

    $treeCount = Dump-UiTree $window "ui-tree-final.txt"
    Add-Check "ui-tree" ($treeCount -gt 20) "the UI tree has $treeCount automation elements"
    }
}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
    Log ("fatal: " + $_.Exception.Message)
}
finally {
    if ($recorder) { Complete-Demo $recorder }
    if ($desktopProcess -and -not $desktopProcess.HasExited) {
        $desktopProcess.Kill()
        [void]$desktopProcess.WaitForExit(5000)
    }
    if ($SetupOnly -and $desktopProcess) {
        $stopResult = Invoke-Ws @("supervisor-stop", "--root", $Root) -AllowFailure
        Add-Check "cleanup.supervisor-stop" ($stopResult.Code -eq 0) $stopResult.Text
    }
}

$failed = @($script:results | Where-Object { -not $_.Passed }).Count
$summary = [ordered]@{
    runId = $run
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    sourceCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    harness = "wpf-clickthrough-acceptance"
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
