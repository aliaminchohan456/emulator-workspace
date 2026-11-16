namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>UX-18: which stream a closed subscription belongs to.</summary>
public enum SubscriptionKind
{
    /// <summary>The closed id belongs to a stream this window does not hold.</summary>
    None,

    /// <summary>The frame stream.</summary>
    Frames,

    /// <summary>The log stream.</summary>
    Logcat,
}

/// <summary>
/// UX-18: the rule behind a closed subscription.
///
/// The supervisor closes a subscription when the guest's engine goes away or
/// when a session is reset. The window used to keep the closed id in its
/// field: the status poll read "already subscribed" and never opened a new
/// one, so the viewport stayed dead for the rest of the session even after
/// the guest came back. Clearing the field is what lets the poll re-attach.
///
/// The rule is small enough to live outside WPF and be tested without a
/// guest.
/// </summary>
public static class SubscriptionClose
{
    /// <summary>
    /// Which stream the closed subscription belonged to.
    ///
    /// A closed id the window does not hold is <see cref="SubscriptionKind.None"/>:
    /// it may be a subscription that was already replaced, and clearing the
    /// current one in that case would open a second stream.
    /// </summary>
    public static SubscriptionKind Which(string? closedId, string? frameId, string? logcatId)
    {
        if (string.IsNullOrEmpty(closedId))
        {
            return SubscriptionKind.None;
        }

        if (frameId is not null && string.Equals(closedId, frameId, StringComparison.Ordinal))
        {
            return SubscriptionKind.Frames;
        }

        if (logcatId is not null && string.Equals(closedId, logcatId, StringComparison.Ordinal))
        {
            return SubscriptionKind.Logcat;
        }

        return SubscriptionKind.None;
    }
}
