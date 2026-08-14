using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace AgenTally.Storage.Runtime;

public enum AgenTallyChannel
{
    Development,
    Stable
}

public static class AgenTallyBuild
{
    private const string ChannelMetadataName = "AgenTallyChannel";

    public static AgenTallyChannel Channel { get; } = ReadChannel();

    private static AgenTallyChannel ReadChannel()
    {
        string? value = typeof(AgenTallyBuild).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(
                attribute.Key,
                ChannelMetadataName,
                StringComparison.Ordinal))?
            .Value;
        return value switch
        {
            "Development" => AgenTallyChannel.Development,
            "Stable" => AgenTallyChannel.Stable,
            _ => throw new InvalidOperationException(
                "AgenTally build channel metadata is missing or invalid.")
        };
    }
}

public sealed record AgenTallyRuntimeProfile
{
    public const string DevelopmentCodexHomeEnvironmentVariable =
        "AGENTALLY_CODEX_HOME";
    private const string SolutionMarker = "AgenTally.sln";
    private const string RepositoryMarker = ".agentally-root";

    private AgenTallyRuntimeProfile(
        AgenTallyChannel channel,
        string repositoryRoot,
        string applicationRoot,
        string dataRoot,
        string runtimeRoot,
        string logRoot,
        string tempRoot,
        string databasePath,
        string codexHome,
        string coreExecutablePath,
        string uiExecutablePath)
    {
        Channel = channel;
        RepositoryRoot = repositoryRoot;
        ApplicationRoot = applicationRoot;
        DataRoot = dataRoot;
        RuntimeRoot = runtimeRoot;
        LogRoot = logRoot;
        TempRoot = tempRoot;
        DatabasePath = databasePath;
        CodexHome = codexHome;
        CoreExecutablePath = coreExecutablePath;
        UiExecutablePath = uiExecutablePath;
        DisplayName = channel == AgenTallyChannel.Development
            ? "AgenTally Dev"
            : "AgenTally";

        ProfileId = HashIdentity(
            $"profile|{channel}|{NormalizeIdentity(databasePath)}|{NormalizeIdentity(codexHome)}");
        UiPreferencesPath = Path.Combine(
            dataRoot,
            $"ui-preferences-{ProfileId}.json");
        DataManagementStatePath = Path.Combine(
            dataRoot,
            $"data-management-{ProfileId}.json");
        StartupRegistrationStatePath = Path.Combine(
            dataRoot,
            $"startup-registration-{ProfileId}.json");
        string userHash = CurrentUserHash();
        ShutdownEventName =
            $@"Local\AgenTally.AppShutdown.{channel}.{userHash}.{ProfileId}";
        CoreMaintenanceShutdownEventName =
            $@"Local\AgenTally.CoreMaintenanceShutdown.{channel}.{userHash}.{ProfileId}";
        UiInstanceLeaseName =
            $@"Local\AgenTally.UIInstance.{channel}.{userHash}.{ProfileId}";
        UiActivationEventName =
            $@"Local\AgenTally.UIActivation.{channel}.{userHash}.{ProfileId}";
        VersionCheckLifecycleEventName =
            $@"Local\AgenTally.VersionCheckLifecycle.{channel}.{userHash}.{ProfileId}";
        PriceCommandPipeName =
            $"AgenTally.PriceCommands.{channel}.{userHash}.{ProfileId}";
        SourceLeaseName = CreateSourceLeaseName(codexHome);
        DatabaseLeaseName = CreateDatabaseLeaseName(databasePath);
        StatusPath = Path.Combine(runtimeRoot, "core-status.json");
        DataMaintenanceRequestPath = Path.Combine(
            runtimeRoot,
            $"data-maintenance-request-{ProfileId}.json");
        ShutdownRequestPath = Path.Combine(
            runtimeRoot,
            $"application-shutdown-request-{ProfileId}.json");
    }

    public AgenTallyChannel Channel { get; }

    public string DisplayName { get; }

    public string RepositoryRoot { get; }

    public string ApplicationRoot { get; }

    public string DataRoot { get; }

    public string RuntimeRoot { get; }

    public string LogRoot { get; }

    public string TempRoot { get; }

    public string DatabasePath { get; }

    public string UiPreferencesPath { get; }

    public string DataManagementStatePath { get; }

    public string StartupRegistrationStatePath { get; }

    public string CodexHome { get; }

    public string CoreExecutablePath { get; }

    public string UiExecutablePath { get; }

    public string StatusPath { get; }

    public string DataMaintenanceRequestPath { get; }

    public string ShutdownRequestPath { get; }

    public string ProfileId { get; }

    public string ShutdownEventName { get; }

    public string CoreMaintenanceShutdownEventName { get; }

    public string UiInstanceLeaseName { get; }

    public string UiActivationEventName { get; }

    public string VersionCheckLifecycleEventName { get; }

    public string PriceCommandPipeName { get; }

    public string SourceLeaseName { get; }

    public string DatabaseLeaseName { get; }

    public static AgenTallyRuntimeProfile CreateDefault(
        string? applicationBaseDirectory = null)
    {
        string appRoot = Path.GetFullPath(
            applicationBaseDirectory ?? AppContext.BaseDirectory);
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException(
                "Unable to determine the current user profile.");
        }

        return AgenTallyBuild.Channel switch
        {
            AgenTallyChannel.Development => CreateDevelopment(
                FindRepositoryRoot(appRoot),
                ResolveDevelopmentCodexHome(
                    FindRepositoryRoot(appRoot),
                    userProfile,
                    Environment.GetEnvironmentVariable(
                        DevelopmentCodexHomeEnvironmentVariable))),
            AgenTallyChannel.Stable => CreateStable(
                appRoot,
                RequireLocalAppData(),
                userProfile),
            _ => throw new InvalidOperationException("Unsupported AgenTally channel.")
        };
    }

    public static AgenTallyRuntimeProfile CreateDevelopment(
        string repositoryRoot,
        string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        string repository = Path.GetFullPath(repositoryRoot);
        ValidateRepositoryRoot(repository);
        RejectReparseAncestors(repository);

        string developmentRoot = Path.Combine(
            repository,
            "artifacts",
            "development");
        string applicationRoot = Path.Combine(developmentRoot, "app");
        string dataRoot = Path.Combine(developmentRoot, "data");
        string runtimeRoot = Path.Combine(developmentRoot, "runtime");
        string logRoot = Path.Combine(developmentRoot, "logs");
        string tempRoot = Path.Combine(developmentRoot, "temp");
        return new AgenTallyRuntimeProfile(
            AgenTallyChannel.Development,
            repository,
            applicationRoot,
            dataRoot,
            runtimeRoot,
            logRoot,
            tempRoot,
            Path.Combine(dataRoot, "agentally.db"),
            Path.GetFullPath(codexHome),
            Path.Combine(applicationRoot, "Core", "AgenTally.Core.exe"),
            Path.Combine(applicationRoot, "AgenTally.UI.exe"));
    }

    public static string ResolveDevelopmentCodexHome(
        string repositoryRoot,
        string userProfile,
        string? configuredCodexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        string repository = Path.GetFullPath(repositoryRoot);
        ValidateRepositoryRoot(repository);
        string selected = string.IsNullOrWhiteSpace(configuredCodexHome)
            ? Path.Combine(Path.GetFullPath(userProfile), ".codex")
            : Path.GetFullPath(configuredCodexHome.Trim());
        return selected;
    }

    public static AgenTallyRuntimeProfile CreateStable(
        string applicationRoot,
        string localAppData,
        string userProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        string stableRoot = Path.Combine(
            Path.GetFullPath(localAppData),
            "AgenTally",
            "Stable");
        string dataRoot = Path.Combine(stableRoot, "data");
        string runtimeRoot = Path.Combine(stableRoot, "runtime");
        string logRoot = Path.Combine(stableRoot, "logs");
        string tempRoot = Path.Combine(stableRoot, "temp");
        string appRoot = Path.GetFullPath(applicationRoot);
        return new AgenTallyRuntimeProfile(
            AgenTallyChannel.Stable,
            string.Empty,
            appRoot,
            dataRoot,
            runtimeRoot,
            logRoot,
            tempRoot,
            Path.Combine(dataRoot, "agentally.db"),
            Path.Combine(Path.GetFullPath(userProfile), ".codex"),
            Path.Combine(appRoot, "AgenTally.Core.exe"),
            Path.Combine(appRoot, "AgenTally.UI.exe"));
    }

    public static string FindRepositoryRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionMarker)) &&
                File.Exists(Path.Combine(current.FullName, RepositoryMarker)))
            {
                RejectReparseAncestors(current.FullName);
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Development runtime requires a verified AgenTally repository root.");
    }

    public static string CreateSourceLeaseName(string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        string sourceHash = HashIdentity(
            $"source|{NormalizeIdentity(codexHome)}");
        return $@"Local\AgenTally.Source.{CurrentUserHash()}.{sourceHash}";
    }

    public static string CreateDatabaseLeaseName(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string databaseHash = HashIdentity(
            $"database|{NormalizeIdentity(databasePath)}");
        return $@"Local\AgenTally.Database.{CurrentUserHash()}.{databaseHash}";
    }

    public bool IsDevelopmentOwnedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Channel != AgenTallyChannel.Development)
        {
            return false;
        }

        try
        {
            string candidate = Path.GetFullPath(path);
            string developmentRoot = Path.Combine(
                RepositoryRoot,
                "artifacts",
                "development");
            if (!IsWithinOrEqual(candidate, developmentRoot))
            {
                return false;
            }

            RejectReparseAncestors(candidate);
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                InvalidOperationException or
                IOException or
                NotSupportedException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string RequireLocalAppData()
    {
        string value = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                "Unable to determine LocalApplicationData.")
            : value;
    }

    private static void ValidateRepositoryRoot(string repository)
    {
        if (!File.Exists(Path.Combine(repository, SolutionMarker)) ||
            !File.Exists(Path.Combine(repository, RepositoryMarker)))
        {
            throw new InvalidOperationException(
                "Development runtime requires AgenTally.sln and .agentally-root markers.");
        }
    }

    private static bool IsWithinOrEqual(string candidate, string root)
    {
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(
                   normalizedCandidate,
                   normalizedRoot,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparseAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "AgenTally runtime paths cannot traverse a reparse point.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static string NormalizeIdentity(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();

    private static string HashIdentity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private static string CurrentUserHash() => HashIdentity(
        $"user|{Environment.UserDomainName}|{Environment.UserName}");
}
