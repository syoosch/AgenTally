using AgenTally.Domain.Sources;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors;

public interface ISourceFileChangeCollector : IParserVersionedCollector
{
    IReadOnlyList<string> GetWatchRoots(SourceInstanceDescriptor instance);

    string WatchFilter => "*.jsonl";

    string NormalizeSourcePath(string path);

    string GetSourceEntityId(string normalizedChangePath);

    bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedChangePath);

    bool IsRelevantChangePath(string normalizedChangePath) =>
        string.Equals(
            Path.GetExtension(normalizedChangePath),
            ".jsonl",
            StringComparison.OrdinalIgnoreCase);

    bool HasSourceChanged(
        SourceEntityDescriptor entity,
        StoredCursor cursor) => false;
}
