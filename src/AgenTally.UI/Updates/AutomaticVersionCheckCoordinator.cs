using System.Reflection;
using AgenTally.Storage.Runtime;

namespace AgenTally.UI.Updates;

internal enum AutomaticVersionCheckRunResult
{
    UpdateAvailable,
    UpToDate,
    NetworkFailure,
    InvalidResponse,
    DevelopmentDisabled,
    StableChannelNotConfigured,
    InvalidCurrentVersion,
    AlreadyChecked,
    LifecycleUnavailable,
    Cancelled,
    Failed,
    AlreadyRun
}

internal interface IAutomaticVersionCheckPresenter
{
    void ShowUpdateAvailable(
        ReleaseVersion currentVersion,
        ReleaseVersion latestVersion,
        Uri releasePageUri);
}

internal sealed class AutomaticVersionCheckCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly VersionCheckRuntimeConfiguration _runtimeConfiguration;
    private readonly Func<IAutomaticVersionCheckLifecycleGate?>
        _lifecycleGateFactory;
    private readonly Func<IVersionCheckService> _serviceFactory;
    private readonly IAutomaticVersionCheckPresenter _presenter;
    private IAutomaticVersionCheckLifecycleGate? _lifecycleGate;
    private int _disposed;
    private int _started;

    public AutomaticVersionCheckCoordinator(
        VersionCheckRuntimeConfiguration runtimeConfiguration,
        Func<IAutomaticVersionCheckLifecycleGate?> lifecycleGateFactory,
        Func<IVersionCheckService> serviceFactory,
        IAutomaticVersionCheckPresenter presenter)
    {
        _runtimeConfiguration = runtimeConfiguration ??
            throw new ArgumentNullException(nameof(runtimeConfiguration));
        _lifecycleGateFactory = lifecycleGateFactory ??
            throw new ArgumentNullException(nameof(lifecycleGateFactory));
        _serviceFactory = serviceFactory ??
            throw new ArgumentNullException(nameof(serviceFactory));
        _presenter = presenter ??
            throw new ArgumentNullException(nameof(presenter));
    }

    public async Task<AutomaticVersionCheckRunResult> RunAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return AutomaticVersionCheckRunResult.Failed;
        }

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return AutomaticVersionCheckRunResult.AlreadyRun;
        }

        AutomaticVersionCheckRunResult? unavailable =
            MapUnavailableConfiguration(_runtimeConfiguration.Availability);
        if (unavailable is not null)
        {
            return unavailable.Value;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return AutomaticVersionCheckRunResult.Cancelled;
        }

        ReleaseVersion? currentVersion =
            _runtimeConfiguration.CurrentVersion;
        VersionCheckConfiguration? serviceConfiguration =
            _runtimeConfiguration.ServiceConfiguration;
        if (currentVersion is null || serviceConfiguration is null)
        {
            return AutomaticVersionCheckRunResult.Failed;
        }

        IAutomaticVersionCheckLifecycleGate? lifecycleGate;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return AutomaticVersionCheckRunResult.Failed;
            }

            _lifecycleGate ??= _lifecycleGateFactory();
            lifecycleGate = _lifecycleGate;
        }

        if (lifecycleGate is null)
        {
            return AutomaticVersionCheckRunResult.LifecycleUnavailable;
        }

        AutomaticVersionCheckRunResult? claimFailure =
            MapClaimFailure(lifecycleGate.TryClaim());
        if (claimFailure is not null)
        {
            return claimFailure.Value;
        }

        IVersionCheckService? service = null;
        try
        {
            service = _serviceFactory();
            VersionCheckResult result = await service.CheckAsync(
                currentVersion.Value,
                cancellationToken);
            if (result.Outcome == VersionCheckOutcome.UpdateAvailable)
            {
                if (result.LatestVersion is null ||
                    result.ReleasePageUri is null ||
                    result.ReleasePageUri !=
                        serviceConfiguration.ReleasePageUri)
                {
                    return AutomaticVersionCheckRunResult.InvalidResponse;
                }

                _presenter.ShowUpdateAvailable(
                    currentVersion.Value,
                    result.LatestVersion.Value,
                    serviceConfiguration.ReleasePageUri);
            }

            return MapOutcome(result.Outcome);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return AutomaticVersionCheckRunResult.Cancelled;
        }
        catch (Exception)
        {
            return AutomaticVersionCheckRunResult.Failed;
        }
        finally
        {
            if (service is IDisposable disposable)
            {
                disposable.Dispose();
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

            _lifecycleGate?.Dispose();
            _lifecycleGate = null;
        }
    }

    private static AutomaticVersionCheckRunResult? MapUnavailableConfiguration(
        VersionCheckAvailability availability) =>
        availability switch
        {
            VersionCheckAvailability.Available => null,
            VersionCheckAvailability.DevelopmentDisabled =>
                AutomaticVersionCheckRunResult.DevelopmentDisabled,
            VersionCheckAvailability.StableChannelNotConfigured =>
                AutomaticVersionCheckRunResult.StableChannelNotConfigured,
            VersionCheckAvailability.InvalidCurrentVersion =>
                AutomaticVersionCheckRunResult.InvalidCurrentVersion,
            _ => AutomaticVersionCheckRunResult.Failed
        };

    private static AutomaticVersionCheckRunResult? MapClaimFailure(
        AutomaticVersionCheckClaimResult result) =>
        result switch
        {
            AutomaticVersionCheckClaimResult.Claimed => null,
            AutomaticVersionCheckClaimResult.AlreadyClaimed =>
                AutomaticVersionCheckRunResult.AlreadyChecked,
            AutomaticVersionCheckClaimResult.DevelopmentDisabled =>
                AutomaticVersionCheckRunResult.DevelopmentDisabled,
            AutomaticVersionCheckClaimResult.Unavailable =>
                AutomaticVersionCheckRunResult.LifecycleUnavailable,
            _ => AutomaticVersionCheckRunResult.Failed
        };

    private static AutomaticVersionCheckRunResult MapOutcome(
        VersionCheckOutcome outcome) =>
        outcome switch
        {
            VersionCheckOutcome.UpdateAvailable =>
                AutomaticVersionCheckRunResult.UpdateAvailable,
            VersionCheckOutcome.UpToDate =>
                AutomaticVersionCheckRunResult.UpToDate,
            VersionCheckOutcome.NetworkFailure =>
                AutomaticVersionCheckRunResult.NetworkFailure,
            VersionCheckOutcome.InvalidResponse =>
                AutomaticVersionCheckRunResult.InvalidResponse,
            _ => AutomaticVersionCheckRunResult.Failed
        };
}

internal static class AutomaticVersionCheckProductionComposition
{
    public static AutomaticVersionCheckCoordinator Create(
        AgenTallyRuntimeProfile profile,
        Assembly applicationAssembly,
        IAutomaticVersionCheckPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(applicationAssembly);
        ArgumentNullException.ThrowIfNull(presenter);

        VersionCheckRuntimeConfiguration runtimeConfiguration =
            VersionCheckProductionConfiguration.Resolve(
                profile.Channel,
                applicationAssembly);
        return new AutomaticVersionCheckCoordinator(
            runtimeConfiguration,
            () => new AutomaticVersionCheckLifecycleGate(profile),
            () => VersionCheckProductionServiceFactory.Create(
                runtimeConfiguration),
            presenter);
    }
}
