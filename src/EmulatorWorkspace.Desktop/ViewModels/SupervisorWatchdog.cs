namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>What the desktop's supervisor watchdog is doing right now.</summary>
public enum SupervisorWatchState
{
    /// <summary>Nothing to recover: the pipe is up or nobody has lost it yet.</summary>
    Idle,

    /// <summary>A loss was detected and the next bounded attempt is pending.</summary>
    Waiting,

    /// <summary>An attempt is running (start the supervisor, reconnect, re-read).</summary>
    Attempting,

    /// <summary>The budget is spent; the error bar's Retry owns the next move.</summary>
    GaveUp,
}

/// <summary>
/// UX-16 item C: the rule for recovering a supervisor that went away, with no
/// timer of its own and no loop that can run forever. A run is at most
/// <see cref="MaxAttempts"/> attempts with a growing delay, one attempt at a
/// time, and every loss during a run is absorbed instead of starting a second
/// one. Pure: the view model performs the start, the connect and the log.
/// </summary>
public sealed class SupervisorWatchdog
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
    };

    public SupervisorWatchState State { get; private set; } = SupervisorWatchState.Idle;

    /// <summary>Attempts this run has already spent.</summary>
    public int Attempts { get; private set; }

    public bool IsRunning => State is SupervisorWatchState.Waiting or SupervisorWatchState.Attempting;

    /// <summary>The delay of a given attempt, 0-based, so a test can pin the schedule.</summary>
    public static TimeSpan DelayFor(int attempt) => Backoff[Math.Clamp(attempt, 0, Backoff.Length - 1)];

    /// <summary>
    /// One supervisor loss. Returns true when it starts a run, so the caller
    /// starts exactly one recovery loop: a loss while a run is in flight, or
    /// after the budget is spent, starts nothing.
    /// </summary>
    public bool SupervisorLost()
    {
        if (IsRunning || State == SupervisorWatchState.GaveUp)
        {
            return false;
        }

        State = SupervisorWatchState.Waiting;
        Attempts = 0;
        return true;
    }

    /// <summary>
    /// Takes the next attempt's delay. False once the budget is spent, which is
    /// the only way a run ends without a connection.
    /// </summary>
    public bool TryNextDelay(out TimeSpan delay)
    {
        delay = default;
        if (State != SupervisorWatchState.Waiting)
        {
            return false;
        }

        if (Attempts >= MaxAttempts)
        {
            State = SupervisorWatchState.GaveUp;
            return false;
        }

        delay = DelayFor(Attempts);
        State = SupervisorWatchState.Attempting;
        return true;
    }

    /// <summary>Counts one spent attempt and schedules the next or gives up.</summary>
    public void AttemptFailed()
    {
        Attempts++;
        State = Attempts >= MaxAttempts ? SupervisorWatchState.GaveUp : SupervisorWatchState.Waiting;
    }

    /// <summary>The supervisor answered: the run is over and the budget is fresh.</summary>
    public void AttemptSucceeded()
    {
        State = SupervisorWatchState.Idle;
        Attempts = 0;
    }

    /// <summary>
    /// A deliberate refresh or Retry re-arms the watchdog; the recovery loop
    /// itself never calls this, so its own attempts still count.
    /// </summary>
    public void Reset() => AttemptSucceeded();
}
