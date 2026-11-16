namespace EmulatorWorkspace.Core.Frames;

/// <summary>
/// EV-11: how a frame subscriber wants its frames. <see cref="File"/> is the
/// original behaviour and stays the default, so the CLI and the MCP client keep
/// working unchanged; <see cref="Shared"/> reads the per-instance ring and lets
/// the supervisor skip the per-frame file write.
/// </summary>
public static class FrameDelivery
{
    public const string File = "file";
    public const string Shared = "shared";

    /// <summary>The delivery a request asked for; an absent or unknown value is a file subscriber.</summary>
    public static string Parse(string? value) =>
        string.Equals(value, Shared, StringComparison.OrdinalIgnoreCase) ? Shared : File;

    public static bool WantsFiles(string? value) => Parse(value) == File;
}
