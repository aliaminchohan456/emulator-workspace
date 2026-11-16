using System.Globalization;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-09: the display rules of the Agent Sessions window, kept out of the XAML so
/// they can be tested without WPF. Nothing here decides what the supervisor
/// allows: the words, tone words, countdowns and scope chips only present the
/// values the supervisor already returned (agent.session.list). A tone word is
/// always paired with a printed label, never colour alone, and an unknown value
/// falls back to a neutral chip instead of inventing a state.
/// </summary>
public static class AgentSessionsPresentation
{
    /// <summary>A session that expires within this window reads "Expiring".</summary>
    public static readonly TimeSpan ExpiringWindow = TimeSpan.FromMinutes(5);

    /// <summary>A pairing request that expires within this window turns warn.</summary>
    public static readonly TimeSpan PairingWarningWindow = TimeSpan.FromSeconds(60);

    /// <summary>Session lifecycle ("active" / "expired" / "revoked") to the chip tone.</summary>
    public static string SessionTone(string? state) => Normalize(state) switch
    {
        "active" => "ok",
        "expired" => "warn",
        "revoked" => "err",
        _ => "neutral",
    };

    /// <summary>The same word the supervisor returned, capitalised for a chip.</summary>
    public static string SessionLabel(string? state)
    {
        var value = (state ?? "").Trim();
        return value.Length == 0 ? "Unknown" : Capitalize(value);
    }

    /// <summary>
    /// The chip a session really earns right now: an active grant inside the last
    /// five minutes is "Expiring" (warn), a grant whose expiry has passed is
    /// "Expired" (warn) even if the list has not been refreshed, and only a
    /// revoked grant is an error.
    /// </summary>
    public static (string Label, string Tone) SessionChip(
        string? state, DateTimeOffset? expiresUtc, DateTimeOffset now)
    {
        var normalized = Normalize(state);
        if (normalized == "revoked")
        {
            return ("Revoked", "err");
        }

        if (normalized == "expired")
        {
            return ("Expired", "warn");
        }

        if (normalized == "active")
        {
            if (expiresUtc is { } expiry)
            {
                if (expiry <= now)
                {
                    return ("Expired", "warn");
                }

                if (expiry - now <= ExpiringWindow)
                {
                    return ("Expiring", "warn");
                }
            }

            return ("Active", "ok");
        }

        return (SessionLabel(state), "neutral");
    }

    /// <summary>Pairing request status ("pending" / "approved") to the chip tone.</summary>
    public static string PairingTone(string? status) => Normalize(status) switch
    {
        "approved" or "completed" => "ok",
        "pending" => "warn",
        "expired" or "denied" or "rejected" => "err",
        _ => "neutral",
    };

    public static string PairingLabel(string? status)
    {
        var value = (status ?? "").Trim();
        return value.Length == 0 ? "Unknown" : Capitalize(value);
    }

    /// <summary>
    /// A pending request whose two-minute window has closed reads "Expired" even
    /// before the next refresh; nothing else is re-worded.
    /// </summary>
    public static (string Label, string Tone) PairingChip(
        string? status, DateTimeOffset? expiresUtc, DateTimeOffset now)
    {
        if (Normalize(status) == "pending" && expiresUtc is { } expiry && expiry <= now)
        {
            return ("Expired", "warn");
        }

        return (PairingLabel(status), PairingTone(status));
    }

    /// <summary>
    /// A live countdown for an expiry ("in 4m 52s", "in 1h 46m", "in 3d 2h",
    /// "expired"). The unit is chosen from the real remaining time, so the value
    /// never claims more precision than it has and never goes negative.
    /// </summary>
    public static string Countdown(DateTimeOffset? expiresUtc, DateTimeOffset now)
    {
        if (expiresUtc is not { } expiry)
        {
            return "—";
        }

        var remaining = expiry - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "expired";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"in {(int)remaining.TotalSeconds}s");
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"in {(int)remaining.TotalMinutes}m {remaining.Seconds:00}s");
        }

        if (remaining < TimeSpan.FromDays(1))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"in {(int)remaining.TotalHours}h {remaining.Minutes:00}m");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"in {(int)remaining.TotalDays}d {remaining.Hours}h");
    }

    /// <summary>
    /// An operation record's own outcome word ("completed", "denied", "started",
    /// "unknown_outcome" ...) to the chip tone. "started" is not a success and not
    /// a failure: it is an operation the store has not finished recording, so it
    /// keeps the warn tone.
    /// </summary>
    public static string OutcomeTone(string? outcome) => Normalize(outcome) switch
    {
        "completed" or "ok" or "allowed" => "ok",
        "denied" or "failed" or "error" => "err",
        "started" or "in_progress" or "unknown_outcome" or "unresolved" => "warn",
        _ => "neutral",
    };

    /// <summary>
    /// The chip's word. "completed" prints the mockup's "OK"; the other store
    /// values keep a short, readable form rather than the raw enum.
    /// </summary>
    public static string OutcomeLabel(string? outcome) => Normalize(outcome) switch
    {
        "" => "—",
        "completed" or "ok" or "allowed" => "OK",
        "denied" => "Denied",
        "failed" or "error" => "Failed",
        "started" or "in_progress" => "Running",
        "unknown_outcome" => "Unknown",
        "unresolved" => "Unresolved",
        var other => Capitalize(other),
    };

    /// <summary>
    /// The outcome chip's whole text: the word, and the supervisor's own result
    /// code in the operator's words ("Denied · Scope not allowed"). The raw code
    /// stays in the row's tooltip; an unmapped code is humanised rather than
    /// printed as snake_case.
    /// </summary>
    public static string OutcomeChipText(string? outcome, string? code)
    {
        var label = OutcomeLabel(outcome);
        var reason = CodeLabel(code);
        return reason.Length == 0 ? label : $"{label} · {reason}";
    }

    /// <summary>
    /// A supervisor result code as plain words. The store's codes are stable
    /// enums, not prose: "forbidden_scope" reads "Scope not allowed",
    /// "invalid_request" reads "Invalid request", and anything unmapped is
    /// humanised (underscores to spaces, first letter capitalised) so a new code
    /// never shows as raw snake_case in the UI.
    /// </summary>
    public static string CodeLabel(string? code) => Normalize(code) switch
    {
        "" or "ok" => "",
        "forbidden_scope" or "scope_not_allowed" or "scope_denied" => "Scope not allowed",
        "invalid_request" => "Invalid request",
        "invalid_argument" => "Invalid argument",
        "not_found" => "Not found",
        "not_authorized" or "unauthorized" or "forbidden" => "Not authorized",
        "approval_required" => "Approval required",
        "expired" => "Expired",
        "revoked" => "Revoked",
        "conflict" => "Conflict",
        "busy" => "Busy",
        "timeout" or "timed_out" => "Timed out",
        "unavailable" => "Unavailable",
        "unsupported" => "Not supported",
        "cancelled" or "canceled" => "Cancelled",
        var other => Humanize(other),
    };

    /// <summary>"snake_case_word" -> "Snake case word" for a code with no mapping.</summary>
    private static string Humanize(string value)
    {
        var words = value.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return Capitalize(value);
        }

        return Capitalize(string.Join(' ', words));
    }

    /// <summary>
    /// "opencode-cli/s1-81f2" -> "opencode-cli". The store records the caller as
    /// client plus session; the table already has a Session column, so the Caller
    /// column prints the client only and the full value stays in the tooltip.
    /// </summary>
    public static string CallerLabel(string? caller)
    {
        var value = (caller ?? "").Trim();
        if (value.Length == 0)
        {
            return "—";
        }

        var slash = value.IndexOf('/');
        return slash > 0 ? value[..slash] : value;
    }

    /// <summary>
    /// The comma-joined scope grant split into the chips the row prints. Empty
    /// pieces are dropped, duplicates collapse (ordinal-ignore-case, first spelling
    /// wins) and an empty grant stays empty so the row can say "no scopes".
    /// </summary>
    public static IReadOnlyList<string> Scopes(string? grant)
    {
        if (string.IsNullOrWhiteSpace(grant))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chips = new List<string>();
        foreach (var piece in grant.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(piece))
            {
                chips.Add(piece);
            }
        }

        return chips;
    }

    /// <summary>
    /// The chips a row shows and how many it had to leave out. The row has one
    /// line of height, so at most <paramref name="max"/> chips are printed and the
    /// rest collapse into a "+N" chip whose tooltip lists every grant.
    /// </summary>
    public static (IReadOnlyList<string> Visible, int Hidden) VisibleScopes(string? grant, int max = 3)
    {
        var chips = Scopes(grant);
        if (max < 1 || chips.Count <= max)
        {
            return (chips, 0);
        }

        return (chips.Take(max).ToArray(), chips.Count - max);
    }

    /// <summary>
    /// A guest's record state as the operator reads it.
    ///
    /// UX-18: the word comes from the one lifecycle, so the agent table and the
    /// device surfaces cannot disagree about the same guest.
    /// </summary>
    public static string InstanceLabel(string? state) => DeviceLifecycle.StateWord(state);

    /// <summary>
    /// UX-15: a request still waiting for an answer. The supervisor calls it
    /// "pending"; a pending request whose window has closed is answered by time
    /// and belongs with the past ones, exactly like the chip that already reads
    /// "Expired" without a refresh.
    /// </summary>
    public static bool IsPendingPairing(string? status, DateTimeOffset? expiresUtc, DateTimeOffset now) =>
        Normalize(status) == "pending" && (expiresUtc is null || expiresUtc > now);

    /// <summary>
    /// The other half of the same rule: expired, approved, completed, denied or
    /// any state the supervisor has moved on from. Unknown words count as past
    /// because nothing can be approved on them.
    /// </summary>
    public static bool IsPastPairing(string? status, DateTimeOffset? expiresUtc, DateTimeOffset now) =>
        !IsPendingPairing(status, expiresUtc, now);

    /// <summary>
    /// UX-15 item 1: the pairing list prints the waiting requests first and the
    /// answered ones afterwards, each group in the order the supervisor returned
    /// (newest first). The split is by the same clock the chips use, so the list
    /// and the "Expired" label never disagree.
    /// </summary>
    public static (IReadOnlyList<T> Live, IReadOnlyList<T> Past) PartitionPairings<T>(
        IEnumerable<T> rows,
        Func<T, string?> status,
        Func<T, DateTimeOffset?> expiresUtc,
        DateTimeOffset now)
    {
        var live = new List<T>();
        var past = new List<T>();
        foreach (var row in rows)
        {
            if (IsPendingPairing(status(row), expiresUtc(row), now))
            {
                live.Add(row);
            }
            else
            {
                past.Add(row);
            }
        }

        return (live, past);
    }

    /// <summary>
    /// EV-02: the one-line name UI Automation reads for a pairing-request row.
    /// A ListBoxItem without a name falls back to its item's type name, so a
    /// screen reader used to hear "AgentSessionsPage+PairingRow". The format is
    /// built here, from the same words the row prints, so the name carries the
    /// chip label and the live countdown instead of a raw enum.
    /// </summary>
    public static string PairingRowName(string? client, string? statusLabel, string? countdown) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}, pairing {1}, expires {2}",
            NamePart(client, "Unknown client"),
            NamePart(statusLabel, "Unknown"),
            NamePart(countdown, "unknown"));

    /// <summary>EV-02: the same one-line name for a paired-session row.</summary>
    public static string SessionRowName(string? client, string? stateLabel, string? guest) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}, session {1}, guest {2}",
            NamePart(client, "Unknown client"),
            NamePart(stateLabel, "Unknown"),
            NamePart(guest, "unknown guest"));

    /// <summary>EV-02: the same one-line name for a recent-agent-operation row.</summary>
    public static string OperationRowName(string? operation, string? outcomeLabel) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}, {1}",
            NamePart(operation, "unknown operation"),
            NamePart(outcomeLabel, "Unknown"));

    /// <summary>
    /// A value as it appears inside a row name: trimmed, and a missing value
    /// reads as the given words. The em dash the table prints for "no value" is
    /// spoken badly, so it counts as missing here.
    /// </summary>
    private static string NamePart(string? value, string fallback)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 || trimmed == "—" ? fallback : trimmed;
    }

    /// <summary>"3 sessions · 1 pairing" for the window's status line.</summary>
    public static string Summary(int sessions, int pairings) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} · {2} {3}",
            sessions,
            Plural(sessions, "session"),
            pairings,
            Plural(pairings, "pairing"));

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static string Capitalize(string value) =>
        char.ToUpperInvariant(value[0]) + value[1..];
}

/// <summary>
/// UX-15 item 0: the lifetime rule of the badge poll, kept out of the window so
/// it can be tested. The poll may run only while the supervisor is connected and
/// the main window is not minimised; losing the connection stops it until a new
/// connection is reported, and minimising pauses it without forgetting that the
/// supervisor is there. It never polls on its own account: the page owns the
/// timer and asks this object whether the timer should be running.
/// </summary>
public sealed class PendingPairingPoll
{
    /// <summary>One light read every ten seconds, as the design sheet asks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private bool _connected;
    private bool _minimised;

    /// <summary>True when the timer is allowed to run and its next tick may read.</summary>
    public bool ShouldPoll => _connected && !_minimised;

    /// <summary>The reason the poll is in its current state, for the desktop log.</summary>
    public string State => !_connected
        ? "stopped: disconnected"
        : _minimised ? "paused: window minimised" : "running";

    /// <summary>The supervisor connection arrived; returns whether the timer should now run.</summary>
    public bool OnConnected()
    {
        _connected = true;
        return ShouldPoll;
    }

    /// <summary>The pipe or the shell's connection is gone; the poll stops until a reconnect.</summary>
    public bool OnDisconnected()
    {
        _connected = false;
        return ShouldPoll;
    }

    /// <summary>The window was minimised; nothing is visible, so nothing is read.</summary>
    public bool OnMinimised()
    {
        _minimised = true;
        return ShouldPoll;
    }

    /// <summary>The window came back; the poll resumes only if the supervisor is connected.</summary>
    public bool OnRestored()
    {
        _minimised = false;
        return ShouldPoll;
    }
}
