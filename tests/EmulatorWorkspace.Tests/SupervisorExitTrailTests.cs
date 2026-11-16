using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>
/// EV-11b: every exit path writes one line with a reason word, the exit code and
/// the uptime. These pin the words, the shape and the precedence, so a log slice
/// can be trusted without reading the supervisor's source.
/// </summary>
public sealed class SupervisorExitTrailTests
{
    public static TheoryData<SupervisorExitReason, string> Reasons => new()
    {
        { SupervisorExitReason.Normal, "normal" },
        { SupervisorExitReason.ClientRequest, "client" },
        { SupervisorExitReason.Idle, "idle" },
        { SupervisorExitReason.UnhandledException, "unhandled_exception" },
        { SupervisorExitReason.ProcessExit, "process_exit" },
        { SupervisorExitReason.ConsoleClose, "console_close" },
        { SupervisorExitReason.ConsoleShutdown, "console_shutdown" },
        { SupervisorExitReason.ConsoleLogoff, "console_logoff" },
        { SupervisorExitReason.ConsoleBreak, "console_break" },
        { SupervisorExitReason.ConsoleCancel, "console_cancel" },
        { SupervisorExitReason.StartupRefused, "startup_refused" },
    };

    [Theory]
    [MemberData(nameof(Reasons))]
    public void EveryPath_HasItsOwnWordAndOneLineShape(SupervisorExitReason reason, string word)
    {
        Assert.Equal(word, ExitTrail.Word(reason));

        var line = ExitTrail.Format(reason, 0, TimeSpan.FromSeconds(3725));
        Assert.Equal($"exit reason={word} code=0 uptime=01:02:05", line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void EveryEnumValue_IsCoveredByTheTable()
    {
        foreach (var reason in Enum.GetValues<SupervisorExitReason>())
        {
            Assert.Contains(Reasons, row => Equals(row[0], reason));
        }
    }

    [Fact]
    public void EveryReason_HasADistinctWord()
    {
        var words = Enum.GetValues<SupervisorExitReason>().Select(ExitTrail.Word).ToArray();
        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("unspecified", words);
    }

    [Fact]
    public void Format_CarriesTheExitCodeAsWindowsReportedIt()
    {
        Assert.Equal("exit reason=unhandled_exception code=1 uptime=00:00:09",
            ExitTrail.Format(SupervisorExitReason.UnhandledException, 1, TimeSpan.FromSeconds(9)));
        Assert.Contains("code=-1073741819",
            ExitTrail.Format(SupervisorExitReason.ProcessExit, -1073741819, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(59, "00:00:59")]
    [InlineData(60, "00:01:00")]
    [InlineData(3599, "00:59:59")]
    [InlineData(86399, "23:59:59")]
    [InlineData(90061, "25:01:01")]
    public void Uptime_CountsHoursInsteadOfWrappingIntoDays(int seconds, string expected)
    {
        Assert.Equal(expected, ExitTrail.Uptime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Uptime_NeverPrintsANegativeClock()
    {
        Assert.Equal("00:00:00", ExitTrail.Uptime(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void Detail_IsFlattenedOntoTheSameLineAndKeptWhenShort()
    {
        var line = ExitTrail.Format(
            SupervisorExitReason.UnhandledException,
            1,
            TimeSpan.FromMinutes(2),
            "InvalidOperationException: pipe closed\r\n   at Supervisor.Run()");
        Assert.Equal(
            "exit reason=unhandled_exception code=1 uptime=00:02:00 " +
            "detail=InvalidOperationException: pipe closed |    at Supervisor.Run()",
            line);
    }

    [Fact]
    public void Detail_IsTrimmedSoOneCrashStaysOneLogEntry()
    {
        var line = ExitTrail.Format(
            SupervisorExitReason.UnhandledException, 1, TimeSpan.Zero, new string('x', 500));
        Assert.EndsWith("...", line);
        Assert.Equal(400 + 3, line[(line.IndexOf("detail=", StringComparison.Ordinal) + 7)..].Length);
    }

    [Fact]
    public void BlankDetailAddsNothing()
    {
        var plain = ExitTrail.Format(SupervisorExitReason.ClientRequest, 0, TimeSpan.Zero);
        Assert.Equal(plain, ExitTrail.Format(SupervisorExitReason.ClientRequest, 0, TimeSpan.Zero, "   "));
        Assert.Equal(plain, ExitTrail.Format(SupervisorExitReason.ClientRequest, 0, TimeSpan.Zero, null));
    }

    [Theory]
    [InlineData(0, SupervisorExitReason.ConsoleCancel)]
    [InlineData(1, SupervisorExitReason.ConsoleBreak)]
    [InlineData(2, SupervisorExitReason.ConsoleClose)]
    [InlineData(5, SupervisorExitReason.ConsoleLogoff)]
    [InlineData(6, SupervisorExitReason.ConsoleShutdown)]
    public void ConsoleControl_MapsTheFiveEvents(int controlType, SupervisorExitReason expected)
    {
        Assert.Equal(expected, ExitTrail.FromConsoleControl(controlType));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(-1)]
    public void ConsoleControl_LeavesEveryOtherEventAlone(int controlType)
    {
        Assert.Null(ExitTrail.FromConsoleControl(controlType));
    }

    [Fact]
    public void Priority_KeepsTheStrongestReasonForOneExit()
    {
        // A console close is a fact that already happened.
        Assert.True(ExitTrail.Priority(SupervisorExitReason.ConsoleClose)
            > ExitTrail.Priority(SupervisorExitReason.UnhandledException));
        // A crash is never reported as a plain return or a runtime teardown.
        Assert.True(ExitTrail.Priority(SupervisorExitReason.UnhandledException)
            > ExitTrail.Priority(SupervisorExitReason.ProcessExit));
        Assert.True(ExitTrail.Priority(SupervisorExitReason.ProcessExit)
            > ExitTrail.Priority(SupervisorExitReason.Normal));
        // A requested stop is stronger than the loop ending by itself.
        Assert.True(ExitTrail.Priority(SupervisorExitReason.ClientRequest)
            > ExitTrail.Priority(SupervisorExitReason.Normal));
        // An idle timeout, if one is ever added, outranks a plain return too.
        Assert.True(ExitTrail.Priority(SupervisorExitReason.Idle)
            > ExitTrail.Priority(SupervisorExitReason.Normal));
        Assert.Equal(
            ExitTrail.Priority(SupervisorExitReason.ConsoleShutdown),
            ExitTrail.Priority(SupervisorExitReason.ConsoleLogoff));
    }
}
