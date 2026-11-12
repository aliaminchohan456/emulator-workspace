#requires -Version 5.1
<#
.SYNOPSIS
    Runs the desktop composition-proxy input latency probe on a real guest.

.DESCRIPTION
    Ensures the guest is running, brings the sample app to the foreground, then
    launches the desktop with --latency-probe. Each trial dispatches one tap and
    measures from desktop dispatch to the next WPF composition pass after the
    frame that contains the changed sample marker. Results are written as JSON.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\latency-probe.ps1 -Root D:\Phase1Root -Name t1 -Count 25
#>
param(
    [string]$Root = "D:\Phase1Root",
    [string]$Name = "t1",
    [ValidateRange(1, 100)][int]$Count = 25,
    [string]$Gpu,
    [string]$OutDir = "artifacts/phase3/wpf",
    [int]$BootTimeoutSeconds = 600,
    [string]$CandidateRelease = "",
    [string]$CandidatePayload = "",
    [string]$DesktopExecutable = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src/EmulatorWorkspace.Cli/bin/Debug/net10.0/wsctl.exe"
$desktop = Join-Path $repo "src/EmulatorWorkspace.Desktop/bin/Debug/net10.0-windows/EmulatorWorkspace.Desktop.exe"
$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$candidate = $null
$owned = $false
$checks = [System.Collections.Generic.List[object]]::new()
$resultPath = Join-Path $out "presentation.json"

function Invoke-Wsctl([string[]]$arguments) {
    $prior = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $wsctl @arguments --name $Name --root $Root 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prior }
    if ($code -ne 0) { throw "wsctl $($arguments[0]) failed ($code): $output" }
    return $output
}

function Check([string]$name, [bool]$passed, [string]$detail) {
    $checks.Add([pscustomobject]@{ name = $name; passed = $passed; detail = $detail })
    Write-Host "$name $passed $detail"
}

try {
    if ($CandidateRelease -or $CandidatePayload) {
        if (-not ($CandidateRelease -and $CandidatePayload)) { throw "Pass both candidate paths." }
        . (Join-Path $PSScriptRoot "fixtures/candidate-payload.ps1")
        $verified = Resolve-VerifiedCandidate $CandidateRelease $CandidatePayload
        $candidate = $verified.identity
        $candidate.evidenceScope = if ($DesktopExecutable) { "CLI, supervisor and sample APK" } else { "entire runtime payload" }
        $wsctl = Join-Path $verified.payload "wsctl.exe"
        $desktop = Join-Path $verified.payload "EmulatorWorkspace.Desktop.exe"
        $env:EMULATORWORKSPACE_SUPERVISOR_EXE = Join-Path $verified.payload "EmulatorWorkspace.Supervisor.exe"
        if (Get-Process qemu-system-x86_64-headless, qemu-system-x86_64 -ErrorAction SilentlyContinue) {
            throw "Run the quiet-host WPF probe after other emulator guests have stopped."
        }
        if (Test-Path -LiteralPath $Root) { throw "Use a new disposable root for the candidate probe." }
        $Name = "wpf-latency-$run"
        Invoke-Wsctl @("create") | Out-Null
        $owned = $true
    }

    if ($DesktopExecutable) { $desktop = (Resolve-Path -LiteralPath $DesktopExecutable).ProviderPath }
    $desktopSha256 = (Get-FileHash -LiteralPath $desktop).Hash
    $status = Invoke-Wsctl @("status", "--json") | ConvertFrom-Json
    if (-not $status.engineAlive) {
        $arguments = @("start")
        if ($Gpu) { $arguments += @("--gpu", $Gpu) }
        Invoke-Wsctl $arguments | Out-Null
    }
    Invoke-Wsctl @("wait", "--timeout", "$BootTimeoutSeconds") | Out-Null
    $list = Invoke-Wsctl @("list")
    if (@($list.Trim() -split '\r?\n').Count -ne 1 -or $list -notmatch "(?m)^$([regex]::Escape($Name))\s") {
        throw "The desktop probe requires exactly one profile in the selected root."
    }

    if ($owned) {
        $fixture = Join-Path $verified.payload "ws-sample.apk"
        $approved = Join-Path $Root "apps/ws-sample.apk"
        New-Item -ItemType Directory -Force (Split-Path -Parent $approved) | Out-Null
        Copy-Item -LiteralPath $fixture -Destination $approved
        Invoke-Wsctl @("install", "--apk", $approved) | Out-Null
    }
    $package = "dev.emulatorworkspace.sample"
    Invoke-Wsctl @("launch", "--package", $package) | Out-Null
    $foreground = Invoke-Wsctl @("foreground")
    if (-not $foreground.Contains($package)) { throw "The fixture did not reach the foreground." }
    Start-Sleep -Seconds 3

    $launch = New-Object System.Diagnostics.ProcessStartInfo
    $launch.FileName = $desktop
    $launch.UseShellExecute = $false
    $launch.Arguments = ('--root "{0}" --latency-probe {1} --out "{2}"' -f $Root, $Count, $resultPath)
    if ($verified) {
        $launch.EnvironmentVariables["EMULATORWORKSPACE_SUPERVISOR_EXE"] = Join-Path $verified.payload "EmulatorWorkspace.Supervisor.exe"
    }
    $desktopProcess = [System.Diagnostics.Process]::Start($launch)
    if (-not $desktopProcess.WaitForExit(300000)) {
        $desktopProcess.Kill()
        $desktopProcess.WaitForExit(15000) | Out-Null
        throw "WPF latency probe exceeded five minutes."
    }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw "The desktop did not write presentation evidence." }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Check "presentation.coverage" ($result.valid -ge 20) "$($result.valid)/$($result.trials) valid samples"
    Check "presentation.latency" ($result.valid -ge 20 -and $result.p95Ms -le 100) "p95=$($result.p95Ms) ms"
    Check "presentation.throughput" ($result.deliveredFps -ge 60) "viewport-delivered FPS=$($result.deliveredFps), see workload evidence before evaluating the release gate"
}
catch { Check "workflow" $false $_.Exception.Message }
finally {
    if ($owned) {
        try {
            Invoke-Wsctl @("stop", "--force") | Out-Null
            Invoke-Wsctl @("destroy") | Out-Null
            Invoke-Wsctl @("supervisor-stop") | Out-Null
            Check "cleanup" $true $Name
        }
        catch { Check "cleanup" $false $_.Exception.Message }
    }
    [ordered]@{
        runId = $run
        finishedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        sourceCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
        candidate = $candidate
        desktopSha256 = $desktopSha256
        desktopOverride = [bool]$DesktopExecutable
        checks = $checks.ToArray()
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out "summary.json") -Encoding UTF8
    Write-Host "Evidence: $out"
}
if ($checks | Where-Object { -not $_.passed }) { exit 2 }
