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
    /// code when it is not the success code ("Denied · forbidden_scope").
    /// </summary>
    public static string OutcomeChipText(string? outcome, string? code)
    {
        var label = OutcomeLabel(outcome);
        var reason = (code ?? "").Trim();
        return reason.Length == 0 || reason == "ok" ? label : $"{label} · {reason}";
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

    /// <summary>A guest's record state as the operator reads it.</summary>
    public static string InstanceLabel(string? state)
    {
        var value = (state ?? "").Trim();
        return value.Length == 0 ? "Unknown" : Normalize(value) == "defined" ? "Stopped" : Capitalize(value);
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
