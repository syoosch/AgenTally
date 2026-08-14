using System.Security;
using AgenTally.Core.Collectors;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Processing;

public sealed class ImportCoordinator
{
    private const int MaxBufferedBatches = 25;
    private const int MaxEventsPerBatch = 200;
    private const int MaxBufferedEvents = MaxBufferedBatches * MaxEventsPerBatch;

    private readonly IUsageWriter _writer;
    private readonly TimeProvider _timeProvider;

    public ImportCoordinator(
        IUsageWriter writer,
        TimeProvider? timeProvider = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SyncResult> SyncAsync(
        IAgentCollector collector,
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Instance);
        ArgumentNullException.ThrowIfNull(request.Entity);

        if (!string.Equals(
                collector.AgentId,
                request.Instance.AgentId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Collector identity does not match its request.");
        }

        var bufferedBatches = new List<CollectedBatch>();
        var diagnostics = new List<CollectorDiagnostic>();
        int bufferedEvents = 0;

        try
        {
            await foreach (CollectedBatch batch in collector.CollectAsync(
                               request,
                               cancellationToken))
            {
                ValidateBatchIdentity(batch, request);
                if (bufferedBatches.Count >= MaxBufferedBatches ||
                    batch.Events.Count > MaxEventsPerBatch ||
                    bufferedEvents > MaxBufferedEvents - batch.Events.Count)
                {
                    throw new InvalidOperationException(
                        "Collector exceeded the bounded import buffer.");
                }

                bufferedEvents += batch.Events.Count;
                bufferedBatches.Add(batch);
                diagnostics.AddRange(batch.Diagnostics);
            }
        }
        catch (Exception exception) when (IsSourceIoFailure(exception))
        {
            string error = $"Source collection failed ({exception.GetType().Name}).";
            await _writer.RecordFailureAsync(
                request.Instance,
                request.Entity,
                error,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            diagnostics.Add(new CollectorDiagnostic(
                "collector.source_failure",
                error,
                request.Entity.SourceEntityId));
            return new SyncResult(
                false,
                0,
                0,
                diagnostics,
                error);
        }

        int appliedCount = 0;
        int ignoredCount = 0;
        StoredCursor? persistedCursor = request.Cursor;
        foreach (CollectedBatch batch in bufferedBatches)
        {
            if (IsHealthyEmptyNoOp(batch, persistedCursor))
            {
                continue;
            }

            DateTimeOffset checkedAtUtc = _timeProvider.GetUtcNow();
            WriteResult result = await _writer.CommitAsync(
                new UsageEventBatch(
                    batch.Instance,
                    batch.Entity,
                    batch.NextCursorJson,
                    batch.SourceFingerprint,
                    batch.ParserVersion,
                    checkedAtUtc,
                    batch.Events)
                {
                    EventRevisionHighWatermark =
                        batch.EventRevisionHighWatermark,
                    Sessions = batch.Sessions,
                    Turns = batch.Turns,
                    EventTools = batch.EventTools,
                    Dispatches = batch.Dispatches
                },
                cancellationToken);
            appliedCount += result.AppliedCount;
            ignoredCount += result.IgnoredCount;
            persistedCursor = new StoredCursor(
                batch.Instance.SourceInstanceId,
                batch.Entity.SourceEntityId,
                batch.Entity.SourcePath,
                batch.NextCursorJson,
                batch.SourceFingerprint,
                batch.ParserVersion,
                checkedAtUtc,
                null,
                null)
            {
                EventRevisionHighWatermark = batch.EventRevisionHighWatermark
            };
        }

        return new SyncResult(
            true,
            appliedCount,
            ignoredCount,
            diagnostics,
            null);
    }

    private static bool IsHealthyEmptyNoOp(
        CollectedBatch batch,
        StoredCursor? persistedCursor) =>
        persistedCursor is not null &&
        persistedCursor.LastError is null &&
        persistedCursor.LastErrorAtUtc is null &&
        batch.Events.Count == 0 &&
        batch.Sessions.Count == 0 &&
        batch.Turns.Count == 0 &&
        batch.EventTools.Count == 0 &&
        batch.Dispatches.Count == 0 &&
        string.Equals(
            persistedCursor.SourceInstanceId,
            batch.Instance.SourceInstanceId,
            StringComparison.Ordinal) &&
        string.Equals(
            persistedCursor.SourceEntityId,
            batch.Entity.SourceEntityId,
            StringComparison.Ordinal) &&
        string.Equals(
            persistedCursor.SourcePath,
            batch.Entity.SourcePath,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            persistedCursor.CursorJson,
            batch.NextCursorJson,
            StringComparison.Ordinal) &&
        string.Equals(
            persistedCursor.SourceFingerprint,
            batch.SourceFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(
            persistedCursor.ParserVersion,
            batch.ParserVersion,
            StringComparison.Ordinal) &&
        persistedCursor.EventRevisionHighWatermark ==
            batch.EventRevisionHighWatermark;

    private static void ValidateBatchIdentity(
        CollectedBatch batch,
        CollectionRequest request)
    {
        if (batch is null ||
            batch.Instance != request.Instance ||
            batch.Entity != request.Entity ||
            batch.Events is null ||
            batch.Sessions is null ||
            batch.Turns is null ||
            batch.EventTools is null ||
            batch.Dispatches is null ||
            batch.Diagnostics is null ||
            string.IsNullOrWhiteSpace(batch.NextCursorJson) ||
            string.IsNullOrWhiteSpace(batch.SourceFingerprint) ||
            string.IsNullOrWhiteSpace(batch.ParserVersion))
        {
            throw new InvalidOperationException(
                "Collector returned a batch that does not match its request.");
        }

        foreach (var value in batch.Events)
        {
            if (value is null ||
                !string.Equals(
                    value.SourceInstanceId,
                    batch.Instance.SourceInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.SourceEntityId,
                    batch.Entity.SourceEntityId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.AgentId,
                    batch.Instance.AgentId,
                    StringComparison.Ordinal) ||
                value.SourceKind != batch.Instance.SourceKind ||
                !string.Equals(
                    value.ParserVersion,
                    batch.ParserVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.SourceFingerprint,
                    batch.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Collector returned a batch that does not match its request.");
            }
        }

        foreach (UsageSessionMetadata value in batch.Sessions)
        {
            if (value is null ||
                !string.Equals(
                    value.SourceInstanceId,
                    batch.Instance.SourceInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.SourceEntityId,
                    batch.Entity.SourceEntityId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.AgentId,
                    batch.Instance.AgentId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.ParserVersion,
                    batch.ParserVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Collector returned session metadata that does not match its request.");
            }
        }

        ValidateDerivedMetadata(
            batch.Turns,
            batch.Instance,
            batch.Entity,
            batch.ParserVersion,
            static value => (
                value.AgentId,
                value.SourceInstanceId,
                value.SourceEntityId,
                value.ParserVersion));
        ValidateDerivedMetadata(
            batch.EventTools,
            batch.Instance,
            batch.Entity,
            batch.ParserVersion,
            static value => (
                value.AgentId,
                value.SourceInstanceId,
                value.SourceEntityId,
                value.ParserVersion));
        ValidateDerivedMetadata(
            batch.Dispatches,
            batch.Instance,
            batch.Entity,
            batch.ParserVersion,
            static value => (
                value.AgentId,
                value.SourceInstanceId,
                value.SourceEntityId,
                value.ParserVersion));
    }

    private static void ValidateDerivedMetadata<T>(
        IReadOnlyList<T> values,
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        string parserVersion,
        Func<T, (string AgentId, string InstanceId, string EntityId, string ParserVersion)>
            identity)
    {
        foreach (T value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            (string agentId, string instanceId, string entityId, string valueParser) =
                identity(value);
            if (!string.Equals(agentId, instance.AgentId, StringComparison.Ordinal) ||
                !string.Equals(
                    instanceId,
                    instance.SourceInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    entityId,
                    entity.SourceEntityId,
                    StringComparison.Ordinal) ||
                !string.Equals(valueParser, parserVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Collector returned derived metadata that does not match its request.");
            }
        }
    }

    private static bool IsSourceIoFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException;
}
