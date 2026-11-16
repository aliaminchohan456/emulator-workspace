using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-05: the subscription state machine.
///
/// The frame and log streams used to be three independent fields (an id, a
/// "subscribe pending" flag and the log stream's id) that every caller wrote
/// directly. These tests are the races that used to need a live guest: a grant
/// that lands after the device changed, a close while a subscribe is in
/// flight, a lost supervisor, and two requests for the same stream.
/// </summary>
public class StreamSubscriptionTests
{
    [Fact]
    public void ARequest_StartsSubscribingForThatGuest()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            StreamSubscription.None,
            new SubEvent.Requested("dev"));

        Assert.Equal(new StreamSubscription(SubPhase.Subscribing, "dev", null), next);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void AGrantForTheRequestedGuest_GoesLive()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            Subscribing("dev"),
            new SubEvent.Granted("sub-1", "dev"));

        Assert.Equal(new StreamSubscription(SubPhase.Live, "dev", "sub-1"), next);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void ALateGrantAfterADeviceChange_IsUnsubscribed()
    {
        // The operator switched guests while the subscribe was in flight. The
        // grant names the old guest, so the id must not become this window's
        // stream - it is handed back instead.
        var switched = Next(Subscribing("a"), new SubEvent.DeviceChanged("b"));

        var (next, unsubscribe) = StreamSubscription.Next(switched, new SubEvent.Granted("sub-a", "a"));

        Assert.Equal(new StreamSubscription(SubPhase.None, "b", null), next);
        Assert.Equal("sub-a", unsubscribe);
    }

    [Fact]
    public void ACloseWhileSubscribing_IsIgnored_AndTheGrantStillLands()
    {
        // A close that arrives while the request is out names an id this window
        // does not hold; cancelling the in-flight subscribe on it would leave
        // the stream closed with a live id in the supervisor.
        var (next, unsubscribe) = StreamSubscription.Next(
            Subscribing("dev"),
            new SubEvent.Closed("sub-0"));

        Assert.Equal(SubPhase.Subscribing, next.Phase);
        Assert.Null(unsubscribe);

        var (live, late) = StreamSubscription.Next(next, new SubEvent.Granted("sub-1", "dev"));
        Assert.Equal(SubPhase.Live, live.Phase);
        Assert.Equal("sub-1", live.Id);
        Assert.Null(late);
    }

    [Fact]
    public void PipeLost_DropsTheIdWithoutAnUnsubscribe()
    {
        // The subscription lives in the hub of the supervisor that just went
        // away: there is no pipe left to ask, and the next poll re-attaches.
        var (next, unsubscribe) = StreamSubscription.Next(
            Live("dev", "sub-1"),
            new SubEvent.PipeLost());

        Assert.Equal(new StreamSubscription(SubPhase.None, "dev", null), next);
        Assert.Null(unsubscribe);

        Assert.True(StreamSubscription.ShouldSubscribe(next, lifecycle: true, device: "dev"));
    }

    [Fact]
    public void AGrantAfterPipeLost_IsUnsubscribed()
    {
        // The reply to a subscribe was already on the wire when the pipe died.
        var dropped = Next(Subscribing("dev"), new SubEvent.PipeLost());

        var (next, unsubscribe) = StreamSubscription.Next(dropped, new SubEvent.Granted("sub-1", "dev"));

        Assert.Equal(SubPhase.None, next.Phase);
        Assert.Null(next.Id);
        Assert.Equal("sub-1", unsubscribe);
    }

    [Fact]
    public void ADoubleRequest_IsIgnored()
    {
        // Two callers both thought nothing was attached. Only the first request
        // may open a stream.
        var subscribing = Subscribing("dev");

        var (next, unsubscribe) = StreamSubscription.Next(subscribing, new SubEvent.Requested("dev"));

        Assert.Same(subscribing, next);
        Assert.Null(unsubscribe);

        var (live, again) = StreamSubscription.Next(next, new SubEvent.Granted("sub-1", "dev"));
        var (afterLiveRequest, duplicate) = StreamSubscription.Next(live, new SubEvent.Requested("dev"));
        Assert.Equal(SubPhase.Live, afterLiveRequest.Phase);
        Assert.Null(again);
        Assert.Null(duplicate);
    }

    [Fact]
    public void ARequestForAnotherGuest_IsIgnoredWhileSubscribing()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            Subscribing("a"),
            new SubEvent.Requested("b"));

        Assert.Equal(SubPhase.Subscribing, next.Phase);
        Assert.Equal("a", next.Device);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void AStaleClose_IsIgnored()
    {
        // The id of a subscription that was already replaced.
        var (next, unsubscribe) = StreamSubscription.Next(
            Live("dev", "sub-1"),
            new SubEvent.Closed("sub-0"));

        Assert.Equal(SubPhase.Live, next.Phase);
        Assert.Equal("sub-1", next.Id);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void ACloseOfTheHeldId_EndsTheStream()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            Live("dev", "sub-1"),
            new SubEvent.Closed("sub-1"));

        Assert.Equal(new StreamSubscription(SubPhase.None, "dev", null), next);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void ADeviceChange_UnsubscribesTheLiveId()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            Live("a", "sub-a"),
            new SubEvent.DeviceChanged("b"));

        Assert.Equal(new StreamSubscription(SubPhase.None, "b", null), next);
        Assert.Equal("sub-a", unsubscribe);
    }

    [Fact]
    public void ADeviceChangeWhileSubscribing_LeavesNoIdToUnsubscribe()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            Subscribing("a"),
            new SubEvent.DeviceChanged("b"));

        Assert.Equal(new StreamSubscription(SubPhase.None, "b", null), next);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void DeviceChangedToTheSameGuest_IsANoOp()
    {
        // A refresh rebuilds the device list and re-selects the same guest; the
        // live stream must not be churned by that.
        var live = Live("dev", "sub-1");

        var (next, unsubscribe) = StreamSubscription.Next(live, new SubEvent.DeviceChanged("dev"));

        Assert.Same(live, next);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void SelectingNoGuest_DropsTheStream()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            Live("dev", "sub-1"),
            new SubEvent.DeviceChanged(null));

        Assert.Equal(new StreamSubscription(SubPhase.None, null, null), next);
        Assert.Equal("sub-1", unsubscribe);
    }

    [Fact]
    public void ASecondGrant_IsUnsubscribed()
    {
        // A double subscribe that raced this one: the live id stays, the extra
        // id is handed back.
        var (next, unsubscribe) = StreamSubscription.Next(
            Live("dev", "sub-1"),
            new SubEvent.Granted("sub-2", "dev"));

        Assert.Equal(new StreamSubscription(SubPhase.Live, "dev", "sub-1"), next);
        Assert.Equal("sub-2", unsubscribe);
    }

    [Fact]
    public void AGrantWithoutARequest_IsUnsubscribed()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            StreamSubscription.None,
            new SubEvent.Granted("sub-1", "dev"));

        Assert.Equal(SubPhase.None, next.Phase);
        Assert.Equal("sub-1", unsubscribe);
    }

    [Fact]
    public void AnEmptyGrant_ChangesNothing()
    {
        var subscribing = Subscribing("dev");

        var (next, unsubscribe) = StreamSubscription.Next(subscribing, new SubEvent.Granted("", "dev"));

        Assert.Same(subscribing, next);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void Refused_ReturnsToNoneWithNothingToUnsubscribe()
    {
        var (next, unsubscribe) = StreamSubscription.Next(
            Subscribing("dev"),
            new SubEvent.Refused());

        Assert.Equal(new StreamSubscription(SubPhase.None, "dev", null), next);
        Assert.Null(unsubscribe);

        Assert.True(StreamSubscription.ShouldSubscribe(next, lifecycle: true, device: "dev"));
    }

    [Fact]
    public void RefusedWhileLive_ChangesNothing()
    {
        var live = Live("dev", "sub-1");

        var (next, unsubscribe) = StreamSubscription.Next(live, new SubEvent.Refused());

        Assert.Same(live, next);
        Assert.Null(unsubscribe);
    }

    [Fact]
    public void ShouldSubscribe_IsTrueOnlyWhenNothingIsHeld()
    {
        Assert.True(StreamSubscription.ShouldSubscribe(StreamSubscription.None, true, "dev"));
        Assert.False(StreamSubscription.ShouldSubscribe(Subscribing("dev"), true, "dev"));
        Assert.False(StreamSubscription.ShouldSubscribe(Live("dev", "sub-1"), true, "dev"));
    }

    [Fact]
    public void ShouldSubscribe_NeedsTheGuestToAllowIt()
    {
        Assert.False(StreamSubscription.ShouldSubscribe(StreamSubscription.None, lifecycle: false, device: "dev"));
    }

    [Fact]
    public void ShouldSubscribe_NeedsAGuest_AndNotAnotherOne()
    {
        Assert.False(StreamSubscription.ShouldSubscribe(StreamSubscription.None, true, device: null));
        Assert.False(StreamSubscription.ShouldSubscribe(StreamSubscription.None, true, device: ""));

        // The state still names the guest it was pointed at: another guest is
        // not subscribed on that reading.
        Assert.False(StreamSubscription.ShouldSubscribe(
            new StreamSubscription(SubPhase.None, "a", null), true, "b"));
    }

    [Fact]
    public void IsSameGuest_ComparesTheNameIncludingNone()
    {
        Assert.True(StreamSubscription.IsSameGuest(Live("dev", "sub-1"), "dev"));
        Assert.False(StreamSubscription.IsSameGuest(Live("dev", "sub-1"), "other"));
        Assert.True(StreamSubscription.IsSameGuest(StreamSubscription.None, null));
        Assert.False(StreamSubscription.IsSameGuest(StreamSubscription.None, "dev"));
    }

    private static StreamSubscription Subscribing(string device) => Next(StreamSubscription.None, new SubEvent.Requested(device));

    private static StreamSubscription Live(string device, string id) =>
        Next(Subscribing(device), new SubEvent.Granted(id, device));

    /// <summary>Applies one event and asserts the machine asked for no cleanup.</summary>
    private static StreamSubscription Next(StreamSubscription state, SubEvent e)
    {
        var (next, unsubscribe) = StreamSubscription.Next(state, e);
        Assert.Null(unsubscribe);
        return next;
    }
}
