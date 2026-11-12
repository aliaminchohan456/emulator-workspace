#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$RootPrefix,
    [Parameter(Mandatory)][string]$CandidateRelease,
    [Parameter(Mandatory)][string]$CandidatePayload,
    [string]$McpExecutable = "",
    [ValidateSet("phase3", "recipe-launcher", "recipe-fixture", "rootlab", "import", "devtools", "wpf", "play36", "play35")]
    [string[]]$Lane = @("phase3", "recipe-launcher", "recipe-fixture", "rootlab", "import", "devtools", "wpf", "play36", "play35")
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "fixtures/candidate-payload.ps1")
$verified = Resolve-VerifiedCandidate $CandidateRelease $CandidatePayload
$mcp = if ($McpExecutable) {
    (Resolve-Path -LiteralPath $McpExecutable).ProviderPath
}
else {
    Join-Path $verified.payload "EmulatorWorkspace.Mcp.exe"
}
if (-not (Test-Path -LiteralPath $mcp -PathType Leaf)) {
    throw "This candidate lacks an MCP executable. Supply a separately built matching MCP using -McpExecutable."
}
$run = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = "artifacts/phase3/candidate/$run"
$out = Join-Path $repo $outDir
New-Item -ItemType Directory -Force $out | Out-Null
$context = Join-Path $out "candidate.json"
$identity = $verified.identity
$identity.evidenceScope = if ($McpExecutable) { "verified runtime payload and a separately built MCP executable" } else { "entire runtime payload" }
$identity.mcpExecutableSha256 = (Get-FileHash -LiteralPath $mcp).Hash
$identity.mcpExecutableVersion = (Get-Item -LiteralPath $mcp).VersionInfo.ProductVersion
if ($McpExecutable -and ($identity.mcpExecutableVersion -split '\+', 2)[-1] -ne $identity.sourceCommit) {
    throw "The MCP override was not built from candidate source $($identity.sourceCommit)."
}
$identity | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $context -Encoding UTF8
$env:WS_ACCEPTANCE_PAYLOAD = $verified.payload
$env:WS_ACCEPTANCE_CONTEXT = $context
$env:WS_ACCEPTANCE_MCP_EXE = $mcp
$env:EMULATORWORKSPACE_SUPERVISOR_EXE = Join-Path $verified.payload "EmulatorWorkspace.Supervisor.exe"
$fixture = Join-Path $verified.payload "ws-sample.apk"
$cli = Join-Path $verified.payload "wsctl.exe"
$sourceCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
$checks = [System.Collections.Generic.List[object]]::new()

foreach ($name in $Lane) {
    $root = "$RootPrefix-$run-$name"
    if (Test-Path -LiteralPath $root) { throw "The disposable regression root already exists: $root" }
    switch ($name) {
        "phase3"          { $script = "phase3-acceptance.ps1"; $extra = @("-SkipBuild") }
        "recipe-launcher" { $script = "recipe-acceptance.ps1"; $extra = @("-SkipBuild", "-Baseline", "LauncherVisible") }
        "recipe-fixture"  { $script = "recipe-acceptance.ps1"; $extra = @("-SkipBuild", "-Baseline", "FixtureForeground") }
        "rootlab"         { $script = "root-lab-acceptance.ps1"; $extra = @() }
        "import"          { $script = "avd-import-acceptance.ps1"; $extra = @("-ExternalHome", "$root-external", "-AvdName", "external-$run") }
        "devtools"        { $script = "devtools-acceptance.ps1"; $extra = @() }
        "wpf"             { $script = "wpf-clickthrough-acceptance.ps1"; $extra = @("-Apk", $fixture) }
        "play36"          { $script = "play-image-acceptance.ps1"; $extra = @("-Image", "system-images;android-36;google_apis_playstore;x86_64") }
        "play35"          { $script = "play-image-acceptance.ps1"; $extra = @("-Image", "system-images;android-35;google_apis_playstore_ps16k;x86_64") }
    }
    Write-Host "Candidate regression $name at $root"
    $log = Join-Path $out "$name.log"
    $laneOut = Join-Path $outDir $name
    try {
        $ErrorActionPreference = "Continue"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) -Root $root -OutDir $laneOut @extra *>&1 |
            Tee-Object -FilePath $log
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = "Stop" }
    $laneRoot = Join-Path $repo $laneOut
    $summaries = @(Get-ChildItem -LiteralPath $laneRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "summary.json" } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    $laneChecks = @()
    if ($summaries.Count -eq 1) {
        $laneSummary = Get-Content -LiteralPath $summaries[0] -Raw | ConvertFrom-Json -ErrorAction Continue
        $laneChecks = @($laneSummary.checks | Where-Object { $null -ne $_ })
    }
    $failed = @($laneChecks | Where-Object { -not $_.Passed }).Count
    $passed = $code -eq 0 -and $laneChecks.Count -gt 0 -and $failed -eq 0
    $checks.Add([pscustomobject]@{ name = $name; passed = $passed; detail = "exit=$code, checks=$($laneChecks.Count), failures=$failed, log=$name.log" })
    if (Test-Path -LiteralPath (Join-Path $root "supervisor.lock")) {
        & $cli supervisor-stop --root $root | Out-Null
    }
}

[ordered]@{
    runId = $run
    finishedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    sourceCommit = $sourceCommit
    harnessSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    candidate = $identity
    checks = $checks.ToArray()
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out "summary.json") -Encoding UTF8
if ($checks | Where-Object { -not $_.passed }) { exit 2 }
