using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.ClaudeCode;

public interface IClaudeCodeSourceResolver
{
    ValueTask<SourceProbeResult> ProbeAsync(
        string sourceRoot,
        CancellationToken cancellationToken);
}

public sealed class ClaudeCodeSourceResolver : IClaudeCodeSourceResolver
{
    private const string UnavailableRootCode =
        "claude_code.source_root_unavailable";
    private const string UnavailableRootMessage =
        "A known Claude Code source directory could not be inspected.";
    private const string ReparseRootCode =
        "claude_code.source_root_reparse_point";
    private const string ReparseRootMessage =
        "A known Claude Code source directory is a reparse point and was skipped.";
    private const string ReparseDescendantCode =
        "claude_code.source_descendant_reparse_point";
    private const string ReparseDescendantMessage =
        "A known Claude Code source directory contains a reparse point and was not fully inspected.";
    private const string ChangedRootCode =
        "claude_code.source_root_changed";
    private const string ChangedRootMessage =
        "A known Claude Code source directory changed while it was being inspected.";

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public ValueTask<SourceProbeResult> ProbeAsync(
        string claudeHome,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claudeHome);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedHome = ClaudeCodeSourceIdentity.NormalizePath(claudeHome);
        string instanceId = ClaudeCodeSourceIdentity.InstanceId(normalizedHome);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            "claude-code",
            SourceKind.Jsonl,
            "Claude Code (Windows)",
            normalizedHome);
        var entities = new List<SourceEntityDescriptor>();
        var diagnostics = new List<CollectorDiagnostic>();
        string projectsRoot = Path.Combine(normalizedHome, "projects");

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(projectsRoot);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                []));
        }
        catch (Exception exception) when (IsExpectedRootFailure(exception))
        {
            diagnostics.Add(new CollectorDiagnostic(
                UnavailableRootCode,
                UnavailableRootMessage));
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                diagnostics));
        }

        try
        {
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(new CollectorDiagnostic(
                    ReparseRootCode,
                    ReparseRootMessage));
                return ValueTask.FromResult(new SourceProbeResult(
                    [instance],
                    [],
                    diagnostics));
            }

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(projectsRoot);
            while (pendingDirectories.TryPop(out string? currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes currentAttributes = File.GetAttributes(currentDirectory);
                if ((currentAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(new CollectorDiagnostic(
                        ReparseDescendantCode,
                        ReparseDescendantMessage));
                    break;
                }

                foreach (string path in Directory.EnumerateFileSystemEntries(
                             currentDirectory,
                             "*",
                             EnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(new CollectorDiagnostic(
                            ReparseDescendantCode,
                            ReparseDescendantMessage));
                        break;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(path);
                        continue;
                    }

                    if (!IsVerifiedTranscriptPath(projectsRoot, path))
                    {
                        continue;
                    }

                    string fullPath = ClaudeCodeSourceIdentity.NormalizePath(path);
                    entities.Add(new SourceEntityDescriptor(
                        instanceId,
                        ClaudeCodeSourceIdentity.EntityId(fullPath),
                        fullPath));
                }

                if (diagnostics.Count > 0)
                {
                    break;
                }
            }

            FileAttributes attributesAfter = File.GetAttributes(projectsRoot);
            if (diagnostics.Count == 0 &&
                (attributesAfter != rootAttributes ||
                 (attributesAfter & FileAttributes.ReparsePoint) != 0))
            {
                diagnostics.Add(new CollectorDiagnostic(
                    ChangedRootCode,
                    ChangedRootMessage));
            }
        }
        catch (Exception exception) when (IsExpectedRootFailure(exception))
        {
            diagnostics.Add(new CollectorDiagnostic(
                UnavailableRootCode,
                UnavailableRootMessage));
        }

        if (diagnostics.Count > 0)
        {
            entities.Clear();
        }
        else
        {
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
        }

        return ValueTask.FromResult(new SourceProbeResult(
            [instance],
            entities,
            diagnostics));
    }

    private static bool IsExpectedRootFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException;

    private static bool IsVerifiedTranscriptPath(
        string projectsRoot,
        string path)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = Path.GetRelativePath(projectsRoot, path);
        string[] parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            return !string.IsNullOrWhiteSpace(parts[0]) &&
                IsSessionTranscriptName(parts[1]);
        }

        return parts.Length == 4 &&
            !string.IsNullOrWhiteSpace(parts[0]) &&
            IsSessionIdentity(parts[1]) &&
            string.Equals(
                parts[2],
                "subagents",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(
                Path.GetFileNameWithoutExtension(parts[3]));
    }

    private static bool IsSessionTranscriptName(string fileName) =>
        string.Equals(
            Path.GetExtension(fileName),
            ".jsonl",
            StringComparison.OrdinalIgnoreCase) &&
        IsSessionIdentity(Path.GetFileNameWithoutExtension(fileName));

    private static bool IsSessionIdentity(string value) =>
        value is { Length: > 0 and <= 1024 } &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);
}
