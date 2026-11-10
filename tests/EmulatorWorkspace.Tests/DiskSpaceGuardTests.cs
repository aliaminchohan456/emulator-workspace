using EmulatorWorkspace.Core.Supervisor;
using Xunit;

namespace EmulatorWorkspace.Tests;

public sealed class DiskSpaceGuardTests
{
    [Fact]
    public void Low_free_space_is_rejected_with_low_disk_code()
    {
        try
        {
            DiskSpaceGuard.SetOverrideForTests(_ => 1L * 1024 * 1024 * 1024);
            var error = Assert.Throws<SupervisorOperationException>(
                () => DiskSpaceGuard.Ensure(@"C:\workspace", 8L * 1024 * 1024 * 1024, "start a guest"));
            Assert.Equal("low_disk", error.Code);
            Assert.Contains("GiB free", error.Message);
        }
        finally
        {
            DiskSpaceGuard.SetOverrideForTests(null);
        }
    }

    [Fact]
    public void Sufficient_free_space_passes()
    {
        try
        {
            DiskSpaceGuard.SetOverrideForTests(_ => 100L * 1024 * 1024 * 1024);
            DiskSpaceGuard.Ensure(@"C:\workspace", 8L * 1024 * 1024 * 1024, "start a guest");
        }
        finally
        {
            DiskSpaceGuard.SetOverrideForTests(null);
        }
    }
}
