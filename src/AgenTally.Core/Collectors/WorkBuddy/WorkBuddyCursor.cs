using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;

namespace AgenTally.Core.Collectors.WorkBuddy;

public sealed record WorkBuddyCursor(
    JsonlCursor Jsonl,
    WorkBuddyParseState State)
{
    private const int MaxStateStringCharacters = 32_767;
    private const int MaxToolsPerCall = 256;
    private const int MaxTurnRecords = 1024;
    private const int MaxSerializedCursorCharacters =
        JsonlCursor.MaxSerializedCursorCharacters + 131_072;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static WorkBuddyCursor Start { get; } = new(
        JsonlCursor.Start,
        new WorkBuddyParseState());

    public string Serialize()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException(
                "WorkBuddy collection cursor is invalid and cannot be serialized.");
        }

        string json = JsonSerializer.Serialize(this, JsonOptions);
        return json.Length <= MaxSerializedCursorCharacters
            ? json
            : throw new InvalidOperationException(
                "WorkBuddy collection cursor exceeds its serialized size limit.");
    }

    public static WorkBuddyCursor DeserializeOrStart(
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
            WorkBuddyCursor? cursor = JsonSerializer.Deserialize<WorkBuddyCursor>(
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
            !ValidOptionalString(State.SessionId, 1024) ||
            !ValidHash(State.CurrentTurnIdHash) ||
            !ValidUtc(State.CurrentTurnStartedAtUtc) ||
            !ValidPreview(State.CurrentPromptPreview) ||
            State.CurrentUserMessageCount < 0 ||
            !ValidProject(State) ||
            !ValidPreview(State.SessionName) ||
            !ValidUtc(State.SessionNameUpdatedAtUtc) ||
            !ValidTurnRecords(State.CurrentTurnRecordIdHashes) ||
            !ValidTools(State.PendingTools) ||
            !ValidUtc(State.LastTimestampUtc) ||
            !ValidFingerprint(Jsonl))
        {
            return false;
        }

        return Jsonl.ByteOffset != 0 ||
               Jsonl.LineNumber != 0 ||
               IsEmptyState(State);
    }

    private static bool ValidProject(WorkBuddyParseState state)
    {
        if (state.ProjectId is null &&
            state.ProjectPath is null &&
            state.ProjectRepositoryIdentityHash is null)
        {
            return true;
        }

        return state.ProjectId is { Length: CodexProjectIdentity.ProjectIdCharacters } &&
            state.ProjectPath is { Length: > 0 and <= MaxStateStringCharacters } &&
            CodexProjectIdentity.TryCreate(
                state.ProjectPath,
                out CodexProjectIdentity project) &&
            string.Equals(
                state.ProjectId,
                project.ProjectId,
                StringComparison.Ordinal) &&
            state.ProjectRepositoryIdentityHash is null;
    }

    private static bool ValidTools(IReadOnlyList<WorkBuddyToolReference>? tools) =>
        tools is null ||
        (tools.Count <= MaxToolsPerCall &&
         tools.All(static tool =>
             tool is not null &&
             ValidHash(tool.CallIdHash) &&
             tool.Name is { Length: > 0 and <= 128 } &&
             !tool.Name.Any(char.IsControl)) &&
         tools.Select(static tool => tool.CallIdHash).Distinct().Count() == tools.Count);

    private static bool ValidTurnRecords(IReadOnlyList<string>? values) =>
        values is null ||
        (values.Count <= MaxTurnRecords &&
         values.All(ValidHash) &&
         values.Distinct(StringComparer.Ordinal).Count() == values.Count);

    private static bool ValidOptionalString(string? value, int maximum) =>
        value is null ||
        (value.Length is > 0 &&
         value.Length <= maximum &&
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

    private static bool IsEmptyState(WorkBuddyParseState state) =>
        state.SessionId is null &&
        state.CurrentTurnIdHash is null &&
        state.CurrentTurnStartedAtUtc is null &&
        state.CurrentPromptPreview is null &&
        state.CurrentUserMessageCount == 0 &&
        state.ProjectId is null &&
        state.ProjectPath is null &&
        state.ProjectRepositoryIdentityHash is null &&
        state.SessionName is null &&
        state.SessionNameUpdatedAtUtc is null &&
        state.CurrentTurnRecordIdHashes is null &&
        state.PendingTools is null &&
        state.LastTimestampUtc is null;

    private static CollectorDiagnostic InvalidCursorDiagnostic() => new(
        "workbuddy.invalid_cursor",
        "WorkBuddy collection cursor was invalid and has been reset.");
}
