#requires -Version 5.1
<#
.SYNOPSIS
    Phase 2 acceptance: paired MCP and CLI workflows, authorization failures,
    observation, artifacts, snapshot restore and scoped cleanup.

.DESCRIPTION
    Prerequisites:
      - a supervisor root containing a stopped template instance (default "tpl")
      - the paired sample APK at <root>\apps\ws-sample.apk
      - a session token file produced by: wsctl agent-pair --template <tpl>
      - the host HTTP fixture on port 8765 for the login step (see docs)
    The script starts the disposable guest, installs/launches the sample, runs
    the CLI workflow, runs the TypeScript MCP client, executes negative checks,
    then cleans up with a single-use approval. Artifacts go to
    artifacts/phase2/<run-id>/.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\phase2-acceptance.ps1 `
      -Root D:\Phase2Root -TokenFile D:\Phase2Root\cli-session.json
#>
param(
    [string]$Root = "D:\Phase2Root",
    [string]$TokenFile = "D:\Phase2Root\cli-session.json",
    [string]$Template = "tpl",
    [string]$OutDir = "artifacts\phase2",
    [switch]$SkipMcp,
    [switch]$KeepRunning
)

$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
$wsctl = Join-Path $repo "src\EmulatorWorkspace.Cli\bin\Debug\net10.0\wsctl.exe"
$mcpExe = Join-Path $repo "src\EmulatorWorkspace.Mcp\bin\Debug\net10.0\EmulatorWorkspace.Mcp.exe"
$mcpClient = Join-Path $repo "clients\mcp-client\workflow.mjs"
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$adb = Join-Path $sdk "platform-tools\adb.exe"

$run = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (Join-Path $OutDir $run)
New-Item -ItemType Directory -Force $out | Out-Null
$log = Join-Path $out "transcript.txt"
$results = @()

function Log([string]$m) { $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $m; Write-Host $line; Add-Content $log $line -Encoding UTF8 }
function Record([string]$n, [bool]$ok, [string]$d) { $script:results += [pscustomobject]@{Check=$n;Passed=$ok;Detail=$d}; Log ("{0} {1}: {2}" -f ($(if($ok){"PASS"}else{"FAIL"})), $n, $d) }
function Ws([string[]]$a, [switch]$AllowFail) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { $o = & $wsctl @a 2>&1; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $prev }
    if ($code -ne 0 -and -not $AllowFail) { throw "wsctl $($a -join ' ') failed ($code): $o" }
    return [pscustomobject]@{ Code=$code; Out=($o -join "`n") }
}
function Call([string]$op, [string]$json, [string]$extra = "") {
    $argsJson = Join-Path $out ("args-" + [guid]::NewGuid().ToString("N")[..6] + ".json")
    Set-Content $argsJson $json -Encoding UTF8
    $a = @("call","--op",$op,"--args-file",$argsJson,"--token-file",$TokenFile,"--root",$Root)
    if ($extra) { $a += $extra.Split(' ') }
    return Ws $a
}

$session = Get-Content $TokenFile -Raw | ConvertFrom-Json
$instance = $session.instance
Log "Phase 2 acceptance $run instance=$instance template=$Template"

$build = & dotnet build -v q --nologo 2>&1 | Out-String
Record "build" ($LASTEXITCODE -eq 0) "dotnet build"
$test = & dotnet test --no-build -v q --nologo 2>&1 | Out-String
Record "unit-tests" ($LASTEXITCODE -eq 0) ([regex]::Match($test,'Passed:\s+\d+').Value)

# Start the disposable guest
Ws @("start","--name",$instance,"--token-file",$TokenFile,"--root",$Root) | Out-Null
$wait = Ws @("wait","--name",$instance,"--timeout","600","--token-file",$TokenFile,"--root",$Root) -AllowFail
Record "boot" ($wait.Code -eq 0) $wait.Out
$serial = ([regex]::Match($wait.Out,"serial\s+:\s+(emulator-\d+)")).Groups[1].Value

Ws @("install","--name",$instance,"--apk","sample","--token-file",$TokenFile,"--root",$Root) | Out-Null
Ws @("launch","--name",$instance,"--package","dev.emulatorworkspace.sample","--token-file",$TokenFile,"--root",$Root) | Out-Null
Start-Sleep -Seconds 3

# CLI workflow
$shot = Call "observation.screenshot" "{}"
$frameId = (($shot.Out -replace '\u0022','"') | ConvertFrom-Json).frameId
$tree = Call "observation.tree" "{}"
$user = Call "input.text" '{"text":"cli-user","selector":{"contentDescription":"login_user"}}'
$pass = Call "input.text" '{"text":"cli-pass","selector":{"contentDescription":"login_password"}}'
Call "input.tap" '{"selector":{"contentDescription":"login_button"}}' | Out-Null
Start-Sleep -Seconds 2
$assert = Call "ui.assert" '{"selector":{"contentDescription":"login_status","text":"WELCOME"}}'
Record "cli.login" ((($assert.Out -replace '\u0022','"') | ConvertFrom-Json).pass -eq $true) $assert.Out
$logs = Call "logs.read" '{"lines":200,"contains":"LOGIN_RESULT"}'
Record "cli.logs" ((($logs.Out -replace '\u0022','"') | ConvertFrom-Json).lines -gt 0) "lines"
$save = Call "snapshot.save" '{"tag":"phase2-cli-baseline"}'
Record "snapshot.save" ($save.Code -eq 0) $save.Out
$approvalJson = Ws @("agent-approve-action","--session",$session.session,"--operation","snapshot.load","--root",$Root)
$code = (($approvalJson.Out -replace '\u0022','"') | ConvertFrom-Json).code
$restore = Call "snapshot.load" ("{`"tag`":`"phase2-cli-baseline`",`"approval`":`"$code`"}")
Record "snapshot.restore" ($restore.Code -eq 0) $restore.Out

# MCP workflow
if (-not $SkipMcp) {
    $env:WS_ROOT = $Root
    $env:WS_TOKEN_FILE = $TokenFile
    $env:WS_OUT = Join-Path $out "mcp"
    $env:WS_MCP_EXE = $mcpExe
    $env:WS_WSCTL = $wsctl
    Push-Location (Join-Path $repo "clients\mcp-client")
    try {
        $mcp = & node workflow.mjs 2>&1 | Out-String
        $mcpCode = $LASTEXITCODE
    } finally { Pop-Location }
    Record "mcp.workflow" ($mcpCode -eq 0 -and (Test-Path (Join-Path $out "mcp\mcp-transcript.json"))) $mcp.Trim()
}

# Authorization failures
$unpaired = Ws @("call","--op","observation.screenshot","--root",$Root) -AllowFail
Record "deny.unpaired" ($unpaired.Out -match "unauthorized") "unauthorized"
$wrongArgs = Join-Path $out "wrong-instance.json"; Set-Content $wrongArgs ('{"name":"' + $Template + '"}')
$wrong = Ws @("call","--op","instance.status","--args-file",$wrongArgs,"--token-file",$TokenFile,"--root",$Root) -AllowFail
Record "deny.wrong-instance" ($wrong.Out -match "wrong_instance") $wrong.Out
$staleJson = Join-Path $out "stale.json"; Set-Content $staleJson ("{`"frameId`":`"$frameId`",`"x`":540,`"y`":720}")
Call "input.tap" '{"selector":{"contentDescription":"login_button"}}' | Out-Null
$stale = Ws @("call","--op","input.tap","--args-file",$staleJson,"--token-file",$TokenFile,"--root",$Root) -AllowFail
Record "deny.stale-frame" ($stale.Out -match "stale_observation") $stale.Out
$amb = Call "ui.assert" '{"selector":{"text":""}}'
Record "deny.ambiguous" ($amb.Out -match "ambiguous_selector") $amb.Out
$replay1 = Call "snapshot.save" '{"tag":"idem-a"}' "--idempotency phase2-key"
$replay2 = Call "snapshot.save" '{"tag":"idem-b"}' "--idempotency phase2-key"
Record "idempotency.conflict" ($replay2.Out -match "idempotency_conflict") $replay2.Out

# Cleanup with approval, then the revoked credential must fail.
$cleanupApproval = Ws @("agent-approve-action","--session",$session.session,"--operation","device.cleanup","--root",$Root)
$cleanupCode = (($cleanupApproval.Out -replace '\u0022','"') | ConvertFrom-Json).code
if (-not $KeepRunning) {
    $cleanup = Call "device.cleanup" ("{`"approval`":`"$cleanupCode`"}")
    Record "cleanup" ($cleanup.Code -eq 0) $cleanup.Out
    $revoked = Ws @("call","--op","observation.screenshot","--token-file",$TokenFile,"--root",$Root) -AllowFail
    Record "deny.revoked" ($revoked.Out -match "revoked") $revoked.Out
}

$failed = @($results | Where-Object { -not $_.Passed }).Count
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $out "summary.json") -Encoding UTF8
$results | Format-Table -AutoSize | Out-String | Write-Host
Log "completed failures=$failed artifacts=$out"
if ($failed -gt 0) { exit 2 }
exit 0
