namespace AgenTally.Domain.Usage;

public enum SessionRole
{
    Unknown = 0,
    Main = 1,
    Subagent = 2,
    Guardian = 3,
    Internal = 4
}

public enum TurnDispatchKind
{
    Spawn = 0,
    FollowUp = 1
}

public enum DispatchTargetKind
{
    AgentPath = 0,
    AgentLeaf = 1
}

public enum TurnAttributionOrigin
{
    Direct = 0,
    Spawn = 1,
    FollowUp = 2,
    GuardianInterval = 3,
    SourceParentInterval = 4,
    GoalContinuation = 5
}

public enum TurnAttributionState
{
    Confirmed = 0,
    Uncertain = 1
}

public sealed record UsageTurnMetadata
{
    public UsageTurnMetadata(
        string agentId,
        string sourceInstanceId,
        string sourceEntityId,
        string sessionId,
        string turnIdHash,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? promptPreview,
        int userMessageCount,
        string parserVersion,
        string? promptOriginTurnIdHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidateHash(turnIdHash, nameof(turnIdHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        ValidateUtc(startedAtUtc, nameof(startedAtUtc));
        if (completedAtUtc.HasValue)
        {
            ValidateUtc(completedAtUtc.Value, nameof(completedAtUtc));
        }

        if (userMessageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userMessageCount));
        }

        if (promptOriginTurnIdHash is not null)
        {
            ValidateHash(
                promptOriginTurnIdHash,
                nameof(promptOriginTurnIdHash));
        }

        if (promptPreview is { Length: > 240 } ||
            promptPreview?.Any(char.IsControl) is true)
        {
            throw new ArgumentException(
                "Prompt preview is invalid.",
                nameof(promptPreview));
        }

        AgentId = agentId;
        SourceInstanceId = sourceInstanceId;
        SourceEntityId = sourceEntityId;
        SessionId = sessionId;
        TurnIdHash = turnIdHash;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        PromptPreview = promptPreview;
        UserMessageCount = userMessageCount;
        ParserVersion = parserVersion;
        PromptOriginTurnIdHash = promptOriginTurnIdHash;
    }

    public string AgentId { get; }

    public string SourceInstanceId { get; }

    public string SourceEntityId { get; }

    public string SessionId { get; }

    public string TurnIdHash { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public string? PromptPreview { get; }

    public int UserMessageCount { get; }

    public string ParserVersion { get; }

    public string? PromptOriginTurnIdHash { get; }

    internal static void ValidateHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 ||
            value.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Hashed identifier is invalid.",
                parameterName);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use UTC.", parameterName);
        }
    }
}

public sealed record UsageEventToolMetadata
{
    public UsageEventToolMetadata(
        string agentId,
        string sourceInstanceId,
        string sourceEntityId,
        string eventDedupKey,
        int ordinal,
        string toolName,
        string parserVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntityId);
        UsageTurnMetadata.ValidateHash(eventDedupKey, nameof(eventDedupKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        if (toolName.Length > 128 || toolName.Any(char.IsControl))
        {
            throw new ArgumentException("Tool name is invalid.", nameof(toolName));
        }

        AgentId = agentId;
        SourceInstanceId = sourceInstanceId;
        SourceEntityId = sourceEntityId;
        EventDedupKey = eventDedupKey;
        Ordinal = ordinal;
        ToolName = toolName;
        ParserVersion = parserVersion;
    }

    public string AgentId { get; }

    public string SourceInstanceId { get; }

    public string SourceEntityId { get; }

    public string EventDedupKey { get; }

    public int Ordinal { get; }

    public string ToolName { get; }

    public string ParserVersion { get; }
}

public sealed record UsageTurnDispatch
{
    public UsageTurnDispatch(
        string agentId,
        string sourceInstanceId,
        string sourceEntityId,
        string sourceSessionId,
        string sourceTurnIdHash,
        string dispatchIdHash,
        string targetAgentHash,
        TurnDispatchKind dispatchKind,
        DispatchTargetKind targetKind,
        DateTimeOffset occurredAtUtc,
        string parserVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSessionId);
        UsageTurnMetadata.ValidateHash(sourceTurnIdHash, nameof(sourceTurnIdHash));
        UsageTurnMetadata.ValidateHash(dispatchIdHash, nameof(dispatchIdHash));
        UsageTurnMetadata.ValidateHash(targetAgentHash, nameof(targetAgentHash));
        if (!Enum.IsDefined(dispatchKind))
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchKind));
        }

        if (!Enum.IsDefined(targetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(targetKind));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must use UTC.",
                nameof(occurredAtUtc));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        AgentId = agentId;
        SourceInstanceId = sourceInstanceId;
        SourceEntityId = sourceEntityId;
        SourceSessionId = sourceSessionId;
        SourceTurnIdHash = sourceTurnIdHash;
        DispatchIdHash = dispatchIdHash;
        TargetAgentHash = targetAgentHash;
        DispatchKind = dispatchKind;
        TargetKind = targetKind;
        OccurredAtUtc = occurredAtUtc;
        ParserVersion = parserVersion;
    }

    public string AgentId { get; }

    public string SourceInstanceId { get; }

    public string SourceEntityId { get; }

    public string SourceSessionId { get; }

    public string SourceTurnIdHash { get; }

    public string DispatchIdHash { get; }

    public string TargetAgentHash { get; }

    public TurnDispatchKind DispatchKind { get; }

    public DispatchTargetKind TargetKind { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string ParserVersion { get; }
}
