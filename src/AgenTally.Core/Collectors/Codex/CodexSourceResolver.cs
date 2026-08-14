using AgenTally.Domain.Sources;
using System.Security;

namespace AgenTally.Core.Collectors.Codex;

public sealed class CodexSourceResolver
{
    private const string UnavailableRootCode = "codex.source_root_unavailable";
    private const string UnavailableRootMessage =
        "A known Codex source directory could not be inspected.";
    private const string ReparseRootCode = "codex.source_root_reparse_point";
    private const string ReparseRootMessage =
        "A known Codex source directory is a reparse point and was skipped.";
    private const string ReparseDescendantCode =
        "codex.source_descendant_reparse_point";
    private const string ReparseDescendantMessage =
        "A known Codex source directory contains a reparse point and was not fully inspected.";
    private const string ChangedRootCode = "codex.source_root_changed";
    private const string ChangedRootMessage =
        "A known Codex source directory changed while it was being inspected.";

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public ValueTask<SourceProbeResult> ProbeAsync(
        string codexHome,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedHome = CodexSourceIdentity.NormalizePath(codexHome);
        string instanceId = CodexSourceIdentity.InstanceId(normalizedHome);
        var instance = new SourceInstanceDescriptor(
            instanceId,
            "codex",
            SourceKind.Jsonl,
            "Codex (Windows)",
            normalizedHome);
        var entitiesById = new Dictionary<string, SourceEntityDescriptor>(
            StringComparer.Ordinal);
        var diagnostics = new List<CollectorDiagnostic>();

        // Active wins when the same rollout is briefly visible in both trees.
        AddEntities(Path.Combine(normalizedHome, "sessions"));
        AddEntities(Path.Combine(normalizedHome, "archived_sessions"));

        var entities = entitiesById.Values.ToList();
        entities.Sort(static (left, right) =>
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(
                left.SourcePath,
                right.SourcePath);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.SourcePath, right.SourcePath);
        });

        return ValueTask.FromResult(new SourceProbeResult(
            [instance],
            entities,
            diagnostics));

        void AddEntities(string rootPath)
        {
            FileAttributes attributesBefore;
            try
            {
                attributesBefore = File.GetAttributes(rootPath);
            }
            catch (Exception exception)
                when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (IsExpectedRootFailure(exception))
            {
                diagnostics.Add(new CollectorDiagnostic(
                    UnavailableRootCode,
                    UnavailableRootMessage));
                return;
            }

            var rootEntities = new List<SourceEntityDescriptor>();

            try
            {
                if ((attributesBefore & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(new CollectorDiagnostic(
                        ReparseRootCode,
                        ReparseRootMessage));
                    return;
                }

                var pendingDirectories = new Stack<string>();
                pendingDirectories.Push(rootPath);
                while (pendingDirectories.TryPop(out string? currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes currentAttributes = File.GetAttributes(currentDirectory);
                    if ((currentAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(new CollectorDiagnostic(
                            ReparseDescendantCode,
                            ReparseDescendantMessage));
                        return;
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
                            return;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pendingDirectories.Push(path);
                            continue;
                        }

                        if (!string.Equals(
                                Path.GetExtension(path),
                                ".jsonl",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string fullPath = CodexSourceIdentity.NormalizePath(path);
                        rootEntities.Add(new SourceEntityDescriptor(
                            instanceId,
                            CodexSourceIdentity.EntityId(fullPath),
                            fullPath));
                    }
                }

                FileAttributes attributesAfter = File.GetAttributes(rootPath);
                if (attributesAfter != attributesBefore
                    || (attributesAfter & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(new CollectorDiagnostic(
                        ChangedRootCode,
                        ChangedRootMessage));
                    return;
                }
            }
            catch (Exception exception) when (IsExpectedRootFailure(exception))
            {
                diagnostics.Add(new CollectorDiagnostic(
                    UnavailableRootCode,
                    UnavailableRootMessage));
                return;
            }

            foreach (SourceEntityDescriptor entity in rootEntities)
            {
                entitiesById.TryAdd(entity.SourceEntityId, entity);
            }
        }
    }

    private static bool IsExpectedRootFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException;
}
