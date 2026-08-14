using System.Collections.Concurrent;
using System.Security;
using System.Threading.Channels;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Domain.Sources;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Monitoring;

public sealed class SourceChangeMonitor : IAsyncDisposable
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);

    private readonly IAgentCollector _collector;
    private readonly ISourceFileChangeCollector? _changeCollector;
    private readonly CollectorContext _context;
    private readonly IUsageWriter _writer;
    private readonly CollectionRequestQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounces =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<CompensationKey, byte> _compensations = new();
    private readonly Dictionary<FileSystemWatcher, SourceInstanceDescriptor> _watchers = [];
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly object _gate = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _started;
    private bool _acceptingTasks = true;
    private bool _disposed;
    private Action<FileSystemWatcher>? WatcherEnabledTestHook { get; set; }

    public SourceChangeMonitor(
        IAgentCollector collector,
        CollectorContext context,
        IUsageWriter writer,
        CollectionRequestQueue queue,
        TimeProvider? timeProvider = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _changeCollector = collector as ISourceFileChangeCollector;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _timeProvider = timeProvider ?? context.TimeProvider;
    }

    public Task Completion => _completion.Task;

    public Task StartAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instances);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("Source monitor has already started.");
            }

            _started = true;
        }

        try
        {
            foreach (SourceInstanceDescriptor instance in instances)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateInstance(instance);
                foreach (string rootPath in GetWatchRoots(instance))
                {
                    AddWatcher(instance, rootPath);
                }
            }
        }
        catch
        {
            List<FileSystemWatcher> watchers;
            lock (_gate)
            {
                watchers = DetachWatchersLocked();
            }

            DisposeWatchers(watchers);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task RefreshRootsAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instances);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                throw new InvalidOperationException("Source monitor has not started.");
            }
        }

        foreach (SourceInstanceDescriptor instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInstance(instance);
            foreach (string rootPath in GetWatchRoots(instance))
            {
                AddWatcher(instance, rootPath);
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        List<FileSystemWatcher> watchers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _acceptingTasks = false;
            watchers = DetachWatchersLocked();
            _shutdown.Cancel();
            tasks = [.. _backgroundTasks];
        }

        DisposeWatchers(watchers);

        Exception? failure = null;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Expected while pending debounce work is stopped.
        }
        catch (Exception exception)
        {
            failure = exception;
            PropagateFailure(exception);
            throw;
        }
        finally
        {
            foreach (CancellationTokenSource debounce in _debounces.Values)
            {
                debounce.Dispose();
            }

            _debounces.Clear();
            _shutdown.Dispose();
            if (failure is null)
            {
                _completion.TrySetResult();
            }
        }
    }

    private void AddWatcher(SourceInstanceDescriptor instance, string rootPath)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            string normalizedRoot = NormalizePath(rootPath);
            lock (_gate)
            {
                if (!_acceptingTasks || _watchers.Keys.Any(value => string.Equals(
                        value.Path,
                        normalizedRoot,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            if (!Directory.Exists(normalizedRoot) ||
                (File.GetAttributes(normalizedRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            watcher = new FileSystemWatcher(
                normalizedRoot,
                _changeCollector?.WatchFilter ?? "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime
            };
            watcher.Changed += OnPathChanged;
            watcher.Created += OnPathChanged;
            watcher.Renamed += OnPathChanged;
            watcher.Error += OnWatcherError;
            lock (_gate)
            {
                if (!_acceptingTasks)
                {
                    DetachWatcher(watcher);
                    watcher.Dispose();
                    return;
                }

                if (_watchers.Keys.Any(value => string.Equals(
                        value.Path,
                        normalizedRoot,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    DetachWatcher(watcher);
                    watcher.Dispose();
                    return;
                }

                _watchers.Add(watcher, instance);
                try
                {
                    watcher.EnableRaisingEvents = true;
                    WatcherEnabledTestHook?.Invoke(watcher);
                }
                catch
                {
                    _watchers.Remove(watcher);
                    throw;
                }
            }
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            if (watcher is not null)
            {
                DetachWatcher(watcher);
                watcher.Dispose();
            }

            // The periodic audit remains as compensation when a known root
            // disappears or cannot be watched during startup.
        }
    }

    private void OnPathChanged(object sender, FileSystemEventArgs eventArgs)
    {
        SourceInstanceDescriptor? instance;
        lock (_gate)
        {
            if (!_acceptingTasks ||
                sender is not FileSystemWatcher watcher ||
                !_watchers.TryGetValue(watcher, out instance))
            {
                return;
            }
        }

        string normalizedPath;
        try
        {
            normalizedPath = NormalizePath(eventArgs.FullPath);
            if (!IsRelevantChangePath(normalizedPath) ||
                !IsWithinMonitoredRoots(instance, normalizedPath))
            {
                return;
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        ScheduleDebounce(instance, normalizedPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        FileSystemWatcher? failedWatcher = null;
        lock (_gate)
        {
            if (!_acceptingTasks ||
                sender is not FileSystemWatcher watcher ||
                !_watchers.TryGetValue(watcher, out SourceInstanceDescriptor? instance))
            {
                return;
            }

            _watchers.Remove(watcher);
            DetachWatcher(watcher);
            failedWatcher = watcher;
            string failedRoot = NormalizePath(watcher.Path);
            CompensationKey compensationKey = CreateCompensationKey(
                instance.SourceInstanceId,
                failedRoot);
            if (_compensations.TryAdd(compensationKey, 0))
            {
                TrackLocked(Task.Run(
                    () => CompensateAsync(
                        instance,
                        failedRoot,
                        compensationKey,
                        _shutdown.Token),
                    CancellationToken.None));
            }
        }

        if (failedWatcher is not null)
        {
            DisposeWatchers([failedWatcher]);
        }
    }

    private void ScheduleDebounce(
        SourceInstanceDescriptor instance,
        string normalizedPath)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (!_acceptingTasks)
            {
                return;
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _debounces.AddOrUpdate(
                normalizedPath,
                cancellation,
                (_, previous) =>
                {
                    try
                    {
                        previous.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The replaced debounce completed concurrently.
                    }

                    return cancellation;
                });
            TrackLocked(DebounceAsync(instance, normalizedPath, cancellation));
        }
    }

    private async Task DebounceAsync(
        SourceInstanceDescriptor instance,
        string normalizedPath,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(DebounceDelay, _timeProvider, cancellation.Token);
            await ProbePathAsync(instance, normalizedPath, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Replaced by a newer event or stopped during disposal.
        }
        catch (Exception exception) when (IsExpectedBackgroundFailure(exception))
        {
            // A later watcher event or periodic audit retries this known root.
        }
        finally
        {
            if (_debounces.TryGetValue(normalizedPath, out CancellationTokenSource? current) &&
                ReferenceEquals(current, cancellation))
            {
                _debounces.TryRemove(normalizedPath, out _);
            }

            cancellation.Dispose();
        }
    }

    private async Task ProbePathAsync(
        SourceInstanceDescriptor expectedInstance,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        SourceProbeResult probe = await _collector.ProbeAsync(_context, cancellationToken);
        EnsureCompleteProbe(probe);
        SourceInstanceDescriptor? instance = probe.Instances.FirstOrDefault(value =>
            string.Equals(
                value.SourceInstanceId,
                expectedInstance.SourceInstanceId,
                StringComparison.Ordinal));
        if (instance is null)
        {
            return;
        }

        string entityId = GetSourceEntityId(normalizedPath);
        SourceEntityDescriptor? entity = probe.Entities.FirstOrDefault(value =>
            string.Equals(value.SourceInstanceId, instance.SourceInstanceId, StringComparison.Ordinal) &&
            string.Equals(value.SourceEntityId, entityId, StringComparison.Ordinal));
        if (entity is null)
        {
            return;
        }

        StoredCursor? cursor = await _writer.GetCursorAsync(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            cancellationToken);
        await _queue.EnqueueAsync(
            new CollectionRequest(
                instance,
                entity,
                cursor,
                CollectionReason.FileChanged),
            cancellationToken);
    }

    private async Task CompensateAsync(
        SourceInstanceDescriptor expectedInstance,
        string failedRoot,
        CompensationKey compensationKey,
        CancellationToken cancellationToken)
    {
        SourceInstanceDescriptor instanceForWatcher = expectedInstance;
        try
        {
            SourceProbeResult probe = await _collector.ProbeAsync(_context, cancellationToken);
            EnsureCompleteProbe(probe);
            SourceInstanceDescriptor? instance = probe.Instances.FirstOrDefault(value =>
                string.Equals(
                    value.SourceInstanceId,
                    expectedInstance.SourceInstanceId,
                    StringComparison.Ordinal));
            if (instance is null)
            {
                return;
            }
            instanceForWatcher = instance;

            foreach (SourceEntityDescriptor entity in probe.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(
                        entity.SourceInstanceId,
                        instance.SourceInstanceId,
                        StringComparison.Ordinal) ||
                    !IsWithinRoot(
                        NormalizePath(entity.SourcePath),
                        failedRoot))
                {
                    continue;
                }

                StoredCursor? cursor = await _writer.GetCursorAsync(
                    instance.SourceInstanceId,
                    entity.SourceEntityId,
                    cancellationToken);
                await _queue.EnqueueAsync(
                    new CollectionRequest(
                        instance,
                        entity,
                        cursor,
                        CollectionReason.RepairScan),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during disposal.
        }
        catch (Exception exception) when (IsExpectedBackgroundFailure(exception))
        {
            // The periodic audit remains available after a failed compensation.
        }
        finally
        {
            // Remove first so an Error raised immediately by the replacement
            // watcher can register a fresh compensation for this same root.
            _compensations.TryRemove(compensationKey, out _);
            AddWatcher(instanceForWatcher, failedRoot);
        }
    }

    private void TrackLocked(Task task)
    {
        _backgroundTasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted && completed.Exception is not null)
                {
                    PropagateFailure(completed.Exception);
                }

                lock (_gate)
                {
                    _backgroundTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PropagateFailure(Exception exception)
    {
        IReadOnlyCollection<Exception> failures = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [exception];
        _completion.TrySetException(failures);
    }

    private List<FileSystemWatcher> DetachWatchersLocked()
    {
        var watchers = _watchers.Keys.ToList();
        foreach (FileSystemWatcher watcher in watchers)
        {
            DetachWatcher(watcher);
        }

        _watchers.Clear();
        return watchers;
    }

    private static void DisposeWatchers(IEnumerable<FileSystemWatcher> watchers)
    {
        foreach (FileSystemWatcher watcher in watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch (ObjectDisposedException)
            {
                // Already closed while startup was unwinding.
            }

            watcher.Dispose();
        }
    }

    private void DetachWatcher(FileSystemWatcher watcher)
    {
        watcher.Changed -= OnPathChanged;
        watcher.Created -= OnPathChanged;
        watcher.Renamed -= OnPathChanged;
        watcher.Error -= OnWatcherError;
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.SourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.RootPath);

        if (!string.Equals(instance.AgentId, _collector.AgentId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Source instance does not belong to this collector.",
                nameof(instance));
        }
    }

    private bool IsWithinRoot(string normalizedPath, string rootPath)
    {
        string normalizedRoot = NormalizePath(rootPath);
        string relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        return !string.Equals(relative, ".", StringComparison.Ordinal) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private string NormalizePath(string path) =>
        _changeCollector?.NormalizeSourcePath(path) ??
        (string.Equals(_collector.AgentId, "codex", StringComparison.Ordinal)
            ? CodexSourceIdentity.NormalizePath(path)
            : Path.GetFullPath(path));

    private IReadOnlyList<string> GetWatchRoots(SourceInstanceDescriptor instance) =>
        _changeCollector?.GetWatchRoots(instance) ??
        (string.Equals(_collector.AgentId, "codex", StringComparison.Ordinal)
            ?
            [
                Path.Combine(instance.RootPath, "sessions"),
                Path.Combine(instance.RootPath, "archived_sessions")
            ]
            : []);

    private bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedPath) =>
        _changeCollector?.IsWithinMonitoredRoots(instance, normalizedPath) ??
        (string.Equals(_collector.AgentId, "codex", StringComparison.Ordinal) &&
         (IsWithinRoot(normalizedPath, Path.Combine(instance.RootPath, "sessions")) ||
          IsWithinRoot(normalizedPath, Path.Combine(instance.RootPath, "archived_sessions"))));

    private string GetSourceEntityId(string normalizedPath) =>
        _changeCollector?.GetSourceEntityId(normalizedPath) ??
        (string.Equals(_collector.AgentId, "codex", StringComparison.Ordinal)
            ? CodexSourceIdentity.EntityId(normalizedPath)
            : throw new InvalidOperationException(
                "The collector does not expose incremental file identity."));

    private bool IsRelevantChangePath(string normalizedPath) =>
        _changeCollector?.IsRelevantChangePath(normalizedPath) ??
        string.Equals(
            Path.GetExtension(normalizedPath),
            ".jsonl",
            StringComparison.OrdinalIgnoreCase);

    private CompensationKey CreateCompensationKey(
        string sourceInstanceId,
        string rootPath) =>
        new(sourceInstanceId, NormalizePath(rootPath).ToUpperInvariant());

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException;

    private static void EnsureCompleteProbe(SourceProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(probe.Diagnostics);
        if (probe.Diagnostics.Count > 0)
        {
            throw new SourceProbeIncompleteException(probe.Diagnostics.Count);
        }
    }

    private static bool IsExpectedBackgroundFailure(Exception exception) =>
        IsExpectedFileFailure(exception) ||
        exception is ChannelClosedException;

    private readonly record struct CompensationKey(
        string SourceInstanceId,
        string NormalizedRoot);
}
