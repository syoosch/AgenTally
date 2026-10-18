using System.Security.Cryptography;
using System.Text;
using AgenTally.Core.Collectors.Codex;

namespace AgenTally.Core.Collectors.DeepSeekHarness;

public static class DeepSeekHarnessSourceIdentity
{
    public const string AgentId = "deepseek-harness";

    public static string NormalizePath(string path) => CodexSourceIdentity.NormalizePath(path);

    public static string InstanceId(string home) =>
        $"deepseek-harness:windows:{Hash("home", NormalizePath(home).ToUpperInvariant())[..16]}";

    // Generations are replacements of one session, never independent usage sources.
    public static string EntityId(string file) =>
        $"deepseek-harness:session:{Hash("directory", NormalizePath(Path.GetDirectoryName(file)!).ToUpperInvariant())[..24]}";

    public static string Hash(string kind, string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + value)))
            .ToLowerInvariant();

    internal static string EncodeSegment(string value)
    {
        if (value is "." or "..") return string.Concat(value.Select(_ => "~002E"));
        var encoded = new StringBuilder();
        foreach (char character in value)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                or '.' or '_' or '-') encoded.Append(character);
            else encoded.Append('~').Append(((int)character).ToString("X4"));
        }
        return encoded.ToString();
    }

    internal static bool TryReadGeneration(string name, out int version)
    {
        version = -1;
        string plain = name.EndsWith(".zstd", StringComparison.Ordinal)
            ? name[..^5] : name;
        if (plain == "session.jsonl") { version = 0; return true; }
        const string prefix = "session.v";
        const string suffix = ".jsonl";
        if (!plain.StartsWith(prefix, StringComparison.Ordinal) ||
            !plain.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string number = plain[prefix.Length..^suffix.Length];
        if (number.Length is not (> 0 and <= 16) || number[0] == '0' ||
            !number.All(char.IsAsciiDigit) || !long.TryParse(number, out long parsed) ||
            parsed > 9007199254740991L) return false;
        version = (int)Math.Min(parsed, int.MaxValue);
        return true;
    }

    internal static void AssertSafePath(string home, string source)
    {
        string normalized = NormalizePath(source);
        string root = Path.Combine(NormalizePath(home), "sessions");
        string relative = Path.GetRelativePath(root, normalized);
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || parts.Length != 3 ||
            parts.Any(part => part is ".." or "." or "") ||
            !TryReadGeneration(parts[^1], out _))
            throw new InvalidDataException("The Harness source path has an unsupported shape.");
        string current = NormalizePath(home);
        foreach (string part in new[] { "sessions" }.Concat(parts))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Harness source path is a reparse point.");
            current = Path.Combine(current, part);
        }
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Harness source path is a reparse point.");
    }
}
