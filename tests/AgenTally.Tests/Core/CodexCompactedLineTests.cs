using System.IO;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CodexCompactedLineTests
{
    internal static string Compacted(int characters) =>
        "{\"type\":\"compacted\",\"payload\":{\"replacement_history\":[{\"type\":\"message\"," +
        "\"role\":\"user\",\"content\":\"" + new string('X', characters) +
        "\"}],\"latest_token_usage_record\":{\"usage\":{\"input_tokens\":999999}}}}";

    internal static readonly string[] Fixture =
    [
        """{"timestamp":"2026-07-16T01:00:00Z","type":"session_meta","payload":{"id":"compact-fixture","cwd":"C:\\fixture","model_provider":"openai"}}""",
        """{"timestamp":"2026-07-16T01:00:01Z","type":"turn_context","payload":{"turn_id":"fixture-turn","model":"gpt-4.1","cwd":"C:\\fixture"}}""",
        """{"timestamp":"2026-07-16T01:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120},"total_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120}}}}"""
    ];

    [TestMethod]
    public void TryProject_ValidatesWholeRecordAndUsesFinalDiscriminators()
    {
        string valid = Compacted(40_000);
        byte[] projection = Project(valid)!;
        Assert.AreEqual("{\"type\":\"compacted\",\"payload\":{}}", Encoding.UTF8.GetString(projection));
        Assert.IsNotNull(Project(valid.Replace("\"type\":\"compacted\"",
            "\"type\":\"event_msg\",\"type\":\"compacted\"", StringComparison.Ordinal)));
        string[] rejected =
        [
            valid + ",", valid[..^1],
            valid[..^1] + ",\"type\":\"event_msg\"}",
            valid.Replace("\"payload\":{", "\"payload\":{\"type\":\"inter_agent_communication\",", StringComparison.Ordinal),
            valid.Replace("compacted", "response_item", StringComparison.Ordinal),
            valid.Replace(new string('X', 40_000), new string('X', 40_000) + "\\q", StringComparison.Ordinal),
            valid.Replace(new string('X', 40_000), "\\uD800", StringComparison.Ordinal),
            valid[..^1] + ",\"extra\":" + new string('[', 64) + "0" + new string(']', 64) + "}",
            valid[..^1] + ",\"extra\":[" + string.Join(',', Enumerable.Repeat("0", 140_000)) + "]}"
        ];
        foreach (string record in rejected) { Assert.IsNull(Project(record)); }
        byte[] badUtf8 = Encoding.UTF8.GetBytes(valid);
        badUtf8[Array.IndexOf(badUtf8, (byte)'X')] = 0xff;
        using var source = new MemoryStream(badUtf8);
        var projector = new CodexCompactedLineProjector();
        Assert.IsNull(projector.TryProject(source, 0, source.Length, CancellationToken.None));
        Assert.AreEqual(0L, source.Position);
        Assert.IsNull(projector.TryProject(source, 0,
            CodexCompactedLineProjector.MaxSourceBytes + 1, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            projector.TryProject(source, 0, source.Length, cancelled.Token));
        Assert.AreEqual(0L, source.Position);
    }

    [TestMethod]
    public async Task ReadBatchAsync_KeepsSharedLimitIncompleteRetryAndPhysicalOffsets()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("oversized.jsonl");
        await File.WriteAllTextAsync(path, "{}\n" + Compacted(8 * 1024 * 1024 + 1));
        var reader = new IncrementalJsonlReader(new CodexCompactedLineProjector());
        JsonlReadBatch incomplete = await reader.ReadBatchAsync(path, JsonlCursor.Start, 200, CancellationToken.None);
        Assert.AreEqual("jsonl.incomplete_line_too_long", incomplete.Diagnostic?.Code);
        Assert.AreEqual(0L, incomplete.NextCursor.ByteOffset);
        await File.AppendAllTextAsync(path, "\r\n{}\n");
        JsonlReadBatch shared = await new IncrementalJsonlReader().ReadBatchAsync(path, JsonlCursor.Start, 200, CancellationToken.None);
        Assert.AreEqual("jsonl.line_too_long", shared.Diagnostic?.Code);
        JsonlReadBatch complete = await reader.ReadBatchAsync(path, incomplete.NextCursor, 200, CancellationToken.None);
        Assert.IsNull(complete.Diagnostic);
        Assert.HasCount(3, complete.Lines);
        Assert.AreEqual(3L, complete.Lines[1].ByteOffset);
        Assert.AreEqual(new FileInfo(path).Length, complete.NextCursor.ByteOffset);
        Assert.AreEqual(3L, complete.NextCursor.LineNumber);
        Assert.IsTrue(complete.Lines[1].Utf8.Length < 100);
        await File.WriteAllTextAsync(path, "{}\n" + Compacted(8 * 1024 * 1024 + 1).Replace("compacted", "event_msg", StringComparison.Ordinal) + "\n");
        JsonlReadBatch unsupported = await reader.ReadBatchAsync(path, JsonlCursor.Start, 200, CancellationToken.None);
        Assert.AreEqual("jsonl.line_too_long", unsupported.Diagnostic?.Code);
    }

    [TestMethod]
    public async Task CollectAsync_MatchesSmallCompactionAcrossPersistedCursorBoundary()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File(".codex");
        string path = Path.Combine(root, "sessions", "rollout-fixture.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string prefix = string.Join('\n', Fixture[..2]) + "\n" +
            string.Concat(Enumerable.Repeat("{\"type\":\"fixture_padding\"}\n", 197));
        await File.WriteAllTextAsync(path, prefix + Compacted(1) + "\n" + Fixture[2] + "\n");
        using var collector = new CodexCollector(root);
        SourceProbeResult probe = await collector.ProbeAsync(new CollectorContext(directory.Path, TimeProvider.System), CancellationToken.None);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        CollectedBatch[] baseline = await Collect(collector, new(instance, entity, null, CollectionReason.StartupImport));
        await File.WriteAllTextAsync(path, prefix + Compacted(8 * 1024 * 1024 + 1) + "\n");
        CollectedBatch before = Assert.ContainsSingle(await Collect(collector, new(instance, entity, null, CollectionReason.StartupImport)));
        Assert.IsEmpty(before.Diagnostics);
        Assert.AreEqual(200L, CodexCursor.DeserializeOrStart(before.NextCursorJson, true, out _).Jsonl.LineNumber);
        var stored = new StoredCursor(instance.SourceInstanceId, entity.SourceEntityId,
            entity.SourcePath, before.NextCursorJson, before.SourceFingerprint,
            before.ParserVersion, DateTimeOffset.UtcNow, null, null);
        await File.AppendAllTextAsync(path, Fixture[2] + "\n");
        using var restarted = new CodexCollector(root);
        CollectedBatch after = Assert.ContainsSingle(await Collect(restarted, new(instance, entity, stored, CollectionReason.FileChanged)));
        Assert.IsEmpty(after.Diagnostics);
        var expected = Assert.ContainsSingle(baseline.SelectMany(batch => batch.Events));
        var actual = Assert.ContainsSingle(after.Events);
        Assert.AreEqual(expected.DedupKey, actual.DedupKey);
        Assert.AreEqual(expected.Tokens, actual.Tokens);
        Assert.AreEqual(expected.Model, actual.Model);
        Assert.AreEqual(120L, actual.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(new FileInfo(path).Length, CodexCursor.DeserializeOrStart(after.NextCursorJson, true, out _).Jsonl.ByteOffset);
        Assert.DoesNotContain("XXXX", before.NextCursorJson);
        Assert.DoesNotContain("replacement_history", after.NextCursorJson);
        Assert.AreEqual(JsonSerializer.Serialize(baseline.SelectMany(batch => batch.Turns)),
            JsonSerializer.Serialize(new[] { before, after }.SelectMany(batch => batch.Turns)));
    }

    private static byte[]? Project(string value)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(value));
        return new CodexCompactedLineProjector().TryProject(source, 0, source.Length, CancellationToken.None);
    }

    private static async Task<CollectedBatch[]> Collect(CodexCollector collector, CollectionRequest request)
    {
        var batches = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(request, CancellationToken.None)) { batches.Add(batch); }
        return batches.ToArray();
    }
}
