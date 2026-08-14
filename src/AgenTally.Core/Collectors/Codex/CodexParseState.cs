namespace AgenTally.Core.Collectors.Codex;

using AgenTally.Domain.Usage;

public sealed record CodexTokenCounters(
    long? Input,
    long? CachedInput,
    long? Output,
    long? Reasoning,
    long? CacheWrite,
    long? Total);

public sealed record CodexReplayTargetState(
    string SessionId,
    string? ParentSessionId,
    string? ForkedFromSessionId,
    SessionKind SessionKind,
    SessionRelationOrigin ParentRelationOrigin,
    SessionRelationState ParentRelationState,
    CompatibilityLevel CompatibilityLevel,
    SessionRole SessionRole,
    string? AgentPathHash,
    string? AgentLeafHash,
    string? CurrentRawModel,
    string? CurrentProviderId,
    string? ProjectId,
    string? ProjectPath,
    string? ProjectRepositoryIdentityHash,
    string? ReplaySourceProjectPath);

public sealed record CodexParseState(
    string? ThreadId = null,
    string? ParentSessionId = null,
    string? CurrentRawModel = null,
    string? CurrentProviderId = null,
    string? ProjectId = null,
    string? ProjectPath = null,
    CodexTokenCounters? PreviousCumulative = null,
    long TokenEventIndex = 0,
    bool IsHistoryReplay = false,
    string? CurrentTurnIdHash = null,
    string? CurrentEffort = null,
    DateTimeOffset? CurrentTurnTimestampUtc = null,
    SessionKind SessionKind = SessionKind.Unknown,
    string? ForkedFromSessionId = null,
    SessionRelationOrigin ParentRelationOrigin = SessionRelationOrigin.None,
    SessionRelationState ParentRelationState = SessionRelationState.None,
    CompatibilityLevel CompatibilityLevel = CompatibilityLevel.FullyCompatible,
    SessionRole SessionRole = SessionRole.Unknown,
    string? AgentPathHash = null,
    string? AgentLeafHash = null,
    DateTimeOffset? CurrentTurnStartedAtUtc = null,
    DateTimeOffset? CurrentTurnCompletedAtUtc = null,
    string? CurrentPromptPreview = null,
    int CurrentUserMessageCount = 0,
    string[]? PendingToolNames = null,
    string? ProjectRepositoryIdentityHash = null,
    CodexReplayTargetState? ReplayTarget = null,
    bool IsReplayTargetContextPending = false)
{
    public ReplayState ReplayState =>
        IsHistoryReplay ? ReplayState.HistoryReplay : ReplayState.Active;
}
