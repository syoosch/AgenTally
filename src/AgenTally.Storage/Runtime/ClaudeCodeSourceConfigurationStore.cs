using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Storage.Runtime;

public sealed class ClaudeCodeSourceConfigurationStore
{
    public const int MaxAdditionalRoots = 8;
    private const int MaxDocumentBytes = 64 * 1024;
    private const int ProtocolVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _path;

    public ClaudeCodeSourceConfigurationStore(AgenTallyRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _path = Path.GetFullPath(profile.ClaudeCodeSourceConfigurationPath);
        if (profile.Channel == AgenTallyChannel.Development &&
            !profile.IsDevelopmentOwnedPath(_path))
        {
            throw new InvalidOperationException(
                "Development Claude Code source settings must stay inside artifacts/development.");
        }
    }

    public IReadOnlyList<string> ReadAdditionalRoots()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidDocument();
            }

            byte[] payload = BoundedFileReader.ReadAllBytes(
                _path,
                MaxDocumentBytes);
            ClaudeCodeSourceDocument? document =
                JsonSerializer.Deserialize<ClaudeCodeSourceDocument>(
                    payload,
                    SerializerOptions);
            if (document is null ||
                document.ProtocolVersion != ProtocolVersion ||
                document.AdditionalRoots is null)
            {
                throw InvalidDocument();
            }

            return NormalizeRoots(document.AdditionalRoots);
        }
        catch (Exception exception)
            when (exception is JsonException
                or NotSupportedException
                or ArgumentException
                or PathTooLongException)
        {
            throw InvalidDocument(exception);
        }
    }

    public void WriteAdditionalRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        IReadOnlyList<string> normalized = NormalizeRoots(roots);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new ClaudeCodeSourceDocument(
                ProtocolVersion,
                normalized.ToArray()),
            SerializerOptions);
        if (payload.Length > MaxDocumentBytes)
        {
            throw InvalidDocument();
        }

        string directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException(
                "The Claude Code source configuration has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_path) &&
            (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidDocument();
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static IReadOnlyList<string> NormalizeRoots(
        IEnumerable<string> roots)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw InvalidDocument();
            }

            string candidate = root.Trim();
            if (!Path.IsPathFullyQualified(candidate))
            {
                throw InvalidDocument();
            }

            string fullPath = Path.GetFullPath(candidate);
            string? pathRoot = Path.GetPathRoot(fullPath);
            if (!string.Equals(
                    fullPath,
                    pathRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                fullPath = fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }

            if (!seen.Add(fullPath))
            {
                continue;
            }

            normalized.Add(fullPath);
            if (normalized.Count > MaxAdditionalRoots)
            {
                throw InvalidDocument();
            }
        }

        normalized.Sort(StringComparer.OrdinalIgnoreCase);
        return normalized;
    }

    private static InvalidDataException InvalidDocument(
        Exception? innerException = null) => new(
        "The Claude Code source configuration is invalid.",
        innerException);

    private sealed record ClaudeCodeSourceDocument(
        int ProtocolVersion,
        string[] AdditionalRoots);
}
