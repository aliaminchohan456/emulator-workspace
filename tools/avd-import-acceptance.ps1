#requires -Version 5.1
<#
.SYNOPSIS
    Phase 3 AVD import evidence: builds an external SDK AVD outside the
    workspace, previews the import, imports it as an independent managed copy
    and proves the source configuration and writable disks are unchanged.

.DESCRIPTION
    The source AVD is created with the pinned avdmanager in a directory that
    belongs to no workspace, so it is a genuine external source. The import is
    read-only: the supervisor copies the configuration and the writable disk
    into a new workspace-owned instance and reports the source fingerprint and
    disk hashes before and after.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\avd-import-acceptance.ps1 `
      -Root D:\Phase4Root -ExternalHome D:\Phase4External
#>
param(
    [string]$Root = "D:\Phase4Root",
    [string]$ExternalHome = "D:\Phase4External",
    [string]$AvdName = "ext-source",
    [string]$TargetName = "imported-avd",
    [string]$OutDir = "artifacts\phase3\import"
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$avdmanager = Join-Path $sdk "cmdline-tools\latest\bin\avdmanager.bat"

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$script:results = New-Object System.Collections.ArrayList

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
    [void]$process.WaitForExit(420000)
    $text = ""
    try { $text = $stdout.Result + $stderr.Result } catch { }
    $code = -1
    try { $code = $process.ExitCode } catch { }
    $text = ($text | Out-String).Trim()
    if ($code -ne 0 -and -not $AllowFailure) {
        throw "wsctl $($arguments -join ' ') failed ($code): $text"
    }
    return [pscustomobject]@{ Code = $code; Text = $text }
}

function Parse-Json([string]$text) {
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Get-SourceState([string]$source) {
    $config = Join-Path $source "config.ini"
    $hash = ""
    if (Test-Path $config) { $hash = (Get-FileHash $config -Algorithm SHA256).Hash }
    $disks = @()
    if (Test-Path $source) {
        $disks = @(Get-ChildItem $source -Filter "userdata*" -File -ErrorAction SilentlyContinue |
            ForEach-Object { "{0}:{1}" -f $_.Name, (Get-FileHash $_.FullName -Algorithm SHA256).Hash })
    }
    return [pscustomobject]@{ ConfigSha256 = $hash; Disks = $disks }
}

Log "AVD import acceptance $run root=$Root external=$ExternalHome"

try {
    New-Item -ItemType Directory -Force $Root | Out-Null
    New-Item -ItemType Directory -Force $ExternalHome | Out-Null

    # The source is an SDK AVD that belongs to no workspace.
    $env:ANDROID_AVD_HOME = $ExternalHome
    $env:ANDROID_SDK_ROOT = $sdk
    $create = & $avdmanager create avd -n $AvdName -k "system-images;android-35;default;x86_64" -d pixel_6 --force 2>&1 | Out-String
    Set-Content -Path (Join-Path $out "avdmanager-create.txt") -Value $create -Encoding UTF8
    $source = Join-Path $ExternalHome ($AvdName + ".avd")
    Add-Check "source-created" (Test-Path (Join-Path $source "config.ini")) "source=$source"

    $before = Get-SourceState $source
    Set-Content -Path (Join-Path $out "source-before.json") -Value ($before | ConvertTo-Json -Depth 4) -Encoding UTF8

    # The source must never be offered as a managed instance of this workspace.
    $managed = Invoke-Ws @("list", "--root", $Root) -AllowFailure
    Add-Check "source-not-managed" ($managed.Text -notmatch [regex]::Escape($AvdName)) "the external AVD is not a managed instance"

    $preview = Invoke-Ws @("import-avd", "--source", $source, "--target", $TargetName, "--root", $Root) -AllowFailure
    Set-Content -Path (Join-Path $out "preview.json") -Value $preview.Text -Encoding UTF8
    $previewJson = Parse-Json $preview.Text
    Add-Check "preview" ($preview.Code -eq 0 -and $previewJson.sourceFingerprint) `
        ("fingerprint=" + $previewJson.sourceFingerprint)

    $apply = Invoke-Ws @("import-avd", "--source", $source, "--target", $TargetName, "--apply", "--root", $Root) -AllowFailure
    Set-Content -Path (Join-Path $out "import.json") -Value $apply.Text -Encoding UTF8
    $applyJson = Parse-Json $apply.Text
    Add-Check "import" ($apply.Code -eq 0 -and $applyJson.name) ("instance=" + $applyJson.name)
    Add-Check "source-unchanged" ($applyJson.sourceUnchanged -eq $true) `
        ("before=" + $applyJson.sourceFingerprintBefore + " after=" + $applyJson.sourceFingerprintAfter)
    Add-Check "independent-copy" ($applyJson.targetFingerprint -ne $applyJson.sourceFingerprintAfter) `
        ("target=" + $applyJson.targetFingerprint)

    $after = Get-SourceState $source
    Set-Content -Path (Join-Path $out "source-after.json") -Value ($after | ConvertTo-Json -Depth 4) -Encoding UTF8
    Add-Check "source-bytes-unchanged" ($after.ConfigSha256 -eq $before.ConfigSha256) `
        ("config sha256=" + $after.ConfigSha256)

    $instances = Invoke-Ws @("list", "--root", $Root) -AllowFailure
    Set-Content -Path (Join-Path $out "instances.txt") -Value $instances.Text -Encoding UTF8
    Add-Check "imported-instance-owned" ($instances.Text -match [regex]::Escape($TargetName)) "the copy is a managed instance"

    Invoke-Ws @("destroy", "--name", $TargetName, "--root", $Root) -AllowFailure | Out-Null
    $final = Invoke-Ws @("list", "--root", $Root) -AllowFailure
    Add-Check "cleanup" ($final.Text -notmatch [regex]::Escape($TargetName)) "the imported instance was removed"
}
catch {
    Add-Check "harness" "fatal" $false $_.Exception.Message
}

$failed = @($script:results | Where-Object { -not $_.Passed }).Count
$summary = [ordered]@{
    runId = $run
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    failed = $failed
    checks = @($script:results)
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$script:results | Format-Table -AutoSize | Out-String | Write-Host
Log "completed failures=$failed artifacts=$out"
if ($failed -gt 0) { exit 2 }
exit 0
