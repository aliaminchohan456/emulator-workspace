using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// UX-16 item A: the shell has one overlay state, and it only stops being inert
/// when the last surface that covers the chrome has closed.
/// </summary>
public class OverlayStackTests
{
    [Fact]
    public void PushShell_MakesTheShellInertAndReportsTheTransition()
    {
        var overlays = new OverlayStack();

        Assert.True(overlays.Push(OverlayScope.Shell));

        Assert.True(overlays.IsOpen);
        Assert.True(overlays.ShellInert);
        Assert.False(overlays.StageInert);
        Assert.Equal(1, overlays.Count);
    }

    [Fact]
    public void StagePanelUnderAShellSurface_ChangesNothingUntilBothClose()
    {
        var overlays = new OverlayStack();
        Assert.True(overlays.Push(OverlayScope.Shell));

        // The narrow inspector opens over a stage the drawer already covers:
        // that adds the stage region, so it is a real change.
        Assert.True(overlays.Push(OverlayScope.Stage));
        Assert.True(overlays.ShellInert);
        Assert.True(overlays.StageInert);

        // Closing the drawer must not free the stage the panel still covers.
        Assert.True(overlays.Pop(OverlayScope.Shell));
        Assert.False(overlays.ShellInert);
        Assert.True(overlays.StageInert);

        // Closing the panel restores the last of it.
        Assert.True(overlays.Pop(OverlayScope.Stage));
        Assert.False(overlays.IsOpen);
    }

    [Fact]
    public void PopOfAScopeThatIsNotOpen_IsANoOp()
    {
        var overlays = new OverlayStack();
        overlays.Push(OverlayScope.Stage);

        // A second close of the drawer (Esc and a scrim click in the same
        // frame) cannot free chrome a different surface still owns.
        Assert.False(overlays.Pop(OverlayScope.Shell));
        Assert.True(overlays.StageInert);
        Assert.Equal(1, overlays.Count);
    }

    [Fact]
    public void SameScopeTwice_KeepsTheChromeInertUntilTheSecondClose()
    {
        var overlays = new OverlayStack();

        Assert.True(overlays.Push(OverlayScope.Shell));
        Assert.False(overlays.Push(OverlayScope.Shell));
        Assert.Equal(2, overlays.Count);

        Assert.False(overlays.Pop(OverlayScope.Shell));
        Assert.True(overlays.ShellInert);

        Assert.True(overlays.Pop(OverlayScope.Shell));
        Assert.False(overlays.IsOpen);
    }
}
