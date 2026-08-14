using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.Zcode;

public static class ZcodeSourceIdentity
{
    public const string DatabaseFileName = "db.sqlite";

    public static string InstanceId(string zcodeHome)
    {
        string normalized = NormalizePath(zcodeHome).ToUpperInvariant();
        return $"zcode:desktop:windows:{ShortHash(normalized, 16)}";
    }

    public static string EntityId(string path)
    {
        string normalized = CanonicalDatabasePath(path).ToUpperInvariant();
        return $"zcode:sqlite:{ShortHash(normalized, 24)}";
    }

    public static string SourceFingerprint(string databasePath) =>
        HashIdentity("zcode-sqlite-source", CanonicalDatabasePath(databasePath));

    public static string HashIdentity(string kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{kind}\0{value}")))
            .ToLowerInvariant();
    }

    public static string NormalizePath(string path) =>
        CodexSourceIdentity.NormalizePath(path);

    public static string CanonicalDatabasePath(string path)
    {
        string normalized = NormalizePath(path);
        string fileName = Path.GetFileName(normalized);
        if (string.Equals(
                fileName,
                $"{DatabaseFileName}-wal",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fileName,
                $"{DatabaseFileName}-shm",
                StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(
                Path.GetDirectoryName(normalized) ?? normalized,
                DatabaseFileName);
        }

        return normalized;
    }

    public static bool IsDatabaseChangePath(string path)
    {
        // The SHM sidecar is deliberately excluded: readers may update its lock
        // state even when no persistent usage row changed.
        string fileName = Path.GetFileName(path);
        return string.Equals(
                   fileName,
                   DatabaseFileName,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   fileName,
                   $"{DatabaseFileName}-wal",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortHash(string value, int characters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..characters]
            .ToLowerInvariant();
}
