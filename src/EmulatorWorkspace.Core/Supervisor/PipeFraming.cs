using System.Buffers;
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

    /// <summary>
    /// EV-09: the reader side of the same contract - the wire is camelCase and
    /// every envelope was read case-insensitively. One instance is shared by the
    /// desktop connection and the supervisor session so neither spends a
    /// <see cref="JsonSerializerOptions"/> per message.
    /// </summary>
    public static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (json.Length > SupervisorProtocol.MaxResponseBytes)
        {
            throw new InvalidOperationException("Supervisor message exceeds the maximum frame size.");
        }

        // Header and body go out as one buffer. Two separate writes let a second
        // writer land between them, which the reader sees as a bad frame length.
        var buffer = ArrayPool<byte>.Shared.Rent(json.Length + 4);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, json.Length);
            json.CopyTo(buffer.AsSpan(4));
            await stream.WriteAsync(buffer.AsMemory(0, json.Length + 4), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<JsonObject?> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var length = await ReadLengthAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
        if (length < 0)
        {
            return null;
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(payload, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        return node as JsonObject ?? throw new IOException("Supervisor frame was not a JSON object.");
    }

    /// <summary>
    /// EV-09: the same frame, but the payload comes back as a lease over an
    /// <see cref="ArrayPool{T}"/>-rented buffer instead of a fresh <c>byte[]</c>
    /// and a <see cref="JsonNode"/> document that is then written back to a
    /// string and parsed again. The caller reads or deserialises the payload and
    /// disposes the lease, which returns the buffer; a typed deserialize copies
    /// everything it keeps, so the lease can be returned as soon as the frame
    /// has been dispatched. A <see cref="JsonNode"/> parsed straight from
    /// <see cref="PipeFrame.Payload"/> must not outlive the lease: the buffer
    /// goes back to the pool and may be handed to the next frame.
    /// </summary>
    public static async Task<PipeFrame?> ReadAsync(
        Stream stream,
        int maxBytes,
        ArrayPool<byte> pool,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var length = await ReadLengthAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
        if (length < 0)
        {
            return null;
        }

        var buffer = pool.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pool.Return(buffer);
            throw;
        }

        return new PipeFrame(buffer, length, pool);
    }

    /// <summary>
    /// EV-09: reads only the top-level <c>type</c> and <c>id</c> of a frame,
    /// without building a document and without writing the frame back to a
    /// string. It walks the root object and skips every other value, so a frame
    /// that is not a JSON object fails with the same <see cref="IOException"/>
    /// the document parse produced, and malformed JSON still surfaces as a
    /// <see cref="JsonException"/>. A scalar that is not a string is returned in
    /// its JSON text form, which is what the old document read returned.
    /// </summary>
    public static (string? Type, string? Id) PeekHead(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 32 });
        if (!reader.Read())
        {
            // No token at all (an empty or whitespace payload) is malformed JSON,
            // which is what the document parse reported for it.
            throw new JsonException("The frame did not contain a JSON token.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new IOException("Supervisor frame was not a JSON object.");
        }

        string? type = null;
        string? id = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                // EndObject, or a token order that cannot occur in valid JSON.
                break;
            }

            var isType = reader.ValueTextEquals("type"u8);
            var isId = !isType && reader.ValueTextEquals("id"u8);
            reader.Read();

            if (isType)
            {
                type ??= ScalarText(ref reader);
            }
            else if (isId)
            {
                id ??= ScalarText(ref reader);
            }

            // A head that is not a scalar is skipped so the walk stays on the
            // root object's property names and a later property is still seen.
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return (type, id);
    }

    public static string Describe(JsonObject? message)
    {
        if (message is null)
        {
            return "<null>";
        }

        return message.ToJsonString(JsonOptions);
    }

    private static string? ScalarText(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => null,
    };

    /// <summary>The frame's byte length, or -1 for a clean end of stream.</summary>
    private static async ValueTask<int> ReadLengthAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return -1;
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

        return length;
    }
}

/// <summary>
/// A frame payload borrowed from an <see cref="ArrayPool{T}"/>. Disposing returns
/// the buffer; the payload must be consumed before that and nothing parsed
/// directly from it may outlive it.
/// </summary>
public sealed class PipeFrame : IDisposable
{
    private readonly byte[] _buffer;
    private readonly ArrayPool<byte> _pool;
    private int _returned;

    internal PipeFrame(byte[] buffer, int length, ArrayPool<byte> pool)
    {
        _buffer = buffer;
        _pool = pool;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Payload => _buffer.AsMemory(0, Length);

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            _pool.Return(_buffer);
        }
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
