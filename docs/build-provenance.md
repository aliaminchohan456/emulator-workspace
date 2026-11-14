# Recording source and build identity

Use an isolated checkout for builds whose binaries will be used as evidence.
Record the commit, full tree, SDK and empty `git status --porcelain` output
before building. Build and test there, then confirm that status is still empty
and the tree has not changed. Hash the actual payloads used by the hardware
harness, including the managed DLLs, not just executable apphosts.

For example, from a fresh detached worktree:

```powershell
git rev-parse HEAD
git rev-parse 'HEAD^{tree}'
git status --porcelain
dotnet --version
dotnet build -c Release
dotnet test tests/EmulatorWorkspace.Tests/EmulatorWorkspace.Tests.csproj -c Release --no-build
git status --porcelain
git rev-parse 'HEAD^{tree}'
Get-FileHash src/EmulatorWorkspace.Supervisor/bin/Release/net10.0-windows/EmulatorWorkspace*.dll
```

Abort on nonempty status, command failure, or a changed tree. Save the output,
timestamps and payload hashes outside tracked source. Retain the exact
checkout and binaries if a later hardware run will use them. A subsequent
documentation-only evidence commit may reference this earlier build commit;
do not claim it was itself the build input.

The follow-up verification uses source commit `81e98d2` and complete tree
`0792e7c24d60a04b95ba5e4d08c466b76c32c825`. Its recorded payload hashes apply
only to that new build. No new WHPX or WPF run is attached to it. The older
cleanup-verification run retains its explicitly documented partial source
attribution.
