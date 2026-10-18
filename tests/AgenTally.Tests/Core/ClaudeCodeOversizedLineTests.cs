using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.ClaudeCode;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class ClaudeCodeOversizedLineTests
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly string Payload = new('A', 64);

    [TestMethod]
    public async Task CollectAsync_PreservesOversizedSubagentChainAcrossCursorRestart()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File(".claude");
        string path = Path.Combine(root, "projects", "fixture-project",
            "fixture-session", "subagents", "agent-fixture.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string[] fixture = Fixture();
        var initial = new List<string>(fixture[..2]);
        for (int index = 0; index < 197; index++)
        {
            JsonObject progress = JsonNode.Parse(fixture[0])!.AsObject();
            progress["type"] = "progress";
            progress["uuid"] = $"invented-padding-{index}";
            progress["parentUuid"] = "parent-1";
            progress.Remove("message");
            initial.Add(progress.ToJsonString());
        }

        await File.WriteAllTextAsync(path,
            string.Join('\n', initial.Concat(fixture[2..])) + "\n", Utf8);
        var collector = new ClaudeCodeCollector(root);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System), CancellationToken.None);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        CollectedBatch[] baseline = await Collect(collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport));

        await File.WriteAllTextAsync(path, string.Join('\n', initial) + "\n" +
            Large(fixture[2]) + "\n", Utf8);
        CollectedBatch before = Assert.ContainsSingle(await Collect(
            new ClaudeCodeCollector(root),
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport)));
        Assert.IsEmpty(before.Diagnostics);
        Assert.AreEqual(200L, Cursor(before).Jsonl.LineNumber);
        Assert.AreEqual(new FileInfo(path).Length, Cursor(before).Jsonl.ByteOffset);
        await File.AppendAllTextAsync(path,
            Large(fixture[3]) + "\n" + fixture[4] + "\n", Utf8);
        long length = new FileInfo(path).Length;
        DateTime writeTime = File.GetLastWriteTimeUtc(path);
        var stored = new StoredCursor(instance.SourceInstanceId, entity.SourceEntityId,
            entity.SourcePath, before.NextCursorJson, before.SourceFingerprint,
            before.ParserVersion, DateTimeOffset.UtcNow, null, null);
        CollectedBatch after = Assert.ContainsSingle(await Collect(
            new ClaudeCodeCollector(root),
            new CollectionRequest(instance, entity, stored, CollectionReason.FileChanged)));
        Assert.IsEmpty(after.Diagnostics);
        Assert.AreEqual(202L, Cursor(after).Jsonl.LineNumber);
        Assert.AreEqual(length, Cursor(after).Jsonl.ByteOffset);
        Assert.AreEqual(writeTime, File.GetLastWriteTimeUtc(path));
        Assert.AreEqual(length, new FileInfo(path).Length);
        Assert.AreEqual(JsonSerializer.Serialize(Cursor(baseline[^1]).State.NodeTurnIds),
            JsonSerializer.Serialize(Cursor(after).State.NodeTurnIds));
        Assert.AreEqual(Cursor(baseline[^1]).State.ProjectPath, Cursor(after).State.ProjectPath);
        Assert.AreEqual(Cursor(baseline[^1]).State.SubagentHash, Cursor(after).State.SubagentHash);
        Assert.AreEqual(JsonSerializer.Serialize(Semantics(baseline)),
            JsonSerializer.Serialize(Semantics([before, after])));
        Assert.AreEqual(JsonSerializer.Serialize(baseline.SelectMany(batch => batch.EventTools)),
            JsonSerializer.Serialize(new[] { before, after }.SelectMany(batch => batch.EventTools)));
        Assert.DoesNotContain(Payload, after.NextCursorJson);
        Assert.DoesNotContain("invented.bin", after.NextCursorJson);
        Assert.HasCount(1, Cursor(after).State.Turns!);
        Assert.AreEqual(61L, new[] { before, after }.SelectMany(batch => batch.Events)
            .Sum(usage => usage.Tokens.NormalizedTotal.Value));
    }

    [TestMethod]
    public void TryProject_UsesFinalDuplicateFieldsAndExcludesPayloads()
    {
        string record = Fixture()[3].Replace("\"parentUuid\":\"case-A\"",
            "\"parentUuid\":\"discarded-parent\",\"parentUuid\":\"case-A\"",
            StringComparison.Ordinal).Replace(Payload,
            new string('A', 40_000) + "汉😀\\uD83D\\uDE00\\\"\\\\",
            StringComparison.Ordinal);
        record = record[..^1] + ",\"isSidechain\":false,\"isMeta\":true}";
        record = record.Replace("\"type\":\"document\"",
            "\"type\":\"document\",\"id\":\"invented-attachment-id\"," +
            "\"name\":\"X:/synthetic/invented-attachment.bin\",\"text\":\"invented body\"",
            StringComparison.Ordinal);
        byte[] projected = Project(Encoding.UTF8.GetBytes(record))!;
        using JsonDocument document = JsonDocument.Parse(projected);
        Assert.AreEqual("case-A", document.RootElement.GetProperty("parentUuid").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("isSidechain").GetBoolean());
        Assert.IsTrue(document.RootElement.GetProperty("isMeta").GetBoolean());
        Assert.AreEqual("fixture-session",
            document.RootElement.GetProperty("sessionId").GetString());
        Assert.DoesNotContain("opaque_payload", Encoding.UTF8.GetString(projected));
        Assert.DoesNotContain("invented-attachment", Encoding.UTF8.GetString(projected));
        Assert.DoesNotContain("invented body", Encoding.UTF8.GetString(projected));
        Assert.IsNull(Project(Encoding.UTF8.GetBytes(record[..^1] + ",\"isMeta\":false}")));
        JsonNode envelope = JsonNode.Parse(Fixture()[2])!;
        envelope["message"]!["content"]!.AsArray().Add("invented private body");
        envelope["toolUseResult"] = "invented private tool result";
        byte[] opaque = Project(Encoding.UTF8.GetBytes(envelope.ToJsonString()))!;
        Assert.DoesNotContain("invented private", Encoding.UTF8.GetString(opaque));
    }

    [TestMethod]
    public void TryProject_RejectsMalformedUnsupportedAndBoundedInputs()
    {
        string record = Fixture()[2];
        string prompt = record.Replace("\"isSidechain\":true", "\"isSidechain\":false",
            StringComparison.Ordinal);
        string[] rejected =
        [
            record + ",", record.Replace(Payload, new string('A', 40_000) + "\\q",
                StringComparison.Ordinal),
            record.Replace(Payload, "\\uD800", StringComparison.Ordinal),
            record[..^1] + ",\"opaque_depth\":" + new string('[', 64) + "0" +
                new string(']', 64) + "}",
            prompt, Fixture()[1],
            record.Replace("X:/synthetic/fixture-project", new string('C', 33_000),
                StringComparison.Ordinal),
            record.Replace("\"role\":\"user\"", "\"role\":\"user\",\"usage\":null",
                StringComparison.Ordinal),
            record.Replace(Payload, new string('A', 262_144) + "\" , \"opaque_array\":[" +
                string.Join(',', Enumerable.Repeat("0", 140_000)) + "] , \"opaque_tail\":\"",
                StringComparison.Ordinal)
        ];
        for (int index = 0; index < rejected.Length; index++)
        {
            Assert.IsNull(Project(Encoding.UTF8.GetBytes(rejected[index])), $"Case {index}");
        }

        byte[] invalidUtf8 = Encoding.UTF8.GetBytes(record);
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'A', record.IndexOf("opaque_payload",
            StringComparison.Ordinal))] = 0xff;
        Assert.IsNull(Project(invalidUtf8));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(record));
        Assert.IsNull(new ClaudeCodeOversizedLineProjector().TryProject(source, 0,
            ClaudeCodeOversizedLineProjector.MaxSourceBytes + 1, CancellationToken.None));
        Assert.AreEqual(0L, source.Position);
    }

    [TestMethod]
    public async Task ReadBatchAsync_RequiresCompleteLineBeforeProjecting()
    {
        using var directory = new TestTempDirectory();
        string path = directory.File("incomplete.jsonl");
        await File.WriteAllTextAsync(path, "{}\n" + Large(Fixture()[2]), Utf8);
        var reader = new IncrementalJsonlReader(new ClaudeCodeOversizedLineProjector());
        JsonlReadBatch incomplete = await reader.ReadBatchAsync(
            path, JsonlCursor.Start, 200, CancellationToken.None);
        Assert.AreEqual("jsonl.incomplete_line_too_long", incomplete.Diagnostic?.Code);
        Assert.IsEmpty(incomplete.Lines);
        Assert.AreEqual(0L, incomplete.NextCursor.ByteOffset);
        await File.AppendAllTextAsync(path, "\r\n{}\n", Utf8);
        JsonlReadBatch complete = await reader.ReadBatchAsync(
            path, incomplete.NextCursor, 200, CancellationToken.None);
        Assert.IsNull(complete.Diagnostic);
        Assert.HasCount(3, complete.Lines);
        Assert.AreEqual(3L, complete.Lines[1].ByteOffset);
        Assert.AreEqual(new FileInfo(path).Length, complete.NextCursor.ByteOffset);
        Assert.IsTrue(complete.Lines[1].Utf8.Length < 4096);
    }

    private static byte[]? Project(byte[] input)
    {
        using var source = new MemoryStream(input);
        source.Position = 1;
        byte[]? result = new ClaudeCodeOversizedLineProjector().TryProject(
            source, 0, input.LongLength, CancellationToken.None);
        Assert.AreEqual(1L, source.Position);
        return result;
    }

    private static object[] Semantics(IEnumerable<CollectedBatch> batches) =>
        batches.SelectMany(batch => batch.Events).Select(usage => (object)new
        {
            usage.EventId, usage.DedupKey, usage.Model, usage.Tokens,
            usage.SessionId, usage.TurnIdHash, usage.ProjectId, usage.ProjectPath
        }).ToArray();

    private static ClaudeCodeCursor Cursor(CollectedBatch batch) =>
        ClaudeCodeCursor.DeserializeOrStart(batch.NextCursorJson, true, out _);

    private static async Task<CollectedBatch[]> Collect(
        IAgentCollector collector, CollectionRequest request)
    {
        var result = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(request, CancellationToken.None))
        {
            result.Add(batch);
        }

        return result.ToArray();
    }

    private static string Large(string record) => record.Replace(Payload,
        new string('A', JsonlCursor.MaxLogicalLineBytes + 1), StringComparison.Ordinal);

    private static string[] Fixture() => File.ReadAllLines(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ClaudeCode",
        "synthetic-oversized-nonprompt.jsonl")));
}
