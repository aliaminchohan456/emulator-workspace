namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// EV-11b: the reason words and the line shape a supervisor writes when it
/// stops. The supervisor used to leave no trace at all when it was killed,
/// which made "it died" indistinguishable from "it never started"; every path
/// that ends the process now names itself, the exit code and how long the
/// process had been up.
///
/// The words are stable strings, so a log slice can be grouped by reason
/// without parsing prose. <see cref="SupervisorExitReason.Idle"/> is reserved
/// for an inactivity shutdown; this supervisor has no idle timeout, so nothing
/// emits it today.
/// </summary>
public static class ExitTrail
{
    /// <summary>The reason word for a log line, the same string for every caller.</summary>
    public static string Word(SupervisorExitReason reason) => reason switch
    {
        SupervisorExitReason.Normal => "normal",
        SupervisorExitReason.ClientRequest => "client",
        SupervisorExitReason.Idle => "idle",
        SupervisorExitReason.UnhandledException => "unhandled_exception",
        SupervisorExitReason.ProcessExit => "process_exit",
        SupervisorExitReason.ConsoleClose => "console_close",
        SupervisorExitReason.ConsoleShutdown => "console_shutdown",
        SupervisorExitReason.ConsoleLogoff => "console_logoff",
        SupervisorExitReason.ConsoleBreak => "console_break",
        SupervisorExitReason.ConsoleCancel => "console_cancel",
        SupervisorExitReason.StartupRefused => "startup_refused",
        _ => "unspecified",
    };

    /// <summary>
    /// One line: <c>exit reason=client code=0 uptime=00:12:34</c>, with an
    /// optional detail flattened onto the same line so a stack trace stays one
    /// log entry.
    /// </summary>
    public static string Format(
        SupervisorExitReason reason,
        int code,
        TimeSpan uptime,
        string? detail = null)
    {
        var line = $"exit reason={Word(reason)} code={code} uptime={Uptime(uptime)}";
        return string.IsNullOrWhiteSpace(detail) ? line : line + " detail=" + Flatten(detail);
    }

    /// <summary>Hours keep counting past a day rather than wrapping into days.</summary>
    public static string Uptime(TimeSpan uptime)
    {
        var clamped = uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime;
        var hours = (int)Math.Floor(clamped.TotalHours);
        return $"{hours:00}:{clamped.Minutes:00}:{clamped.Seconds:00}";
    }

    /// <summary>
    /// Which note wins when several paths fire for one exit. A console close or
    /// shutdown has already happened and outranks everything; a crash outranks a
    /// runtime teardown, a requested stop outranks the plain end of the loop,
    /// and a normal return is the weakest note of all.
    /// </summary>
    public static int Priority(SupervisorExitReason reason) => reason switch
    {
        SupervisorExitReason.Normal => 0,
        SupervisorExitReason.Idle => 1,
        SupervisorExitReason.ClientRequest => 1,
        SupervisorExitReason.ConsoleCancel => 2,
        SupervisorExitReason.ConsoleBreak => 2,
        SupervisorExitReason.ProcessExit => 3,
        SupervisorExitReason.UnhandledException => 4,
        SupervisorExitReason.StartupRefused => 4,
        SupervisorExitReason.ConsoleClose => 5,
        SupervisorExitReason.ConsoleLogoff => 5,
        SupervisorExitReason.ConsoleShutdown => 5,
        _ => 0,
    };

    /// <summary>
    /// The console control events, as Windows numbers them, mapped to the one
    /// reason each stands for. Null means the event is not one this host cares
    /// about, and the handler leaves the default behaviour alone.
    /// </summary>
    public static SupervisorExitReason? FromConsoleControl(int controlType) => controlType switch
    {
        0 => SupervisorExitReason.ConsoleCancel,
        1 => SupervisorExitReason.ConsoleBreak,
        2 => SupervisorExitReason.ConsoleClose,
        5 => SupervisorExitReason.ConsoleLogoff,
        6 => SupervisorExitReason.ConsoleShutdown,
        _ => null,
    };

    private static string Flatten(string detail)
    {
        var oneLine = detail.Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return oneLine.Length <= 400 ? oneLine : oneLine[..400] + "...";
    }
}

/// <summary>The paths that end a supervisor process.</summary>
public enum SupervisorExitReason
{
    /// <summary>The pipe loop ended and the host returned normally.</summary>
    Normal,

    /// <summary>A client asked the supervisor to stop.</summary>
    ClientRequest,

    /// <summary>Reserved: the last client has been gone for an idle timeout. Nothing emits it; there is no timeout.</summary>
    Idle,

    /// <summary>An exception escaped the host's own handling.</summary>
    UnhandledException,

    /// <summary>The runtime is tearing the process down (AppDomain.ProcessExit).</summary>
    ProcessExit,

    /// <summary>CTRL_CLOSE_EVENT: the console window or the process was closed.</summary>
    ConsoleClose,

    /// <summary>CTRL_SHUTDOWN_EVENT: Windows is shutting down.</summary>
    ConsoleShutdown,

    /// <summary>CTRL_LOGOFF_EVENT: the interactive user logged off.</summary>
    ConsoleLogoff,

    /// <summary>CTRL_BREAK_EVENT.</summary>
    ConsoleBreak,

    /// <summary>CTRL_C_EVENT or Console.CancelKeyPress.</summary>
    ConsoleCancel,

    /// <summary>The root lock is held by another supervisor, so this one refused to run.</summary>
    StartupRefused,
}
