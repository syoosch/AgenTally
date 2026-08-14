using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Hosting;

internal sealed class SerializedUsageWriter : IUsageWriter
{
    private readonly IUsageWriter _inner;
    private readonly CoreDatabaseWriteGate _gate;

    public SerializedUsageWriter(
        IUsageWriter inner,
        CoreDatabaseWriteGate gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public Task<StoredCursor?> GetCursorAsync(
        string sourceInstanceId,
        string sourceEntityId,
        CancellationToken cancellationToken) =>
        _inner.GetCursorAsync(
            sourceInstanceId,
            sourceEntityId,
            cancellationToken);

    public Task<SourceInstanceParserState> GetSourceInstanceParserStateAsync(
        SourceInstanceDescriptor instance,
        string requiredParserVersion,
        CancellationToken cancellationToken) =>
        _inner.GetSourceInstanceParserStateAsync(
            instance,
            requiredParserVersion,
            cancellationToken);

    public Task<IReadOnlyList<StoredUsageSourceEntity>>
        GetSourceEntitiesWithUsageEventsAsync(
            string agentId,
            CancellationToken cancellationToken) =>
        _inner.GetSourceEntitiesWithUsageEventsAsync(
            agentId,
            cancellationToken);

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        RunAsync(token => _inner.InitializeAsync(token), cancellationToken);

    public Task<WriteResult> CommitAsync(
        UsageEventBatch batch,
        CancellationToken cancellationToken) =>
        RunAsync(token => _inner.CommitAsync(batch, token), cancellationToken);

    public Task SynchronizeSessionNamesAsync(
        SourceInstanceDescriptor instance,
        IReadOnlyList<UsageSessionNameMetadata> sessionNames,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.SynchronizeSessionNamesAsync(
                instance,
                sessionNames,
                token),
            cancellationToken);

    public Task ResetSourceInstanceAsync(
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.ResetSourceInstanceAsync(instance, token),
            cancellationToken);

    public Task ReplaceSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.ReplaceSourceInstancesFromStagingAsync(
                instances,
                stagingDatabasePath,
                token),
            cancellationToken);

    public Task MergeSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        string acceptedParserVersion,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.MergeSourceInstancesFromStagingAsync(
                instances,
                stagingDatabasePath,
                acceptedParserVersion,
                token),
            cancellationToken);

    public Task MergeSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceMaintenanceState> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.MergeSourceInstancesFromStagingAsync(
                instances,
                stagingDatabasePath,
                token),
            cancellationToken);

    public Task ClearSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        string acceptedParserVersion,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.ClearSourceInstancesFromStagingAsync(
                instances,
                stagingDatabasePath,
                acceptedParserVersion,
                token),
            cancellationToken);

    public Task ClearAllStatisticsFromStagingAsync(
        IReadOnlyList<SourceInstanceMaintenanceState> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.ClearAllStatisticsFromStagingAsync(
                instances,
                stagingDatabasePath,
                token),
            cancellationToken);

    public Task SetSourceCompatibilityAsync(
        SourceInstanceDescriptor instance,
        CompatibilityLevel compatibilityLevel,
        string? compatibilityCode,
        bool requiresRescan,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.SetSourceCompatibilityAsync(
                instance,
                compatibilityLevel,
                compatibilityCode,
                requiresRescan,
                token),
            cancellationToken);

    public Task RecordFailureAsync(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        string error,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.RecordFailureAsync(
                instance,
                entity,
                error,
                failedAtUtc,
                token),
            cancellationToken);

    private async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using IDisposable lease = await _gate.EnterAsync(cancellationToken);
        await operation(cancellationToken);
    }

    private async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using IDisposable lease = await _gate.EnterAsync(cancellationToken);
        return await operation(cancellationToken);
    }
}
