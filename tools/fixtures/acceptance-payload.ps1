function Get-AcceptanceBinary([string]$fileName, [string]$debugPath) {
    if ($env:WS_ACCEPTANCE_PAYLOAD) {
        return (Join-Path $env:WS_ACCEPTANCE_PAYLOAD $fileName)
    }
    return (Join-Path $repo $debugPath)
}

function Write-AcceptanceContext([string]$directory) {
    if ($env:WS_ACCEPTANCE_CONTEXT) {
        Copy-Item -LiteralPath $env:WS_ACCEPTANCE_CONTEXT -Destination (Join-Path $directory "candidate.json") -ErrorAction Stop
    }
}

function Start-AcceptanceLoginFixture([string]$log, [int]$bytes) {
    if (Get-NetTCPConnection -LocalPort 8765 -State Listen -ErrorAction SilentlyContinue) {
        throw "The login fixture port 8765 is already in use."
    }
    $python = (& py -3 -c 'import sys; print(sys.executable)' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $python -PathType Leaf)) {
        throw "Could not resolve the Python 3 executable for the fixture."
    }
    $env:FIXTURE_BODY_BYTES = "$bytes"
    $env:FIXTURE_LOG = $log
    $env:FIXTURE_HOST = "127.0.0.1"
    $env:FIXTURE_PORT = "8765"
    $script = Join-Path $repo "tools/fixtures/login_fixture.py"
    Start-Process -FilePath $python -ArgumentList ('"{0}"' -f $script) -PassThru -WindowStyle Hidden
}
