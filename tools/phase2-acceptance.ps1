#requires -Version 5.1
<#
.SYNOPSIS
    Phase 2 acceptance: paired MCP and CLI workflows, secure observation,
    guest-only network shaping, authorization failures, artifacts, snapshot
    restore and scoped cleanup.

.DESCRIPTION
    Prerequisites:
      - a supervisor root containing a stopped template instance (default "tpl")
      - the paired sample APK at <root>\apps\ws-sample.apk
      - an active session token file produced by:
          wsctl agent-pair --template <tpl> --out <token-file> --root <root>
      - Python 3 with the "mcp" package for the second real client
    The script starts a 64 KiB login fixture unless one is already running,
    starts the disposable guest, installs/launches the sample, runs the CLI,
    MCP (TypeScript and Python), network, secure-window and negative checks,
    then cleans up with a single-use approval. Artifacts go to
    artifacts/phase2/<run-id>/.

.NOTES
    A running supervisor locks its binaries. Run "dotnet build" before the
    supervisor starts, or pass -SkipBuild after capturing build output.
#>
param(
    [string]$Root = "D:\Phase2Root",
    [string]$TokenFile = "D:\Phase2Root\secure-session.json",
    [string]$Template = "tpl",
    [string]$OutDir = "artifacts\phase2",
    [int]$FixtureBytes = 65536,
    [switch]$SkipBuild,
    [switch]$SkipMcp,
    [switch]$SkipDesktop,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$mcpExe = Join-Path $repo "src\EmulatorWorkspace.Mcp\bin\Debug\net10.0\EmulatorWorkspace.Mcp.exe"
$mcpClient = Join-Path $repo "clients\mcp-client\workflow.mjs"
$mcpSecure = Join-Path $repo "clients\mcp-client\secure-check.mjs"
$pythonClient = Join-Path $repo "clients\mcp-python\client.py"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out "transcript.txt"
$script:checkResults = New-Object System.Collections.ArrayList
$fixtureProcess = $null
$session = Get-Content $TokenFile -Raw | ConvertFrom-Json
$instance = $session.instance
$serial = $null

function Log([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $message
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$script:checkResults.Add([pscustomobject]@{ Check = $name; Passed = $ok; Detail = $detail })
    Log ("{0} {1}: {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail)
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
function Parse-Json([string]$text) {
    try {
        return $text | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Call([string]$op, [string]$json, [string[]]$extra = @()) {
    $argsFile = Join-Path $out ("args-" + [guid]::NewGuid().ToString("N").Substring(0,8) + ".json")
    Set-Content -Path $argsFile -Value $json -Encoding UTF8
    $arguments = @("call", "--op", $op, "--args-file", $argsFile, "--token-file", $TokenFile, "--root", $Root) + $extra
    $result = Invoke-Ws $arguments -AllowFailure
    $result | Add-Member -NotePropertyName Json -NotePropertyValue (Parse-Json $result.Text)
    return $result
}

function Wait-Port([string]$hostName, [int]$port, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $task = $client.ConnectAsync($hostName, $port)
            if ($task.Wait(500) -and $client.Connected) {
                $client.Close()
                return $true
            }

            $client.Close()
        }
        catch {
        }

        Start-Sleep -Milliseconds 300
    }

    return $false
}

function Wait-Adb([string]$serial, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $reconnectAt = Get-Date
    while ((Get-Date) -lt $deadline) {
        $state = (& $adb -s $serial get-state 2>&1 | Out-String).Trim()
        if ($state -eq "device") { return $true }
        if ((Get-Date) -ge $reconnectAt) {
            & $adb -s $serial reconnect 2>&1 | Out-Null
            $reconnectAt = (Get-Date).AddSeconds(5)
        }
        Start-Sleep -Milliseconds 500
    }

    return $false
}

Log "Phase 2 acceptance $run instance=$instance template=$Template root=$Root"

try {
    # Isolated deterministic login fixture.
    $existing = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
        Where-Object { $_.CommandLine -match 'login_fixture\.py' }
    foreach ($process in $existing) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }

    $env:FIXTURE_BODY_BYTES = "$FixtureBytes"
    $env:FIXTURE_LOG = Join-Path $out "fixture.jsonl"
    $env:FIXTURE_PORT = "8765"
    $fixtureProcess = Start-Process -FilePath "py" -ArgumentList "-3", (Join-Path $repo "tools\fixtures\login_fixture.py") -PassThru -WindowStyle Hidden
    $fixtureReady = Wait-Port "127.0.0.1" 8765 10
    Add-Check "fixture" $fixtureReady "127.0.0.1:8765 body=$FixtureBytes"

    $versions = [ordered]@{
        runId = $run
        startedUtc = (Get-Date).ToUniversalTime().ToString("O")
        root = $Root
        instance = $instance
        session = $session.session
        template = $Template
        fixtureBytes = $FixtureBytes
        dotnet = (& dotnet --version 2>&1 | Out-String).Trim()
        node = (& node --version 2>&1 | Out-String).Trim()
        python = (& py -3 --version 2>&1 | Out-String).Trim()
        commit = (& git -C $repo rev-parse --short HEAD 2>&1 | Out-String).Trim()
        workTree = (& git -C $repo status --porcelain 2>&1 | Out-String).Trim()
    }
    $versions | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out "versions.json") -Encoding UTF8

    if (-not $SkipBuild) {
        $build = (& dotnet build -v q --nologo 2>&1 | Out-String)
        $buildCode = $LASTEXITCODE
        Set-Content (Join-Path $out "build.txt") $build -Encoding UTF8
        Add-Check "build" ($buildCode -eq 0) "dotnet build exit=$buildCode"
        $tests = (& dotnet test --no-build -v q --nologo 2>&1 | Out-String)
        $testCode = $LASTEXITCODE
        Set-Content (Join-Path $out "tests.txt") $tests -Encoding UTF8
        Add-Check "unit-tests" ($testCode -eq 0) (([regex]::Match($tests, 'Passed:\s+\d+')).Value)
    }
    else {
        Add-Check "build" $true "skipped (-SkipBuild; pre-captured)"
    }

    $start = Invoke-Ws @("start", "--name", $instance, "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Set-Content (Join-Path $out "boot.txt") ($start.Text + "`n" + $start.Code) -Encoding UTF8
    $wait = Invoke-Ws @("wait", "--name", $instance, "--timeout", "600", "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Add-Content (Join-Path $out "boot.txt") ($wait.Text + "`n" + $wait.Code) -Encoding UTF8
    $serial = ([regex]::Match($wait.Text, "serial\s+:\s+(emulator-\d+)")).Groups[1].Value
    Add-Check "boot" ($wait.Code -eq 0 -and $serial.Length -gt 0) "serial=$serial"

    $status = Call "instance.status" "{}"
    Add-Check "status" ($status.Code -eq 0) (($status.Text -split "`n")[0])
    $fingerprint = ([regex]::Match($wait.Text, "fingerprint\s*:\s*(.+)")).Groups[1].Value.Trim()
    Add-Check "fingerprint" ($fingerprint.Length -gt 0) $fingerprint

    $install = Invoke-Ws @("install", "--name", $instance, "--apk", "sample", "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Add-Check "install" ($install.Code -eq 0) ($install.Text -split "`n")[0]
    $anr = Ensure-NoAnr $serial $instance
    Add-Check "guest.no-anr-overlay" $anr.Clear "recovered=$($anr.Recovered)"
    $launch = Invoke-Ws @("launch", "--name", $instance, "--package", "dev.emulatorworkspace.sample", "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Add-Check "launch" ($launch.Code -eq 0) ($launch.Text -split "`n")[0]
    Start-Sleep -Seconds 3
    $adbReady = Wait-Adb $serial 120
    Add-Check "adb.ready" $adbReady "get-state=device"
    if (-not $adbReady) { throw "the managed ADB transport did not become ready" }

    $baseline = Call "snapshot.save" '{"tag":"phase2-boot-baseline"}'
    Add-Check "baseline.snapshot" ($baseline.Code -eq 0) $baseline.Text

    # CLI workflow on the disposable guest.
    $cliSteps = @()
    function CliStep([string]$name, [string]$json) {
        $result = Call $name $json
        $script:cliSteps += [ordered]@{ op = $name; code = $result.Code; output = $result.Text }
        return $result
    }

    $shot = CliStep "observation.screenshot" "{}"
    Add-Check "cli.screenshot" ($shot.Code -eq 0 -and $shot.Json.artifactId) "artifact=$($shot.Json.artifactId)"
    $frameId = $shot.Json.frameId
    $tree = CliStep "observation.tree" "{}"
    Add-Check "cli.tree" ($tree.Code -eq 0 -and $tree.Json.nodes.Count -gt 0) "nodes=$($tree.Json.nodes.Count)"
    CliStep "input.text" '{"text":"cli-user","selector":{"contentDescription":"login_user"}}' | Out-Null
    CliStep "input.text" '{"text":"cli-pass","selector":{"contentDescription":"login_password"}}' | Out-Null
    CliStep "input.tap" '{"selector":{"contentDescription":"login_button"}}' | Out-Null
    Start-Sleep -Seconds 3
    $assert = CliStep "ui.assert" '{"selector":{"contentDescription":"login_status","text":"WELCOME"}}'
    Add-Check "cli.login" ($assert.Json.pass -eq $true) $assert.Text
    $logs = CliStep "logs.read" '{"lines":2000,"contains":"LOGIN_RESULT"}'
    Add-Check "cli.logs" ($logs.Json.lines -gt 0) "lines=$($logs.Json.lines)"
    $artifact = CliStep "artifact.read" ("{`"artifactId`":`"$($shot.Json.artifactId)`"}")
    Add-Check "cli.artifact" ($artifact.Code -eq 0 -and $artifact.Json.bytes -gt 0) "bytes=$($artifact.Json.bytes)"
    $save = CliStep "snapshot.save" '{"tag":"phase2-cli-baseline"}'
    Add-Check "cli.snapshot.save" ($save.Code -eq 0) $save.Text
    $approval = Invoke-Ws @("agent-approve-action", "--session", $session.session, "--operation", "snapshot.load", "--root", $Root)
    $approvalJson = Parse-Json $approval.Text
    $restore = CliStep "snapshot.load" ("{`"tag`":`"phase2-cli-baseline`",`"approval`":`"$($approvalJson.code)`"}")
    Add-Check "cli.snapshot.restore" ($restore.Code -eq 0) $restore.Text
    # A snapshot load restarts the guest, so the harness waits for interactive
    # readiness again, relaunches the fixture and re-checks the precondition.
    $bootDeadline = (Get-Date).AddSeconds(360)
    $booted = $false
    while ((Get-Date) -lt $bootDeadline) {
        $state = (Invoke-Adb @("-s", $serial, "get-state")).Text
        $boot = (Invoke-Adb @("-s", $serial, "shell", "getprop", "sys.boot_completed")).Text
        if ($state -eq "device" -and $boot -eq "1") { $booted = $true; break }
        Start-Sleep -Seconds 3
    }
    Add-Check "cli.snapshot.restore.readiness" $booted "sys.boot_completed=1 after the snapshot load"
    Invoke-Ws @("launch", "--name", $instance, "--package", "dev.emulatorworkspace.sample", "--token-file", $TokenFile, "--root", $Root) | Out-Null
    $appForeground = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Seconds 3
        $afterRestore = Call "observation.tree" "{}"
        $appForeground = @($afterRestore.Json.nodes | Where-Object { $_.contentDescription -eq "login_button" }).Count -eq 1
        if ($appForeground) { break }
    }
    Add-Check "cli.snapshot.restore.fixture-ready" $appForeground "the fixture is in the foreground after the restore"
    Add-Check "adb.ready.post-restore" (Wait-Adb $serial 120) "get-state=device"
    $cliSteps | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out "cli-transcript.json") -Encoding UTF8

    # Guest-only network shaping: baseline success, shaped timeout, baseline again.
    Add-Check "adb.ready.pre-network" (Wait-Adb $serial 120) "get-state=device"
    $anr2 = Ensure-NoAnr $serial $instance
    Add-Check "guest.no-anr-overlay.pre-network" $anr2.Clear "recovered=$($anr2.Recovered)"
    $netBaseline = Call "network.profile" '{"delay":"none","speed":"full","latencyMs":0}'
    Call "input.tap" '{"selector":{"contentDescription":"login_button"}}' | Out-Null
    Start-Sleep -Seconds 3
    $netOk = Call "ui.assert" '{"selector":{"contentDescription":"login_status","text":"WELCOME"}}'
    $netShape = Call "network.profile" '{"delay":"gprs","speed":"gsm","latencyMs":2500}'
    Call "input.tap" '{"selector":{"contentDescription":"login_button"}}' | Out-Null
    Start-Sleep -Seconds 6
    $netTimeout = Call "ui.assert" '{"selector":{"contentDescription":"login_status","text":"NETWORK TIMEOUT"}}'
    $netReset = Call "network.profile" '{"delay":"none","speed":"full","latencyMs":0}'
    Call "input.tap" '{"selector":{"contentDescription":"login_button"}}' | Out-Null
    Start-Sleep -Seconds 3
    $netRecovered = Call "ui.assert" '{"selector":{"contentDescription":"login_status","text":"WELCOME"}}'
    $netTranscript = [ordered]@{
        baseline = $netBaseline.Text
        baselineAssert = $netOk.Text
        shaped = $netShape.Text
        shapedAssert = $netTimeout.Text
        reset = $netReset.Text
        recoveredAssert = $netRecovered.Text
    }
    $netTranscript | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "network.json") -Encoding UTF8
    Add-Check "network.baseline" ($netBaseline.Code -eq 0 -and $netOk.Json.pass -eq $true) "WELCOME"
    Add-Check "network.shaped" ($netShape.Code -eq 0 -and $netShape.Json.latencyMs -eq 2500 -and $netTimeout.Json.pass -eq $true) "NETWORK TIMEOUT"
    Add-Check "network.recovered" ($netReset.Code -eq 0 -and $netRecovered.Json.pass -eq $true) "WELCOME"

    # CLI secure-window behavior and recovery.
    Call "input.tap" '{"selector":{"contentDescription":"secure_button"}}' | Out-Null
    Start-Sleep -Seconds 1
    $secureShot = Call "observation.screenshot" "{}"
    $secureTree = Call "observation.tree" "{}"
    $secureAssert = Call "ui.assert" '{"selector":{"contentDescription":"login_status"}}'
    Add-Check "secure.cli.screenshot" ($secureShot.Json.status -eq "secure_window" -and $secureShot.Json.secure -eq $true) $secureShot.Text
    Add-Check "secure.cli.tree" ($secureTree.Json.status -eq "secure_window" -and $secureTree.Json.nodes.Count -eq 0) $secureTree.Text
    Add-Check "secure.cli.selector" ($secureAssert.Text -match "secure_window") "selector denied"
    & $adb -s $serial shell input tap 502 581 2>&1 | Out-Null
    Start-Sleep -Seconds 1
    $secureRecovered = Call "observation.screenshot" "{}"
    Add-Check "secure.cli.recovered" ($secureRecovered.Json.secure -eq $false) "capture restored"
    [ordered]@{
        screenshot = $secureShot.Text
        tree = $secureTree.Text
        selector = $secureAssert.Text
        recovered = $secureRecovered.Text
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "secure-cli.json") -Encoding UTF8

    if (-not $SkipMcp) {
        Add-Check "adb.ready.pre-mcp" (Wait-Adb $serial 120) "get-state=device"
        $mcpOut = Join-Path $out "mcp"
        $env:WS_ROOT = $Root
        $env:WS_TOKEN_FILE = $TokenFile
        $env:WS_OUT = $mcpOut
        $env:WS_MCP_EXE = $mcpExe
        $env:WS_WSCTL = $wsctl
        $env:WS_ADB = $adb
        $env:WS_SERIAL = $serial

        Push-Location (Join-Path $repo "clients\mcp-client")
        try {
            $mcpSecureOut = (& node secure-check.mjs 2>&1 | Out-String)
            $mcpSecureCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
        Set-Content (Join-Path $out "mcp-secure.txt") $mcpSecureOut -Encoding UTF8
        $mcpSecureChecks = Parse-Json (($mcpSecureOut -split "`n" | Where-Object { $_ -match '^\{' } | Select-Object -Last 1))
        Add-Check "secure.mcp" ($mcpSecureCode -eq 0 -and $mcpSecureChecks.screenshotRedacted -and $mcpSecureChecks.treeRedacted -and $mcpSecureChecks.selectorDenied -and $mcpSecureChecks.recoveredOk) $mcpSecureOut.Trim()

        Push-Location (Join-Path $repo "clients\mcp-client")
        try {
            $mcpOutText = (& node workflow.mjs 2>&1 | Out-String)
            $mcpCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
        Set-Content (Join-Path $out "mcp-workflow.txt") $mcpOutText -Encoding UTF8
        $mcpSummary = Parse-Json (($mcpOutText -split "`n" | Where-Object { $_ -match '^\{' } | Select-Object -Last 1))
        Add-Check "mcp.workflow" ($mcpCode -eq 0 -and $mcpSummary.login -eq $true -and (Test-Path (Join-Path $mcpOut "mcp-transcript.json"))) $mcpOutText.Trim()

        Push-Location (Join-Path $repo "clients\mcp-python")
        try {
            $pythonOut = (& py -3 client.py 2>&1 | Out-String)
            $pythonCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
        Set-Content (Join-Path $out "python-mcp.txt") $pythonOut -Encoding UTF8
        $pythonSummary = Parse-Json (($pythonOut -split "`n" | Where-Object { $_ -match '^\{' } | Select-Object -Last 1))
        Add-Check "mcp.python" ($pythonCode -eq 0 -and $pythonSummary.ok -eq $true) $pythonOut.Trim()
    }

    if (-not $SkipDesktop) {
        $desktopExe = Join-Path $repo "src\EmulatorWorkspace.Desktop\bin\Debug\net10.0-windows\EmulatorWorkspace.Desktop.exe"
        if (Test-Path $desktopExe) {
            $probePath = Join-Path $out "agent-sessions-probe.json"
            $probeProcess = Start-Process -FilePath $desktopExe `
                -ArgumentList "--root", $Root, "--agent-sessions-probe", "--out", $probePath `
                -PassThru
            $probeDeadline = (Get-Date).AddSeconds(30)
            while (-not $probeProcess.HasExited -and (Get-Date) -lt $probeDeadline) {
                Start-Sleep -Milliseconds 300
                $probeProcess.Refresh()
            }
            if (-not $probeProcess.HasExited) {
                Stop-Process -Id $probeProcess.Id -Force -ErrorAction SilentlyContinue
            }
            $probeJson = $null
            if (Test-Path $probePath) { $probeJson = Get-Content $probePath -Raw | ConvertFrom-Json }
            Add-Check "wpf.agent-sessions" ($probeJson -and $probeJson.sessions -ge 1 -and $probeJson.templates -ge 1) $(if ($probeJson) { $probeJson.status } else { "probe failed" })
        }
        else {
            Add-Check "wpf.agent-sessions" $false "desktop binary not built"
        }
    }

    # Authorization and safety checks.
    Add-Check "adb.ready.pre-security" (Wait-Adb $serial 120) "get-state=device"
    $unpaired = Invoke-Ws @("call", "--op", "observation.screenshot", "--root", $Root) -AllowFailure
    Add-Check "deny.unpaired" ($unpaired.Text -match "unauthorized") "unauthorized"
    $wrongArgs = Join-Path $out "wrong-instance.json"
    Set-Content $wrongArgs ('{"name":"' + $Template + '"}') -Encoding UTF8
    $wrong = Invoke-Ws @("call", "--op", "instance.status", "--args-file", $wrongArgs, "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Add-Check "deny.wrong-instance" ($wrong.Text -match "wrong_instance") $wrong.Text

    $staleShot = Call "observation.screenshot" "{}"
    $staleFrame = $staleShot.Json.frameId
    $staleArgs = Join-Path $out "stale-frame.json"
    Set-Content $staleArgs ("{`"frameId`":`"$staleFrame`",`"x`":540,`"y`":1400}") -Encoding UTF8
    $bump = Invoke-Ws @("call", "--op", "input.tap", "--args-file", $staleArgs, "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Add-Check "stale.bump" ($bump.Code -eq 0) $bump.Text
    $stale = Invoke-Ws @("call", "--op", "input.tap", "--args-file", $staleArgs, "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Add-Check "deny.stale-frame" ($stale.Text -match "stale_observation") $stale.Text

    # The sample exposes two controls with contentDescription "dup_action", so
    # the ambiguous-selector path is deterministic and the exact count is known.
    $duplicateTree = Call "observation.tree" "{}"
    $duplicateCount = @($duplicateTree.Json.nodes | Where-Object { $_.contentDescription -eq "dup_action" }).Count
    Add-Check "fixture.duplicate-selectors" ($duplicateCount -eq 2) "dup_action matches=$duplicateCount"
    $ambiguous = Call "ui.assert" '{"selector":{"contentDescription":"dup_action"}}'
    Add-Check "deny.ambiguous" ($ambiguous.Text -match "ambiguous_selector" -and $ambiguous.Text -match "2 nodes matched") $ambiguous.Text

    $idemArgs = Join-Path $out "idem-a.json"
    $idemOther = Join-Path $out "idem-b.json"
    Set-Content $idemArgs '{"cursor":0}' -Encoding UTF8
    Set-Content $idemOther '{"cursor":1}' -Encoding UTF8
    $idemKey = "phase2-idem-$run"
    $idem1 = Invoke-Ws @("call", "--op", "event.poll", "--args-file", $idemArgs, "--idempotency", $idemKey, "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    $idem2 = Invoke-Ws @("call", "--op", "event.poll", "--args-file", $idemArgs, "--idempotency", $idemKey, "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    $idem3 = Invoke-Ws @("call", "--op", "event.poll", "--args-file", $idemOther, "--idempotency", $idemKey, "--token-file", $TokenFile, "--root", $Root) -AllowFailure
    Add-Check "idempotency.replay" ($idem1.Code -eq 0 -and $idem1.Text -eq $idem2.Text) "cached response identical"
    Add-Check "idempotency.conflict" ($idem3.Text -match "idempotency_conflict") $idem3.Text

    [ordered]@{
        unpaired = $unpaired.Text
        wrongInstance = $wrong.Text
        staleFrame = $stale.Text
        ambiguous = $ambiguous.Text
        idempotentReplay = $idem2.Text
        idempotencyConflict = $idem3.Text
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "security.json") -Encoding UTF8

    if (-not $KeepRunning) {
        $cleanupApproval = Invoke-Ws @("agent-approve-action", "--session", $session.session, "--operation", "device.cleanup", "--root", $Root)
        $cleanupJson = Parse-Json $cleanupApproval.Text
        $cleanup = Call "device.cleanup" ("{`"approval`":`"$($cleanupJson.code)`"}")
        Add-Check "cleanup" ($cleanup.Code -eq 0 -and $cleanup.Json.cleaned -eq $true) $cleanup.Text
        $revoked = Call "observation.screenshot" "{}"
        Add-Check "deny.revoked" ($revoked.Text -match "revoked") $revoked.Text
        $remaining = Invoke-Ws @("list", "--root", $Root) -AllowFailure
        Set-Content (Join-Path $out "cleanup.txt") ($cleanup.Text + "`n" + $revoked.Text + "`n" + $remaining.Text) -Encoding UTF8
        Add-Check "cleanup.template-preserved" ($remaining.Text -match [regex]::Escape($Template)) "template remains"
    }
}
catch {
    Add-Check "acceptance.fatal" $false $_.Exception.Message
}
finally {
    if ($fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

$failed = @($script:checkResults | Where-Object { -not $_.Passed }).Count
$summary = [ordered]@{
    runId = $run
    startedUtc = $versions.startedUtc
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    root = $Root
    instance = $instance
    session = $session.session
    serial = $serial
    failed = $failed
    checks = $script:checkResults
}
$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$script:checkResults | Format-Table -AutoSize | Out-String | Write-Host
Log "completed failures=$failed artifacts=$out"
if ($failed -gt 0) { exit 2 }
exit 0
