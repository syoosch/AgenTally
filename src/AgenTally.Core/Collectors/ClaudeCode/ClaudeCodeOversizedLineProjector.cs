using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;

namespace AgenTally.Core.Collectors.ClaudeCode;

// Only source-proved non-Prompt user envelopes may bypass the shared line limit.
// Large string payloads are validated and compacted before bounded DOM parsing;
// only current Parser metadata is returned to the collector.
internal sealed class ClaudeCodeOversizedLineProjector : IJsonlOversizedLineProjector
{
    internal const long MaxSourceBytes = 32L * 1024 * 1024;
    private const int BufferBytes = 64 * 1024;
    private const int MaxEnvelopeBytes = 256 * 1024;
    private const string OmittedString = "\0agentally:oversized-string";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RootFields =
    [
        "type", "entrypoint", "sessionId", "timestamp", "agentId",
        "isSidechain", "isMeta", "cwd", "uuid", "parentUuid",
        "sourceToolAssistantUUID"
    ];
    private static readonly string[] AssistantFields =
        ["id", "model", "usage", "stop_reason", "stop_details", "error"];
    private static readonly string[] BlockFields =
        ["type", "tool_use_id"];

    public byte[]? TryProject(
        Stream source,
        long byteOffset,
        long byteLength,
        CancellationToken cancellationToken)
    {
        if (!source.CanSeek || byteOffset < 0 ||
            byteLength is <= 0 or > MaxSourceBytes)
        {
            return null;
        }

        long originalPosition = source.Position;
        try
        {
            source.Seek(byteOffset, SeekOrigin.Begin);
            using var range = new RangeStream(source, byteLength, cancellationToken);
            using var reader = new StreamReader(
                range, StrictUtf8, detectEncodingFromByteOrderMarks: false,
                bufferSize: BufferBytes, leaveOpen: true);
            byte[] compact = CompactStrings(reader);
            using JsonDocument document = JsonDocument.Parse(compact,
                new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !IsString(root, "type", "user") ||
                !(IsTrue(root, "isSidechain") || IsTrue(root, "isMeta")) ||
                !root.TryGetProperty("message", out JsonElement message) ||
                message.ValueKind != JsonValueKind.Object ||
                !IsString(message, "role", "user") ||
                AssistantFields.Any(field => message.TryGetProperty(field, out _)))
            {
                return null;
            }

            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                WriteFields(writer, root, RootFields);
                writer.WritePropertyName("message");
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                if (message.TryGetProperty("content", out JsonElement content))
                {
                    writer.WritePropertyName("content");
                    WriteContent(writer, content);
                }

                writer.WriteEndObject();
                if (root.TryGetProperty("toolUseResult", out JsonElement result))
                {
                    writer.WritePropertyName("toolUseResult");
                    if (result.ValueKind == JsonValueKind.Object)
                    {
                        writer.WriteStartObject();
                        WriteFields(writer, result, ["agentId"]);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        WriteOpaqueValue(writer, result);
                    }
                }

                writer.WriteEndObject();
            }

            return output.Length <= MaxEnvelopeBytes ? output.ToArray() : null;
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            source.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static bool IsString(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String &&
        property.ValueEquals(expected);

    private static bool IsTrue(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.True;

    private static void WriteFields(
        Utf8JsonWriter writer, JsonElement value, string[] fields)
    {
        foreach (string field in fields)
        {
            // TryGetProperty selects the last duplicate, as in the normal Parser.
            if (value.TryGetProperty(field, out JsonElement property))
            {
                writer.WritePropertyName(field);
                WriteMetadataValue(writer, property);
            }
        }
    }

    private static void WriteMetadataValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (value.ValueEquals(OmittedString))
                {
                    throw new JsonException("Oversized retained metadata.");
                }

                value.WriteTo(writer);
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void WriteContent(Utf8JsonWriter writer, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(string.Empty);
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    WriteFields(writer, block, BlockFields);
                    writer.WriteEndObject();
                }
                else
                {
                    WriteOpaqueValue(writer, block);
                }
            }

            writer.WriteEndArray();
        }
        else
        {
            WriteOpaqueValue(writer, content);
        }
    }

    private static void WriteOpaqueValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            writer.WriteStringValue(string.Empty);
        }
        else
        {
            WriteMetadataValue(writer, value);
        }
    }

    private static byte[] CompactStrings(StreamReader reader)
    {
        using var output = new MemoryStream();
        int next;
        while ((next = reader.Read()) >= 0)
        {
            if (next == '"')
            {
                byte[] token = JsonSerializer.SerializeToUtf8Bytes(ReadString(reader));
                if (output.Length + token.Length > MaxEnvelopeBytes)
                {
                    throw new JsonException("Oversized compact envelope.");
                }

                output.Write(token);
            }
            else
            {
                if (next > 127 || output.Length == MaxEnvelopeBytes)
                {
                    throw new JsonException("Invalid or oversized compact envelope.");
                }

                output.WriteByte((byte)next);
            }
        }

        return output.ToArray();
    }

    private static string ReadString(StreamReader reader)
    {
        var text = new StringBuilder();
        bool omitted = false;
        while (true)
        {
            int next = reader.Read();
            if (next == '"')
            {
                return omitted ? OmittedString : text.ToString();
            }

            char first = ReadStringUnit(reader, next);
            char? second = null;
            if (char.IsHighSurrogate(first))
            {
                second = ReadStringUnit(reader, reader.Read());
                if (!char.IsLowSurrogate(second.Value))
                {
                    throw new JsonException("Invalid JSON surrogate pair.");
                }
            }
            else if (char.IsLowSurrogate(first))
            {
                throw new JsonException("Invalid JSON surrogate pair.");
            }

            if (!omitted)
            {
                int units = second.HasValue ? 2 : 1;
                if (text.Length + units > CodexProjectIdentity.MaxProjectPathCharacters)
                {
                    omitted = true;
                    text.Clear();
                }
                else
                {
                    text.Append(first);
                    if (second.HasValue) { text.Append(second.Value); }
                }
            }
        }
    }

    private static char ReadStringUnit(StreamReader reader, int next)
    {
        if (next < 0x20 || next == '"')
        {
            throw new JsonException("Incomplete or invalid JSON string.");
        }

        if (next != '\\') { return (char)next; }
        return reader.Read() switch
        {
            '"' => '"', '\\' => '\\', '/' => '/',
            'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
            'u' => ReadUnicodeEscape(reader),
            _ => throw new JsonException("Invalid JSON escape.")
        };
    }

    private static char ReadUnicodeEscape(StreamReader reader)
    {
        int value = 0;
        for (int index = 0; index < 4; index++)
        {
            int next = reader.Read();
            int digit = next switch
            {
                >= '0' and <= '9' => next - '0',
                >= 'a' and <= 'f' => next - 'a' + 10,
                >= 'A' and <= 'F' => next - 'A' + 10,
                _ => throw new JsonException("Invalid JSON Unicode escape.")
            };
            value = (value << 4) | digit;
        }

        return (char)value;
    }

    private sealed class RangeStream(
        Stream source, long remaining, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining == 0) { return 0; }
            int read = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            if (read == 0) { throw new JsonException("Incomplete source range."); }
            remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
