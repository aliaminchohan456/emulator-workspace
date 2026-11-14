using Xunit;

namespace EmulatorWorkspace.Tests;

/// <summary>Report Windows-specific assertions as skipped on other platforms.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows file-sharing enforcement.";
    }
}
