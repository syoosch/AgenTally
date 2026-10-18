namespace AgenTally.Core.Collectors.ClaudeCode;

public sealed record ClaudeCodeTurnState(
    DateTimeOffset StartedAtUtc,
    string? PromptPreview,
    int UserMessageCount);

public sealed record ClaudeCodeToolState(
    int Ordinal,
    string Name);

public sealed record ClaudeCodeCallState(
    string SessionId,
    string Model,
    long Input,
    long CacheRead,
    long CacheWrite,
    long Output,
    bool HasUsage,
    string? InvalidDiagnosticCode,
    bool Terminal,
    string? TurnIdHash,
    DateTimeOffset OccurredAtUtc,
    string? ProjectId,
    string? ProjectPath,
    string? ProjectRepositoryIdentityHash,
    long LastSourceRevision,
    long EmittedSourceRevision,
    int NextToolOrdinal,
    Dictionary<string, ClaudeCodeToolState> Tools);

public sealed record ClaudeCodeAgentDispatchState(
    string SessionId,
    string? TurnIdHash,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<string> AgentToolUseIdHashes);

public sealed record ClaudeCodeParseState(
    string? SessionId = null,
    string? SourceSessionId = null,
    string? Entrypoint = null,
    int RecognizedRecordCount = 0,
    string? SessionName = null,
    bool HasCustomSessionName = false,
    string? ProjectId = null,
    string? ProjectPath = null,
    string? ProjectRepositoryIdentityHash = null,
    string? SubagentHash = null,
    DateTimeOffset? LastTimestampUtc = null,
    Dictionary<string, string?>? NodeTurnIds = null,
    Dictionary<string, ClaudeCodeTurnState>? Turns = null,
    Dictionary<string, ClaudeCodeCallState>? Calls = null,
    Dictionary<string, ClaudeCodeAgentDispatchState>? AgentDispatches = null);
