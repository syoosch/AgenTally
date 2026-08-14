using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.GeminiCli;

internal static class GeminiCliSourceIdentity
{
    internal static string NormalizePath(string path) =>
        CodexSourceIdentity.NormalizePath(path);

    internal static string InstanceId(string root) =>
        $"gemini-cli:windows:{ShortHash(NormalizePath(root).ToUpperInvariant(), 16)}";

    internal static string EntityId(string path) =>
        $"gemini-cli:transcript:{ShortHash(NormalizePath(path).ToUpperInvariant(), 24)}";

    internal static string SourceFingerprint(string path) =>
        HashIdentity("gemini-cli-source", NormalizePath(path).ToUpperInvariant());

    internal static string HashIdentity(string kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{kind}\0{value}")))
            .ToLowerInvariant();
    }

    internal static bool IsSupportedFile(string path)
    {
        string extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Path.GetFileName(path).StartsWith("session-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileName(Path.GetDirectoryName(path)),
            "chats",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortHash(string value, int characters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            [..characters]
            .ToLowerInvariant();
}
