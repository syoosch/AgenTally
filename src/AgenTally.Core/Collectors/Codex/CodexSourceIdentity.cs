using System.Security.Cryptography;
using System.Text;

namespace AgenTally.Core.Collectors.Codex;

public static class CodexSourceIdentity
{
    public static string InstanceId(string codexHome)
    {
        string normalized = NormalizePath(codexHome).ToUpperInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $"codex:windows:{hash.ToLowerInvariant()}";
    }

    public static string EntityId(string filePath)
    {
        string name = Path.GetFileName(filePath).ToLowerInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..24];
        return $"codex:rollout:{hash.ToLowerInvariant()}";
    }

    public static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
        }

        return fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }
}
