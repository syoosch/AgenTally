using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.KimiCode;

public sealed class KimiCodeSourceResolver
{
    private const string UnavailableRootCode =
        "kimi_code.source_root_unavailable";
    private const string ReparseCode =
        "kimi_code.source_reparse_point";
    private const string ChangedRootCode =
        "kimi_code.source_root_changed";

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    private readonly KimiCodeSourceLayout _layout;

    public KimiCodeSourceResolver()
        : this(KimiCodeSourceLayout.Cli)
    {
    }

    internal KimiCodeSourceResolver(KimiCodeSourceLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public ValueTask<SourceProbeResult> ProbeAsync(
        string kimiHome,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kimiHome);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedHome = KimiCodeSourceIdentity.NormalizePath(kimiHome);
        string instanceId = _layout.InstanceId(normalizedHome);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            _layout.AgentId,
            SourceKind.Jsonl,
            _layout.DisplayName,
            normalizedHome);
        var entities = new List<SourceEntityDescriptor>();
        var diagnostics = new List<CollectorDiagnostic>();
        string sessionsRoot = Path.Combine(normalizedHome, "sessions");

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(sessionsRoot);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ValueTask.FromResult(new SourceProbeResult(
                _layout.KeepInstanceWhenMissing ? [instance] : [],
                [],
                []));
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            diagnostics.Add(UnavailableRootDiagnostic());
            return ValueTask.FromResult(new SourceProbeResult(
                [instance],
                [],
                diagnostics));
        }

        try
        {
            if (IsReparsePoint(rootAttributes))
            {
                diagnostics.Add(ReparseDiagnostic());
                return ValueTask.FromResult(new SourceProbeResult(
                    [instance],
                    [],
                    diagnostics));
            }

            foreach (string workDirectory in Directory.EnumerateDirectories(
                         sessionsRoot,
                         "*",
                         EnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ValidateDirectory(workDirectory, diagnostics))
                {
                    break;
                }

                foreach (string sessionDirectory in Directory.EnumerateDirectories(
                             workDirectory,
                             "*",
                             EnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_layout.TryGetRootSessionId(
                            Path.GetFileName(sessionDirectory),
                            out _))
                    {
                        continue;
                    }

                    if (!ValidateDirectory(sessionDirectory, diagnostics))
                    {
                        break;
                    }

                    string agentsDirectory = Path.Combine(
                        sessionDirectory,
                        "agents");
                    if (!Directory.Exists(agentsDirectory))
                    {
                        continue;
                    }

                    if (!ValidateDirectory(agentsDirectory, diagnostics))
                    {
                        break;
                    }

                    foreach (string agentDirectory in Directory.EnumerateDirectories(
                                 agentsDirectory,
                                 "*",
                                 EnumerationOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!ValidateDirectory(agentDirectory, diagnostics))
                        {
                            break;
                        }

                        string wirePath = Path.Combine(agentDirectory, "wire.jsonl");
                        if (!File.Exists(wirePath))
                        {
                            continue;
                        }

                        FileAttributes wireAttributes = File.GetAttributes(wirePath);
                        if (IsReparsePoint(wireAttributes))
                        {
                            diagnostics.Add(ReparseDiagnostic());
                            break;
                        }

                        string normalizedPath =
                            KimiCodeSourceIdentity.NormalizePath(wirePath);
                        entities.Add(new SourceEntityDescriptor(
                            instanceId,
                            KimiCodeSourceIdentity.EntityId(normalizedPath),
                            normalizedPath));
                    }

                    if (diagnostics.Count > 0)
                    {
                        break;
                    }
                }

                if (diagnostics.Count > 0)
                {
                    break;
                }
            }

            FileAttributes attributesAfter = File.GetAttributes(sessionsRoot);
            if (diagnostics.Count == 0 &&
                (attributesAfter != rootAttributes ||
                 IsReparsePoint(attributesAfter)))
            {
                diagnostics.Add(new CollectorDiagnostic(
                    ChangedRootCode,
                    "The Kimi Code sessions directory changed while it was inspected."));
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            diagnostics.Add(UnavailableRootDiagnostic());
        }

        if (diagnostics.Count > 0)
        {
            entities.Clear();
        }
        else
        {
            entities.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(
                    left.SourcePath,
                    right.SourcePath));
        }

        return ValueTask.FromResult(new SourceProbeResult(
            [instance],
            entities,
            diagnostics));
    }

    private static bool ValidateDirectory(
        string path,
        ICollection<CollectorDiagnostic> diagnostics)
    {
        if (!IsReparsePoint(File.GetAttributes(path)))
        {
            return true;
        }

        diagnostics.Add(ReparseDiagnostic());
        return false;
    }

    private static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException;

    private static CollectorDiagnostic UnavailableRootDiagnostic() => new(
        UnavailableRootCode,
        "The Kimi Code sessions directory could not be inspected.");

    private static CollectorDiagnostic ReparseDiagnostic() => new(
        ReparseCode,
        "A Kimi Code source path is a reparse point and was skipped.");
}
