using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Storage.Writing;

public sealed record SourceInstanceMaintenanceState(
    SourceInstanceDescriptor Instance,
    string AcceptedParserVersion,
    CompatibilityLevel CompatibilityLevel = CompatibilityLevel.FullyCompatible,
    string? CompatibilityCode = null);

public interface IUsageWriter
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<StoredCursor?> GetCursorAsync(
        string sourceInstanceId,
        string sourceEntityId,
        CancellationToken cancellationToken);

    Task<SourceInstanceParserState> GetSourceInstanceParserStateAsync(
        SourceInstanceDescriptor instance,
        string requiredParserVersion,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support reading source parser state.");

    Task<IReadOnlyList<StoredUsageSourceEntity>>
        GetSourceEntitiesWithUsageEventsAsync(
            string agentId,
            CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support reading stored usage source entities.");

    Task<WriteResult> CommitAsync(
        UsageEventBatch batch,
        CancellationToken cancellationToken);

    Task SynchronizeSessionNamesAsync(
        SourceInstanceDescriptor instance,
        IReadOnlyList<UsageSessionNameMetadata> sessionNames,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task ResetSourceInstanceAsync(
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support resetting derived source data.");

    Task ReplaceSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support atomic staged source replacement.");

    Task MergeSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        string acceptedParserVersion,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support atomic staged source merge.");

    Task MergeSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceMaintenanceState> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support atomic multi-parser staged source merge.");

    Task ClearSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        string acceptedParserVersion,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support clear-to-baseline.");

    Task ClearAllStatisticsFromStagingAsync(
        IReadOnlyList<SourceInstanceMaintenanceState> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support all-source clear-to-baseline.");

    Task SetSourceCompatibilityAsync(
        SourceInstanceDescriptor instance,
        CompatibilityLevel compatibilityLevel,
        string? compatibilityCode,
        bool requiresRescan,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "This usage writer does not support source compatibility state.");

    Task RecordFailureAsync(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        string error,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);
}
