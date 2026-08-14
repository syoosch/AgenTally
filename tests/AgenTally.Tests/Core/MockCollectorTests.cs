using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Mock;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class MockCollectorTests
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [TestMethod]
    public async Task ProbeAsync_ReturnsOnlyExplicitFixture()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("usage.jsonl");
        await File.WriteAllTextAsync(path, Entry("record-1") + "\n", Utf8WithoutBom);
        await File.WriteAllTextAsync(directory.File("unrelated.jsonl"), Entry("other") + "\n", Utf8WithoutBom);
        var collector = new MockCollector(path);

        SourceProbeResult result = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        Assert.HasCount(1, result.Instances);
        Assert.HasCount(1, result.Entities);
        Assert.AreEqual(Path.GetFullPath(path), result.Entities[0].SourcePath);
        Assert.StartsWith("mock:windows:", result.Instances[0].SourceInstanceId);
        Assert.StartsWith("mock:jsonl:", result.Entities[0].SourceEntityId);
        Assert.IsEmpty(result.Diagnostics);
    }

    [TestMethod]
    public void Constructor_DerivesStableDistinctIdentityFromNormalizedFixturePath()
    {
        using var firstDirectory = new TestTempDirectory();
        using var secondDirectory = new TestTempDirectory();
        string firstPath = firstDirectory.File("usage.jsonl");
        string siblingPath = firstDirectory.File("other.jsonl");
        string secondPath = secondDirectory.File("usage.jsonl");

        var first = new MockCollector(firstPath);
        var repeated = new MockCollector(Path.GetFullPath(firstPath));
        var sibling = new MockCollector(siblingPath);
        var second = new MockCollector(secondPath);

        Assert.AreEqual(first.Instance.SourceInstanceId, repeated.Instance.SourceInstanceId);
        Assert.AreEqual(first.Entity.SourceEntityId, repeated.Entity.SourceEntityId);
        Assert.AreEqual(first.Instance.SourceInstanceId, sibling.Instance.SourceInstanceId);
        Assert.AreNotEqual(first.Entity.SourceEntityId, sibling.Entity.SourceEntityId);
        Assert.AreNotEqual(first.Instance.SourceInstanceId, second.Instance.SourceInstanceId);
        Assert.AreNotEqual(first.Entity.SourceEntityId, second.Entity.SourceEntityId);
    }

    [TestMethod]
    public async Task CollectAsync_HandlesIncrementalRecordsAndPartialLine()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("usage.jsonl");
        await File.WriteAllTextAsync(path, Entry("record-1", rawInput: 0, output: null) + "\n", Utf8WithoutBom);
        var collector = new MockCollector(path);

        CollectedBatch first = Assert.ContainsSingle(await CollectAsync(collector));
        UsageEvent firstEvent = Assert.ContainsSingle(first.Events);
        Assert.AreEqual(0L, firstEvent.Tokens.InputReported.Value);
        Assert.AreEqual(MetricOrigin.Unavailable, firstEvent.Tokens.Output.Origin);
        Assert.AreEqual(collector.AgentId, firstEvent.AgentId);
        Assert.AreEqual(
            $"mock:{collector.Entity.SourceEntityId}:record-1",
            firstEvent.DedupKey);
        Assert.AreEqual(1L, firstEvent.SourceRevision);
        Assert.AreEqual(MetricOrigin.Derived, firstEvent.Tokens.NormalizedTotal.Origin);
        Assert.AreEqual(MetricInclusion.Unknown, firstEvent.Tokens.CacheIncludedInInput);
        Assert.AreEqual(MetricInclusion.Unknown, firstEvent.Tokens.ReasoningIncludedInOutput);

        string partialJson = Entry("partial-1", model: null, rawInput: 20, output: 8);
        int splitAt = partialJson.Length / 2;
        await File.AppendAllTextAsync(path, partialJson[..splitAt], Utf8WithoutBom);

        CollectedBatch partial = Assert.ContainsSingle(await CollectAsync(collector, Cursor(first)));
        Assert.IsEmpty(partial.Events);
        Assert.IsEmpty(partial.Diagnostics);

        await File.AppendAllTextAsync(path, partialJson[splitAt..] + "\n", Utf8WithoutBom);

        CollectedBatch completed = Assert.ContainsSingle(await CollectAsync(collector, Cursor(partial)));
        UsageEvent completedEvent = Assert.ContainsSingle(completed.Events);
        Assert.AreEqual("partial-1", completedEvent.EventId);
        Assert.AreEqual(ModelResolutionOrigin.Unknown, completedEvent.Model.ResolutionOrigin);
    }

    [TestMethod]
    public async Task CollectAsync_ReportsFixedDiagnosticForMalformedCompleteLine()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("invalid.jsonl");
        const string invalidRow = "{private-original-row}";
        await File.WriteAllTextAsync(
            path,
            $"{Entry("record-1")}\n{invalidRow}\n{Entry("record-2")}\n",
            Utf8WithoutBom);
        var collector = new MockCollector(path);

        CollectedBatch batch = Assert.ContainsSingle(await CollectAsync(collector));

        Assert.HasCount(2, batch.Events);
        var diagnostic = Assert.ContainsSingle(batch.Diagnostics);
        Assert.AreEqual("mock.invalid_json", diagnostic.Code);
        Assert.AreEqual("模拟日志行不是有效 JSON。", diagnostic.Message);
        Assert.DoesNotContain(invalidRow, diagnostic.Message);
        Assert.IsGreaterThan(0L, diagnostic.ByteOffset!.Value);
    }

    [TestMethod]
    public async Task CollectAsync_RejectsRowWhoseAgentDoesNotMatchCollector()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("wrong-agent.jsonl");
        await File.WriteAllTextAsync(
            path,
            Entry("record-1", agent: "another-agent") + "\n",
            Utf8WithoutBom);
        var collector = new MockCollector(path);

        CollectedBatch batch = Assert.ContainsSingle(await CollectAsync(collector));

        Assert.IsEmpty(batch.Events);
        Assert.AreEqual("mock.invalid_json", Assert.ContainsSingle(batch.Diagnostics).Code);
    }

    [TestMethod]
    public async Task CollectAsync_DoesNotYieldCommittableBatchBeforeFirstCompleteLine()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("only-partial.jsonl");
        await File.WriteAllTextAsync(path, "{\"recordId\":\"still-partial\"", Utf8WithoutBom);
        var collector = new MockCollector(path);

        IReadOnlyList<CollectedBatch> batches = await CollectAsync(collector);

        Assert.IsEmpty(batches);
    }

    [TestMethod]
    public async Task CollectAsync_ThrowsFixedFailureWhenFirstLineExceedsFingerprintLimit()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("oversized-first-line.jsonl");
        string oversizedContent = new string('x', (64 * 1024) + 1) + "\n";
        await File.WriteAllTextAsync(path, oversizedContent, Utf8WithoutBom);
        var collector = new MockCollector(path);

        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await CollectAsync(collector));

        Assert.AreEqual(
            "JSONL 首行超过 64 KiB 指纹上限，未提交读取游标。",
            exception.Message);
        Assert.DoesNotContain(oversizedContent, exception.Message);
    }

    [TestMethod]
    public async Task CollectAsync_YieldsAtMostTwoHundredEventsPerBatch()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("large.jsonl");
        string contents = string.Join('\n', Enumerable.Range(1, 500).Select(i => Entry($"record-{i}"))) + "\n";
        await File.WriteAllTextAsync(path, contents, Utf8WithoutBom);
        var collector = new MockCollector(path);

        IReadOnlyList<CollectedBatch> batches = await CollectAsync(collector);

        CollectionAssert.AreEqual(new[] { 200, 200, 100 }, batches.Select(batch => batch.Events.Count).ToArray());
        Assert.IsTrue(batches.All(batch => batch.Events.Count <= 200));
        Assert.AreEqual(500, batches.Sum(batch => batch.Events.Count));
    }

    [TestMethod]
    public async Task CollectAsync_StopsAtFiveThousandLinesAndContinuesFromReturnedCursor()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("collection-limit.jsonl");
        string contents = string.Join(
            '\n',
            Enumerable.Range(1, 5001).Select(i => Entry($"record-{i}"))) + "\n";
        await File.WriteAllTextAsync(path, contents, Utf8WithoutBom);
        var collector = new MockCollector(path);

        IReadOnlyList<CollectedBatch> firstRun = await CollectAsync(collector);

        Assert.HasCount(25, firstRun);
        Assert.AreEqual(5000, firstRun.Sum(batch => batch.Events.Count));
        CollectorDiagnostic limitDiagnostic = Assert.ContainsSingle(
            firstRun[^1].Diagnostics.Where(
                item => item.Code == "collector.batch_limit_reached"));
        Assert.AreEqual(
            "单次采集已达到 25 批（最多 5000 行）上限，将从当前游标继续。",
            limitDiagnostic.Message);

        CollectedBatch continuation = Assert.ContainsSingle(
            await CollectAsync(collector, Cursor(firstRun[^1])));
        Assert.AreEqual("record-5001", Assert.ContainsSingle(continuation.Events).EventId);
    }

    [TestMethod]
    public async Task CollectAsync_IgnoresStoredCursorBelongingToAnotherSource()
    {
        using var directory = new TestTempDirectory();
        string firstPath = directory.File("first.jsonl");
        string secondPath = directory.File("second.jsonl");
        await File.WriteAllTextAsync(firstPath, Entry("first") + "\n", Utf8WithoutBom);
        await File.WriteAllTextAsync(secondPath, Entry("second") + "\n", Utf8WithoutBom);
        var firstCollector = new MockCollector(firstPath);
        var secondCollector = new MockCollector(secondPath);
        CollectedBatch firstBatch = Assert.ContainsSingle(await CollectAsync(firstCollector));

        CollectedBatch secondBatch = Assert.ContainsSingle(
            await CollectAsync(secondCollector, Cursor(firstBatch)));

        Assert.AreEqual("second", Assert.ContainsSingle(secondBatch.Events).EventId);
        CollectorDiagnostic diagnostic = Assert.ContainsSingle(secondBatch.Diagnostics);
        Assert.AreEqual("collector.cursor_source_mismatch", diagnostic.Code);
        Assert.AreEqual(
            "已忽略不属于当前来源的读取游标，并从头重新读取。",
            diagnostic.Message);
    }

    [TestMethod]
    public async Task ProbeAsync_ReportsMissingExplicitFixtureWithoutScanningDirectory()
    {
        using var directory = new TestTempDirectory();
        string missingPath = directory.File("missing.jsonl");
        await File.WriteAllTextAsync(directory.File("other.jsonl"), Entry("other") + "\n", Utf8WithoutBom);
        var collector = new MockCollector(missingPath);

        SourceProbeResult result = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        Assert.IsEmpty(result.Instances);
        Assert.IsEmpty(result.Entities);
        Assert.AreEqual("mock.source_missing", Assert.ContainsSingle(result.Diagnostics).Code);
    }

    private static async Task<IReadOnlyList<CollectedBatch>> CollectAsync(
        MockCollector collector,
        StoredCursor? cursor = null)
    {
        var batches = new List<CollectedBatch>();
        var request = new CollectionRequest(
            collector.Instance,
            collector.Entity,
            cursor,
            CollectionReason.ManualRequest);

        await foreach (CollectedBatch batch in collector.CollectAsync(request, CancellationToken.None))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static StoredCursor Cursor(CollectedBatch batch) => new(
        batch.Instance.SourceInstanceId,
        batch.Entity.SourceEntityId,
        batch.Entity.SourcePath,
        batch.NextCursorJson,
        batch.SourceFingerprint,
        batch.ParserVersion,
        DateTimeOffset.UtcNow,
        null,
        null);

    private static string Entry(
        string recordId,
        string? model = "gpt-test",
        long? rawInput = 10,
        long? output = 5,
        string agent = "mock-agent")
    {
        var entry = new
        {
            recordId,
            timestamp = "2026-07-15T10:00:00.0000000+00:00",
            agent,
            model,
            rawInput,
            freshInput = rawInput,
            cacheRead = (long?)null,
            cacheWrite = 0L,
            output,
            reasoning = (long?)null,
            tool = (long?)null,
            totalProcessed = 15L
        };

        return JsonSerializer.Serialize(entry, JsonOptions);
    }
}
