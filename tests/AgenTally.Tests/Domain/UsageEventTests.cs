using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Domain;

[TestClass]
public sealed class UsageEventTests
{
    [TestMethod]
    public void Constructor_RejectsBlankDedupKey()
    {
        Assert.ThrowsExactly<ArgumentException>(() => TestEvents.Create(dedupKey: " "));
    }

    [TestMethod]
    public void Constructor_RejectsNegativeSourceRevision()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TestEvents.Create(sourceRevision: -1));
    }

    [TestMethod]
    public void Event_PreservesRawAndNormalizedTotals()
    {
        UsageEvent value = TestEvents.Create();

        Assert.AreEqual(120L, value.Tokens.ReportedTotal.Value);
        Assert.AreEqual(100L, value.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricOrigin.Derived, value.Tokens.NormalizedTotal.Origin);
        Assert.AreEqual(MetricInclusion.Included, value.Tokens.ReasoningIncludedInOutput);
    }

    [TestMethod]
    public void Event_PreservesProjectPath()
    {
        UsageEvent value = TestEvents.Create() with
        {
            ProjectId = "project-1",
            ProjectPath = @"D:\Projects\AgenTally"
        };

        Assert.AreEqual("project-1", value.ProjectId);
        Assert.AreEqual(@"D:\Projects\AgenTally", value.ProjectPath);
    }

    [TestMethod]
    public void Constructor_RejectsNonUtcOccurredAt()
    {
        DateTimeOffset localTime = new(2026, 7, 16, 8, 0, 0, TimeSpan.FromHours(8));

        Assert.ThrowsExactly<ArgumentException>(() =>
            CreateWithTimes(localTime, DateTimeOffset.UnixEpoch));
    }

    [TestMethod]
    public void Constructor_RejectsNonUtcImportedAt()
    {
        DateTimeOffset localTime = new(2026, 7, 16, 8, 0, 0, TimeSpan.FromHours(8));

        Assert.ThrowsExactly<ArgumentException>(() =>
            CreateWithTimes(DateTimeOffset.UnixEpoch, localTime));
    }

    [TestMethod]
    public void Constructor_RejectsUndefinedSourceKind()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CreateWithTimes(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                sourceKind: (SourceKind)99));
    }

    [TestMethod]
    public void Constructor_RejectsUndefinedCompletionState()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CreateWithTimes(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                completionState: (CompletionState)99));
    }

    [TestMethod]
    public void Constructor_RejectsUndefinedDataQuality()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CreateWithTimes(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                dataQuality: (DataQuality)99));
    }

    private static UsageEvent CreateWithTimes(
        DateTimeOffset occurredAtUtc,
        DateTimeOffset importedAtUtc,
        SourceKind sourceKind = SourceKind.Jsonl,
        CompletionState completionState = CompletionState.Completed,
        DataQuality dataQuality = DataQuality.Exact)
    {
        return new UsageEvent(
            "codex",
            "codex:windows:test",
            "rollout:test",
            "event-1",
            "codex:thread-1:1",
            sourceKind,
            occurredAtUtc,
            importedAtUtc,
            new ModelIdentity(),
            new TokenUsage(),
            completionState,
            dataQuality,
            "codex-v1",
            "fixture-1",
            1);
    }
}
