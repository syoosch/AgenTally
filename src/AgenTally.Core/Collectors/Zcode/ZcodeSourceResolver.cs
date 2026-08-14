using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.Zcode;

public sealed class ZcodeSourceResolver
{
    private const string UnavailableCode = "zcode.source_unavailable";
    private const string ReparseCode = "zcode.source_reparse_point";

    public ValueTask<SourceProbeResult> ProbeAsync(
        string zcodeHome,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zcodeHome);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedHome = ZcodeSourceIdentity.NormalizePath(zcodeHome);
        string instanceId = ZcodeSourceIdentity.InstanceId(normalizedHome);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            "zcode",
            SourceKind.Sqlite,
            "ZCode (Windows)",
            normalizedHome);
        string databaseDirectory = Path.Combine(normalizedHome, "cli", "db");
        string databasePath = Path.Combine(
            databaseDirectory,
            ZcodeSourceIdentity.DatabaseFileName);

        try
        {
            if (!File.Exists(databasePath))
            {
                return ValueTask.FromResult(new SourceProbeResult(
                    [instance],
                    [],
                    []));
            }

            foreach (string path in new[]
                     {
                         normalizedHome,
                         Path.Combine(normalizedHome, "cli"),
                         databaseDirectory,
                         databasePath
                     })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    return ValueTask.FromResult(new SourceProbeResult(
                        [instance],
                        [],
                        [new CollectorDiagnostic(
                            ReparseCode,
                            "A ZCode source path is a reparse point and was skipped.")]));
                }
            }

            string normalizedDatabase = ZcodeSourceIdentity.NormalizePath(databasePath);
            var entity = new SourceEntityDescriptor(
                instanceId,
                ZcodeSourceIdentity.EntityId(normalizedDatabase),
                normalizedDatabase);
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [entity],
                []));
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                [new CollectorDiagnostic(
                    UnavailableCode,
                    "The ZCode usage database could not be inspected safely.")]));
        }
    }

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;
}
