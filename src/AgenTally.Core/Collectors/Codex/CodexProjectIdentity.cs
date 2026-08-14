using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace AgenTally.Core.Collectors.Codex;

internal readonly record struct CodexProjectIdentity(
    string ProjectId,
    string ProjectPath,
    string? RepositoryIdentityHash)
{
    internal const int ProjectIdCharacters = 24;
    internal const int MaxProjectPathCharacters = 32767;
    internal const int RepositoryIdentityHashCharacters = 64;
    private const int MaxRepositoryUrlCharacters = 8192;

    internal static bool TryCreate(
        string? rawCwd,
        out CodexProjectIdentity identity) =>
        TryCreate(rawCwd, rawRepositoryUrl: null, out identity);

    internal static bool TryCreate(
        string? rawCwd,
        string? rawRepositoryUrl,
        out CodexProjectIdentity identity)
    {
        identity = default;
        if (!TryNormalizeProjectPath(rawCwd, out string? projectPath))
        {
            return false;
        }

        string? repositoryIdentityHash = TryNormalizeRepositoryIdentity(
            rawRepositoryUrl,
            out string? repositoryIdentity)
            ? HashIdentity("repository", repositoryIdentity!)
            : null;
        string projectId = repositoryIdentityHash is null
            ? HashValue(projectPath!.ToUpperInvariant())[..ProjectIdCharacters]
            : repositoryIdentityHash[..ProjectIdCharacters];
        identity = new CodexProjectIdentity(
            projectId,
            projectPath!,
            repositoryIdentityHash);
        return true;
    }

    internal static bool TryCreateFromRepositoryHash(
        string? rawCwd,
        string? repositoryIdentityHash,
        out CodexProjectIdentity identity)
    {
        identity = default;
        if (!TryNormalizeProjectPath(rawCwd, out string? projectPath) ||
            !IsRepositoryIdentityHash(repositoryIdentityHash))
        {
            return false;
        }

        identity = new CodexProjectIdentity(
            repositoryIdentityHash![..ProjectIdCharacters],
            projectPath!,
            repositoryIdentityHash);
        return true;
    }

    internal static bool IsRepositoryIdentityHash(string? value) =>
        value is { Length: RepositoryIdentityHashCharacters } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryNormalizeProjectPath(
        string? rawCwd,
        out string? projectPath)
    {
        projectPath = null;
        if (rawCwd is not { Length: > 0 and <= MaxProjectPathCharacters } ||
            string.IsNullOrWhiteSpace(rawCwd) ||
            rawCwd.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(rawCwd))
        {
            return false;
        }

        try
        {
            projectPath = Path.GetFullPath(rawCwd);
            if (projectPath.Length is 0 or > MaxProjectPathCharacters ||
                projectPath.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(projectPath))
            {
                return false;
            }

            string? root = Path.GetPathRoot(projectPath);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            string pathWithoutTrailingSeparators = projectPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string rootWithoutTrailingSeparators = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            bool isRoot = string.Equals(
                pathWithoutTrailingSeparators,
                rootWithoutTrailingSeparators,
                StringComparison.OrdinalIgnoreCase);
            projectPath = isRoot
                ? Path.EndsInDirectorySeparator(root)
                    ? root
                    : root + Path.DirectorySeparatorChar
                : pathWithoutTrailingSeparators;

            if (projectPath.Length is 0 or > MaxProjectPathCharacters)
            {
                return false;
            }

            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                NotSupportedException or
                PathTooLongException or
                SecurityException)
        {
            return false;
        }
    }

    private static bool TryNormalizeRepositoryIdentity(
        string? rawRepositoryUrl,
        out string? repositoryIdentity)
    {
        repositoryIdentity = null;
        if (rawRepositoryUrl is not
                { Length: > 0 and <= MaxRepositoryUrlCharacters } ||
            string.IsNullOrWhiteSpace(rawRepositoryUrl) ||
            rawRepositoryUrl.Any(char.IsControl))
        {
            return false;
        }

        string value = rawRepositoryUrl.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            if (uri.IsFile)
            {
                if (!TryNormalizeProjectPath(uri.LocalPath, out string? localPath))
                {
                    return false;
                }

                repositoryIdentity = $"file/{localPath!.ToUpperInvariant()}";
                return true;
            }

            if (uri.Scheme is not ("http" or "https" or "ssh" or "git") ||
                string.IsNullOrWhiteSpace(uri.IdnHost))
            {
                return false;
            }

            string path = NormalizeRepositoryPath(uri.AbsolutePath);
            if (path.Length == 0)
            {
                return false;
            }

            string host = uri.IsDefaultPort
                ? uri.IdnHost
                : string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{uri.IdnHost}:{uri.Port}");
            repositoryIdentity =
                $"{host.ToUpperInvariant()}/{path}";
            return true;
        }

        int separator = value.IndexOf(':');
        if (value.Contains("://", StringComparison.Ordinal) ||
            separator <= 0 ||
            separator == value.Length - 1)
        {
            return false;
        }

        string authority = value[..separator];
        int userSeparator = authority.LastIndexOf('@');
        string hostPart = userSeparator >= 0
            ? authority[(userSeparator + 1)..]
            : authority;
        string scpPath = NormalizeRepositoryPath(value[(separator + 1)..]);
        if (string.IsNullOrWhiteSpace(hostPart) ||
            hostPart.Any(char.IsWhiteSpace) ||
            scpPath.Length == 0)
        {
            return false;
        }

        repositoryIdentity =
            $"{hostPart.ToUpperInvariant()}/{scpPath}";
        return true;
    }

    private static string NormalizeRepositoryPath(string value)
    {
        string path = value
            .Replace('\\', '/')
            .Trim('/');
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4].TrimEnd('/')
            : path;
    }

    private static string HashIdentity(string kind, string value) =>
        HashValue(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{kind}\0{value}"));

    private static string HashValue(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
