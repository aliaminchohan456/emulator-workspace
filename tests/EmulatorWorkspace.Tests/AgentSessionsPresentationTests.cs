using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-09: the Agent Sessions table only prints values the supervisor returned, so
/// the chip words, their tones and the scope chips are pure rules and are pinned
/// here. The closed tone set matters for the view: the XAML has a trigger for
/// exactly these four words, and an unknown tone would silently fall back to the
/// neutral chip.
/// </summary>
public class AgentSessionsPresentationTests
{
    private static readonly string[] Tones = { "ok", "warn", "err", "neutral" };

    [Theory]
    [InlineData("active", "ok")]
    [InlineData("ACTIVE", "ok")]
    [InlineData("expired", "warn")]
    [InlineData("revoked", "err")]
    [InlineData("", "neutral")]
    [InlineData("something-new", "neutral")]
    public void SessionTone_MapsTheStoreWords(string state, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.SessionTone(state));

    [Theory]
    [InlineData("active", "Active")]
    [InlineData(" revoked ", "Revoked")]
    [InlineData("", "Unknown")]
    public void SessionLabel_CapitalisesTheReturnedWord(string state, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.SessionLabel(state));

    [Theory]
    [InlineData("Defined", "Stopped")]
    [InlineData("stopped", "Stopped")]
    [InlineData("Running", "Running")]
    [InlineData("", "Unknown")]
    public void InstanceLabel_PrintsStoppedForTheInternalDefinedState(string state, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.InstanceLabel(state));

    [Theory]
    [InlineData("pending", "warn")]
    [InlineData("approved", "ok")]
    [InlineData("completed", "ok")]
    [InlineData("denied", "err")]
    [InlineData("expired", "err")]
    [InlineData("", "neutral")]
    public void PairingTone_MapsApprovalStates(string status, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.PairingTone(status));

    [Theory]
    [InlineData("completed", "ok")]
    [InlineData("denied", "err")]
    [InlineData("failed", "err")]
    [InlineData("started", "warn")]
    [InlineData("unknown_outcome", "warn")]
    [InlineData("", "neutral")]
    public void OutcomeTone_KeepsStartedOperationsOutOfTheSuccessTone(string outcome, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.OutcomeTone(outcome));

    [Theory]
    [InlineData("completed", "OK")]
    [InlineData("denied", "Denied")]
    [InlineData("failed", "Failed")]
    [InlineData("started", "Running")]
    [InlineData("unknown_outcome", "Unknown")]
    [InlineData("queued", "Queued")]
    [InlineData("", "—")]
    public void OutcomeLabel_PrintsAWordForEveryRecordedValue(string outcome, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.OutcomeLabel(outcome));

    [Fact]
    public void ToneRules_NeverLeaveTheViewsClosedSet()
    {
        var samples = new[] { "active", "expired", "revoked", "pending", "approved", "completed", "denied", "started", "", "other" };
        foreach (var sample in samples)
        {
            Assert.Contains(AgentSessionsPresentation.SessionTone(sample), Tones);
            Assert.Contains(AgentSessionsPresentation.PairingTone(sample), Tones);
            Assert.Contains(AgentSessionsPresentation.OutcomeTone(sample), Tones);
        }
    }

    [Theory]
    [InlineData("opencode-cli/s1-81f2", "opencode-cli")]
    [InlineData("cli", "cli")]
    [InlineData("", "—")]
    public void CallerLabel_KeepsTheClientAndDropsTheSession(string caller, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.CallerLabel(caller));

    [Fact]
    public void Scopes_SplitTrimAndDecollapseWithoutReordering()
    {
        Assert.Equal(new[] { "observe", "input", "logcat.read" },
            AgentSessionsPresentation.Scopes("observe, input ,logcat.read"));
        Assert.Equal(new[] { "observe", "input" },
            AgentSessionsPresentation.Scopes("observe,OBSERVE,input"));
        Assert.Empty(AgentSessionsPresentation.Scopes(""));
        Assert.Empty(AgentSessionsPresentation.Scopes("  , ,  "));
        Assert.Empty(AgentSessionsPresentation.Scopes(null));
    }

    [Fact]
    public void Scopes_ReturnsACopyThatCannotMutateTheRow()
    {
        var first = AgentSessionsPresentation.Scopes("observe");
        var second = AgentSessionsPresentation.Scopes("observe");
        Assert.NotSame(first, second);
        Assert.Single(first);
    }

    [Theory]
    [InlineData(3, 1, "3 sessions · 1 pairing")]
    [InlineData(1, 1, "1 session · 1 pairing")]
    [InlineData(0, 0, "0 sessions · 0 pairings")]
    public void Summary_AgreesInNumber(int sessions, int pairings, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.Summary(sessions, pairings));

    [Fact]
    public void SessionChip_ExpiresOnItsOwnClock()
    {
        var now = new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal(("Active", "ok"), AgentSessionsPresentation.SessionChip("active", now.AddHours(1), now));
        Assert.Equal(("Expiring", "warn"), AgentSessionsPresentation.SessionChip("active", now.AddMinutes(4), now));
        Assert.Equal(("Expired", "warn"), AgentSessionsPresentation.SessionChip("active", now.AddSeconds(-1), now));
        Assert.Equal(("Expired", "warn"), AgentSessionsPresentation.SessionChip("expired", now.AddHours(1), now));
        Assert.Equal(("Revoked", "err"), AgentSessionsPresentation.SessionChip("revoked", null, now));
        Assert.Equal(("Active", "ok"), AgentSessionsPresentation.SessionChip("active", null, now));
    }

    [Fact]
    public void PairingChip_TurnsExpiredWithoutARefresh()
    {
        var now = new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal(("Pending", "warn"), AgentSessionsPresentation.PairingChip("pending", now.AddMinutes(1), now));
        Assert.Equal(("Expired", "warn"), AgentSessionsPresentation.PairingChip("pending", now.AddSeconds(-1), now));
        Assert.Equal(("Approved", "ok"), AgentSessionsPresentation.PairingChip("approved", now.AddMinutes(-5), now));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-5, "expired")]
    [InlineData(45, "in 45s")]
    [InlineData(292, "in 4m 52s")]
    [InlineData(6360, "in 1h 46m")]
    [InlineData(270000, "in 3d 3h")]
    public void Countdown_ReadsTheRealRemainder(int? seconds, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero);
        var expiry = seconds is { } value ? now.AddSeconds(value) : (DateTimeOffset?)null;
        Assert.Equal(expected, AgentSessionsPresentation.Countdown(expiry, now));
    }

    [Fact]
    public void VisibleScopes_CollapseIntoANumber()
    {
        var (visible, hidden) = AgentSessionsPresentation.VisibleScopes("observe,input,app,lifecycle,network");
        Assert.Equal(new[] { "observe", "input", "app" }, visible);
        Assert.Equal(2, hidden);

        var (all, none) = AgentSessionsPresentation.VisibleScopes("observe,input");
        Assert.Equal(2, all.Count);
        Assert.Equal(0, none);

        var (empty, emptyHidden) = AgentSessionsPresentation.VisibleScopes("");
        Assert.Empty(empty);
        Assert.Equal(0, emptyHidden);
    }

    [Theory]
    [InlineData("denied", "forbidden_scope", "Denied · forbidden_scope")]
    [InlineData("failed", "invalid_request", "Failed · invalid_request")]
    [InlineData("completed", "ok", "OK")]
    [InlineData("completed", "", "OK")]
    [InlineData("started", null, "Running")]
    public void OutcomeChipText_PrintsTheWordAndTheReason(string outcome, string? code, string expected) =>
        Assert.Equal(expected, AgentSessionsPresentation.OutcomeChipText(outcome, code));
}
