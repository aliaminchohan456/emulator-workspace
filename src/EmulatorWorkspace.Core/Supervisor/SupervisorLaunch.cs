namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>
/// EV-11b: the creation flags and the one fallback rule for the supervisor
/// process, kept apart from the Win32 call so both are testable without
/// starting anything.
///
/// A tool host such as an editor or an agent runner commonly puts the commands
/// it runs inside a job object with KILL_ON_JOB_CLOSE, so ending the command
/// ends every process the command started. CREATE_BREAKAWAY_FROM_JOB asks
/// Windows to create the child outside the caller's job, which is what lets
/// the supervisor (and the guest it owns) outlive the shell that launched it.
/// CREATE_NEW_PROCESS_GROUP keeps console control events (Ctrl+C, Ctrl+Break,
/// close, logoff, shutdown) away from the launcher's own group, and
/// CREATE_NO_WINDOW keeps the child invisible.
///
/// Windows refuses the breakaway with ERROR_ACCESS_DENIED when the job does not
/// permit it. That refusal is the only failure worth retrying: the same
/// CreateProcess call without the flag is exactly the launch this code did
/// before, so the supervisor still starts and the operator is told once that it
/// shares the launcher's job.
/// </summary>
public static class SupervisorLaunch
{
    public const uint CreateNewProcessGroup = 0x00000200;
    public const uint CreateNoWindow = 0x08000000;
    public const uint CreateBreakawayFromJob = 0x01000000;

    /// <summary>ERROR_ACCESS_DENIED: what a job that forbids breakaway answers.</summary>
    public const int BreakawayDeniedError = 5;

    /// <summary>The flag word for one attempt.</summary>
    public static uint Flags(bool breakawayFromJob) =>
        CreateNoWindow | CreateNewProcessGroup | (breakawayFromJob ? CreateBreakawayFromJob : 0u);

    /// <summary>True when Windows named the one failure the fallback covers.</summary>
    public static bool IsBreakawayDenial(int win32Error) =>
        win32Error == BreakawayDeniedError;

    /// <summary>
    /// What the launcher does after CreateProcess returned false: retry once
    /// without the breakaway flag when that flag was the reason, otherwise
    /// report the failure as it is.
    /// </summary>
    public static LaunchRetry NextAttempt(int win32Error, bool breakawayRequested) =>
        breakawayRequested && IsBreakawayDenial(win32Error)
            ? LaunchRetry.WithoutBreakaway
            : LaunchRetry.GiveUp;

    /// <summary>The one line the launcher logs when it had to fall back.</summary>
    public static string FallbackNote(int win32Error) =>
        $"Supervisor launch: CREATE_BREAKAWAY_FROM_JOB was denied (error {win32Error}); " +
        "started without it, so the supervisor shares the launcher's job object and a " +
        "process-tree kill of the launcher ends it too.";
}

/// <summary>The launcher's next step after a failed CreateProcess.</summary>
public enum LaunchRetry
{
    /// <summary>CreateProcess failed for a reason the flag cannot explain.</summary>
    GiveUp,

    /// <summary>Retry the same command line without CREATE_BREAKAWAY_FROM_JOB.</summary>
    WithoutBreakaway,
}
