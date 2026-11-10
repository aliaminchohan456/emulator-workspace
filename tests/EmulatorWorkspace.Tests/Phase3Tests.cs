using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class AdbIdentityTests
{
    private const string Listing = """
        List of devices attached
        emulator-5554 device product:sdk_phone64_x86_64 model:Android_SDK_built_for_x86_64 device:emu64x transport_id:1
        emulator-5556 device product:sdk_phone64_x86_64 model:Android_SDK_built_for_x86_64 device:emu64x transport_id:2
        """;

    [Fact]
    public void Managed_serial_resolves_to_its_own_transport()
    {
        var transport = AdbIdentity.Find(Listing, "emulator-5556");

        Assert.NotNull(transport);
        Assert.Equal("emulator-5556", transport!.Serial);
        Assert.Equal("device", transport.State);
        Assert.Equal("2", transport.TransportId);
        Assert.Equal("emu64x", transport.Device);
    }

    [Fact]
    public void Missing_serial_is_not_matched_to_another_target()
    {
        Assert.Null(AdbIdentity.Find(Listing, "emulator-5599"));
    }

    [Fact]
    public void Offline_transport_is_reported_as_not_usable()
    {
        const string offline = """
            List of devices attached
            emulator-5556 offline transport_id:4
            """;

        var transport = AdbIdentity.Find(offline, "emulator-5556");

        Assert.NotNull(transport);
        Assert.Equal("offline", transport!.State);
    }

    [Fact]
    public void Physical_devices_are_not_managed_targets()
    {
        const string listing = """
            List of devices attached
            ABC1234567 device product:raven model:Pixel_6 device:raven transport_id:9
            """;

        Assert.Null(AdbIdentity.Find(listing, "emulator-5554"));
    }
}

public sealed class InputGateTests
{
    [Fact]
    public async Task Held_input_parks_until_released_and_records_the_queue_time()
    {
        var gate = new InputGate();
        gate.Hold("guest-a");

        var started = DateTimeOffset.UtcNow;
        var waiter = gate.WaitAsync("guest-a", "req-1", CancellationToken.None);
        await Task.Delay(50);

        Assert.True(gate.IsHeld("guest-a"));
        Assert.Equal(1, gate.WaitingCount("guest-a"));
        Assert.False(waiter.IsCompleted);
        var queued = gate.Waiters("guest-a").Single();
        Assert.Equal("req-1", queued.RequestId);
        Assert.True(queued.QueuedUtc >= started);

        var released = gate.Release("guest-a");

        Assert.Equal(1, released);
        var resolved = await waiter;
        Assert.Equal("req-1", resolved.RequestId);
        Assert.False(gate.IsHeld("guest-a"));
    }

    [Fact]
    public async Task Unheld_input_is_never_parked()
    {
        var gate = new InputGate();
        var waiter = await gate.WaitAsync("guest-b", "req-2", CancellationToken.None);

        Assert.Equal("req-2", waiter.RequestId);
        Assert.Equal(0, gate.WaitingCount("guest-b"));
    }

    [Fact]
    public async Task Cancelled_waiter_leaves_the_queue()
    {
        var gate = new InputGate();
        gate.Hold("guest-c");
        using var cancellation = new CancellationTokenSource();
        var pending = gate.WaitAsync("guest-c", "req-3", cancellation.Token);
        await Task.Delay(30);
        Assert.Equal(1, gate.WaitingCount("guest-c"));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, gate.WaitingCount("guest-c"));
    }
}
