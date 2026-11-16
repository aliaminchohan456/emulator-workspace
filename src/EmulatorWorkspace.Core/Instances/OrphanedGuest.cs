namespace EmulatorWorkspace.Core.Instances;

/// <summary>
/// UX-18: the guest that outlived its engine process.
///
/// The engine is a wrapper: on Windows it starts the real emulator process
/// (qemu) as a child. When the wrapper dies on its own, the guest it started
/// keeps running and keeps the instance's reserved ports (the console, the
/// ADB port and the controller port). The supervisor, which tracks the
/// wrapper's process, then sees an instance that is gone - while the ports it
/// needs are still busy, and the next start fails with a port conflict that
/// names a stranger.
///
/// The guest is only ever touched when it answers as *this* instance's AVD on
/// *this* instance's serial: the console reports the AVD name it serves, and
/// it has to match the record. An emulator that belongs to somebody else is
/// never addressed.
/// </summary>
public static class OrphanedGuest
{
    /// <summary>
    /// The AVD name the emulator console reported, or null when the answer
    /// holds no name.
    ///
    /// The console answers with the name, a blank line and "OK"; the discovery
    /// path answer ends in ".ini". Neither of those is a name.
    /// </summary>
    public static string? ReadConsoleAvdName(string? consoleOutput)
    {
        foreach (var line in (consoleOutput ?? "").Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0
                || value.Equals("OK", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return value;
        }

        return null;
    }

    /// <summary>
    /// Whether the guest that answered is this instance's own guest.
    /// </summary>
    public static bool BelongsTo(string? consoleAvdName, string? recordAvdName) =>
        !string.IsNullOrWhiteSpace(consoleAvdName)
        && !string.IsNullOrWhiteSpace(recordAvdName)
        && string.Equals(consoleAvdName.Trim(), recordAvdName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The message the start path prints when the busy port belongs to this
    /// instance's own leftover guest. It names the instance, the serial and
    /// the one command that clears it.
    /// </summary>
    public static string PortConflictMessage(string instanceName, string serial, int port) =>
        $"Port {port} is held by the previous guest of instance '{instanceName}': it still answers on " +
        $"{serial} as this instance's AVD, although its engine process is gone. Stop the instance first - " +
        $"that clears the leftover guest: wsctl stop --name {instanceName} --force.";
}
