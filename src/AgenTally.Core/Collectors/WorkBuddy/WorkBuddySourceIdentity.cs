using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.WorkBuddy;

public static class WorkBuddySourceIdentity
{
    public static string InstanceId(string workBuddyHome)
    {
        string normalized = NormalizePath(workBuddyHome).ToUpperInvariant();
        return $"workbuddy:desktop:windows:{ShortHash(normalized, 16)}";
    }

    public static string EntityId(string filePath)
    {
        string normalized = NormalizePath(filePath).ToUpperInvariant();
        return $"workbuddy:session:{ShortHash(normalized, 24)}";
    }

    public static string NormalizePath(string path) =>
        CodexSourceIdentity.NormalizePath(path);

    public static string HashIdentity(string kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{kind}\0{value}")))
            .ToLowerInvariant();
    }

    private static string ShortHash(string value, int characters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..characters]
            .ToLowerInvariant();
}
