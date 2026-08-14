using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.OpenCode;

internal static class OpenCodeSourceIdentity
{
    internal static string NormalizePath(string path) =>
        CodexSourceIdentity.NormalizePath(path);

    internal static string InstanceId(string root) =>
        $"opencode:windows:{ShortHash(NormalizePath(root).ToUpperInvariant(), 16)}";

    internal static string EntityId(string path) =>
        $"opencode:source:{ShortHash(NormalizePath(path).ToUpperInvariant(), 24)}";

    internal static string SourceFingerprint(string path) =>
        HashIdentity("opencode-source", NormalizePath(path).ToUpperInvariant());

    internal static string HashIdentity(string kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{kind}\0{value}")))
            .ToLowerInvariant();
    }

    internal static bool IsDatabase(string path) =>
        Path.GetFileName(path).StartsWith("opencode", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDatabaseChangePath(string path)
    {
        string file = Path.GetFileName(path);
        return IsDatabase(path) ||
            (file.StartsWith("opencode", StringComparison.OrdinalIgnoreCase) &&
             (file.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
              file.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)));
    }

    internal static string CanonicalEntityPath(string path)
    {
        string normalized = NormalizePath(path);
        string file = Path.GetFileName(normalized);
        if (file.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[..^4];
        }
        if (file.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[..^4];
        }
        return normalized;
    }

    private static string ShortHash(string value, int characters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..characters]
            .ToLowerInvariant();
}
