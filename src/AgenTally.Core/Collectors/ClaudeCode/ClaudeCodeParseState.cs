namespace AgenTally.Core.Collectors.ClaudeCode;

public sealed record ClaudeCodeParseState(
    string? SessionId = null,
    string? ProjectId = null,
    string? ProjectPath = null,
    string? ProjectRepositoryIdentityHash = null,
    string? CurrentTurnIdHash = null,
    DateTimeOffset? CurrentTurnStartedAtUtc = null,
    string? CurrentPromptPreview = null,
    int CurrentUserMessageCount = 0,
    DateTimeOffset? LastTimestampUtc = null);
