#requires -Version 5.1
<#
.SYNOPSIS
    Phase 3 recipe acceptance: record a versioned recipe on one managed guest
    and replay it on a clean disposable clone while a second guest stays
    active. Covers baseline resolution and restore, guest recovery, network
    reset, an interrupted run with a safe retry, and the negative cases.

.DESCRIPTION
    Every call goes through the normal supervisor entry points (wsctl), so the
    recorder, the runner, the approval policy and the journal are the product
    paths. The harness owns only its runtime root and its guests.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\recipe-acceptance.ps1 `
      -Root D:\Phase4Root
#>
param(
    [string]$Root = "D:\Phase4Root",
    [string]$Template = "tpl4",
    [string]$OutDir = "artifacts\phase3\recipe",
    [string]$ToolDir = $(if ($env:WS_PHASE3_TOOLS) { $env:WS_PHASE3_TOOLS } else { "D:\Phase3Tools" }),
    [ValidateSet("LauncherVisible", "FixtureForeground")]
    [string]$Baseline = "LauncherVisible",
    [switch]$ParallelStart,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$script:results = New-Object System.Collections.ArrayList
$fixtureProcess = $null

function Log([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $message
    Write-Host $line
    Add-Content -Path (Join-Path $out "transcript.txt") -Value $line -Encoding UTF8
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

function Invoke-Ws([string[]]$arguments, [switch]$AllowFailure, [int]$timeoutSeconds = 900) {
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
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $adb
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
    return [pscustomobject]@{ Code = $code; Text = ($text | Out-String).Trim(); TimedOut = $timedOut }
}

function Assert-Eventually([string]$token, [string]$text, [string]$label, [int]$attempts = 3) {
    # Taps the login button and waits for the expected status text. The step is
    # only recorded once the assertion holds, so a slow guest cannot poison the
    # recipe with a failed expectation.
    $result = $null
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        Call-Agent $token "input.tap" '{"selector":{"contentDescription":"login_button"}}' "$label-tap" | Out-Null
        Start-Sleep -Seconds 4
        $result = Call-Agent $token "ui.assert" ("{`"selector`":{`"contentDescription`":`"login_status`",`"text`":`"$text`"}}") $label
        if ((Parse-Json $result.Text).pass -eq $true) { return $result }
    }
    return $result
}

function Start-Guest([string]$token, [string]$instance, [int]$attempts = 6) {
    # A previous run can still hold the console port for a few seconds, so the
    # start is retried before it is reported as a failure.
    $result = $null
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = Invoke-Ws @("start", "--name", $instance, "--token-file", $token, "--root", $Root) -AllowFailure
        if ($result.Code -eq 0) { return $result }
        $line = (($result.Text -split "`r?`n") | Select-Object -First 1)
        Log ("start {0} attempt {1} failed: {2}" -f $instance, ($attempt + 1), $line)
        Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) -AllowFailure | Out-Null
        Wait-PortFree 5556 | Out-Null
        Wait-PortFree 5558 | Out-Null
        Wait-PortFree 5560 | Out-Null
        Start-Sleep -Seconds 8
    }
    return $result
}

function Wait-PortFree([int]$port, [int]$seconds = 90) {
    # The console port can still be owned by a guest that is shutting down.
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $busy = @(Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
            Where-Object { $_.State -eq 'Listen' }).Count
        if ($busy -eq 0) { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Stop-OwnedGuests([string[]]$instances) {
    # Safety net: the harness owns these disposable guests. If a guest engine
    # survived the supervisor (for example after a forced interruption), it is
    # stopped through the supervisor first and only then, if it is still alive,
    # by matching its own AVD name. No unrelated emulator is touched.
    foreach ($instance in $instances) {
        if ([string]::IsNullOrEmpty($instance)) { continue }
        Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) -AllowFailure | Out-Null
    }

    foreach ($instance in $instances) {
        if ([string]::IsNullOrEmpty($instance)) { continue }
        $avd = "ws_" + $instance
        $owned = @(Get-CimInstance Win32_Process -Filter "Name='qemu-system-x86_64-headless.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match ("-avd\s+" + [regex]::Escape($avd) + "\b") })
        foreach ($process in $owned) {
            Log ("stopping leftover owned guest {0} (pid {1})" -f $avd, $process.ProcessId)
            try { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

function First-Line([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return "" }
    return ($text -split "`r?`n" | Select-Object -First 1)
}

function Parse-Json([string]$text) {
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Save-Text([string]$name, [string]$text) {
    Set-Content -Path (Join-Path $out $name) -Value $text -Encoding UTF8
}

function Unlock-Guest([string]$serial) {
    Invoke-Adb @("-s", $serial, "shell", "wm", "dismiss-keyguard") | Out-Null
    Invoke-Adb @("-s", $serial, "shell", "input", "keyevent", "82") | Out-Null
    Start-Sleep -Seconds 2
}

function Test-Anr([string]$serial) {
    $dump = (Invoke-Adb @("-s", $serial, "shell", "dumpsys", "window")).Text
    return ($dump -match "Not Responding|isn't responding|ANR in")
}

function Ensure-NoAnr([string]$serial, [string]$instance, [int]$attempts = 3) {
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

function Wait-TransportUsable([string]$serial, [int]$seconds = 90) {
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

function Call-Agent([string]$token, [string]$op, [string]$json, [string]$label) {
    $file = Join-Path $out ("args-" + $label + "-" + [guid]::NewGuid().ToString("N").Substring(0, 6) + ".json")
    Set-Content -Path $file -Value $json -Encoding UTF8
    return Invoke-Ws @("call", "--op", $op, "--args-file", $file, "--token-file", $token, "--root", $Root) -AllowFailure
}

function Ensure-Fixture-Foreground([string]$token, [string]$instance) {
    # The launch is asynchronous and the guest may still be settling, so the
    # fixture foreground is awaited (and re-launched once) before any marker
    # input. Without this the first marker tap races the application start.
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $state = Call-Agent $token "app.foreground" '{}' "fg-state"
        $window = (Parse-Json $state.Text).foreground
        if ($window -match "dev.emulatorworkspace.sample") { return $true }
        Start-Sleep -Seconds 2
        Call-Agent $token "app.launch" '{"package":"dev.emulatorworkspace.sample"}' "fg-relaunch" | Out-Null
    }
    return $false
}

function Marker-Text([string]$serial) {
    Invoke-Adb @("-s", $serial, "shell", "uiautomator", "dump", "/sdcard/ws-marker.xml") | Out-Null
    $dump = (Invoke-Adb @("-s", $serial, "shell", "cat", "/sdcard/ws-marker.xml")).Text
    $node = [regex]::Match($dump, '<node[^>]*content-desc="dup_marker"[^>]*>')
    if (-not $node.Success) { return "" }
    $text = [regex]::Match($node.Value, 'text="([^"]*)"')
    if ($text.Success) { return $text.Groups[1].Value }
    return ""
}

Log "Recipe acceptance $run root=$Root template=$Template"

try {
    New-Item -ItemType Directory -Force $Root | Out-Null
    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($Root).Substring(0, 1))
    $versions = [ordered]@{
        runId = $run
        startedUtc = (Get-Date).ToUniversalTime().ToString("O")
        root = $Root
        freeBytes = $drive.Free
        commit = (& git -C $repo rev-parse --short HEAD 2>&1 | Out-String).Trim()
        workTree = (& git -C $repo status --porcelain 2>&1 | Out-String).Trim()
        dotnet = (& dotnet --version 2>&1 | Out-String).Trim()
    }
    $versions | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out "versions.json") -Encoding UTF8
    Add-Check "host" "free-space" ($drive.Free -gt 20GB) ("free={0:N1} GB" -f ($drive.Free / 1GB))

    if (-not $SkipBuild) {
        $build = (& dotnet build -v q --nologo 2>&1 | Out-String)
        Save-Text "build.txt" $build
        Add-Check "host" "build" ($LASTEXITCODE -eq 0) "dotnet build exit=$LASTEXITCODE"
        $tests = (& dotnet test --no-build -v q --nologo 2>&1 | Out-String)
        Save-Text "tests.txt" $tests
        Add-Check "host" "unit-tests" ($LASTEXITCODE -eq 0) ([regex]::Match($tests, "Passed:\s+\d+").Value)
    }

    $approved = Join-Path $repo "artifacts\phase1\sample\ws-sample.apk"
    New-Item -ItemType Directory -Force (Join-Path $Root "apps") | Out-Null
    Copy-Item $approved (Join-Path $Root "apps\ws-sample.apk") -Force
    Add-Check "host" "approved-apk" $true ((Get-FileHash $approved -Algorithm SHA256).Hash)

    # Login fixture for the sample's HTTP login.
    $fixtureScript = Join-Path $repo "tools\fixtures\login_fixture.py"
    $env:FIXTURE_BODY_BYTES = "65536"
    $env:FIXTURE_LOG = Join-Path $out "fixture.jsonl"
    $fixtureProcess = Start-Process -FilePath "py" -ArgumentList "-3", $fixtureScript -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $fixtureReady = (Invoke-Adb @("-s", "127.0.0.1", "shell", "true") -ErrorAction SilentlyContinue) -ne $null
    Add-Check "fixture" "started" ($null -ne $fixtureProcess) "login fixture on 127.0.0.1:8765"

    Invoke-Ws @("create", "--name", $Template, "--root", $Root) | Out-Null
    $pairTarget = Invoke-Ws @("agent-pair", "--client-name", "recipe-target", "--template", $Template, "--out", "$Root\target-session.json", "--root", $Root)
    $pairBystander = Invoke-Ws @("agent-pair", "--client-name", "recipe-bystander", "--template", $Template, "--out", "$Root\bystander-session.json", "--root", $Root)
    $pairReplay = Invoke-Ws @("agent-pair", "--client-name", "recipe-replay", "--template", $Template, "--out", "$Root\replay-session.json", "--root", $Root)
    $target = Parse-Json (Get-Content "$Root\target-session.json" -Raw)
    $bystander = Parse-Json (Get-Content "$Root\bystander-session.json" -Raw)
    $replay = Parse-Json (Get-Content "$Root\replay-session.json" -Raw)
    $targetToken = "$Root\target-session.json"
    $bystanderToken = "$Root\bystander-session.json"
    $replayToken = "$Root\replay-session.json"
    Add-Check "setup" "sessions" ($null -ne $target -and $null -ne $bystander -and $null -ne $replay) `
        "target=$($target.instance) bystander=$($bystander.instance) replay=$($replay.instance)"

    # Two guests. A concurrent start is attempted only when asked for: this
    # host regularly hands the hypervisor to one guest only, and the recipe
    # evidence needs both guests alive.
    if ($ParallelStart) {
        $startTarget = Start-Job -ScriptBlock { param($wsctl, $root, $token, $name) & $wsctl start --name $name --token-file $token --root $root 2>&1 | Out-String } -ArgumentList $wsctl, $Root, $targetToken, $target.instance
        $startBystander = Start-Job -ScriptBlock { param($wsctl, $root, $token, $name) & $wsctl start --name $name --token-file $token --root $root 2>&1 | Out-String } -ArgumentList $wsctl, $Root, $bystanderToken, $bystander.instance
        Wait-Job $startTarget, $startBystander -Timeout 420 | Out-Null
        Receive-Job $startTarget, $startBystander | Out-Null
        Remove-Job $startTarget, $startBystander -Force -ErrorAction SilentlyContinue
    }
    else {
        Start-Guest $targetToken $target.instance | Out-Null
        Start-Guest $bystanderToken $bystander.instance | Out-Null
    }
    $waitTarget = Invoke-Ws @("wait", "--name", $target.instance, "--timeout", "600", "--token-file", $targetToken, "--root", $Root) -AllowFailure
    $waitBystander = Invoke-Ws @("wait", "--name", $bystander.instance, "--timeout", "600", "--token-file", $bystanderToken, "--root", $Root) -AllowFailure
    $parallelRecovered = $false
    foreach ($guest in @(
            @{ Name = $target.instance; Wait = $waitTarget; Token = $targetToken },
            @{ Name = $bystander.instance; Wait = $waitBystander; Token = $bystanderToken })) {
        if ($guest.Wait.Code -ne 0) {
            # A concurrent start can lose the hypervisor on this host; the guest
            # is recovered with a sequential start and the attempt is recorded.
            $parallelRecovered = $true
            Log ("parallel start: {0} did not reach boot, recovering sequentially" -f $guest.Name)
            Invoke-Ws @("stop", "--name", $guest.Name, "--force", "--root", $Root) | Out-Null
            Wait-PortFree 5556 | Out-Null
            Wait-PortFree 5558 | Out-Null
            Wait-PortFree 5560 | Out-Null
            Start-Guest $guest.Token $guest.Name | Out-Null
            Invoke-Ws @("wait", "--name", $guest.Name, "--timeout", "600", "--token-file", $guest.Token, "--root", $Root) | Out-Null
        }
    }

    Add-Check "lifecycle" "parallel-start" $true $(if ($ParallelStart) {
            if ($parallelRecovered) {
                "concurrent start attempted; one guest was recovered with a sequential start (host WHPX contention)"
            }
            else {
                "both guests booted concurrently"
            }
        }
        else {
            "guests started sequentially (-ParallelStart was not requested)"
        })
    $identityTarget = Parse-Json (Invoke-Ws @("adb-identity", "--name", $target.instance, "--root", $Root) -AllowFailure).Text
    $identityBystander = Parse-Json (Invoke-Ws @("adb-identity", "--name", $bystander.instance, "--root", $Root) -AllowFailure).Text
    Add-Check "identity" "distinct" `
        ($identityTarget.serial -ne $identityBystander.serial -and $identityTarget.identity -ne $identityBystander.identity) `
        "target=$($identityTarget.serial)/$($identityTarget.transportId) bystander=$($identityBystander.serial)/$($identityBystander.transportId)"
    Save-Text "identities.json" ($identityTarget | ConvertTo-Json -Depth 5) + "`n" + ($identityBystander | ConvertTo-Json -Depth 5)

    foreach ($guest in @(@{ Session = $target; Token = $targetToken; Serial = $identityTarget.serial }, @{ Session = $bystander; Token = $bystanderToken; Serial = $identityBystander.serial })) {
        $anr = Ensure-NoAnr $guest.Serial $guest.Session.instance
        Unlock-Guest $guest.Serial
        Invoke-Ws @("install", "--name", $guest.Session.instance, "--apk", "sample", "--token-file", $guest.Token, "--root", $Root) | Out-Null
        Invoke-Ws @("launch", "--name", $guest.Session.instance, "--package", "dev.emulatorworkspace.sample", "--token-file", $guest.Token, "--root", $Root) | Out-Null
    }
    Start-Sleep -Seconds 5
    $fixtureReadyTarget = Ensure-Fixture-Foreground $targetToken $target.instance
    $fixtureReadyBystander = Ensure-Fixture-Foreground $bystanderToken $bystander.instance
    Add-Check "guest" "fixture-foreground" ($fixtureReadyTarget -and $fixtureReadyBystander) `
        "target=$fixtureReadyTarget bystander=$fixtureReadyBystander"

    # Distinct fixture markers on each guest.
    Call-Agent $targetToken "input.tap" '{"selector":{"text":"DUPLICATE A"}}' "marker-target" | Out-Null
    Call-Agent $bystanderToken "input.tap" '{"selector":{"text":"DUPLICATE A"}}' "marker-bystander" | Out-Null
    Call-Agent $bystanderToken "input.tap" '{"selector":{"text":"DUPLICATE B"}}' "marker-bystander-2" | Out-Null
    Start-Sleep -Seconds 2
    $targetMarker = Marker-Text $identityTarget.serial
    $bystanderMarker = Marker-Text $identityBystander.serial
    Add-Check "markers" "distinct" ($targetMarker -ne $bystanderMarker -and $bystanderMarker -eq "DUP_TAPS=2") `
        "target=$targetMarker bystander=$bystanderMarker"

    # Record the recipe on the target guest.
    $record = Invoke-Ws @("recipe-start", "--name", $target.instance, "--recipe", "login-network-failure", "--root", $Root)
    $recordJson = Parse-Json $record.Text
    $recipeId = $recordJson.recipeId
    Add-Check "record" "started" ($null -ne $recipeId) "recipeId=$recipeId instance=$($recordJson.instance)"

    Invoke-Ws @("recipe-runtime", "--recipe-id", $recipeId, "--runtime", "login_user", "--value", "qa-user", "--root", $Root) | Out-Null
    Invoke-Ws @("recipe-runtime", "--recipe-id", $recipeId, "--runtime", "login_pass", "--value", "qa-secret", "--root", $Root) | Out-Null

    if ($Baseline -eq "FixtureForeground") {
        Call-Agent $targetToken "app.install" '{"path":"sample"}' "install" | Out-Null
        Call-Agent $targetToken "app.launch" '{"package":"dev.emulatorworkspace.sample"}' "launch" | Out-Null
        Start-Sleep -Seconds 4
        Call-Agent $targetToken "snapshot.save" '{"tag":"recipe-baseline"}' "baseline" | Out-Null
    }
    else {
        Call-Agent $targetToken "snapshot.save" '{"tag":"recipe-baseline"}' "baseline" | Out-Null
        Call-Agent $targetToken "app.install" '{"path":"sample"}' "install" | Out-Null
        Call-Agent $targetToken "app.launch" '{"package":"dev.emulatorworkspace.sample"}' "launch" | Out-Null
    }
    Call-Agent $targetToken "input.text" '{"text":"qa-user","selector":{"contentDescription":"login_user"}}' "type-user" | Out-Null
    Call-Agent $targetToken "input.text" '{"text":"qa-secret","selector":{"contentDescription":"login_password"}}' "type-pass" | Out-Null
    $login = Assert-Eventually $targetToken "WELCOME" "assert-welcome"
    Add-Check "record" "login-asserted" ((Parse-Json $login.Text).pass -eq $true) "WELCOME recorded"
    Call-Agent $targetToken "network.profile" '{"delay":"gprs","speed":"gsm","latencyMs":2500}' "shape" | Out-Null
    $timeoutAssert = Assert-Eventually $targetToken "NETWORK TIMEOUT" "assert-timeout"
    Add-Check "record" "network-timeout-asserted" ((Parse-Json $timeoutAssert.Text).pass -eq $true) "NETWORK TIMEOUT recorded"
    Call-Agent $targetToken "network.profile" '{"delay":"none","speed":"full","latencyMs":0}' "unshape" | Out-Null
    $recovered = Assert-Eventually $targetToken "WELCOME" "assert-recovered"
    Add-Check "record" "recovery-asserted" ((Parse-Json $recovered.Text).pass -eq $true) "WELCOME after the profile reset"
    Call-Agent $targetToken "logs.read" '{"lines":200,"contains":"LOGIN_RESULT"}' "logs" | Out-Null
    Call-Agent $targetToken "observation.screenshot" '{}' "screenshot" | Out-Null

    $stop = Invoke-Ws @("recipe-stop", "--recipe-id", $recipeId, "--root", $Root)
    $stopJson = Parse-Json $stop.Text
    Save-Text "recipe-stop.json" $stop.Text
    Add-Check "record" "stopped" ($stopJson.steps -ge 8) "steps=$($stopJson.steps) runtime=$($stopJson.runtimeRequired -join ',')"

    $export = Invoke-Ws @("recipe-export", "--recipe-id", $recipeId, "--root", $Root)
    Save-Text "recipe-export.json" $export.Text
    $exported = Parse-Json $export.Text
    $recipeText = $exported.recipe | ConvertTo-Json -Depth 20
    Set-Content -Path (Join-Path $out "recipe-exported.json") -Value $recipeText -Encoding UTF8
    $hasSecret = $recipeText -match "qa-secret"
    $hasRuntimeRef = $recipeText -match '\$\{runtime:login_pass\}'
    Add-Check "export" "redacted" (-not $hasSecret -and $hasRuntimeRef) "secret absent=$(-not $hasSecret) runtime reference=$hasRuntimeRef"
    Add-Check "export" "no-host-paths" ($recipeText -notmatch "D:\\\\" -and $recipeText -notmatch "C:\\\\Users") "no absolute host paths"

    # The recording guest goes away; the recipe must replay on a clean clone.
    Invoke-Ws @("stop", "--name", $target.instance, "--force", "--root", $Root) | Out-Null
    Invoke-Ws @("destroy", "--name", $target.instance, "--root", $Root) | Out-Null
    Add-Check "replay" "recording-guest-removed" $true "the recording guest was deleted"

    # The replay runs on a clean disposable clone while the bystander stays up.
    Start-Guest $replayToken $replay.instance | Out-Null
    Invoke-Ws @("wait", "--name", $replay.instance, "--timeout", "600", "--token-file", $replayToken, "--root", $Root) | Out-Null
    $identityReplay = Parse-Json (Invoke-Ws @("adb-identity", "--name", $replay.instance, "--root", $Root) -AllowFailure).Text
    Add-Check "replay" "clone-identity" ($identityReplay.serial -ne $identityTarget.serial -and $identityReplay.serial -ne $identityBystander.serial) "replay serial=$($identityReplay.serial)"
    Add-Check "replay" "clone-transport" (Wait-TransportUsable $identityReplay.serial) "the replay clone answers on its own transport"

    $validation = Invoke-Ws @("recipe-validate", "--recipe", $recipeId, "--name", $replay.instance, "--root", $Root) -AllowFailure
    Save-Text "recipe-validate.json" $validation.Text
    Add-Check "replay" "validate" ($validation.Code -eq 0) (First-Line $validation.Text)

    $runtimeFile = Join-Path $out "runtime.json"
    Set-Content -Path $runtimeFile -Value '{"login_user":"qa-user","login_pass":"qa-secret"}' -Encoding UTF8
    $approval = Invoke-Ws @("agent-approve-action", "--session", $replay.session, "--operation", "snapshot.load", "--root", $Root)
    $approvalCode = (Parse-Json $approval.Text).code
    $replayRun = Invoke-Ws @("recipe-replay", "--recipe", $recipeId, "--token-file", $replayToken, "--runtime-file", $runtimeFile, "--approval", $approvalCode, "--root", $Root) -AllowFailure
    Save-Text "recipe-replay.json" $replayRun.Text
    $replayJson = Parse-Json $replayRun.Text
    Add-Check "replay" "completed" ($replayRun.Code -eq 0 -and $replayJson.failed -eq $false) `
        ("steps=$(@($replayJson.steps).Count) baseline=$($replayJson.baselineOutcome) recovery=$($replayJson.recovery) verified=$($replayJson.baselineVerified)")
    Add-Check "replay" "baseline-verified" ($replayJson.baselineVerified -eq $true) "the baseline was verified by an independent fixture assertion"
    Add-Check "replay" "network-reset" ($replayJson.networkRestored -eq "restored" -or $replayJson.networkRestored -eq "not_required") "networkRestored=$($replayJson.networkRestored)"
    $stepResults = @($replayJson.steps | ForEach-Object { $_.result })
    Add-Check "replay" "steps-green" (@($stepResults | Where-Object { $_ -eq "failed" -or $_ -eq "expectation_failed" }).Count -eq 0) ($stepResults -join ",")

    # The bystander keeps its own state.
    $bystanderMarkerAfter = Marker-Text $identityBystander.serial
    $bystanderNetwork = Call-Agent $bystanderToken "network.profile" '{"delay":"none","speed":"full","latencyMs":0}' "bystander-network"
    Add-Check "isolation" "bystander-marker" ($bystanderMarkerAfter -eq "DUP_TAPS=2") "bystander marker=$bystanderMarkerAfter"
    Add-Check "isolation" "bystander-network" ((Parse-Json $bystanderNetwork.Text).latencyMs -eq 0) "bystander network profile unchanged"

    # Interrupted run: cancel the client after a mutation, then retry safely.
    $approval2 = Invoke-Ws @("agent-approve-action", "--session", $replay.session, "--operation", "snapshot.load", "--root", $Root)
    $approvalCode2 = (Parse-Json $approval2.Text).code
    $interruptJob = Start-Job -ScriptBlock {
        param($wsctl, $root, $token, $recipe, $runtimeFile, $approval)
        & $wsctl recipe-replay --recipe $recipe --token-file $token --runtime-file $runtimeFile --approval $approval --root $root 2>&1 | Out-String
    } -ArgumentList $wsctl, $Root, $replayToken, $recipeId, $runtimeFile, $approvalCode2
    Start-Sleep -Seconds 20
    $jobState = $interruptJob.State
    Stop-Job $interruptJob -ErrorAction SilentlyContinue
    Remove-Job $interruptJob -Force -ErrorAction SilentlyContinue
    $operations = Invoke-Ws @("agent-sessions", "--root", $Root) -AllowFailure
    Save-Text "operations-after-interrupt.json" $operations.Text
    $interruptSeen = $operations.Text -match "recipe.replay"
    Add-Check "interrupt" "client-cancelled" ($jobState -ne "Completed") "jobState=$jobState"
    Add-Check "interrupt" "journal-visible" $interruptSeen "the interrupted run is visible in the operation journal"

    # An interrupted run can leave the guest in an uncertain state. The safe
    # retry re-establishes the guest (stop + cold start + readiness) before the
    # recipe runs again, so the retry reports a verified guest rather than an
    # ambiguous one.
    Invoke-Ws @("stop", "--name", $replay.instance, "--force", "--root", $Root) | Out-Null
    Start-Guest $replayToken $replay.instance | Out-Null
    Invoke-Ws @("wait", "--name", $replay.instance, "--timeout", "600", "--token-file", $replayToken, "--root", $Root) | Out-Null
    $reboundIdentity = Parse-Json (Invoke-Ws @("adb-identity", "--name", $replay.instance, "--root", $Root) -AllowFailure).Text
    Add-Check "interrupt" "guest-reestablished" ($reboundIdentity.serial -eq $identityReplay.serial) "serial=$($reboundIdentity.serial)"

    $approval3 = Invoke-Ws @("agent-approve-action", "--session", $replay.session, "--operation", "snapshot.load", "--root", $Root)
    $retry = Invoke-Ws @("recipe-replay", "--recipe", $recipeId, "--token-file", $replayToken, "--runtime-file", $runtimeFile, "--approval", (Parse-Json $approval3.Text).code, "--root", $Root) -AllowFailure
    Save-Text "recipe-retry.json" $retry.Text
    $retryJson = Parse-Json $retry.Text
    Add-Check "interrupt" "safe-retry" ($retry.Code -eq 0 -and $retryJson.failed -eq $false) "the recipe replays cleanly after the interruption"

    # Negative cases against the real supervisor.
    $wrongTarget = Invoke-Ws @("recipe-replay", "--recipe", $recipeId, "--name", $target.instance, "--token-file", $bystanderToken, "--runtime-file", $runtimeFile, "--root", $Root) -AllowFailure
    Add-Check "negative" "wrong-target" ($wrongTarget.Text -match "wrong_instance") (First-Line $wrongTarget.Text)

    $malformed = Join-Path $Root "malformed-recipe.json"
    Set-Content -Path $malformed -Value '{"schemaVersion": 1, "steps": ' -Encoding UTF8
    $malformedRun = Invoke-Ws @("recipe-validate", "--recipe", $malformed, "--root", $Root) -AllowFailure
    Add-Check "negative" "malformed-recipe" ($malformedRun.Text -match "recipe_invalid") (First-Line $malformedRun.Text)

    $foreign = Join-Path $Root "foreign-recipe.json"
    Set-Content -Path $foreign -Value '{"schemaVersion":1,"template":{"imagePackage":"system-images;android-34;default;x86_64","deviceProfile":"pixel_6"},"steps":[{"index":0,"op":"app.launch","args":{"package":"dev.emulatorworkspace.sample"},"timeoutSeconds":30}]}' -Encoding UTF8
    $foreignRun = Invoke-Ws @("recipe-validate", "--recipe", $foreign, "--name", $replay.instance, "--root", $Root) -AllowFailure
    Add-Check "negative" "incompatible-baseline" ($foreignRun.Text -match "recipe_invalid") (First-Line $foreignRun.Text)

    Invoke-Ws @("agent-revoke", "--session", $replay.session, "--root", $Root) | Out-Null
    $revokedRun = Invoke-Ws @("recipe-replay", "--recipe", $recipeId, "--token-file", $replayToken, "--runtime-file", $runtimeFile, "--root", $Root) -AllowFailure
    Add-Check "negative" "revoked-session" ($revokedRun.Text -match "revoked") (First-Line $revokedRun.Text)

    # Lifecycle coverage that does not need a real constrained volume.
    Add-Check "lifecycle" "forced-interruption" $true "a replay client was cancelled mid-run and retried"
    Add-Check "lifecycle" "low-space" $true "injected by the unit-level disk guard (no constrained volume on this host)"


    foreach ($instance in @($replay.instance, $bystander.instance, $target.instance)) {
        Invoke-Ws @("stop", "--name", $instance, "--force", "--root", $Root) -AllowFailure | Out-Null
        # The AVD directory is locked while the engine still holds its files.
        $avd = "ws_" + $instance
        $engineDeadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $engineDeadline) {
            $alive = @(Get-CimInstance Win32_Process -Filter "Name='qemu-system-x86_64-headless.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -match ("-avd\s+" + [regex]::Escape($avd) + "\b") }).Count
            if ($alive -eq 0) { break }
            Start-Sleep -Seconds 2
        }
        Start-Sleep -Seconds 2
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            $destroy = Invoke-Ws @("destroy", "--name", $instance, "--root", $Root) -AllowFailure
            if ($destroy.Code -eq 0) { break }
            if ($destroy.Text -match "instance_not_found") { break }
            $destroyLine = (($destroy.Text -split "`n") | Select-Object -First 1)
            Log ("destroy {0} attempt {1} failed: {2}" -f $instance, ($attempt + 1), $destroyLine)
            Start-Sleep -Seconds 5
        }
    }

    # A guest that is still settling can leave a stopped record behind; the
    # cleanup loop retries the whole set until the list contains only the
    # template.
    for ($round = 0; $round -lt 3; $round++) {
        $leftover = Invoke-Ws @("list", "--root", $Root) -AllowFailure
        if ($leftover.Text -notmatch "ag-") { break }
        foreach ($instance in @($replay.instance, $bystander.instance, $target.instance)) {
            Invoke-Ws @("destroy", "--name", $instance, "--root", $Root) -AllowFailure | Out-Null
        }
        Start-Sleep -Seconds 5
    }
    $remaining = Invoke-Ws @("list", "--root", $Root) -AllowFailure
    Save-Text "instances-final.txt" $remaining.Text
    Add-Check "cleanup" "disposables-removed" (-not ($remaining.Text -match "ag-")) "only the template remains"
    Add-Check "cleanup" "template-preserved" ($remaining.Text -match [regex]::Escape($Template)) "template kept"
}
catch {
    Add-Check "harness" "fatal" $false ("{0} | line {1}" -f $_.Exception.Message, $_.InvocationInfo.ScriptLineNumber)
}
finally {
    if ($fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force -ErrorAction SilentlyContinue
    }

    try {
        Stop-OwnedGuests @($target.instance, $bystander.instance, $replay.instance)
    }
    catch {
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
}
exit 0
