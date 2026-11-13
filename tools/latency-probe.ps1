#requires -Version 5.1
<#
.SYNOPSIS
    Runs the desktop composition-proxy input latency probe on a real guest.

.DESCRIPTION
    Ensures the guest is running, brings the sample app to the foreground, then
    launches the desktop with --latency-probe. Each trial dispatches one tap and
    measures from desktop dispatch to the next WPF composition pass after the
    frame that contains the changed sample marker. Results are written as JSON.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\latency-probe.ps1 -Root D:\Phase1Root -Name t1 -Count 25
#>
param(
    [string]$Root = "D:\Phase1Root",
    [string]$Name = "t1",
    [ValidateRange(1, 100)][int]$Count = 25,
    [switch]$Diagnostic,
    [ValidateSet(1, 2)][int]$GuestCount = 1,
    [switch]$CompareCapture,
    [string]$Gpu,
    [string]$OutDir = "artifacts/phase3/wpf",
    [int]$BootTimeoutSeconds = 600,
    [string]$CandidateRelease = "",
    [string]$CandidatePayload = "",
    [string]$DesktopExecutable = "",
    [string]$SupervisorExecutable = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src/EmulatorWorkspace.Cli/bin/Debug/net10.0/wsctl.exe"
$desktop = Join-Path $repo "src/EmulatorWorkspace.Desktop/bin/Debug/net10.0-windows/EmulatorWorkspace.Desktop.exe"
$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$candidate = $null
$owned = $false
$peerOwned = $false
$checks = [System.Collections.Generic.List[object]]::new()
$resultPath = Join-Path $out "presentation.json"

function Invoke-Wsctl([string[]]$arguments, [string]$guestName = $Name, [string]$guestRoot = $Root) {
    $prior = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $wsctl @arguments --name $guestName --root $guestRoot 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prior }
    if ($code -ne 0) { throw "wsctl $($arguments[0]) failed ($code): $output" }
    return $output
}

function Install-ProbeFixture([string]$guestName, [string]$guestRoot) {
    $fixture = Join-Path $verified.payload "ws-sample.apk"
    $approved = Join-Path $guestRoot "apps/ws-sample.apk"
    New-Item -ItemType Directory -Force (Split-Path -Parent $approved) | Out-Null
    Copy-Item -LiteralPath $fixture -Destination $approved
    Invoke-Wsctl @("install", "--apk", $approved) $guestName $guestRoot | Out-Null
}

function Check([string]$name, [bool]$passed, [string]$detail) {
    $checks.Add([pscustomobject]@{ name = $name; passed = $passed; detail = $detail })
    Write-Host "$name $passed $detail"
}

function Measure-EncodedCapture([string]$adb, [string]$transportId) {
    $ffprobe = (Get-Command ffprobe.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $video = Join-Path $out "guest.h264"
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $adb
    $info.Arguments = "-t $transportId exec-out screenrecord --size 540x1200 --bit-rate 4000000 --time-limit 10 --output-format=h264 -"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $output = [IO.File]::Create($video)
    $process = $null
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($info)
        $copy = $process.StandardOutput.BaseStream.CopyToAsync($output)
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { throw "The encoded capture exceeded 30 s." }
        if (-not $copy.Wait(5000)) { throw "The encoded capture output did not finish copying." }
        $timer.Stop()
        $errors.Result | Set-Content -LiteralPath (Join-Path $out "screenrecord-stderr.txt") -Encoding UTF8
        if ($process.ExitCode -ne 0) { throw "screenrecord exited with $($process.ExitCode)." }
    }
    finally {
        try {
            if ($process) {
                try {
                    if (-not $process.HasExited) { $process.Kill() }
                }
                catch { if (-not $process.WaitForExit(5000)) { throw } }
                if (-not $process.WaitForExit(5000)) { throw "The owned capture process did not exit." }
            }
        }
        finally {
            if ($process) { $process.Dispose() }
            $output.Dispose()
        }
    }

    $raw = & $ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=codec_name,width,height,nb_read_frames -of json $video
    if ($LASTEXITCODE -ne 0) { throw "ffprobe could not read the captured H.264 stream." }
    $stream = ($raw | Out-String | ConvertFrom-Json).streams | Select-Object -First 1
    if ($stream.codec_name -ne "h264" -or $stream.nb_read_frames -notmatch '^\d+$') {
        throw "The captured stream did not contain countable H.264 frames."
    }
    [ordered]@{
        command = $info.Arguments
        transportId = $transportId
        seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
        frames = [int]$stream.nb_read_frames
        framesPerWallSecond = [Math]::Round([int]$stream.nb_read_frames / $timer.Elapsed.TotalSeconds, 2)
        bytes = (Get-Item -LiteralPath $video).Length
        width = $stream.width
        height = $stream.height
        adbSha256 = (Get-FileHash -LiteralPath $adb).Hash
        ffprobeSha256 = (Get-FileHash -LiteralPath $ffprobe).Hash
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out "encoded-capture.json") -Encoding UTF8
}

try {
    if ($GuestCount -eq 2 -and -not ($CandidateRelease -and $CandidatePayload)) {
        throw "The two-guest probe requires the verified candidate inputs."
    }
    if ($CompareCapture -and ($GuestCount -ne 1 -or -not ($CandidateRelease -and $CandidatePayload))) {
        throw "Capture comparison requires a single verified candidate guest."
    }
    if ($CandidateRelease -or $CandidatePayload) {
        if (-not ($CandidateRelease -and $CandidatePayload)) { throw "Pass both candidate paths." }
        . (Join-Path $PSScriptRoot "fixtures/candidate-payload.ps1")
        $verified = Resolve-VerifiedCandidate $CandidateRelease $CandidatePayload
        $candidate = $verified.identity
        $candidate.evidenceScope = if ($DesktopExecutable) { "CLI, supervisor and sample APK" } else { "entire runtime payload" }
        $wsctl = Join-Path $verified.payload "wsctl.exe"
        $desktop = Join-Path $verified.payload "EmulatorWorkspace.Desktop.exe"
        $env:EMULATORWORKSPACE_SUPERVISOR_EXE = Join-Path $verified.payload "EmulatorWorkspace.Supervisor.exe"
        if ($SupervisorExecutable) {
            $env:EMULATORWORKSPACE_SUPERVISOR_EXE = (Resolve-Path -LiteralPath $SupervisorExecutable).ProviderPath
            $candidate.evidenceScope = if ($DesktopExecutable) { "CLI and sample APK" } else { "CLI, desktop and sample APK" }
            $supervisorFiles = @("EmulatorWorkspace.Supervisor.exe", "EmulatorWorkspace.Supervisor.dll", "EmulatorWorkspace.Core.dll")
            $supervisorDirectory = Split-Path -Parent $env:EMULATORWORKSPACE_SUPERVISOR_EXE
            $supervisorOverride = [ordered]@{
                version = (Get-Item -LiteralPath $env:EMULATORWORKSPACE_SUPERVISOR_EXE).VersionInfo.ProductVersion
                files = @($supervisorFiles | ForEach-Object {
                    @{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $supervisorDirectory $_) -ErrorAction Stop).Hash }
                })
            }
        }
        if (Get-Process qemu-system-x86_64-headless, qemu-system-x86_64 -ErrorAction SilentlyContinue) {
            throw "Run the quiet-host WPF probe after other emulator guests have stopped."
        }
        if (Test-Path -LiteralPath $Root) { throw "Use a new disposable root for the candidate probe." }
        $Name = "wpf-latency-$run"
        Invoke-Wsctl @("create") | Out-Null
        $owned = $true
    }

    if ($DesktopExecutable) { $desktop = (Resolve-Path -LiteralPath $DesktopExecutable).ProviderPath }
    $desktopSha256 = (Get-FileHash -LiteralPath $desktop).Hash
    $desktopVersion = (Get-Item -LiteralPath $desktop).VersionInfo.ProductVersion
    $desktopAssembly = Join-Path (Split-Path -Parent $desktop) "EmulatorWorkspace.Desktop.dll"
    if (Test-Path -LiteralPath $desktopAssembly) { $desktopAssemblySha256 = (Get-FileHash -LiteralPath $desktopAssembly).Hash }
    $status = Invoke-Wsctl @("status", "--json") | ConvertFrom-Json
    if (-not $status.engineAlive) {
        $arguments = @("start")
        if ($Gpu) { $arguments += @("--gpu", $Gpu) }
        Invoke-Wsctl $arguments | Out-Null
    }
    Invoke-Wsctl @("wait", "--timeout", "$BootTimeoutSeconds") | Out-Null
    $list = Invoke-Wsctl @("list")
    if (@($list.Trim() -split '\r?\n').Count -ne 1 -or $list -notmatch "(?m)^$([regex]::Escape($Name))\s") {
        throw "The desktop probe requires exactly one profile in the selected root."
    }

    if ($owned) { Install-ProbeFixture $Name $Root }
    $package = "dev.emulatorworkspace.sample"
    Invoke-Wsctl @("launch", "--package", $package) | Out-Null
    $foreground = Invoke-Wsctl @("foreground")
    if (-not $foreground.Contains($package)) { throw "The fixture did not reach the foreground." }
    $primaryIdentity = Invoke-Wsctl @("adb-identity", "--json") | ConvertFrom-Json
    if (-not $primaryIdentity.transportUsable -or $primaryIdentity.transportId -notmatch '^\d+$') {
        throw "The foreground workload has no usable managed ADB transport."
    }
    $primaryStatus = Invoke-Wsctl @("status", "--json") | ConvertFrom-Json
    $effectiveGpu = $primaryStatus.gpu
    if ($Gpu -and $effectiveGpu -ne $Gpu) {
        throw "The active guest reports GPU '$effectiveGpu', expected '$Gpu'."
    }

    if ($GuestCount -eq 2) {
        $peerRoot = "$Root-peer"
        if (Test-Path -LiteralPath $peerRoot) { throw "Use a new disposable peer root for the candidate probe." }
        $peerName = "wpf-peer-$run"
        Invoke-Wsctl @("create") $peerName $peerRoot | Out-Null
        $peerOwned = $true
        $peerStart = @("start")
        if ($Gpu) { $peerStart += @("--gpu", $Gpu) }
        Invoke-Wsctl $peerStart $peerName $peerRoot | Out-Null
        Invoke-Wsctl @("wait", "--timeout", "$BootTimeoutSeconds") $peerName $peerRoot | Out-Null
        Install-ProbeFixture $peerName $peerRoot
        Invoke-Wsctl @("launch", "--package", $package) $peerName $peerRoot | Out-Null
        if (-not (Invoke-Wsctl @("foreground") $peerName $peerRoot).Contains($package)) {
            throw "The peer's fixture did not reach the foreground."
        }
        $peerIdentity = Invoke-Wsctl @("adb-identity", "--json") $peerName $peerRoot | ConvertFrom-Json
        if (-not ($primaryIdentity.transportUsable -and $peerIdentity.transportUsable) -or
            $primaryIdentity.transportId -eq $peerIdentity.transportId -or
            $primaryIdentity.enginePid -eq $peerIdentity.enginePid) {
            throw "The WPF probe requires two distinct managed guest transports."
        }
        @($primaryIdentity, $peerIdentity) | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $out "guest-identities.json") -Encoding UTF8
    }
    Start-Sleep -Seconds 3

    $launch = New-Object System.Diagnostics.ProcessStartInfo
    $launch.FileName = $desktop
    $launch.UseShellExecute = $false
    $launch.Arguments = ('--root "{0}" --latency-probe {1} --out "{2}"' -f $Root, $Count, $resultPath)
    if ($verified) {
        $launch.EnvironmentVariables["EMULATORWORKSPACE_SUPERVISOR_EXE"] = $env:EMULATORWORKSPACE_SUPERVISOR_EXE
    }
    $desktopProcess = [System.Diagnostics.Process]::Start($launch)
    if (-not $desktopProcess.WaitForExit(300000)) {
        $desktopProcess.Kill()
        $desktopProcess.WaitForExit(15000) | Out-Null
        throw "WPF latency probe exceeded five minutes."
    }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw "The desktop did not write presentation evidence." }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($Diagnostic) {
        Check "diagnostic.coverage" ($result.valid -eq $Count) "$($result.valid)/$($result.trials) valid samples, p95=$($result.p95Ms) ms, delivered=$($result.deliveredFps) FPS"
    }
    else {
        Check "presentation.coverage" ($result.valid -ge 20) "$($result.valid)/$($result.trials) valid samples"
        Check "presentation.latency" ($result.valid -ge 20 -and $result.p95Ms -le 100) "p95=$($result.p95Ms) ms"
        Check "presentation.throughput" ($result.deliveredFps -ge 60) "viewport-delivered FPS=$($result.deliveredFps), see workload evidence before evaluating the release gate"
    }
    $afterIdentity = Invoke-Wsctl @("adb-identity", "--json") | ConvertFrom-Json
    if ($afterIdentity.identity -ne $primaryIdentity.identity) { throw "The measured guest identity changed." }
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
    $adb = Join-Path $sdk 'platform-tools/adb.exe'
    $gfx = & $adb -t $primaryIdentity.transportId shell dumpsys -t 10 gfxinfo $package framestats
    if ($LASTEXITCODE -ne 0 -or -not ($gfx -match '---PROFILEDATA---')) {
        throw "The measured guest did not return gfxinfo framestats."
    }
    $gfx | Set-Content -LiteralPath (Join-Path $out "guest-framestats.txt") -Encoding UTF8
    if ($CompareCapture) {
        if (-not (Invoke-Wsctl @("foreground")).Contains($package)) {
            throw "The sample must remain foreground for the capture comparison."
        }
        try {
            Invoke-Wsctl @("capture-bench", "--format", "rgb", "--width", "540", "--height", "1200", "--seconds", "10",
                "--out", (Join-Path $out "rgb-capture.json")) | Out-Null
            Check "capture.rgb" $true "Saved raw controller stream evidence."
        }
        catch { Check "capture.rgb" $false $_.Exception.Message }
        try {
            $captureIdentity = Invoke-Wsctl @("adb-identity", "--json") | ConvertFrom-Json
            if ($captureIdentity.identity -ne $primaryIdentity.identity) { throw "The measured guest identity changed." }
            Measure-EncodedCapture $adb $primaryIdentity.transportId
            Check "capture.h264" $true "Saved encoded guest stream evidence."
        }
        catch { Check "capture.h264" $false $_.Exception.Message }
    }
}
catch { Check "workflow" $false $_.Exception.Message }
finally {
    if ($peerOwned) {
        try {
            Invoke-Wsctl @("stop", "--force") $peerName $peerRoot | Out-Null
            Invoke-Wsctl @("destroy") $peerName $peerRoot | Out-Null
            Invoke-Wsctl @("supervisor-stop") $peerName $peerRoot | Out-Null
            Check "cleanup.peer" $true $peerName
        }
        catch { Check "cleanup.peer" $false $_.Exception.Message }
    }
    if ($owned) {
        $desktopLog = Join-Path $Root "logs/desktop.log"
        if (Test-Path -LiteralPath $desktopLog) {
            Copy-Item -LiteralPath $desktopLog -Destination (Join-Path $out "desktop.log") -ErrorAction Continue
        }
        try {
            Invoke-Wsctl @("stop", "--force") | Out-Null
            Invoke-Wsctl @("destroy") | Out-Null
            Invoke-Wsctl @("supervisor-stop") | Out-Null
            Check "cleanup" $true $Name
        }
        catch { Check "cleanup" $false $_.Exception.Message }
    }
    [ordered]@{
        runId = $run
        finishedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        sourceCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
        harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
        candidate = $candidate
        root = $Root
        guestCount = $GuestCount
        gpu = $effectiveGpu
        diagnostic = [bool]$Diagnostic
        desktopSha256 = $desktopSha256
        desktopAssemblySha256 = $desktopAssemblySha256
        desktopVersion = $desktopVersion
        desktopOverride = [bool]$DesktopExecutable
        supervisorOverride = $supervisorOverride
        checks = $checks.ToArray()
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out "summary.json") -Encoding UTF8
    Write-Host "Evidence: $out"
}
if ($checks | Where-Object { -not $_.passed }) { exit 2 }
