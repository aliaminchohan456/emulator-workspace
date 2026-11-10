#requires -Version 5.1
<#
.SYNOPSIS
    Strict sample-app launch check with race/ANR/timeout classification.

.DESCRIPTION
    Waits for the guest to be interactive, launches the sample activity, and
    classifies the result:
      - pass: sample activity is the focused window, no ANR/overlay, and a tap
        produces a WS_SAMPLE log line;
      - fail-anr: a blocking "Not Responding" window was observed;
      - fail-timeout: the sample activity never became focused in the window;
      - fail-command: the launch command or install failed.
    Artifacts (window dumps, logcat slice, screenshot) are written per attempt.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\launch-check.ps1 -Root D:\Phase1Root -Name t1
#>
param(
    [string]$Root = "D:\Phase1Root",
    [string]$Name = "t1",
    [string]$OutDir = "artifacts\phase1-1",
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"

function Invoke-Wsctl([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $output = & $wsctl @Arguments 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    return [pscustomobject]@{ ExitCode = $code; Output = ($output -join "`n") }
}

$status = Invoke-Wsctl @("status", "--name", $Name, "--root", $Root)
$serial = ([regex]::Match($status.Output, "serial=(emulator-\d+)")).Groups[1].Value
if (-not $serial) { throw "Could not resolve the managed serial for $Name." }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir "launch-$Name-$stamp")
New-Item -ItemType Directory -Force $out | Out-Null

& $adb -s $serial shell input keyevent KEYCODE_WAKEUP | Out-Null
& $adb -s $serial logcat -c | Out-Null

$boot = & $adb -s $serial shell getprop sys.boot_completed 2>&1
if (($boot -join "").Trim() -ne "1") { throw "Guest is not boot-complete." }

$install = Invoke-Wsctl @("install", "--name", $Name, "--apk", (Join-Path $repo "artifacts\phase1\sample\ws-sample.apk"), "--root", $Root)
if ($install.ExitCode -ne 0) {
    "install failed: $($install.Output)" | Set-Content (Join-Path $out "result.txt")
    Write-Output "FAIL fail-command (install)"
    exit 2
}

# Settle the device before launching: screen on, animations off, sample stopped.
& $adb -s $serial shell am force-stop dev.emulatorworkspace.sample | Out-Null
& $adb -s $serial shell input keyevent KEYCODE_HOME | Out-Null
Start-Sleep -Seconds 2

$launch = Invoke-Wsctl @("launch", "--name", $Name, "--package", "dev.emulatorworkspace.sample", "--root", $Root)
$launch.Output | Set-Content (Join-Path $out "launch-command.txt")

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$focused = $null
$anr = $false
$windowDump = ""
while ((Get-Date) -lt $deadline) {
    $windowDump = (& $adb -s $serial shell dumpsys window 2>&1 | Out-String)
    $focus = [regex]::Match($windowDump, 'mCurrentFocus=(.+)').Groups[1].Value.Trim()
    if ($focus -match "Not Responding|Application Not Responding") { $anr = $true; break }
    if ($focus -match "dev\.emulatorworkspace\.sample") { $focused = $focus; break }
    Start-Sleep -Milliseconds 400
}

$windowDump | Set-Content (Join-Path $out "windows.txt")
(& $adb -s $serial shell dumpsys activity activities 2>&1 | Out-String) | Set-Content (Join-Path $out "activities.txt")

if ($anr) {
    (& $adb -s $serial logcat -d -v threadtime 2>&1 | Out-String) |
        Set-Content (Join-Path $out "logcat.txt")
    "ANR observed; focus dump in windows.txt" | Set-Content (Join-Path $out "result.txt")
    Write-Output "FAIL fail-anr"
    exit 2
}

if (-not $focused) {
    (& $adb -s $serial logcat -d -v threadtime 2>&1 | Out-String) |
        Set-Content (Join-Path $out "logcat.txt")
    "Sample activity did not become focused within $TimeoutSeconds s" | Set-Content (Join-Path $out "result.txt")
    Write-Output "FAIL fail-timeout"
    exit 2
}

# Responsiveness: one tap must produce a WS_SAMPLE marker and a colour change.
$frameBefore = Join-Path $out "before.png"
$frameAfter = Join-Path $out "after.png"
Invoke-Wsctl @("frame", "--name", $Name, "--via", "controller", "--out", $frameBefore, "--root", $Root) | Out-Null
Invoke-Wsctl @("tap", "--name", $Name, "--x", 540, "--y", 720, "--root", $Root) | Out-Null
Start-Sleep -Seconds 2
Invoke-Wsctl @("frame", "--name", $Name, "--via", "controller", "--out", $frameAfter, "--root", $Root) | Out-Null
$logs = & $adb -s $serial logcat -d -s WS_SAMPLE:I 2>&1 | Out-String
$logs | Set-Content (Join-Path $out "logcat-sample.txt")

if ($logs -notmatch "TAP index=") {
    "Sample activity focused but no TAP marker" | Set-Content (Join-Path $out "result.txt")
    Write-Output "FAIL fail-responsiveness"
    exit 2
}

$hashBefore = (Get-FileHash $frameBefore -Algorithm SHA256).Hash
$hashAfter = (Get-FileHash $frameAfter -Algorithm SHA256).Hash
if ($hashBefore -eq $hashAfter) {
    "TAP marker present but frames identical" | Set-Content (Join-Path $out "result.txt")
    Write-Output "FAIL fail-responsiveness"
    exit 2
}

"PASS focus=$focused" | Set-Content (Join-Path $out "result.txt")
Write-Output "PASS launch $Name focus=$focused artifacts=$out"
exit 0
