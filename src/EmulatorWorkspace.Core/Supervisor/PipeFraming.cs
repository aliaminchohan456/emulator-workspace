using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmulatorWorkspace.Contracts;

namespace EmulatorWorkspace.Core.Supervisor;

/// <summary>Length-prefixed JSON framing shared by the supervisor and clients.</summary>
public static class PipeFraming
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32,
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (json.Length > SupervisorProtocol.MaxResponseBytes)
        {
            throw new InvalidOperationException("Supervisor message exceeds the maximum frame size.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonObject?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read != 4)
        {
            throw new IOException("Truncated supervisor frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxBytes)
        {
            throw new IOException($"Invalid supervisor frame length {length} (max {maxBytes}).");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(payload, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        return node as JsonObject ?? throw new IOException("Supervisor frame was not a JSON object.");
    }

    public static string Describe(JsonObject? message)
    {
        if (message is null)
        {
            return "<null>";
        }

        return message.ToJsonString(JsonOptions);
    }
}

public static class JsonArgs
{
    public static string? String(JsonObject? args, string name)
    {
        if (args is null || !args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
    }

    public static double? Double(JsonObject? args, string name)
    {
        if (args?[name] is not { } node)
        {
            return null;
        }

        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            return node.GetValue<double>();
        }

        if (node.GetValueKind() == System.Text.Json.JsonValueKind.String
            && double.TryParse(
                node.GetValue<string>(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static int? Int(JsonObject? args, string name)
    {
        var text = String(args, name);
        return int.TryParse(text, out var value) ? value : null;
    }

    public static bool Bool(JsonObject? args, string name, bool fallback = false)
    {
        var text = String(args, name);
        return bool.TryParse(text, out var value) ? value : fallback;
    }
}
