using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Pricing;

namespace AgenTally.Storage.Queries;

public sealed record UsageFilter
{
    public UsageFilter(
        DateTimeOffset startInclusiveUtc,
        DateTimeOffset endExclusiveUtc,
        string? agentId = null,
        string? normalizedModel = null,
        int limit = 200,
        int offset = 0,
        string? projectId = null,
        string? rootSessionId = null,
        bool unidentifiedProjectOnly = false,
        RootSessionIdentity? rootIdentity = null)
    {
        if (startInclusiveUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "筛选起始时间必须使用 UTC。",
                nameof(startInclusiveUtc));
        }

        if (endExclusiveUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "筛选结束时间必须使用 UTC。",
                nameof(endExclusiveUtc));
        }

        if (endExclusiveUtc <= startInclusiveUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endExclusiveUtc));
        }

        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        StartInclusiveUtc = startInclusiveUtc;
        EndExclusiveUtc = endExclusiveUtc;
        AgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
        NormalizedModel = string.IsNullOrWhiteSpace(normalizedModel)
            ? null
            : normalizedModel;
        Limit = limit;
        Offset = offset;
        ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId;
        string? normalizedRootSessionId = string.IsNullOrWhiteSpace(rootSessionId)
            ? null
            : rootSessionId;
        if (rootIdentity is not null &&
            normalizedRootSessionId is not null &&
            !string.Equals(
                normalizedRootSessionId,
                rootIdentity.RootSessionId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Root-session identity does not match the requested session ID.",
                nameof(rootIdentity));
        }

        RootIdentity = rootIdentity;
        RootSessionId = rootIdentity?.RootSessionId ?? normalizedRootSessionId;
        if (unidentifiedProjectOnly && ProjectId is not null)
        {
            throw new ArgumentException(
                "未识别项目筛选不能同时指定项目标识。",
                nameof(unidentifiedProjectOnly));
        }

        UnidentifiedProjectOnly = unidentifiedProjectOnly;
    }

    public DateTimeOffset StartInclusiveUtc { get; }

    public DateTimeOffset EndExclusiveUtc { get; }

    public string? AgentId { get; }

    public string? NormalizedModel { get; }

    public int Limit { get; }

    public int Offset { get; }

    public string? ProjectId { get; }

    public string? RootSessionId { get; }

    public RootSessionIdentity? RootIdentity { get; }

    public bool UnidentifiedProjectOnly { get; }
}

public enum MetricCoverageStatus
{
    NoData = 0,
    Complete = 1,
    Partial = 2,
    Unknown = 3,
    Unavailable = 4
}

public sealed record MetricAggregate(
    long? Value,
    int AvailableRecords,
    int UnavailableRecords,
    int UnknownRecords = 0)
{
    public MetricCoverageStatus Coverage =>
        AvailableRecords > 0
            ? UnavailableRecords > 0 || UnknownRecords > 0
                ? MetricCoverageStatus.Partial
                : MetricCoverageStatus.Complete
            : UnknownRecords > 0
                ? MetricCoverageStatus.Unknown
                : UnavailableRecords > 0
                    ? MetricCoverageStatus.Unavailable
                    : MetricCoverageStatus.NoData;
}

public sealed record UsageMetricSet(
    MetricAggregate InputReported,
    MetricAggregate UncachedInput,
    MetricAggregate CacheRead,
    MetricAggregate CacheWrite,
    MetricAggregate Output,
    MetricAggregate Reasoning,
    MetricAggregate Tool,
    MetricAggregate ReportedTotal,
    MetricAggregate NormalizedTotal);

public sealed record UsageOverview(
    long RequestCount,
    MetricAggregate NormalizedTotal,
    MetricAggregate UncachedInput,
    MetricAggregate Output,
    MetricAggregate CacheRead,
    MetricAggregate CacheWrite,
    DateTimeOffset? LastOccurredAtUtc)
{
    public DateTimeOffset? FirstOccurredAtUtc { get; init; }

    public UsageMetricSet? Metrics { get; init; }

    public PricingAggregate? Pricing { get; init; }
}

public sealed record UsageTrendPoint(
    DateTimeOffset BucketStartUtc,
    MetricAggregate NormalizedTotal,
    MetricAggregate UncachedInput,
    MetricAggregate Output,
    MetricAggregate CacheRead,
    MetricAggregate CacheWrite,
    long RequestCount = 0)
{
    public UsageMetricSet? Metrics { get; init; }

    public PricingAggregate? Pricing { get; init; }
}

public sealed record UsageRecordRow(
    string SourceInstanceId,
    string SourceEntityId,
    string EventId,
    DateTimeOffset OccurredAtUtc,
    string AgentId,
    string Model,
    long? NormalizedTotal,
    long? UncachedInput,
    long? Output,
    long? CacheRead,
    long? CacheWrite,
    CompletionState CompletionState,
    DataQuality DataQuality)
{
    public EventPriceEstimate? Pricing { get; init; }
}

public sealed record ModelUsageRow(
    string Model,
    long RequestCount,
    MetricAggregate NormalizedTotal,
    MetricAggregate UncachedInput,
    MetricAggregate Output,
    MetricAggregate CacheRead,
    MetricAggregate CacheWrite)
{
    public UsageMetricSet? Metrics { get; init; }

    public PricingAggregate? Pricing { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? LastActivityUtc { get; init; }
}

public sealed record AgentUsageRow(
    string AgentId,
    long RequestCount,
    MetricAggregate NormalizedTotal,
    MetricAggregate UncachedInput,
    MetricAggregate Output,
    MetricAggregate CacheRead,
    MetricAggregate CacheWrite)
{
    public UsageMetricSet? Metrics { get; init; }

    public PricingAggregate? Pricing { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? LastActivityUtc { get; init; }

    public AgentUsageRow(
        string agentId,
        long requestCount,
        MetricAggregate normalizedTotal)
        : this(
            agentId,
            requestCount,
            normalizedTotal,
            UnavailableAggregate(),
            UnavailableAggregate(),
            UnavailableAggregate(),
            UnavailableAggregate())
    {
    }

    private static MetricAggregate UnavailableAggregate() => new(null, 0, 0);
}

public sealed record AgentModelUsageRow(
    string AgentId,
    string Model,
    long RequestCount,
    MetricAggregate NormalizedTotal,
    MetricAggregate UncachedInput,
    MetricAggregate Output,
    MetricAggregate CacheRead,
    MetricAggregate CacheWrite)
{
    public UsageMetricSet? Metrics { get; init; }

    public PricingAggregate? Pricing { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? LastActivityUtc { get; init; }
}

public sealed record UsageFilterValues(
    IReadOnlyList<string> AgentIds,
    IReadOnlyList<string> Models)
{
    public IReadOnlyList<ProjectFilterValue> Projects { get; init; } = [];
}

public sealed record SourceStatusRow(
    string SourceInstanceId,
    string SourceEntityId,
    string AgentId,
    SourceKind SourceKind,
    string DisplayName,
    string RootPath,
    string SourcePath,
    string? ParserVersion,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc)
{
    public CompatibilityLevel CompatibilityLevel { get; init; } =
        CompatibilityLevel.FullyCompatible;

    public string? CompatibilityCode { get; init; }

    public bool RequiresRescan { get; init; }
}

public enum PathAvailability
{
    Available = 0,
    Unavailable = 1
}

public sealed record ProjectFilterValue(
    string ProjectId,
    string? ProjectPath,
    PathAvailability PathAvailability);

public enum PriceSettingSource
{
    BuiltInDefault = 0,
    CustomOverride = 1,
    Unpriced = 2
}

public sealed record PriceSettingRow(
    string NormalizedModel,
    ModelPriceRate? BuiltInRate,
    ModelPriceRate? CustomRate,
    long ObservedRecords)
{
    public ModelPriceRate? EffectiveRate => CustomRate ?? BuiltInRate;

    public PriceSettingSource Source => CustomRate is not null
        ? PriceSettingSource.CustomOverride
        : BuiltInRate is not null
            ? PriceSettingSource.BuiltInDefault
            : PriceSettingSource.Unpriced;
}

public sealed record RootSessionIdentity
{
    public RootSessionIdentity(
        string agentId,
        string sourceInstanceId,
        string rootSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        AgentId = agentId;
        SourceInstanceId = sourceInstanceId;
        RootSessionId = rootSessionId;
    }

    public string AgentId { get; }

    public string SourceInstanceId { get; }

    public string RootSessionId { get; }
}

public sealed record RootSessionCursor(
    DateTimeOffset LastActivityUtc,
    RootSessionIdentity Identity);

public sealed record RootSessionPageRequest
{
    public RootSessionPageRequest(
        UsageFilter filter,
        int pageSize = 50,
        RootSessionCursor? after = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (after is not null && after.LastActivityUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Root-session cursor time must use UTC.",
                nameof(after));
        }

        Filter = filter;
        PageSize = pageSize;
        After = after;
    }

    public UsageFilter Filter { get; }

    public int PageSize { get; }

    public RootSessionCursor? After { get; }
}

public sealed record RootSessionSummaryRow(
    RootSessionIdentity Identity,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc,
    string? ProjectId,
    string? ProjectPath,
    PathAvailability ProjectPathAvailability,
    long RequestCount,
    int SideSessionCount,
    UsageMetricSet Metrics)
{
    public string RootSessionId => Identity.RootSessionId;

    public string AgentId => Identity.AgentId;

    public string SourceInstanceId => Identity.SourceInstanceId;

    public string? SessionName { get; init; }

    public PricingAggregate? Pricing { get; init; }
}

public sealed record RootSessionPage(
    IReadOnlyList<RootSessionSummaryRow> Items,
    RootSessionCursor? NextCursor);

public sealed record SessionModelUsageRow(
    string Model,
    long RequestCount,
    UsageMetricSet Metrics)
{
    public PricingAggregate? Pricing { get; init; }
}

public sealed record SessionContributionRow(
    string SessionId,
    string? DirectParentSessionId,
    SessionKind SessionKind,
    int Depth,
    long RequestCount,
    UsageMetricSet Metrics,
    IReadOnlyList<SessionModelUsageRow> Models)
{
    public PricingAggregate? Pricing { get; init; }

    public SessionRole SessionRole { get; init; } = SessionRole.Unknown;
}

public sealed record RootSessionDetail(
    RootSessionSummaryRow Summary,
    IReadOnlyList<SessionContributionRow> Contributions)
{
    public IReadOnlyList<SessionModelUsageRow> Models { get; init; } = [];
}

public sealed record ProjectUsageRow(
    string ProjectId,
    string? ProjectPath,
    PathAvailability PathAvailability,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc,
    long RequestCount,
    int RootSessionCount,
    UsageMetricSet Metrics)
{
    public const string UnidentifiedProjectId = "__unidentified_project__";

    public PricingAggregate? Pricing { get; init; }

    public bool IsUnidentified { get; init; }
}

public enum TurnCoverageStatus
{
    NoData = 0,
    Complete = 1,
    Partial = 2,
    Unsupported = 3
}

public sealed record TurnUsageRow(
    string TurnIdHash,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastActivityUtc,
    long CallCount,
    UsageMetricSet Metrics)
{
    public PricingAggregate? Pricing { get; init; }

    public string? PromptPreview { get; init; }

    public int UserMessageCount { get; init; }

    public long ToolCallCount { get; init; }

    public long MaxPromptTokens { get; init; }
}

public sealed record TurnCallUsageRow(
    DateTimeOffset OccurredAtUtc,
    string Model,
    string SessionId,
    SessionKind SessionKind,
    SessionRole SessionRole,
    IReadOnlyList<string> Tools,
    UsageMetricSet Metrics)
{
    public PricingAggregate? Pricing { get; init; }
}

public sealed record UnattributedUsageSummary(
    long CallCount,
    UsageMetricSet Metrics)
{
    public PricingAggregate? Pricing { get; init; }
}

public sealed record TurnUsagePage(
    TurnCoverageStatus Coverage,
    IReadOnlyList<TurnUsageRow> Turns,
    UnattributedUsageSummary Unattributed,
    long PromptTurnCount = 0);
