using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Engine;
using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class MagiskProbeEvidenceTests
{
    private const string Valid = """
        INSTRUMENTATION_RESULT: caller_context=u:r:untrusted_app:s0:c149,c256,c512,c768
        INSTRUMENTATION_RESULT: caller_id=uid=10149(u0_a149) gid=10149(u0_a149)
        INSTRUMENTATION_RESULT: caller_package=dev.emulatorworkspace.suprobe
        INSTRUMENTATION_RESULT: caller_pid=1642
        INSTRUMENTATION_RESULT: caller_uid=10149
        INSTRUMENTATION_RESULT: su_exit=0
        INSTRUMENTATION_RESULT: su_stderr=
        INSTRUMENTATION_RESULT: su_stdout=uid=0(root) gid=0(root) groups=0(root) context=u:r:magisk:s0
        context=u:r:magisk:s0
        INSTRUMENTATION_CODE: -1
        """;

    [Fact] public void AcceptsCompleteBundleWithWindowsAdbLineEndings()
        => Assert.True(MagiskProbeEvidence.Validate(Valid.Replace("\n", "\r\r\n"), 10149, true)["verified"]!.GetValue<bool>());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("uid=0(root) caller_uid=10149 su_exit=0 context=u:r:magisk:s0")]
    public void MissingOrUnstructuredOutputFails(string? output)
        => Assert.Throws<SupervisorOperationException>(() => MagiskProbeEvidence.Validate(output, 10149, true));

    [Theory]
    [InlineData("caller_uid=10149", "caller_uid=101490")]
    [InlineData("caller_pid=1642", "caller_pid=unknown")]
    [InlineData("su_exit=0", "su_exit=timeout")]
    [InlineData("INSTRUMENTATION_CODE: -1", "")]
    [InlineData("INSTRUMENTATION_CODE: -1", "INSTRUMENTATION_CODE: 0")]
    [InlineData("caller_uid=10149", "caller_uid=10149\nINSTRUMENTATION_RESULT: caller_uid=10149")]
    [InlineData("caller_context=u:r:untrusted_app:s0:c149,c256,c512,c768", "caller_context=u:r:magisk:s0")]
    public void IncompleteAmbiguousOrUnsuccessfulProofFails(string old, string replacement)
        => Assert.Throws<SupervisorOperationException>(() => MagiskProbeEvidence.Validate(Valid.Replace(old, replacement), 10149, true));

    [Fact] public void StockDenialRequiresExplicitProbeFailure()
    {
        var stock = Valid.Replace("\r", "").Replace("su_exit=0", "su_exit=exception").Replace("su_stderr=", "su_stderr=java.io.IOException: Cannot run program su: error=13, Permission denied")
            .Replace("su_stdout=uid=0(root) gid=0(root) groups=0(root) context=u:r:magisk:s0\ncontext=u:r:magisk:s0", "su_stdout=");
        Assert.True(MagiskProbeEvidence.Validate(stock, 10149, false)["verified"]!.GetValue<bool>());
        Assert.Throws<SupervisorOperationException>(() => MagiskProbeEvidence.Validate(Valid, 10149, false));
    }
}
