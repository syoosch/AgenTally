using System.Security;
using AgenTally.Domain.Sources;

namespace AgenTally.Core.Collectors.DeepSeekHarness;

public sealed class DeepSeekHarnessSourceResolver
{
    private static readonly EnumerationOptions Options = new()
    { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };

    public ValueTask<SourceProbeResult> ProbeAsync(string home, CancellationToken cancellationToken)
    {
        home = DeepSeekHarnessSourceIdentity.NormalizePath(home);
        var instance = new SourceInstanceDescriptor(DeepSeekHarnessSourceIdentity.InstanceId(home),
            DeepSeekHarnessSourceIdentity.AgentId, SourceKind.Jsonl, "DeepSeek Harness (Windows)", home);
        var entities = new List<SourceEntityDescriptor>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sessions = Path.Combine(home, "sessions");
            if (Directory.Exists(home)) AssertDirectory(home);
            if (!Directory.Exists(sessions)) return ValueTask.FromResult(new SourceProbeResult([instance], [], []));
            AssertDirectory(sessions);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string project in Directory.EnumerateDirectories(sessions, "*", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssertDirectory(project);
                foreach (string session in Directory.EnumerateDirectories(project, "*", Options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AssertDirectory(session);
                    string? path = SelectArtifact(session);
                    if (path is null) continue;
                    if (!ids.Add(Path.GetFileName(session)))
                        throw new InvalidDataException("Harness session directory identities conflict.");
                    DeepSeekHarnessSourceIdentity.AssertSafePath(home, path);
                    entities.Add(new(instance.SourceInstanceId, DeepSeekHarnessSourceIdentity.EntityId(path), path));
                }
            }
            return ValueTask.FromResult(new SourceProbeResult([instance], entities, []));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            SecurityException or ArgumentException or NotSupportedException)
        {
            // Fail the whole probe: maintenance must not commit a partial source inventory.
            return ValueTask.FromResult(new SourceProbeResult([instance], [],
                [new("deepseek-harness.source_unavailable", "Harness session sources could not be inspected safely.")]));
        }
    }

    internal static string? SelectArtifact(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", Options)
            .Select(path => (Path: path, Valid: DeepSeekHarnessSourceIdentity.TryReadGeneration(
                Path.GetFileName(path), out int version), Version: version))
            .Where(entry => entry.Valid).ToArray();
        if (files.Length == 0) return null;
        int highest = files.Max(entry => entry.Version);
        var selected = files.Where(entry => entry.Version == highest).ToArray();
        if (selected.Length != 1) throw new InvalidDataException("Harness generation encodings conflict.");
        return DeepSeekHarnessSourceIdentity.NormalizePath(selected[0].Path);
    }

    private static void AssertDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Harness source directory is a reparse point.");
    }
}
