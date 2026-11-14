using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;
using EmulatorWorkspace.Core.Supervisor;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>Internal lab-only root lifetime; callers must supply exact enrollment validation.</summary>
internal static class ProvisioningAdbRoot
{
    internal static async Task<JsonObject> Run(Action requireEnrollment,
        Func<bool, CancellationToken, Task<JsonObject>> setRoot,
        Func<CancellationToken, Task<JsonObject>> provision,
        Action<JsonObject> journal, CancellationToken ct)
    {
        var proof = new JsonObject { ["rootRequested"] = false, ["unrootAttempted"] = false };
        requireEnrollment();
        proof["rootRequested"] = true;
        journal(proof); // Persist intent before invoking any root command.
        Exception? provisioningError = null;
        try
        {
            var before = await setRoot(true, ct);
            proof["root"] = before.DeepClone();
            journal(proof);
            if (before["uidAfter"]?.GetValue<string>() != "0")
                throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Provisioning adbd did not become root.");
            return await provision(ct);
        }
        catch (Exception ex)
        {
            provisioningError = ex;
            proof["provisioningError"] = ex.Message;
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                proof["unrootAttempted"] = true;
                JsonObject after;
                try { journal(proof); }
                finally { after = await setRoot(false, cleanup.Token); }
                proof["unroot"] = after.DeepClone();
                proof["adbdUid2000Verified"] = after["uidAfter"]?.GetValue<string>() == "2000";
                journal(proof);
                if (proof["adbdUid2000Verified"]!.GetValue<bool>() != true)
                    throw new SupervisorOperationException(ErrorCodes.AdbFailure, "Could not restore adbd UID 2000.");
            }
            catch (Exception ex)
            {
                proof["unrootError"] = ex.Message;
                journal(proof);
                if (provisioningError is not null)
                    throw new AggregateException("Provisioning and unroot both failed.", provisioningError, ex);
                throw;
            }
        }
    }
}
