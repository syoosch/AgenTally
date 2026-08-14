using System.Runtime.CompilerServices;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;

namespace AgenTally.Core.Collectors.ClaudeCode;

public sealed class ClaudeCodeDesktopCollector : IIncrementalFileCollector
{
    private readonly ClaudeCodeCollector _inner;

    public ClaudeCodeDesktopCollector(
        string sourceRoot,
        TimeProvider? timeProvider = null,
        ClaudeCodeDesktopSourceResolver? resolver = null)
    {
        string normalizedRoot = ClaudeCodeSourceIdentity.NormalizePath(sourceRoot);
        _inner = new ClaudeCodeCollector(
            normalizedRoot,
            normalizedRoot,
            ClaudeCodeDesktopSourceIdentity.InstanceId(normalizedRoot),
            timeProvider,
            resolver ?? new ClaudeCodeDesktopSourceResolver(),
            parser: new ClaudeCodeTranscriptParser(
                ClaudeCodeTranscriptParserOptions.DesktopLocalAgent));
    }

    public string AgentId => _inner.AgentId;

    public string ParserVersion => _inner.ParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.PartiallyCompatible;

    public string MaintenanceCompatibilityCode =>
        "desktop_prompt_attribution_unavailable";

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
