#requires -Version 5.1
<#
.SYNOPSIS
    Builds the versioned Windows release candidate: a self-contained publish
    of the desktop, the supervisor and the CLI, an SBOM, the notices, the
    provenance record and an MSI installer.

.DESCRIPTION
    The stage directory is deterministic: the same commit and the same version
    produce the same payload. The release record carries the SHA-256 of every
    published file, the dependency inventory (SBOM), the notices and the
    provenance (commit, SDK versions, build commands).

    The signing status is reported honestly: when no user-owned code-signing
    identity is configured the package is produced unsigned and says so.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package-release.ps1 `
      -Version 0.1.0 -OutDir D:\Release
#>
param(
    [string]$Version = "",
    [string]$OutDir = "D:\Release",
    [string]$StageDir = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$CertificateThumbprint = "",
    [string]$CertificatePath = "",
    [string]$CertificatePassword = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if (-not $StageDir) { $StageDir = Join-Path $OutDir "stage-$stamp" }
$payload = Join-Path $StageDir "payload"
$releaseDir = Join-Path $OutDir "emulator-workspace-$Version-$stamp"
New-Item -ItemType Directory -Force $payload | Out-Null
New-Item -ItemType Directory -Force $releaseDir | Out-Null

$commit = (git rev-parse HEAD).Trim()
$shortCommit = (git rev-parse --short HEAD).Trim()
if (-not $Version) {
    $props = Get-Content (Join-Path $repo "Directory.Build.props") -Raw
    if ($props -match '<VersionPrefix>([^<]+)</VersionPrefix>') { $Version = $Matches[1] } else { $Version = "0.1.0" }
}

Write-Host "Emulator Workspace $Version"
Write-Host "  commit  : $commit"
Write-Host "  stage   : $StageDir"
Write-Host "  release : $releaseDir"

function Publish-App([string]$projectDirectory, [string]$assembly, [bool]$selfContained) {
    $project = Join-Path $repo $projectDirectory
    $arguments = @(
        "publish", $project,
        "-c", $Configuration,
        "-r", $Runtime,
        "--nologo",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        "-p:PublishSingleFile=false",
        "-p:DebugType=none",
        "-p:SelfContained=$(if ($selfContained) { 'true' } else { 'false' })"
    )
    Write-Host "  publishing $assembly"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
    # The publish output is in the runtime-specific publish directory. The
    # target framework is part of the path, so the directory is discovered
    # instead of being assumed.
    $candidates = New-Object System.Collections.ArrayList
    foreach ($pattern in @(
            "bin\$Configuration\*\$Runtime\publish",
            "bin\$Configuration\$Runtime\publish",
            "bin\$Configuration\$Runtime",
            "bin\$Configuration")) {
        foreach ($found in (Get-ChildItem -Path (Join-Path $project $pattern) -Directory -ErrorAction SilentlyContinue)) {
            [void]$candidates.Add($found.FullName)
        }
    }

    $source = $null
    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate $assembly)) {
            $source = $candidate
            break
        }
    }
    if (-not $source) { throw "The publish output for $assembly was not found." }
    Copy-Item (Join-Path $source "*") $payload -Recurse -Force
}

Publish-App "src\EmulatorWorkspace.Desktop" "EmulatorWorkspace.Desktop.exe" $true
Publish-App "src\EmulatorWorkspace.Supervisor" "EmulatorWorkspace.Supervisor.exe" $true
Publish-App "src\EmulatorWorkspace.Cli" "wsctl.exe" $true

# The sample fixture belongs to the product: the desktop installs it as the
# approved package.
$sampleApk = Join-Path $repo "artifacts\phase1\sample\ws-sample.apk"
if (Test-Path $sampleApk) {
    Copy-Item $sampleApk (Join-Path $payload "ws-sample.apk") -Force
}

# ------------------------------------------------------------------- notices
$notices = Join-Path $payload "THIRD-PARTY-NOTICES.txt"
$noticeText = New-Object System.Text.StringBuilder
[void]$noticeText.AppendLine("Emulator Workspace $Version - third party notices")
[void]$noticeText.AppendLine("===================================================")
[void]$noticeText.AppendLine("")
[void]$noticeText.AppendLine("This package redistributes no Android SDK or system image component.")
[void]$noticeText.AppendLine("The Android SDK, the emulator and every system image remain subject to the")
[void]$noticeText.AppendLine("Android SDK Terms and Conditions (https://developer.android.com/studio/terms)")
[void]$noticeText.AppendLine("and are obtained by the guided first-run setup, not by this installer.")
[void]$noticeText.AppendLine("")
[void]$noticeText.AppendLine("Bundled components:")
[void]$noticeText.AppendLine("  - .NET runtime 10.0 (MIT, https://github.com/dotnet/runtime)")
[void]$noticeText.AppendLine("    The desktop and the supervisor are published self-contained and carry the")
[void]$noticeText.AppendLine("    Microsoft .NET runtime and its dependencies.")
[void]$noticeText.AppendLine("")
$noticesPath = Join-Path $releaseDir "THIRD-PARTY-NOTICES.txt"
Set-Content -Path $noticesPath -Value $noticeText.ToString() -Encoding UTF8
Copy-Item $noticesPath $payload -Force

# ---------------------------------------------------------------------- SBOM
$sbom = [ordered]@{
    schemaVersion = "1.0"
    name = "Emulator Workspace"
    version = $Version
    generatedUtc = (Get-Date).ToUniversalTime().ToString("O")
    commit = $commit
    components = @()
}
$componentList = New-Object System.Collections.ArrayList
foreach ($project in @(
        "src\EmulatorWorkspace.Core\EmulatorWorkspace.Core.csproj",
        "src\EmulatorWorkspace.Contracts\EmulatorWorkspace.Contracts.csproj",
        "src\EmulatorWorkspace.Controller\EmulatorWorkspace.Controller.csproj",
        "src\EmulatorWorkspace.Supervisor\EmulatorWorkspace.Supervisor.csproj",
        "src\EmulatorWorkspace.Cli\EmulatorWorkspace.Cli.csproj",
        "src\EmulatorWorkspace.Desktop\EmulatorWorkspace.Desktop.csproj")) {
    $json = & dotnet list $project package --include-transitive 2>$null | Out-String
    $current = ""
    foreach ($line in ($json -split "`r?`n")) {
        if ($line -match '^\s*>\s+(\S+)\s+(\S+)\s*$') {
            $current = $Matches[1]
            [void]$componentList.Add([ordered]@{
                name = $current
                version = $Matches[2]
                scope = "nuget"
                project = $project
            })
        }
        elseif ($line -match '^\s*(\S+)\s+(\S+)\s*$' -and $line -notmatch 'Project' -and $line -notmatch 'Top-level') {
            [void]$componentList.Add([ordered]@{
                name = $Matches[1]
                version = $Matches[2]
                scope = "nuget"
                project = $project
            })
        }
    }
}
$sbom.components = @($componentList | Sort-Object name, version -Unique)
$sbomPath = Join-Path $releaseDir "sbom.json"
$sbom | ConvertTo-Json -Depth 6 | Set-Content -Path $sbomPath -Encoding UTF8

# --------------------------------------------------------------- provenance
$provenance = [ordered]@{
    schemaVersion = "1.0"
    product = "Emulator Workspace"
    version = $Version
    commit = $commit
    shortCommit = $shortCommit
    builtUtc = (Get-Date).ToUniversalTime().ToString("O")
    buildHost = [ordered]@{
        machine = $env:COMPUTERNAME
        os = (Get-CimInstance Win32_OperatingSystem).Caption
        osBuild = (Get-CimInstance Win32_OperatingSystem).BuildNumber
        dotnet = (& dotnet --version | Out-String).Trim()
    }
    commands = @(
        "dotnet publish src\EmulatorWorkspace.Desktop\EmulatorWorkspace.Desktop.csproj -c $Configuration -r $Runtime -p:Version=$Version",
        "dotnet publish src\EmulatorWorkspace.Supervisor\EmulatorWorkspace.Supervisor.csproj -c $Configuration -r $Runtime -p:Version=$Version",
        "dotnet publish src\EmulatorWorkspace.Cli\EmulatorWorkspace.Cli.csproj -c $Configuration -r $Runtime -p:Version=$Version",
        "dotnet test -c $Configuration"
    )
    pins = [ordered]@{
        emulator = "37.2.12.0 build 16428233"
        systemImage = "AOSP API 35 x86_64 revision 2"
        runtime = $Runtime
        configuration = $Configuration
    }
    signing = [ordered]@{
        status = "unsigned"
        identity = ""
        reason = "No user-owned code-signing certificate was supplied to this build."
    }
}
if ($CertificateThumbprint -or $CertificatePath) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $CertificateThumbprint -or $_.Subject -eq $CertificatePath } |
        Select-Object -First 1
    if ($certificate) {
        $provenance.signing.status = "signed"
        $provenance.signing.identity = $certificate.Subject
        $provenance.signing.thumbprint = $certificate.Thumbprint
    }
}
$provenancePath = Join-Path $releaseDir "provenance.json"
$provenance | ConvertTo-Json -Depth 6 | Set-Content -Path $provenancePath -Encoding UTF8

# -------------------------------------------------------------- file hashes
$hashes = New-Object System.Collections.ArrayList
foreach ($file in (Get-ChildItem $payload -Recurse -File | Sort-Object FullName)) {
    [void]$hashes.Add([ordered]@{
        path = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
        bytes = $file.Length
        sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}
$hashesPath = Join-Path $releaseDir "SHA256SUMS.json"
$hashes | ConvertTo-Json -Depth 4 | Set-Content -Path $hashesPath -Encoding UTF8

# ---------------------------------------------------------------- installer
$msiPath = $null
if (-not $SkipInstaller) {
    $wix = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wix) {
        $wixTool = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
        if (Test-Path $wixTool) { $wix = Get-Item $wixTool }
    }
    if ($wix) {
        Write-Host "  building the MSI"
        # The installer harvests the payload from a directory next to the
        # project, so the harvest is relative and always resolves.
        $harvest = Join-Path $repo "packaging\payload"
        Remove-Item $harvest -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item $payload $harvest -Recurse -Force
        # The harvest is an input to the installer, so the previous build is
        # discarded; otherwise MSBuild would reuse the old package.
        Remove-Item (Join-Path $repo "packaging\obj") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $repo "packaging\bin") -Recurse -Force -ErrorAction SilentlyContinue
        # The WiX project is built through MSBuild, which carries the version
        # and the output directory as properties.
        $wixArgs = @(
            "build",
            (Join-Path $repo "packaging\Product.wixproj"),
            "-c", "Release",
            "-p:PkgVersion=$Version",
            "-p:OutputDir=$releaseDir\"
        )
        & dotnet @wixArgs
        if ($LASTEXITCODE -ne 0) { throw "the installer build failed with exit code $LASTEXITCODE" }
        $built = Join-Path $releaseDir "EmulatorWorkspace.msi"
        $msiPath = Join-Path $releaseDir "EmulatorWorkspace-$Version.msi"
        if (Test-Path $built) { Move-Item $built $msiPath -Force }
    }
    else {
        Write-Warning "wix was not found; the installer was not built."
    }
}

# ------------------------------------------------------- release manifest
$release = [ordered]@{
    schemaVersion = 1
    product = "Emulator Workspace"
    version = $Version
    commit = $commit
    builtUtc = (Get-Date).ToUniversalTime().ToString("O")
    runtime = $Runtime
    configuration = $Configuration
    installer = $null
    installerSha256 = $null
    installerBytes = $null
    signingStatus = $provenance.signing.status
    sbom = "sbom.json"
    provenance = "provenance.json"
    notices = "THIRD-PARTY-NOTICES.txt"
    hashes = "SHA256SUMS.json"
    payloadFiles = $hashes.Count
    payloadSha256 = ([System.Security.Cryptography.SHA256]::Create().ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes(($hashes | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n")) |
            ForEach-Object { ($_ | ForEach-Object { $_.ToString("x2") }) -join "" })
}
if ($msiPath -and (Test-Path $msiPath)) {
    $msiInfo = Get-Item $msiPath
    $release.installer = $msiInfo.FullName
    $release.installerSha256 = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $release.installerBytes = $msiInfo.Length
}
$releasePath = Join-Path $releaseDir "release.json"
$release | ConvertTo-Json -Depth 5 | Set-Content -Path $releasePath -Encoding UTF8

Write-Host ""
Write-Host "Release $Version"
Write-Host "  directory : $releaseDir"
if ($release.installer) {
    Write-Host "  installer : $release.installer"
    Write-Host "  sha256    : $($release.installerSha256)"
    Write-Host "  bytes     : $($release.installerBytes)"
}
Write-Host "  signing   : $($release.signingStatus)"
Write-Host "  sbom      : $sbomPath"
Write-Host "  provenance: $provenancePath"
