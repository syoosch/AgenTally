using System.Reflection;
using AgenTally.Storage.Runtime;

namespace AgenTally.UI.Updates;

internal enum ManualVersionCheckStatus
{
    UpdateAvailable,
    UpToDate,
    NetworkFailure,
    InvalidResponse,
    DevelopmentDisabled,
    StableChannelNotConfigured,
    InvalidCurrentVersion,
    Unavailable,
    Cancelled,
    Failed
}

internal sealed record ManualVersionCheckResult(
    ManualVersionCheckStatus Status,
    ReleaseVersion? CurrentVersion = null,
    ReleaseVersion? LatestVersion = null,
    Uri? ReleasePageUri = null);

internal interface IManualVersionCheckCoordinator
{
    Task<ManualVersionCheckResult> CheckAsync(
        CancellationToken cancellationToken);
}

internal sealed class ManualVersionCheckCoordinator(
    VersionCheckRuntimeConfiguration runtimeConfiguration,
    Func<IVersionCheckService> serviceFactory) :
    IManualVersionCheckCoordinator
{
    private readonly VersionCheckRuntimeConfiguration _runtimeConfiguration =
        runtimeConfiguration ??
        throw new ArgumentNullException(nameof(runtimeConfiguration));
    private readonly Func<IVersionCheckService> _serviceFactory =
        serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));

    public async Task<ManualVersionCheckResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        ManualVersionCheckStatus? unavailable =
            MapUnavailableConfiguration(_runtimeConfiguration.Availability);
        if (unavailable is not null)
        {
            return new ManualVersionCheckResult(unavailable.Value);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new ManualVersionCheckResult(
                ManualVersionCheckStatus.Cancelled);
        }

        ReleaseVersion? currentVersion =
            _runtimeConfiguration.CurrentVersion;
        VersionCheckConfiguration? serviceConfiguration =
            _runtimeConfiguration.ServiceConfiguration;
        if (currentVersion is null || serviceConfiguration is null)
        {
            return new ManualVersionCheckResult(
                ManualVersionCheckStatus.Failed);
        }

        IVersionCheckService? service = null;
        try
        {
            service = _serviceFactory();
            VersionCheckResult result = await service.CheckAsync(
                currentVersion.Value,
                cancellationToken);
            if ((result.Outcome is VersionCheckOutcome.UpdateAvailable or
                 VersionCheckOutcome.UpToDate) &&
                result.LatestVersion is null)
            {
                return new ManualVersionCheckResult(
                    ManualVersionCheckStatus.InvalidResponse,
                    currentVersion);
            }

            if (result.Outcome == VersionCheckOutcome.UpdateAvailable)
            {
                if (result.ReleasePageUri is null ||
                    result.ReleasePageUri !=
                        serviceConfiguration.ReleasePageUri)
                {
                    return new ManualVersionCheckResult(
                        ManualVersionCheckStatus.InvalidResponse,
                        currentVersion);
                }

                return new ManualVersionCheckResult(
                    ManualVersionCheckStatus.UpdateAvailable,
                    currentVersion,
                    result.LatestVersion,
                    serviceConfiguration.ReleasePageUri);
            }

            return result.Outcome switch
            {
                VersionCheckOutcome.UpToDate => new ManualVersionCheckResult(
                    ManualVersionCheckStatus.UpToDate,
                    currentVersion,
                    result.LatestVersion),
                VersionCheckOutcome.NetworkFailure =>
                    new ManualVersionCheckResult(
                        ManualVersionCheckStatus.NetworkFailure,
                        currentVersion),
                VersionCheckOutcome.InvalidResponse =>
                    new ManualVersionCheckResult(
                        ManualVersionCheckStatus.InvalidResponse,
                        currentVersion),
                _ => new ManualVersionCheckResult(
                    ManualVersionCheckStatus.Failed,
                    currentVersion)
            };
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return new ManualVersionCheckResult(
                ManualVersionCheckStatus.Cancelled,
                currentVersion);
        }
        catch (Exception)
        {
            return new ManualVersionCheckResult(
                ManualVersionCheckStatus.Failed,
                currentVersion);
        }
        finally
        {
            if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static ManualVersionCheckStatus? MapUnavailableConfiguration(
        VersionCheckAvailability availability) =>
        availability switch
        {
            VersionCheckAvailability.Available => null,
            VersionCheckAvailability.DevelopmentDisabled =>
                ManualVersionCheckStatus.DevelopmentDisabled,
            VersionCheckAvailability.StableChannelNotConfigured =>
                ManualVersionCheckStatus.StableChannelNotConfigured,
            VersionCheckAvailability.InvalidCurrentVersion =>
                ManualVersionCheckStatus.InvalidCurrentVersion,
            _ => ManualVersionCheckStatus.Failed
        };
}

internal sealed class UnavailableManualVersionCheckCoordinator :
    IManualVersionCheckCoordinator
{
    public Task<ManualVersionCheckResult> CheckAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new ManualVersionCheckResult(
            cancellationToken.IsCancellationRequested
                ? ManualVersionCheckStatus.Cancelled
                : ManualVersionCheckStatus.Unavailable));
}

internal static class ManualVersionCheckProductionComposition
{
    public static IManualVersionCheckCoordinator Create(
        AgenTallyRuntimeProfile profile,
        Assembly applicationAssembly)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(applicationAssembly);
        VersionCheckRuntimeConfiguration runtimeConfiguration =
            VersionCheckProductionConfiguration.Resolve(
                profile.Channel,
                applicationAssembly);
        return new ManualVersionCheckCoordinator(
            runtimeConfiguration,
            () => VersionCheckProductionServiceFactory.Create(
                runtimeConfiguration));
    }
}
