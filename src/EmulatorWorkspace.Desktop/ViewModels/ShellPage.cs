namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-06: the one page the shell body shows.
///
/// Before this the Studio, the full Logcat page and the Agents page were three
/// independent booleans (two stored flags and one computed from them), so the
/// shell could be told two contradictory things: "Logcat is showing" and
/// "Agents is showing" at once, with the Studio hidden by both. The page is one
/// value now, and every "is this visible" answer is derived from it.
/// </summary>
public enum ShellPage
{
    Studio,
    Logcat,
    Agents,
}

/// <summary>
/// EV-06: the pure mapping from <see cref="ShellPage"/> to the booleans the XAML
/// and the rail read. The view model notifies on every page change; this class
/// is what the tests pin, so the three destinations cannot disagree again.
/// </summary>
public static class ShellPageRules
{
    public static bool ShowsStudio(ShellPage page) => page == ShellPage.Studio;

    public static bool ShowsLogcat(ShellPage page) => page == ShellPage.Logcat;

    public static bool ShowsAgents(ShellPage page) => page == ShellPage.Agents;

    /// <summary>
    /// The rail's Logcat item is active for the dock as well as the page, because
    /// both are the same stream in the same destination.
    /// </summary>
    public static bool LogcatRailActive(ShellPage page, bool dockOpen) =>
        dockOpen || page == ShellPage.Logcat;

    public static bool AgentsRailActive(ShellPage page) => page == ShellPage.Agents;

    /// <summary>
    /// True when a page change changes whether a Logcat surface is visible. That
    /// is the moment the log subscription has to be attached or detached; a
    /// Studio-to-Agents move leaves the dock exactly as it was.
    /// </summary>
    public static bool TouchesLogcat(ShellPage from, ShellPage to) =>
        from != to && (from == ShellPage.Logcat || to == ShellPage.Logcat);
}
