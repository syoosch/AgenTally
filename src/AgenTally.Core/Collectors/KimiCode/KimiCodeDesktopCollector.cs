using System.Runtime.CompilerServices;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.KimiCode;

public sealed class KimiCodeDesktopCollector :
    IIncrementalFileCollector,
    IUsageSessionNameSource
{
    private readonly KimiCodeCollector _inner;

    public KimiCodeDesktopCollector(
        string kimiHome,
        TimeProvider? timeProvider = null)
    {
        _inner = new KimiCodeCollector(
            kimiHome,
            KimiCodeSourceLayout.DesktopWork,
            timeProvider);
    }

    public string AgentId => _inner.AgentId;

    public string ParserVersion => _inner.ParserVersion;

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken) =>
        _inner.ProbeAsync(context, cancellationToken);

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (CollectedBatch batch in _inner.CollectAsync(
                           request,
                           cancellationToken))
        {
            yield return batch;
        }
    }

    public Task<IReadOnlyList<UsageSessionNameMetadata>> ReadSessionNamesAsync(
        CancellationToken cancellationToken) =>
        _inner.ReadSessionNamesAsync(cancellationToken);

    public IReadOnlyList<string> GetWatchRoots(
        SourceInstanceDescriptor instance) =>
        ((IIncrementalFileCollector)_inner).GetWatchRoots(instance);

    public string NormalizeSourcePath(string path) =>
        ((IIncrementalFileCollector)_inner).NormalizeSourcePath(path);

    public string GetSourceEntityId(string normalizedPath) =>
        ((IIncrementalFileCollector)_inner).GetSourceEntityId(normalizedPath);

    public bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedPath) =>
        ((IIncrementalFileCollector)_inner).IsWithinMonitoredRoots(
            instance,
            normalizedPath);

    public bool TryGetCursorByteOffset(StoredCursor cursor, out long byteOffset) =>
        ((IIncrementalFileCollector)_inner).TryGetCursorByteOffset(
            cursor,
            out byteOffset);
}
