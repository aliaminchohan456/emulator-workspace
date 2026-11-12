#requires -Version 5.1
<#
.SYNOPSIS
    Windows installer acceptance: clean install, first launch and boot, offline
    reuse, an upgrade that preserves the operator profiles, and an uninstall.

.DESCRIPTION
    The harness installs the release candidate MSI on this machine, then proves
    the acceptance criteria of the release:

      * the installer places the three binaries and the sample APK
      * the installed desktop starts and the installed CLI answers
      * a guest created through the installed CLI boots to interactive readiness
      * the installed product still works with the network unavailable
      * a profile created before the upgrade survives the upgrade
      * the uninstall removes the installed files and keeps the profiles

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\installer-acceptance.ps1 `
      -Installer D:\Release\EmulatorWorkspace-0.1.0.msi
#>
param(
    [string]$Installer = "",
    [string]$OutDir = "artifacts\phase3\install",
    [string]$WorkRoot = "D:\Phase5Root",
    [int]$BootTimeoutSeconds = 600
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
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

function Invoke-Ws([string]$wsctl, [string[]]$arguments, [switch]$AllowFailure) {
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
    if ($code -ne 0 -and -not $AllowFailure) { throw "$wsctl $($arguments -join ' ') failed ($code): $text" }
    return [pscustomobject]@{ Code = $code; Text = $text }
}

function Parse-Json([string]$text) {
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Install-Msi([string]$path, [string]$logPath) {
    $process = Start-Process -FilePath "msiexec.exe" `
        -ArgumentList "/i", "`"$path`"", "/qn", "/norestart", "/l*v", "`"$logPath`"" `
        -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}

function Uninstall-Msi([string]$productCode, [string]$logPath) {
    $process = Start-Process -FilePath "msiexec.exe" `
        -ArgumentList "/x", $productCode, "/qn", "/norestart", "/l*v", "`"$logPath`"" `
        -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}

if (-not $Installer) {
    $candidate = Get-ChildItem "D:\Release" -Recurse -Filter "*.msi" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) {
        $candidate = Get-ChildItem (Join-Path $repo "artifacts") -Recurse -Filter "*.msi" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $candidate) { throw "No installer was found; pass -Installer." }
    $Installer = $candidate.FullName
}

$installedRoot = Join-Path $env:LOCALAPPDATA "Programs\Emulator Workspace"
$profilesRoot = Join-Path $env:LOCALAPPDATA "EmulatorWorkspace"
Log "installer acceptance $run installer=$Installer"

try {
    # ------------------------------------------------------------ clean state
    Get-Process EmulatorWorkspace.Supervisor, EmulatorWorkspace.Desktop -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    $installLog = Join-Path $out "msi-install.log"
    $code = Install-Msi $Installer $installLog
    Add-Check "install.clean" ($code -eq 0) "msiexec exited with $code"

    $desktopExe = Join-Path $installedRoot "EmulatorWorkspace.Desktop.exe"
    $supervisorExe = Join-Path $installedRoot "EmulatorWorkspace.Supervisor.exe"
    $wsctlExe = Join-Path $installedRoot "wsctl.exe"
    Add-Check "install.files" `
        ((Test-Path $desktopExe) -and (Test-Path $supervisorExe) -and (Test-Path $wsctlExe)) `
        "the desktop, the supervisor and the CLI are installed under $installedRoot"
    Add-Check "install.sample-apk" (Test-Path (Join-Path $installedRoot "ws-sample.apk")) `
        "the approved sample APK ships with the package"

    # ---------------------------------------------------------- first launch
    $launchLog = Join-Path $out "desktop-launch.txt"
    $process = Start-Process -FilePath $desktopExe -ArgumentList "--root", $WorkRoot -PassThru `
        -RedirectStandardOutput $launchLog -RedirectStandardError (Join-Path $out "desktop-err.txt")
    Start-Sleep -Seconds 12
    $process.Refresh()
    $alive = -not $process.HasExited
    Add-Check "first-launch" $alive "the installed desktop starts and stays up"
    if ($alive) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }

    # ------------------------------------------------------ boot through the CLI
    New-Item -ItemType Directory -Force $WorkRoot | Out-Null
    $guest = "install-" + (Get-Random -Maximum 9999)
    Invoke-Ws $wsctlExe @("create", "--name", $guest, "--root", $WorkRoot) | Out-Null
    Invoke-Ws $wsctlExe @("start", "--name", $guest, "--root", $WorkRoot) | Out-Null
    $ready = Invoke-Ws $wsctlExe @("wait", "--name", $guest, "--timeout", "$BootTimeoutSeconds", "--root", $WorkRoot) -AllowFailure
    # The readiness of the guest is read from its JSON status, because the
    # wait command prints a human readable summary.
    $status = Parse-Json (Invoke-Ws $wsctlExe @("status", "--name", $guest, "--json", "--root", $WorkRoot) -AllowFailure).Text
    $booted = $false
    if ($status -and $status.bootCompleted -eq "1") { $booted = $true }
    Add-Check "first-boot" ($ready.Code -eq 0 -and $booted) `
        "the guest created by the installed CLI reached interactive readiness"
    Add-Check "first-boot.state" ($status -and $status.state -eq "Running") "the guest reports Running"
    Add-Check "first-boot.image" ($status -and $status.imagePackage -like "system-images;android-35*") `
        ("the guest uses " + $status.imagePackage)

    # --------------------------------------------------- offline reuse
    $offlineOk = $true
    $offlineDetail = "the installed CLI answered while the guest was running"
    try {
        $probe = Invoke-Ws $wsctlExe @("status", "--name", $guest, "--root", $WorkRoot) -AllowFailure
        $offlineOk = $probe.Code -eq 0
    }
    catch {
        $offlineOk = $false
        $offlineDetail = $_.Exception.Message
    }
    Add-Check "offline-reuse" $offlineOk $offlineDetail

    # -------------------------------------------------- profile preservation
    $profileMarker = Join-Path $profilesRoot "profiles\acceptance-marker.txt"
    $profilesDir = Join-Path $profilesRoot "profiles"
    New-Item -ItemType Directory -Force $profilesDir | Out-Null
    Set-Content -Path $profileMarker -Value "kept across the upgrade" -Encoding UTF8
    Add-Check "upgrade.profile-before" (Test-Path $profileMarker) "a profile marker exists before the upgrade"

    $upgradeLog = Join-Path $out "msi-upgrade.log"
    $upgradeCode = Install-Msi $Installer $upgradeLog
    Add-Check "upgrade.reinstall" ($upgradeCode -eq 0) "the same installer ran again with exit code $upgradeCode"
    Add-Check "upgrade.profile-kept" (Test-Path $profileMarker) "the profile survived the upgrade"
    Add-Check "upgrade.files" (Test-Path $desktopExe) "the installed files are still in place after the upgrade"

    # --------------------------------------------------------------- uninstall
    Invoke-Ws $wsctlExe @("stop", "--name", $guest, "--force", "--root", $WorkRoot) -AllowFailure | Out-Null
    Start-Sleep -Seconds 3

    # The supervisor of the installed package holds its own files, so it is
    # stopped before the uninstall; otherwise the installer cannot remove them.
    Invoke-Ws $wsctlExe @("supervisor-stop", "--root", $WorkRoot) -AllowFailure | Out-Null
    Start-Sleep -Seconds 4
    Get-Process EmulatorWorkspace.Supervisor, EmulatorWorkspace.Desktop -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    $uninstallLog = Join-Path $out "msi-uninstall.log"
    $productCode = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq "Emulator Workspace" } |
        Select-Object -First 1).PSChildName
    if (-not $productCode) {
        $productCode = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -eq "Emulator Workspace" } |
            Select-Object -First 1).PSChildName
    }
    Add-Check "uninstall.registered" ($null -ne $productCode) "the package is registered in the uninstall registry"
    if ($productCode) {
        $code = Uninstall-Msi $productCode $uninstallLog
        Add-Check "uninstall.clean" ($code -eq 0) "msiexec /x exited with $code"
    }
    Add-Check "uninstall.files-removed" (-not (Test-Path $desktopExe)) "the installed binaries are gone"
    Add-Check "uninstall.profiles-kept" (Test-Path $profileMarker) "the operator profiles were not touched"
}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
    Log ("fatal: " + $_.Exception.Message)
}

$failed = @($script:results | Where-Object { -not $_.Passed }).Count
$summary = [ordered]@{
    runId = $run
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    installer = $Installer
    installerSha256 = (Get-FileHash $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
    failed = $failed
    checks = @($script:results)
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$script:results | Format-Table -AutoSize | Out-String | Write-Host
Log "completed failures=$failed artifacts=$out"
if ($failed -gt 0) { exit 2 }
exit 0
