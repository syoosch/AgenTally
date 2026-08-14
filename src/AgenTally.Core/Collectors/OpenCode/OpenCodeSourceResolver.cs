using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.OpenCode;

internal sealed class OpenCodeSourceResolver
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    internal ValueTask<SourceProbeResult> ProbeAsync(
        string root,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedRoot = OpenCodeSourceIdentity.NormalizePath(root);
        string instanceId = OpenCodeSourceIdentity.InstanceId(normalizedRoot);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            "opencode",
            SourceKind.Mixed,
            "OpenCode (Windows)",
            normalizedRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            return ValueTask.FromResult(new SourceProbeResult([instance], [], []));
        }

        try
        {
            if (IsReparse(normalizedRoot))
            {
                return ValueTask.FromResult(Reparse(instance));
            }

            var entities = new List<SourceEntityDescriptor>();
            foreach (string database in Directory.EnumerateFiles(normalizedRoot, "opencode*.db", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparse(database))
                {
                    return ValueTask.FromResult(Reparse(instance));
                }
                string path = OpenCodeSourceIdentity.NormalizePath(database);
                entities.Add(new SourceEntityDescriptor(
                    instanceId,
                    OpenCodeSourceIdentity.EntityId(path),
                    path));
            }

            string legacyRoot = Path.Combine(normalizedRoot, "storage", "message");
            if (Directory.Exists(legacyRoot))
            {
                var pending = new Stack<string>();
                pending.Push(legacyRoot);
                while (pending.TryPop(out string? current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparse(current))
                    {
                        return ValueTask.FromResult(Reparse(instance));
                    }
                    foreach (string path in Directory.EnumerateFileSystemEntries(current, "*", Options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            return ValueTask.FromResult(Reparse(instance));
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(path);
                        }
                        else if (string.Equals(
                                     Path.GetExtension(path),
                                     ".json",
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            string normalized = OpenCodeSourceIdentity.NormalizePath(path);
                            entities.Add(new SourceEntityDescriptor(
                                instanceId,
                                OpenCodeSourceIdentity.EntityId(normalized),
                                normalized));
                        }
                    }
                }
            }

            entities.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.SourcePath, right.SourcePath));
            return ValueTask.FromResult(new SourceProbeResult([instance], entities, []));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                [new CollectorDiagnostic(
                    "opencode.source_unavailable",
                    "The OpenCode data directory could not be inspected safely.")]));
        }
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static SourceProbeResult Reparse(SourceInstanceDescriptor instance) => new(
        [instance],
        [],
        [new CollectorDiagnostic(
            "opencode.source_reparse_point",
            "An OpenCode source path is a reparse point and was skipped.")]);
}
