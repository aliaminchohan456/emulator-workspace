#requires -Version 5.1
<#
.SYNOPSIS
    Phase 1 acceptance: supervisor, guest lifecycle, capture, APK loop, recovery.

.DESCRIPTION
    Runs the non-visual half of the Phase 1 gate against a fresh workspace root.
    Every step goes through the supervisor-backed wsctl surface. Results and raw
    artifacts are written to artifacts/phase1/<run-id>/.

    The WPF interactions (embedded viewport, drag/drop, Logcat drawer) are
    exercised by the documented manual sequence in docs/phase1-evidence.md and
    supported by captured screenshots; this script verifies the same operations
    and the supervisor contract behind them.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\phase1-acceptance.ps1
#>
param(
    [string]$Root = "D:\Phase1Accept",
    [string]$Sdk = $env:ANDROID_SDK_ROOT,
    [string]$SampleApk = "artifacts\phase1\sample\ws-sample.apk",
    [int]$BootTimeoutSeconds = 600,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Sdk) { $Sdk = $env:ANDROID_HOME }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }

$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo ("artifacts\phase1\" + $runId)
New-Item -ItemType Directory -Force $out | Out-Null
$transcript = Join-Path $out "transcript.txt"
$results = @()

function Log([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $message
    Write-Host $line
    Add-Content -Path $transcript -Value $line -Encoding UTF8
}

function Record([string]$name, [bool]$passed, [string]$detail) {
    $script:results += [pscustomobject]@{ Check = $name; Passed = $passed; Detail = $detail }
    Log ("{0} {1}: {2}" -f ($(if ($passed) { "PASS" } else { "FAIL" })), $name, $detail)
}

function Invoke-Wsctl {
    param([string[]]$Arguments, [switch]$AllowFailure)
    $all = $Arguments + @("--root", $Root, "--sdk", $Sdk)
    Log ("$ wsctl " + ($Arguments -join " "))
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $output = & $wsctl @all 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($output) { $output | ForEach-Object { Add-Content -Path $transcript -Value ("    " + $_) -Encoding UTF8 } }
    if ($code -ne 0 -and -not $AllowFailure) { throw "wsctl failed: $($Arguments -join ' ') ($code)" }
    return [pscustomobject]@{ ExitCode = $code; Output = ($output -join "`n") }
}

$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
if (-not (Test-Path $wsctl)) { throw "Build the solution first: $wsctl is missing." }
$adb = Join-Path $Sdk "platform-tools\adb.exe"
$apk = Join-Path $repo $SampleApk
if (-not (Test-Path $apk)) { throw "Sample APK missing: $apk. Run sample\build-sample-apk.ps1." }

Log "Phase 1 acceptance run $runId on root $Root"

# 0. Regression baseline
Push-Location $repo
try {
    $build = & dotnet build -v q --nologo 2>&1 | Out-String
    Record "build" ($LASTEXITCODE -eq 0) "dotnet build exit $LASTEXITCODE"
    $test = & dotnet test --no-build -v q --nologo 2>&1 | Out-String
    $passed = [regex]::Match($test, 'Passed:\s+(\d+)').Groups[1].Value
    $failed = [regex]::Match($test, 'Failed:\s+(\d+)').Groups[1].Value
    Record "unit-tests" ($LASTEXITCODE -eq 0) "passed=$passed failed=$failed"
}
finally { Pop-Location }

# 1. Supervisor and first run
$status = Invoke-Wsctl @("supervisor-status")
Record "supervisor.status" ($status.Output -match "protocol 1") $status.Output

$doctor = Invoke-Wsctl @("doctor", "--json", (Join-Path $out "doctor.json"))
Record "doctor.whpx" ($doctor.Output -match "is installed and usable") "WHPX verdict"
Record "doctor.image" ($doctor.Output -match "android-35;default;x86_64") "pinned image present"

$create = Invoke-Wsctl @("create", "--name", "qa1")
Record "instance.create" ($create.Output -match "created qa1") $create.Output

$bootStart = Get-Date
$start = Invoke-Wsctl @("start", "--name", "qa1")
$wait = Invoke-Wsctl @("wait", "--name", "qa1", "--timeout", $BootTimeoutSeconds) -AllowFailure
$bootSeconds = [math]::Round(((Get-Date) - $bootStart).TotalSeconds, 1)
Record "instance.boot" ($wait.ExitCode -eq 0) ("wall={0}s " -f $bootSeconds + $wait.Output)

$target = Invoke-Wsctl @("verify-target", "--name", "qa1")
Record "adb.target" ($target.Output -match "get-serialno\s+:\s+emulator-\d+") "exact serial"

$probe = Invoke-Wsctl @("controller-probe", "--name", "qa1")
Record "controller.auth" (($probe.Output -match "no-token call\s+: Unauthenticated") -and ($probe.Output -match "loopback-only")) "token + loopback"

# 2. Capture trace
$trace = Invoke-Wsctl @("frame-trace", "--name", "qa1", "--seconds", "10", "--width", "1080",
    "--out", (Join-Path $out "frame-trace.json")) -AllowFailure
$traceJson = Get-Content (Join-Path $out "frame-trace.json") -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
Record "capture.continuous" ($trace.ExitCode -eq 0 -and $traceJson.frames -gt 10) `
    ("frames={0} fps={1} p50={2}ms p95={3}ms" -f $traceJson.frames, $traceJson.fps, $traceJson.p50IntervalMs, $traceJson.p95IntervalMs)

# 3. Developer loop through the supervisor
$install = Invoke-Wsctl @("install", "--name", "qa1", "--apk", $apk)
Record "apk.install" ($install.Output -match "installed") $install.Output
$launch = Invoke-Wsctl @("launch", "--name", "qa1", "--package", "dev.emulatorworkspace.sample")
Start-Sleep -Seconds 3
$foreground = Invoke-Wsctl @("foreground", "--name", "qa1")
Record "apk.launch" ($foreground.Output -match "dev.emulatorworkspace.sample") $foreground.Output

$frame = Invoke-Wsctl @("frame", "--name", "qa1", "--via", "controller", "--out", (Join-Path $out "screenshot.png"))
Record "screenshot" (Test-Path (Join-Path $out "screenshot.png")) $frame.Output

$tap = Invoke-Wsctl @("tap", "--name", "qa1", "--x", "540", "--y", "720")
Start-Sleep -Seconds 2
$tapLog = & $adb -s emulator-5554 logcat -d -s WS_SAMPLE:I 2>&1 | Out-String
Record "input.visible" ($tapLog -match "TAP index=") "WS_SAMPLE marker present"
$tapLog | Set-Content (Join-Path $out "logcat-marker.txt") -Encoding UTF8

# 4. Agent denial
$denied = Invoke-Wsctl @("frame", "--name", "qa1", "--agent", "--out", (Join-Path $out "denied.png")) -AllowFailure
Record "agent.denied" ($denied.ExitCode -eq 4) ("exit " + $denied.ExitCode)

# 5. Snapshot round trip
$save = Invoke-Wsctl @("snapshot-save", "--name", "qa1", "--tag", "qa-baseline")
$load = Invoke-Wsctl @("snapshot-load", "--name", "qa1", "--tag", "qa-baseline")
Record "snapshot.roundtrip" (($save.ExitCode -eq 0) -and ($load.ExitCode -eq 0)) ($save.Output + " / " + $load.Output)

# 6. Second guest isolation
$create2 = Invoke-Wsctl @("create", "--name", "qa2")
$start2 = Invoke-Wsctl @("start", "--name", "qa2")
$wait2 = Invoke-Wsctl @("wait", "--name", "qa2", "--timeout", $BootTimeoutSeconds) -AllowFailure
$target2 = Invoke-Wsctl @("verify-target", "--name", "qa2")
Record "isolation.second-guest" ($wait2.ExitCode -eq 0 -and $target2.Output -match "emulator-") $wait2.Output

# 7. Port conflict is a typed, actionable error
$conflict = Invoke-Wsctl @("create", "--name", "qa3", "--console", "5554") -AllowFailure
Record "error.port-conflict" ($conflict.Output -match "already owned|port_conflict") $conflict.Output

# 8. Supervisor restart and reattachment
$sup = Invoke-Wsctl @("supervisor-status")
$supPid = [regex]::Match($sup.Output, 'pid (\d+)').Groups[1].Value
if ($supPid) { Stop-Process -Id ([int]$supPid) -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3
$reattach = Invoke-Wsctl @("status", "--name", "qa1") -AllowFailure
Record "recovery.reattach" (($reattach.Output -match "engineAlive=True") -or ($reattach.Output -match "engineAlive=true")) $reattach.Output

if (-not $KeepRunning) {
    Invoke-Wsctl @("stop", "--name", "qa1") -AllowFailure | Out-Null
    Invoke-Wsctl @("stop", "--name", "qa2") -AllowFailure | Out-Null
}

$failedCount = @($results | Where-Object { -not $_.Passed }).Count
$summary = [pscustomobject]@{
    runId = $runId
    root = $Root
    completedUtc = (Get-Date).ToUniversalTime().ToString("o")
    checks = $results
    failed = $failedCount
}
$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$results | Format-Table -AutoSize | Out-String | Write-Host
Log ("Completed with " + $failedCount + " failure(s). Artifacts: " + $out)
if ($failedCount -gt 0) { exit 2 }
exit 0
