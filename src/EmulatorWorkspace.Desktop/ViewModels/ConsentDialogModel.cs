using System.IO;
using System.Security.Cryptography;

namespace EmulatorWorkspace.Desktop.ViewModels;

/// <summary>
/// UX-09: the facts a privileged consent dialog prints. Every value comes from the
/// supervisor's device record, from the file the operator selected, or from the
/// fixed policy copy of the action itself - the dialog has no sample data - and
/// the whole model is built before the window opens, so nothing about the surface
/// can change what is consented to.
/// </summary>
public sealed class ConsentDialogModel
{
    /// <summary>The question, e.g. "Install Magisk lab on this guest?".</summary>
    public required string Title { get; init; }

    /// <summary>One sentence naming what the action changes.</summary>
    public required string Subtitle { get; init; }

    /// <summary>"err" | "warn" | "accent" for the header tile.</summary>
    public string TileTone { get; init; } = "err";

    public required string GuestName { get; init; }

    /// <summary>"emulator-5554 · x86_64 · android-35;google_apis".</summary>
    public required string GuestMeta { get; init; }

    public string GuestMetaTooltip { get; init; } = "";

    /// <summary>The supervisor's own state word, never colour alone.</summary>
    public string StateLabel { get; init; } = "";

    /// <summary>"ok" | "warn" | "err" | "neutral" for the state chip.</summary>
    public string StateTone { get; init; } = "neutral";

    /// <summary>The files the action will hand the supervisor; empty when it takes none.</summary>
    public IReadOnlyList<ConsentArtifact> Artifacts { get; init; } = Array.Empty<ConsentArtifact>();

    /// <summary>Risks, safeguards and irreversible effects, in the action's own words.</summary>
    public IReadOnlyList<ConsentConsequence> Consequences { get; init; } = Array.Empty<ConsentConsequence>();

    public required string AcknowledgeText { get; init; }

    /// <summary>
    /// The guest name the operator must type. Empty when the action only needs the
    /// acknowledgement (the reversible privileged step: ADB root).
    /// </summary>
    public string ConfirmWord { get; init; } = "";

    public bool RequiresTypedTarget => ConfirmWord.Length > 0;

    /// <summary>The confirm button's label, e.g. "Install Magisk lab".</summary>
    public required string ConfirmLabel { get; init; }

    /// <summary>The sentence beside Cancel. Only true when nothing runs before consent.</summary>
    public string FooterNote { get; init; } = "Cancel creates no transaction or journal.";

    public IReadOnlyList<string> ArtifactRoles => Artifacts.Select(artifact => artifact.Role).ToArray();
}

/// <summary>One consequence row: the glyph tone and the sentence.</summary>
public sealed record ConsentConsequence(string Tone, string Text);

/// <summary>
/// One file the action is about to hand the supervisor, with the SHA-256 read from
/// the file itself. A file that cannot be read keeps an empty hash and says so; the
/// dialog never invents a digest.
/// </summary>
public sealed record ConsentArtifact(string Role, string FileName, string Path, string Hash)
{
    public string HashText => Hash.Length == 0
        ? "sha256 not read"
        : "sha256 " + Hash[..Math.Min(12, Hash.Length)] + "…";

    public string HashTip => Hash.Length == 0
        ? "The file could not be read to compute its hash."
        : "sha256 " + Hash;

    /// <summary>Reads the role, the leaf name and the digest of one selected file.</summary>
    public static async Task<ConsentArtifact> ReadAsync(string role, string path)
    {
        var hash = "";
        try
        {
            await using var stream = File.OpenRead(path);
            var digest = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            hash = Convert.ToHexString(digest).ToLowerInvariant();
        }
        catch (Exception)
        {
            // An unreadable file is a fact the dialog can print; it is not a
            // reason to change the consent decision.
        }

        return new ConsentArtifact(role, System.IO.Path.GetFileName(path), path, hash);
    }
}
