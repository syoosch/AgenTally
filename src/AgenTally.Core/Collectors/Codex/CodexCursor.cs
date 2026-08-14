using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.Codex;

public sealed record CodexCursor(
    JsonlCursor Jsonl,
    CodexParseState State)
{
    internal const int MaxStateStringCharacters = 1024;
    private const int MaxJsonEscapedCharactersPerUtf16CodeUnit = 6;
    private const int MaxTurnIdHashCharacters = 64;
    private const int MaxSerializedStateStringCharacters =
        MaxStateStringCharacters + // ThreadId
        MaxStateStringCharacters + // ParentSessionId
        MaxStateStringCharacters + // ForkedFromSessionId
        MaxStateStringCharacters + // CurrentRawModel
        MaxStateStringCharacters + // CurrentProviderId
        CodexProjectIdentity.ProjectIdCharacters +
        CodexProjectIdentity.MaxProjectPathCharacters +
        CodexProjectIdentity.RepositoryIdentityHashCharacters +
        MaxTurnIdHashCharacters +
        MaxStateStringCharacters + // CurrentEffort
        MaxTurnIdHashCharacters + // AgentPathHash
        MaxTurnIdHashCharacters + // AgentLeafHash
        240 + // CurrentPromptPreview
        (64 * 128) + // PendingToolNames
        MaxStateStringCharacters + // ReplayTarget.SessionId
        MaxStateStringCharacters + // ReplayTarget.ParentSessionId
        MaxStateStringCharacters + // ReplayTarget.ForkedFromSessionId
        MaxTurnIdHashCharacters + // ReplayTarget.AgentPathHash
        MaxTurnIdHashCharacters + // ReplayTarget.AgentLeafHash
        MaxStateStringCharacters + // ReplayTarget.CurrentRawModel
        MaxStateStringCharacters + // ReplayTarget.CurrentProviderId
        CodexProjectIdentity.ProjectIdCharacters +
        CodexProjectIdentity.MaxProjectPathCharacters +
        CodexProjectIdentity.RepositoryIdentityHashCharacters +
        CodexProjectIdentity.MaxProjectPathCharacters; // ReplayTarget.ReplaySourceProjectPath
    private const int MaxSerializedCursorCharacters =
        JsonlCursor.MaxSerializedCursorCharacters +
        // System.Text.Json may emit one UTF-16 code unit as a six-character \uXXXX escape.
        (MaxSerializedStateStringCharacters *
         MaxJsonEscapedCharactersPerUtf16CodeUnit) +
        2048;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static CodexCursor Start { get; } = new(
        JsonlCursor.Start,
        new CodexParseState());

    public string Serialize()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException(
                "Codex collection cursor is invalid and cannot be serialized.");
        }

        string json = JsonSerializer.Serialize(this, JsonOptions);
        if (json.Length > MaxSerializedCursorCharacters)
        {
            throw new InvalidOperationException(
                "Codex collection cursor exceeds its serialized size limit.");
        }

        return json;
    }

    public static CodexCursor DeserializeOrStart(
        string? cursorJson,
        out CollectorDiagnostic? diagnostic)
        => DeserializeOrStart(cursorJson, hasStoredCursor: false, out diagnostic);

    public static CodexCursor DeserializeOrStart(
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
            CodexCursor? cursor = JsonSerializer.Deserialize<CodexCursor>(
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
            State.TokenEventIndex < 0 ||
            State.TokenEventIndex > Jsonl.LineNumber ||
            !ValidSourceFingerprint(Jsonl) ||
            !ValidOptionalString(State.ThreadId) ||
            !ValidOptionalString(State.ParentSessionId) ||
            !ValidOptionalString(State.ForkedFromSessionId) ||
            !ValidOptionalString(State.CurrentRawModel) ||
            !ValidOptionalString(State.CurrentProviderId) ||
            !ValidProjectIdentity(
                State.ProjectId,
                State.ProjectPath,
                State.ProjectRepositoryIdentityHash) ||
            !ValidTurnIdHash(State.CurrentTurnIdHash) ||
            !ValidOptionalString(State.CurrentEffort) ||
            !ValidUtcTimestamp(State.CurrentTurnTimestampUtc) ||
            !ValidUtcTimestamp(State.CurrentTurnStartedAtUtc) ||
            !ValidUtcTimestamp(State.CurrentTurnCompletedAtUtc) ||
            !ValidTurnIdHash(State.AgentPathHash) ||
            !ValidTurnIdHash(State.AgentLeafHash) ||
            !ValidPromptPreview(State.CurrentPromptPreview) ||
            State.CurrentUserMessageCount < 0 ||
            !ValidPendingTools(State.PendingToolNames) ||
            !ValidReplayTarget(State.ReplayTarget) ||
            (State.IsReplayTargetContextPending &&
             (State.ReplayTarget is null || State.IsHistoryReplay)) ||
            !Enum.IsDefined(State.SessionKind) ||
            !Enum.IsDefined(State.SessionRole) ||
            !Enum.IsDefined(State.ParentRelationOrigin) ||
            !Enum.IsDefined(State.ParentRelationState) ||
            !Enum.IsDefined(State.CompatibilityLevel))
        {
            return false;
        }

        if (Jsonl.ByteOffset == 0 &&
            Jsonl.LineNumber == 0 &&
            State != new CodexParseState())
        {
            return false;
        }

        CodexTokenCounters? counters = State.PreviousCumulative;
        return counters is null ||
            (Nonnegative(counters.Input) &&
             Nonnegative(counters.CachedInput) &&
             Nonnegative(counters.Output) &&
             Nonnegative(counters.Reasoning) &&
             Nonnegative(counters.CacheWrite) &&
             Nonnegative(counters.Total));
    }

    private static bool ValidReplayTarget(CodexReplayTargetState? target)
    {
        if (target is null)
        {
            return true;
        }

        return ValidOptionalString(target.SessionId) &&
            target.SessionId is not null &&
            ValidOptionalString(target.ParentSessionId) &&
            ValidOptionalString(target.ForkedFromSessionId) &&
            ValidTurnIdHash(target.AgentPathHash) &&
            ValidTurnIdHash(target.AgentLeafHash) &&
            ValidOptionalString(target.CurrentRawModel) &&
            ValidOptionalString(target.CurrentProviderId) &&
            ValidProjectIdentity(
                target.ProjectId,
                target.ProjectPath,
                target.ProjectRepositoryIdentityHash) &&
            ValidCanonicalProjectPath(target.ReplaySourceProjectPath) &&
            Enum.IsDefined(target.SessionKind) &&
            Enum.IsDefined(target.SessionRole) &&
            Enum.IsDefined(target.ParentRelationOrigin) &&
            Enum.IsDefined(target.ParentRelationState) &&
            Enum.IsDefined(target.CompatibilityLevel);
    }

    private static bool ValidCanonicalProjectPath(string? projectPath) =>
        projectPath is null ||
        (CodexProjectIdentity.TryCreate(
            projectPath,
            out CodexProjectIdentity identity) &&
         string.Equals(
             projectPath,
             identity.ProjectPath,
             StringComparison.Ordinal));

    private static bool ValidOptionalString(string? value) =>
        value is null ||
        (value.Length is > 0 and <= MaxStateStringCharacters &&
         !string.IsNullOrWhiteSpace(value) &&
         !value.Any(char.IsControl));

    private static bool ValidProjectId(string? value) =>
        value is null ||
        (value.Length == CodexProjectIdentity.ProjectIdCharacters &&
         value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static bool ValidProjectIdentity(
        string? projectId,
        string? projectPath,
        string? repositoryIdentityHash)
    {
        if (projectPath is null)
        {
            // v2/v3 cursors can contain the irreversible ProjectId without a path.
            return repositoryIdentityHash is null && ValidProjectId(projectId);
        }

        if (projectId is null ||
            !CodexProjectIdentity.TryCreate(
                projectPath,
                out CodexProjectIdentity pathIdentity) ||
            !string.Equals(
                projectPath,
                pathIdentity.ProjectPath,
                StringComparison.Ordinal))
        {
            return false;
        }

        return repositoryIdentityHash is null
            ? string.Equals(
                projectId,
                pathIdentity.ProjectId,
                StringComparison.Ordinal)
            : CodexProjectIdentity.IsRepositoryIdentityHash(
                repositoryIdentityHash) &&
            string.Equals(
                projectId,
                repositoryIdentityHash[..CodexProjectIdentity.ProjectIdCharacters],
                StringComparison.Ordinal) &&
            CodexProjectIdentity.TryCreateFromRepositoryHash(
                projectPath,
                repositoryIdentityHash,
                out _);
    }

    private static bool ValidTurnIdHash(string? value) =>
        value is null ||
        (value.Length == MaxTurnIdHashCharacters &&
         value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static bool ValidPromptPreview(string? value) =>
        value is null ||
        (value.Length is > 0 and <= 240 &&
         !value.Any(char.IsControl));

    private static bool ValidPendingTools(string[]? values) =>
        values is null ||
        (values.Length <= 64 &&
         values.All(static value =>
            value.Length is > 0 and <= 128 &&
            !value.Any(char.IsControl)));

    private static bool ValidUtcTimestamp(DateTimeOffset? value) =>
        !value.HasValue || value.Value.Offset == TimeSpan.Zero;

    private static bool ValidSourceFingerprint(JsonlCursor jsonl) =>
        jsonl == JsonlCursor.Start ||
        (jsonl.SourceFingerprint.Length == 64 &&
         jsonl.SourceFingerprint.All(static character =>
             character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static bool Nonnegative(long? value) => !value.HasValue || value.Value >= 0;

    private static CollectorDiagnostic InvalidCursorDiagnostic() => new(
        "codex.invalid_cursor",
        "Codex collection cursor was invalid and has been reset.");
}
