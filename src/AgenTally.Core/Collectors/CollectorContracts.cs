using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors;

public enum CollectionReason
{
    StartupImport,
    FileChanged,
    ManualRequest,
    PeriodicAudit,
    RepairScan
}

public sealed record CollectorContext(
    string UserProfilePath,
    TimeProvider TimeProvider);

public sealed record CollectorDiagnostic(
    string Code,
    string Message,
    string? SourceEntityId = null,
    long? ByteOffset = null);

public sealed record SourceProbeResult(
    IReadOnlyList<SourceInstanceDescriptor> Instances,
    IReadOnlyList<SourceEntityDescriptor> Entities,
    IReadOnlyList<CollectorDiagnostic> Diagnostics);

public sealed record CollectionRequest(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    StoredCursor? Cursor,
    CollectionReason Reason);

public sealed record CollectedBatch(
    SourceInstanceDescriptor Instance,
    SourceEntityDescriptor Entity,
    IReadOnlyList<UsageEvent> Events,
    string NextCursorJson,
    string SourceFingerprint,
    string ParserVersion,
    IReadOnlyList<CollectorDiagnostic> Diagnostics)
{
    public long? EventRevisionHighWatermark { get; init; }

    public IReadOnlyList<UsageSessionMetadata> Sessions { get; init; } = [];

    public IReadOnlyList<UsageTurnMetadata> Turns { get; init; } = [];

    public IReadOnlyList<UsageEventToolMetadata> EventTools { get; init; } = [];

    public IReadOnlyList<UsageTurnDispatch> Dispatches { get; init; } = [];
}
