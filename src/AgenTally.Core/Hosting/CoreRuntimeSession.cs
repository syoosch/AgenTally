using System.Diagnostics;
using System.Reflection;
using AgenTally.Storage.Runtime;

namespace AgenTally.Core.Hosting;

internal sealed class CoreRuntimeSession : IDisposable
{
    private readonly CoreRuntimeStatusStore _statusStore;
    private readonly VersionCheckLifecycleRegistration? _versionCheckLifecycle;
    private readonly AgenTallyRuntimeProfile _profile;
    private readonly string _applicationVersion;
    private readonly int _processId;
    private readonly long _processStartUtcTicks;

    public CoreRuntimeSession(AgenTallyRuntimeProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _statusStore = new CoreRuntimeStatusStore(profile);
        using Process process = Process.GetCurrentProcess();
        _processId = process.Id;
        _processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        _applicationVersion = typeof(CoreRuntimeSession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
            typeof(CoreRuntimeSession).Assembly.GetName().Version?.ToString() ??
            "unknown";
        _versionCheckLifecycle =
            VersionCheckLifecycleRegistration.TryCreateCoreOwner(profile);
    }

    public Task PublishAsync(
        CoreRuntimePhase phase,
        CoreRuntimeErrorCode errorCode,
        string messageCode,
        int? exitCode = null) =>
        _statusStore.WriteAsync(
            new CoreRuntimeStatus(
                CoreRuntimeStatus.CurrentProtocolVersion,
                _profile.Channel,
                _profile.ProfileId,
                _applicationVersion,
                _processId,
                _processStartUtcTicks,
                phase,
                errorCode,
                messageCode,
                DateTimeOffset.UtcNow,
                exitCode),
            CancellationToken.None);

    public void Dispose() => _versionCheckLifecycle?.Dispose();
}

public static class CoreExitCodes
{
    public const int Success = 0;
    public const int RuntimeFailure = 1;
    public const int InvalidArguments = 2;
    public const int ParserRebuildRequired = 3;

    public const int ParserRescanRequired = ParserRebuildRequired;
    public const int AlreadyRunning = 4;
}
