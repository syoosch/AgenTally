using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors.Jsonl;

namespace AgenTally.Core.Collectors.Codex;

// Compaction snapshots repeat context; they are not new calls or user input.
// Preserve the normal parser's no-op semantics without materializing that context.
internal sealed class CodexCompactedLineProjector : IJsonlOversizedLineProjector
{
    internal const long MaxSourceBytes = 32L * 1024 * 1024;

    public byte[]? TryProject(
        Stream source, long byteOffset, long byteLength,
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
                maxStringCharacters: 128, maxEnvelopeBytes: 256 * 1024,
                cancellationToken);
            using JsonDocument document = JsonDocument.Parse(compact,
                new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String || !type.ValueEquals("compacted") ||
                !root.TryGetProperty("payload", out JsonElement payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                // A payload type can be a replay boundary in the normal parser.
                // Unproved variants retain the shared size rejection.
                payload.TryGetProperty("type", out _))
            {
                return null;
            }

            return "{\"type\":\"compacted\",\"payload\":{}}"u8.ToArray();
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}
