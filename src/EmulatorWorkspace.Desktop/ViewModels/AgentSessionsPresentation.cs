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
    /// EV-08: why the approval primary cannot run for the request a row prints,
    /// or null when it can. The button is offered only for a request that is
    /// still waiting, so an expired window and an already answered request read
    /// as two different sentences - a request that merely ran out of time is not
    /// the same thing as one an operator approved.
    /// </summary>
    public static string? ApproveDisabledReason(string? status, DateTimeOffset? expiresUtc, DateTimeOffset now)
    {
        if (IsPendingPairing(status, expiresUtc, now))
        {
            return null;
        }

        return Normalize(status) == "pending" ? "Request expired" : "Request already answered";
    }

    /// <summary>
    /// EV-08: the keyed diff a poll applies instead of clearing and rebuilding
    /// the three tables. A row whose key is still present keeps its instance, so
    /// the list box keeps the operator's selection and its scroll offset; a row
    /// whose payload changed is marked <see cref="RowDiffKind.Update"/> and the
    /// caller writes the new values into that existing instance; a key that is
    /// new becomes <see cref="RowDiffKind.Insert"/>; a key that is gone lands in
    /// <see cref="RowDiff{T}.Removed"/>.
    ///
    /// The desired list decides the order. Each existing row is consumed at most
    /// once, so a repeated key can never bind two desired rows to one instance,
    /// and an empty key never matches (a row without an id is always new).
    /// </summary>
    public static RowDiff<T> DiffRows<T>(
        IEnumerable<T> existing,
        IEnumerable<T> desired,
        Func<T, string> key,
        Func<T, string>? payload = null,
        string? selectedKey = null)
    {
        payload ??= key;

        var available = new Dictionary<string, Queue<T>>(StringComparer.Ordinal);
        foreach (var row in existing)
        {
            var k = key(row) ?? "";
            if (!available.TryGetValue(k, out var queue))
            {
                queue = new Queue<T>();
                available[k] = queue;
            }

            queue.Enqueue(row);
        }

        var rows = new List<DiffedRow<T>>();
        foreach (var row in desired)
        {
            var k = key(row) ?? "";
            if (k.Length > 0 && available.TryGetValue(k, out var queue) && queue.Count > 0)
            {
                var current = queue.Dequeue();
                var kind = string.Equals(payload(current), payload(row), StringComparison.Ordinal)
                    ? RowDiffKind.Keep
                    : RowDiffKind.Update;
                rows.Add(new DiffedRow<T>(kind, k, current));
                continue;
            }

            rows.Add(new DiffedRow<T>(RowDiffKind.Insert, k, row));
        }

        var removed = new List<DiffedRow<T>>();
        foreach (var (k, queue) in available)
        {
            while (queue.Count > 0)
            {
                removed.Add(new DiffedRow<T>(RowDiffKind.Remove, k, queue.Dequeue()));
            }
        }

        var selectedIndex = -1;
        if (!string.IsNullOrEmpty(selectedKey))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (string.Equals(rows[i].Key, selectedKey, StringComparison.Ordinal))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        return new RowDiff<T>(rows, removed, selectedKey, selectedIndex);
    }

    /// <summary>
    /// EV-02: the one-line name UI Automation reads for a pairing-request row.
    /// A ListBoxItem without a name falls back to its item's type name, so a
    /// screen reader used to hear "AgentSessionsPage+PairingRow". The format is
    /// built here, from the same words the row prints, so the name carries the
    /// chip label and the live countdown instead of a raw enum.
    ///
    /// EV-03 nit: a countdown that has already run out reads "expired", not
    /// "expires expired", because there is no longer an expiry to count down to.
    /// </summary>
    public static string PairingRowName(string? client, string? statusLabel, string? countdown)
    {
        var time = NamePart(countdown, "unknown");
        return string.Format(
            CultureInfo.InvariantCulture,
            string.Equals(time, "expired", StringComparison.OrdinalIgnoreCase)
                ? "{0}, pairing {1}, expired"
                : "{0}, pairing {1}, expires {2}",
            NamePart(client, "Unknown client"),
            NamePart(statusLabel, "Unknown"),
            time);
    }

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
/// EV-08: what a keyed diff decides for one row. <see cref="Keep"/> and
/// <see cref="Update"/> carry the row that is already in the list, so the
/// collection keeps the very instance the operator selected; <see cref="Insert"/>
/// carries the new row; <see cref="Remove"/> only appears in the removed list.
/// </summary>
public enum RowDiffKind
{
    Keep,
    Update,
    Insert,
    Remove,
}

/// <summary>EV-08: one row of a keyed diff, with the key it matched on.</summary>
public readonly record struct DiffedRow<T>(RowDiffKind Kind, string Key, T Row);

/// <summary>
/// EV-08: the result of comparing the rows a table shows with the rows the
/// supervisor just returned. <see cref="Rows"/> is the desired order using the
/// existing instances wherever a key survived, <see cref="Removed"/> is what
/// must leave the collection, and <see cref="SelectedIndex"/> locates the
/// operator's selection in the new order (-1 when the selected row is gone).
/// </summary>
public sealed record RowDiff<T>(
    IReadOnlyList<DiffedRow<T>> Rows,
    IReadOnlyList<DiffedRow<T>> Removed,
    string? SelectedKey,
    int SelectedIndex)
{
    public int Kept => Rows.Count(row => row.Kind == RowDiffKind.Keep);

    public int Updated => Rows.Count(row => row.Kind == RowDiffKind.Update);

    public int Inserted => Rows.Count(row => row.Kind == RowDiffKind.Insert);

    public int RemovedCount => Removed.Count;
}

/// <summary>
/// EV-08: the one writer of the rail's pending-pairing badge.
///
/// Before this, three places assigned the count: the ten-second badge poll, the
/// full refresh, and the page's one-second ticker (through PublishCounts). A
/// tick that ran while a poll was in flight could put the older, full-list count
/// back on the rail after the poll had already published the true one.
///
/// Every completed read now carries a sequence number and only the newest one
/// is taken, so the badge always shows the freshest supervisor answer and no
/// second author can overwrite it with rows that were read earlier.
/// </summary>
public sealed class PendingPairingBadge
{
    private long _newestSequence = long.MinValue;

    /// <summary>The count the rail should print; 0 until the first read.</summary>
    public int Count { get; private set; }

    /// <summary>Which read published the current value ("poll", "refresh"...).</summary>
    public string Source { get; private set; } = "";

    /// <summary>The sequence of the value the badge holds.</summary>
    public long Sequence => _newestSequence == long.MinValue ? 0 : _newestSequence;

    /// <summary>
    /// Offers one read's count. Returns true when it is newer than the value the
    /// badge holds and was therefore taken; a stale or repeated sequence is
    /// refused and changes nothing.
    /// </summary>
    public bool Publish(string source, long sequence, int count)
    {
        if (sequence <= _newestSequence)
        {
            return false;
        }

        _newestSequence = sequence;
        Count = Math.Max(0, count);
        Source = source ?? "";
        return true;
    }
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
