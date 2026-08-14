using AgenTally.Domain.Sources;

namespace AgenTally.Domain.Usage;

public sealed record UsageEvent
{
    public UsageEvent(
        string agentId,
        string sourceInstanceId,
        string sourceEntityId,
        string eventId,
        string dedupKey,
        SourceKind sourceKind,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset importedAtUtc,
        ModelIdentity model,
        TokenUsage tokens,
        CompletionState completionState,
        DataQuality dataQuality,
        string parserVersion,
        string sourceFingerprint,
        long sourceRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);

        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceKind),
                sourceKind,
                "未知的来源类型。");
        }

        if (!Enum.IsDefined(completionState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionState),
                completionState,
                "未知的完成状态。");
        }

        if (!Enum.IsDefined(dataQuality))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataQuality),
                dataQuality,
                "未知的数据质量。");
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("事件发生时间必须使用 UTC。", nameof(occurredAtUtc));
        }

        if (importedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("导入时间必须使用 UTC。", nameof(importedAtUtc));
        }

        if (sourceRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceRevision),
                sourceRevision,
                "来源修订号不能为负数。");
        }

        AgentId = agentId;
        SourceInstanceId = sourceInstanceId;
        SourceEntityId = sourceEntityId;
        EventId = eventId;
        DedupKey = dedupKey;
        SourceKind = sourceKind;
        OccurredAtUtc = occurredAtUtc;
        ImportedAtUtc = importedAtUtc;
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        CompletionState = completionState;
        DataQuality = dataQuality;
        ParserVersion = parserVersion;
        SourceFingerprint = sourceFingerprint;
        SourceRevision = sourceRevision;
    }

    public string AgentId { get; }

    public string SourceInstanceId { get; }

    public string SourceEntityId { get; }

    public string EventId { get; }

    public string DedupKey { get; }

    public SourceKind SourceKind { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset ImportedAtUtc { get; }

    public ModelIdentity Model { get; }

    public TokenUsage Tokens { get; }

    public CompletionState CompletionState { get; }

    public DataQuality DataQuality { get; }

    public string ParserVersion { get; }

    public string SourceFingerprint { get; }

    public long SourceRevision { get; }

    public string? SessionId { get; init; }

    public string? ParentSessionId { get; init; }

    public string? TurnIdHash { get; init; }

    public string? ProjectId { get; init; }

    public string? ProjectPath { get; init; }

    public string? ProjectRepositoryIdentityHash { get; init; }

    public decimal? ReportedCost { get; init; }

    public string? Currency { get; init; }
}
