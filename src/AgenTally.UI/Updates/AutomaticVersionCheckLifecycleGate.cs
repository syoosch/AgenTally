using AgenTally.Storage.Runtime;

namespace AgenTally.UI.Updates;

internal enum AutomaticVersionCheckClaimResult
{
    Claimed,
    AlreadyClaimed,
    DevelopmentDisabled,
    Unavailable
}

internal interface IAutomaticVersionCheckLifecycleGate : IDisposable
{
    AutomaticVersionCheckClaimResult TryClaim();
}

internal sealed class AutomaticVersionCheckLifecycleGate(
    AgenTallyRuntimeProfile profile)
    : IAutomaticVersionCheckLifecycleGate
{
    private readonly object _gate = new();
    private readonly AgenTallyRuntimeProfile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));
    private VersionCheckLifecycleRegistration? _registration;
    private int _disposed;

    public AutomaticVersionCheckClaimResult TryClaim()
    {
        if (_profile.Channel == AgenTallyChannel.Development)
        {
            return AutomaticVersionCheckClaimResult.DevelopmentDisabled;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return AutomaticVersionCheckClaimResult.Unavailable;
            }

            _registration ??=
                VersionCheckLifecycleRegistration.TryOpenForUi(_profile);
            if (_registration is null)
            {
                return AutomaticVersionCheckClaimResult.Unavailable;
            }

            return _registration.TryClaim() switch
            {
                VersionCheckLifecycleClaimResult.Claimed =>
                    AutomaticVersionCheckClaimResult.Claimed,
                VersionCheckLifecycleClaimResult.AlreadyClaimed =>
                    AutomaticVersionCheckClaimResult.AlreadyClaimed,
                _ => AutomaticVersionCheckClaimResult.Unavailable
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _registration?.Dispose();
            _registration = null;
        }
    }
}
