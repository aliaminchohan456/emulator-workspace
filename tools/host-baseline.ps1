#requires -Version 5.1
<#
.SYNOPSIS
    Collects the Phase 0 host baseline and fails loudly on a missing prerequisite.

.DESCRIPTION
    Runs the workspace CLI's own diagnostics, then checks the facts Phase 0
    depends on: a usable WHPX accelerator, enough RAM and disk for two guests,
    and an installed API 35 x86_64 AOSP image. Writes JSON and Markdown copies
    into the artifact directory so a reviewer can diff hosts.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\host-baseline.ps1
#>
param(
    [string]$Sdk = $env:ANDROID_SDK_ROOT,
    [string]$Root,
    [string]$OutDir = "artifacts",
    [int]$MinimumFreeGiB = 10,
    [double]$MinimumRamGiB = 12
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"

if (-not (Test-Path $wsctl)) {
    Write-Host "Building workspace CLI..."
    Push-Location $repo
    try { & dotnet build -v q --nologo | Out-Host } finally { Pop-Location }
}

if (-not (Test-Path $wsctl)) { throw "wsctl.exe not found at $wsctl" }

$outPath = Join-Path $repo $OutDir
New-Item -ItemType Directory -Force $outPath | Out-Null
$jsonPath = Join-Path $outPath "host-baseline.json"

$arguments = @("doctor", "--json", $jsonPath)
if ($Sdk) { $arguments += @("--sdk", $Sdk) }
if ($Root) { $arguments += @("--root", $Root) }

& $wsctl @arguments
if ($LASTEXITCODE -ne 0) { throw "wsctl doctor failed with exit code $LASTEXITCODE" }

$report = Get-Content $jsonPath -Raw | ConvertFrom-Json

$checks = @()
function Add-Check([string]$Name, [bool]$Passed, [string]$Detail, [string]$Action) {
    $script:checks += [pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail; Action = $Action }
}

Add-Check "WHPX accelerator" `
    ($report.accelerationVerdict -match "usable") `
    $report.accelerationVerdict `
    "Enable the Windows Hypervisor Platform feature (requires elevation and a reboot)."

Add-Check "Physical memory" `
    ($report.totalMemoryGiB -ge $MinimumRamGiB) `
    ("{0:N1} GiB total, {1:N1} GiB available" -f $report.totalMemoryGiB, $report.availableMemoryGiB) `
    "Free memory or use one guest at a time."

Add-Check "Free disk" `
    ($report.workspaceDriveFreeGiB -ge $MinimumFreeGiB) `
    ("{0:N1} GiB free" -f $report.workspaceDriveFreeGiB) `
    "Free disk space; each AVD needs about 2 GiB plus 2 GiB per snapshot."

$images = @($report.androidSdk.images)
$imageMatches = @($images | Where-Object { $_.packageId -eq "system-images;android-35;default;x86_64" })
Add-Check "AOSP API 35 image" `
    ($imageMatches.Count -eq 1) `
    $(if ($imageMatches.Count -eq 1) { "rev " + $imageMatches[0].revision } else { "not installed" }) `
    "Run: sdkmanager ""system-images;android-35;default;x86_64"""

Add-Check "Emulator binary" `
    ($report.androidSdk.emulatorVersion -match "^\d+\.\d+\.\d+") `
    $report.androidSdk.emulatorVersion `
    "Install the emulator SDK package."

Write-Host ""
Write-Host "Host baseline"
Write-Host "-------------"
Write-Host ("OS            : " + $report.operatingSystem.full)
Write-Host ("CPU           : " + $report.processorName)
Write-Host ("Memory        : {0:N1} GiB" -f $report.totalMemoryGiB)
Write-Host ("GPU           : " + $report.gpu.name + " / " + $report.gpu.driverVersion)
Write-Host ("Acceleration  : " + $report.accelerationVerdict)
Write-Host ("Emulator      : " + $report.androidSdk.emulatorVersion)
Write-Host ("ADB           : " + $report.androidSdk.adbVersion)
Write-Host ""

$failed = @($checks | Where-Object { -not $_.Passed })
foreach ($check in $checks) {
    $mark = if ($check.Passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1}: {2}" -f $mark, $check.Name, $check.Detail)
    if (-not $check.Passed) { Write-Host ("       action: " + $check.Action) }
}

$md = @()
$md += "# Host baseline"
$md += ""
$md += "- Collected: " + $report.collectedUtc
$md += "- Machine: " + $report.machineName + " / " + $report.operatingSystem.full
$md += "- CPU: " + $report.processorName + " (" + $report.logicalProcessors + " logical)"
$md += "- Memory: {0:N1} GiB total, {1:N1} GiB available" -f $report.totalMemoryGiB, $report.availableMemoryGiB
$md += "- GPU: " + $report.gpu.name + " driver " + $report.gpu.driverVersion
$md += "- Acceleration: " + $report.accelerationVerdict
$md += "- Hyper-V: " + $report.hyperV
$md += "- Emulator: " + $report.androidSdk.emulatorVersion
$md += "- ADB: " + $report.androidSdk.adbVersion
$md += "- SDK: " + $report.androidSdk.root
$md += ""
$md += "| Check | Result | Detail |"
$md += "| --- | --- | --- |"
foreach ($check in $checks) {
    $md += ("| {0} | {1} | {2} |" -f $check.Name, $(if ($check.Passed) { "pass" } else { "fail" }), $check.Detail)
}
$md | Set-Content (Join-Path $outPath "host-baseline.md") -Encoding UTF8

Write-Host ""
Write-Host ("Wrote " + $jsonPath)
Write-Host ("Wrote " + (Join-Path $outPath "host-baseline.md"))

if ($failed.Count -gt 0) {
    Write-Error ("Host baseline has " + $failed.Count + " failing prerequisite(s).")
    exit 2
}

Write-Host "Host baseline passed."
exit 0
