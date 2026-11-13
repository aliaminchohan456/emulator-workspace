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

    A single guest and two guests are measured separately. The ADB/Logcat
    and one-shot PNG timings diagnose separate paths from the WPF targets.
    Warm restore is checked against the 8 s median using 20 verified restores.
    The managed boot control measures run-to-run variation.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\benchmark.ps1 `
      -Root D:\Phase4Root -Samples 20 -Cycles 30
#>
param(
    [string]$Root = "D:\Phase4Root",
    [string]$OutDir = "artifacts\phase3\bench",
    [ValidateRange(1, 1000)][int]$Samples = 20,
    [switch]$Diagnostic,
    [ValidateRange(0, 1000)][int]$RestoreAttempts = 0,
    [int]$Cycles = 30,
    [string]$Apk = "",
    [string]$CandidateRelease = "",
    [string]$CandidatePayload = "",
    [ValidateRange(0, 240)][int]$SoakMinutes = 0,
    [ValidateRange(1, 60)][int]$SoakProbeSeconds = 60,
    [switch]$SkipTwoGuest,
    [switch]$SkipLifecycle,
    [switch]$SkipBaseline,
    [switch]$RawBaseline,
    [string]$RawControlRun = "",
    [switch]$SkipColdBoot,
    [switch]$SkipLatency,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$candidate = $null
if ($CandidateRelease -or $CandidatePayload) {
    if (-not ($CandidateRelease -and $CandidatePayload) -or $Apk) {
        throw "Pass both candidate paths and use the candidate's sample APK."
    }
    . (Join-Path $PSScriptRoot "fixtures/candidate-payload.ps1")
    $verifiedCandidate = Resolve-VerifiedCandidate $CandidateRelease $CandidatePayload
    $wsctl = Join-Path $verifiedCandidate.payload "wsctl.exe"
    $Apk = Join-Path $verifiedCandidate.payload "ws-sample.apk"
    $candidate = $verifiedCandidate.identity
    $env:EMULATORWORKSPACE_SUPERVISOR_EXE = Join-Path $verifiedCandidate.payload "EmulatorWorkspace.Supervisor.exe"
}
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$script:results = New-Object System.Collections.ArrayList
$ownedGuests = [System.Collections.Generic.List[string]]::new()
$soakProcesses = @{}
$package = "dev.emulatorworkspace.sample"

if ($SoakMinutes -gt 0 -and $SkipTwoGuest) {
    throw "A soak requires the two-guest scenario."
}
if ($RestoreAttempts -gt 0 -and $RestoreAttempts -lt $Samples) {
    throw "RestoreAttempts must cover the requested verified samples."
}
if ($Diagnostic -and $SoakMinutes -gt 0) { throw "A soak is a separate acceptance run." }
if ($RawBaseline -and ($SkipBaseline -or $SkipColdBoot)) {
    throw "A raw comparison requires both cold-boot scenarios."
}
if ($RawControlRun -and -not ($RawBaseline -and $candidate)) {
    throw "Reusing a raw control requires -RawBaseline and the verified candidate inputs."
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

if (-not $Apk) { $Apk = Join-Path $repo "artifacts/phase1/sample/ws-sample.apk" }

$report = [ordered]@{
    diagnostic = [bool]$Diagnostic
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
    candidate = $candidate
    harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    fixtureSha256 = $null
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

function Wait-SoakProcessExit([string]$name) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($owned in $soakProcesses[$name]) {
        $remaining = Get-Process -Id $owned.pid -ErrorAction SilentlyContinue
        if (-not $remaining) { continue }
        try {
            if ($remaining.HasExited -or $remaining.StartTime.ToUniversalTime() -ne $owned.startUtc) { continue }
            $remainingMs = [Math]::Max(0, 15000 - $timer.ElapsedMilliseconds)
            if (-not $remaining.WaitForExit([int]$remainingMs)) {
                throw "Owned process $($owned.pid) is still running after the supervisor stop."
            }
        }
        finally { $remaining.Dispose() }
    }
}

function Get-GuestIdentity([string]$name) {
    $identity = Parse-Json (Invoke-Ws @("adb-identity", "--name", $name, "--json", "--root", $Root)).Text
    if (-not $identity.transportUsable -or $identity.transportId -notmatch '^\d+$' -or
        $identity.serial -ne "emulator-$($identity.consolePort)" -or -not $identity.enginePid) {
        throw "No usable managed ADB transport for $name."
    }
    return $identity
}

function Get-ColdBootFocus($identity) {
    try {
        $state = Invoke-GuestAdb $identity @("shell", 'boot=$(getprop sys.boot_completed); echo "$boot"; [ "$boot" = 1 ] && dumpsys -t 5 window') 8000
        if (($state -split '\r?\n', 2)[0].Trim() -eq "1") {
            return [regex]::Match($state, '(?m)^\s*mCurrentFocus=(Window\{[^\r\n]+)').Groups[1].Value
        }
    }
    catch { return $null }
}

function Get-RawEngineChildren($launcher, [datetime]$started, [string]$avdName) {
    $qemu = Join-Path $sdk "emulator/qemu/windows-x86_64/qemu-system-x86_64-headless.exe"
    $filter = "ParentProcessId=$($launcher.Id) AND Name='qemu-system-x86_64-headless.exe'"
    $avdArgument = '(?:^|\s)-avd\s+(?:"{0}"|{0})(?=\s|$)' -f [regex]::Escape($avdName)
    $hardwareIni = [regex]::Escape("$avdName.avd\hardware-qemu.ini") + '(?="|\s|$)'
    Get-CimInstance Win32_Process -Filter $filter -ErrorAction Stop |
        Where-Object { $_.ExecutablePath -eq $qemu -and $_.CreationDate -ge $started -and
            ($_.CommandLine -match $avdArgument -or $_.CommandLine -match $hardwareIni) } |
        ForEach-Object {
            $child = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            if ($child -and $child.StartTime -ge $started -and
                [Math]::Abs(($child.StartTime - $_.CreationDate).TotalSeconds) -lt 1) { $child }
        }
}

function Get-BootProfileConfig($profile) {
    $pattern = '^\s*(abi.type|image.sysdir.1|hw.cpu.ncore|hw.ramSize|hw.lcd.width|hw.lcd.height|hw.lcd.density|hw.gpu.mode)\s*=\s*(.+)$'
    $config = Join-Path $Root "avd/$($profile.avdName).avd/config.ini"
    Get-Content -LiteralPath $config -ErrorAction Stop | ForEach-Object {
        if ($_ -match $pattern) { "$($Matches[1])=$($Matches[2].Trim())" }
    } | Sort-Object
}

function Read-RawControl([string]$directory, [string[]]$config) {
    $directory = (Resolve-Path -LiteralPath $directory -ErrorAction Stop).ProviderPath
    $benchmarkPath = Join-Path $directory "benchmark.json"
    $samplesPath = Join-Path $directory "cold-boot.jsonl"
    $prior = Get-Content -LiteralPath $benchmarkPath -Raw -ErrorAction Stop | ConvertFrom-Json
    $records = @(Get-Content -LiteralPath $samplesPath -ErrorAction Stop |
        ForEach-Object { $_ | ConvertFrom-Json } | Where-Object arm -eq "raw")
    $report.avdConfig = @{ raw = $prior.matchedAvdConfig; managed = $config }
    if ($records.Count -ne $Samples -or $prior.scenarios.rawBoot.samples -ne $Samples -or
        $prior.bootEndpoint -ne $report.bootEndpoint -or
        $prior.candidate.installerSha256 -ne $candidate.installerSha256 -or
        $prior.candidate.sourceCommit -ne $candidate.sourceCommit -or $prior.fixtureSha256 -ne $report.fixtureSha256 -or
        ($config -join "`n") -ne ($prior.matchedAvdConfig -join "`n")) {
        throw "The saved raw control does not match the sample count, candidate, fixture or AVD settings."
    }
    foreach ($key in @('machine', 'osBuild', 'osVersion', 'cpu', 'gpu', 'gpuDriver', 'emulatorVersion', 'whpx')) {
        if ($prior.host.$key -ne $hostInfo[$key]) { throw "The raw control's host property '$key' changed." }
    }
    for ($i = 0; $i -lt $records.Count; $i++) {
        $sample = $records[$i]
        if ($sample.trial -ne ($i + 1) -or $sample.seconds -le 0 -or -not $sample.launcherPid -or
            -not $sample.qemuPids -or $sample.transportId -notmatch '^\d+$') {
            throw "The raw control has an invalid sample at row $($i + 1)."
        }
    }
    $report.rawControl = [ordered]@{
        runId = $prior.runId
        directory = $directory
        sourceCommit = $prior.sourceCommit
        harnessSha256 = $prior.harnessSha256
        benchmarkSha256 = (Get-FileHash -LiteralPath $benchmarkPath).Hash
        samplesSha256 = (Get-FileHash -LiteralPath $samplesPath).Hash
    }
    return $records
}

function Stop-RawProcess($process) {
    try {
        if (-not $process.HasExited) {
            try { $process.Kill() }
            catch {
                if (-not $process.WaitForExit(15000)) { throw }
            }
        }
        if (-not $process.WaitForExit(15000)) { throw "Raw process $($process.Id) did not exit." }
    }
    finally { $process.Dispose() }
}

function Measure-RawBoot($profile, [int]$sample) {
    $ports = @($profile.consolePort, ($profile.consolePort + 1), $profile.grpcPort)
    $listing = Invoke-BenchmarkProcess $adb @("devices", "-l") 6000
    if ($listing.Code -ne 0 -or $listing.Text -match "(?m)^$($profile.serial)\s" -or
        (Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -in $ports)) {
        throw "The raw control's reserved ports or serial are already in use."
    }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $sdk "emulator/emulator.exe"
    $info.WorkingDirectory = $sdk
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables["ANDROID_AVD_HOME"] = Join-Path $Root "avd"
    $info.EnvironmentVariables["ANDROID_SDK_ROOT"] = $sdk
    $info.Arguments = @("-avd", $profile.avdName, "-port", $profile.consolePort, "-no-audio",
        "-no-boot-anim", "-no-metrics", "-accel", "on", "-gpu", "host", "-no-window",
        "-no-snapshot-load", "-no-snapshot-save", "-grpc", $profile.grpcPort, "-grpc-use-token", "-verbose") -join " "
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $launcher = [System.Diagnostics.Process]::Start($info)
    $started = $launcher.StartTime
    $stdout = $launcher.StandardOutput.ReadToEndAsync()
    $stderr = $launcher.StandardError.ReadToEndAsync()
    $owned = @()
    try {
        while ($timer.Elapsed.TotalSeconds -lt 600 -and -not $launcher.HasExited) {
            if (-not $owned) {
                $owned = @(Get-RawEngineChildren $launcher $started $profile.avdName)
            }
            $listing = Invoke-BenchmarkProcess $adb @("devices", "-l") 6000
            $transport = [regex]::Match($listing.Text, "(?m)^$($profile.serial)\s+device\s+[^\r\n]*transport_id:(\d+)\b")
            if ($listing.Code -eq 0 -and $transport.Success -and $owned) {
                $listeners = @(Get-NetTCPConnection -OwningProcess @($owned.Id) -State Listen -ErrorAction SilentlyContinue)
                $loopback = @($listeners | Where-Object LocalAddress -in @('127.0.0.1', '::1'))
                if (@($ports | Where-Object { $_ -notin $loopback.LocalPort }).Count -eq 0) {
                    $id = [pscustomobject]@{ name = $profile.name; transportId = $transport.Groups[1].Value }
                    $focus = Get-ColdBootFocus $id
                    if ($focus) {
                        return [pscustomobject]@{
                            trial = $sample; arm = "raw"; seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
                            launcherPid = $launcher.Id; qemuPids = @($owned.Id)
                            transportId = $id.transportId; arguments = $info.Arguments; focus = $focus
                        }
                    }
                }
            }
            Start-Sleep -Milliseconds 700
        }
        throw "Raw emulator did not reach interactive readiness."
    }
    finally {
        try {
            if (-not $owned) { $owned = @(Get-RawEngineChildren $launcher $started $profile.avdName) }
            foreach ($process in $owned) { Stop-RawProcess $process }
        }
        finally {
            try { Stop-RawProcess $launcher }
            finally {
                if ($stdout.Wait(3000) -and $stderr.Wait(3000)) {
                    ($stdout.Result + $stderr.Result) | Set-Content -LiteralPath (Join-Path $out "raw-engine-$sample.log") -Encoding UTF8
                }
            }
        }
    }
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

            $launcher = Get-Process -Id $current.enginePid -ErrorAction Stop
            $childFilter = "ParentProcessId=$($launcher.Id) AND Name='qemu-system-x86_64-headless.exe'"
            $enginePath = Join-Path $sdk "emulator/qemu/windows-x86_64/qemu-system-x86_64-headless.exe"
            $children = @(Get-CimInstance Win32_Process -Filter $childFilter -ErrorAction Stop |
                Where-Object { $_.ExecutablePath -eq $enginePath -and $_.CreationDate -ge $launcher.StartTime })
            $processes = @($launcher) + @($children | ForEach-Object {
                Get-Process -Id $_.ProcessId -ErrorAction Stop
            })
            $metrics = @($processes | ForEach-Object {
                [pscustomobject]@{
                    pid = $_.Id
                    startUtc = $_.StartTime.ToUniversalTime()
                    workingSetBytes = $_.WorkingSet64
                    privateBytes = $_.PrivateMemorySize64
                    handles = $_.HandleCount
                    cpuSeconds = $_.CPU
                }
            })
            $soakProcesses[$current.name] = $metrics
            $ports = @(Get-NetTCPConnection -OwningProcess @($metrics.pid) -State Listen -ErrorAction Stop |
                Select-Object LocalAddress, LocalPort, OwningProcess)
            foreach ($port in @($current.consolePort, ($current.consolePort + 1), $current.grpcPort)) {
                if (-not ($ports | Where-Object { $_.LocalPort -eq $port -and $_.LocalAddress -in @("127.0.0.1", "::1") })) {
                    throw "Soak guest $($current.name) has no owned loopback listener on port ${port}."
                }
            }
            [pscustomobject]@{
                identity = $current
                listeners = $ports
                processes = $metrics
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
    if (-not (Test-Path -LiteralPath $Apk -PathType Leaf)) {
        throw "Build the sample APK or pass -Apk before benchmarking input."
    }
    $approved = Join-Path $Root "apps/benchmark-$([Guid]::NewGuid().ToString('N')).apk"
    New-Item -ItemType Directory -Force (Split-Path -Parent $approved) | Out-Null
    Copy-Item -LiteralPath $Apk -Destination $approved -ErrorAction Stop
    $report.fixtureSha256 = (Get-FileHash -LiteralPath $approved).Hash

    # ---------------------------------------------------- the same-engine baseline
    # The historical baseline is a second supervisor-managed profile. This
    # comparison measures run-to-run variation on the managed path.
    if (-not $SkipBaseline) {
    if ($RawBaseline) {
        $baseline = if (-not $RawControlRun) { New-BenchmarkGuest "bench-base" }
        $guest = New-BenchmarkGuest "bench"
        $managedProfile = Parse-Json (Invoke-Ws @("status", "--name", $guest, "--json", "--root", $Root)).Text
        $managedConfig = @(Get-BootProfileConfig $managedProfile)
        $report.bootEndpoint = "host dispatch through boot_completed=1 and a current foreground Window"
        if ($RawControlRun) {
            $report.rawBootTrials = @(Read-RawControl $RawControlRun $managedConfig)
            $report.matchedAvdConfig = $managedConfig
            foreach ($trial in $report.rawBootTrials) {
                $trial | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $out "cold-boot.jsonl") -Encoding UTF8
                [void]$baselineBootSamples.Add($trial.seconds)
            }
            Log "Reused $Samples raw trials from $($report.rawControl.runId) on the same matched host and profile."
        }
        else {
            $profile = Parse-Json (Invoke-Ws @("status", "--name", $baseline, "--json", "--root", $Root)).Text
            $rawConfig = @(Get-BootProfileConfig $profile)
            $report.avdConfig = @{ raw = $rawConfig; managed = $managedConfig }
            if ($rawConfig.Count -ne 8 -or ($rawConfig -join "`n") -ne ($managedConfig -join "`n")) {
                throw "Raw and managed AVD settings did not match. See avdConfig in benchmark.json."
            }
            $report.matchedAvdConfig = $rawConfig
            $report.rawBootTrials = @()
            for ($sample = 1; $sample -le $Samples; $sample++) {
                $trial = Measure-RawBoot $profile $sample
                $report.rawBootTrials += $trial
                $trial | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $out "cold-boot.jsonl") -Encoding UTF8
                [void]$baselineBootSamples.Add($trial.seconds)
                Log ("raw cold boot {0}/{1}: {2:N2}s" -f $sample, $Samples, $trial.seconds)
                Start-Sleep -Seconds 3
            }
        }
    }
    else {
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
    }
    $baselineScenario = if ($RawBaseline) { "rawBoot" } else { "baselineBoot" }
    $report.scenarios[$baselineScenario] = Summary ([double[]]$baselineBootSamples.ToArray())
    $baselineResult = $report.scenarios[$baselineScenario]
    Log ("baseline boot p50={0}s p95={1}s over {2} samples" -f $baselineResult.p50, $baselineResult.p95, $baselineResult.samples)
    }

    # ------------------------------------------------------------- managed guest
    if (-not $RawBaseline) { $guest = New-BenchmarkGuest "bench" }

    for ($sample = 1; $sample -le $Samples -and -not $SkipColdBoot; $sample++) {
        $started = Get-Date
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-Ws @("start", "--name", $guest, "--root", $Root) | Out-Null
        $ready = Invoke-Ws @("wait", "--name", $guest, "--timeout", "600", "--root", $Root) -AllowFailure
        $readyReplySeconds = $timer.Elapsed.TotalSeconds
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
        if ($RawBaseline) {
            if ($ready.Code -ne 0) { throw "Managed cold boot trial $sample failed readiness." }
            $target = Get-GuestIdentity $guest
            $focus = $null
            do {
                $focus = Get-ColdBootFocus $target
                if ($focus) { break }
                Start-Sleep -Milliseconds 700
            } while ($timer.Elapsed.TotalSeconds -lt 600)
            if (-not $focus) {
                throw "Managed cold boot trial $sample has no foreground window."
            }
            # wsctl wait returns when the supervisor has observed boot and a
            # focused window. The independent post-check proves that endpoint
            # and the exact transport after the timed reply.
            $reported = [Math]::Round($readyReplySeconds, 2)
            $elapsed = $reported
            [pscustomobject]@{
                trial = $sample; arm = "managed"; seconds = $reported; transportId = $target.transportId; focus = $focus
                supervisorInteractiveSeconds = $readyJson.launchedToInteractiveSeconds
                verificationSeconds = [Math]::Round($timer.Elapsed.TotalSeconds - $readyReplySeconds, 2)
                bootCompletedUtc = $readyJson.bootCompletedUtc; interactiveUtc = $readyJson.interactiveUtc
            } |
                ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $out "cold-boot.jsonl") -Encoding UTF8
        }
        if ($reported -gt 0) { [void]$bootSamples.Add($reported) }
        Log ("cold boot {0}/{1}: {2:N1}s" -f $sample, $Samples, $elapsed)
        Invoke-Ws @("stop", "--name", $guest, "--force", "--root", $Root) -AllowFailure | Out-Null
        Start-Sleep -Seconds 3
    }
    $report.scenarios["coldBoot"] = Summary ([double[]]$bootSamples.ToArray())
    Log ("cold boot p50={0}s p95={1}s over {2} samples" -f $report.scenarios["coldBoot"].p50, $report.scenarios["coldBoot"].p95, $report.scenarios["coldBoot"].samples)

    # ------------------------------------------------------------- warm restore
    if (-not ($SkipRestore -and $SkipLatency -and $SkipTwoGuest)) {
        Invoke-Ws @("start", "--name", $guest, "--root", $Root) | Out-Null
        Invoke-Ws @("wait", "--name", $guest, "--timeout", "600", "--root", $Root) | Out-Null
    }
    if (-not $SkipRestore) {
        $restoreKey = "ws_benchmark_restore_marker"
        $restoreMarker = "baseline_$($guest.Replace('-', '_'))"
        Invoke-GuestAdb (Get-GuestIdentity $guest) @("shell", "settings", "put", "global", $restoreKey, $restoreMarker) | Out-Null
        Invoke-Ws @("snapshot-save", "--name", $guest, "--tag", "bench-base", "--root", $Root) | Out-Null
        $attemptLimit = if ($RestoreAttempts) { $RestoreAttempts } else { $Samples }
        $verified = 0
        for ($sample = 1; $sample -le $attemptLimit -and $verified -lt $Samples; $sample++) {
            $dirty = "trial_$sample"
            try {
                $before = Get-GuestIdentity $guest
                Invoke-GuestAdb $before @("shell", "settings", "put", "global", $restoreKey, $dirty) 20000 | Out-Null
                if ((Invoke-GuestAdb $before @("shell", "settings", "get", "global", $restoreKey) 20000) -ne $dirty) {
                    throw "The pre-load marker did not match."
                }
            }
            catch {
                $trial = [pscustomobject]@{
                    trial = $sample; stage = "pre-load"; interactive = $false; stateRestored = $false
                    exitCode = -1; error = $_.Exception.Message
                }
                $report.restoreTrials += $trial
                $trial | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $out "restore.jsonl") -Encoding UTF8
                Log "restore attempt $sample/$attemptLimit could not establish the pre-load marker: $($trial.error)"
                Start-Sleep -Seconds 2
                continue
            }
            $timer = [System.Diagnostics.Stopwatch]::StartNew()
            $load = Invoke-Ws @("snapshot-load", "--name", $guest, "--tag", "bench-base", "--json", "--root", $Root) -AllowFailure
            $operationSeconds = $timer.Elapsed.TotalSeconds
            $result = Parse-Json $load.Text
            $observedMarker = $null
            $focus = $null
            $probeError = $null
            if ($load.Code -eq 0) {
                try {
                    $after = Get-GuestIdentity $guest
                    $verifyUntil = $timer.Elapsed.TotalSeconds + 30
                    do {
                        $windows = Invoke-GuestAdb $after @("shell", "dumpsys", "window") 20000
                        $focus = [regex]::Match($windows, '(?m)^\s*mCurrentFocus=(Window\{[^\r\n]+)').Groups[1].Value
                        if ($focus) { break }
                        Start-Sleep -Milliseconds 700
                    } while ($timer.Elapsed.TotalSeconds -lt $verifyUntil)
                    $observedMarker = Invoke-GuestAdb $after @("shell", "settings", "get", "global", $restoreKey) 20000
                }
                catch { $probeError = $_.Exception.Message }
            }
            $timer.Stop()
            $elapsed = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
            $stateRestored = $observedMarker -eq $restoreMarker
            $interactive = -not [string]::IsNullOrWhiteSpace($focus)
            if ($load.Code -eq 0 -and $result.recovery -eq "restored" -and $stateRestored -and $interactive) { $verified++ }
            $trial = [pscustomobject]@{
                trial = $sample
                stage = "load"
                wallSeconds = $elapsed
                operationSeconds = [Math]::Round($operationSeconds, 2)
                consoleSeconds = $result.seconds
                recovery = $result.recovery
                interactive = $interactive
                stateRestored = $stateRestored
                observedMarker = $observedMarker
                exitCode = $load.Code
                error = if ($load.Code -ne 0) { $load.Text } else { $probeError }
            }
            $report.restoreTrials += $trial
            $trial | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $out "restore.jsonl") -Encoding UTF8
            if ($load.Code -eq 0 -and $interactive -and $result.recovery -in @("restored", "cold_boot")) {
                [void]$restoreSamples.Add($elapsed)
            }
            Log ("restore attempt {0}/{1}: {2:N2}s recovery={3} exit={4} verified={5}/{6}" -f `
                $sample, $attemptLimit, $elapsed, $result.recovery, $load.Code, $verified, $Samples)
        }
        $report.scenarios["restoreAttemptWallSeconds"] = Summary ([double[]]$restoreSamples.ToArray())
        $warm = @($report.restoreTrials | Where-Object {
            $_.exitCode -eq 0 -and $_.recovery -eq "restored" -and $_.stateRestored -and $_.interactive
        } | ForEach-Object { $_.wallSeconds })
        $report.scenarios["warmRestore"] = Summary ([double[]]$warm)
        $fallbacks = @($report.restoreTrials | Where-Object recovery -eq "cold_boot").Count
        Log "restore attempts=$($report.restoreTrials.Count) verifiedWarm=$($warm.Count) coldBootFallbacks=$fallbacks"
    }

    if (-not $SkipLatency) {
    Install-Fixture $guest
    $report.scenarios["inputLatencyMs"] = Measure-GuestLatency $guest "singleGuest" ""

    # -------------------------------------------------------- one-shot PNG cost
    $frameSamples = New-Object System.Collections.ArrayList
    for ($sample = 1; $sample -le $Samples; $sample++) {
        $started = Get-Date
        $frame = Invoke-Ws @("frame", "--name", $guest, "--width", "540", "--root", $Root) -AllowFailure
        $elapsed = ((Get-Date) - $started).TotalMilliseconds
        if ($frame.Code -eq 0) { [void]$frameSamples.Add($elapsed) }
        Log ("capture {0}/{1}: {2:N0} ms" -f $sample, $Samples, $elapsed)
    }
    $report.scenarios["captureMs"] = Summary ([double[]]$frameSamples.ToArray())
    }

    # ------------------------------------------------------- two-guest scenario
    if (-not $SkipTwoGuest) {
        $second = New-BenchmarkGuest "bench2"
        Invoke-Ws @("start", "--name", $second, "--root", $Root) | Out-Null
        Invoke-Ws @("wait", "--name", $second, "--timeout", "600", "--root", $Root) | Out-Null
        Install-Fixture $second
        if (-not $SkipLatency) {
            $twoGuestLatency = Measure-GuestLatency $guest "twoGuest" $second
            $report.scenarios["twoGuestLatencyMs"] = $twoGuestLatency
            Add-Check "coverage.two-guest-latency" ($twoGuestLatency.samples -ge 20) (
                "valid={0}/{1} p50={2}ms p95={3}ms" -f $twoGuestLatency.samples, $Samples, $twoGuestLatency.p50, $twoGuestLatency.p95)
        }
        if ($SoakMinutes -gt 0) { Invoke-TwoGuestSoak @($guest, $second) }
        Stop-OwnedGuest $second
    }

    # ------------------------------------------------------- lifecycle stability
    if (-not $SkipLifecycle) {
        Stop-OwnedGuest $guest
        $before = Get-MemorySnapshot
        $profile = Parse-Json (Invoke-Ws @("status", "--name", $guest, "--json", "--root", $Root)).Text
        $ports = @($profile.consolePort, ($profile.consolePort + 1), $profile.grpcPort)
        $cycleTimes = New-Object System.Collections.ArrayList
        $failures = 0
        for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $wait = $null
            $start = Invoke-Ws @("start", "--name", $guest, "--root", $Root) -AllowFailure
            if ($start.Code -eq 0) {
                $wait = Invoke-Ws @("wait", "--name", $guest, "--timeout", "300", "--root", $Root) -AllowFailure
            }
            $stop = Invoke-Ws @("stop", "--name", $guest, "--force", "--root", $Root) -AllowFailure
            $timer.Stop()
            $status = Parse-Json (Invoke-Ws @("status", "--name", $guest, "--json", "--root", $Root)).Text
            $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -in $ports |
                Select-Object LocalAddress, LocalPort, OwningProcess)
            $trial = [pscustomobject]@{
                cycle = $cycle
                wallSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
                start = $start
                wait = $wait
                stop = $stop
                engineAliveAfterStop = $status.engineAlive
                remainingListeners = $listeners
                passed = $start.Code -eq 0 -and $wait -and $wait.Code -eq 0 -and $stop.Code -eq 0 -and
                    $status.engineAlive -eq $false -and $listeners.Count -eq 0
            }
            $trial | ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath (Join-Path $out "lifecycle.jsonl") -Encoding UTF8
            if (-not $trial.passed) {
                $failures++
                Log "cycle $cycle failed start=$($start.Code) wait=$($wait.Code) stop=$($stop.Code) listeners=$($listeners.Count)"
            }
            [void]$cycleTimes.Add($trial.wallSeconds)
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
        Add-Check "lifecycle.no-leak" ($after.engines -le $before.engines) "engine count $($before.engines) -> $($after.engines)"
    }

    # ------------------------------------------------------------------ targets
    $boot = $report.scenarios["coldBoot"]
    $baselineBoot = if ($RawBaseline) { $report.scenarios["rawBoot"] } else { $report.scenarios["baselineBoot"] }
    if ($SkipBaseline -or $SkipColdBoot) {
        Log "SKIP target.boot-overhead: boot comparison omitted"
    }
    elseif ($boot.samples -ge 20 -and $baselineBoot.samples -ge 20) {
        $overhead = [Math]::Round($boot.p50 - $baselineBoot.p50, 2)
        $allowed = [Math]::Max(1.0, [Math]::Round($baselineBoot.p50 * 0.10, 2))
        if ($RawBaseline) {
            Add-Check "target.boot-overhead" ($overhead -le $allowed) (
                "managed p50 {0}s vs raw p50 {1}s = {2}s overhead (allowed {3}s)" -f $boot.p50, $baselineBoot.p50, $overhead, $allowed)
        }
        else {
            Add-Check "comparison.managed-boot" $true (
                "managed p50 {0}s vs second managed profile p50 {1}s" -f $boot.p50, $baselineBoot.p50)
        }
    }
    else {
        Add-Check "comparison.managed-boot" $false "not enough samples (boot $($boot.samples), baseline $($baselineBoot.samples))"
    }

    $restore = $report.scenarios["warmRestore"]
    if ($SkipRestore) {
        Log "SKIP target.warm-restore: restore scenario omitted"
    }
    elseif ($Diagnostic) {
        Add-Check "diagnostic.restore" ($restore.samples -ge $Samples) (
            "verified {0}/{1} warm restores, p50 {2}s" -f $restore.samples, $Samples, $restore.p50)
    }
    elseif ($restore.samples -ge 20) {
        Add-Check "target.warm-restore" ($restore.p50 -le 8) ("warm restore p50 {0}s (target <= 8s)" -f $restore.p50)
    }
    else {
        Add-Check "target.warm-restore" $false "only $($restore.samples) restore samples"
    }

    if (-not $SkipLatency) {
        $latency = $report.scenarios["inputLatencyMs"]
        Add-Check "coverage.single-guest-latency" ($latency.samples -ge 20) (
            "ADB tap to Logcat p50 {0}ms p95 {1}ms over {2} samples" -f $latency.p50, $latency.p95, $latency.samples)
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
            Wait-SoakProcessExit $name
            Invoke-Ws @("destroy", "--name", $name, "--root", $Root) | Out-Null
            Add-Check "cleanup.$name" $true "stopped and removed through the supervisor"
        }
        catch {
            Add-Check "cleanup.$name" $false $_.Exception.Message
        }
    }
    if ($approved -and (Test-Path -LiteralPath $approved)) {
        Remove-Item -LiteralPath $approved
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
