namespace AgenTally.Core.Hosting;

public sealed class CoreDatabaseWriteGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _pricingBlocked;
    private int _disposed;

    public async Task<IDisposable> EnterAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _semaphore.WaitAsync(cancellationToken);
        return new GateLease(_semaphore);
    }

    public async Task<IDisposable?> TryEnterPricingAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Volatile.Read(ref _pricingBlocked) != 0 ||
            !await _semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return null;
        }

        if (Volatile.Read(ref _pricingBlocked) == 0)
        {
            return new GateLease(_semaphore);
        }

        _semaphore.Release();
        return null;
    }

    public IDisposable BlockPricing()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        Interlocked.Increment(ref _pricingBlocked);
        return new PricingBlockLease(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    private sealed class GateLease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public GateLease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose() => Interlocked.Exchange(
            ref _semaphore,
            null)?.Release();
    }

    private sealed class PricingBlockLease : IDisposable
    {
        private CoreDatabaseWriteGate? _owner;

        public PricingBlockLease(CoreDatabaseWriteGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            CoreDatabaseWriteGate? owner =
                Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner._pricingBlocked);
            }
        }
    }
}
