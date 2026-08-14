using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.GeminiCli;

internal sealed class GeminiCliSourceResolver
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
        string normalizedRoot = GeminiCliSourceIdentity.NormalizePath(root);
        string instanceId = GeminiCliSourceIdentity.InstanceId(normalizedRoot);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            "gemini-cli",
            SourceKind.Mixed,
            "Gemini CLI (Windows)",
            normalizedRoot);
        string tempRoot = Path.Combine(normalizedRoot, "tmp");
        if (!Directory.Exists(tempRoot))
        {
            return ValueTask.FromResult(new SourceProbeResult([instance], [], []));
        }

        try
        {
            var pending = new Stack<string>();
            pending.Push(tempRoot);
            var entities = new List<SourceEntityDescriptor>();
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
                        continue;
                    }

                    if (!GeminiCliSourceIdentity.IsSupportedFile(path))
                    {
                        continue;
                    }

                    string normalizedPath = GeminiCliSourceIdentity.NormalizePath(path);
                    entities.Add(new SourceEntityDescriptor(
                        instanceId,
                        GeminiCliSourceIdentity.EntityId(normalizedPath),
                        normalizedPath));
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
                    "gemini-cli.source_unavailable",
                    "The Gemini CLI transcript directory could not be inspected safely.")]));
        }
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static SourceProbeResult Reparse(SourceInstanceDescriptor instance) => new(
        [instance],
        [],
        [new CollectorDiagnostic(
            "gemini-cli.source_reparse_point",
            "A Gemini CLI source path is a reparse point and was skipped.")]);
}
