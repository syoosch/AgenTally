using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.QwenCode;

public static class QwenCodeSourceIdentity
{
    public static string InstanceId(string qwenHome) =>
        $"qwen-code:cli:windows:{ShortHash(NormalizePath(qwenHome).ToUpperInvariant(), 16)}";

    public static string EntityId(string path) =>
        $"qwen-code:chat:{ShortHash(NormalizePath(path).ToUpperInvariant(), 24)}";

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
