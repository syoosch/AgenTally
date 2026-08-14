using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.ClaudeCode;

public sealed class ClaudeCodeDesktopSourceResolver : IClaudeCodeSourceResolver
{
    private const string UnavailableRootCode =
        "claude_code.desktop_source_root_unavailable";
    private const string UnsafeTreeCode =
        "claude_code.desktop_source_tree_unsafe";

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public ValueTask<SourceProbeResult> ProbeAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedRoot = ClaudeCodeSourceIdentity.NormalizePath(sourceRoot);

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(normalizedRoot);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ValueTask.FromResult(new SourceProbeResult([], [], []));
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return ValueTask.FromResult(Failure(UnavailableRootCode));
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return ValueTask.FromResult(Failure(UnsafeTreeCode));
        }

        var entities = new List<SourceEntityDescriptor>();
        try
        {
            string instanceId = ClaudeCodeDesktopSourceIdentity.InstanceId(
                normalizedRoot);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(normalizedRoot);
            while (pendingDirectories.TryPop(out string? currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string path in Directory.EnumerateFileSystemEntries(
                             currentDirectory,
                             "*",
                             EnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return ValueTask.FromResult(Failure(UnsafeTreeCode));
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(path);
                    }
                    else if (IsLocalAgentTranscript(path))
                    {
                        string fullPath = ClaudeCodeSourceIdentity.NormalizePath(path);
                        entities.Add(new SourceEntityDescriptor(
                            instanceId,
                            ClaudeCodeSourceIdentity.EntityId(fullPath),
                            fullPath));
                    }
                }
            }

            if (File.GetAttributes(normalizedRoot) != rootAttributes)
            {
                return ValueTask.FromResult(Failure(UnsafeTreeCode));
            }

            if (entities.Count == 0)
            {
                return ValueTask.FromResult(new SourceProbeResult([], [], []));
            }

            entities.Sort(static (left, right) =>
            {
                int comparison = StringComparer.OrdinalIgnoreCase.Compare(
                    left.SourcePath,
                    right.SourcePath);
                return comparison != 0
                    ? comparison
                    : StringComparer.Ordinal.Compare(
                        left.SourcePath,
                        right.SourcePath);
            });
            var instance = new SourceInstanceDescriptor(
                instanceId,
                "claude-code",
                SourceKind.Jsonl,
                "Claude Code Desktop Code (Windows)",
                normalizedRoot);
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                entities,
                []));
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return ValueTask.FromResult(Failure(UnavailableRootCode));
        }
    }

    private static bool IsLocalAgentTranscript(string path)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("session_", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(name["session_".Length..], out _))
        {
            return false;
        }

        DirectoryInfo? projectKey = Directory.GetParent(path);
        DirectoryInfo? projects = projectKey?.Parent;
        DirectoryInfo? claude = projects?.Parent;
        return projectKey is not null &&
            !string.IsNullOrWhiteSpace(projectKey.Name) &&
            string.Equals(projects?.Name, "projects", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(claude?.Name, ".claude", StringComparison.OrdinalIgnoreCase);
    }

    private static SourceProbeResult Failure(string code) => new(
        [],
        [],
        [new CollectorDiagnostic(
            code,
            "The Claude Desktop Code local-Agent source could not be safely inspected.")]);

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException;
}
