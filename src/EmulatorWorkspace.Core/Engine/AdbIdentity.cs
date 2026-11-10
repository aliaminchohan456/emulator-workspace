namespace EmulatorWorkspace.Core.Engine;

/// <summary>One transport as reported by the pinned ADB server.</summary>
public sealed record AdbTransportInfo(
    string Serial,
    string State,
    string? TransportId,
    string? Model,
    string? Device,
    string? Product);

/// <summary>
/// Parses the read-only "adb devices -l" listing so a managed serial can be
/// matched to the exact transport the server resolves. Parsing never touches
/// any transport; it only reads the listing.
/// </summary>
public static class AdbIdentity
{
    public static AdbTransportInfo? Find(string? listing, string serial)
    {
        if (string.IsNullOrEmpty(listing))
        {
            return null;
        }

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals(serial, StringComparison.Ordinal))
            {
                continue;
            }

            string? transportId = null;
            string? model = null;
            string? device = null;
            string? product = null;
            for (var index = 2; index < parts.Length; index++)
            {
                var token = parts[index];
                var separator = token.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var key = token[..separator];
                var value = token[(separator + 1)..];
                if (key.Equals("transport_id", StringComparison.OrdinalIgnoreCase))
                {
                    transportId = value;
                }
                else if (key.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    model = value;
                }
                else if (key.Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    device = value;
                }
                else if (key.Equals("product", StringComparison.OrdinalIgnoreCase))
                {
                    product = value;
                }
            }

            return new AdbTransportInfo(parts[0], parts[1], transportId, model, device, product);
        }

        return null;
    }
}
