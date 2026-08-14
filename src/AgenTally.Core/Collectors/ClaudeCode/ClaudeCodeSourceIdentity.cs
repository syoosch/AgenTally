using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.ClaudeCode;

public static class ClaudeCodeSourceIdentity
{
    public static string InstanceId(string claudeHome)
    {
        string normalized = NormalizePath(claudeHome).ToUpperInvariant();
        return $"claude-code:cli:windows:{Hash(normalized, 16)}";
    }

    public static string EntityId(string filePath)
    {
        string normalized = NormalizePath(filePath).ToUpperInvariant();
        return $"claude-code:transcript:{Hash(normalized, 24)}";
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

    public static int StableOrdinal(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }

    private static string Hash(string value, int characters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..characters]
            .ToLowerInvariant();
}
