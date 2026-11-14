# WSL development verification (2026-11-14)

Ubuntu 24.04.5 LTS on WSL2 is available under the normal user `aliamin`.
Kernel: `6.18.58.1-microsoft-standard-WSL2`. Portable .NET builds and tests
run in the Linux filesystem; WPF and WHPX acceptance still run on Windows.
No host features or reboot were changed during this verification.

The pinned Linux x64 SDK is .NET **10.0.404**, installed at
`~/.local/share/emulator-workspace/dotnet-10.0.404`. This satisfies the
repository's existing `global.json` latestFeature roll-forward policy.
The archive was downloaded from Microsoft's
[official SDK endpoint](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-linux-x64.tar.gz)
and checked against the SHA512 in the official release metadata:

```text
577d0eb17fec33d029104c5c63a1ab0accc3e28776274abef2218dbac5af18d2109b5406d26b31d76f5c245215e3ba0926a94c20246c672c41847c9e1eb1dc50
```

Run inside Ubuntu, from a source checkout in its Linux filesystem:

```sh
export DOTNET_ROOT="$HOME/.local/share/emulator-workspace/dotnet-10.0.404"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build -c Release
dotnet test tests/EmulatorWorkspace.Tests/EmulatorWorkspace.Tests.csproj -c Release --no-build
```

## Baseline and result

The local `git archive 7f08299` baseline built with 22 warnings and no errors,
but tests returned **388 passed, 1 failed, 2 skipped**. The failing
`StockDenialRequiresExplicitProbeFailure` fixture used a multiline replacement
that did not match its CRLF source on Linux. This is separate from Grok's
reported 389/2 result. The fixture now removes CR before its substitutions;
production evidence parsing is unchanged.

With the six new simulated-ADB process tests and fixture fix, Linux returned
**395 passed, 2 skipped, 0 failed (397 total)**. The skips are the Windows-only
lease sharing/write-exclusion tests. Windows returned **397 passed, 0 skipped**.
Both Release builds had 22 existing warnings and no errors.

Local logs: `artifacts/wsl-baseline-{build,tests}.log`,
`artifacts/wsl-cleanup-{build,tests}.log`, and
`artifacts/magisk-cleanup-{build,focused,tests}.log`.

The fake executable uses real subprocess cancellation and timeouts, but its
ADB responses are simulated. These tests cover root/provision cancellation,
root timeout, provisioning plus unroot double failure, and rejection before
root when enrollment or the initial durable journal fails. They do not model
physical power loss or replace real guest verification.

WSL is ready for portable tooling work. A full source-built AOSP image remains
an open gate: the virtual Linux filesystem's reported capacity is not physical
free space on the Windows volume. No AOSP source sync or build is claimed.
