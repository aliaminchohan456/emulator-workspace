#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$CandidateRelease,
    [Parameter(Mandatory)][string]$StagingRoot,
    [string]$Sdk = $env:ANDROID_HOME,
    [string]$Java = $env:JAVA_HOME,
    [switch]$Launch
)

$ErrorActionPreference = "Stop"
$record = Get-Content -LiteralPath (Join-Path $CandidateRelease "release.json") -Raw | ConvertFrom-Json
$source = Join-Path $CandidateRelease (Split-Path -Leaf $record.installer)
if ((Get-FileHash -LiteralPath $source).Hash -ne $record.installerSha256) {
    throw "The selected MSI does not match the release record."
}
$Sdk = (Resolve-Path -LiteralPath $Sdk).ProviderPath
$Java = (Resolve-Path -LiteralPath $Java).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $Sdk "licenses") -PathType Container) -or
    -not (Test-Path -LiteralPath (Join-Path $Java "bin/java.exe") -PathType Leaf)) {
    throw "The isolated test needs an installed SDK with accepted licenses and a local JDK."
}
if (Test-Path -LiteralPath $StagingRoot) { throw "Use a new staging root." }

$inputFolder = New-Item -ItemType Directory -Path (Join-Path $StagingRoot "input") -Force
$outputFolder = New-Item -ItemType Directory -Path (Join-Path $StagingRoot "output") -Force
Copy-Item -LiteralPath $source -Destination (Join-Path $inputFolder.FullName "candidate.msi")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "installer-acceptance.ps1") -Destination $inputFolder.FullName
[ordered]@{ sourceCommit = $record.commit; installerSha256 = $record.installerSha256 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputFolder.FullName "candidate.json") -Encoding UTF8

@'
$ErrorActionPreference = "Stop"
$env:ANDROID_HOME = "C:\WSValidationSdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:JAVA_HOME = "C:\WSValidationJdk"
$env:Path = "$env:JAVA_HOME\bin;$env:Path"
$inputPath = "C:\WSValidationInput"
$resultPath = "C:\WSValidationOutput"
try {
    $emulator = Join-Path $env:ANDROID_HOME "emulator/emulator.exe"
    $ErrorActionPreference = "Continue"
    try {
        $acceleration = & $emulator -accel-check 2>&1 | Out-String
        $accelCode = $LASTEXITCODE
        $version = & $emulator -version 2>&1 | Out-String
    }
    finally { $ErrorActionPreference = "Stop" }
    $acceleration | Set-Content -LiteralPath (Join-Path $resultPath "acceleration.txt") -Encoding UTF8
    if ($accelCode -ne 0 -or $acceleration -notmatch 'WHPX.*is installed and usable') {
        throw "This isolated Windows instance did not expose usable WHPX."
    }
    $revision = Get-Content -LiteralPath (Join-Path $env:ANDROID_HOME "system-images/android-35/default/x86_64/source.properties")
    if ($version -notmatch '37\.2\.12\.0.*16428233' -or $revision -notcontains 'Pkg.Revision=2') {
        throw "The offline SDK differs from the pinned engine or image."
    }
    $candidate = Get-Content -LiteralPath (Join-Path $inputPath "candidate.json") -Raw | ConvertFrom-Json
    $msi = Join-Path $inputPath "candidate.msi"
    if ((Get-FileHash -LiteralPath $msi).Hash -ne $candidate.installerSha256) { throw "MSI hash mismatch in the sandbox." }
    $tools = New-Item -ItemType Directory -Path "C:\WSOffline\tools" -Force
    Copy-Item -LiteralPath (Join-Path $inputPath "installer-acceptance.ps1") -Destination $tools.FullName
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tools.FullName "installer-acceptance.ps1") `
        -Installer $msi -WorkRoot "C:\WSOfflineGuest" -OutDir artifacts/phase3/offline -VerifyDisconnectedReuse |
        Tee-Object -FilePath (Join-Path $resultPath "installer-transcript.txt")
    $exitCode = $LASTEXITCODE
    Get-ChildItem "C:\WSOffline\artifacts\phase3\offline" -Directory | ForEach-Object {
        $copied = Join-Path $resultPath $_.Name
        Copy-Item -LiteralPath $_.FullName -Destination $copied -Recurse
        Copy-Item -LiteralPath (Join-Path $inputPath "candidate.json") -Destination $copied
    }
    "installerExitCode=$exitCode" | Set-Content -LiteralPath (Join-Path $resultPath "result.txt") -Encoding UTF8
}
catch {
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path $resultPath "preflight-failure.txt") -Encoding UTF8
    exit 2
}
'@ | Set-Content -LiteralPath (Join-Path $inputFolder.FullName "run.ps1") -Encoding UTF8

$document = New-Object System.Xml.XmlDocument
$configuration = $document.AppendChild($document.CreateElement("Configuration"))
function Add-Setting($parent, [string]$name, [string]$value) {
    $element = $parent.AppendChild($document.CreateElement($name))
    $element.InnerText = $value
}
Add-Setting $configuration "Networking" "Disable"
Add-Setting $configuration "MemoryInMB" "8192"
$mappings = $configuration.AppendChild($document.CreateElement("MappedFolders"))
foreach ($entry in @(
    @{ host = $inputFolder.FullName; guest = "C:\WSValidationInput"; readOnly = "true" },
    @{ host = $Sdk; guest = "C:\WSValidationSdk"; readOnly = "true" },
    @{ host = $Java; guest = "C:\WSValidationJdk"; readOnly = "true" },
    @{ host = $outputFolder.FullName; guest = "C:\WSValidationOutput"; readOnly = "false" }
)) {
    $folder = $mappings.AppendChild($document.CreateElement("MappedFolder"))
    Add-Setting $folder "HostFolder" $entry.host
    Add-Setting $folder "SandboxFolder" $entry.guest
    Add-Setting $folder "ReadOnly" $entry.readOnly
}
$logon = $configuration.AppendChild($document.CreateElement("LogonCommand"))
Add-Setting $logon "Command" "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WSValidationInput\run.ps1"
$configPath = Join-Path (Resolve-Path -LiteralPath $StagingRoot).ProviderPath "offline-acceptance.wsb"
$document.Save($configPath)
Write-Host "Sandbox configuration: $configPath"
Write-Host "Isolated evidence: $($outputFolder.FullName)"
if ($Launch) { Start-Process -FilePath $configPath }
