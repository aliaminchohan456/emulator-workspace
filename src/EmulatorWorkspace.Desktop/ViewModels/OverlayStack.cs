namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// Which chrome a floating surface covers while it is open. UX-16 item A: a
/// drawer or a dialog covers the whole shell, the narrow inspector covers the
/// stage only.
/// </summary>
public enum OverlayScope
{
    /// <summary>The rail, the title bar's content and the page body.</summary>
    Shell,

    /// <summary>Only the stage behind the panel: guest frame, chips and pill.</summary>
    Stage,
}

/// <summary>
/// The shell's single "an overlay is open" state. Every floating surface enters
/// the scope it covers and leaves it on close, and the window applies exactly
/// what the stack reports, so two surfaces open at once keep the chrome inert
/// until the last one closes. Pure: it holds no WPF types and decides no
/// geometry.
/// </summary>
public sealed class OverlayStack
{
    private readonly List<OverlayScope> _open = new();

    public int Count => _open.Count;

    public bool IsOpen => _open.Count > 0;

    /// <summary>True while any open surface covers the rail, title bar or body.</summary>
    public bool ShellInert => _open.Contains(OverlayScope.Shell);

    /// <summary>True while any open surface covers the stage.</summary>
    public bool StageInert => _open.Contains(OverlayScope.Stage);

    /// <summary>
    /// Opens one surface. Returns true when that changed what the chrome behind
    /// it must do, so the caller only touches the tree on a real transition.
    /// </summary>
    public bool Push(OverlayScope scope)
    {
        var before = Flags();
        _open.Add(scope);
        return before != Flags();
    }

    /// <summary>
    /// Closes the most recent surface of that scope. Returns true when the
    /// chrome must be restored. A scope that is not open is a no-op, so a
    /// double close can never un-inert chrome a different surface still owns.
    /// </summary>
    public bool Pop(OverlayScope scope)
    {
        var index = _open.LastIndexOf(scope);
        if (index < 0)
        {
            return false;
        }

        var before = Flags();
        _open.RemoveAt(index);
        return before != Flags();
    }

    private int Flags() => (ShellInert ? 1 : 0) | (StageInert ? 2 : 0);
}
