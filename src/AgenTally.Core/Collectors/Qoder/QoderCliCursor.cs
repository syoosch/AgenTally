using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors.Jsonl;

namespace AgenTally.Core.Collectors.Qoder;

public sealed record QoderCliState(
    string? SessionId = null,
    string? TurnIdHash = null,
    DateTimeOffset? TurnStartedAtUtc = null,
    string? PromptPreview = null,
    string? ProjectId = null,
    string? ProjectPath = null,
    string? ProjectRepositoryIdentityHash = null);

public sealed record QoderCliCursor(JsonlCursor Jsonl, QoderCliState State)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static QoderCliCursor Start { get; } = new(JsonlCursor.Start, new QoderCliState());

    public string Serialize()
    {
        string json = JsonSerializer.Serialize(this, Options);
        return json.Length <= JsonlCursor.MaxSerializedCursorCharacters + 8192
            ? json
            : throw new InvalidOperationException("Qoder CLI collection cursor is too large.");
    }

    public static QoderCliCursor DeserializeOrStart(
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
            QoderCliCursor? cursor = JsonSerializer.Deserialize<QoderCliCursor>(json, Options);
            if (cursor is null || cursor.Jsonl is null || cursor.State is null ||
                !cursor.Jsonl.TryGetPendingBytes(out _) || !Valid(cursor.State))
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

    private static bool Valid(QoderCliState state) =>
        Text(state.SessionId, 1024) && Hash(state.TurnIdHash) &&
        Text(state.PromptPreview, 240) && Text(state.ProjectId, 128) &&
        Text(state.ProjectPath, 32768) && Hash(state.ProjectRepositoryIdentityHash) &&
        (!state.TurnStartedAtUtc.HasValue || state.TurnStartedAtUtc.Value.Offset == TimeSpan.Zero);

    private static bool Text(string? value, int maximum) =>
        value is null || (value.Length > 0 && value.Length <= maximum && !value.Any(char.IsControl));

    private static bool Hash(string? value) =>
        value is null || (value.Length == 64 && value.All(static c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static CollectorDiagnostic Invalid() => new(
        "qoder-cli.invalid_cursor",
        "The Qoder CLI collection cursor was invalid and has been reset.");
}
