using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.Qoder;

public enum QoderEdition
{
    International,
    China
}

public static class QoderSourceIdentity
{
    public const string DatabaseFileName = "local.db";

    public static string AgentId(QoderEdition edition) =>
        edition == QoderEdition.China ? "qoder-cn" : "qoder";

    public static string DesktopInstanceId(string root, QoderEdition edition) =>
        $"{AgentId(edition)}:desktop:windows:{ShortHash(NormalizePath(root).ToUpperInvariant(), 16)}";

    public static string CliInstanceId(string root) =>
        $"qoder:cli:windows:{ShortHash(NormalizePath(root).ToUpperInvariant(), 16)}";

    public static string DesktopEntityId(string path, QoderEdition edition) =>
        $"{AgentId(edition)}:sqlite:{ShortHash(CanonicalDatabasePath(path).ToUpperInvariant(), 24)}";

    public static string CliEntityId(string path) =>
        $"qoder:transcript:{ShortHash(NormalizePath(path).ToUpperInvariant(), 24)}";

    public static string SourceFingerprint(string kind, string path) =>
        HashIdentity(kind, NormalizePath(path).ToUpperInvariant());

    public static string HashIdentity(string kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{kind}\0{value}")))
            .ToLowerInvariant();
    }

    public static string NormalizePath(string path) => CodexSourceIdentity.NormalizePath(path);

    public static string DatabasePath(string root) => Path.Combine(
        NormalizePath(root),
        "SharedClientCache",
        "cache",
        "db",
        DatabaseFileName);

    public static string CanonicalDatabasePath(string path)
    {
        string normalized = NormalizePath(path);
        string file = Path.GetFileName(normalized);
        return string.Equals(file, $"{DatabaseFileName}-wal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(file, $"{DatabaseFileName}-shm", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(normalized)!, DatabaseFileName)
            : normalized;
    }

    public static bool IsDatabaseChangePath(string path)
    {
        string file = Path.GetFileName(path);
        return string.Equals(file, DatabaseFileName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(file, $"{DatabaseFileName}-wal", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortHash(string value, int characters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..characters]
            .ToLowerInvariant();
}
