function Resolve-VerifiedCandidate([string]$releaseDirectory, [string]$payloadDirectory) {
    $payload = (Resolve-Path -LiteralPath $payloadDirectory -ErrorAction Stop).ProviderPath
    $prefix = $payload.TrimEnd('\', '/') + '\'
    $release = Get-Content -LiteralPath (Join-Path $releaseDirectory "release.json") -Raw -ErrorAction Stop | ConvertFrom-Json
    $msi = Join-Path $releaseDirectory (Split-Path -Leaf $release.installer)
    if ((Get-FileHash -LiteralPath $msi -ErrorAction Stop).Hash -ne $release.installerSha256) {
        throw "The candidate MSI does not match its release record."
    }
    $inventory = Get-Content -LiteralPath (Join-Path $releaseDirectory "SHA256SUMS.json") -Raw -ErrorAction Stop | ConvertFrom-Json
    if ($inventory.Count -ne $release.payloadFiles) { throw "Candidate inventory count mismatch." }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $inventory) {
        $file = [IO.Path]::GetFullPath((Join-Path $payload $entry.path))
        if (-not $file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($file)) {
            throw "Invalid candidate inventory entry: $($entry.path)"
        }
        if ((Get-FileHash -LiteralPath $file -ErrorAction Stop).Hash -ne $entry.sha256) {
            throw "Candidate payload hash mismatch: $($entry.path)"
        }
    }
    $actual = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force)
    if ($actual.Count -ne $seen.Count -or ($actual | Where-Object { -not $seen.Contains($_.FullName) })) {
        throw "The candidate payload contains files outside the verified inventory."
    }
    [pscustomobject]@{
        payload = $payload
        identity = [ordered]@{
            sourceCommit = $release.commit
            installerSha256 = $release.installerSha256
            payloadFilesVerified = $seen.Count
            signingStatus = $release.signingStatus
        }
    }
}
