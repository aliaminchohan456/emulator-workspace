#requires -Version 5.1
<#
.SYNOPSIS
    Runs the Phase 0 Windows acceptance spike against the real emulator.

.DESCRIPTION
    Boots two isolated x86_64 AOSP API 35 guests with WHPX, proves exact ADB
    targeting, authenticated loopback controller access, a frame stream, input
    that produces a visible response, a named snapshot save/restore, and the
    agent access denial path. Optionally runs the ramdisk substitution probe
    that the Magisk lab depends on.

    Every step is executed through the workspace CLI (wsctl). ADB is used only
    to verify guest state independently of the CLI's own claims.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\phase0-spike.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\phase0-spike.ps1 -KeepRunning -SkipMagiskProbe
#>
param(
    [string]$InstanceA = "a",
    [string]$InstanceB = "b",
    [int]$TapX = 332,
    [int]$TapY = 1820,
    [int]$BootTimeoutSeconds = 600,
    [string]$Sdk = $env:ANDROID_SDK_ROOT,
    [string]$Root,
    [string]$OutDir = "artifacts\phase0",
    [switch]$KeepRunning,
    [switch]$SkipMagiskProbe
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Sdk) { $Sdk = $env:ANDROID_SDK_ROOT }
if (-not $Sdk) { $Sdk = $env:ANDROID_HOME }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
if (-not (Test-Path $Sdk)) { throw "Android SDK not found. Pass -Sdk <path> or set ANDROID_HOME." }

$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$adb = Join-Path $Sdk "platform-tools\adb.exe"
if (-not (Test-Path $adb)) { throw "adb not found at $adb" }
$runDir = Join-Path $repo ($OutDir + "\run-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force $runDir | Out-Null
$transcript = Join-Path $runDir "transcript.txt"
$results = @()
$globalArgs = @()
if ($Sdk) { $globalArgs += @("--sdk", $Sdk) }
if ($Root) { $globalArgs += @("--root", $Root) }

function Write-Transcript([string]$line) {
    $stamp = (Get-Date -Format "HH:mm:ss")
    $line = "[$stamp] $line"
    Write-Host $line
    Add-Content -Path $transcript -Value $line -Encoding UTF8
}

function Record-Result([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:results += [pscustomobject]@{ Check = $Name; Passed = $Passed; Detail = $Detail }
    $mark = if ($Passed) { "PASS" } else { "FAIL" }
    Write-Transcript ("{0} {1}: {2}" -f $mark, $Name, $Detail)
}

function Invoke-Wsctl {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )
    $all = $Arguments + $globalArgs
    Write-Transcript ("$ wsctl " + ($all -join " "))
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $wsctl @all 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($output) { $output | ForEach-Object { Add-Content -Path $transcript -Value ("    " + $_) -Encoding UTF8 } }
    if ($code -ne 0 -and -not $AllowFailure) {
        throw ("wsctl " + ($Arguments -join " ") + " failed with exit code $code")
    }
    return [pscustomobject]@{ ExitCode = $code; Output = ($output -join "`n") }
}

function Get-InstanceStatus([string]$Name) {
    $json = (Invoke-Wsctl -Arguments @("status", "--name", $Name, "--json")).Output
    return $json | ConvertFrom-Json
}

function Invoke-Adb([string]$Serial, [string[]]$Command) {
    $all = @("-s", $Serial) + $Command
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $adb @all 2>&1
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return ($output -join "`n")
}

function Wait-Settled([string]$Serial) {
    Invoke-Adb $Serial @("shell", "settings", "put", "global", "window_animation_scale", "0") | Out-Null
    Invoke-Adb $Serial @("shell", "settings", "put", "global", "transition_animation_scale", "0") | Out-Null
    Invoke-Adb $Serial @("shell", "settings", "put", "global", "animator_duration_scale", "0") | Out-Null
    Invoke-Adb $Serial @("shell", "settings", "put", "global", "screen_off_timeout", "2147483647") | Out-Null
    Invoke-Adb $Serial @("shell", "input", "keyevent", "KEYCODE_WAKEUP") | Out-Null
    Invoke-Adb $Serial @("shell", "input", "keyevent", "KEYCODE_HOME") | Out-Null
    Start-Sleep -Seconds 20
}

function Get-Focus([string]$Serial) {
    $dump = Invoke-Adb $Serial @("shell", "dumpsys", "window")
    $line = ($dump -split "`n" | Where-Object { $_ -match "mCurrentFocus" } | Select-Object -First 1)
    return ($line -replace "^\s*mCurrentFocus=", "").Trim()
}

Write-Transcript "Phase 0 spike starting; artifacts in $runDir"

# 1. Host baseline
$baselineJson = Join-Path $runDir "host-baseline.json"
Invoke-Wsctl -Arguments @("doctor", "--json", $baselineJson) | Out-Null
$baseline = Get-Content $baselineJson -Raw | ConvertFrom-Json
Record-Result "host.whpx" ($baseline.accelerationVerdict -match "usable") $baseline.accelerationVerdict
Record-Result "host.image" (@($baseline.androidSdk.images | Where-Object { $_.packageId -eq "system-images;android-35;default;x86_64" }).Count -eq 1) "system-images;android-35;default;x86_64"
Record-Result "host.emulator" ($baseline.androidSdk.emulatorVersion -match "^\d+\.\d+\.\d+") $baseline.androidSdk.emulatorVersion

# 2. Ensure both instances exist and boot
foreach ($name in @($InstanceA, $InstanceB)) {
    $status = Invoke-Wsctl -Arguments @("status", "--name", $name, "--json") -AllowFailure
    if ($status.ExitCode -ne 0) {
        Invoke-Wsctl -Arguments @("create", "--name", $name) | Out-Null
        Record-Result ("instance.create." + $name) $true "created"
    }
    else {
        $existing = $status.Output | ConvertFrom-Json
        Record-Result ("instance.existing." + $name) $true ("state=" + $existing.state)
        if ($existing.engineAlive) {
            Invoke-Wsctl -Arguments @("stop", "--name", $name) | Out-Null
        }
    }

    $start = Invoke-Wsctl -Arguments @("start", "--name", $name) -AllowFailure
    Record-Result ("instance.start." + $name) ($start.ExitCode -eq 0) $start.Output
    $wait = Invoke-Wsctl -Arguments @("wait", "--name", $name, "--timeout", $BootTimeoutSeconds) -AllowFailure
    Record-Result ("instance.boot." + $name) ($wait.ExitCode -eq 0) $wait.Output
}

# 3. Exact targeting and authenticated controller
$serialA = (Get-InstanceStatus $InstanceA).adbSerial
$serialB = (Get-InstanceStatus $InstanceB).adbSerial

foreach ($name in @($InstanceA, $InstanceB)) {
    $verify = Invoke-Wsctl -Arguments @("verify-target", "--name", $name)
    $verify.Output | Set-Content (Join-Path $runDir ("target-" + $name + ".txt")) -Encoding UTF8
    $probe = Invoke-Wsctl -Arguments @("controller-probe", "--name", $name)
    $probe.Output | Set-Content (Join-Path $runDir ("controller-" + $name + ".txt")) -Encoding UTF8
    Record-Result ("target." + $name) ($verify.Output -match "get-serialno\s*:\s*emulator-\d+") "exact serial verified"
    Record-Result ("controller.auth." + $name) `
        (($probe.Output -match "no-token call\s*: Unauthenticated") -and ($probe.Output -match "forged-token\s*: Unauthenticated")) `
        "unauthenticated and forged calls rejected"
    Record-Result ("controller.binding." + $name) ($probe.Output -match "binding\s*: loopback-only") "loopback-only"
}

Record-Result "isolation.serial" ($serialA -ne $serialB) ($serialA + " vs " + $serialB)
$statusA = Get-InstanceStatus $InstanceA
$statusB = Get-InstanceStatus $InstanceB
Record-Result "isolation.ports" (($statusA.consolePort -ne $statusB.consolePort) -and ($statusA.grpcPort -ne $statusB.grpcPort)) `
    ("console " + $statusA.consolePort + "/" + $statusB.consolePort + ", controller " + $statusA.grpcPort + "/" + $statusB.grpcPort)

# 4. Frame and input on instance A
Wait-Settled $serialA
$before = Join-Path $runDir "input-before.png"
$after = Join-Path $runDir "input-after.png"
Invoke-Wsctl -Arguments @("frame", "--name", $InstanceA, "--via", "controller", "--out", $before) | Out-Null
$focusBefore = Get-Focus $serialA
Invoke-Wsctl -Arguments @("tap", "--name", $InstanceA, "--x", $TapX, "--y", $TapY, "--via", "controller") | Out-Null
Start-Sleep -Seconds 4
Invoke-Wsctl -Arguments @("frame", "--name", $InstanceA, "--via", "controller", "--out", $after) | Out-Null
$focusAfter = Get-Focus $serialA
$hashBefore = (Get-FileHash $before -Algorithm SHA256).Hash
$hashAfter = (Get-FileHash $after -Algorithm SHA256).Hash
Record-Result "input.visible-response" (($focusBefore -ne $focusAfter) -and ($hashBefore -ne $hashAfter)) `
    ("focus " + $focusBefore + " -> " + $focusAfter)

# 5. Named snapshot save and restore
$tag = "phase0-" + (Get-Date -Format "HHmmss")
$save = Invoke-Wsctl -Arguments @("snapshot-save", "--name", $InstanceA, "--tag", $tag)
$save.Output | Set-Content (Join-Path $runDir "snapshot-save.txt") -Encoding UTF8
Invoke-Adb $serialA @("shell", "input", "keyevent", "KEYCODE_HOME") | Out-Null
Start-Sleep -Seconds 2
$homeFocus = Get-Focus $serialA
$load = Invoke-Wsctl -Arguments @("snapshot-load", "--name", $InstanceA, "--tag", $tag)
$load.Output | Set-Content (Join-Path $runDir "snapshot-load.txt") -Encoding UTF8
Start-Sleep -Seconds 2
$restoredFocus = Get-Focus $serialA
Invoke-Wsctl -Arguments @("frame", "--name", $InstanceA, "--via", "controller", "--out", (Join-Path $runDir "restored.png")) | Out-Null
Record-Result "snapshot.roundtrip" (($restoredFocus -eq $focusAfter) -and ($homeFocus -ne $restoredFocus)) `
    ("saved at " + $focusAfter + ", home was " + $homeFocus + ", restored " + $restoredFocus)
$save.Output | ForEach-Object { Record-Result "snapshot.save" ($save.Output -match "in \d") $save.Output }
$load.Output | ForEach-Object { Record-Result "snapshot.load" ($load.Output -match "in \d") $load.Output }

# 6. Agent access must be denied by default
$denied = Invoke-Wsctl -Arguments @("frame", "--name", $InstanceA, "--agent", "--agent-name", "phase0-harness", "--out", (Join-Path $runDir "denied.png")) -AllowFailure
Record-Result "agent.denied" ($denied.ExitCode -eq 4) ("exit " + $denied.ExitCode + ": " + $denied.Output)

# 7. Magisk boot-path probe (ramdisk substitution)
if (-not $SkipMagiskProbe) {
    $imageRoot = Join-Path ($(if ($Sdk) { $Sdk } else { $env:ANDROID_SDK_ROOT })) "system-images\android-35\default\x86_64"
    $stockRamdisk = Join-Path $imageRoot "ramdisk.img"
    $probeImage = Join-Path $runDir "ramdisk-probe.img"
    $repack = & python (Join-Path $PSScriptRoot "repack_ramdisk.py") $stockRamdisk $probeImage --append-line "system/etc/ramdisk/build.prop=ro.bootimage.ws_probe=phase0-harness" 2>&1
    Add-Content -Path $transcript -Value ($repack -join "`n") -Encoding UTF8
    Record-Result "ramdisk.repack" ($LASTEXITCODE -eq 0) ($probeImage)

    Invoke-Wsctl -Arguments @("stop", "--name", $InstanceB) | Out-Null
    Invoke-Wsctl -Arguments @("start", "--name", $InstanceB, "--ramdisk", $probeImage) | Out-Null
    Invoke-Wsctl -Arguments @("wait", "--name", $InstanceB, "--timeout", $BootTimeoutSeconds) | Out-Null
    $marker = Invoke-Adb $serialB @("shell", "getprop", "ro.bootimage.ws_probe")
    Record-Result "magisk.ramdisk-boot" ($marker.Trim() -eq "phase0-harness") ("property=" + $marker.Trim())

    Invoke-Wsctl -Arguments @("stop", "--name", $InstanceB) | Out-Null
    Invoke-Wsctl -Arguments @("start", "--name", $InstanceB, "--ramdisk", "stock") | Out-Null
    Invoke-Wsctl -Arguments @("wait", "--name", $InstanceB, "--timeout", $BootTimeoutSeconds) | Out-Null
    Record-Result "magisk.stock-restore" $true "reverted to stock ramdisk and booted"
}

# 8. Independent input on instance B while A stays where it was
Wait-Settled $serialB
$focusA = Get-Focus $serialA
Invoke-Wsctl -Arguments @("tap", "--name", $InstanceB, "--x", $TapX, "--y", $TapY, "--via", "controller") | Out-Null
Start-Sleep -Seconds 4
$focusB = Get-Focus $serialB
$focusA2 = Get-Focus $serialA
Record-Result "isolation.input" (($focusB -ne $focusA2) -and ($focusA -eq $focusA2)) `
    ("A " + $focusA + " -> " + $focusA2 + ", B " + $focusB)

if (-not $KeepRunning) {
    foreach ($name in @($InstanceA, $InstanceB)) {
        Invoke-Wsctl -Arguments @("stop", "--name", $name) | Out-Null
    }
}

$summary = [pscustomobject]@{
    StartedUtc = (Get-Date).ToUniversalTime().ToString("o")
    RunDirectory = $runDir
    Host = $baseline.operatingSystem.full
    Emulator = $baseline.androidSdk.emulatorVersion
    Checks = $results
    Failed = @($results | Where-Object { -not $_.Passed }).Count
}
$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $runDir "summary.json") -Encoding UTF8

$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Transcript ("Completed with " + $summary.Failed + " failure(s). Artifacts: " + $runDir)

if ($summary.Failed -gt 0) { exit 2 }
exit 0
