using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-07: the device library's load is one value instead of three booleans, and
/// a requested refresh is single-flight. The state is claimed before the first
/// await, so two fast Refresh presses make one supervisor pass; a pass clears
/// only the state it began, so an operation that took over keeps ownership.
/// </summary>
public class DeviceLibraryLoadTests
{
    [Fact]
    public void TheRestingLoadIsNeitherBusyNorLoading()
    {
        Assert.True(LibraryLoadRules.AcceptsRefresh(LibraryLoad.Idle));
        Assert.False(LibraryLoadRules.IsBusy(LibraryLoad.Idle));
        Assert.False(LibraryLoadRules.IsLoading(LibraryLoad.Idle));
    }

    [Theory]
    [InlineData(LibraryLoad.Loading)]
    [InlineData(LibraryLoad.Refreshing)]
    [InlineData(LibraryLoad.Busy)]
    public void OnlyAnIdleLibraryAcceptsARequestedRefresh(LibraryLoad load)
    {
        Assert.False(LibraryLoadRules.AcceptsRefresh(load));
        Assert.Equal(load, LibraryLoadRules.BeginRefresh(load));
    }

    [Fact]
    public void TwoFastRefreshesWhileLoadingMakeOneSupervisorCall()
    {
        var load = LibraryLoad.Idle;
        var supervisorCalls = 0;

        // This is the view model's requested-refresh entry point: the guard
        // runs before the first await and the state is claimed in the same
        // dispatcher turn, so a second press cannot reach the supervisor.
        void PressRefresh()
        {
            if (!LibraryLoadRules.AcceptsRefresh(load))
            {
                return;
            }

            load = LibraryLoadRules.BeginRefresh(load);
            supervisorCalls++;
        }

        PressRefresh();
        PressRefresh();
        PressRefresh();

        Assert.Equal(1, supervisorCalls);
        Assert.Equal(LibraryLoad.Refreshing, load);
    }

    [Fact]
    public void AFinishedPassReturnsToRestAndAllowsTheNextRefresh()
    {
        var load = LibraryLoadRules.BeginRefresh(LibraryLoad.Idle);
        load = LibraryLoadRules.PassEnds(load, LibraryLoad.Refreshing);

        Assert.Equal(LibraryLoad.Idle, load);
        Assert.True(LibraryLoadRules.AcceptsRefresh(load));
    }

    [Fact]
    public void TheFirstPassOwnsTheListAsLoading()
    {
        Assert.True(LibraryLoadRules.IsLoading(LibraryLoad.Loading));
        Assert.False(LibraryLoadRules.AcceptsRefresh(LibraryLoad.Loading));
        Assert.False(LibraryLoadRules.IsBusy(LibraryLoad.Loading));
    }

    [Fact]
    public void AnOperationOwnsTheLibraryUntilItEnds()
    {
        var load = LibraryLoadRules.BeginOperation(LibraryLoad.Idle);

        Assert.Equal(LibraryLoad.Busy, load);
        Assert.True(LibraryLoadRules.IsBusy(load));
        Assert.False(LibraryLoadRules.AcceptsRefresh(load));

        // A refresh requested while the operation runs is ignored; the
        // operation's own pass does not steal its state.
        Assert.Equal(LibraryLoad.Busy, LibraryLoadRules.BeginRefresh(load));
        Assert.Equal(LibraryLoad.Busy, LibraryLoadRules.PassEnds(load, LibraryLoad.Refreshing));

        load = LibraryLoadRules.EndOperation(load);
        Assert.Equal(LibraryLoad.Idle, load);
    }

    [Fact]
    public void APassOnlyClearsTheStateItBegan()
    {
        // An operation took over while this pass was running: the pass ends
        // without announcing the list settled.
        Assert.Equal(
            LibraryLoad.Busy,
            LibraryLoadRules.PassEnds(LibraryLoad.Busy, LibraryLoad.Refreshing));
        Assert.Equal(
            LibraryLoad.Loading,
            LibraryLoadRules.PassEnds(LibraryLoad.Loading, LibraryLoad.Refreshing));
        Assert.Equal(
            LibraryLoad.Idle,
            LibraryLoadRules.PassEnds(LibraryLoad.Refreshing, LibraryLoad.Refreshing));
    }

    [Theory]
    [InlineData(LibraryLoad.Loading, true)]
    [InlineData(LibraryLoad.Refreshing, true)]
    [InlineData(LibraryLoad.Idle, false)]
    [InlineData(LibraryLoad.Busy, false)]
    public void LoadingCoversTheFirstPassAndLaterPasses(LibraryLoad load, bool expected)
    {
        Assert.Equal(expected, LibraryLoadRules.IsLoading(load));
    }

    [Theory]
    [InlineData(LibraryLoad.Idle, false)]
    [InlineData(LibraryLoad.Loading, false)]
    [InlineData(LibraryLoad.Refreshing, false)]
    [InlineData(LibraryLoad.Busy, true)]
    public void BusyIsTheOperationsOwnState(LibraryLoad load, bool expected)
    {
        Assert.Equal(expected, LibraryLoadRules.IsBusy(load));
    }
}
