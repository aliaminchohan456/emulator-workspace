using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-07: the device library has one sheet. Opening another one closes the one
/// on screen, a confirmation remembers its parent so Cancel returns there, and
/// Escape closes exactly one layer.
/// </summary>
public class LibrarySheetRulesTests
{
    public static TheoryData<LibrarySheet> OpenableSheets => new()
    {
        LibrarySheet.Create,
        LibrarySheet.Clone,
        LibrarySheet.Recovery,
    };

    [Fact]
    public void OpeningCloneWhileCreateIsOpenClosesCreate()
    {
        var sheet = LibrarySheetRules.Open(LibrarySheet.None, LibrarySheet.Create);
        Assert.Equal(LibrarySheet.Create, sheet);

        sheet = LibrarySheetRules.Open(sheet, LibrarySheet.Clone);
        Assert.Equal(LibrarySheet.Clone, sheet);

        // The XAML's four bindings are derived: exactly one sheet is visible.
        Assert.False(LibrarySheetRules.Shows(sheet, LibrarySheet.None, LibrarySheet.Create));
        Assert.True(LibrarySheetRules.Shows(sheet, LibrarySheet.None, LibrarySheet.Clone));
    }

    [Theory]
    [MemberData(nameof(OpenableSheets))]
    public void EveryOpenableSheetClosesTheOneBeforeIt(LibrarySheet target)
    {
        foreach (var other in new[] { LibrarySheet.Create, LibrarySheet.Clone, LibrarySheet.Recovery })
        {
            var sheet = LibrarySheetRules.Open(other, target);
            Assert.Equal(target, sheet);
            Assert.Equal(target == other, LibrarySheetRules.Shows(sheet, LibrarySheet.None, other));
        }
    }

    [Fact]
    public void ConfirmRemembersItsParentAndCancelReturnsToIt()
    {
        var sheet = LibrarySheetRules.Open(LibrarySheet.None, LibrarySheet.Recovery);
        var parent = sheet;
        sheet = LibrarySheetRules.Confirm(parent);

        Assert.Equal(LibrarySheet.Confirm, sheet);
        // The parent stays visible beneath the confirmation.
        Assert.True(LibrarySheetRules.Shows(sheet, parent, LibrarySheet.Recovery));

        sheet = LibrarySheetRules.Cancel(parent);
        Assert.Equal(LibrarySheet.Recovery, sheet);
        Assert.True(LibrarySheetRules.Shows(sheet, LibrarySheet.None, LibrarySheet.Recovery));
    }

    [Fact]
    public void CancelFromThePageLeavesNothingOpen()
    {
        var sheet = LibrarySheetRules.Confirm(LibrarySheet.None);
        Assert.Equal(LibrarySheet.None, LibrarySheetRules.Cancel(LibrarySheet.None));
        Assert.Equal(LibrarySheet.Confirm, sheet);
    }

    [Fact]
    public void CancelOfAConfirmationParentNeverLeavesAConfirmationOpen()
    {
        // A parent that is itself Confirm is not a sheet the operator can see.
        Assert.Equal(LibrarySheet.None, LibrarySheetRules.Cancel(LibrarySheet.Confirm));
    }

    [Fact]
    public void EscapeClosesOnlyTheTopSheet()
    {
        // Confirm over Recovery: Escape closes the confirmation, not Recovery.
        var parent = LibrarySheet.Recovery;
        var sheet = LibrarySheetRules.Confirm(parent);
        sheet = LibrarySheetRules.Escape(sheet, parent);
        Assert.Equal(LibrarySheet.Recovery, sheet);

        // Escape again closes the recovery panel itself.
        sheet = LibrarySheetRules.Escape(sheet, LibrarySheet.None);
        Assert.Equal(LibrarySheet.None, sheet);
    }

    [Theory]
    [MemberData(nameof(OpenableSheets))]
    public void EscapeOnAPlainSheetLeavesThePage(LibrarySheet sheet)
    {
        Assert.Equal(LibrarySheet.None, LibrarySheetRules.Escape(sheet, LibrarySheet.None));
    }

    [Fact]
    public void ConfirmIsNotOpenableWithoutAParent()
    {
        Assert.False(LibrarySheetRules.CanOpen(LibrarySheet.None));
        Assert.False(LibrarySheetRules.CanOpen(LibrarySheet.Confirm));
        Assert.Equal(LibrarySheet.Create, LibrarySheetRules.Open(LibrarySheet.Create, LibrarySheet.Confirm));
    }

    [Fact]
    public void OnlyOneSheetIsVisibleForEveryValue()
    {
        var sheets = new[]
        {
            LibrarySheet.None,
            LibrarySheet.Create,
            LibrarySheet.Clone,
            LibrarySheet.Recovery,
            LibrarySheet.Confirm,
        };

        foreach (var sheet in sheets)
        {
            var visible = new[]
            {
                LibrarySheetRules.Shows(sheet, LibrarySheet.None, LibrarySheet.Create),
                LibrarySheetRules.Shows(sheet, LibrarySheet.None, LibrarySheet.Clone),
                LibrarySheetRules.Shows(sheet, LibrarySheet.None, LibrarySheet.Recovery),
            };

            var expected = sheet is LibrarySheet.Create or LibrarySheet.Clone or LibrarySheet.Recovery ? 1 : 0;
            Assert.Equal(expected, visible.Count(v => v));
        }
    }
}
