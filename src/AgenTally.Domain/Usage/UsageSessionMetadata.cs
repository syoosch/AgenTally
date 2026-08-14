namespace AgenTally.Domain.Usage;

public enum SessionKind
{
    Unknown = 0,
    Primary = 1,
    Side = 2
}

public enum SessionRelationOrigin
{
    None = 0,
    TopLevelParentThreadId = 1,
    NestedSubagentParentThreadId = 2,
    SessionIdFallback = 3,
    SourceAgentParent = 4
}

public enum SessionRelationState
{
    None = 0,
    Confirmed = 1,
    Uncertain = 2
}

public enum ReplayState
{
    Active = 0,
    HistoryReplay = 1
}

public enum CompatibilityLevel
{
    FullyCompatible = 0,
    PartiallyCompatible = 1,
    TemporarilyIncompatible = 2,
    MissingCapability = 3
}

public sealed record UsageSessionMetadata
{
    public UsageSessionMetadata(
        string agentId,
        string sourceInstanceId,
        string sourceEntityId,
        string sessionId,
        SessionKind sessionKind,
        string? directParentSessionId,
        string? forkedFromSessionId,
        SessionRelationOrigin relationOrigin,
        SessionRelationState relationState,
        ReplayState replayState,
        CompatibilityLevel compatibilityLevel,
        DateTimeOffset observedAtUtc,
        string parserVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);

        if (!Enum.IsDefined(sessionKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sessionKind));
        }

        if (!Enum.IsDefined(relationOrigin))
        {
            throw new ArgumentOutOfRangeException(nameof(relationOrigin));
        }

        if (!Enum.IsDefined(relationState))
        {
            throw new ArgumentOutOfRangeException(nameof(relationState));
        }

        if (!Enum.IsDefined(replayState))
        {
            throw new ArgumentOutOfRangeException(nameof(replayState));
        }

        if (!Enum.IsDefined(compatibilityLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(compatibilityLevel));
        }

        if (relationState is SessionRelationState.Confirmed &&
            (string.IsNullOrWhiteSpace(directParentSessionId) ||
             relationOrigin is SessionRelationOrigin.None))
        {
            throw new ArgumentException(
                "A confirmed session relation requires a direct parent and origin.",
                nameof(directParentSessionId));
        }

        if (relationState is not SessionRelationState.Confirmed &&
            (directParentSessionId is not null ||
             relationOrigin is not SessionRelationOrigin.None))
        {
            throw new ArgumentException(
                "Only a confirmed session relation may retain a direct parent.",
                nameof(directParentSessionId));
        }

        if (string.Equals(
                sessionId,
                directParentSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A session cannot be its own direct parent.",
                nameof(directParentSessionId));
        }

        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Session observation time must use UTC.",
                nameof(observedAtUtc));
        }

        AgentId = agentId;
        SourceInstanceId = sourceInstanceId;
        SourceEntityId = sourceEntityId;
        SessionId = sessionId;
        SessionKind = sessionKind;
        DirectParentSessionId = directParentSessionId;
        ForkedFromSessionId = forkedFromSessionId;
        RelationOrigin = relationOrigin;
        RelationState = relationState;
        ReplayState = replayState;
        CompatibilityLevel = compatibilityLevel;
        ObservedAtUtc = observedAtUtc;
        ParserVersion = parserVersion;
    }

    public string AgentId { get; }

    public string SourceInstanceId { get; }

    public string SourceEntityId { get; }

    public string SessionId { get; }

    public SessionKind SessionKind { get; }

    public string? DirectParentSessionId { get; }

    public string? ForkedFromSessionId { get; }

    public SessionRelationOrigin RelationOrigin { get; }

    public SessionRelationState RelationState { get; }

    public ReplayState ReplayState { get; }

    public CompatibilityLevel CompatibilityLevel { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public string ParserVersion { get; }

    public string? ProjectId { get; init; }

    public string? ProjectPath { get; init; }

    public string? ProjectRepositoryIdentityHash { get; init; }

    public SessionRole SessionRole { get; init; } = SessionRole.Unknown;

    public string? AgentPathHash { get; init; }

    public string? AgentLeafHash { get; init; }

    public string? SessionName { get; init; }

    public DateTimeOffset? SessionNameUpdatedAtUtc { get; init; }
}
