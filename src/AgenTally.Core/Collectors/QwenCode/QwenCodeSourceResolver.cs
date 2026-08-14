using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.QwenCode;

public sealed class QwenCodeSourceResolver
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public ValueTask<SourceProbeResult> ProbeAsync(
        string qwenHome,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qwenHome);
        cancellationToken.ThrowIfCancellationRequested();
        string home = QwenCodeSourceIdentity.NormalizePath(qwenHome);
        string instanceId = QwenCodeSourceIdentity.InstanceId(home);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            "qwen-code",
            SourceKind.Jsonl,
            "Qwen Code CLI (Windows)",
            home);
        string projects = Path.Combine(home, "projects");
        if (!Directory.Exists(projects))
        {
            return ValueTask.FromResult(new SourceProbeResult([instance], [], []));
        }

        try
        {
            if (IsReparse(home) || IsReparse(projects))
            {
                return ValueTask.FromResult(Reparse(instance));
            }

            var entities = new List<SourceEntityDescriptor>();
            foreach (string project in Directory.EnumerateDirectories(projects, "*", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string chats = Path.Combine(project, "chats");
                if (IsReparse(project))
                {
                    return ValueTask.FromResult(Reparse(instance));
                }
                if (!Directory.Exists(chats))
                {
                    continue;
                }
                if (IsReparse(chats))
                {
                    return ValueTask.FromResult(Reparse(instance));
                }

                foreach (string file in Directory.EnumerateFiles(chats, "*.jsonl", Options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparse(file))
                    {
                        return ValueTask.FromResult(Reparse(instance));
                    }
                    string path = QwenCodeSourceIdentity.NormalizePath(file);
                    entities.Add(new SourceEntityDescriptor(
                        instanceId,
                        QwenCodeSourceIdentity.EntityId(path),
                        path));
                }
            }

            return ValueTask.FromResult(new SourceProbeResult([instance], entities, []));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                [new CollectorDiagnostic(
                    "qwen-code.source_unavailable",
                    "The Qwen Code project chats could not be inspected safely.")]));
        }
    }

    private static SourceProbeResult Reparse(SourceInstanceDescriptor instance) => new(
        [instance],
        [],
        [new CollectorDiagnostic(
            "qwen-code.source_reparse_point",
            "A Qwen Code source path is a reparse point and was skipped.")]);

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or PathTooLongException;
}
