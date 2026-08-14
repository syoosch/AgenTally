using System.Runtime.Versioning;

namespace AgenTally.Storage.Runtime;

[SupportedOSPlatform("windows")]
public sealed class CoreMaintenanceShutdownSignal : IDisposable
{
    private readonly object _disposeGate = new();
    private readonly EventWaitHandle _signal;
    private readonly HashSet<WaitOperation> _pendingWaits = [];
    private int _disposed;

    public CoreMaintenanceShutdownSignal(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EventName = profile.CoreMaintenanceShutdownEventName;
        _signal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            EventName);
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
                cancellationToken);
            _pendingWaits.Add(operation);
            return operation.Start();
        }
    }

    public static bool TryRequest(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EventWaitHandle? signal = null;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(
                    profile.CoreMaintenanceShutdownEventName,
                    out signal))
            {
                return false;
            }

            return signal.Set();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            signal?.Dispose();
        }
    }

    public void Dispose()
    {
        WaitOperation[] pending;
        lock (_disposeGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            pending = [.. _pendingWaits];
            foreach (WaitOperation operation in pending)
            {
                operation.CompleteDisposed();
            }
        }

        foreach (WaitOperation operation in pending)
        {
            operation.WaitForCleanup();
        }

        _signal.Dispose();
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
        private readonly CoreMaintenanceShutdownSignal _owner;
        private readonly TaskCompletionSource _result = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cleanupCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RegisteredWaitHandle _signalWait;
        private readonly CancellationTokenRegistration _cancellation;

        public WaitOperation(
            CoreMaintenanceShutdownSignal owner,
            WaitHandle signal,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _signalWait = ThreadPool.RegisterWaitForSingleObject(
                signal,
                static (state, _) => ((WaitOperation)state!).Complete(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: true);
            _cancellation = cancellationToken.Register(
                static state =>
                {
                    var pair =
                        ((WaitOperation Operation, CancellationToken Token))state!;
                    pair.Operation._result.TrySetCanceled(pair.Token);
                },
                (this, cancellationToken));
        }

        public Task Start() => CompleteAndCleanupAsync();

        public void CompleteDisposed() => _result.TrySetException(
            new ObjectDisposedException(nameof(CoreMaintenanceShutdownSignal)));

        public void WaitForCleanup() =>
            _cleanupCompleted.Task.GetAwaiter().GetResult();

        private void Complete() => _result.TrySetResult();

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
                    UnregisterAndWait(_signalWait);
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
}
