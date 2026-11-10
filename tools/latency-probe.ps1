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
    [int]$Count = 25,
    [string]$Gpu,
    [string]$OutDir = "artifacts\phase1-1",
    [int]$BootTimeoutSeconds = 600
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$desktop = Join-Path $repo "src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"

if (-not (Test-Path $wsctl) -or -not (Test-Path $desktop)) { throw "Build the solution first." }

function Invoke-Wsctl([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { $output = & $wsctl @Arguments 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    return [pscustomobject]@{ ExitCode = $code; Output = ($output -join "`n") }
}

$status = Invoke-Wsctl @("status", "--name", $Name, "--root", $Root)
if ($status.Output -notmatch "engineAlive=true") {
    if ($Gpu) { Invoke-Wsctl @("start", "--name", $Name, "--gpu", $Gpu, "--root", $Root) | Out-Null }
    else { Invoke-Wsctl @("start", "--name", $Name, "--root", $Root) | Out-Null }
    Invoke-Wsctl @("wait", "--name", $Name, "--timeout", $BootTimeoutSeconds, "--root", $Root) | Out-Null
}

$serial = ([regex]::Match($status.Output, "serial=(emulator-\d+)")).Groups[1].Value
if (-not $serial) {
    $status = Invoke-Wsctl @("status", "--name", $Name, "--root", $Root)
    $serial = ([regex]::Match($status.Output, "serial=(emulator-\d+)")).Groups[1].Value
}

& $adb -s $serial shell input keyevent KEYCODE_WAKEUP | Out-Null
& $adb -s $serial shell settings put global screen_off_timeout 2147483647 | Out-Null
Invoke-Wsctl @("launch", "--name", $Name, "--package", "dev.emulatorworkspace.sample", "--root", $Root) | Out-Null
Start-Sleep -Seconds 3

Get-Process EmulatorWorkspace.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $repo $OutDir) | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir "latency-$Name-$stamp.json")

$process = Start-Process -FilePath $desktop -PassThru -ArgumentList @(
    "--root", $Root, "--latency-probe", $Count, "--out", $out)
$finished = $process.WaitForExit(300000)
if (-not $finished) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }

Start-Sleep -Seconds 1
if (Test-Path $out) {
    $summary = Get-Content $out -Raw | ConvertFrom-Json
    Write-Host ("latency {0}: mode={1} valid={2}/{3} timeouts={4} p50={5}ms p95={6}ms fps={7}" -f `
        $Name, $summary.mode, $summary.valid, $summary.trials, $summary.timeouts, `
        $summary.p50Ms, $summary.p95Ms, $summary.deliveredFps)
    Write-Host ("artifacts: " + $out)
    if ($summary.valid -lt [Math]::Ceiling($Count * 0.8)) { exit 2 }
    exit 0
}

Write-Error "Probe did not produce $out"
exit 1
