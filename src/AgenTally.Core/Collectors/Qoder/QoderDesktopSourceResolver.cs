using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.Qoder;

public sealed class QoderDesktopSourceResolver
{
    public ValueTask<SourceProbeResult> ProbeAsync(
        string root,
        QoderEdition edition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedRoot = QoderSourceIdentity.NormalizePath(root);
        string agentId = QoderSourceIdentity.AgentId(edition);
        string instanceId = QoderSourceIdentity.DesktopInstanceId(normalizedRoot, edition);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            agentId,
            SourceKind.Sqlite,
            edition == QoderEdition.China
                ? "Qoder CN Desktop (Windows)"
                : "Qoder Desktop (Windows)",
            normalizedRoot);
        string database = QoderSourceIdentity.DatabasePath(normalizedRoot);
        if (!File.Exists(database))
        {
            return ValueTask.FromResult(new SourceProbeResult([instance], [], []));
        }

        try
        {
            string? current = database;
            while (!string.IsNullOrWhiteSpace(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return ValueTask.FromResult(new SourceProbeResult(
                        [instance],
                        [],
                        [new CollectorDiagnostic(
                            $"{agentId}.source_reparse_point",
                            "A Qoder Desktop source path is a reparse point and was skipped.")]));
                }
                if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }

            string path = QoderSourceIdentity.NormalizePath(database);
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [new SourceEntityDescriptor(
                    instanceId,
                    QoderSourceIdentity.DesktopEntityId(path, edition),
                    path)],
                []));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                [new CollectorDiagnostic(
                    $"{agentId}.source_unavailable",
                    "The Qoder Desktop usage database could not be inspected safely.")]));
        }
    }
}
