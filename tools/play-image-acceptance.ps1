#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$Root,
    [ValidateSet("system-images;android-35;google_apis_playstore_ps16k;x86_64",
                 "system-images;android-36;google_apis_playstore;x86_64")]
    [Parameter(Mandatory)][string]$Image,
    [string]$OutDir = "artifacts/phase3/play"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "fixtures/acceptance-payload.ps1")
$cli = Get-AcceptanceBinary "wsctl.exe" "src/EmulatorWorkspace.Cli/bin/Debug/net10.0/wsctl.exe"
$fixture = Get-AcceptanceBinary "ws-sample.apk" "artifacts/phase1/sample/ws-sample.apk"
$package = "dev.emulatorworkspace.sample"
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA "Android/Sdk" }
$imageDir = Join-Path $sdk ($Image -replace ";", "/")
[xml]$metadata = Get-Content -LiteralPath (Join-Path $imageDir "package.xml") -Raw
$localPackage = $metadata.SelectSingleNode("//*[local-name()='localPackage']")
if ($localPackage.path -ne $Image) { throw "SDK package metadata does not match $Image." }
$api = ($Image -split ";")[1] -replace "^android-", ""
$stockRamdisk = Join-Path $imageDir "ramdisk.img"
$stockHash = (Get-FileHash -LiteralPath $stockRamdisk).Hash

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
Write-AcceptanceContext $out
$guest = "play-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
$checks = [System.Collections.Generic.List[object]]::new()
$created = $false

function Invoke-PlayCheck([string]$step, [string[]]$arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $cli @arguments --name $guest --root $Root 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    $output.Trim() | Set-Content -LiteralPath (Join-Path $out "$step.txt") -Encoding UTF8
    if ($code -ne 0) { throw "wsctl $($arguments[0]) exited $code. See $step.txt." }
    return $output
}

function Check([string]$name, [bool]$passed, [string]$detail) {
    $checks.Add([pscustomobject]@{ name = $name; passed = $passed; detail = $detail })
    Write-Host "$name $passed $detail"
}

try {
    if (-not (Test-Path -LiteralPath $fixture)) { throw "Run sample/build-sample-apk.ps1 first." }
    $apk = Join-Path $Root "apps/$guest.apk"
    New-Item -ItemType Directory -Force (Split-Path -Parent $apk) | Out-Null
    Copy-Item -LiteralPath $fixture -Destination $apk
    Invoke-PlayCheck "create" @("create", "--image", $Image) | Out-Null
    $created = $true
    Invoke-PlayCheck "start" @("start") | Out-Null
    Invoke-PlayCheck "wait" @("wait", "--timeout", "600") | Out-Null
    $status = Invoke-PlayCheck "status" @("status", "--json") | ConvertFrom-Json
    Check "interactive" ($status.state -eq "Running" -and $status.bootCompleted -eq "1" -and
        $status.imagePackage -eq $Image -and $status.abi -eq "x86_64" -and $status.sdkLevel -eq $api) $status.fingerprint
    $rootState = Invoke-PlayCheck "root-state" @("adb-root-state") | ConvertFrom-Json
    Check "stock-user" ($rootState.uid -eq "2000" -and $rootState.buildType -eq "user" -and
        $rootState.rootCapable -eq $false) "uid=$($rootState.uid) buildType=$($rootState.buildType)"
    Invoke-PlayCheck "simulation" @("simulation-probe") | Out-Null
    Invoke-PlayCheck "snapshot-save" @("snapshot-save", "--tag", "stock", "--json") | Out-Null
    Invoke-PlayCheck "install" @("install", "--apk", $apk) | Out-Null
    $app = Invoke-PlayCheck "package-info" @("package-info", "--package", $package) | ConvertFrom-Json
    Check "package-installed" ($app.installed -eq $true) $package
    Invoke-PlayCheck "launch" @("launch", "--package", $package) | Out-Null
    $focus = Invoke-PlayCheck "foreground" @("foreground")
    Check "foreground" ($focus.Contains($package)) $focus.Trim()
    $frame = Join-Path $out "fixture.png"
    Invoke-PlayCheck "frame" @("frame", "--width", "540", "--out", $frame) | Out-Null
    Check "capture" ((Get-Item -LiteralPath $frame).Length -gt 8) "fixture.png"
    $restore = Invoke-PlayCheck "snapshot-load" @("snapshot-load", "--tag", "stock", "--json") | ConvertFrom-Json
    $after = Invoke-PlayCheck "package-after-restore" @("package-info", "--package", $package) | ConvertFrom-Json
    Check "snapshot-state" ($restore.recovery -eq "restored" -and $after.installed -eq $false) (
        "recovery=$($restore.recovery) installed=$($after.installed)")
}
catch {
    Check "workflow" $false $_.Exception.Message
}
finally {
    if ($created) {
        try {
            Invoke-PlayCheck "stop" @("stop", "--force") | Out-Null
            Invoke-PlayCheck "destroy" @("destroy") | Out-Null
            Check "cleanup" $true $guest
        }
        catch { Check "cleanup" $false $_.Exception.Message }
    }
    if ($apk -and (Test-Path -LiteralPath $apk)) { Remove-Item -LiteralPath $apk }
    Check "stock-ramdisk" ((Get-FileHash -LiteralPath $stockRamdisk).Hash -eq $stockHash) $stockHash
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $gpu = Get-CimInstance Win32_VideoController | Select-Object -First 1
    $emulator = Join-Path $sdk "emulator/emulator.exe"
    $fixtureHash = $null
    if (Test-Path -LiteralPath $fixture) { $fixtureHash = (Get-FileHash -LiteralPath $fixture).Hash }
    [ordered]@{
        runId = $run
        sourceCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
        image = $Image
        revision = $localPackage.revision.major
        host = [ordered]@{
            os = $os.Caption
            windowsVersion = $os.Version
            cpu = $cpu.Name
            gpu = $gpu.Name
            driver = $gpu.DriverVersion
            emulator = (& $emulator -version 2>&1 | Select-Object -First 1 | Out-String).Trim()
            acceleration = (& $emulator -accel-check 2>&1 | Select-String 'WHPX' | Out-String).Trim()
        }
        fixtureSha256 = $fixtureHash
        checks = $checks.ToArray()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out "summary.json") -Encoding UTF8
    Write-Host "Evidence: $out"
}

if ($checks | Where-Object { -not $_.passed }) { exit 2 }
