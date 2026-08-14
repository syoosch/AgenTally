namespace AgenTally.Domain.Usage;

public sealed record TokenUsage
{
    public TokenMetric InputReported { get; init; } = TokenMetric.Unavailable;

    public TokenMetric UncachedInput { get; init; } = TokenMetric.Unavailable;

    public TokenMetric CacheRead { get; init; } = TokenMetric.Unavailable;

    public TokenMetric CacheWrite { get; init; } = TokenMetric.Unavailable;

    public TokenMetric Output { get; init; } = TokenMetric.Unavailable;

    public TokenMetric Reasoning { get; init; } = TokenMetric.Unavailable;

    public TokenMetric Tool { get; init; } = TokenMetric.Unavailable;

    public TokenMetric ReportedTotal { get; init; } = TokenMetric.Unavailable;

    public TokenMetric NormalizedTotal { get; init; } = TokenMetric.Unavailable;

    public MetricInclusion CacheIncludedInInput { get; init; } = MetricInclusion.Unknown;

    public MetricInclusion ReasoningIncludedInOutput { get; init; } = MetricInclusion.Unknown;
}
