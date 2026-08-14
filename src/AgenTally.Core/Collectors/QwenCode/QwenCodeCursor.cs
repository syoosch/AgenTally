using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors.Jsonl;

namespace AgenTally.Core.Collectors.QwenCode;

public sealed record QwenCodeParseState(
    string? SessionId = null,
    string? TurnIdHash = null,
    DateTimeOffset? TurnStartedAtUtc = null,
    string? PromptPreview = null,
    string? ProjectId = null,
    string? ProjectPath = null,
    string? ProjectRepositoryIdentityHash = null,
    DateTimeOffset? LastTimestampUtc = null);

public sealed record QwenCodeCursor(JsonlCursor Jsonl, QwenCodeParseState State)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static QwenCodeCursor Start { get; } = new(
        JsonlCursor.Start,
        new QwenCodeParseState());

    public string Serialize()
    {
        string json = JsonSerializer.Serialize(this, Options);
        return json.Length <= JsonlCursor.MaxSerializedCursorCharacters + 8192
            ? json
            : throw new InvalidOperationException("Qwen Code collection cursor is too large.");
    }

    public static QwenCodeCursor DeserializeOrStart(
        string? json,
        bool hasStoredCursor,
        out CollectorDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            diagnostic = hasStoredCursor ? Invalid() : null;
            return Start;
        }

        try
        {
            QwenCodeCursor? cursor = JsonSerializer.Deserialize<QwenCodeCursor>(json, Options);
            if (cursor is null || cursor.Jsonl is null || cursor.State is null ||
                !ValidState(cursor.State) ||
                !cursor.Jsonl.TryGetPendingBytes(out _))
            {
                throw new JsonException("Invalid cursor.");
            }
            diagnostic = null;
            return cursor;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException
            or ArgumentException or FormatException)
        {
            diagnostic = Invalid();
            return Start;
        }
    }

    private static bool ValidState(QwenCodeParseState state) =>
        Valid(state.SessionId, 1024) && ValidHash(state.TurnIdHash) &&
        Valid(state.PromptPreview, 240) && Valid(state.ProjectId, 128) &&
        Valid(state.ProjectPath, 32768) && ValidHash(state.ProjectRepositoryIdentityHash) &&
        (!state.TurnStartedAtUtc.HasValue || state.TurnStartedAtUtc.Value.Offset == TimeSpan.Zero) &&
        (!state.LastTimestampUtc.HasValue || state.LastTimestampUtc.Value.Offset == TimeSpan.Zero);

    private static bool Valid(string? value, int maximum) =>
        value is null || (value.Length > 0 && value.Length <= maximum && !value.Any(char.IsControl));

    private static bool ValidHash(string? value) =>
        value is null || (value.Length == 64 && value.All(static c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static CollectorDiagnostic Invalid() => new(
        "qwen-code.invalid_cursor",
        "The Qwen Code collection cursor was invalid and has been reset.");
}
