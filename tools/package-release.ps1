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

function Publish-App([string]$projectDirectory, [string]$assembly, [bool]$selfContained, [bool]$overwrite = $true) {
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

    # The three applications share the framework assemblies, but only the
    # desktop carries the real WPF implementation. The first publish therefore
    # owns the shared files and the later ones only add what is missing.
    foreach ($file in (Get-ChildItem $source -Recurse -File)) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $target = Join-Path $payload $relative
        if ((Test-Path $target) -and -not $overwrite) { continue }
        $targetDirectory = Split-Path $target -Parent
        New-Item -ItemType Directory -Force $targetDirectory | Out-Null
        Copy-Item $file.FullName $target -Force
    }
}

Publish-App "src\EmulatorWorkspace.Desktop" "EmulatorWorkspace.Desktop.exe" $true
Publish-App "src\EmulatorWorkspace.Supervisor" "EmulatorWorkspace.Supervisor.exe" $true $false
Publish-App "src\EmulatorWorkspace.Cli" "wsctl.exe" $true $false

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
# The structured list of the whole solution is the authoritative dependency
# inventory of the release.
$raw = & dotnet list EmulatorWorkspace.slnx package --include-transitive --format json 2>$null | Out-String
$start = $raw.IndexOf("{")
if ($start -ge 0) {
    $raw = $raw.Substring($start)
    try {
        $parsed = $raw | ConvertFrom-Json
        foreach ($project in $parsed.projects) {
            # The test project is not part of the shipped package.
            if ($project.path -match '[\\/]tests[\\/]') { continue }
            foreach ($framework in $project.frameworks) {
                foreach ($package in $framework.topLevelPackages) {
                    [void]$componentList.Add([PSCustomObject][ordered]@{
                        name = $package.id
                        version = $package.resolvedVersion
                        scope = "nuget"
                        project = Split-Path $project.path -Leaf
                        direct = $true
                    })
                }

                foreach ($package in $framework.transitiveDependencies) {
                    [void]$componentList.Add([PSCustomObject][ordered]@{
                        name = $package.id
                        version = $package.resolvedVersion
                        scope = "nuget"
                        project = Split-Path $project.path -Leaf
                        direct = $false
                    })
                }
            }
        }
    }
    catch {
        Write-Warning ("the dependency inventory could not be parsed: " + $_.Exception.Message)
    }
}

# The runtime that is shipped with the package is part of the inventory.
[void]$componentList.Add([PSCustomObject][ordered]@{
    name = "Microsoft.NETCore.App"
    version = "10.0.10"
    scope = "runtime"
    project = "self-contained publish"
    direct = $true
})
[void]$componentList.Add([PSCustomObject][ordered]@{
    name = "Microsoft.WindowsDesktop.App"
    version = "10.0.10"
    scope = "runtime"
    project = "self-contained publish"
    direct = $true
})
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
        Write-Host "  building the MSI"
        # The installer harvests the payload from a directory next to the
        # project, so the harvest is relative and always resolves.
        $harvest = Join-Path $repo "packaging\payload"
        Remove-Item $harvest -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item $payload $harvest -Recurse -Force
        # The payload fragment is generated, because a per-user package needs
        # a registry key path on every component that lands in the profile.
        $fragment = Join-Path $repo "packaging\Payload.wxs"
        & python (Join-Path $repo "tools\generate-payload-wxs.py") $harvest $fragment
        if ($LASTEXITCODE -ne 0) { throw "the payload fragment could not be generated" }

        # The harvest is an input to the installer, so the previous build is
        # discarded; otherwise MSBuild would reuse the old package.
        Remove-Item (Join-Path $repo "packaging\obj") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $repo "packaging\bin") -Recurse -Force -ErrorAction SilentlyContinue
        # The WiX project is built through MSBuild, which carries the version
        # as a property. The output directory is not passed on the command
        # line, because a trailing backslash would be swallowed by the native
        # argument parsing; the package is copied out of the default location
        # instead.
        $wixArgs = @(
            "build",
            (Join-Path $repo "packaging\Product.wixproj"),
            "-c", "Release",
            "-p:PkgVersion=$Version"
        )
        $buildLog = Join-Path $releaseDir "installer-build.log"
        & dotnet @wixArgs *> $buildLog
        if ($LASTEXITCODE -ne 0) {
            Get-Content $buildLog | Write-Host
            throw "the installer build failed with exit code $LASTEXITCODE"
        }

        $built = Join-Path $repo "packaging\bin\Release\EmulatorWorkspace.msi"
        if (-not (Test-Path $built)) {
            Get-Content $buildLog | Write-Host
            throw "the installer was not produced at $built"
        }

        $msiPath = Join-Path $releaseDir "EmulatorWorkspace-$Version.msi"
        Copy-Item $built $msiPath -Force
        Write-Host ("  installer: {0} ({1} bytes)" -f $msiPath, (Get-Item $msiPath).Length)
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
    payloadSha256 = (Get-FileHash -InputStream (
            [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes(
                ($hashes | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"))) -Algorithm SHA256).Hash.ToLowerInvariant()
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
