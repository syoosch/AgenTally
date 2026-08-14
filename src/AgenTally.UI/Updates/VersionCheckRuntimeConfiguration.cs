using System.Reflection;
using AgenTally.Storage.Runtime;

namespace AgenTally.UI.Updates;

internal enum VersionCheckAvailability
{
    Available,
    DevelopmentDisabled,
    StableChannelNotConfigured,
    InvalidCurrentVersion
}

internal sealed record VersionCheckRuntimeConfiguration(
    VersionCheckAvailability Availability,
    ReleaseVersion? CurrentVersion,
    VersionCheckConfiguration? ServiceConfiguration)
{
    public bool CanCheck =>
        Availability == VersionCheckAvailability.Available;
}

internal static class VersionCheckRuntimeConfigurationResolver
{
    public static VersionCheckRuntimeConfiguration Resolve(
        AgenTallyChannel channel,
        Version? applicationVersion,
        Func<VersionCheckConfiguration?> stableConfigurationFactory)
    {
        ArgumentNullException.ThrowIfNull(stableConfigurationFactory);

        if (channel == AgenTallyChannel.Development)
        {
            return new VersionCheckRuntimeConfiguration(
                VersionCheckAvailability.DevelopmentDisabled,
                CurrentVersion: null,
                ServiceConfiguration: null);
        }

        if (channel != AgenTallyChannel.Stable)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channel),
                channel,
                "Unsupported AgenTally channel.");
        }

        if (!TryResolveCurrentVersion(
                applicationVersion,
                out ReleaseVersion currentVersion))
        {
            return new VersionCheckRuntimeConfiguration(
                VersionCheckAvailability.InvalidCurrentVersion,
                CurrentVersion: null,
                ServiceConfiguration: null);
        }

        VersionCheckConfiguration? serviceConfiguration =
            stableConfigurationFactory();
        return serviceConfiguration is null
            ? new VersionCheckRuntimeConfiguration(
                VersionCheckAvailability.StableChannelNotConfigured,
                currentVersion,
                ServiceConfiguration: null)
            : new VersionCheckRuntimeConfiguration(
                VersionCheckAvailability.Available,
                currentVersion,
                serviceConfiguration);
    }

    private static bool TryResolveCurrentVersion(
        Version? applicationVersion,
        out ReleaseVersion currentVersion)
    {
        currentVersion = default;
        if (applicationVersion is null ||
            applicationVersion.Major < 0 ||
            applicationVersion.Minor < 0 ||
            applicationVersion.Build < 0 ||
            applicationVersion.Revision > 0)
        {
            return false;
        }

        currentVersion = new ReleaseVersion(
            applicationVersion.Major,
            applicationVersion.Minor,
            applicationVersion.Build);
        return true;
    }
}

internal static class VersionCheckProductionConfiguration
{
    public static VersionCheckRuntimeConfiguration Resolve(
        AgenTallyChannel channel,
        Assembly applicationAssembly)
    {
        ArgumentNullException.ThrowIfNull(applicationAssembly);
        return VersionCheckRuntimeConfigurationResolver.Resolve(
            channel,
            applicationAssembly.GetName().Version,
            TryCreateStableConfiguration);
    }

    private static VersionCheckConfiguration? TryCreateStableConfiguration()
    {
        // The formal Stable channel is intentionally undecided. Returning null
        // keeps both automatic and manual checks unavailable and guarantees
        // that no HTTP client or request can be created from production wiring.
        return null;
    }
}
