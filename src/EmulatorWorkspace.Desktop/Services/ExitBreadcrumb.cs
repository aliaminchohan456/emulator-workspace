namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// UX-16 item B: one line in the existing desktop log that says why this
/// process ended. Every exit path notes its cause while the app is still
/// running, and the app writes the line once, at the last moment it controls.
/// The reason with the highest severity wins: a crash is never reported as a
/// plain window close, and an explicit shutdown is never reported as a crash.
/// </summary>
public static class ExitBreadcrumb
{
    private const int WindowPriority = 1;
    private const int ShutdownPriority = 2;
    private const int FaultPriority = 3;

    private static readonly object Gate = new();
    private static string? _reason;
    private static int _priority;
    private static int _written;

    /// <summary>Records a cause. Later notes of equal severity replace earlier ones.</summary>
    public static void Note(string reason, bool fault = false, bool shutdown = false)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        var priority = fault ? FaultPriority : shutdown ? ShutdownPriority : WindowPriority;
        lock (Gate)
        {
            if (priority < _priority)
            {
                return;
            }

            _reason = Flatten(reason);
            _priority = priority;
        }
    }

    /// <summary>
    /// Writes the single exit line and flushes it. DesktopLog appends with
    /// File.AppendAllText, which flushes and closes the handle for every line,
    /// so the breadcrumb is on disk before the process ends. A second call is a
    /// no-op: one exit, one line.
    /// </summary>
    public static void Write(int exitCode)
    {
        if (Interlocked.Exchange(ref _written, 1) != 0)
        {
            return;
        }

        string reason;
        lock (Gate)
        {
            reason = _reason ?? "unspecified";
        }

        DesktopLog.Write($"exit reason={reason} code={exitCode}");
    }

    /// <summary>An exception type and message on one line, trimmed to a loggable length.</summary>
    public static string Describe(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}";
        return text.Length <= 300 ? text : text[..300] + "...";
    }

    private static string Flatten(string reason)
    {
        var oneLine = reason.Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return oneLine.Length <= 300 ? oneLine : oneLine[..300] + "...";
    }
}
