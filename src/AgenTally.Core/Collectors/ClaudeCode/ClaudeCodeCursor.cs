using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;

namespace AgenTally.Core.Collectors.ClaudeCode;

public sealed record ClaudeCodeCursor(
    JsonlCursor Jsonl,
    ClaudeCodeParseState State)
{
    private const int MaxStateStringCharacters = 1024;
    private const int MaxSerializedCursorCharacters =
        JsonlCursor.MaxSerializedCursorCharacters +
        (CodexProjectIdentity.MaxProjectPathCharacters * 6) +
        16_384;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ClaudeCodeCursor Start { get; } = new(
        JsonlCursor.Start,
        new ClaudeCodeParseState());

    public string Serialize()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException(
                "Claude Code collection cursor is invalid and cannot be serialized.");
        }

        string json = JsonSerializer.Serialize(this, JsonOptions);
        return json.Length <= MaxSerializedCursorCharacters
            ? json
            : throw new InvalidOperationException(
                "Claude Code collection cursor exceeds its serialized size limit.");
    }

    public static ClaudeCodeCursor DeserializeOrStart(
        string? cursorJson,
        bool hasStoredCursor,
        out CollectorDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(cursorJson))
        {
            diagnostic = hasStoredCursor ? InvalidCursorDiagnostic() : null;
            return Start;
        }

        if (cursorJson.Length > MaxSerializedCursorCharacters)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }

        try
        {
            ClaudeCodeCursor? cursor = JsonSerializer.Deserialize<ClaudeCodeCursor>(
                cursorJson,
                JsonOptions);
            if (cursor is null || !cursor.IsValid())
            {
                throw new JsonException("Cursor fields are invalid.");
            }

            diagnostic = null;
            return cursor;
        }
        catch (Exception exception)
            when (exception is JsonException
                or NotSupportedException
                or ArgumentException
                or FormatException)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }
    }

    private bool IsValid()
    {
        if (Jsonl is null ||
            State is null ||
            !Jsonl.TryGetPendingBytes(out _) ||
            !ValidOptionalString(State.SessionId) ||
            !ValidProject(State) ||
            !ValidHash(State.CurrentTurnIdHash) ||
            !ValidUtc(State.CurrentTurnStartedAtUtc) ||
            !ValidPreview(State.CurrentPromptPreview) ||
            State.CurrentUserMessageCount < 0 ||
            !ValidUtc(State.LastTimestampUtc) ||
            !ValidFingerprint(Jsonl))
        {
            return false;
        }

        return Jsonl.ByteOffset != 0 ||
               Jsonl.LineNumber != 0 ||
               State == new ClaudeCodeParseState();
    }

    private static bool ValidProject(ClaudeCodeParseState state)
    {
        if (state.ProjectPath is null)
        {
            return state.ProjectId is null &&
                   state.ProjectRepositoryIdentityHash is null;
        }

        return state.ProjectPath.Length <= CodexProjectIdentity.MaxProjectPathCharacters &&
               CodexProjectIdentity.TryCreate(
                   state.ProjectPath,
                   out CodexProjectIdentity identity) &&
               string.Equals(
                   state.ProjectPath,
                   identity.ProjectPath,
                   StringComparison.Ordinal) &&
               string.Equals(
                   state.ProjectId,
                   identity.ProjectId,
                   StringComparison.Ordinal) &&
               state.ProjectRepositoryIdentityHash is null;
    }

    private static bool ValidOptionalString(string? value) =>
        value is null ||
        (value.Length is > 0 and <= MaxStateStringCharacters &&
         !string.IsNullOrWhiteSpace(value) &&
         !value.Any(char.IsControl));

    private static bool ValidHash(string? value) =>
        value is null ||
        (value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static bool ValidPreview(string? value) =>
        value is null ||
        (value.Length is > 0 and <= 240 && !value.Any(char.IsControl));

    private static bool ValidUtc(DateTimeOffset? value) =>
        !value.HasValue || value.Value.Offset == TimeSpan.Zero;

    private static bool ValidFingerprint(JsonlCursor jsonl) =>
        jsonl == JsonlCursor.Start ||
        (jsonl.SourceFingerprint.Length == 64 &&
         jsonl.SourceFingerprint.All(static character =>
             character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static CollectorDiagnostic InvalidCursorDiagnostic() => new(
        "claude_code.invalid_cursor",
        "Claude Code collection cursor was invalid and has been reset.");
}
