using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.KimiCode;

public static class KimiCodeSourceIdentity
{
    public static string InstanceId(string kimiHome)
        => InstanceId(kimiHome, "cli");

    internal static string InstanceId(string kimiHome, string instanceKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKind);
        string normalized = NormalizePath(kimiHome).ToUpperInvariant();
        return $"kimi-code:{instanceKind}:windows:{ShortHash(normalized, 16)}";
    }

    public static string EntityId(string filePath)
    {
        string normalized = NormalizePath(filePath).ToUpperInvariant();
        return $"kimi-code:wire:{ShortHash(normalized, 24)}";
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

    public static string AgentSessionId(string rootSessionId, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return string.Equals(agentId, "main", StringComparison.Ordinal)
            ? rootSessionId
            : $"{rootSessionId}:agent:{ShortHash(agentId, 24)}";
    }

    private static string ShortHash(string value, int characters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..characters]
            .ToLowerInvariant();
}
