namespace EmulatorWorkspace.Core.Workspace;

/// <summary>
/// Resolves the runtime state root. Runtime state is deliberately kept outside
/// the source tree so that multi-gigabyte disk images are never synced by cloud
/// backup clients that watch the repository folder.
/// </summary>
public sealed class WorkspacePaths
{
    public const string HomeEnvironmentVariable = "EMULATOR_WORKSPACE_HOME";

    public WorkspacePaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string InstancesDirectory => Path.Combine(Root, "instances");

    public string AvdHome => Path.Combine(Root, "avd");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string JournalDirectory => Path.Combine(Root, "journal");

    public string EvidenceDirectory => Path.Combine(Root, "evidence");

    public string JournalPath => Path.Combine(JournalDirectory, "operations.jsonl");

    public static WorkspacePaths Resolve(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return new WorkspacePaths(explicitRoot);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new WorkspacePaths(fromEnvironment);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new WorkspacePaths(Path.Combine(localAppData, "EmulatorWorkspace"));
    }

    public void EnsureLayout()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(AvdHome);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(JournalDirectory);
        Directory.CreateDirectory(EvidenceDirectory);
    }

    public string InstancePath(string name) => Path.Combine(InstancesDirectory, name + ".json");

    public string InstanceLogPath(string name) => Path.Combine(LogsDirectory, name + ".engine.log");

    public string InstanceHostLogPath(string name) => Path.Combine(LogsDirectory, name + ".host.log");
}
