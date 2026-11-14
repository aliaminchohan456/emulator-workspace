using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>Real child processes with simulated ADB responses; no emulator or physical device.</summary>
public sealed class ProvisioningAdbRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ws-fake-adb-" + Guid.NewGuid().ToString("N"));
    private readonly AdbClient _adb;
    private readonly List<JsonObject> _proofs = [];
    private CancellationToken _cleanupToken;

    public ProvisioningAdbRootTests()
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(AppContext.BaseDirectory, "fake-adb", "EmulatorWorkspace.FakeAdb" +
            (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(executable), executable);
        _adb = new AdbClient(executable);
    }

    private void Journal(JsonObject proof)
    {
        MagiskLabArtifacts.WriteAtomic(Path.Combine(_root, "journal.json"), proof);
        _proofs.Add((JsonObject)proof.DeepClone());
    }

    private async Task<JsonObject> SetRoot(bool root, CancellationToken ct)
    {
        if (root)
        {
            // Intent must already be durable when the fake root process starts.
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "journal.json")))!;
            Assert.True(saved["rootRequested"]!.GetValue<bool>());
        }
        else
        {
            _cleanupToken = ct;
            Assert.False(ct.IsCancellationRequested);
        }
        var result = await _adb.RunAsync("emulator-5554", [root ? "root" : "unroot"],
            TimeSpan.FromSeconds(root && File.ReadAllText(Path.Combine(_root, "mode")) == "timeout-root" ? 2 : 10),
            ct, _root);
        result.EnsureSuccess("fake " + (root ? "root" : "unroot"));
        return new JsonObject { ["uidAfter"] = result.StandardOutput.Trim() };
    }

    private async Task<JsonObject> Provision(CancellationToken ct)
    {
        var result = await _adb.RunAsync("emulator-5554", ["provision"], TimeSpan.FromSeconds(10), ct, _root);
        result.EnsureSuccess("fake provisioning");
        return new JsonObject { ["provisioned"] = true };
    }

    private async Task WaitFor(string command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(_root, command + ".started")))
            await Task.Delay(20, deadline.Token);
    }

    [Theory]
    [InlineData("cancel-root", "root")]
    [InlineData("cancel-provision", "provision")]
    public async Task CancellationStillUnrootsWithIndependentToken(string mode, string command)
    {
        File.WriteAllText(Path.Combine(_root, "mode"), mode);
        using var caller = new CancellationTokenSource();
        var task = ProvisioningAdbRoot.Run(() => { }, SetRoot, Provision, Journal, caller.Token);
        await WaitFor(command);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.NotEqual(caller.Token, _cleanupToken);
        Assert.Equal("unroot", File.ReadAllLines(Path.Combine(_root, "calls"))[^1]);
        Assert.True(_proofs[^1]["adbdUid2000Verified"]!.GetValue<bool>());
        Assert.NotNull(_proofs[^1]["provisioningError"]);
    }

    [Fact]
    public async Task RootProcessTimeoutStillUnroots()
    {
        File.WriteAllText(Path.Combine(_root, "mode"), "timeout-root");
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProvisioningAdbRoot.Run(() => { }, SetRoot, Provision, Journal, default));
        Assert.Equal(new[] { "root", "unroot" }, File.ReadAllLines(Path.Combine(_root, "calls")));
        Assert.True(_proofs[^1]["adbdUid2000Verified"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DoubleFailurePreservesExceptionsAndDurableJournal()
    {
        File.WriteAllText(Path.Combine(_root, "mode"), "double-failure");
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            ProvisioningAdbRoot.Run(() => { }, SetRoot, Provision, Journal, default));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains("fake provisioning failed", error.InnerExceptions[0].Message);
        Assert.Contains("fake unroot failed", error.InnerExceptions[1].Message);
        var proof = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "journal.json")))!;
        Assert.Contains("fake provisioning failed", proof["provisioningError"]!.GetValue<string>());
        Assert.Contains("fake unroot failed", proof["unrootError"]!.GetValue<string>());
        Assert.True(proof["unrootAttempted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EnrollmentFailureNeverStartsAdb()
    {
        await Assert.ThrowsAsync<SupervisorOperationException>(() => ProvisioningAdbRoot.Run(
            () => throw new SupervisorOperationException(ErrorCodes.ApprovalRequired, "not enrolled"),
            SetRoot, Provision, Journal, default));
        Assert.False(File.Exists(Path.Combine(_root, "calls")));
        Assert.Empty(_proofs);
    }

    [Fact]
    public async Task IntentJournalFailureNeverStartsAdb()
    {
        await Assert.ThrowsAsync<IOException>(() => ProvisioningAdbRoot.Run(() => { }, SetRoot, Provision,
            _ => throw new IOException("journal unavailable"), default));
        Assert.False(File.Exists(Path.Combine(_root, "calls")));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
