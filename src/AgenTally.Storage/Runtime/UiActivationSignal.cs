using System.Runtime.Versioning;

namespace AgenTally.Storage.Runtime;

[SupportedOSPlatform("windows")]
public sealed class UiActivationSignal : IDisposable
{
    private readonly EventWaitHandle _signal;

    public UiActivationSignal(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EventName = profile.UiActivationEventName;
        _signal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            EventName);
    }

    public string EventName { get; }

    public void Wait(CancellationToken cancellationToken)
    {
        int completed = WaitHandle.WaitAny(
            [_signal, cancellationToken.WaitHandle]);
        if (completed == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public static bool TryRequest(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EventWaitHandle? signal = null;
        try
        {
            signal = EventWaitHandle.OpenExisting(
                profile.UiActivationEventName);
            return signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
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

    public void Dispose() => _signal.Dispose();
}
