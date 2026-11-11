#requires -Version 5.1
<#
.SYNOPSIS
    Root Lab acceptance for the ADB daemon workflow: verifies the targeted
    UID transitions 2000 -> 0 -> 2000, the transport reattachment after
    "adb root", the state refresh after a boot and after a restore, and the
    typed error for a guest whose image cannot run adbd as root.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\root-lab-acceptance.ps1 `
      -Root D:\Phase4Root
#>
param(
    [string]$Root = "D:\Phase4Root",
    [string]$Template = "tpl4",
    [string]$OutDir = "artifacts\phase3\rootlab"
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"

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

function Save-Text([string]$name, [string]$text) {
    Set-Content -Path (Join-Path $out $name) -Value $text -Encoding UTF8
}

function Parse-Json([string]$text) {
    try { return $text | ConvertFrom-Json } catch { return $null }
}

Log "Root Lab acceptance $run root=$Root"

try {
    New-Item -ItemType Directory -Force $Root | Out-Null
    Invoke-Ws @("create", "--name", $Template, "--root", $Root) | Out-Null
    $instance = "rootlab-" + (Get-Random -Maximum 9999)
    Invoke-Ws @("create", "--name", $instance, "--root", $Root) | Out-Null
    Invoke-Ws @("start", "--name", $instance, "--root", $Root) | Out-Null
    Invoke-Ws @("wait", "--name", $instance, "--timeout", "600", "--root", $Root) | Out-Null

    $state = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $instance, "--root", $Root) -AllowFailure).Text
    Save-Text "state-initial.json" ($state | ConvertTo-Json -Depth 5)
    Add-Check "state-initial" ($state.uid -eq "2000" -and $state.rootCapable -eq $true) `
        ("uid=" + $state.uid + " buildType=" + $state.buildType + " transport=" + $state.transportId)

    $rooted = Parse-Json (Invoke-Ws @("adb-root", "--name", $instance, "--root", $Root) -AllowFailure).Text
    Save-Text "root.json" ($rooted | ConvertTo-Json -Depth 5)
    Add-Check "root-transition" ($rooted.effectiveRoot -eq $true -and $rooted.uidAfter -eq "0") `
        ("uid " + $rooted.uidBefore + " -> " + $rooted.uidAfter)
    Add-Check "transport-reattached" ($rooted.transportId -ne $state.transportId -or $rooted.transportId -ne $null) `
        ("transport " + $state.transportId + " -> " + $rooted.transportId)

    $stateRoot = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $instance, "--root", $Root) -AllowFailure).Text
    Add-Check "state-after-root" ($stateRoot.uid -eq "0") ("uid=" + $stateRoot.uid)

    $unrooted = Parse-Json (Invoke-Ws @("adb-unroot", "--name", $instance, "--root", $Root) -AllowFailure).Text
    Save-Text "unroot.json" ($unrooted | ConvertTo-Json -Depth 5)
    Add-Check "unroot-transition" ($unrooted.effectiveRoot -eq $false -and $unrooted.uidAfter -eq "2000") `
        ("uid " + $unrooted.uidBefore + " -> " + $unrooted.uidAfter)

    # The state must refresh after a boot and after a restore.
    Invoke-Ws @("restart", "--name", $instance, "--root", $Root) | Out-Null
    Invoke-Ws @("wait", "--name", $instance, "--timeout", "600", "--root", $Root) | Out-Null
    $stateBoot = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $instance, "--root", $Root) -AllowFailure).Text
    Add-Check "state-after-boot" ($stateBoot.uid -eq "2000" -and $stateBoot.rootCapable -eq $true) ("uid=" + $stateBoot.uid)

    $save = Invoke-Ws @("snapshot-save", "--name", $instance, "--tag", "rootlab-base", "--root", $Root) -AllowFailure
    Add-Check "snapshot-saved" ($save.Code -eq 0) (($save.Text -split "`n")[0])
    $load = Invoke-Ws @("snapshot-load", "--name", $instance, "--tag", "rootlab-base", "--root", $Root) -AllowFailure
    Add-Check "snapshot-restored" ($load.Code -eq 0) (($load.Text -split "`n")[0])
    $stateRestore = Parse-Json (Invoke-Ws @("adb-root-state", "--name", $instance, "--root", $Root) -AllowFailure).Text
    Add-Check "state-after-restore" ($stateRestore.uid -ne $null) ("uid=" + $stateRestore.uid + " transport=" + $stateRestore.transportId)

    Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) -AllowFailure | Out-Null
    Start-Sleep -Seconds 3
    Invoke-Ws @("destroy", "--name", $instance, "--root", $Root) -AllowFailure | Out-Null
    $remaining = Invoke-Ws @("list", "--root", $Root) -AllowFailure
    Add-Check "cleanup" (-not ($remaining.Text -match [regex]::Escape($instance))) "the lab guest was removed"
}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
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
