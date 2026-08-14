using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.WorkBuddy;

public sealed class WorkBuddySourceResolver
{
    private const string UnavailableRootCode =
        "workbuddy.source_root_unavailable";
    private const string ReparseCode =
        "workbuddy.source_reparse_point";
    private const string ChangedRootCode =
        "workbuddy.source_root_changed";

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public ValueTask<SourceProbeResult> ProbeAsync(
        string workBuddyHome,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workBuddyHome);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedHome = WorkBuddySourceIdentity.NormalizePath(
            workBuddyHome);
        string instanceId = WorkBuddySourceIdentity.InstanceId(normalizedHome);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            "workbuddy",
            SourceKind.Jsonl,
            "WorkBuddy (Windows)",
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
            return ValueTask.FromResult(new SourceProbeResult([instance], [], []));
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

            var pending = new Stack<string>();
            pending.Push(projectsRoot);
            while (pending.TryPop(out string? current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(File.GetAttributes(current)))
                {
                    diagnostics.Add(ReparseDiagnostic());
                    break;
                }

                foreach (string path in Directory.EnumerateFileSystemEntries(
                             current,
                             "*",
                             EnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(path);
                    if (IsReparsePoint(attributes))
                    {
                        diagnostics.Add(ReparseDiagnostic());
                        break;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(path);
                        continue;
                    }

                    if (!string.Equals(
                            Path.GetExtension(path),
                            ".jsonl",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string normalizedPath = WorkBuddySourceIdentity.NormalizePath(
                        path);
                    entities.Add(new SourceEntityDescriptor(
                        instanceId,
                        WorkBuddySourceIdentity.EntityId(normalizedPath),
                        normalizedPath));
                }

                if (diagnostics.Count > 0)
                {
                    break;
                }
            }

            FileAttributes attributesAfter = File.GetAttributes(projectsRoot);
            if (diagnostics.Count == 0 &&
                (attributesAfter != rootAttributes ||
                 IsReparsePoint(attributesAfter)))
            {
                diagnostics.Add(new CollectorDiagnostic(
                    ChangedRootCode,
                    "The WorkBuddy projects directory changed while it was inspected."));
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

    private static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException;

    private static CollectorDiagnostic UnavailableRootDiagnostic() => new(
        UnavailableRootCode,
        "The WorkBuddy projects directory could not be inspected.");

    private static CollectorDiagnostic ReparseDiagnostic() => new(
        ReparseCode,
        "A WorkBuddy source path is a reparse point and was skipped.");
}
