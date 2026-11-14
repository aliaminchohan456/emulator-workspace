using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>Only the reviewed base-only SDK lab inputs are supported.</summary>
public static class MagiskLabArtifacts
{
    public const string Image = "system-images;android-35;default;x86_64";
    public const string Fingerprint = "Android/sdk_phone64_x86_64/emu64x:15/AE3A.240806.019/12368160:userdebug/test-keys";
    public const string StockHash = "60eb16b9c894514f3792c5bef9f58ea478bd1356f6e0fdb8045dca01ab903a30";
    public const string PatchHash = "d5b47070ee8a63bcb80e7851887831e371d55762cec388195552ac09426faf8f";
    public const string ApkHash = "d820319e907a16eecd3239be360def9299b351da1155736ac9a28966092dfd7e";
    public const string ProbeHash = "a6b4837f101c2423d32f801ca6951901f3fb66e2584ad0d59296956bed1e23b1";
    public const string ProbePackage = "dev.emulatorworkspace.suprobe";

    public static readonly IReadOnlyDictionary<string, string> Pins = new Dictionary<string, string>
    {
        ["stock.img"] = StockHash, ["patched.img"] = PatchHash,
        ["manager.apk"] = ApkHash, ["probe.apk"] = ProbeHash,
    };

    public static void NoReparse(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full[Path.GetPathRoot(full)!.Length..].Contains(':'))
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Lab artifacts must be local paths without alternate streams.");
        for (var item = full; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) &&
                (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Lab paths cannot traverse reparse points.");
    }

    public static string Hash(string path)
    {
        NoReparse(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void Verify(string path, string hash)
    {
        if (!File.Exists(path) || Hash(path) != hash)
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, $"Lab artifact hash mismatch: {Path.GetFileName(path)}.");
    }

    public static void CopyVerified(string source, string destination, string hash)
    {
        NoReparse(source); NoReparse(destination);
        Verify(source, hash);
        // Exclusive creation; never overwrite a stock backup or recovery point.
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        Verify(destination, hash);
    }

    /// <summary>Hash the opened file and retain the Windows write/delete exclusion until disposed.</summary>
    public static FileStream LeaseVerified(string path, string hash)
    {
        NoReparse(path);
        if (!File.Exists(path))
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Lab artifact is missing.");
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(lease)).ToLowerInvariant();
            if (actual != hash)
                throw new SupervisorOperationException(ErrorCodes.InvalidRequest, $"Lab artifact hash mismatch: {Path.GetFileName(path)}.");
            lease.Seek(0, SeekOrigin.Begin);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static void WriteAtomic(string path, JsonObject value)
    {
        NoReparse(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var data = Encoding.UTF8.GetBytes(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            stream.Write(data);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public static void StageEnvironment(string apk, string directory)
    {
        Verify(apk, ApkHash);
        NoReparse(directory);
        if (Directory.Exists(directory)) throw new IOException("Environment staging destination already exists.");
        Directory.CreateDirectory(directory);
        using var archive = ZipFile.OpenRead(apk);
        var native = new[] { "busybox", "init-ld", "magisk", "magiskboot", "magiskinit", "magiskpolicy" };
        var assets = new[] { "util_functions.sh", "boot_patch.sh", "addon.d.sh", "stub.apk",
            "chromeos/futility", "chromeos/kernel_data_key.vbprivk", "chromeos/kernel.keyblock" };
        foreach (var name in native.Concat(assets))
        {
            var entry = archive.GetEntry(native.Contains(name) ? $"lib/x86_64/lib{name}.so" : "assets/" + name)
                ?? throw new InvalidDataException("Pinned APK payload is missing " + name);
            var destination = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(true);
        }
    }

    public static void VerifyEnvironment(string apk, string directory)
    {
        Verify(apk, ApkHash); NoReparse(directory);
        var native = new[] { "busybox", "init-ld", "magisk", "magiskboot", "magiskinit", "magiskpolicy" };
        var assets = new[] { "util_functions.sh", "boot_patch.sh", "addon.d.sh", "stub.apk",
            "chromeos/futility", "chromeos/kernel_data_key.vbprivk", "chromeos/kernel.keyblock" };
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var parent))
            foreach (var subdir in Directory.EnumerateDirectories(parent))
            {
                NoReparse(subdir); // Reject junctions before descending into them.
                pending.Push(subdir);
            }
        var expected = native.Concat(assets).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual))
            throw new SupervisorOperationException(ErrorCodes.InvalidRequest, "Lab environment file inventory changed.");
        using var archive = ZipFile.OpenRead(apk);
        foreach (var name in expected)
        {
            using var source = archive.GetEntry(native.Contains(name) ? $"lib/x86_64/lib{name}.so" : "assets/" + name)!.Open();
            Verify(Path.Combine(directory, name), Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant());
        }
    }
}
