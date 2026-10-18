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
        16_777_216;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
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
            !ValidOptionalString(State.SourceSessionId) ||
            !ValidOptionalString(State.Entrypoint) ||
            State.RecognizedRecordCount < 0 ||
            !ValidPreview(State.SessionName) ||
            (State.HasCustomSessionName && State.SessionName is null) ||
            !ValidProject(State) ||
            !ValidHash(State.SubagentHash) ||
            !ValidUtc(State.LastTimestampUtc) ||
            !ValidIndexes(State) ||
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

    private static bool ValidIndexes(ClaudeCodeParseState state)
    {
        if (state.NodeTurnIds is { Count: > 50_000 } ||
            state.Turns is { Count: > 10_000 } ||
            state.Calls is { Count: > 20_000 } ||
            state.AgentDispatches is { Count: > 5_000 })
        {
            return false;
        }

        if (state.NodeTurnIds is not null && state.NodeTurnIds.Any(value =>
                !ValidHash(value.Key) || !ValidHash(value.Value)))
        {
            return false;
        }

        if (state.Turns is not null && state.Turns.Any(value =>
                !ValidHash(value.Key) ||
                !ValidUtc(value.Value.StartedAtUtc) ||
                !ValidPreview(value.Value.PromptPreview) ||
                value.Value.UserMessageCount < 0))
        {
            return false;
        }

        if (state.Calls is not null && state.Calls.Any(value =>
                !ValidHash(value.Key) ||
                !ValidCall(value.Value)))
        {
            return false;
        }

        return state.AgentDispatches is null ||
            state.AgentDispatches.All(value =>
                ValidHash(value.Key) &&
                ValidOptionalString(value.Value.SessionId) &&
                ValidHash(value.Value.TurnIdHash) &&
                ValidUtc(value.Value.OccurredAtUtc) &&
                value.Value.AgentToolUseIdHashes is { Count: <= 1024 } &&
                value.Value.AgentToolUseIdHashes.All(ValidHash));
    }

    private static bool ValidCall(ClaudeCodeCallState call)
    {
        if (!ValidOptionalString(call.SessionId) ||
            string.IsNullOrWhiteSpace(call.Model) ||
            call.Model.Length > 128 ||
            call.Model.Any(char.IsControl) ||
            call.Input < 0 || call.CacheRead < 0 || call.CacheWrite < 0 ||
            call.Output < 0 || !ValidHash(call.TurnIdHash) ||
            !ValidUtc(call.OccurredAtUtc) ||
            call.LastSourceRevision < 0 ||
            call.EmittedSourceRevision < -1 ||
            call.EmittedSourceRevision > call.LastSourceRevision ||
            call.NextToolOrdinal < 0 ||
            call.Tools is null || call.Tools.Count > 10_000)
        {
            return false;
        }

        var ordinals = new HashSet<int>();
        return call.Tools.All(value =>
            ValidHash(value.Key) &&
            value.Value.Ordinal >= 0 &&
            value.Value.Ordinal < call.NextToolOrdinal &&
            ordinals.Add(value.Value.Ordinal) &&
            !string.IsNullOrWhiteSpace(value.Value.Name) &&
            value.Value.Name.Length <= 128 &&
            !value.Value.Name.Any(char.IsControl));
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
