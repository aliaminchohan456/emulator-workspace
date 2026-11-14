using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>Validates the pinned probe's structured instrumentation bundle, independently of JUnit counts.</summary>
public static class MagiskProbeEvidence
{
    public static JsonObject Validate(string? output, int expectedUid, bool patched)
    {
        static void Require(bool condition)
        {
            if (!condition) throw new SupervisorOperationException(ErrorCodes.AdbFailure,
                "Missing, malformed or unsuccessful app root probe evidence.");
        }
        Require(expectedUid >= 10000 && output is { Length: > 0 and <= 64000 });
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        var finished = false;
        foreach (var line in output!.Replace("\r", "").Split('\n'))
        {
            if (line.Length == 0) continue;
            Require(!finished);
            if (line == "INSTRUMENTATION_CODE: -1") { finished = true; continue; }
            const string prefix = "INSTRUMENTATION_RESULT: ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var entry = line[prefix.Length..];
                var split = entry.IndexOf('=');
                Require(split > 0);
                key = entry[..split];
                Require(fields.TryAdd(key, entry[(split + 1)..]));
            }
            else
            {
                Require(key is "su_stdout" or "su_stderr" && !line.StartsWith("INSTRUMENTATION", StringComparison.Ordinal));
                fields[key!] += "\n" + line;
            }
        }
        var required = new[] { "caller_package", "caller_uid", "caller_pid", "caller_context", "caller_id", "su_exit", "su_stdout", "su_stderr" };
        Require(finished && fields.Count == required.Length && required.All(fields.ContainsKey));
        Require(fields["caller_package"] == MagiskLabArtifacts.ProbePackage &&
            fields["caller_uid"] == expectedUid.ToString(CultureInfo.InvariantCulture) &&
            int.TryParse(fields["caller_pid"], out var pid) && pid > 0 &&
            Regex.IsMatch(fields["caller_context"], @"\Au:r:untrusted_app:s0(?::c\d+(?:,c\d+)*)?\z") &&
            Regex.IsMatch(fields["caller_id"], @"\Auid=" + expectedUid + @"\([^\r\n()]+\) "));
        Require(patched
            ? fields["su_exit"] == "0" && fields["su_stderr"] == "" &&
              fields["su_stdout"].StartsWith("uid=0(root) gid=0(root) ", StringComparison.Ordinal) &&
              fields["su_stdout"].Split('\n').Contains("context=u:r:magisk:s0")
            : fields["su_exit"] == "exception" && fields["su_stdout"] == "" &&
              fields["su_stderr"].Contains("error=13", StringComparison.Ordinal));
        return new JsonObject { ["callerUid"] = expectedUid, ["callerContext"] = fields["caller_context"],
            ["suExit"] = fields["su_exit"], ["rootGranted"] = patched, ["verified"] = true };
    }
}
