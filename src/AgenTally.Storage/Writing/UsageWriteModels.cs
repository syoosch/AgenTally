using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Storage.Writing;

public enum WriteIntent
{
    Normal = 0,
    ParserRepair = 1
}

public sealed record StoredCursor(
    string SourceInstanceId,
    string SourceEntityId,
    string SourcePath,
    string CursorJson,
    string SourceFingerprint,
    string ParserVersion,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc)
{
    public long? EventRevisionHighWatermark { get; init; }
}

public sealed record UsageEventBatch(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    string CursorJson,
    string SourceFingerprint,
    string ParserVersion,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<UsageEvent> Events,
    WriteIntent Intent = WriteIntent.Normal)
{
    public long? EventRevisionHighWatermark { get; init; }

    public IReadOnlyList<UsageSessionMetadata> Sessions { get; init; } = [];

    public IReadOnlyList<UsageTurnMetadata> Turns { get; init; } = [];

    public IReadOnlyList<UsageEventToolMetadata> EventTools { get; init; } = [];

    public IReadOnlyList<UsageTurnDispatch> Dispatches { get; init; } = [];
}

public sealed record SourceInstanceParserState(
    bool HasDerivedData,
    bool RequiresRebuild)
{
    public bool RequiresRescan => RequiresRebuild;
}

public sealed record StoredUsageSourceEntity(
    string SourceInstanceId,
    string SourceEntityId);

public sealed record WriteResult(int AppliedCount, int IgnoredCount);
