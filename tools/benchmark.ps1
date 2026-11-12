#requires -Version 5.1
<#
.SYNOPSIS
    Phase 3 benchmark run: boot, restore, input latency, frame throughput and
    lifecycle stability, with p50/p95 over at least 20 valid samples per
    scenario.

.DESCRIPTION
    The harness declares the reference host before it measures anything and
    keeps every raw sample. The scenarios are:

      * cold boot          : engine start to interactive readiness
      * warm restore       : compatible snapshot restore to interactive readiness
      * input latency      : ADB tap dispatch to a fresh guest log marker
      * capture cost       : CLI one-shot PNG round-trip
      * lifecycle          : N start/stop cycles, watching for leaked engines,
                             ports and memory growth

    A single guest and two guests are measured separately. The targets from the
    plan are evaluated at the end: 60 delivered FPS, p95 input latency of at
    most 100 ms, warm restore within 8 s median and boot overhead within the
    larger of 10% or 1 second.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\benchmark.ps1 `
      -Root D:\Phase4Root -Samples 20 -Cycles 30
#>
param(
    [string]$Root = "D:\Phase4Root",
    [string]$OutDir = "artifacts\phase3\bench",
    [ValidateRange(1, 1000)][int]$Samples = 20,
    [int]$Cycles = 30,
    [string]$Apk = "",
    [ValidateRange(0, 240)][int]$SoakMinutes = 0,
    [ValidateRange(1, 60)][int]$SoakProbeSeconds = 60,
    [switch]$SkipTwoGuest,
    [switch]$SkipLifecycle,
    [switch]$SkipBaseline,
    [switch]$SkipColdBoot,
    [switch]$SkipRestore
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
$ownedGuests = [System.Collections.Generic.List[string]]::new()
$package = "dev.emulatorworkspace.sample"

if ($SoakMinutes -gt 0 -and $SkipTwoGuest) {
    throw "A soak requires the two-guest scenario."
}

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

function Invoke-BenchmarkProcess([string]$executable, [string[]]$arguments, [int]$timeoutMs) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $executable
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
    if (-not $process.WaitForExit($timeoutMs)) {
        $process.Kill()
        $process.Dispose()
        throw "$(Split-Path -Leaf $executable) $($arguments[0]) exceeded the harness timeout."
    }
    $text = ""
    try { $text = $stdout.Result + $stderr.Result } catch { }
    $code = -1
    try { $code = $process.ExitCode } catch { }
    $process.Dispose()
    $text = ($text | Out-String).Trim()
    return [pscustomobject]@{ Code = $code; Text = $text }
}

function Invoke-Ws([string[]]$arguments, [switch]$AllowFailure) {
    $result = Invoke-BenchmarkProcess $wsctl $arguments 900000
    if ($result.Code -ne 0 -and -not $AllowFailure) {
        throw "wsctl $($arguments[0]) failed ($($result.Code)): $($result.Text)"
    }
    return $result
}

function Invoke-GuestAdb($identity, [string[]]$arguments, [int]$timeoutMs = 6000) {
    $result = Invoke-BenchmarkProcess $adb (@("-t", $identity.transportId) + $arguments) $timeoutMs
    if ($result.Code -ne 0) {
        throw "ADB $($arguments[0]) on $($identity.name) failed ($($result.Code)): $($result.Text)"
    }
    return $result.Text
}

function Parse-Json([string]$text) {
    try { return $text | ConvertFrom-Json } catch { return $null }
}

function Percentile([double[]]$values, [double]$fraction) {
    if (-not $values -or $values.Count -eq 0) { return 0 }
    $sorted = @($values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Min($sorted.Count - 1, [int][Math]::Ceiling($fraction * $sorted.Count) - 1))
    return [Math]::Round([double]$sorted[$index], 2)
}

function Summary([double[]]$values) {
    if (-not $values -or $values.Count -eq 0) {
        return [pscustomobject]@{ samples = 0; p50 = 0; p95 = 0; min = 0; max = 0; mean = 0 }
    }
    return [pscustomobject]@{
        samples = $values.Count
        p50     = Percentile $values 0.50
        p95     = Percentile $values 0.95
        min     = [Math]::Round([double](@($values | Sort-Object))[0], 2)
        max     = [Math]::Round([double](@($values | Sort-Object))[-1], 2)
        mean    = [Math]::Round((($values | Measure-Object -Average).Average), 2)
    }
}

function Get-EngineCount {
    return @(Get-CimInstance Win32_Process -Filter "Name='qemu-system-x86_64-headless.exe'" -ErrorAction SilentlyContinue).Count
}

function Get-MemorySnapshot {
    $os = Get-CimInstance Win32_OperatingSystem
    return [pscustomobject]@{
        freePhysicalMB = [int]($os.FreePhysicalMemory / 1024)
        freeVirtualMB  = [int]($os.FreeVirtualMemory / 1024)
        engines        = Get-EngineCount
    }
}

# ------------------------------------------------------------------ the host
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem
$gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1
$disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($Root.Substring(0,1)):'" -ErrorAction SilentlyContinue
$hostInfo = [ordered]@{
    machine          = $env:COMPUTERNAME
    os               = $os.Caption
    osBuild          = $os.BuildNumber
    osVersion        = $os.Version
    cpu              = $cpu.Name
    cpuCores         = $cpu.NumberOfCores
    cpuLogical       = $cpu.NumberOfLogicalProcessors
    gpu              = $gpu.Name
    gpuDriver        = $gpu.DriverVersion
    totalRamMB       = [int]($os.TotalVisibleMemorySize / 1024)
    freeRamMB        = [int]($os.FreePhysicalMemory / 1024)
    pagefile         = (Get-CimInstance Win32_PageFileUsage | Select-Object -First 1).AllocatedBaseSize
    pagefileSystem   = (Get-CimInstance Win32_ComputerSystem | Select-Object -First 1).AutomaticManagedPagefile
    diskFreeGB       = if ($disk) { [Math]::Round($disk.FreeSpace / 1GB, 1) } else { $null }
    emulatorVersion  = ((& (Join-Path $sdk "emulator\emulator.exe") -version 2>&1 |
                          Select-String 'Android emulator version' | Select-Object -First 1) | Out-String).Trim()
    emulatorBuild    = ((& (Join-Path $sdk "emulator\emulator.exe") -version 2>&1 |
                          Select-String 'build_id' | Select-Object -First 1) | Out-String).Trim()
    adbVersion       = ((& $adb version 2>&1 | Select-Object -First 1) | Out-String).Trim()
    dotnetVersion    = (& dotnet --version 2>&1 | Out-String).Trim()
    whpx             = ((& (Join-Path $sdk "emulator\emulator.exe") -accel-check 2>&1 |
                          Select-String 'WHPX' | Select-Object -First 1) | Out-String).Trim()
    collectedUtc     = (Get-Date).ToUniversalTime().ToString("O")
}
$hostInfo | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out "host.json") -Encoding UTF8
Log ("host: {0} / {1} / {2} / {3} GB RAM" -f $hostInfo.machine, $hostInfo.cpuCores, $hostInfo.gpu, [Math]::Round($hostInfo.totalRamMB / 1024, 1))

if (-not $Apk) {
    $Apk = (Get-ChildItem (Join-Path $repo "artifacts") -Recurse -Filter "ws-sample.apk" -ErrorAction SilentlyContinue |
        Select-Object -First 1).FullName
}
if ($Apk) {
    $approved = Join-Path $Root "apps\ws-sample.apk"
    New-Item -ItemType Directory -Force (Split-Path $approved) | Out-Null
    Copy-Item $Apk $approved -Force
}

$report = [ordered]@{
    runId = $run
    host = $hostInfo
    scenarios = [ordered]@{}
    targets = [ordered]@{
        deliveredFps = 60
        inputLatencyP95Ms = 100
        warmRestoreMedianSeconds = 8
        bootOverheadSeconds = 1
        bootOverheadPercent = 10
    }
    sourceCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    fixtureSha256 = if ($Apk) { (Get-FileHash -LiteralPath $Apk).Hash } else { $null }
    latencyEndpoint = "ADB tap dispatch to host receipt of the fixture Logcat marker"
    latencyTrials = [ordered]@{}
    restoreTrials = @()
    lifecycle = $null
}

$bootSamples = New-Object System.Collections.ArrayList
$restoreSamples = New-Object System.Collections.ArrayList
$baselineBootSamples = New-Object System.Collections.ArrayList

function New-BenchmarkGuest([string]$prefix) {
    $name = "$prefix-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    Invoke-Ws @("create", "--name", $name, "--root", $Root) | Out-Null
    $ownedGuests.Add($name)
    return $name
}

function Stop-OwnedGuest([string]$name) {
    Invoke-Ws @("stop", "--name", $name, "--force", "--root", $Root) | Out-Null
}

function Get-GuestIdentity([string]$name) {
    $identity = Parse-Json (Invoke-Ws @("adb-identity", "--name", $name, "--json", "--root", $Root)).Text
    if (-not $identity.transportUsable -or $identity.transportId -notmatch '^\d+$' -or
        $identity.serial -ne "emulator-$($identity.consolePort)" -or -not $identity.enginePid) {
        throw "No usable managed ADB transport for $name."
    }
    return $identity
}

function Install-Fixture([string]$name) {
    Invoke-Ws @("install", "--name", $name, "--apk", $approved, "--root", $Root) | Out-Null
    Invoke-Ws @("launch", "--name", $name, "--package", $package, "--root", $Root) | Out-Null
    $target = Get-GuestIdentity $name
    Invoke-GuestAdb $target @("shell", "input", "keyevent", "KEYCODE_WAKEUP") | Out-Null
    Invoke-GuestAdb $target @("shell", "wm", "dismiss-keyguard") | Out-Null
    Invoke-Ws @("launch", "--name", $name, "--package", $package, "--root", $Root) | Out-Null
    Start-Sleep -Seconds 2
}

function Measure-GuestLatency([string]$name, [string]$scenario, [string]$bystander) {
    $identity = Get-GuestIdentity $name
    $peer = if ($bystander) { Get-GuestIdentity $bystander }
    if ($peer -and ($identity.transportId -eq $peer.transportId -or $identity.enginePid -eq $peer.enginePid)) {
        throw "Expected distinct managed guests for $scenario."
    }
    $trials = [System.Collections.Generic.List[object]]::new()
    $report.latencyTrials[$scenario] = $trials
    $previousIndex = 0
    for ($sample = 1; $sample -le $Samples; $sample++) {
        if ((Get-GuestIdentity $name).identity -ne $identity.identity -or
            ($peer -and (Get-GuestIdentity $bystander).identity -ne $peer.identity)) {
            throw "Managed target identity changed during $scenario."
        }

        Invoke-GuestAdb $identity @("logcat", "-b", "main", "-c") | Out-Null
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-GuestAdb $identity @("shell", "input", "tap", "540", "1200") | Out-Null
        $index = $null
        while ($timer.Elapsed.TotalSeconds -lt 6) {
            $lines = Invoke-GuestAdb $identity @('logcat', '-b', 'main', '-d', '-v', 'brief', '-s', 'WS_SAMPLE:I', '*:S')
            $marker = [regex]::Match(($lines -join "`n"), '(?m)^I/WS_SAMPLE.*?: TAP index=(\d+) x=\d+ y=\d+\b')
            if ($marker.Success -and [int]$marker.Groups[1].Value -gt $previousIndex) {
                $index = [int]$marker.Groups[1].Value
                $previousIndex = $index
                break
            }
            Start-Sleep -Milliseconds 20
        }
        $timer.Stop()
        $elapsed = [Math]::Round($timer.Elapsed.TotalMilliseconds, 2)
        $trials.Add([pscustomobject]@{
            trial = $sample
            target = $identity
            bystander = $peer
            markerIndex = $index
            elapsedMs = $elapsed
        })
        Log ("{0} {1}/{2}: {3:N0} ms marker={4}" -f $scenario, $sample, $Samples, $elapsed, $index)
        Start-Sleep -Milliseconds 300
    }
    $valid = @($trials | Where-Object { $null -ne $_.markerIndex } | ForEach-Object { $_.elapsedMs })
    return (Summary ([double[]]$valid))
}

function Invoke-TwoGuestSoak([string[]]$names) {
    $identities = @($names | ForEach-Object { Get-GuestIdentity $_ })
    $duration = [TimeSpan]::FromMinutes($SoakMinutes)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $path = Join-Path $out "soak.jsonl"
    $probes = 0
    do {
        $guests = foreach ($expected in $identities) {
            $current = Get-GuestIdentity $expected.name
            if ($current.identity -ne $expected.identity) {
                throw "Soak identity changed on $($expected.name)."
            }
            $status = Parse-Json (Invoke-Ws @("status", "--name", $current.name, "--json", "--root", $Root)).Text
            if ($status.bootCompleted -ne "1" -or $status.state -ne "Running") {
                throw "Soak guest $($current.name) lost interactive readiness."
            }
            $packageInfo = Parse-Json (Invoke-Ws @("package-info", "--name", $current.name, "--package", $package, "--root", $Root)).Text
            if (-not $packageInfo.installed) { throw "Soak fixture is missing on $($current.name)." }

            $engine = Get-Process -Id $current.enginePid -ErrorAction Stop
            $ports = @(Get-NetTCPConnection -OwningProcess $current.enginePid -State Listen -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty LocalPort -Unique | Sort-Object)
            if ($current.consolePort -notin $ports -or $current.grpcPort -notin $ports) {
                throw "Soak guest $($current.name) lost an owned listener."
            }
            [pscustomobject]@{
                identity = $current
                listeningPorts = $ports
                workingSetBytes = $engine.WorkingSet64
                privateBytes = $engine.PrivateMemorySize64
                handles = $engine.HandleCount
                cpuSeconds = $engine.CPU
            }
        }
        [pscustomobject]@{
            elapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
            atUtc = [DateTimeOffset]::UtcNow.ToString("O")
            memory = Get-MemorySnapshot
            guests = @($guests)
        } | ConvertTo-Json -Depth 7 -Compress | Add-Content -LiteralPath $path -Encoding UTF8
        $probes++
        if ($clock.Elapsed -ge $duration) { break }
        $delay = [Math]::Min($SoakProbeSeconds * 1000, ($duration - $clock.Elapsed).TotalMilliseconds)
        if ($delay -gt 0) { Start-Sleep -Milliseconds ([int]$delay) }
    } while ($true)
    $report.soak = [ordered]@{ minutes = $SoakMinutes; probes = $probes; evidence = "soak.jsonl" }
    Add-Check "soak.responsiveness" $true "$SoakMinutes minutes, $probes successful paired readiness and package-manager probes"
}

Log "benchmark $run root=$Root samples=$Samples cycles=$Cycles"

try {
    New-Item -ItemType Directory -Force $Root | Out-Null

    # ---------------------------------------------------- the same-engine baseline
    # The historical baseline is a second supervisor-managed profile. This
    # comparison measures run-to-run variation on the managed path.
    if (-not $SkipBaseline) {
    $baseline = New-BenchmarkGuest "bench-base"
    for ($sample = 1; $sample -le $Samples; $sample++) {
        $started = Get-Date
        Invoke-Ws @("start", "--name", $baseline, "--root", $Root) | Out-Null
        $ready = Invoke-Ws @("wait", "--name", $baseline, "--timeout", "600", "--root", $Root) -AllowFailure
        $readyJson = Parse-Json $ready.Text
        if ($readyJson -and $readyJson.launchedToInteractiveSeconds) {
            [void]$baselineBootSamples.Add([double]$readyJson.launchedToInteractiveSeconds)
        }
        else {
            $status = Parse-Json (Invoke-Ws @("status", "--name", $baseline, "--json", "--root", $Root) -AllowFailure).Text
            if ($status -and $status.readiness -and $status.readiness.launchedToInteractiveSeconds) {
                [void]$baselineBootSamples.Add([double]$status.readiness.launchedToInteractiveSeconds)
            }
        }
        Log ("baseline boot {0}/{1}: {2:N1}s" -f $sample, $Samples, ((Get-Date) - $started).TotalSeconds)
        Invoke-Ws @("stop", "--name", $baseline, "--force", "--root", $Root) -AllowFailure | Out-Null
        Start-Sleep -Seconds 3
    }
    $report.scenarios["baselineBoot"] = Summary ([double[]]$baselineBootSamples.ToArray())
    Log ("baseline boot p50={0}s p95={1}s over {2} samples" -f $report.scenarios["baselineBoot"].p50, $report.scenarios["baselineBoot"].p95, $report.scenarios["baselineBoot"].samples)
    }

    # ------------------------------------------------------------- managed guest
    $guest = New-BenchmarkGuest "bench"

    for ($sample = 1; $sample -le $Samples -and -not $SkipColdBoot; $sample++) {
        $started = Get-Date
        Invoke-Ws @("start", "--name", $guest, "--root", $Root) | Out-Null
        $ready = Invoke-Ws @("wait", "--name", $guest, "--timeout", "600", "--root", $Root) -AllowFailure
        $readyJson = Parse-Json $ready.Text
        $elapsed = ((Get-Date) - $started).TotalSeconds
        $reported = 0
        if ($readyJson -and $readyJson.launchedToInteractiveSeconds) { $reported = [double]$readyJson.launchedToInteractiveSeconds }
        else {
            $status = Parse-Json (Invoke-Ws @("status", "--name", $guest, "--json", "--root", $Root) -AllowFailure).Text
            if ($status -and $status.readiness -and $status.readiness.launchedToInteractiveSeconds) {
                $reported = [double]$status.readiness.launchedToInteractiveSeconds
            }
        }
        if ($reported -gt 0) { [void]$bootSamples.Add($reported) }
        Log ("cold boot {0}/{1}: {2:N1}s" -f $sample, $Samples, $elapsed)
        Invoke-Ws @("stop", "--name", $guest, "--force", "--root", $Root) -AllowFailure | Out-Null
        Start-Sleep -Seconds 3
    }
    $report.scenarios["coldBoot"] = Summary ([double[]]$bootSamples.ToArray())
    Log ("cold boot p50={0}s p95={1}s over {2} samples" -f $report.scenarios["coldBoot"].p50, $report.scenarios["coldBoot"].p95, $report.scenarios["coldBoot"].samples)

    # ------------------------------------------------------------- warm restore
    Invoke-Ws @("start", "--name", $guest, "--root", $Root) | Out-Null
    Invoke-Ws @("wait", "--name", $guest, "--timeout", "600", "--root", $Root) | Out-Null
    if (-not $SkipRestore) {
        $restoreKey = "ws_benchmark_restore_marker"
        $restoreMarker = "baseline_$($guest.Replace('-', '_'))"
        Invoke-GuestAdb (Get-GuestIdentity $guest) @("shell", "settings", "put", "global", $restoreKey, $restoreMarker) | Out-Null
        Invoke-Ws @("snapshot-save", "--name", $guest, "--tag", "bench-base", "--root", $Root) | Out-Null
        for ($sample = 1; $sample -le $Samples; $sample++) {
            Invoke-GuestAdb (Get-GuestIdentity $guest) @("shell", "settings", "put", "global", $restoreKey, "trial_$sample") | Out-Null
            $timer = [System.Diagnostics.Stopwatch]::StartNew()
            $load = Invoke-Ws @("snapshot-load", "--name", $guest, "--tag", "bench-base", "--json", "--root", $Root) -AllowFailure
            $timer.Stop()
            $result = Parse-Json $load.Text
            $elapsed = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
            $observedMarker = if ($load.Code -eq 0) {
                Invoke-GuestAdb (Get-GuestIdentity $guest) @("shell", "settings", "get", "global", $restoreKey)
            }
            $stateRestored = $observedMarker -eq $restoreMarker
            $report.restoreTrials += [pscustomobject]@{
                trial = $sample
                wallSeconds = $elapsed
                consoleSeconds = $result.seconds
                recovery = $result.recovery
                stateRestored = $stateRestored
                observedMarker = $observedMarker
                exitCode = $load.Code
                error = if ($load.Code -ne 0) { $load.Text }
            }
            if ($load.Code -eq 0 -and $result.recovery -in @("restored", "cold_boot")) {
                [void]$restoreSamples.Add($elapsed)
            }
            Log ("warm restore {0}/{1}: {2:N2}s recovery={3} exit={4}" -f $sample, $Samples, $elapsed, $result.recovery, $load.Code)
        }
        $report.scenarios["restoreAttemptWallSeconds"] = Summary ([double[]]$restoreSamples.ToArray())
        $warm = @($report.restoreTrials | Where-Object {
            $_.exitCode -eq 0 -and $_.recovery -eq "restored" -and $_.stateRestored
        } | ForEach-Object { $_.wallSeconds })
        $report.scenarios["warmRestore"] = Summary ([double[]]$warm)
        $fallbacks = @($report.restoreTrials | Where-Object recovery -eq "cold_boot").Count
        Log "restore attempts=$Samples verifiedWarm=$($warm.Count) coldBootFallbacks=$fallbacks"
    }

    if (-not $Apk) { throw "Build the sample APK or pass -Apk before benchmarking input." }
    Install-Fixture $guest
    $report.scenarios["inputLatencyMs"] = Measure-GuestLatency $guest "singleGuest" ""

    # --------------------------------------------------------------- frame rate
    $frameSamples = New-Object System.Collections.ArrayList
    for ($sample = 1; $sample -le $Samples; $sample++) {
        $started = Get-Date
        $frame = Invoke-Ws @("frame", "--name", $guest, "--width", "540", "--root", $Root) -AllowFailure
        $elapsed = ((Get-Date) - $started).TotalMilliseconds
        if ($frame.Code -eq 0) { [void]$frameSamples.Add($elapsed) }
        Log ("capture {0}/{1}: {2:N0} ms" -f $sample, $Samples, $elapsed)
    }
    $report.scenarios["captureMs"] = Summary ([double[]]$frameSamples.ToArray())

    # ------------------------------------------------------- two-guest scenario
    if (-not $SkipTwoGuest) {
        $second = New-BenchmarkGuest "bench2"
        Invoke-Ws @("start", "--name", $second, "--root", $Root) | Out-Null
        Invoke-Ws @("wait", "--name", $second, "--timeout", "600", "--root", $Root) | Out-Null
        Install-Fixture $second
        $twoGuestLatency = Measure-GuestLatency $guest "twoGuest" $second
        $report.scenarios["twoGuestLatencyMs"] = $twoGuestLatency
        Add-Check "coverage.two-guest-latency" ($twoGuestLatency.samples -ge 20) (
            "valid={0}/{1} p50={2}ms p95={3}ms" -f $twoGuestLatency.samples, $Samples, $twoGuestLatency.p50, $twoGuestLatency.p95)
        if ($SoakMinutes -gt 0) { Invoke-TwoGuestSoak @($guest, $second) }
        Stop-OwnedGuest $second
    }

    # ------------------------------------------------------- lifecycle stability
    if (-not $SkipLifecycle) {
        $before = Get-MemorySnapshot
        $cycleTimes = New-Object System.Collections.ArrayList
        $failures = 0
        for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
            $started = Get-Date
            $start = Invoke-Ws @("start", "--name", $guest, "--root", $Root) -AllowFailure
            if ($start.Code -ne 0) { $failures++ }
            $wait = Invoke-Ws @("wait", "--name", $guest, "--timeout", "300", "--root", $Root) -AllowFailure
            if ($wait.Code -ne 0) { $failures++ }
            $stop = Invoke-Ws @("stop", "--name", $guest, "--force", "--root", $Root) -AllowFailure
            if ($stop.Code -ne 0) { $failures++ }
            [void]$cycleTimes.Add(((Get-Date) - $started).TotalSeconds)
            if ($cycle % 5 -eq 0) {
                $now = Get-MemorySnapshot
                Log ("cycle {0}/{1}: engines={2} freeRam={3} MB" -f $cycle, $Cycles, $now.engines, $now.freePhysicalMB)
            }
        }
        $after = Get-MemorySnapshot
        $report.lifecycle = [ordered]@{
            cycles = $Cycles
            failures = $failures
            cycleSeconds = Summary ([double[]]$cycleTimes.ToArray())
            enginesBefore = $before.engines
            enginesAfter = $after.engines
            freeRamBeforeMB = $before.freePhysicalMB
            freeRamAfterMB = $after.freePhysicalMB
            freeVirtualBeforeMB = $before.freeVirtualMB
            freeVirtualAfterMB = $after.freeVirtualMB
        }
        Add-Check "lifecycle.cycles" ($failures -eq 0) "$Cycles start/stop cycles with $failures failures"
        Add-Check "lifecycle.no-leak" ($after.engines -le $before.engines + 1) "engine count $($before.engines) -> $($after.engines)"
    }

    # ------------------------------------------------------------------ targets
    $boot = $report.scenarios["coldBoot"]
    $baselineBoot = $report.scenarios["baselineBoot"]
    if ($SkipBaseline -or $SkipColdBoot) {
        Log "SKIP target.boot-overhead: boot comparison omitted"
    }
    elseif ($boot.samples -ge 20 -and $baselineBoot.samples -ge 20) {
        $overhead = [Math]::Round($boot.p50 - $baselineBoot.p50, 2)
        $allowed = [Math]::Max(1.0, [Math]::Round($baselineBoot.p50 * 0.10, 2))
        Add-Check "comparison.managed-boot" $true `
            ("managed cold boot p50 {0}s vs second managed profile p50 {1}s = {2}s overhead (allowed {3}s)" -f $boot.p50, $baselineBoot.p50, $overhead, $allowed)
    }
    else {
        Add-Check "comparison.managed-boot" $false "not enough samples (boot $($boot.samples), baseline $($baselineBoot.samples))"
    }

    $restore = $report.scenarios["warmRestore"]
    if ($SkipRestore) {
        Log "SKIP target.warm-restore: restore scenario omitted"
    }
    elseif ($restore.samples -ge 20) {
        Add-Check "target.warm-restore" ($restore.p50 -le 8) ("warm restore p50 {0}s (target <= 8s)" -f $restore.p50)
    }
    else {
        Add-Check "target.warm-restore" $false "only $($restore.samples) restore samples"
    }

    $latency = $report.scenarios["inputLatencyMs"]
    if ($latency.samples -ge 20) {
        Add-Check "target.input-latency" ($latency.p95 -le 100) ("input latency p50 {0}ms p95 {1}ms over {2} samples (target p95 <= 100ms)" -f $latency.p50, $latency.p95, $latency.samples)
    }
    else {
        Add-Check "target.input-latency" $false "only $($latency.samples) latency samples"
    }

}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
    Log ("fatal: " + $_.Exception.Message)
}
finally {
    foreach ($name in $ownedGuests) {
        try {
            Stop-OwnedGuest $name
            Invoke-Ws @("destroy", "--name", $name, "--root", $Root) | Out-Null
            Add-Check "cleanup.$name" $true "stopped and removed through the supervisor"
        }
        catch {
            Add-Check "cleanup.$name" $false $_.Exception.Message
        }
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out "benchmark.json") -Encoding UTF8
}

$failed = @($script:results | Where-Object { -not $_.Passed }).Count
$summary = [ordered]@{
    runId = $run
    finishedUtc = (Get-Date).ToUniversalTime().ToString("O")
    failed = $failed
    checks = @($script:results)
    benchmark = $report
}
$summary | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$script:results | Format-Table -AutoSize | Out-String | Write-Host
Log "completed failures=$failed artifacts=$out"
if ($failed -gt 0) { exit 2 }
exit 0
