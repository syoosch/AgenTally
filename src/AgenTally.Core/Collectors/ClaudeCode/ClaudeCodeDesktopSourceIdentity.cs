using System.Security.Cryptography;
using System.Text;

namespace AgenTally.Core.Collectors.ClaudeCode;

public static class ClaudeCodeDesktopSourceIdentity
{
    public const string PackageFamily = "Claude_pzs8sxrjxfjjc";

    public static string? DefaultRoot()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? null
            : ClaudeCodeSourceIdentity.NormalizePath(Path.Combine(
                localAppData,
                "Packages",
                PackageFamily,
                "LocalCache",
                "Roaming",
                "Claude",
                "local-agent-mode-sessions"));
    }

    public static string InstanceId(string rootPath)
    {
        string normalized = ClaudeCodeSourceIdentity.NormalizePath(rootPath)
            .ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(normalized)))
            [..16]
            .ToLowerInvariant();
        return $"claude-code:desktop-local-agent:windows:{hash}";
    }
}
