using System.Diagnostics;
using System.Runtime.Versioning;

namespace AgenTally.Storage.Runtime;

[SupportedOSPlatform("windows")]
public sealed class UiInstanceRegistration : IDisposable
{
    private static readonly TimeSpan DefaultActivationTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationRetryInterval =
        TimeSpan.FromMilliseconds(50);

    private readonly Semaphore _lease;
    private int _disposed;

    private UiInstanceRegistration(
        Semaphore lease,
        UiActivationSignal activationSignal)
    {
        _lease = lease;
        ActivationSignal = activationSignal;
    }

    public UiActivationSignal ActivationSignal { get; }

    public static Task<UiInstanceRegistration?> TryRegisterAsync(
        AgenTallyRuntimeProfile profile,
        CancellationToken cancellationToken = default) =>
        TryRegisterAsync(
            profile,
            DefaultActivationTimeout,
            cancellationToken);

    public static async Task<UiInstanceRegistration?> TryRegisterAsync(
        AgenTallyRuntimeProfile profile,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (activationTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activationTimeout));
        }

        var lease = new Semaphore(
            initialCount: 1,
            maximumCount: 1,
            profile.UiInstanceLeaseName);
        bool ownsLease;
        try
        {
            ownsLease = lease.WaitOne(0);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (ownsLease)
        {
            try
            {
                return new UiInstanceRegistration(
                    lease,
                    new UiActivationSignal(profile));
            }
            catch
            {
                lease.Release();
                lease.Dispose();
                throw;
            }
        }

        lease.Dispose();
        var elapsed = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (UiActivationSignal.TryRequest(profile))
            {
                return null;
            }

            if (elapsed.Elapsed >= activationTimeout)
            {
                return null;
            }

            TimeSpan remaining = activationTimeout - elapsed.Elapsed;
            await Task.Delay(
                remaining < ActivationRetryInterval
                    ? remaining
                    : ActivationRetryInterval,
                cancellationToken).ConfigureAwait(false);
        }
        while (true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ActivationSignal.Dispose();
        try
        {
            _lease.Release();
        }
        finally
        {
            _lease.Dispose();
        }
    }
}
