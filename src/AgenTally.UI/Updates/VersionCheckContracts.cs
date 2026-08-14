using System.Globalization;

namespace AgenTally.UI.Updates;

internal readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    private const int MaximumTextLength = 64;

    public ReleaseVersion(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public int CompareTo(ReleaseVersion other)
    {
        int majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        int minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0
            ? minorComparison
            : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");

    public static bool TryParse(
        string? value,
        out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumTextLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        string[] components = value.Split(
            '.',
            StringSplitOptions.None);
        if (components.Length != 3 ||
            !TryParseComponent(components[0], out int major) ||
            !TryParseComponent(components[1], out int minor) ||
            !TryParseComponent(components[2], out int patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    private static bool TryParseComponent(
        string component,
        out int value)
    {
        value = 0;
        return component.Length > 0 &&
            int.TryParse(
                component,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);
    }
}

internal sealed class VersionCheckConfiguration
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(30);

    public VersionCheckConfiguration(
        Uri manifestUri,
        Uri releasePageUri,
        TimeSpan? timeout = null)
    {
        ManifestUri = ValidateHttpsUri(
            manifestUri,
            nameof(manifestUri),
            allowFragment: false);
        ReleasePageUri = ValidateHttpsUri(
            releasePageUri,
            nameof(releasePageUri),
            allowFragment: true);
        Timeout = timeout ?? DefaultTimeout;
        if (Timeout < MinimumTimeout || Timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                $"Version check timeout must be between {MinimumTimeout} and {MaximumTimeout}.");
        }
    }

    public Uri ManifestUri { get; }

    public Uri ReleasePageUri { get; }

    public TimeSpan Timeout { get; }

    private static Uri ValidateHttpsUri(
        Uri uri,
        string parameterName,
        bool allowFragment)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!allowFragment && !string.IsNullOrEmpty(uri.Fragment)))
        {
            throw new ArgumentException(
                "Version check URIs must be absolute HTTPS addresses without user information.",
                parameterName);
        }

        return uri;
    }
}

internal enum VersionCheckOutcome
{
    UpdateAvailable,
    UpToDate,
    NetworkFailure,
    InvalidResponse
}

internal sealed record VersionCheckResult(
    VersionCheckOutcome Outcome,
    ReleaseVersion CurrentVersion,
    ReleaseVersion? LatestVersion,
    Uri? ReleasePageUri);

internal interface IVersionCheckService
{
    Task<VersionCheckResult> CheckAsync(
        ReleaseVersion currentVersion,
        CancellationToken cancellationToken);
}
