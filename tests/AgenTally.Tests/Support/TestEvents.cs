using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;

namespace AgenTally.Tests.Support;

public static class TestEvents
{
    public static UsageEvent Create(
        string eventId = "event-1",
        string dedupKey = "codex:thread-1:1",
        long sourceRevision = 1,
        CompletionState completionState = CompletionState.Completed,
        long normalizedTotal = 100,
        long reportedTotal = 120,
        ModelIdentity? model = null)
    {
        DateTimeOffset occurredAtUtc = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

        return new UsageEvent(
            "codex",
            "codex:windows:test",
            "rollout:test",
            eventId,
            dedupKey,
            SourceKind.Jsonl,
            occurredAtUtc,
            occurredAtUtc,
            model ?? new ModelIdentity
            {
                RawModel = "gpt-test",
                NormalizedModel = "gpt-test",
                ProviderId = "openai",
                ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
            },
            new TokenUsage
            {
                InputReported = TokenMetric.Exact(60),
                UncachedInput = TokenMetric.Exact(40),
                CacheRead = TokenMetric.Exact(20),
                CacheWrite = TokenMetric.Exact(0),
                Output = TokenMetric.Exact(30),
                Reasoning = TokenMetric.Exact(10),
                Tool = TokenMetric.Exact(0),
                ReportedTotal = TokenMetric.Exact(reportedTotal),
                NormalizedTotal = new TokenMetric(normalizedTotal, MetricOrigin.Derived),
                CacheIncludedInInput = MetricInclusion.Included,
                ReasoningIncludedInOutput = MetricInclusion.Included
            },
            completionState,
            DataQuality.Exact,
            "codex-v1",
            "fixture-1",
            sourceRevision);
    }
}
