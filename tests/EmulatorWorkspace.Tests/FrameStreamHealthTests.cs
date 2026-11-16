using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-18: the frame stream's state.
///
/// The emulator's image stream is change driven: a guest whose screen does not
/// change sends no frames at all. The viewport used to read "no new frame for
/// five seconds" as a stalled stream, warn about it and drop the operator's
/// taps - on a perfectly healthy, idle guest.
/// </summary>
public class FrameStreamHealthTests
{
    private const bool Running = true;
    private const bool HasFrame = true;
    private const bool Responsive = true;
    private const bool Broken = false;

    [Fact]
    public void FramesArriving_IsLive()
    {
        Assert.Equal(
            FrameStreamState.Live,
            FrameStreamHealth.Evaluate(Running, HasFrame, 1, Broken, Responsive, held: false));
    }

    [Fact]
    public void AGuestThatDidNotChange_IsIdleAndKeepsItsInput()
    {
        // The emulator sent no frame because nothing changed. The picture on
        // screen is current, so this is not a fault.
        var state = FrameStreamHealth.Evaluate(Running, HasFrame, 40, Broken, Responsive, held: false);

        Assert.Equal(FrameStreamState.Idle, state);
        Assert.False(FrameStreamHealth.IsStalled(state));
        Assert.False(FrameStreamHealth.PausesInput(state));
        Assert.Equal("", FrameStreamHealth.Tone(state));
        Assert.Equal("", FrameStreamHealth.Title(state, "the app launch", 40));
    }

    [Fact]
    public void ACaptureThatIsNotRunning_IsStalledAndPausesTheInput()
    {
        var state = FrameStreamHealth.Evaluate(Running, HasFrame, 40, streamBroken: true, Responsive, held: false);

        Assert.Equal(FrameStreamState.Stalled, state);
        Assert.True(FrameStreamHealth.IsStalled(state));
        Assert.True(FrameStreamHealth.PausesInput(state));
        Assert.Equal("warning", FrameStreamHealth.Tone(state));
        Assert.Contains("Frame stalled", FrameStreamHealth.Title(state, "", 40), StringComparison.Ordinal);
    }

    [Fact]
    public void AGuestThatStoppedAnswering_IsStalled()
    {
        var state = FrameStreamHealth.Evaluate(Running, HasFrame, 40, Broken, guestResponsive: false, held: false);

        Assert.Equal(FrameStreamState.Stalled, state);
        Assert.True(FrameStreamHealth.PausesInput(state));
    }

    [Fact]
    public void AHeldChannel_IsHeldAndPausesTheInput()
    {
        var state = FrameStreamHealth.Evaluate(Running, HasFrame, 40, Broken, Responsive, held: true);

        Assert.Equal(FrameStreamState.Held, state);
        Assert.False(FrameStreamHealth.IsStalled(state));
        Assert.True(FrameStreamHealth.PausesInput(state));
        Assert.Equal("info", FrameStreamHealth.Tone(state));
        Assert.Contains("the app launch", FrameStreamHealth.Title(state, "the app launch", 40), StringComparison.Ordinal);
    }

    [Fact]
    public void AGuestThatIsNotRunning_IsLive()
    {
        Assert.Equal(
            FrameStreamState.Live,
            FrameStreamHealth.Evaluate(guestRunning: false, HasFrame, 40, Broken, Responsive, held: false));
    }

    [Fact]
    public void AStreamWithoutAFrame_IsLive()
    {
        // The stage says "waiting for the first frame"; the stream is not
        // stalled, there is simply nothing yet.
        Assert.Equal(
            FrameStreamState.Live,
            FrameStreamHealth.Evaluate(Running, hasFrame: false, 40, Broken, Responsive, held: false));
    }

    [Fact]
    public void TheBudgetOnlyMattersOnceTheStreamIsUnhealthy()
    {
        Assert.Equal(
            FrameStreamState.Live,
            FrameStreamHealth.Evaluate(Running, HasFrame, FrameStreamHealth.StaleSeconds, Broken, Responsive, held: false));

        Assert.Equal(
            FrameStreamState.Idle,
            FrameStreamHealth.Evaluate(Running, HasFrame, FrameStreamHealth.StaleSeconds + 0.1, Broken, Responsive, held: false));
    }
}
