using System.IO;
using System.Runtime.Versioning;

namespace AgenTally.Storage.Runtime;

public enum VersionCheckLifecycleClaimResult
{
    Claimed,
    AlreadyClaimed,
    Unavailable
}

[SupportedOSPlatform("windows")]
public sealed class VersionCheckLifecycleRegistration : IDisposable
{
    private readonly object _gate = new();
    private readonly EventWaitHandle _state;
    private int _disposed;

    private VersionCheckLifecycleRegistration(EventWaitHandle state)
    {
        _state = state;
    }

    public static VersionCheckLifecycleRegistration? TryCreateCoreOwner(
        AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Channel != AgenTallyChannel.Stable)
        {
            return null;
        }

        try
        {
            return new VersionCheckLifecycleRegistration(
                new EventWaitHandle(
                    initialState: false,
                    EventResetMode.ManualReset,
                    profile.VersionCheckLifecycleEventName));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static VersionCheckLifecycleRegistration? TryOpenForUi(
        AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Channel != AgenTallyChannel.Stable)
        {
            return null;
        }

        EventWaitHandle? state = null;
        try
        {
            return EventWaitHandle.TryOpenExisting(
                    profile.VersionCheckLifecycleEventName,
                    out state)
                ? new VersionCheckLifecycleRegistration(state)
                : null;
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                WaitHandleCannotBeOpenedException)
        {
            state?.Dispose();
            return null;
        }
    }

    public VersionCheckLifecycleClaimResult TryClaim()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return VersionCheckLifecycleClaimResult.Unavailable;
            }

            try
            {
                if (_state.WaitOne(0))
                {
                    return VersionCheckLifecycleClaimResult.AlreadyClaimed;
                }

                return _state.Set()
                    ? VersionCheckLifecycleClaimResult.Claimed
                    : VersionCheckLifecycleClaimResult.Unavailable;
            }
            catch (Exception exception)
                when (exception is ObjectDisposedException or
                    UnauthorizedAccessException)
            {
                return VersionCheckLifecycleClaimResult.Unavailable;
            }
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

            _state.Dispose();
        }
    }
}
