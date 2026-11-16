using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-18: the closed subscription.
///
/// The supervisor closes a subscription when the guest's engine goes away or
/// a session is reset. The window used to keep the closed id in its field, so
/// the status poll read "already subscribed" and never opened a new one: the
/// viewport stayed dead for the rest of the session, even after the guest
/// came back.
/// </summary>
public class SubscriptionCloseTests
{
    [Fact]
    public void AClosedFrameSubscription_IsTheFrameStream()
    {
        Assert.Equal(
            SubscriptionKind.Frames,
            SubscriptionClose.Which("abc", frameId: "abc", logcatId: "def"));
    }

    [Fact]
    public void AClosedLogSubscription_IsTheLogStream()
    {
        Assert.Equal(
            SubscriptionKind.Logcat,
            SubscriptionClose.Which("def", frameId: "abc", logcatId: "def"));
    }

    [Fact]
    public void AnIdTheWindowDoesNotHold_IsIgnored()
    {
        // A subscription that was already replaced: closing it must not clear
        // the live one.
        Assert.Equal(
            SubscriptionKind.None,
            SubscriptionClose.Which("old", frameId: "abc", logcatId: "def"));
    }

    [Fact]
    public void NoId_IsIgnored()
    {
        Assert.Equal(
            SubscriptionKind.None,
            SubscriptionClose.Which(null, frameId: "abc", logcatId: "def"));

        Assert.Equal(
            SubscriptionKind.None,
            SubscriptionClose.Which("", frameId: "abc", logcatId: "def"));
    }

    [Fact]
    public void AnEmptyField_DoesNotMatch()
    {
        Assert.Equal(
            SubscriptionKind.None,
            SubscriptionClose.Which("abc", frameId: null, logcatId: "def"));
    }
}
