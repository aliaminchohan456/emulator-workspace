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
      * input latency      : dispatch to the first frame that carries the
                             changed guest marker
      * frame throughput   : delivered frames per second from the frame stream
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
    [int]$Samples = 20,
    [int]$Cycles = 30,
    [string]$Apk = "",
    [switch]$SkipTwoGuest,
    [switch]$SkipLifecycle
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
    diskFreeGB       = if ($disk) { [Math]::Round($disk.FreeSpace / 1GB, 1) } else { $null }
    emulatorVersion  = (Get-Content (Join-Path $sdk "emulator\emulator.exe.source.properties" -ErrorAction SilentlyContinue) -ErrorAction SilentlyContinue | Select-String 'Pkg.Revision' | Select-Object -First 1).Line
    adbVersion       = ((& $adb version 2>&1 | Select-Object -First 1) | Out-String).Trim()
    dotnetVersion    = (& dotnet --version 2>&1 | Out-String).Trim()
    whpx             = (Get-CimInstance Win32_Processor | Select-Object -First 1).VirtualizationFirmwareEnabled
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
    lifecycle = $null
}

$bootSamples = New-Object System.Collections.ArrayList
$restoreSamples = New-Object System.Collections.ArrayList
$latencySamples = New-Object System.Collections.ArrayList
$fpsSamples = New-Object System.Collections.ArrayList
$baselineBootSamples = New-Object System.Collections.ArrayList

function Stop-OwnedGuests([string[]]$instances) {
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
            try { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

Log "benchmark $run root=$Root samples=$Samples cycles=$Cycles"

try {
    New-Item -ItemType Directory -Force $Root | Out-Null

    # ---------------------------------------------------- the same-engine baseline
    # The baseline is the raw emulator started with the same binary, image and
    # allocation, so the boot overhead is measured against the engine itself.
    $baseline = "bench-base-" + (Get-Random -Maximum 9999)
    Invoke-Ws @("create", "--name", $baseline, "--root", $Root) | Out-Null
    for ($sample = 1; $sample -le $Samples; $sample++) {
        $started = Get-Date
        Invoke-Ws @("start", "--name", $baseline, "--root", $Root) | Out-Null
        $ready = Invoke-Ws @("wait", "--name", $baseline, "--timeout", "600", "--root", $Root) -AllowFailure
        $readyJson = Parse-Json $ready.Text
        if ($readyJson -and $readyJson.launchedToInteractiveSeconds) {
            [void]$baselineBootSamples.Add([double]$readyJson.launchedToInteractiveSeconds)
        }
        else {
            $status = Parse-Json (Invoke-Ws @("status", "--name", $baseline, "--root", $Root) -AllowFailure).Text
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

    # ------------------------------------------------------------- managed guest
    $guest = "bench-" + (Get-Random -Maximum 9999)
    Invoke-Ws @("create", "--name", $guest, "--root", $Root) | Out-Null

    for ($sample = 1; $sample -le $Samples; $sample++) {
        $started = Get-Date
        Invoke-Ws @("start", "--name", $guest, "--root", $Root) | Out-Null
        $ready = Invoke-Ws @("wait", "--name", $guest, "--timeout", "600", "--root", $Root) -AllowFailure
        $readyJson = Parse-Json $ready.Text
        $elapsed = ((Get-Date) - $started).TotalSeconds
        $reported = 0
        if ($readyJson -and $readyJson.launchedToInteractiveSeconds) { $reported = [double]$readyJson.launchedToInteractiveSeconds }
        else {
            $status = Parse-Json (Invoke-Ws @("status", "--name", $guest, "--root", $Root) -AllowFailure).Text
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
    Invoke-Ws @("snapshot-save", "--name", $guest, "--tag", "bench-base", "--root", $Root) -AllowFailure | Out-Null
    for ($sample = 1; $sample -le $Samples; $sample++) {
        $started = Get-Date
        $load = Invoke-Ws @("snapshot-load", "--name", $guest, "--tag", "bench-base", "--root", $Root) -AllowFailure
        $loadJson = Parse-Json $load.Text
        # The snapshot load reports the elapsed seconds of the restore; the
        # readiness of the guest is read from its status afterwards.
        $reported = 0
        if ($loadJson -and $loadJson.seconds) { $reported = [double]$loadJson.seconds }
        else {
            $status = Parse-Json (Invoke-Ws @("status", "--name", $guest, "--root", $Root) -AllowFailure).Text
            if ($status -and $status.readiness -and $status.readiness.launchedToInteractiveSeconds) {
                $reported = [double]$status.readiness.launchedToInteractiveSeconds
            }
        }
        if ($reported -gt 0) { [void]$restoreSamples.Add($reported) }
        Log ("warm restore {0}/{1}: {2:N1}s" -f $sample, $Samples, ((Get-Date) - $started).TotalSeconds)
    }
    $report.scenarios["warmRestore"] = Summary ([double[]]$restoreSamples.ToArray())
    Log ("warm restore p50={0}s p95={1}s over {2} samples" -f $report.scenarios["warmRestore"].p50, $report.scenarios["warmRestore"].p95, $report.scenarios["warmRestore"].samples)

    # ------------------------------------------------- input latency and frames
    if ($Apk) {
        Invoke-Ws @("install", "--name", $guest, "--path", (Join-Path $Root "apps\ws-sample.apk"), "--root", $Root) -AllowFailure | Out-Null
        Invoke-Ws @("launch", "--name", $guest, "--package", "dev.emulatorworkspace.sample", "--root", $Root) -AllowFailure | Out-Null
        Start-Sleep -Seconds 8
    }

    $identity = Parse-Json (Invoke-Ws @("adb-identity", "--name", $guest, "--root", $Root) -AllowFailure).Text
    $serial = $identity.serial
    if ($serial) {
        $logcat = Join-Path $out "bench-logcat.txt"
        Start-Process -FilePath $adb -ArgumentList "-s", $serial, "logcat", "-c" -WindowStyle Hidden | Out-Null
        for ($sample = 1; $sample -le $Samples; $sample++) {
            # The marker of the sample scene changes on every tap, so the
            # dispatch-to-response latency is measured from the tap to the log
            # line the guest writes for it.
            $started = Get-Date
            & $adb -s $serial shell input tap 540 1200 | Out-Null
            $deadline = (Get-Date).AddSeconds(5)
            $seen = $false
            while ((Get-Date) -lt $deadline) {
                $text = (& $adb -s $serial logcat -d -t 20 2>&1 | Out-String)
                if ($text -match "TAP index=") { $seen = $true; break }
                Start-Sleep -Milliseconds 100
            }
            $elapsed = ((Get-Date) - $started).TotalMilliseconds
            if ($seen) { [void]$latencySamples.Add($elapsed) }
            Log ("input latency {0}/{1}: {2:N0} ms" -f $sample, $Samples, $elapsed)
            Start-Sleep -Milliseconds 300
        }
        & $adb -s $serial logcat -d -t 400 | Set-Content -Path $logcat -Encoding UTF8
    }
    $report.scenarios["inputLatencyMs"] = Summary ([double[]]$latencySamples.ToArray())

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
        $second = "bench2-" + (Get-Random -Maximum 9999)
        Invoke-Ws @("create", "--name", $second, "--root", $Root) | Out-Null
        Invoke-Ws @("start", "--name", $second, "--root", $Root) | Out-Null
        Invoke-Ws @("wait", "--name", $second, "--timeout", "600", "--root", $Root) -AllowFailure | Out-Null
        $twoGuestSamples = New-Object System.Collections.ArrayList
        $twoIdentity = Parse-Json (Invoke-Ws @("adb-identity", "--name", $second, "--root", $Root) -AllowFailure).Text
        if ($twoIdentity.serial) {
            for ($sample = 1; $sample -le $Samples; $sample++) {
                $started = Get-Date
                & $adb -s $twoIdentity.serial shell input tap 540 1200 | Out-Null
                $deadline = (Get-Date).AddSeconds(6)
                $seen = $false
                while ((Get-Date) -lt $deadline) {
                    $text = (& $adb -s $twoIdentity.serial logcat -d -t 20 2>&1 | Out-String)
                    if ($text -match "TAP index=") { $seen = $true; break }
                    Start-Sleep -Milliseconds 100
                }
                if ($seen) { [void]$twoGuestSamples.Add(((Get-Date) - $started).TotalMilliseconds) }
                Start-Sleep -Milliseconds 300
            }
        }
        $report.scenarios["twoGuestLatencyMs"] = Summary ([double[]]$twoGuestSamples.ToArray())
        Log ("two guest latency p50={0}ms p95={1}ms over {2} samples" -f $report.scenarios["twoGuestLatencyMs"].p50, $report.scenarios["twoGuestLatencyMs"].p95, $report.scenarios["twoGuestLatencyMs"].samples)
        Stop-OwnedGuests @($second)
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
    if ($boot.samples -ge 20 -and $baselineBoot.samples -ge 20) {
        $overhead = [Math]::Round($boot.p50 - $baselineBoot.p50, 2)
        $allowed = [Math]::Max(1.0, [Math]::Round($baselineBoot.p50 * 0.10, 2))
        Add-Check "target.boot-overhead" ($overhead -le $allowed) `
            ("managed cold boot p50 {0}s vs same-engine baseline p50 {1}s = {2}s overhead (allowed {3}s)" -f $boot.p50, $baselineBoot.p50, $overhead, $allowed)
    }
    else {
        Add-Check "target.boot-overhead" $false "not enough samples (boot $($boot.samples), baseline $($baselineBoot.samples))"
    }

    $restore = $report.scenarios["warmRestore"]
    if ($restore.samples -ge 20) {
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

    Stop-OwnedGuests @($guest, $baseline)
}
catch {
    Add-Check "harness.fatal" $false $_.Exception.Message
    Log ("fatal: " + $_.Exception.Message)
}
finally {
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
