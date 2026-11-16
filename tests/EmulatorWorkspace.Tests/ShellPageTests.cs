using EmulatorWorkspace.Desktop.ViewModels;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-06: the shell body has one page. Exactly one destination is visible at a
/// time, the rail's two page destinations follow the same value, and only a
/// Logcat transition touches the log attachment.
/// </summary>
public class ShellPageTests
{
    public static TheoryData<ShellPage> Pages => new()
    {
        ShellPage.Studio,
        ShellPage.Logcat,
        ShellPage.Agents,
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void ExactlyOnePageIsVisible(ShellPage page)
    {
        var visible = new[]
        {
            ShellPageRules.ShowsStudio(page),
            ShellPageRules.ShowsLogcat(page),
            ShellPageRules.ShowsAgents(page),
        };

        Assert.Equal(1, visible.Count(v => v));
    }

    [Fact]
    public void TheThreePagesMapToTheirOwnSurface()
    {
        Assert.True(ShellPageRules.ShowsStudio(ShellPage.Studio));
        Assert.False(ShellPageRules.ShowsLogcat(ShellPage.Studio));
        Assert.False(ShellPageRules.ShowsAgents(ShellPage.Studio));

        Assert.False(ShellPageRules.ShowsStudio(ShellPage.Logcat));
        Assert.True(ShellPageRules.ShowsLogcat(ShellPage.Logcat));
        Assert.False(ShellPageRules.ShowsAgents(ShellPage.Logcat));

        Assert.False(ShellPageRules.ShowsStudio(ShellPage.Agents));
        Assert.False(ShellPageRules.ShowsLogcat(ShellPage.Agents));
        Assert.True(ShellPageRules.ShowsAgents(ShellPage.Agents));
    }

    [Theory]
    [InlineData(ShellPage.Studio, false, false)]
    [InlineData(ShellPage.Logcat, false, true)]
    [InlineData(ShellPage.Agents, false, false)]
    [InlineData(ShellPage.Studio, true, true)]
    [InlineData(ShellPage.Logcat, true, true)]
    [InlineData(ShellPage.Agents, true, true)]
    public void TheLogcatRailItemIsActiveForTheDockOrThePage(
        ShellPage page,
        bool dockOpen,
        bool expected)
    {
        Assert.Equal(expected, ShellPageRules.LogcatRailActive(page, dockOpen));
    }

    [Fact]
    public void TheAgentsRailItemIsActiveOnlyOnItsPage()
    {
        Assert.False(ShellPageRules.AgentsRailActive(ShellPage.Studio));
        Assert.False(ShellPageRules.AgentsRailActive(ShellPage.Logcat));
        Assert.True(ShellPageRules.AgentsRailActive(ShellPage.Agents));
    }

    [Theory]
    [InlineData(ShellPage.Studio, ShellPage.Logcat, true)]
    [InlineData(ShellPage.Logcat, ShellPage.Studio, true)]
    [InlineData(ShellPage.Logcat, ShellPage.Agents, true)]
    [InlineData(ShellPage.Agents, ShellPage.Logcat, true)]
    [InlineData(ShellPage.Studio, ShellPage.Agents, false)]
    [InlineData(ShellPage.Agents, ShellPage.Studio, false)]
    [InlineData(ShellPage.Studio, ShellPage.Studio, false)]
    [InlineData(ShellPage.Logcat, ShellPage.Logcat, false)]
    public void OnlyALogcatTransitionTouchesTheLogSurface(
        ShellPage from,
        ShellPage to,
        bool expected)
    {
        Assert.Equal(expected, ShellPageRules.TouchesLogcat(from, to));
    }
}
