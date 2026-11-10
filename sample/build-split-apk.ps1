#requires -Version 5.1
<#
.SYNOPSIS
    Builds a signed Android App Bundle and a targeted APK set (.apks) from the
    sample fixture with pinned SDK tools and bundletool.

.DESCRIPTION
    The pipeline is: javac -> d8 -> aapt2 link --proto-format -> module zip ->
    bundletool build-bundle -> bundletool build-apks. The resulting .apks can be
    installed on one selected guest with
    "bundletool install-apks --device-id <serial>", which is the deployment path
    exercised by tools\phase3-acceptance.ps1.

    The module zip is written with explicit forward-slash entry names because
    bundletool requires "manifest/AndroidManifest.xml" inside the module.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File sample\build-split-apk.ps1 `
      -OutDir D:\Phase3Tools\apks
#>
param(
    [string]$Sdk = $env:ANDROID_HOME,
    [string]$OutDir = "D:\Phase3Tools\apks",
    [string]$BuildTools = "35.0.0",
    [string]$Platform = "android-35",
    [string]$Bundletool = "D:\Phase3Tools\bundletool-1.18.1.jar"
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$tools = Join-Path $Sdk "build-tools\$BuildTools"
$androidJar = Join-Path $Sdk "platforms\$Platform\android.jar"
$keystore = Join-Path $env:USERPROFILE ".android\debug.keystore"
foreach ($required in @(
        (Join-Path $tools "aapt2.exe"),
        (Join-Path $tools "d8.bat"),
        $androidJar,
        $keystore,
        $Bundletool)) {
    if (-not (Test-Path $required)) { throw "Missing build input: $required" }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$work = Join-Path $OutDir "work"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $work "classes"), (Join-Path $work "dex") | Out-Null

Write-Host "Compiling and dexing..."
$sources = Get-ChildItem (Join-Path $PSScriptRoot "src") -Recurse -Filter "*.java" | ForEach-Object { $_.FullName }
& (Join-Path $env:JAVA_HOME "bin\javac.exe") -encoding UTF-8 -source 8 -target 8 -Xlint:-options `
    -bootclasspath $androidJar -d (Join-Path $work "classes") $sources 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "javac failed with exit code $LASTEXITCODE" }
$classes = Get-ChildItem (Join-Path $work "classes") -Recurse -Filter "*.class" | ForEach-Object { $_.FullName }
& (Join-Path $tools "d8.bat") --lib $androidJar --min-api 35 --output (Join-Path $work "dex") $classes 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "d8 failed with exit code $LASTEXITCODE" }

Write-Host "Linking a proto-format module..."
& (Join-Path $tools "aapt2.exe") link `
    --manifest (Join-Path $PSScriptRoot "AndroidManifest.xml") `
    -I $androidJar `
    --proto-format --min-sdk-version 35 --target-sdk-version 35 `
    -o (Join-Path $work "base-proto.apk") 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed with exit code $LASTEXITCODE" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$apk = Join-Path $work "base-proto.apk"
$zip = [System.IO.Compression.ZipFile]::OpenRead($apk)
$manifestBytes = $null
$resourcesBytes = $null
foreach ($entry in $zip.Entries) {
    if ($entry.FullName -eq "AndroidManifest.xml") {
        $stream = $entry.Open()
        $memory = New-Object System.IO.MemoryStream
        $stream.CopyTo($memory)
        $manifestBytes = $memory.ToArray()
        $stream.Close()
    }
    elseif ($entry.FullName -eq "resources.pb") {
        $stream = $entry.Open()
        $memory = New-Object System.IO.MemoryStream
        $stream.CopyTo($memory)
        $resourcesBytes = $memory.ToArray()
        $stream.Close()
    }
}
$zip.Dispose()

$moduleZip = Join-Path $work "base.zip"
if (Test-Path $moduleZip) { Remove-Item $moduleZip -Force }
$archive = [System.IO.Compression.ZipFile]::Open($moduleZip, [System.IO.Compression.ZipArchiveMode]::Create)
function Add-Bytes($archive, $name, $bytes) {
    $entry = $archive.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
}
Add-Bytes $archive "manifest/AndroidManifest.xml" $manifestBytes
Add-Bytes $archive "resources.pb" $resourcesBytes
$dexBytes = [IO.File]::ReadAllBytes((Join-Path $work "dex\classes.dex"))
Add-Bytes $archive "dex/classes.dex" $dexBytes
$archive.Dispose()

Write-Host "Building the app bundle and the targeted APK set..."
$bundle = Join-Path $OutDir "ws-sample.aab"
$apks = Join-Path $OutDir "ws-sample.apks"
Remove-Item $bundle, $apks -Force -ErrorAction SilentlyContinue
& java -jar $Bundletool build-bundle --modules=$moduleZip --output=$bundle 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "bundletool build-bundle failed with exit code $LASTEXITCODE" }
& java -jar $Bundletool build-apks --bundle=$bundle --output=$apks `
    --ks=$keystore --ks-pass=pass:android --ks-key-alias=androiddebugkey --key-pass=pass:android `
    --overwrite 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "bundletool build-apks failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host ("AAB      : {0} ({1} bytes)" -f $bundle, (Get-Item $bundle).Length)
Write-Host ("APKS     : {0} ({1} bytes)" -f $apks, (Get-Item $apks).Length)
Write-Host ("APKS hash: {0}" -f (Get-FileHash $apks -Algorithm SHA256).Hash)
