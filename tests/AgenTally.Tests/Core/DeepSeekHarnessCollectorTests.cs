using System.IO;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.DeepSeekHarness;
using AgenTally.Core.Hosting;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSharp;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class DeepSeekHarnessCollectorTests
{
    private const long Time = 1_786_080_000_000L;
    private const string Private = "SYNTHETIC_PRIVATE_BODY_MUST_NEVER_BE_STORED";

    [TestMethod]
    public async Task CompressedV4_ProjectsDisjointUsageFirstHumanPreviewToolsAndTitle()
    {
        using var directory = new TestTempDirectory();
        var log = new Log("session-synthetic");
        log.Add("turn/start", new { turn = 1 });
        log.Add("user/message", User("human-1", "  检查\n 压缩日志 " + new string('x', 125) + Private));
        log.Add("user/message", User("human-2", Private));
        log.Add("user/message", User("context", Private, "runtime-context"));
        log.Add("session/title", new { title = "  合成\n会话名称  " });
        log.Add("step/start", new { turn = 1, step = 0 });
        log.Add("assistant/message", Assistant("call-1", 1, 0));
        log.Add("tool/call", new { turn = 1, step = 0, callId = "read-1", name = "read", arguments = Private });
        log.Add("tool/result", new { turn = 1, step = 0, message = new { source = new { kind = "tool", callId = "read-1" }, content = Private } });
        log.Add("step/end", new { turn = 1, step = 0 });
        log.Add("turn/end", new { turn = 1, reason = new { kind = "completed" } });
        string home = directory.File(".dsh");
        string path = Write(home, log, compressed: true);
        string before = HashFile(path);
        var collector = new DeepSeekHarnessCollector(home);
        var (instance, entity) = await Probe(collector, directory.Path);
        var batches = await Collect(collector, instance, entity);
        CollectedBatch batch = Assert.ContainsSingle(batches);
        Assert.IsEmpty(batch.Diagnostics);
        UsageEvent usage = Assert.ContainsSingle(batch.Events);
        Assert.AreEqual(40L, usage.Tokens.UncachedInput.Value);
        Assert.AreEqual(60L, usage.Tokens.CacheRead.Value);
        Assert.AreEqual(0L, usage.Tokens.CacheWrite.Value);
        Assert.AreEqual(20L, usage.Tokens.Output.Value);
        Assert.AreEqual(100L, usage.Tokens.InputReported.Value);
        Assert.AreEqual(120L, usage.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricOrigin.Unavailable, usage.Tokens.Reasoning.Origin);
        Assert.AreEqual("fixture-model", usage.Model.RawModel);
        Assert.AreEqual("deepseek-account", usage.Model.ProviderId);
        Assert.AreEqual("D:\\invented\\project", usage.ProjectPath);
        Assert.AreEqual("合成 会话名称", Assert.ContainsSingle(batch.Sessions).SessionName);
        Assert.AreEqual("read", Assert.ContainsSingle(batch.EventTools).ToolName);
        UsageTurnMetadata turn = batch.Turns.Last();
        Assert.AreEqual(2, turn.UserMessageCount);
        Assert.AreEqual(120, turn.PromptPreview!.EnumerateRunes().Count());
        Assert.StartsWith("检查 压缩日志", turn.PromptPreview);
        Assert.IsNotNull(turn.CompletedAtUtc);
        Assert.DoesNotContain(Private, batch.NextCursorJson);
        Assert.DoesNotContain("replayState", batch.NextCursorJson);
        Assert.DoesNotContain("arguments", batch.NextCursorJson);
        Assert.DoesNotContain("pendingBase64", batch.NextCursorJson);
        Assert.AreEqual(before, HashFile(path));
        Assert.IsTrue(collector.IsRelevantChangePath(path));
        Assert.IsTrue(collector.IsWithinMonitoredRoots(instance, path));
        Assert.IsFalse(collector.IsRelevantChangePath(directory.File(".credentials.yaml")));
    }

    [TestMethod]
    public async Task CompressedV4_ResumesInsideFrameAndRetriesTornTailWithoutReplay()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File(".dsh");
        var log = new Log("session-resume");
        log.Add("turn/start", new { turn = 1 });
        log.Add("user/message", User("human", "Synthetic resumable task"));
        log.Add("step/start", new { turn = 1, step = 0 });
        log.Add("assistant/message", Assistant("call-first", 1, 0));
        for (int index = 0; index < 230; index++)
            log.Add("extension/opaque", index % 2 == 0 ? (object)Private : new { text = Private });
        log.Add("step/end", new { turn = 1, step = 0 });
        log.Add("step/start", new { turn = 1, step = 1 });
        log.Add("assistant/message", Assistant("call-next", 1, 1));
        string path = Write(home, log, compressed: true, oneFrame: true);
        var collector = new DeepSeekHarnessCollector(home);
        var (instance, entity) = await Probe(collector, directory.Path);
        CollectedBatch first = await collector.CollectAsync(
            new(instance, entity, null, CollectionReason.StartupImport), CancellationToken.None).FirstAsync();
        Assert.AreEqual(1, first.Events.Count);
        Assert.IsTrue(collector.TryGetCursorByteOffset(Store(first), out long halfway));
        Assert.AreEqual(0L, halfway);
        Assert.DoesNotContain(Private, first.NextCursorJson);
        var continued = await Collect(collector, instance, entity, Store(first));
        Assert.AreEqual(1, continued.Sum(batch => batch.Events.Count));
        CollectedBatch completed = continued.Last();
        Assert.IsTrue(collector.TryGetCursorByteOffset(Store(completed), out long offset));
        Assert.AreEqual(new FileInfo(path).Length, offset);
        Assert.IsEmpty(await Collect(collector, instance, entity, Store(completed)));

        object appended = log.Add("assistant/message", Assistant("call-appended", 1, 1));
        byte[] suffix = Frame([appended]);
        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write))
            await append.WriteAsync(suffix.AsMemory(0, 8));
        CollectedBatch torn = Assert.ContainsSingle(await Collect(collector, instance, entity, Store(completed)));
        Assert.IsEmpty(torn.Events);
        Assert.AreEqual("deepseek-harness.incomplete_frame", Assert.ContainsSingle(torn.Diagnostics).Code);
        Assert.IsTrue(collector.TryGetCursorByteOffset(Store(torn), out long tornOffset));
        Assert.AreEqual(offset, tornOffset);
        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write))
            await append.WriteAsync(suffix.AsMemory(8));
        CollectedBatch repaired = Assert.ContainsSingle(await Collect(collector, instance, entity, Store(torn)));
        Assert.AreEqual(1, repaired.Events.Count);
        Assert.IsEmpty(repaired.Diagnostics);
        Assert.IsEmpty(await Collect(collector, instance, entity, Store(repaired)));
    }

    [TestMethod]
    public async Task V4_UsesChildHeaderAndCatalogWhileSuppressingExactForkHistory()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File(".dsh");
        var parent = new Log("session-parent");
        parent.Add("turn/start", new { turn = 1 });
        parent.Add("user/message", User("human", "Synthetic parent task"));
        parent.Add("step/start", new { turn = 1, step = 0 });
        parent.Add("assistant/message", Assistant("parent-call", 1, 0));
        parent.Add("tool/call", new { turn = 1, step = 0, callId = "spawn-call", name = "subagent", arguments = Private });
        parent.Add("subagent/catalog", new { version = 0, childId = "session-child", childCreatedAt = Time, mode = "continuable" });
        Write(home, parent, compressed: true);
        var child = new Log("session-child", parent: "session-parent", child: true);
        child.Add("turn/start", new { turn = 1 });
        child.Add("user/message", User("delegated", Private));
        child.Add("step/start", new { turn = 1, step = 0 });
        child.Add("assistant/message", Assistant("child-call", 1, 0));
        Write(home, child, compressed: true);
        var fork = new Log("session-fork", parent: "session-parent", seeded: true);
        fork.Add("turn/start", new { turn = 1 });
        fork.Add("user/message", User("inherited", Private));
        fork.Add("assistant/message", Assistant("parent-call", 1, 0));
        fork.Add("session/end-seed", new { inherited = true });
        fork.Add("session/end-seed", new { inherited = true });
        fork.Add("turn/start", new { turn = 2 });
        fork.Add("user/message", User("fork-human", "Synthetic fork task"));
        fork.Add("step/start", new { turn = 2, step = 0 });
        fork.Add("assistant/message", Assistant("fork-own-call", 2, 0));
        Write(home, fork, compressed: false);
        var collector = new DeepSeekHarnessCollector(home);
        SourceProbeResult probe = await collector.ProbeAsync(new(directory.Path, TimeProvider.System), CancellationToken.None);
        Assert.AreEqual(3, probe.Entities.Count);
        var batches = new List<CollectedBatch>();
        foreach (var entity in probe.Entities) batches.AddRange(await Collect(collector, probe.Instances.Single(), entity));
        Assert.AreEqual(3, batches.Sum(batch => batch.Events.Count));
        var childBatch = batches.Single(batch => batch.Sessions.Single().SessionId == "session-child");
        var childSession = childBatch.Sessions.Single();
        Assert.AreEqual(SessionRelationState.Confirmed, childSession.RelationState);
        Assert.AreEqual("session-parent", childSession.DirectParentSessionId);
        Assert.AreEqual(SessionRole.Subagent, childSession.SessionRole);
        Assert.IsTrue(childBatch.Turns.All(turn => turn.PromptPreview is null && turn.UserMessageCount == 0));
        Assert.DoesNotContain(Private, childBatch.NextCursorJson);
        UsageTurnDispatch dispatch = Assert.ContainsSingle(batches.SelectMany(batch => batch.Dispatches));
        Assert.AreEqual(childSession.AgentPathHash, dispatch.TargetAgentHash);
        var forkBatch = batches.Single(batch => batch.Sessions.Single().SessionId == "session-fork");
        Assert.AreEqual(1, forkBatch.Events.Count);
        Assert.AreEqual("session-parent", forkBatch.Sessions.Single().ForkedFromSessionId);
        Assert.IsNull(forkBatch.Sessions.Single().DirectParentSessionId);
        Assert.DoesNotContain(Private, forkBatch.NextCursorJson);
    }

    [TestMethod]
    public async Task V4_GoalContinuationRetainsHumanOriginWithoutSavingGoalBodies()
    {
        using var directory = new TestTempDirectory();
        var log = new Log("session-goal");
        log.Add("turn/start", new { turn = 1 });
        log.Add("user/message", User("human", "Synthetic human goal"));
        log.Add("step/start", new { turn = 1, step = 0 });
        log.Add("assistant/message", Assistant("call-human", 1, 0));
        log.Add("tool/call", new { turn = 1, step = 0, callId = "goal-call", name = "create_goal", arguments = Private });
        log.Add("goal/change", new { operation = "create", goal = new { id = "goal-synthetic", revision = 1, objective = Private } });
        log.Add("turn/end", new { turn = 1 });
        log.Add("turn/start", new { turn = 2 });
        log.Add("user/message", new { id = "internal", role = "user", source = new { kind = "goal", goalId = "goal-synthetic", revision = 1, round = 1 }, content = new[] { new { type = "text", text = Private } } });
        log.Add("step/start", new { turn = 2, step = 0 });
        log.Add("assistant/message", Assistant("call-internal", 2, 0));
        string home = directory.File(".dsh");
        Write(home, log, compressed: true);
        var collector = new DeepSeekHarnessCollector(home);
        var (instance, entity) = await Probe(collector, directory.Path);
        var batch = Assert.ContainsSingle(await Collect(collector, instance, entity));
        var human = batch.Turns.First(turn => turn.PromptPreview is not null);
        var automatic = batch.Turns.Last();
        Assert.AreEqual(human.TurnIdHash, automatic.PromptOriginTurnIdHash);
        Assert.AreNotEqual(human.TurnIdHash, automatic.TurnIdHash);
        Assert.IsNull(automatic.PromptPreview);
        Assert.AreEqual(0, automatic.UserMessageCount);
        Assert.AreEqual(2, batch.Events.Count);
        Assert.DoesNotContain(Private, batch.NextCursorJson);
    }

    [TestMethod]
    public async Task V4_RefusesUnknownHighestGenerationAndCorruptFramesWithoutAdvancing()
    {
        using var directory = new TestTempDirectory();
        var log = new Log("session-guard");
        log.Add("turn/start", new { turn = 1 });
        log.Add("user/message", User("human", "Synthetic safety task"));
        log.Add("step/start", new { turn = 1, step = 0 });
        log.Add("assistant/message", Assistant("call", 1, 0));
        string home = directory.File(".dsh");
        string path = Write(home, log, compressed: true);
        var collector = new DeepSeekHarnessCollector(home);
        var (instance, entity) = await Probe(collector, directory.Path);
        CollectedBatch good = Assert.ContainsSingle(await Collect(collector, instance, entity));
        object next = log.Add("assistant/message", Assistant("next-call", 1, 0));
        byte[] damaged = Frame([next]);
        damaged[^1] ^= 1;
        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write)) await append.WriteAsync(damaged);
        CollectedBatch rejected = Assert.ContainsSingle(await Collect(collector, instance, entity, Store(good)));
        Assert.IsEmpty(rejected.Events);
        Assert.AreEqual(good.NextCursorJson, rejected.NextCursorJson);
        Assert.AreEqual("deepseek-harness.invalid_record", rejected.Diagnostics.Single().Code);
        string future = Path.Combine(Path.GetDirectoryName(path)!, "session.v2147483648.jsonl.zstd");
        await File.WriteAllBytesAsync(future, Frame([log.Header]));
        var (_, futureEntity) = await Probe(collector, directory.Path);
        Assert.AreEqual(future, futureEntity.SourcePath);
        var unsupported = Assert.ContainsSingle(await Collect(collector, instance, futureEntity));
        Assert.IsEmpty(unsupported.Events);
        Assert.AreEqual("deepseek-harness.unsupported_format", unsupported.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void V4_TokenClassificationRequiresExactArithmeticAndPreservesUnavailable()
    {
        using var input = JsonDocument.Parse("""{"inputTokens":40,"outputTokens":20,"cacheReadTokens":60,"cacheWriteTokens":0,"totalTokens":120,"reasoningTokens":5}""");
        TokenUsage known = DeepSeekHarnessParser.Tokens(input.RootElement);
        Assert.AreEqual(15L, known.Output.Value);
        Assert.AreEqual(5L, known.Reasoning.Value);
        Assert.AreEqual(120L, known.NormalizedTotal.Value);
        Assert.AreEqual(MetricInclusion.Included, known.ReasoningIncludedInOutput);
        using var absent = JsonDocument.Parse("""{"inputTokens":40,"outputTokens":20}""");
        TokenUsage missing = DeepSeekHarnessParser.Tokens(absent.RootElement);
        Assert.AreEqual(MetricOrigin.Unavailable, missing.CacheRead.Origin);
        Assert.AreEqual(MetricOrigin.Unavailable, missing.CacheWrite.Origin);
        Assert.AreEqual(MetricOrigin.Unavailable, missing.NormalizedTotal.Origin);
        foreach (string invalid in new[]
        {
            """{"inputTokens":40,"outputTokens":20,"cacheReadTokens":60,"cacheWriteTokens":0,"totalTokens":119}""",
            """{"inputTokens":-1,"outputTokens":20}""",
            """{"inputTokens":"40","outputTokens":20}""",
            """{"inputTokens":9007199254740992,"outputTokens":20}"""
        })
        {
            using var bad = JsonDocument.Parse(invalid);
            Assert.ThrowsExactly<InvalidDataException>(() => DeepSeekHarnessParser.Tokens(bad.RootElement));
        }
    }

    [TestMethod]
    public async Task CoreHost_DeepSeekMaintenanceFreezesPricesPreservesHistoryAndClearBaseline()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File(".dsh");
        string database = directory.File("deepseek.db");
        var log = new Log("session-maintenance");
        log.Add("turn/start", new { turn = 1 });
        log.Add("user/message", User("human", "Synthetic maintenance task"));
        log.Add("step/start", new { turn = 1, step = 0 });
        log.Add("assistant/message", Assistant("call-original", 1, 0));
        string path = Write(home, log, compressed: true);
        string[] options = ["--database", database, "--codex-home", directory.File(".codex"),
            "--deepseek-harness-home", home, "--gemini-home", directory.File("empty-gemini"),
            "--opencode-home", directory.File("empty-opencode")];
        var storage = new StorageOptions(database);
        var connections = new SqliteConnectionFactory(storage);
        async Task<int> Run(string mode)
        {
            using var output = new StringWriter();
            return await new CoreHost(storage, output: output).RunAsync([mode, .. options]);
        }
        Assert.AreEqual(0, await Run("--once"));
        var prices = new SqlitePriceLedger(connections);
        Assert.AreEqual(1, await prices.SetCustomPriceAsync(new("fixture-model", 2m, 0.2m, 3m, 8m), CancellationToken.None));
        string original = await Facts(connections);
        await prices.SetCustomPriceAsync(new("fixture-model", 20m, 2m, 30m, 80m), CancellationToken.None);
        Assert.AreEqual(0, await Run("--rescan-codex"));
        Assert.AreEqual(original, await Facts(connections));
        Assert.AreEqual(0, await Run("--once"));
        Assert.AreEqual(original, await Facts(connections));
        byte[] good = await File.ReadAllBytesAsync(path);
        await File.AppendAllTextAsync(path, "invalid-compressed-tail", new UTF8Encoding(false));
        Assert.AreNotEqual(0, await Run("--rescan-codex"));
        Assert.AreEqual(original, await Facts(connections));
        Assert.AreNotEqual(0, await Run("--clear-statistics"));
        Assert.AreEqual(original, await Facts(connections));
        await File.WriteAllBytesAsync(path, good);
        File.Delete(path); // Generated source only: already extracted history must survive.
        Assert.AreEqual(0, await Run("--rescan-codex"));
        Assert.AreEqual(original, await Facts(connections));
        await File.WriteAllBytesAsync(path, good);
        Assert.AreEqual(0, await Run("--clear-statistics"));
        Assert.AreEqual("[]", await Facts(connections));
        Assert.AreEqual(0, await Run("--once"));
        Assert.AreEqual("[]", await Facts(connections));
        object next = log.Add("assistant/message", Assistant("new-after-clear", 1, 0));
        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write)) await append.WriteAsync(Frame([next]));
        Assert.AreEqual(0, await Run("--once"));
        Assert.AreNotEqual("[]", await Facts(connections));

        using var output = new StringWriter();
        int guarded = await new CoreHost(storage, output: output).RunAsync(
            ["--once", "--codex-home", directory.File(".codex"), "--deepseek-harness-home", home,
             "--database", Path.Combine(home, "must-not-write.db")]);
        Assert.AreNotEqual(0, guarded);
        Assert.IsFalse(File.Exists(Path.Combine(home, "must-not-write.db")));
    }

    private static async Task<string> Facts(SqliteConnectionFactory connections)
    {
        await using var connection = await connections.OpenReaderAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dedup_key, uncached_input_value, cache_read_value, cache_write_value,
                output_value, normalized_total_value, price_rule_id, estimated_cost_usd
            FROM usage_events WHERE agent_id = 'deepseek-harness' ORDER BY dedup_key;
            """;
        var rows = new List<object[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
        return JsonSerializer.Serialize(rows);
    }

    private static object User(string id, string text, string kind = "user") => new
    { id, role = "user", source = new { kind }, content = new[] { new { type = "text", text } } };
    private static object Assistant(string id, long turn, long step) => new
    {
        turn, step, usage = new { inputTokens = 40, cacheReadTokens = 60, cacheWriteTokens = 0, outputTokens = 20, totalTokens = 120 },
        message = new { id, role = "assistant", source = new { kind = "model", provider = "deepseek-account", model = "fixture-model", replayState = Private },
            content = new object[] { new { type = "text", text = Private }, new { type = "tool-call", id = "read-1", name = "read", arguments = Private } } },
        stream = new object[] { new { type = "text-chunks", texts = new[] { Private } } }
    };

    private sealed class Log
    {
        internal string Id { get; }
        internal object Header { get; }
        internal List<object> Records { get; } = [];
        private long _seq;
        internal Log(string id, string? parent = null, bool child = false, bool seeded = false)
        {
            Id = id;
            Header = new { type = "session", version = 4, id, createdAt = Time, cwd = "D:\\invented\\project",
                parentSession = parent, origin = child ? "subagent" : null, isSeeded = seeded };
            Records.Add(Header);
        }
        internal object Add(string type, object data)
        {
            object record = new { type, seq = _seq, time = Time + ++_seq, data };
            Records.Add(record); return record;
        }
    }
    private static string Write(string home, Log log, bool compressed, bool oneFrame = false)
    {
        string directory = Path.Combine(home, "sessions", "synthetic-project", log.Id);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "session.v4.jsonl" + (compressed ? ".zstd" : ""));
        if (!compressed) File.WriteAllText(path, string.Join('\n', log.Records.Select(static value => JsonSerializer.Serialize(value))) + "\n", new UTF8Encoding(false));
        else File.WriteAllBytes(path, oneFrame ? Frame(log.Records) : log.Records.SelectMany(record => Frame([record])).ToArray());
        return path;
    }
    private static byte[] Frame(IEnumerable<object> records)
    {
        using var compressor = new Compressor();
        compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor.Wrap(Encoding.UTF8.GetBytes(string.Join('\n', records.Select(static value => JsonSerializer.Serialize(value))) + "\n")).ToArray();
    }
    private static string HashFile(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    private static StoredCursor Store(CollectedBatch batch) => new(batch.Instance.SourceInstanceId, batch.Entity.SourceEntityId,
        batch.Entity.SourcePath, batch.NextCursorJson, batch.SourceFingerprint, batch.ParserVersion, DateTimeOffset.UtcNow, null, null);
    private static async Task<(SourceInstanceDescriptor, SourceEntityDescriptor)> Probe(IAgentCollector collector, string profile)
    {
        var result = await collector.ProbeAsync(new(profile, TimeProvider.System), CancellationToken.None);
        Assert.IsEmpty(result.Diagnostics);
        return (Assert.ContainsSingle(result.Instances), Assert.ContainsSingle(result.Entities));
    }
    private static async Task<List<CollectedBatch>> Collect(IAgentCollector collector, SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity, StoredCursor? cursor = null)
    {
        var result = new List<CollectedBatch>();
        await foreach (var batch in collector.CollectAsync(new(instance, entity, cursor, CollectionReason.StartupImport), CancellationToken.None)) result.Add(batch);
        return result;
    }
}
