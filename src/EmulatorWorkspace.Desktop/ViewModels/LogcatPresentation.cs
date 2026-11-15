using System.Globalization;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-08: the Logcat drawer's presentation rules, kept out of WPF so they can be
/// tested. Every value here is derived from state the app already has - the
/// retained lines, the filter the operator set, and the three stream facts the
/// view model tracks (the guest is running, a subscription exists, the stream is
/// paused). Nothing invents a line, a count or a state.
/// </summary>
public static class LogcatPresentation
{
    /// <summary>The five level chips, in Android's own severity order.</summary>
    public static readonly IReadOnlyList<string> Levels = new[] { "V", "D", "I", "W", "E" };

    /// <summary>
    /// "1,284 lines · 3 warnings · 1 error": warnings and errors appear only when
    /// they exist, and an empty drawer prints nothing (it shows its empty state).
    /// </summary>
    public static string CountText(int total, int warnings, int errors)
    {
        if (total <= 0)
        {
            return "";
        }

        var text = LineCount(total);
        if (warnings > 0)
        {
            text += " · " + WarningCount(warnings);
        }

        if (errors > 0)
        {
            text += " · " + ErrorCount(errors);
        }

        return text;
    }

    /// <summary>"1 line" / "1,284 lines" - the summary's first segment.</summary>
    public static string LineCount(int total) => total == 1
        ? "1 line"
        : $"{total.ToString("N0", CultureInfo.InvariantCulture)} lines";

    /// <summary>"1 warning" / "3 warnings"; the caller only asks when above zero.</summary>
    public static string WarningCount(int warnings) => warnings == 1
        ? "1 warning"
        : $"{warnings.ToString("N0", CultureInfo.InvariantCulture)} warnings";

    /// <summary>"1 error" / "1,024 errors"; the caller only asks when above zero.</summary>
    public static string ErrorCount(int errors) => errors == 1
        ? "1 error"
        : $"{errors.ToString("N0", CultureInfo.InvariantCulture)} errors";

    /// <summary>
    /// The page's paused chip: "Paused · 12 buffered". Nothing is claimed while
    /// the stream is not paused, and the count is the real held-back line count.
    /// </summary>
    public static string PausedBuffered(int buffered) => buffered <= 0
        ? "Paused"
        : $"Paused · {buffered.ToString("N0", CultureInfo.InvariantCulture)} buffered";

    /// <summary>
    /// Returns every character offset of <paramref name="query"/> in
    /// <paramref name="text"/>, case-insensitively - the same comparison the
    /// filter uses, so a row can never show a hit the filter did not keep.
    /// </summary>
    public static IReadOnlyList<int> IndexesOf(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query) || query.Length > text.Length)
        {
            return Array.Empty<int>();
        }

        var hits = new List<int>();
        var index = 0;
        while (index <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            for (var offset = 0; offset < query.Length; offset++)
            {
                hits.Add(found + offset);
            }

            index = found + query.Length;
        }

        return hits;
    }

    /// <summary>
    /// The chip row's five toggles as one filter. All five on is the default and
    /// is exactly what the drawer showed before the chips existed (every level).
    /// </summary>
    public static LogLevelFilter Filter(bool verbose, bool debug, bool info, bool warn, bool error) =>
        new(verbose, debug, info, warn, error);

    /// <summary>
    /// The "following / paused" chip. It always names the stream's real state and
    /// never claims to be live while the guest is stopped or the subscription is
    /// gone; the tooltip carries the same facts as a sentence.
    /// </summary>
    public static LogStreamState Stream(bool guestRunning, bool subscribed, bool paused, string? deviceName)
    {
        var guest = string.IsNullOrWhiteSpace(deviceName) ? "the selected guest" : deviceName;

        if (!guestRunning)
        {
            return new LogStreamState(
                "No guest",
                "info",
                "No guest is running, so there is no log stream to follow.");
        }

        if (!subscribed)
        {
            return new LogStreamState(
                "Not attached",
                "info",
                $"The drawer is open but no log subscription is attached to {guest} yet.");
        }

        if (paused)
        {
            return new LogStreamState(
                "Paused",
                "warning",
                "Paused: lines the guest writes now are not added. Resume to keep following.");
        }

        return new LogStreamState(
            "Following",
            "ok",
            $"Following {guest}: new lines are appended as the guest writes them.");
    }

    /// <summary>
    /// The drawer's calm empty state: a title, one sentence, and at most one
    /// action - and only an action the app can really run. The order matters: an
    /// error outranks a filter, a filter outranks the guest, and the honest
    /// "waiting" case offers no button at all because there is nothing to fix.
    /// </summary>
    public static LogEmptyState Empty(
        bool hasLines,
        int visibleLines,
        bool guestRunning,
        bool subscribed,
        bool paused,
        bool hasError)
    {
        if (visibleLines > 0)
        {
            return LogEmptyState.None;
        }

        if (hasError)
        {
            return new LogEmptyState(
                "The log stream is not running",
                "The supervisor refused the subscription. Retry once the cause above is cleared.",
                "retry");
        }

        if (hasLines)
        {
            return new LogEmptyState(
                "No lines match these filters",
                "Every retained line is filtered out by the search, the tag or the level chips.",
                "filters");
        }

        if (!guestRunning)
        {
            return new LogEmptyState(
                "The guest is not running",
                "Start it to open a log stream for this device.",
                "start");
        }

        // No subscription means there is nothing to resume, so "Not attached"
        // outranks "Paused": an empty state never offers a Resume that would do
        // nothing. The chip uses the same precedence.
        if (!subscribed)
        {
            return new LogEmptyState(
                "Waiting for the log stream",
                "The subscription is not attached yet. Retry if it does not arrive on its own.",
                "retry");
        }

        if (paused)
        {
            return new LogEmptyState(
                "Paused before the first line",
                "Resume the stream to read what the guest logs next.",
                "resume");
        }

        return new LogEmptyState(
            "Waiting for the first line",
            "The stream is open; lines appear here as the guest writes them.",
            "");
    }

    /// <summary>
    /// The full Logcat page's state chip. It says "Live" only while the stream is
    /// really attached and appending; every other state keeps its own honest word
    /// instead of pretending to be live.
    /// </summary>
    public static LogStreamState PageStream(bool guestRunning, bool subscribed, bool paused, string? serial)
    {
        var guest = string.IsNullOrWhiteSpace(serial) ? "the selected guest" : serial;

        if (!guestRunning)
        {
            return new LogStreamState(
                "No guest",
                "info",
                "No guest is running, so there is no log stream to follow.");
        }

        if (!subscribed)
        {
            return new LogStreamState(
                "Not attached",
                "info",
                $"No log subscription is attached to {guest} yet.");
        }

        if (paused)
        {
            return new LogStreamState(
                "Paused",
                "warning",
                "Paused: lines the guest writes now are held back until you resume.");
        }

        return new LogStreamState(
            "Live",
            "ok",
            $"Live stream from {guest}: new lines are appended as the guest writes them.");
    }

    /// <summary>
    /// The page's empty state. The two states the brief names come first - "No
    /// lines yet · waiting for &lt;serial&gt;" with no button, and "No lines match
    /// these filters" with Clear filters - and the guest/subscription failures keep
    /// the action that can actually fix them. No sample line is ever produced.
    /// </summary>
    public static LogEmptyState PageEmpty(
        bool hasLines,
        int visibleLines,
        bool guestRunning,
        bool subscribed,
        bool paused,
        bool hasError,
        string? serial)
    {
        if (visibleLines > 0)
        {
            return LogEmptyState.None;
        }

        if (hasError)
        {
            return new LogEmptyState(
                "The log stream is not running",
                "The supervisor refused the subscription. Retry once the cause above is cleared.",
                "retry");
        }

        if (hasLines)
        {
            return new LogEmptyState(
                "No lines match these filters",
                "The search, the tag or the level chips hide every retained line.",
                "filters");
        }

        var guest = string.IsNullOrWhiteSpace(serial) ? "the selected guest" : serial;

        if (!guestRunning)
        {
            return new LogEmptyState(
                "No lines yet",
                "The guest is not running. Start it to open a log stream.",
                "start");
        }

        if (!subscribed)
        {
            return new LogEmptyState(
                "No lines yet",
                $"Waiting for the log stream from {guest} to attach.",
                "retry");
        }

        if (paused)
        {
            return new LogEmptyState(
                "No lines yet",
                "Paused before the first line. Resume to read what the guest writes next.",
                "resume");
        }

        return new LogEmptyState(
            $"No lines yet · waiting for {guest}",
            "Lines appear here as the guest writes them.",
            "");
    }
}

/// <summary>
/// UX-08: one chip's filter state. Android's fatal and assert levels are errors,
/// and a level the supervisor never sends is read as info rather than silently
/// dropped, so an unrecognised line can never vanish behind a filter that did not
/// name it.
/// </summary>
public readonly record struct LogLevelFilter(bool Verbose, bool Debug, bool Info, bool Warn, bool Error)
{
    public static LogLevelFilter All => new(true, true, true, true, true);

    public static string Normalize(string? level) => (level ?? "").Trim().ToUpperInvariant() switch
    {
        "V" => "V",
        "D" => "D",
        "W" => "W",
        "E" or "F" or "A" => "E",
        _ => "I",
    };

    public bool Allows(string? level) => Normalize(level) switch
    {
        "V" => Verbose,
        "D" => Debug,
        "W" => Warn,
        "E" => Error,
        _ => Info,
    };
}

/// <summary>UX-08: the word, tone and sentence behind the stream state chip.</summary>
public sealed record LogStreamState(string Text, string Tone, string Tooltip);

/// <summary>
/// UX-08: the drawer's empty state. <see cref="Action"/> is one of "filters",
/// "retry", "start", "resume" or empty; the XAML maps each to a command that
/// already exists (or to a logcat-only one), so an empty state can never offer a
/// button that does nothing.
/// </summary>
public sealed record LogEmptyState(string Title, string Detail, string Action)
{
    public static readonly LogEmptyState None = new("", "", "");

    public bool IsVisible => Title.Length > 0;
}
