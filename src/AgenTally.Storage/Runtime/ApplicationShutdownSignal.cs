using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Storage.Runtime;

[SupportedOSPlatform("windows")]
public sealed record ApplicationShutdownRequestResult(
    string ProfileId,
    bool MarkerWritten,
    bool SemaphoreOpened,
    bool SemaphoreBroadcast)
{
    public bool AnyTransportSucceeded => MarkerWritten || SemaphoreBroadcast;

    public bool RequestAccepted => MarkerWritten;
}

[SupportedOSPlatform("windows")]
public sealed class ApplicationShutdownSignal : IDisposable
{
    private const int BroadcastCapacity = 64;
    private const int MaximumRequestBytes = 4096;
    private readonly object _disposeGate = new();
    private readonly Semaphore _signal;
    private readonly AutoResetEvent _requestSignal = new(initialState: false);
    private readonly string? _requestPath;
    private readonly string? _profileId;
    private readonly long _processStartUtcTicks;
    private readonly FileSystemWatcher? _requestWatcher;
    private readonly HashSet<WaitOperation> _pendingWaits = [];
    private int _disposed;

    public ApplicationShutdownSignal(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        EventName = eventName;
        _signal = new Semaphore(
            initialCount: 0,
            maximumCount: BroadcastCapacity,
            eventName);
        using Process process = Process.GetCurrentProcess();
        _processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
    }

    public ApplicationShutdownSignal(AgenTallyRuntimeProfile profile)
        : this((profile ?? throw new ArgumentNullException(nameof(profile)))
            .ShutdownEventName)
    {
        _requestPath = profile.ShutdownRequestPath;
        _profileId = profile.ProfileId;
        Directory.CreateDirectory(profile.RuntimeRoot);
        var watcher = new FileSystemWatcher(
            profile.RuntimeRoot,
            Path.GetFileName(profile.ShutdownRequestPath))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.CreationTime
        };
        watcher.Created += OnRequestChanged;
        watcher.Changed += OnRequestChanged;
        watcher.Renamed += OnRequestChanged;
        watcher.EnableRaisingEvents = true;
        _requestWatcher = watcher;
        _ = CheckRequestMarker();
    }

    public string EventName { get; }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_disposeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            var operation = new WaitOperation(
                this,
                _signal,
                _requestSignal,
                cancellationToken);
            _pendingWaits.Add(operation);
            return operation.Start();
        }
    }

    public void Wait(CancellationToken cancellationToken) =>
        WaitAsync(cancellationToken).GetAwaiter().GetResult();

    public static bool TryRequest(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return TryBroadcast(eventName).Broadcast;
    }

    public static bool TryRequest(AgenTallyRuntimeProfile profile)
        => Request(profile).RequestAccepted;

    public static ApplicationShutdownRequestResult Request(
        AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        bool markerWritten = TryWriteRequestMarker(profile);
        (bool semaphoreOpened, bool semaphoreBroadcast) =
            TryBroadcast(profile.ShutdownEventName);
        return new ApplicationShutdownRequestResult(
            profile.ProfileId,
            markerWritten,
            semaphoreOpened,
            semaphoreBroadcast);
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        WaitOperation[] pending;
        lock (_disposeGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            watcher = _requestWatcher;
            if (watcher is not null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnRequestChanged;
                watcher.Changed -= OnRequestChanged;
                watcher.Renamed -= OnRequestChanged;
            }

            pending = [.. _pendingWaits];
            foreach (WaitOperation operation in pending)
            {
                operation.CompleteDisposed();
            }
        }

        watcher?.Dispose();
        foreach (WaitOperation operation in pending)
        {
            operation.WaitForCleanup();
        }

        _requestSignal.Dispose();
        _signal.Dispose();
    }

    private void OnRequestChanged(object sender, FileSystemEventArgs eventArgs) =>
        _ = CheckRequestMarker();

    private bool CheckRequestMarker()
    {
        if (_requestPath is null ||
            _profileId is null ||
            Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            byte[] payload = BoundedFileReader.ReadAllBytes(
                _requestPath,
                MaximumRequestBytes);
            try
            {
                ReadOnlySpan<byte> json = payload;
                if (json.Length >= 3 &&
                    json[0] == 0xEF &&
                    json[1] == 0xBB &&
                    json[2] == 0xBF)
                {
                    json = json[3..];
                }
                ShutdownRequest? request =
                    JsonSerializer.Deserialize<ShutdownRequest>(json);
                if (request is not null &&
                    string.Equals(
                        request.ProfileId,
                        _profileId,
                        StringComparison.Ordinal) &&
                    request.RequestedAtUtcTicks >= _processStartUtcTicks)
                {
                    lock (_disposeGate)
                    {
                        if (Volatile.Read(ref _disposed) == 0)
                        {
                            _requestSignal.Set();
                            return true;
                        }
                    }
                }
            }
            finally
            {
                Array.Clear(payload);
            }
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException
                or System.Security.SecurityException
                or JsonException)
        {
            // Atomic replacement can briefly race a notification; later events retry.
        }

        return false;
    }

    private bool ShouldCompleteSemaphoreWait() =>
        _requestPath is null || CheckRequestMarker();

    private static bool TryWriteRequestMarker(AgenTallyRuntimeProfile profile)
    {
        string temporaryPath = profile.ShutdownRequestPath +
            $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(profile.RuntimeRoot);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new ShutdownRequest(profile.ProfileId, DateTime.UtcNow.Ticks));
            File.WriteAllBytes(temporaryPath, json);
            File.Move(
                temporaryPath,
                profile.ShutdownRequestPath,
                overwrite: true);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static (bool Opened, bool Broadcast) TryBroadcast(string eventName)
    {
        Semaphore? signal = null;
        try
        {
            if (!Semaphore.TryOpenExisting(eventName, out signal))
            {
                return (false, false);
            }

            try
            {
                signal.Release(BroadcastCapacity);
            }
            catch (SemaphoreFullException)
            {
                // A previous requester already broadcast while listeners exit.
            }

            return (true, true);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, false);
        }
        finally
        {
            signal?.Dispose();
        }
    }

    private void RemoveWait(WaitOperation operation)
    {
        lock (_disposeGate)
        {
            _pendingWaits.Remove(operation);
        }
    }

    private sealed class WaitOperation
    {
        private readonly ApplicationShutdownSignal _owner;
        private readonly TaskCompletionSource _result = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cleanupCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RegisteredWaitHandle _semaphoreWait;
        private readonly RegisteredWaitHandle _requestWait;
        private readonly CancellationTokenRegistration _cancellation;

        public WaitOperation(
            ApplicationShutdownSignal owner,
            WaitHandle semaphore,
            WaitHandle request,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _semaphoreWait = ThreadPool.RegisterWaitForSingleObject(
                semaphore,
                static (state, _) =>
                    ((WaitOperation)state!).CompleteFromSemaphore(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: owner._requestPath is null);
            _requestWait = ThreadPool.RegisterWaitForSingleObject(
                request,
                static (state, _) => ((WaitOperation)state!).Complete(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: true);
            _cancellation = cancellationToken.Register(
                static state =>
                {
                    var pair = ((WaitOperation Operation, CancellationToken Token))state!;
                    pair.Operation._result.TrySetCanceled(pair.Token);
                },
                (this, cancellationToken));
        }

        public Task Start() => CompleteAndCleanupAsync();

        public void CompleteDisposed() => _result.TrySetException(
            new ObjectDisposedException(nameof(ApplicationShutdownSignal)));

        public void WaitForCleanup() =>
            _cleanupCompleted.Task.GetAwaiter().GetResult();

        private void Complete() => _result.TrySetResult();

        private void CompleteFromSemaphore()
        {
            if (_owner.ShouldCompleteSemaphoreWait())
            {
                Complete();
            }
        }

        private async Task CompleteAndCleanupAsync()
        {
            try
            {
                await _result.Task.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    UnregisterAndWait(_semaphoreWait);
                    UnregisterAndWait(_requestWait);
                    _cancellation.Dispose();
                    _owner.RemoveWait(this);
                }
                finally
                {
                    _cleanupCompleted.TrySetResult();
                }
            }
        }

        private static void UnregisterAndWait(
            RegisteredWaitHandle registration)
        {
            using var completed = new ManualResetEvent(initialState: false);
            if (registration.Unregister(completed))
            {
                completed.WaitOne();
            }
        }
    }

    private sealed record ShutdownRequest(
        [property: JsonPropertyName("profileId")]
        string ProfileId,
        [property: JsonPropertyName("requestedAtUtcTicks")]
        long RequestedAtUtcTicks);
}
