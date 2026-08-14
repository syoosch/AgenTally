using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Monitoring;
using AgenTally.Core.Processing;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using Microsoft.Data.Sqlite;
using System.Runtime.ExceptionServices;

namespace AgenTally.Core.Hosting;

public sealed class CollectionScheduler : IAsyncDisposable
{
    private const string BatchLimitDiagnosticCode = "collector.batch_limit_reached";
    private const string StalledCursorMessage =
        "Collection cursor did not advance after reaching the batch limit.";

    public static readonly TimeSpan DefaultAuditInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultSessionNameSyncInterval =
        TimeSpan.FromSeconds(60);

    private readonly IAgentCollector _collector;
    private readonly IUsageWriter _writer;
    private readonly CollectorContext _context;
    private readonly CollectionRequestQueue _queue;
    private readonly ImportCoordinator _coordinator;
    private readonly SourceChangeMonitor _monitor;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _auditInterval;
    private readonly TimeSpan _sessionNameSyncInterval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _knownInstanceIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private Task _consumerTask = Task.CompletedTask;
    private Task _auditTask = Task.CompletedTask;
    private Task _sessionNameSyncTask = Task.CompletedTask;
    private Task _monitorObserverTask = Task.CompletedTask;
    private Task _completion = Task.CompletedTask;
    private IReadOnlyList<SourceInstanceDescriptor> _sessionNameInstances = [];
    private IReadOnlyList<UsageSessionNameMetadata>? _lastSynchronizedSessionNames;
    private bool _started;
    private bool _disposed;

    public CollectionScheduler(
        IAgentCollector collector,
        IUsageWriter writer,
        CollectorContext context,
        CollectionRequestQueue? queue = null,
        TimeProvider? timeProvider = null,
        TimeSpan? auditInterval = null,
        TimeSpan? sessionNameSyncInterval = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _queue = queue ?? new CollectionRequestQueue();
        _timeProvider = timeProvider ?? context.TimeProvider;
        _auditInterval = auditInterval ?? DefaultAuditInterval;
        if (_auditInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(auditInterval));
        }

        _sessionNameSyncInterval = sessionNameSyncInterval ??
            DefaultSessionNameSyncInterval;
        if (_sessionNameSyncInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionNameSyncInterval));
        }

        _coordinator = new ImportCoordinator(_writer, _timeProvider);
        _monitor = new SourceChangeMonitor(
            _collector,
            _context,
            _writer,
            _queue,
            _timeProvider);
    }

    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _completion;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("Collection scheduler has already started.");
            }

            _started = true;
        }

        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        CancellationToken startupToken = startupCancellation.Token;
        try
        {
            await _writer.InitializeAsync(startupToken);
            SourceProbeResult probe = await _collector.ProbeAsync(
                _context,
                startupToken);
            ValidateProbe(probe);
            await EnsureCurrentParserStateAsync(probe.Instances, startupToken);
            _sessionNameInstances = probe.Instances.Where(value =>
                    string.Equals(
                        value.AgentId,
                        _collector.AgentId,
                        StringComparison.Ordinal))
                .ToArray();
            await SynchronizeSessionNamesAsync(
                _sessionNameInstances,
                startupToken);
            foreach (SourceInstanceDescriptor instance in probe.Instances)
            {
                _knownInstanceIds.Add(instance.SourceInstanceId);
            }

            _consumerTask = ConsumeAsync(_shutdown.Token);
            CancelSiblingsOnFault(_consumerTask);
            _monitorObserverTask = ObserveMonitorAsync(_shutdown.Token);
            CancelSiblingsOnFault(_monitorObserverTask);
            lock (_gate)
            {
                _completion = Task.WhenAll(
                    _consumerTask,
                    _auditTask,
                    _sessionNameSyncTask,
                    _monitorObserverTask);
            }

            await _monitor.StartAsync(probe.Instances, startupToken);

            foreach (SourceEntityDescriptor entity in probe.Entities)
            {
                startupToken.ThrowIfCancellationRequested();
                SourceInstanceDescriptor instance = FindInstance(probe.Instances, entity);
                StoredCursor? cursor = await _writer.GetCursorAsync(
                    instance.SourceInstanceId,
                    entity.SourceEntityId,
                    startupToken);
                await EnqueueDuringStartupAsync(
                    new CollectionRequest(
                        instance,
                        entity,
                        cursor,
                        CollectionReason.StartupImport),
                    startupToken);
            }

            _auditTask = AuditLoopAsync(_shutdown.Token);
            CancelSiblingsOnFault(_auditTask);
            _sessionNameSyncTask = SessionNameSyncLoopAsync(_shutdown.Token);
            CancelSiblingsOnFault(_sessionNameSyncTask);
            lock (_gate)
            {
                _completion = Task.WhenAll(
                    _consumerTask,
                    _auditTask,
                    _sessionNameSyncTask,
                    _monitorObserverTask);
            }
        }
        catch (Exception exception)
        {
            try
            {
                await DisposeAsync();
            }
            catch
            {
                // Preserve the startup failure; cleanup still runs in DisposeAsync finally.
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool shouldDispose;
        lock (_gate)
        {
            shouldDispose = !_disposed;
            _disposed = true;
        }

        if (!shouldDispose)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            _shutdown.Cancel();

            try
            {
                await _monitor.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Task completion;
            lock (_gate)
            {
                completion = Task.WhenAll(
                    _consumerTask,
                    _auditTask,
                    _sessionNameSyncTask,
                    _monitorObserverTask);
                _completion = completion;
            }

            try
            {
                await completion;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Expected during normal shutdown.
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        finally
        {
            _queue.Complete();
            _shutdown.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task EnqueueDuringStartupAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        Task enqueue = _queue.EnqueueAsync(request, cancellationToken).AsTask();
        await Task.WhenAny(enqueue, _consumerTask, _monitorObserverTask);

        if (_consumerTask.IsFaulted)
        {
            await _consumerTask;
        }

        if (_monitorObserverTask.IsFaulted)
        {
            await _monitorObserverTask;
        }

        if (_consumerTask.IsCompletedSuccessfully &&
            !enqueue.IsCompleted &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Collection consumer stopped during scheduler startup.");
        }

        await enqueue;
    }

    private async Task ObserveMonitorAsync(CancellationToken cancellationToken)
    {
        Task stopped = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task completed = await Task.WhenAny(_monitor.Completion, stopped);
        await completed;
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                CollectionRequest queued = await _queue.DequeueAsync(cancellationToken);
                StoredCursor? latestCursor = await _writer.GetCursorAsync(
                    queued.Instance.SourceInstanceId,
                    queued.Entity.SourceEntityId,
                    cancellationToken);
                var current = queued with { Cursor = latestCursor };
                await SyncWithRetryAsync(current, cancellationToken);
            }
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            // Normal completion after the scheduler stops accepting requests.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal scheduler shutdown.
        }
    }

    private async Task SyncWithRetryAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        if (request.Cursor is not null)
        {
            seenCursors.Add(request.Cursor.CursorJson);
        }

        while (true)
        {
            SyncResult result = await SyncOnceWithRetryAsync(
                request,
                cancellationToken);
            bool batchLimitReached = result.Succeeded &&
                result.Diagnostics.Any(static value =>
                    string.Equals(
                        value.Code,
                        BatchLimitDiagnosticCode,
                        StringComparison.Ordinal));
            if (!batchLimitReached)
            {
                return;
            }

            StoredCursor? nextCursor = await _writer.GetCursorAsync(
                request.Instance.SourceInstanceId,
                request.Entity.SourceEntityId,
                cancellationToken);
            if (nextCursor is null || !seenCursors.Add(nextCursor.CursorJson))
            {
                throw new InvalidOperationException(StalledCursorMessage);
            }

            request = request with { Cursor = nextCursor };
        }
    }

    private async Task<SyncResult> SyncOnceWithRetryAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        TimeSpan[] delays =
        [
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500)
        ];

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await _coordinator.SyncAsync(
                    _collector,
                    request,
                    cancellationToken);
            }
            catch (SqliteException exception)
                when (IsBusy(exception) && attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], _timeProvider, cancellationToken);
                StoredCursor? latestCursor = await _writer.GetCursorAsync(
                    request.Instance.SourceInstanceId,
                    request.Entity.SourceEntityId,
                    cancellationToken);
                request = request with { Cursor = latestCursor };
            }
        }
    }

    private async Task AuditLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_auditInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await AuditAsync(cancellationToken);
        }
    }

    private async Task AuditAsync(CancellationToken cancellationToken)
    {
        SourceProbeResult probe = await _collector.ProbeAsync(_context, cancellationToken);
        ValidateProbe(probe);
        SourceInstanceDescriptor[] knownInstances = probe.Instances
            .Where(value => _knownInstanceIds.Contains(value.SourceInstanceId))
            .ToArray();
        await _monitor.RefreshRootsAsync(knownInstances, cancellationToken);

        foreach (SourceEntityDescriptor entity in probe.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_knownInstanceIds.Contains(entity.SourceInstanceId))
            {
                continue;
            }

            SourceInstanceDescriptor instance = FindInstance(probe.Instances, entity);
            StoredCursor? cursor = await _writer.GetCursorAsync(
                instance.SourceInstanceId,
                entity.SourceEntityId,
                cancellationToken);
            if (!NeedsAuditForCollector(entity, cursor))
            {
                continue;
            }

            await _queue.EnqueueAsync(
                new CollectionRequest(
                    instance,
                    entity,
                    cursor,
                    CollectionReason.PeriodicAudit),
                cancellationToken);
        }
    }

    private async Task SessionNameSyncLoopAsync(
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            _sessionNameSyncInterval,
            _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await SynchronizeSessionNamesAsync(
                _sessionNameInstances,
                cancellationToken);
        }
    }

    private bool NeedsAuditForCollector(
        SourceEntityDescriptor entity,
        StoredCursor? cursor)
    {
        if (cursor is null ||
            !string.Equals(cursor.SourcePath, entity.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            cursor.LastSuccessAtUtc is null)
        {
            return true;
        }

        try
        {
            if (_collector is ISourceFileChangeCollector changeCollector &&
                changeCollector.HasSourceChanged(entity, cursor))
            {
                return true;
            }

            var info = new FileInfo(entity.SourcePath);
            if (!info.Exists || info.LastWriteTimeUtc > cursor.LastSuccessAtUtc.Value.UtcDateTime)
            {
                return info.Exists;
            }

            if (_collector is IIncrementalFileCollector fileCollector)
            {
                return !fileCollector.TryGetCursorByteOffset(cursor, out long byteOffset) ||
                    info.Length != byteOffset;
            }

            return string.Equals(_collector.AgentId, "codex", StringComparison.Ordinal)
                ? NeedsAudit(entity, cursor)
                : false;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static bool NeedsAudit(
        SourceEntityDescriptor entity,
        StoredCursor? cursor)
    {
        if (cursor is null ||
            !string.Equals(cursor.SourcePath, entity.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            cursor.LastSuccessAtUtc is null)
        {
            return true;
        }

        try
        {
            var info = new FileInfo(entity.SourcePath);
            if (!info.Exists || info.LastWriteTimeUtc > cursor.LastSuccessAtUtc.Value.UtcDateTime)
            {
                return info.Exists;
            }

            CodexCursor parsed = CodexCursor.DeserializeOrStart(
                cursor.CursorJson,
                hasStoredCursor: true,
                out CollectorDiagnostic? diagnostic);
            return diagnostic is not null || info.Length != parsed.Jsonl.ByteOffset;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private void CancelSiblingsOnFault(Task task)
    {
        _ = task.ContinueWith(
            _ => _shutdown.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;

    private async Task EnsureCurrentParserStateAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        CancellationToken cancellationToken)
    {
        string? parserVersion = (_collector as IParserVersionedCollector)?.ParserVersion;
        if (parserVersion is null &&
            string.Equals(_collector.AgentId, "codex", StringComparison.Ordinal))
        {
            parserVersion = CodexRolloutParser.CurrentParserVersion;
        }

        if (parserVersion is null)
        {
            return;
        }

        foreach (SourceInstanceDescriptor instance in instances.Where(value =>
                     string.Equals(
                         value.AgentId,
                         _collector.AgentId,
                         StringComparison.Ordinal)))
        {
            SourceInstanceParserState state =
                await _writer.GetSourceInstanceParserStateAsync(
                    instance,
                    parserVersion,
                    cancellationToken);
            if (state.RequiresRebuild)
            {
                if (string.Equals(_collector.AgentId, "codex", StringComparison.Ordinal))
                {
                    throw new CodexParserRebuildRequiredException(
                        "stored-derived-data",
                        parserVersion);
                }

                throw new AgentParserRebuildRequiredException(
                    _collector.AgentId,
                    "stored-derived-data",
                    parserVersion);
            }
        }
    }

    private async Task SynchronizeSessionNamesAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        CancellationToken cancellationToken)
    {
        if (_collector is not IUsageSessionNameSource sessionNameSource)
        {
            return;
        }

        IReadOnlyList<UsageSessionNameMetadata> sessionNames =
            await sessionNameSource.ReadSessionNamesAsync(cancellationToken);
        if (_lastSynchronizedSessionNames is not null &&
            HaveSameSessionNames(
                _lastSynchronizedSessionNames,
                sessionNames))
        {
            return;
        }

        bool synchronized = false;
        foreach (SourceInstanceDescriptor instance in instances.Where(value =>
                     string.Equals(
                         value.AgentId,
                         _collector.AgentId,
                         StringComparison.Ordinal)))
        {
            await _writer.SynchronizeSessionNamesAsync(
                instance,
                sessionNames,
                cancellationToken);
            synchronized = true;
        }

        if (synchronized)
        {
            _lastSynchronizedSessionNames = sessionNames;
        }
    }

    private static bool HaveSameSessionNames(
        IReadOnlyList<UsageSessionNameMetadata> left,
        IReadOnlyList<UsageSessionNameMetadata> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                    left[index].SessionId,
                    right[index].SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    left[index].SessionName,
                    right[index].SessionName,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static SourceInstanceDescriptor FindInstance(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        SourceEntityDescriptor entity) =>
        instances.First(value => string.Equals(
            value.SourceInstanceId,
            entity.SourceInstanceId,
            StringComparison.Ordinal));

    private static void ValidateProbe(SourceProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(probe.Instances);
        ArgumentNullException.ThrowIfNull(probe.Entities);
        ArgumentNullException.ThrowIfNull(probe.Diagnostics);
        if (probe.Diagnostics.Count > 0)
        {
            throw new SourceProbeIncompleteException(probe.Diagnostics.Count);
        }

        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SourceInstanceDescriptor instance in probe.Instances)
        {
            ArgumentNullException.ThrowIfNull(instance);
            if (!instanceIds.Add(instance.SourceInstanceId))
            {
                throw new InvalidOperationException("Collector returned a duplicate source instance.");
            }
        }

        foreach (SourceEntityDescriptor entity in probe.Entities)
        {
            ArgumentNullException.ThrowIfNull(entity);
            if (!instanceIds.Contains(entity.SourceInstanceId))
            {
                throw new InvalidOperationException("Collector returned an entity without its instance.");
            }
        }
    }
}
