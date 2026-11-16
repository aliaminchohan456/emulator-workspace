namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-07: what owns the device library's list right now.
///
/// It replaces three booleans (<c>_isLoading</c>, <c>_refreshing</c> and
/// <c>_isBusy</c>) that could disagree: a Refresh could set "loading" while an
/// operation set "busy", and the second pass of a double Refresh was stopped by
/// a flag nobody could observe. One value owns the list, and "busy" and
/// "loading" are derived from it.
/// </summary>
public enum LibraryLoad
{
    /// <summary>Nothing is reading or writing; the list on screen is settled.</summary>
    Idle,

    /// <summary>The first pass, from connect through the first device list.</summary>
    Loading,

    /// <summary>A later pass re-reading the list, doctor and profiles.</summary>
    Refreshing,

    /// <summary>A supervised operation owns the library until it finishes.</summary>
    Busy,
}

/// <summary>
/// EV-07: the pure load transitions. A requested refresh is accepted only from
/// rest, so two fast Refresh presses make one supervisor pass; a pass only
/// clears the state it began, so an operation that took over keeps ownership
/// until it ends. No WPF types, pinned by <c>DeviceLibraryLoadTests</c>.
/// </summary>
public static class LibraryLoadRules
{
    /// <summary>Whether a refresh request may start now (single-flight).</summary>
    public static bool AcceptsRefresh(LibraryLoad load) => load == LibraryLoad.Idle;

    public static bool IsBusy(LibraryLoad load) => load == LibraryLoad.Busy;

    /// <summary>The list is not settled: the first pass or a later pass runs.</summary>
    public static bool IsLoading(LibraryLoad load) =>
        load is LibraryLoad.Loading or LibraryLoad.Refreshing;

    /// <summary>A requested refresh from rest owns the list as Refreshing.</summary>
    public static LibraryLoad BeginRefresh(LibraryLoad current) =>
        AcceptsRefresh(current) ? LibraryLoad.Refreshing : current;

    /// <summary>
    /// A pass clears only the state it began. If an operation took ownership
    /// while the pass ran, the operation keeps it until it ends.
    /// </summary>
    public static LibraryLoad PassEnds(LibraryLoad current, LibraryLoad begun) =>
        current == begun ? LibraryLoad.Idle : current;

    /// <summary>A supervised operation owns the library for its whole run.</summary>
    public static LibraryLoad BeginOperation(LibraryLoad current) => LibraryLoad.Busy;

    public static LibraryLoad EndOperation(LibraryLoad current) => LibraryLoad.Idle;
}
