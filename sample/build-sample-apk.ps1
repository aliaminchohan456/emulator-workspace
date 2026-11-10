#requires -Version 5.1
<#
.SYNOPSIS
    Builds the deterministic Phase 1 sample APK with pinned SDK tools.

.DESCRIPTION
    Uses javac + d8 + aapt2 + zipalign + apksigner from the pinned Android SDK
    build-tools 35.0.0 and platform android-35. No Gradle or network access is
    required. The resulting APK is debug-signed with the user's standard
    ~/.android/debug.keystore and its SHA-256 is recorded next to it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File sample\build-sample-apk.ps1
#>
param(
    [string]$Sdk = $env:ANDROID_SDK_ROOT,
    [string]$OutDir = "artifacts\phase1\sample",
    [string]$BuildTools = "35.0.0",
    [string]$Platform = "android-35"
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Sdk) { $Sdk = $env:ANDROID_HOME }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
if (-not (Test-Path $Sdk)) { throw "Android SDK not found. Pass -Sdk or set ANDROID_HOME." }

$tools = Join-Path $Sdk "build-tools\$BuildTools"
$androidJar = Join-Path $Sdk "platforms\$Platform\android.jar"
foreach ($required in @(
    (Join-Path $tools "aapt2.exe"),
    (Join-Path $tools "d8.bat"),
    (Join-Path $tools "zipalign.exe"),
    (Join-Path $tools "apksigner.bat"),
    $androidJar,
    (Join-Path $env:JAVA_HOME "bin\javac.exe"))) {
    if (-not (Test-Path $required)) { throw "Missing build input: $required" }
}

$keystore = Join-Path $env:USERPROFILE ".android\debug.keystore"
if (-not (Test-Path $keystore)) { throw "Debug keystore not found at $keystore" }

$out = Join-Path $repo $OutDir
$work = Join-Path $out "work"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, (Join-Path $work "classes"), (Join-Path $work "dex") | Out-Null

Write-Host "Compiling Java sources..."
$sources = Get-ChildItem (Join-Path $PSScriptRoot "src") -Recurse -Filter "*.java" | ForEach-Object { $_.FullName }
& (Join-Path $env:JAVA_HOME "bin\javac.exe") -encoding UTF-8 -source 8 -target 8 -Xlint:-options `
    -bootclasspath $androidJar -d (Join-Path $work "classes") $sources 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "javac failed with exit code $LASTEXITCODE" }

Write-Host "Dexing with d8..."
$classes = Get-ChildItem (Join-Path $work "classes") -Recurse -Filter "*.class" | ForEach-Object { $_.FullName }
& (Join-Path $tools "d8.bat") --lib $androidJar --min-api 35 --output (Join-Path $work "dex") $classes 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "d8 failed with exit code $LASTEXITCODE" }

Write-Host "Linking resources and manifest..."
$baseApk = Join-Path $work "base.apk"
& (Join-Path $tools "aapt2.exe") link -o $baseApk --manifest (Join-Path $PSScriptRoot "AndroidManifest.xml") `
    -I $androidJar --min-sdk-version 35 --target-sdk-version 35 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed with exit code $LASTEXITCODE" }

Write-Host "Adding classes.dex..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($baseApk, "Update")
try {
    $dex = Join-Path $work "dex\classes.dex"
    $existing = $zip.GetEntry("classes.dex")
    if ($existing) { $existing.Delete() }
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, $dex, "classes.dex", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    $zip.Dispose()
}

Write-Host "Aligning..."
$aligned = Join-Path $work "aligned.apk"
& (Join-Path $tools "zipalign.exe") -f 4 $baseApk $aligned 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "zipalign failed with exit code $LASTEXITCODE" }

Write-Host "Signing with the debug keystore..."
$signed = Join-Path $out "ws-sample.apk"
& (Join-Path $tools "apksigner.bat") sign --ks $keystore --ks-pass pass:android --key-pass pass:android `
    --ks-key-alias androiddebugkey --out $signed $aligned 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "apksigner failed with exit code $LASTEXITCODE" }

& (Join-Path $tools "apksigner.bat") verify --verbose $signed 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed" }

$hash = (Get-FileHash $signed -Algorithm SHA256).Hash
$size = (Get-Item $signed).Length
$metadata = [pscustomobject]@{
    apk = $signed
    sha256 = $hash
    bytes = $size
    package = "dev.emulatorworkspace.sample"
    activity = "dev.emulatorworkspace.sample/.MainActivity"
    buildTools = $BuildTools
    platform = $Platform
    javac = (& (Join-Path $env:JAVA_HOME "bin\javac.exe") -version 2>&1 | Out-String).Trim()
    builtUtc = (Get-Date).ToUniversalTime().ToString("o")
}
$metadata | ConvertTo-Json | Set-Content (Join-Path $out "sample-apk.json") -Encoding UTF8

Write-Host ""
Write-Host ("APK      : " + $signed)
Write-Host ("SHA-256  : " + $hash)
Write-Host ("Size     : " + $size + " bytes")
Write-Host ("Metadata : " + (Join-Path $out "sample-apk.json"))
exit 0
