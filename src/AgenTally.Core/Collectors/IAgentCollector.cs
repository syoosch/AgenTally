namespace AgenTally.Core.Collectors;

public interface IAgentCollector
{
    string AgentId { get; }

    ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken);

    IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken);
}
