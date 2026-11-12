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
    [int]$BootTimeoutSeconds = 600,
    [switch]$VerifySnapshotJson,
    [switch]$VerifySimulationProbe,
    [switch]$VerifyDisconnectedReuse,
    [string]$PreviousInstaller = ""
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
    $path = (Resolve-Path -LiteralPath $path -ErrorAction Stop).ProviderPath
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

function Read-InstallerIdentity([string]$path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase((Resolve-Path -LiteralPath $path).Path, 0)
    $view = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
    $identity = @{}
    try {
        $view.Execute()
        while ($record = $view.Fetch()) {
            $property = $record.StringData(1)
            if ($property -in @("ProductCode", "ProductVersion", "UpgradeCode")) {
                $identity[$property] = $record.StringData(2)
            }
        }
    }
    finally { $view.Close() }
    if ($identity.Count -ne 3) { throw "Could not read the MSI product identity." }
    return $identity
}

function Get-WorkspaceRegistration([string]$productCode) {
    $keys = @(
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$productCode",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$productCode"
    )
    Get-ItemProperty -LiteralPath $keys -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Assert-NetworkDisconnected([string]$stage) {
    $adapters = @(Get-NetAdapter -ErrorAction Stop | Select-Object Name, Status)
    $routes = @(Get-NetRoute -ErrorAction Stop |
        Where-Object DestinationPrefix -in @("0.0.0.0/0", "::/0") |
        Select-Object InterfaceIndex, DestinationPrefix)
    [pscustomobject]@{ atUtc = [DateTimeOffset]::UtcNow; adapters = $adapters; defaultRoutes = $routes } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out "network-$stage.json") -Encoding UTF8
    if (($adapters | Where-Object Status -eq "Up") -or $routes) {
        throw "Disconnected reuse requires the isolated Windows environment to have no active adapter or default route."
    }
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
    if ($VerifyDisconnectedReuse) { Assert-NetworkDisconnected "before-install" }
    # ------------------------------------------------------------ clean state
    $candidateIdentity = Read-InstallerIdentity $Installer
    $previousIdentity = if ($PreviousInstaller) { Read-InstallerIdentity $PreviousInstaller }
    if ($previousIdentity -and ($previousIdentity.UpgradeCode -ne $candidateIdentity.UpgradeCode -or
        [version]$previousIdentity.ProductVersion -ge [version]$candidateIdentity.ProductVersion)) {
        throw "Use a previous, lower version from the same MSI upgrade family."
    }
    if ((Get-ChildItem -LiteralPath $installedRoot -Force -ErrorAction SilentlyContinue | Select-Object -First 1) -or
        (Get-WorkspaceRegistration $candidateIdentity.ProductCode) -or
        ($previousIdentity -and (Get-WorkspaceRegistration $previousIdentity.ProductCode))) {
        throw "Clean-install acceptance requires a Windows user without an existing Emulator Workspace installation."
    }

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

    if ($VerifySnapshotJson) {
        $snapshot = Invoke-Ws $wsctlExe @("snapshot-save", "--name", $guest, "--tag", "installer-probe", "--json", "--root", $WorkRoot) -AllowFailure
        $snapshot.Text | Set-Content -LiteralPath (Join-Path $out "installed-snapshot.json") -Encoding UTF8
        $snapshotResult = Parse-Json $snapshot.Text
        Add-Check "installed.snapshot-json" ($snapshot.Code -eq 0 -and $snapshotResult.tag -eq "installer-probe" -and
            $snapshotResult.action -eq "save" -and $snapshotResult.seconds -gt 0) "the installed CLI reports structured snapshot results"
    }

    if ($VerifySimulationProbe) {
        $probe = Invoke-Ws $wsctlExe @("simulation-probe", "--name", $guest, "--root", $WorkRoot)
        $probe.Text | Set-Content -LiteralPath (Join-Path $out "installed-simulation.json") -Encoding UTF8
        $capabilities = (Parse-Json $probe.Text).capabilities
        $route = $capabilities | Where-Object capability -eq "routes and offline mode"
        $battery = $capabilities | Where-Object capability -eq "battery and charging"
        $gps = $capabilities | Where-Object capability -eq "GPS"
        Add-Check "installed.simulation-probe" ($route.supported -eq $false -and $route.reason -match '^KO:' -and
            $battery.supported -eq $true -and $battery.evidence -match 'capacity:' -and
            $gps.supported -eq $true -and $gps.evidence -eq "feature:android.hardware.location.gps") (
            "route=$($route.reason) battery=$($battery.supported) GPS=$($gps.evidence)")
    }

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
    Add-Check "local-status-reuse" $offlineOk $offlineDetail
    if ($VerifyDisconnectedReuse) {
        $package = "dev.emulatorworkspace.sample"
        $approved = Join-Path $WorkRoot "apps\offline-$guest.apk"
        New-Item -ItemType Directory -Force (Split-Path -Parent $approved) | Out-Null
        Copy-Item -LiteralPath (Join-Path $installedRoot "ws-sample.apk") -Destination $approved -ErrorAction Stop
        Invoke-Ws $wsctlExe @("install", "--name", $guest, "--apk", $approved, "--root", $WorkRoot) | Out-Null
        Invoke-Ws $wsctlExe @("stop", "--name", $guest, "--root", $WorkRoot) | Out-Null
        Invoke-Ws $wsctlExe @("start", "--name", $guest, "--root", $WorkRoot) | Out-Null
        Invoke-Ws $wsctlExe @("wait", "--name", $guest, "--timeout", "$BootTimeoutSeconds", "--root", $WorkRoot) | Out-Null
        $packageInfo = Invoke-Ws $wsctlExe @("package-info", "--name", $guest, "--package", $package, "--root", $WorkRoot)
        Invoke-Ws $wsctlExe @("launch", "--name", $guest, "--package", $package, "--root", $WorkRoot) | Out-Null
        $focus = Invoke-Ws $wsctlExe @("foreground", "--name", $guest, "--root", $WorkRoot)
        $focus.Text | Set-Content -LiteralPath (Join-Path $out "offline-foreground.txt") -Encoding UTF8
        Assert-NetworkDisconnected "after-reuse"
        Add-Check "network-disconnected-reuse" ((Parse-Json $packageInfo.Text).installed -eq $true -and
            $focus.Text.Contains($package)) "Rebooted the same profile and launched its installed fixture with Windows disconnected."
    }

    # -------------------------------------------------- profile preservation
    $profileMarker = Join-Path $profilesRoot "profiles\acceptance-$([Guid]::NewGuid().ToString('N')).txt"
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

    $uninstallLog = Join-Path $out "msi-uninstall.log"
    $productCode = (Get-WorkspaceRegistration $candidateIdentity.ProductCode).PSChildName
    Add-Check "uninstall.registered" ($null -ne $productCode) "the package is registered in the uninstall registry"
    if ($productCode) {
        $code = Uninstall-Msi $productCode $uninstallLog
        Add-Check "uninstall.clean" ($code -eq 0) "msiexec /x exited with $code"
    }
    Add-Check "uninstall.files-removed" (-not (Test-Path $desktopExe)) "the installed binaries are gone"
    Add-Check "uninstall.profiles-kept" (Test-Path $profileMarker) "the operator profiles were not touched"

    if ($PreviousInstaller) {
        if ((Get-WorkspaceRegistration $candidateIdentity.ProductCode) -or (Test-Path -LiteralPath $wsctlExe)) {
            throw "The clean-install lane must uninstall before the version-upgrade lane."
        }
        $code = Install-Msi $PreviousInstaller (Join-Path $out "msi-previous.log")
        $prior = Get-WorkspaceRegistration $previousIdentity.ProductCode
        if ($code -ne 0 -or -not $prior) { throw "Previous installer failed (exit $code)." }
        $upgradeGuest = "upgrade-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
        Invoke-Ws $wsctlExe @("create", "--name", $upgradeGuest, "--root", $WorkRoot) | Out-Null
        Invoke-Ws $wsctlExe @("supervisor-stop", "--root", $WorkRoot) | Out-Null
        Start-Sleep -Seconds 4
        $code = Install-Msi $Installer (Join-Path $out "msi-version-upgrade.log")
        $current = Get-WorkspaceRegistration $candidateIdentity.ProductCode
        Add-Check "upgrade.version-changed" ($code -eq 0 -and $current -and
            [version]$current.DisplayVersion -gt [version]$prior.DisplayVersion -and
            -not (Get-WorkspaceRegistration $previousIdentity.ProductCode)) "$($prior.DisplayVersion) -> $($current.DisplayVersion)"
        $preserved = Invoke-Ws $wsctlExe @("status", "--name", $upgradeGuest, "--json", "--root", $WorkRoot) -AllowFailure
        $profile = Parse-Json $preserved.Text
        Add-Check "upgrade.managed-profile-kept" ($preserved.Code -eq 0 -and $profile.name -eq $upgradeGuest -and
            $profile.imagePackage -eq "system-images;android-35;default;x86_64") $upgradeGuest
        Invoke-Ws $wsctlExe @("destroy", "--name", $upgradeGuest, "--root", $WorkRoot) | Out-Null
        Invoke-Ws $wsctlExe @("supervisor-stop", "--root", $WorkRoot) | Out-Null
        Start-Sleep -Seconds 4
        $code = Uninstall-Msi $current.PSChildName (Join-Path $out "msi-version-upgrade-uninstall.log")
        Add-Check "upgrade.uninstall" ($code -eq 0 -and -not (Get-WorkspaceRegistration $candidateIdentity.ProductCode)) "msiexec /x exited with $code"
    }
}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
    Log ("fatal: " + $_.Exception.Message)
}
finally {
    if ($approved -and (Test-Path -LiteralPath $approved)) {
        Remove-Item -LiteralPath $approved
    }
    if ($profileMarker -and (Test-Path -LiteralPath $profileMarker)) {
        Remove-Item -LiteralPath $profileMarker
    }
}

$failed = @($script:results | Where-Object { -not $_.Passed }).Count
$previousHash = $null
if ($PreviousInstaller -and (Test-Path -LiteralPath $PreviousInstaller)) {
    $previousHash = (Get-FileHash -LiteralPath $PreviousInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
}
$summary = [ordered]@{
    runId = $run
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    installer = $Installer
    installerSha256 = (Get-FileHash $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
    previousInstallerSha256 = $previousHash
    failed = $failed
    checks = @($script:results)
}
$summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$script:results | Format-Table -AutoSize | Out-String | Write-Host
Log "completed failures=$failed artifacts=$out"
if ($failed -gt 0) { exit 2 }
exit 0
