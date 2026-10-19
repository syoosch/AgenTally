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
    private const int MaxEnvelopeBytes = 256 * 1024;
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

        try
        {
            byte[] compact = BoundedJsonEnvelope.Read(
                source, byteOffset, byteLength,
                CodexProjectIdentity.MaxProjectPathCharacters, MaxEnvelopeBytes,
                cancellationToken);
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
                if (value.ValueEquals(BoundedJsonEnvelope.OmittedString))
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
}
