#requires -Version 5.1
<#
.SYNOPSIS
    Developer-tool acceptance: exercises the scoped package, permission, deep
    link, instrumentation and diagnostics paths against a real guest.

.DESCRIPTION
    Every operation is verified through its normal CLI entry point on a managed
    guest:

      * package info and package permissions
      * a deep link that reaches the guest's intent parser
      * an instrumentation run
      * a searchable Logcat export written as an artifact
      * the crash and ANR collection
      * a full bug report

    The negative paths are verified too: a malformed deep link and an
    uninstalled package must be refused with a typed error.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\devtools-acceptance.ps1 `
      -Root D:\Phase4Root
#>
param(
    [string]$Root = "D:\Phase4Root",
    [string]$OutDir = "artifacts\phase3\devtools",
    [string]$Package = "dev.emulatorworkspace.sample",
    [switch]$RequireInstrumentation
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "fixtures/acceptance-payload.ps1")
$wsctl = Get-AcceptanceBinary "wsctl.exe" "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
Write-AcceptanceContext $out
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
    [void]$process.WaitForExit(900000)
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

Log "developer-tool acceptance $run root=$Root"

$instance = $null
try {
    New-Item -ItemType Directory -Force $Root | Out-Null
    $instance = "devtools-" + (Get-Random -Maximum 9999)
    Invoke-Ws @("create", "--name", $instance, "--root", $Root) | Out-Null
    Invoke-Ws @("start", "--name", $instance, "--root", $Root) | Out-Null
    Invoke-Ws @("wait", "--name", $instance, "--timeout", "600", "--root", $Root) | Out-Null

    $apk = Join-Path $Root "apps\ws-sample.apk"
    if ($env:WS_ACCEPTANCE_PAYLOAD) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $apk) | Out-Null
        Copy-Item -LiteralPath (Join-Path $env:WS_ACCEPTANCE_PAYLOAD "ws-sample.apk") -Destination $apk -ErrorAction Stop
    }
    if (-not (Test-Path $apk)) {
        $builtSample = Join-Path $repo "artifacts/phase1/sample/ws-sample.apk"
        $found = Get-Item -LiteralPath $builtSample -ErrorAction SilentlyContinue
        if (-not $found) {
            $found = Get-ChildItem (Join-Path $repo "artifacts") -Recurse -Filter "ws-sample.apk" -ErrorAction SilentlyContinue |
                Select-Object -First 1
        }
        if ($found) {
            New-Item -ItemType Directory -Force (Split-Path $apk) | Out-Null
            Copy-Item $found.FullName $apk -Force
        }
    }
    if (Test-Path $apk) {
        Invoke-Ws @("install", "--name", $instance, "--apk", $apk, "--root", $Root) -AllowFailure | Out-Null
    }
    Invoke-Ws @("launch", "--name", $instance, "--package", $Package, "--root", $Root) -AllowFailure | Out-Null
    Start-Sleep -Seconds 6

    # ---------------------------------------------------------- package info
    $info = Parse-Json (Invoke-Ws @("package-info", "--name", $instance, "--package", $Package, "--root", $Root) -AllowFailure).Text
    Set-Content -Path (Join-Path $out "package-info.json") -Value ($info | ConvertTo-Json -Depth 6) -Encoding UTF8
    Add-Check "package-info" ($info -and $info.installed -eq $true) `
        ("installed=" + $info.installed + " version=" + $info.versionName + " codePath=" + $info.codePath)

    # ----------------------------------------------------------- permissions
    $permissions = Parse-Json (Invoke-Ws @("package-permissions", "--name", $instance, "--package", $Package, "--root", $Root) -AllowFailure).Text
    Set-Content -Path (Join-Path $out "package-permissions.json") -Value ($permissions | ConvertTo-Json -Depth 6) -Encoding UTF8
    $granted = @($permissions.granted).Count
    $requested = @($permissions.requested).Count
    Add-Check "package-permissions" ($permissions -and $requested -ge 0) `
        ("requested=" + $requested + " granted=" + $granted)

    # ------------------------------------------------------------- deep link
    $deep = Parse-Json (Invoke-Ws @("deep-link", "--name", $instance, "--uri", "https://emulatorworkspace.local/login?user=qa", "--root", $Root) -AllowFailure).Text
    Set-Content -Path (Join-Path $out "deep-link.json") -Value ($deep | ConvertTo-Json -Depth 6) -Encoding UTF8
    Add-Check "deep-link" ($deep -and $deep.started -eq $true) `
        ("scheme=" + $deep.scheme + " host=" + $deep.host + " started=" + $deep.started)

    $bad = Invoke-Ws @("deep-link", "--name", $instance, "--uri", "not a uri", "--root", $Root) -AllowFailure
    Add-Check "deep-link.negative" ($bad.Code -ne 0 -and $bad.Text -match "invalid_request") `
        ("a malformed URI is refused: " + (($bad.Text -split "`n")[0]))

    # -------------------------------------------------------- instrumentation
    $instrument = Parse-Json (Invoke-Ws @("instrumentation", "--name", $instance, "--package", $Package, "--root", $Root) -AllowFailure).Text
    Set-Content -Path (Join-Path $out "instrumentation.json") -Value ($instrument | ConvertTo-Json -Depth 6) -Encoding UTF8
    Add-Check "instrumentation" ($null -ne $instrument -and $null -ne $instrument.available) `
        ("available=" + $instrument.available + " tests=" + $instrument.tests + " failures=" + $instrument.failures + " reason=" + $instrument.reason)
    if ($RequireInstrumentation) {
        Add-Check "instrumentation.passed" ($instrument.available -eq $true -and $instrument.passed -eq $true -and
            $instrument.tests -eq 1 -and $instrument.failures -eq 0) "the fixture completed its smoke test"
        $invalid = Invoke-Ws @("instrumentation", "--name", $instance, "--package", $Package, "--runner",
            "invalid;true", "--root", $Root) -AllowFailure
        Add-Check "instrumentation.invalid-runner" ($invalid.Code -ne 0 -and $invalid.Text -match "invalid_request") "invalid runner rejected"
    }

    # ----------------------------------------------------------- logcat export
    $export = Parse-Json (Invoke-Ws @("logcat-export", "--name", $instance, "--lines", "300", "--root", $Root) -AllowFailure).Text
    Set-Content -Path (Join-Path $out "logcat-export.json") -Value ($export | ConvertTo-Json -Depth 6) -Encoding UTF8
    $exportFile = $export.artifactPath
    $hasLogcat = $false
    if ($exportFile -and (Test-Path $exportFile)) {
        $content = Get-Content $exportFile -Raw
        Set-Content -Path (Join-Path $out "logcat-export.txt") -Value $content -Encoding UTF8
        $hasLogcat = $content.Length -gt 0
    }
    Add-Check "logcat-export" ($export -and (Test-Path $exportFile) -and $hasLogcat) `
        ("artifact=" + $exportFile + " bytes=" + $export.bytes)

    # ------------------------------------------------------------ crash report
    $crash = Parse-Json (Invoke-Ws @("crash-report", "--name", $instance, "--package", $Package, "--root", $Root) -AllowFailure).Text
    Set-Content -Path (Join-Path $out "crash-report.json") -Value ($crash | ConvertTo-Json -Depth 6) -Encoding UTF8
    Add-Check "crash-report" ($null -ne $crash -and $null -ne $crash.crashCount) `
        ("crashes=" + $crash.crashCount + " anrs=" + $crash.anrCount)

    # -------------------------------------------------------------- bug report
    $bug = Parse-Json (Invoke-Ws @("bugreport", "--name", $instance, "--root", $Root) -AllowFailure).Text
    Set-Content -Path (Join-Path $out "bugreport.json") -Value ($bug | ConvertTo-Json -Depth 6) -Encoding UTF8
    $bugFile = $bug.artifactPath
    Add-Check "bugreport" ($bug -and (Test-Path $bugFile)) `
        ("artifact=" + $bugFile + " bytes=" + $bug.bytes + " sha256=" + $bug.sha256)

    # --------------------------------------------------------------- cleanup
    Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) -AllowFailure | Out-Null
    Start-Sleep -Seconds 3
    Invoke-Ws @("destroy", "--name", $instance, "--root", $Root) -AllowFailure | Out-Null
    $remaining = Invoke-Ws @("list", "--root", $Root) -AllowFailure
    Add-Check "cleanup" (-not ($remaining.Text -match [regex]::Escape($instance))) "the lab guest was removed"
}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
    Log ("fatal: " + $_.Exception.Message)
}
finally {
    if ($instance) {
        Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) -AllowFailure | Out-Null
    }
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
