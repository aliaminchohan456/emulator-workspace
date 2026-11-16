namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// EV-07: the one sheet the device library is showing.
///
/// The create drawer, the clone drawer, the recovery panel and the confirmation
/// were four independent booleans, so the library could be told two
/// contradictory things at once: "the create drawer is open" and "the clone
/// drawer is open", with both hosts visible behind one scrim. The sheet is one
/// value now and every "is this open" answer is derived from it.
///
/// <see cref="Confirm"/> is the only sheet that stacks: it remembers the sheet
/// that asked for it, so Cancel (or Escape) returns there instead of dropping
/// the operator back to the page.
/// </summary>
public enum LibrarySheet
{
    None,
    Create,
    Clone,
    Recovery,
    Confirm,
}

/// <summary>
/// EV-07: the pure transitions between <see cref="LibrarySheet"/> values.
/// Opening a sheet replaces whatever is open, so exactly one sheet is ever on
/// screen; a confirmation keeps its parent and Cancel returns to it. No WPF
/// types, pinned by <c>LibrarySheetRulesTests</c>.
/// </summary>
public static class LibrarySheetRules
{
    /// <summary>
    /// A sheet the operator can open from the page. Confirm is excluded because
    /// it is only ever reached through <see cref="Confirm"/> with a parent.
    /// </summary>
    public static bool CanOpen(LibrarySheet target) =>
        target is LibrarySheet.Create or LibrarySheet.Clone or LibrarySheet.Recovery;

    /// <summary>
    /// Opening a sheet makes it the only sheet on screen: an open create drawer
    /// closes the moment Clone opens, instead of leaving two hosts behind one
    /// scrim. An invalid target leaves the current sheet untouched.
    /// </summary>
    public static LibrarySheet Open(LibrarySheet current, LibrarySheet target) =>
        CanOpen(target) ? target : current;

    /// <summary>Closing a sheet leaves nothing open behind it.</summary>
    public static LibrarySheet Close() => LibrarySheet.None;

    /// <summary>
    /// A confirmation stacks over the sheet that asked for it. The caller keeps
    /// the returned parent, because the confirmation itself is one value and
    /// cannot hold it.
    /// </summary>
    public static LibrarySheet Confirm(LibrarySheet parent) => LibrarySheet.Confirm;

    /// <summary>
    /// Cancel on a confirmation returns to the remembered parent; a parent that
    /// is itself a confirmation (or nothing) falls back to an empty page.
    /// </summary>
    public static LibrarySheet Cancel(LibrarySheet parent) =>
        parent is LibrarySheet.Confirm or LibrarySheet.None ? LibrarySheet.None : parent;

    /// <summary>
    /// Escape closes only the top sheet. A confirmation goes back to its parent;
    /// any other sheet closes to None and leaves the page in place.
    /// </summary>
    public static LibrarySheet Escape(LibrarySheet current, LibrarySheet parent) =>
        current == LibrarySheet.Confirm ? Cancel(parent) : Close();

    /// <summary>
    /// Whether <paramref name="target"/> is on screen. A confirmation's parent
    /// counts as visible beneath it, so Cancel reveals the sheet it came from
    /// without reopening it.
    /// </summary>
    public static bool Shows(LibrarySheet sheet, LibrarySheet parent, LibrarySheet target)
    {
        if (target is LibrarySheet.None or LibrarySheet.Confirm)
        {
            return false;
        }

        return sheet == target || (sheet == LibrarySheet.Confirm && parent == target);
    }
}
