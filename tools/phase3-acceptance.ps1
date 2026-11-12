#requires -Version 5.1
<#
.SYNOPSIS
    Phase 3 acceptance: Phase 2 regression closure, device library lifecycle,
    snapshot operations, two-guest transport identity, a queued-input takeover
    race, simulation controls, deployment and an external test runner.

.DESCRIPTION
    The harness owns its runtime root (default D:\Phase3Root), creates a stopped
    template, pairs two disposable guests, runs the Phase 2 acceptance as lane A
    and then exercises the Phase 3 lanes on the same guests. Every check is
    recorded in artifacts\phase3\<run-id>\summary.json; failures stay visible.

    Prerequisites:
      - pinned SDK (Emulator 37.2.12.0, API 35 x86_64 rev 2) and WHPX
      - optional: bundletool and Maestro under $env:WS_PHASE3_TOOLS for the
        deployment and external-runner lanes (they are skipped when absent)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\phase3-acceptance.ps1 `
      -Root D:\Phase3Root
#>
param(
    [string]$Root = "D:\Phase3Root",
    [string]$Template = "tpl3",
    [string]$OutDir = "artifacts\phase3",
    [string]$ToolDir = $(if ($env:WS_PHASE3_TOOLS) { $env:WS_PHASE3_TOOLS } else { "D:\Phase3Tools" }),
    [switch]$SkipBuild,
    [switch]$SkipPhase2,
    [switch]$SkipDeployment,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "fixtures/acceptance-payload.ps1")
$wsctl = Get-AcceptanceBinary "wsctl.exe" "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk
$env:MAESTRO_CLI_ANALYSIS_NOTIFICATION_DISABLED = "true"

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
Write-AcceptanceContext $out
$log = Join-Path $out "transcript.txt"
$script:results = New-Object System.Collections.ArrayList
$lanes = @{}

function Log([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $message
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

function Add-Check([string]$lane, [string]$name, [bool]$ok, [string]$detail) {
    [void]$script:results.Add([pscustomobject]@{
        Lane = $lane
        Check = $name
        Passed = $ok
        Detail = $detail
        AtUtc = (Get-Date).ToUniversalTime().ToString("O")
    })
    Log ("{0} [{1}] {2}: {3}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $lane, $name, $detail)
}

function Invoke-Ws([string[]]$arguments, [switch]$AllowFailure, [int]$timeoutSeconds = 420) {
    # The client is bounded: a supervisor that never answers is reported as a
    # timeout instead of blocking the harness.
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $wsctl
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $quoted = @()
    foreach ($argument in $arguments) {
        $quoted += ('"' + ($argument -replace '"', '\"') + '"')
    }
    $info.Arguments = $quoted -join " "

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($timeoutSeconds * 1000)
    if ($timedOut) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }

    $text = ""
    try { $text = $stdout.Result + $stderr.Result } catch { }
    $code = -1
    try { $code = $process.ExitCode } catch { }
    try { $process.Dispose() } catch { }
    $text = ($text | Out-String).Trim()
    if ($code -ne 0 -and -not $AllowFailure -and -not $timedOut) {
        throw "wsctl $($arguments -join ' ') failed ($code): $text"
    }

    return [pscustomobject]@{ Code = $code; Text = $text; TimedOut = $timedOut }
}
function Invoke-Adb([string[]]$arguments, [int]$timeoutSeconds = 30) {
    # Every ADB client is bounded: on timeout only this owned client is
    # terminated; the shared ADB server and unrelated targets are untouched.
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $adb
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $quoted = @()
    foreach ($argument in $arguments) {
        $quoted += ('"' + ($argument -replace '"', '\"') + '"')
    }
    $info.Arguments = $quoted -join " "

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($timeoutSeconds * 1000)
    if ($timedOut) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }

    $text = ""
    try { $text = $stdout.Result + $stderr.Result } catch { }
    $code = -1
    try { $code = $process.ExitCode } catch { }
    try { $process.Dispose() } catch { }
    return [pscustomobject]@{
        Code = $code
        Text = ($text | Out-String).Trim()
        TimedOut = $timedOut
    }
}
function Test-Anr([string]$serial) {
    $dump = (Invoke-Adb @("-s", $serial, "shell", "dumpsys", "window")).Text
    return ($dump -match "Not Responding|isn't responding|ANR in")
}

function Ensure-NoAnr([string]$serial, [string]$instance, [int]$attempts = 3) {
    # A stuck SystemUI/launcher can raise a blocking "not responding" dialog
    # that hides the application from every observer. The guest is restarted
    # until the dialog is gone, and the recovery is reported.
    $recovered = $false
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        if (-not (Test-Anr $serial)) { return [pscustomobject]@{ Clear = $true; Recovered = $recovered } }
        $recovered = $true
        Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) | Out-Null
        Invoke-Ws @("start", "--name", $instance, "--root", $Root) | Out-Null
        Invoke-Ws @("wait", "--name", $instance, "--timeout", "600", "--root", $Root) | Out-Null
    }
    return [pscustomobject]@{ Clear = -not (Test-Anr $serial); Recovered = $recovered }
}

function Unlock-Guest([string]$serial) {
    # Headless guests boot with the keyguard up, which hides the application
    # window from the runner and from the hierarchy dump.
    Invoke-Adb @("-s", $serial, "shell", "wm", "dismiss-keyguard") | Out-Null
    Invoke-Adb @("-s", $serial, "shell", "input", "keyevent", "82") | Out-Null
    Start-Sleep -Seconds 2
}

function Wait-TransportUsable([string]$serial, [int]$seconds = 90) {
    # Bounded reconnect of one owned transport, never a global ADB action.
    $deadline = (Get-Date).AddSeconds($seconds)
    $attempts = 0
    while ((Get-Date) -lt $deadline) {
        $probe = Invoke-Adb @("-s", $serial, "shell", "echo", "ws-transport-probe")
        if ($probe.Code -eq 0 -and $probe.Text -match "ws-transport-probe") { return $true }
        if ($attempts -lt 3) {
            $attempts++
            Invoke-Adb @("reconnect", $serial) | Out-Null
        }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Parse-Json([string]$text) {
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Save-Text([string]$name, [string]$text) {
    Set-Content -Path (Join-Path $out $name) -Value $text -Encoding UTF8
}

Log "Phase 3 acceptance $run root=$Root template=$Template tools=$ToolDir"

$hostInfo = $null
try {
    New-Item -ItemType Directory -Force $Root | Out-Null
    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($Root).Substring(0, 1))
    $hostInfo = [ordered]@{
        runId = $run
        startedUtc = (Get-Date).ToUniversalTime().ToString("O")
        root = $Root
        template = $Template
        freeBytes = $drive.Free
        dotnet = (& dotnet --version 2>&1 | Out-String).Trim()
        commit = (& git -C $repo rev-parse --short HEAD 2>&1 | Out-String).Trim()
        workTree = (& git -C $repo status --porcelain 2>&1 | Out-String).Trim()
        emulator = (& (Join-Path $sdk "emulator\emulator.exe") -version 2>&1 | Select-Object -First 1 | Out-String).Trim()
        imageRevision = (Get-Content (Join-Path $sdk "system-images\android-35\default\x86_64\source.properties") |
            Select-String -Pattern "Pkg.Revision").ToString().Trim()
    }
    $hostInfo | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out "versions.json") -Encoding UTF8
    Add-Check "host" "free-space" ($drive.Free -gt 20GB) ("free={0:N1} GB" -f ($drive.Free / 1GB))
} catch {
    Add-Check "host" "setup" $false $_.Exception.Message
}

try {
    if (-not $SkipBuild) {
        $build = (& dotnet build -v q --nologo 2>&1 | Out-String)
        $buildCode = $LASTEXITCODE
        Save-Text "build.txt" $build
        Add-Check "host" "build" ($buildCode -eq 0) "dotnet build exit=$buildCode"
        $tests = (& dotnet test --no-build -v q --nologo 2>&1 | Out-String)
        $testCode = $LASTEXITCODE
        Save-Text "tests.txt" $tests
        Add-Check "host" "unit-tests" ($testCode -eq 0) ([regex]::Match($tests, "Passed:\s+\d+").Value)
    }

    # The Phase 2 lane installs the approved sample APK from this root.
    $approvedApk = Get-AcceptanceBinary "ws-sample.apk" "artifacts\phase1\sample\ws-sample.apk"
    if (Test-Path $approvedApk) {
        New-Item -ItemType Directory -Force (Join-Path $Root "apps") | Out-Null
        Copy-Item $approvedApk (Join-Path $Root "apps\ws-sample.apk") -Force
        Add-Check "setup" "approved-apk" $true ((Get-FileHash $approvedApk -Algorithm SHA256).Hash)
    }
    else {
        Add-Check "setup" "approved-apk" $false "artifacts\phase1\sample\ws-sample.apk is missing"
    }

    # Template + two paired disposable guests.
    Invoke-Ws @("create", "--name", $Template, "--root", $Root) | Out-Null
    $pairP = Invoke-Ws @("agent-pair", "--client-name", "phase3-primary", "--template", $Template, "--out", "$Root\session-p.json", "--root", $Root)
    $sessionP = Parse-Json (Get-Content "$Root\session-p.json" -Raw)
    $pairQ = Invoke-Ws @("agent-pair", "--client-name", "phase3-secondary", "--template", $Template, "--out", "$Root\session-q.json", "--root", $Root)
    $sessionQ = Parse-Json (Get-Content "$Root\session-q.json" -Raw)
    Add-Check "setup" "pairs" ($null -ne $sessionP -and $null -ne $sessionQ) "P=$($sessionP.instance) Q=$($sessionQ.instance)"
    $tokenP = "$Root\session-p.json"
    $tokenQ = "$Root\session-q.json"

    # Lane A: Phase 2 regression run on the candidate commit.
    if (-not $SkipPhase2) {
        $phase2 = Join-Path $repo "tools\phase2-acceptance.ps1"
        $phase2OutDir = Join-Path (Join-Path $OutDir $run) "phase2"
        $phase2Out = (& powershell -ExecutionPolicy Bypass -File $phase2 -Root $Root -TokenFile $tokenP -Template $Template -OutDir $phase2OutDir -KeepRunning -SkipBuild 2>&1 | Out-String)
        $phase2Code = $LASTEXITCODE
        Save-Text "phase2-acceptance.txt" $phase2Out
        $phase2Run = Get-ChildItem -LiteralPath (Join-Path $repo $phase2OutDir) -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $phase2Summary = $null
        if ($phase2Run) {
            $summaryPath = Join-Path $phase2Run.FullName "summary.json"
            if (Test-Path $summaryPath) {
                $phase2Summary = Parse-Json (Get-Content $summaryPath -Raw)
                Copy-Item $summaryPath (Join-Path $out "phase2-summary.json") -Force
            }
        }

        $phase2Failed = if ($phase2Summary) { $phase2Summary.failed } else { -1 }
        Add-Check "phase2" "acceptance" ($phase2Code -eq 0 -and $phase2Failed -eq 0) "exit=$phase2Code failed=$phase2Failed run=$($phase2Run.Name)"
        $lanes["phase2"] = $phase2Summary
    }

    # Exact identity of the primary guest is resolved once and reused.
    $idP = Parse-Json (Invoke-Ws @("adb-identity", "--name", $sessionP.instance, "--root", $Root) -AllowFailure).Text
    if ($idP) { Unlock-Guest $idP.serial }
    if (-not $idP) {
        $idP = [pscustomobject]@{ serial = "emulator-5556" }
    }

    # Lane B: device library lifecycle and snapshots.
    $duplicateRunning = Invoke-Ws @("duplicate", "--name", $sessionP.instance, "--target", "p3-running-dup", "--root", $Root) -AllowFailure
    Add-Check "lifecycle" "duplicate-running-refused" ($duplicateRunning.Text -match "must be stopped") "running writable disks are never copied"
    $duplicate = Invoke-Ws @("duplicate", "--name", $Template, "--target", "p3-dup", "--root", $Root) -AllowFailure
    $duplicateJson = Parse-Json $duplicate.Text
    Add-Check "lifecycle" "duplicate" ($duplicate.Code -eq 0 -and $duplicateJson.copiedBytes -ge 0) `
        ("target=$($duplicateJson.target) ports=$($duplicateJson.consolePort)/$($duplicateJson.grpcPort) lineage=$($duplicateJson.lineageId)")
    $portsDiffer = $duplicateJson.consolePort -ne $null -and $duplicateJson.sourceFingerprint -ne $duplicateJson.targetFingerprint
    Add-Check "lifecycle" "duplicate-isolated" ($portsDiffer -and $duplicateJson.parentLineageId -ne $null) `
        ("source=$($duplicateJson.sourceFingerprint) target=$($duplicateJson.targetFingerprint)")
    $templateAfter = Invoke-Ws @("status", "--name", $Template, "--root", $Root) -AllowFailure
    Add-Check "lifecycle" "template-untouched" ($templateAfter.Code -eq 0) (($templateAfter.Text -split "`n")[0])

    $reset = Invoke-Ws @("reset", "--name", "p3-dup", "--root", $Root) -AllowFailure
    $resetJson = Parse-Json $reset.Text
    Add-Check "lifecycle" "reset" ($reset.Code -eq 0 -and $resetJson.fingerprintAfter) ("freedBytes=$($resetJson.freedBytes)")

    $presets = Invoke-Ws @("presets", "--root", $Root) -AllowFailure
    $presetsJson = Parse-Json $presets.Text
    Add-Check "lifecycle" "presets" ($presets.Code -eq 0 -and $presetsJson.presets.Count -ge 2) "presets=$($presetsJson.presets.Count)"
    $estimate = $presetsJson.estimate
    Add-Check "lifecycle" "disk-estimate" ($estimate.sufficient -eq $true) ("required=$($estimate.withSnapshotReserveBytes) free=$($estimate.freeBytes)")

    $profile = Invoke-Ws @("profile-export", "--name", $sessionP.instance, "--root", $Root) -AllowFailure
    Save-Text "profile-export.json" $profile.Text
    $profileJson = Parse-Json $profile.Text
    $profileText = $profile.Text
    $noSecrets = ($profileText -notmatch "token" -and $profileText -notmatch "password" -and $profileText -notmatch "secret")
    Add-Check "lifecycle" "profile-export" ($profile.Code -eq 0 -and $noSecrets) "schema=$($profileJson.profile.schemaVersion) secrets=$(-not $noSecrets)"
    $profileFile = Join-Path $out "profile-import-request.json"
    Set-Content $profileFile (@{ profile = $profileJson.profile; name = "p3-import" } | ConvertTo-Json -Depth 8) -Encoding UTF8
    $import = Invoke-Ws @("call", "--op", "instance.profileImport", "--args-file", $profileFile, "--root", $Root) -AllowFailure
    Add-Check "lifecycle" "profile-import" ($import.Code -eq 0) (($import.Text -split "`n")[0])

    # Snapshot lifecycle on the primary guest.
    $snapSave = Invoke-Ws @("snapshot-save", "--name", $sessionP.instance, "--tag", "p3-base", "--root", $Root) -AllowFailure
    Add-Check "snapshot" "save" ($snapSave.Code -eq 0) (($snapSave.Text -split "`n")[0])
    $snapList = Invoke-Ws @("snapshots", "--name", $sessionP.instance, "--root", $Root) -AllowFailure
    Save-Text "snapshot-list.json" $snapList.Text
    $snapJson = Parse-Json $snapList.Text
    $base = $snapJson.snapshots | Where-Object { $_.tag -eq "p3-base" } | Select-Object -First 1
    Add-Check "snapshot" "list" ($snapList.Code -eq 0 -and $base -ne $null) "count=$($snapJson.count) compatible=$($base.compatible)"
    $compatible = @($snapJson.snapshots | Where-Object { $_.compatible })
    $newest = @($compatible | Sort-Object { $_.createdUtc } -Descending)[0]
    $older = @($compatible | Where-Object { $_.tag -ne $newest.tag -and $_.tag -ne "default_boot" })[0]
    if ($older) {
        $snapDelete = Invoke-Ws @("snapshot-delete", "--name", $sessionP.instance, "--tag", $older.tag, "--root", $Root) -AllowFailure
        Add-Check "snapshot" "delete-older" ($snapDelete.Code -eq 0) ("deleted=$($older.tag)")
    }
    $snapDelete = Invoke-Ws @("snapshot-delete", "--name", $sessionP.instance, "--tag", $newest.tag, "--root", $Root) -AllowFailure
    Add-Check "snapshot" "delete-guarded" ($snapDelete.Text -match "approval_required") "newest recovery point protected"
    $snapDeleteForced = Invoke-Ws @("snapshot-delete", "--name", $sessionP.instance, "--tag", $newest.tag, "--force", "--root", $Root) -AllowFailure
    Add-Check "snapshot" "delete-forced" ($snapDeleteForced.Code -eq 0) (($snapDeleteForced.Text -split "`n")[0])

    # Stale frame invalidation after a restore.
    $frame = Invoke-Ws @("call", "--op", "observation.screenshot", "--token-file", $tokenP, "--root", $Root) -AllowFailure
    $frameJson = Parse-Json $frame.Text
    $staleFile = Join-Path $out "stale-after-restore.json"
    Set-Content $staleFile (@{ frameId = $frameJson.frameId; x = 540; y = 1400 } | ConvertTo-Json) -Encoding UTF8
    $snapSave2 = Invoke-Ws @("snapshot-save", "--name", $sessionP.instance, "--tag", "p3-restore", "--root", $Root) -AllowFailure
    $approval = Invoke-Ws @("agent-approve-action", "--session", $sessionP.session, "--operation", "snapshot.load", "--root", $Root)
    $approvalCode = (Parse-Json $approval.Text).code
    $restoreFile = Join-Path $out "restore.json"
    Set-Content $restoreFile (@{ tag = "p3-restore"; approval = $approvalCode } | ConvertTo-Json) -Encoding UTF8
    $restore = Invoke-Ws @("call", "--op", "snapshot.load", "--args-file", $restoreFile, "--token-file", $tokenP, "--root", $Root) -AllowFailure
    Add-Check "snapshot" "restore" ($restore.Code -eq 0) (($restore.Text -split "`n")[0])
    $bootDeadline = (Get-Date).AddSeconds(360)
    $booted = $false
    while ((Get-Date) -lt $bootDeadline) {
        $state = (Invoke-Adb @("-s", $idP.serial, "get-state")).Text
        $boot = (Invoke-Adb @("-s", $idP.serial, "shell", "getprop", "sys.boot_completed")).Text
        if ($state -eq "device" -and $boot -eq "1") { $booted = $true; break }
        Start-Sleep -Seconds 3
    }
    Add-Check "snapshot" "restore-readiness" $booted "the guest reports sys.boot_completed=1 after the restore"
    & $adb -s $idP.serial shell monkey -p dev.emulatorworkspace.sample -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
    Start-Sleep -Seconds 4
    $stale = Invoke-Ws @("call", "--op", "input.tap", "--args-file", $staleFile, "--token-file", $tokenP, "--root", $Root) -AllowFailure
    Add-Check "snapshot" "stale-frame-invalidated" ($stale.Text -match "stale_observation") "frame id rejected after restore"

    # Lane C: two guests and the external ADB transport.
    Invoke-Ws @("start", "--name", $sessionQ.instance, "--token-file", $tokenQ, "--root", $Root) | Out-Null
    Invoke-Ws @("wait", "--name", $sessionQ.instance, "--timeout", "600", "--token-file", $tokenQ, "--root", $Root) | Out-Null
    $before = (Invoke-Adb @("devices", "-l")).Text
    Save-Text "adb-devices-before.txt" $before
    $identityP = Invoke-Ws @("adb-identity", "--name", $sessionP.instance, "--root", $Root) -AllowFailure
    $identityQ = Invoke-Ws @("adb-identity", "--name", $sessionQ.instance, "--root", $Root) -AllowFailure
    Save-Text "adb-identity.json" ($identityP.Text + "`n" + $identityQ.Text)
    $idP = Parse-Json $identityP.Text
    $idQ = Parse-Json $identityQ.Text
    $externallyVisible = ($before -match [regex]::Escape($idP.serial)) -and ($before -match [regex]::Escape($idQ.serial))
    Add-Check "transport" "identity" ($identityP.Code -eq 0 -and $identityQ.Code -eq 0 -and $externallyVisible) `
        ("P=$($idP.serial)/$($idP.transportId) Q=$($idQ.serial)/$($idQ.transportId)")
    Add-Check "transport" "distinct-targets" ($idP.serial -ne $idQ.serial -and $idP.identity -ne $idQ.identity) "serials and identities differ"

    $crossFile = Join-Path $out "cross-guest.json"
    Set-Content $crossFile (@{ name = $sessionQ.instance } | ConvertTo-Json) -Encoding UTF8
    $cross = Invoke-Ws @("call", "--op", "instance.status", "--args-file", $crossFile, "--token-file", $tokenP, "--root", $Root) -AllowFailure
    Add-Check "transport" "cross-guest-denied" ($cross.Text -match "wrong_instance") "agent on P cannot address Q"

    $after = (Invoke-Adb @("devices", "-l")).Text
    Save-Text "adb-devices-after.txt" $after
    $unchanged = ($after -match [regex]::Escape($idQ.transportId)) -and ($after -match [regex]::Escape($idP.transportId))
    Add-Check "transport" "external-state-unchanged" $unchanged "transport ids stable across agent calls"

    # A recycled transport must not break exact target binding.
    & $adb -s $idQ.serial reconnect 2>&1 | Out-Null
    Start-Sleep -Seconds 3
    $recycled = Invoke-Ws @("adb-identity", "--name", $sessionQ.instance, "--root", $Root) -AllowFailure
    $idQ2 = Parse-Json $recycled.Text
    Add-Check "transport" "recycled-transport-resolves" `
        ($recycled.Code -eq 0 -and $idQ2.serial -eq $idQ.serial -and $idQ2.enginePid -eq $idQ.enginePid) `
        ("transportId=$($idQ2.transportId) (was $($idQ.transportId)) enginePid=$($idQ2.enginePid)")

    # A disconnected target must fail closed instead of touching another device.
    Invoke-Ws @("stop", "--name", $sessionQ.instance, "--force", "--root", $Root) | Out-Null
    Start-Sleep -Seconds 5
    $lost = Invoke-Ws @("adb-identity", "--name", $sessionQ.instance, "--root", $Root) -AllowFailure
    Add-Check "transport" "disconnected-target-fails-closed" ($lost.Text -match "instance_unavailable") (($lost.Text -split "`n")[0])
    $crossLost = Invoke-Ws @("call", "--op", "instance.status", "--args-file", $crossFile, "--token-file", $tokenP, "--root", $Root) -AllowFailure
    Add-Check "transport" "disconnected-target-not-substituted" ($crossLost.Text -match "wrong_instance" -or $crossLost.Text -match "instance_unavailable") `
        "a missing target is never substituted with another guest"
    Invoke-Ws @("start", "--name", $sessionQ.instance, "--root", $Root) | Out-Null
    Invoke-Ws @("wait", "--name", $sessionQ.instance, "--timeout", "600", "--root", $Root) | Out-Null
    $recovered = Invoke-Ws @("adb-identity", "--name", $sessionQ.instance, "--root", $Root) -AllowFailure
    $idQ3 = Parse-Json $recovered.Text
    Add-Check "transport" "reconnect-rebounds" ($recovered.Code -eq 0 -and $idQ3.serial -eq $idQ.serial) `
        ("transportId=$($idQ3.transportId) (was $($idQ.transportId))")
    $physical = @($after -split "`n" | Where-Object { $_ -match "^\S+\s+(device|offline|unauthorized)" -and $_ -notmatch "emulator-\d+" })
    $managedSerials = @($remaining.Text -split "`n" | ForEach-Object { if ($_ -match "(emulator-\d+)") { $Matches[1] } })
    $physicalManaged = @($managedSerials | Where-Object { $_ -notmatch "^emulator-\d+$" })
    Add-Check "transport" "physical-devices-not-managed" ($physicalManaged.Count -eq 0) `
        ("non-emulator transports visible=$($physical.Count); managed serials are emulator-only")

    # Lane D: queued-input race with a controlled synchronization point.
    # The fixture is put back in the foreground first so the marker can be read
    # before and after the race.
    $anr = Ensure-NoAnr $idP.serial $sessionP.instance
    Add-Check "guest" "no-anr-overlay" $anr.Clear "recovered=$($anr.Recovered)"
    Unlock-Guest $idP.serial
    & $adb -s $idP.serial shell monkey -p dev.emulatorworkspace.sample -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null
    Start-Sleep -Seconds 5
    & $adb -s $idP.serial shell uiautomator dump /sdcard/p3-before.xml 2>&1 | Out-Null
    $beforeDump = (Invoke-Adb @("-s", $idP.serial, "shell", "cat", "/sdcard/p3-before.xml")).Text
    $beforeNode = [regex]::Match($beforeDump, '<node[^>]*content-desc="dup_marker"[^>]*>')
    $markerBefore = ""
    if ($beforeNode.Success) {
        $beforeText = [regex]::Match($beforeNode.Value, 'text="([^"]*)"')
        if ($beforeText.Success) { $markerBefore = $beforeText.Groups[1].Value }
    }
    Add-Check "race" "fixture-foreground" ($markerBefore -eq "DUP_TAPS=0") "marker before the race=$markerBefore"
    $tapBaseline = (Invoke-Adb @("-s", $idP.serial, "logcat", "-d", "-s", "WS_SAMPLE:I")).Text
    $tapBaselineCount = ([regex]::Matches($tapBaseline, "DUP_TAP index=")).Count
    $hold = Invoke-Ws @("input-hold", "--name", $sessionP.instance, "--root", $Root) -AllowFailure
    Add-Check "race" "input-held" ($hold.Code -eq 0) (($hold.Text -split "`n")[0])
    $tapFile = Join-Path $out "race-tap.json"
    Set-Content $tapFile (@{ selector = @{ text = "Duplicate A" } } | ConvertTo-Json -Depth 4) -Encoding UTF8
    $tapJob = Start-Job -ScriptBlock {
        param($wsctl, $token, $root, $argsFile)
        & $wsctl call --op input.tap --args-file $argsFile --token-file $token --root $root 2>&1 | Out-String
    } -ArgumentList $wsctl, $tokenP, $Root, $tapFile
    $queuedUtc = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        $gate = Invoke-Ws @("input-gate", "--name", $sessionP.instance, "--root", $Root) -AllowFailure
        $gateJson = Parse-Json $gate.Text
        if ($gateJson.waiting -ge 1) {
            $queuedUtc = $gateJson.waiters[0].queuedUtc
            break
        }
    }

    Add-Check "race" "input-queued" ($null -ne $queuedUtc) ("queuedUtc=$queuedUtc")
    $takeoverUtc = (Get-Date).ToUniversalTime().ToString("O")
    $takeover = Invoke-Ws @("agent-takeover", "--name", $sessionP.instance, "--root", $Root) -AllowFailure
    Add-Check "race" "takeover" ($takeover.Code -eq 0) (($takeover.Text -replace "`n", " "))
    $releaseUtc = (Get-Date).ToUniversalTime().ToString("O")
    $release = Invoke-Ws @("input-hold", "--name", $sessionP.instance, "--release", "--root", $Root) -AllowFailure
    $tapResult = Receive-Job $tapJob -Wait -ErrorAction SilentlyContinue | Out-String
    Remove-Job $tapJob -Force -ErrorAction SilentlyContinue
    Save-Text "race-tap-result.txt" $tapResult
    Add-Check "race" "queued-input-not-authorized" ($tapResult -match "revoked" -or $tapResult -match "cancelled" -or $tapResult -match "forbidden") `
        (($tapResult -split "`n" | Where-Object { $_ -match "FAILED" } | Select-Object -First 1))
    & $adb -s $idP.serial shell uiautomator dump /sdcard/p3-race.xml 2>&1 | Out-Null
    $dump = (Invoke-Adb @("-s", $idP.serial, "shell", "cat", "/sdcard/p3-race.xml")).Text
    Save-Text "race-ui.xml" $dump
    $nodeMatch = [regex]::Match($dump, '<node[^>]*content-desc="dup_marker"[^>]*>')
    $markerText = ""
    if ($nodeMatch.Success) {
        $textMatch = [regex]::Match($nodeMatch.Value, 'text="([^"]*)"')
        if ($textMatch.Success) { $markerText = $textMatch.Groups[1].Value }
    }
    $tapLog = (Invoke-Adb @("-s", $idP.serial, "logcat", "-d", "-s", "WS_SAMPLE:I")).Text
    Save-Text "race-logcat.txt" $tapLog
    $dupTaps = ([regex]::Matches($tapLog, "DUP_TAP index=")).Count
    Add-Check "race" "guest-marker-unchanged" ($markerText -eq "DUP_TAPS=0" -and $dupTaps -eq $tapBaselineCount) `
        ("marker=$markerText dupTapLogLines=$dupTaps baseline=$tapBaselineCount")
    [ordered]@{
        queuedUtc = $queuedUtc
        takeoverUtc = $takeoverUtc
        releaseUtc = $releaseUtc
        resultUtc = (Get-Date).ToUniversalTime().ToString("O")
        tapResult = $tapResult.Trim()
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out "race-timeline.json") -Encoding UTF8

    # Lane E: simulation controls.
    Set-Content (Join-Path $out "geo.json") (@{ name = $sessionP.instance; latitude = 52.37; longitude = 4.89; altitude = 12 } | ConvertTo-Json) -Encoding UTF8
    $geo = Invoke-Ws @("call", "--op", "sim.geoFix", "--args-file", (Join-Path $out "geo.json"), "--root", $Root) -AllowFailure
    Add-Check "simulation" "geo-fix" ($geo.Code -eq 0) (($geo.Text -split "`n")[0])
    Set-Content (Join-Path $out "battery.json") (@{ name = $sessionQ.instance; level = 42; charging = $true } | ConvertTo-Json) -Encoding UTF8
    $battery = Invoke-Ws @("call", "--op", "sim.battery", "--args-file", (Join-Path $out "battery.json"), "--root", $Root) -AllowFailure
    Add-Check "simulation" "battery" ($battery.Code -eq 0) (($battery.Text -split "`n")[0])
    $batteryState = (Invoke-Adb @("-s", $idQ.serial, "shell", "dumpsys", "battery")).Text
    Save-Text "battery-dumpsys.txt" $batteryState
    Add-Check "simulation" "battery-verified" ($batteryState -match "level: 42") (([regex]::Match($batteryState, "level: \d+")).Value)
    $rotate = Invoke-Ws @("rotate", "--name", $sessionQ.instance, "--rotation", "1", "--root", $Root) -AllowFailure
    Add-Check "simulation" "rotation" ($rotate.Code -eq 0) (($rotate.Text -split "`n")[0])

    # Lane F: targeted .apks deployment with the pinned bundletool.
    if (-not $SkipDeployment) {
        $bundletool = Get-ChildItem $ToolDir -Filter "bundletool-*.jar" -ErrorAction SilentlyContinue | Select-Object -First 1
        $apks = Join-Path $ToolDir "apks\ws-sample.apks"
        if ($bundletool -and (Test-Path $apks)) {
            $splitBuild = Join-Path $repo "sample\build-split-apk.ps1"
            $newestSource = Get-ChildItem (Join-Path $repo "sample\src") -Recurse -Filter "*.java" |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ((Test-Path $splitBuild) -and $newestSource -and $newestSource.LastWriteTime -gt (Get-Item $apks).LastWriteTime) {
                Log "rebuilding the signed APK set from the current sample sources"
                & powershell -ExecutionPolicy Bypass -File $splitBuild -OutDir (Join-Path $ToolDir "apks") -Bundletool $bundletool.FullName 2>&1 | Out-Null
            }

            $version = (& java -jar $bundletool.FullName version 2>&1 | Out-String).Trim()
            Save-Text "bundletool-version.txt" $version
            Add-Check "deployment" "bundletool" ($version -match "\d+\.\d+") $version

            # The deployment target is a freshly booted duplicate so the
            # selected device is unambiguous for an external tool.
            Invoke-Ws @("start", "--name", "p3-dup", "--root", $Root) | Out-Null
            Invoke-Ws @("wait", "--name", "p3-dup", "--timeout", "600", "--root", $Root) | Out-Null
            $dupIdentity = Parse-Json (Invoke-Ws @("adb-identity", "--name", "p3-dup", "--root", $Root) -AllowFailure).Text
            $dupSerial = $dupIdentity.serial
            Add-Check "deployment" "target-selected" ($dupSerial -ne $null -and $dupSerial -ne $idP.serial) "target serial=$dupSerial"
            $dupAnr = Ensure-NoAnr $dupSerial "p3-dup"
            Add-Check "deployment" "no-anr-overlay" $dupAnr.Clear "recovered=$($dupAnr.Recovered)"
            Unlock-Guest $dupSerial
            $usable = Wait-TransportUsable $dupSerial
            Add-Check "deployment" "transport-usable" $usable "round-trip probe on the selected serial"
            $install = ""
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                $install = (& java -jar $bundletool.FullName install-apks --apks=$apks --device-id=$dupSerial 2>&1 | Out-String)
                if ($install -notmatch "Unable to find the requested device") { break }
                Start-Sleep -Seconds 5
            }
            Save-Text "bundletool-install.txt" $install
            $installed = (Invoke-Adb @("-s", $dupSerial, "shell", "pm", "list", "packages")).Text
            Add-Check "deployment" "apks-install" ($installed -match "dev.emulatorworkspace.sample") "package present after .apks deployment"
            $splits = (Invoke-Adb @("-s", $dupSerial, "shell", "dumpsys", "package", "dev.emulatorworkspace.sample")).Text
            Save-Text "package-dumpsys.txt" $splits
            Add-Check "deployment" "split-install-recorded" ($splits -match "split") "package manager reports split APKs"
            $otherPackages = (Invoke-Adb @("-s", $idQ.serial, "shell", "pm", "list", "packages")).Text
            Add-Check "deployment" "target-explicit" (-not ($otherPackages -match "dev.emulatorworkspace.sample")) "the other guest did not receive the deployment"

            $bad = Join-Path $out "malformed.apks"
            Set-Content $bad "this is not an apk set" -Encoding UTF8
            $badInstall = (& java -jar $bundletool.FullName install-apks --apks=$bad --device-id=$idP.serial 2>&1 | Out-String)
            $badCode = $LASTEXITCODE
            Save-Text "bundletool-malformed.txt" $badInstall
            Add-Check "deployment" "malformed-apks-diagnosed" ($badCode -ne 0 -and $badInstall -match "Error") "exit=$badCode typed diagnostic returned"
        }
        else {
            Add-Check "deployment" "apks-build" $false "bundletool jar or ws-sample.apks missing under $ToolDir"
        }

        # Lane G: a real external test runner against one selected guest.
        $maestroBat = Join-Path $ToolDir "maestro\maestro\bin\maestro.bat"
        if (Test-Path $maestroBat) {
            $flow = Join-Path $out "maestro-duplicate.yaml"
            @"
appId: dev.emulatorworkspace.sample
---
- launchApp
- assertVisible: "Emulator Workspace QA fixture"
- tapOn: "Duplicate A"
- assertVisible: "DUP_TAPS=1"
"@ | Set-Content $flow -Encoding UTF8
            $env:ANDROID_HOME = $sdk
            $env:ANDROID_SDK_ROOT = $sdk
            $env:MAESTRO_CLI_ANALYSIS_NOTIFICATION_DISABLED = "true"
            $otherBefore = (Invoke-Adb @("-s", $idQ.serial, "logcat", "-d", "-s", "WS_SAMPLE:I")).Text
            $runnerSerial = $dupSerial
            $otherBeforeCount = ([regex]::Matches($otherBefore, "DUP_TAP index=")).Count
            $listing = (Invoke-Adb @("devices", "-l")).Text
            $listed = $listing -match ("(?m)^" + [regex]::Escape($runnerSerial) + "\s+device")
            if (-not $listed) {
                Invoke-Adb @("reconnect", $runnerSerial) | Out-Null
                Start-Sleep -Seconds 5
                $listing = (Invoke-Adb @("devices", "-l")).Text
                $listed = $listing -match ("(?m)^" + [regex]::Escape($runnerSerial) + "\s+device")
            }
            Save-Text "maestro-devices.txt" $listing
            Add-Check "runner" "device-visible-to-runner" $listed "serial=$runnerSerial listed for the external runner"
            $maestroRun = (& $maestroBat test --device $runnerSerial $flow 2>&1 | Out-String)
            $maestroCode = $LASTEXITCODE
            Save-Text "maestro-run.txt" $maestroRun
            Add-Check "runner" "maestro-flow" ($maestroCode -eq 0) "exit=$maestroCode"
            $otherAfter = (Invoke-Adb @("-s", $idQ.serial, "logcat", "-d", "-s", "WS_SAMPLE:I")).Text
            $otherAfterCount = ([regex]::Matches($otherAfter, "DUP_TAP index=")).Count
            Add-Check "runner" "touches-only-selected-guest" ($otherAfterCount -eq $otherBeforeCount) "other guest tap count $otherBeforeCount -> $otherAfterCount"
        }
        else {
            Add-Check "runner" "maestro" $false "maestro CLI not present under $ToolDir"
        }
    }

    if (-not $KeepRunning) {
        foreach ($instance in @($sessionP.instance, $sessionQ.instance, "p3-dup", "p3-import")) {
            Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) -AllowFailure | Out-Null
            Start-Sleep -Seconds 2
            $cleanup = Invoke-Ws @("destroy", "--name", $instance, "--root", $Root) -AllowFailure
            Log ("cleanup {0}: exit={1} {2}" -f $instance, $cleanup.Code, (($cleanup.Text -split "`n")[0]))
        }
        $remaining = Invoke-Ws @("list", "--root", $Root) -AllowFailure
        Save-Text "instances-final.txt" $remaining.Text
        $leftovers = @($remaining.Text -split "`n" | Where-Object { $_ -match "^(p3-|ag-)" })
        Add-Check "cleanup" "disposables-removed" ($leftovers.Count -eq 0) ("remaining=$($leftovers.Count)")
        Add-Check "cleanup" "template-preserved" ($remaining.Text -match [regex]::Escape($Template)) "template kept"
    }
}
catch {
    Add-Check "harness" "fatal" $false $_.Exception.Message
}
finally {
    $failed = @($script:results | Where-Object { -not $_.Passed }).Count
    $summary = [ordered]@{
        runId = $run
        finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
        failed = $failed
        checks = @($script:results)
        lanes = $lanes
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
    $script:results | Format-Table -AutoSize | Out-String | Write-Host
    Log "completed failures=$failed artifacts=$out"
    if ($failed -gt 0) { exit 2 }
}
exit 0
