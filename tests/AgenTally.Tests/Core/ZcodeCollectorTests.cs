using System.IO;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Zcode;
using AgenTally.Core.Hosting;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class ZcodeCollectorTests
{
    private const long BaseTime = 1_754_012_800_000L;

    [TestMethod]
    public async Task CollectAsync_NormalizesProvenInclusiveAndSeparateRows()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string database = await CreateDatabaseAsync(zcodeHome);
        await InsertSessionAsync(
            database,
            "session-root",
            null,
            directory.Path,
            "  Root   title  ",
            "first_input");
        await InsertSessionAsync(
            database,
            "session-child",
            "session-root",
            directory.Path,
            "Child title",
            "generated");
        await InsertTurnAsync(database, "session-root", "turn-1", BaseTime, BaseTime + 90);
        await InsertUsageAsync(
            database,
            "usage-inclusive",
            "session-root",
            "turn-1",
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            input: 100,
            output: 20,
            reasoning: 5,
            cacheRead: 30,
            cacheWrite: 10,
            computedTotal: 120,
            providerTotal: 120);
        await InsertUsageAsync(
            database,
            "usage-separate",
            "session-child",
            "turn-2",
            "k3-256k",
            BaseTime + 200,
            BaseTime + 300,
            input: 60,
            output: 10,
            reasoning: 2,
            cacheRead: 20,
            cacheWrite: 0,
            computedTotal: 92,
            providerTotal: null);

        var collector = new ZcodeCollector(zcodeHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        Assert.HasCount(2, batch.Events);
        UsageEvent inclusive = batch.Events.Single(value =>
            value.Model.RawModel == "deepseek-v4-pro");
        Assert.AreEqual(100L, inclusive.Tokens.InputReported.Value);
        Assert.AreEqual(60L, inclusive.Tokens.UncachedInput.Value);
        Assert.AreEqual(30L, inclusive.Tokens.CacheRead.Value);
        Assert.AreEqual(10L, inclusive.Tokens.CacheWrite.Value);
        Assert.AreEqual(15L, inclusive.Tokens.Output.Value);
        Assert.AreEqual(5L, inclusive.Tokens.Reasoning.Value);
        Assert.AreEqual(120L, inclusive.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricInclusion.Included, inclusive.Tokens.CacheIncludedInInput);
        Assert.AreEqual(MetricInclusion.Included, inclusive.Tokens.ReasoningIncludedInOutput);
        Assert.AreEqual(SourceKind.Sqlite, inclusive.SourceKind);
        Assert.AreEqual("zcode", inclusive.AgentId);

        UsageEvent separate = batch.Events.Single(value =>
            value.Model.RawModel == "k3-256k");
        Assert.AreEqual(60L, separate.Tokens.UncachedInput.Value);
        Assert.AreEqual(20L, separate.Tokens.CacheRead.Value);
        Assert.AreEqual(10L, separate.Tokens.Output.Value);
        Assert.AreEqual(2L, separate.Tokens.Reasoning.Value);
        Assert.AreEqual(92L, separate.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricInclusion.Separate, separate.Tokens.CacheIncludedInInput);
        Assert.AreEqual(MetricInclusion.Separate, separate.Tokens.ReasoningIncludedInOutput);

        Assert.HasCount(2, batch.Sessions);
        UsageSessionMetadata root = batch.Sessions.Single(value =>
            value.SessionId == "session-root");
        Assert.AreEqual("Root title", root.SessionName);
        Assert.AreEqual(SessionRole.Main, root.SessionRole);
        Assert.AreEqual(
            CompatibilityLevel.PartiallyCompatible,
            root.CompatibilityLevel);
        UsageSessionMetadata child = batch.Sessions.Single(value =>
            value.SessionId == "session-child");
        Assert.AreEqual("session-root", child.DirectParentSessionId);
        Assert.AreEqual(SessionRelationState.Confirmed, child.RelationState);
        Assert.AreEqual(SessionRole.Unknown, child.SessionRole);
        Assert.ContainsSingle(batch.Turns);
        Assert.IsNull(Assert.ContainsSingle(batch.Turns).PromptPreview);
    }

    [TestMethod]
    public async Task CollectAsync_StoresOnlyBoundedPreviewFromExactUserMessage()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string database = await CreateDatabaseAsync(zcodeHome);
        await InsertSessionAsync(
            database,
            "session-prompt",
            null,
            directory.Path,
            "Prompt preview",
            "first_input");
        await InsertTurnAsync(
            database,
            "session-prompt",
            "turn-prompt",
            BaseTime,
            BaseTime + 90);
        await InsertMessageAsync(
            database,
            "message-1",
            "session-prompt",
            "user",
            synthetic: false,
            sequence: 0);
        await InsertPartAsync(
            database,
            "part-image",
            "message-1",
            "session-prompt",
            "file",
            text: null,
            mime: "image/png",
            synthetic: false,
            sequence: 0);
        await InsertPartAsync(
            database,
            "part-audio",
            "message-1",
            "session-prompt",
            "file",
            text: null,
            mime: "audio/wav",
            synthetic: false,
            sequence: 1);
        await InsertPartAsync(
            database,
            "part-text",
            "message-1",
            "session-prompt",
            "text",
            $"  第一行\n\t第二行 {string.Concat(Enumerable.Repeat("😀", 200))}",
            mime: null,
            synthetic: false,
            sequence: 2);
        await InsertPartAsync(
            database,
            "part-reasoning",
            "message-1",
            "session-prompt",
            "reasoning",
            "private reasoning",
            mime: null,
            synthetic: false,
            sequence: 3);
        await InsertPartAsync(
            database,
            "part-synthetic",
            "message-1",
            "session-prompt",
            "text",
            "private synthetic",
            mime: null,
            synthetic: true,
            sequence: 4);
        await InsertMessageAsync(
            database,
            "message-assistant",
            "session-prompt",
            "assistant",
            synthetic: false,
            sequence: 1);
        await InsertPartAsync(
            database,
            "part-assistant",
            "message-assistant",
            "session-prompt",
            "text",
            "private response",
            mime: null,
            synthetic: false,
            sequence: 0);
        await InsertMessageAsync(
            database,
            "message-follow-up",
            "session-prompt",
            "user",
            synthetic: false,
            sequence: 2);
        await InsertPartAsync(
            database,
            "part-follow-up",
            "message-follow-up",
            "session-prompt",
            "text",
            "private follow-up",
            mime: null,
            synthetic: false,
            sequence: 0);
        await InsertUsageAsync(
            database,
            "usage-prompt",
            "session-prompt",
            "turn-prompt",
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            input: 10,
            output: 1,
            reasoning: 0,
            cacheRead: 0,
            cacheWrite: 0,
            computedTotal: 11,
            providerTotal: null);

        var collector = new ZcodeCollector(zcodeHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        UsageTurnMetadata turn = Assert.ContainsSingle(batch.Turns);
        Assert.IsNotNull(turn.PromptPreview);
        StringAssert.StartsWith(turn.PromptPreview, "[图片] [音频] 第一行 第二行");
        Assert.AreEqual(120, turn.PromptPreview.EnumerateRunes().Count());
        Assert.AreEqual(1, turn.UserMessageCount);
        Assert.DoesNotContain("private", turn.PromptPreview);
        Assert.AreEqual("zcode-sqlite-v2", turn.ParserVersion);
    }

    [TestMethod]
    public async Task CollectAsync_KeepsExactUsageWhenPromptTablesAreUnavailable()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string database = await CreateDatabaseAsync(zcodeHome);
        await ExecuteAsync(database, "DROP TABLE part; DROP TABLE message;");
        await InsertSessionAsync(
            database,
            "session-legacy",
            null,
            directory.Path,
            "Legacy prompt schema",
            "first_input");
        await InsertTurnAsync(
            database,
            "session-legacy",
            "turn-legacy",
            BaseTime,
            BaseTime + 90);
        await InsertUsageAsync(
            database,
            "usage-legacy",
            "session-legacy",
            "turn-legacy",
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            input: 10,
            output: 1,
            reasoning: 0,
            cacheRead: 0,
            cacheWrite: 0,
            computedTotal: 11,
            providerTotal: null);

        var collector = new ZcodeCollector(zcodeHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        Assert.ContainsSingle(batch.Events);
        UsageTurnMetadata turn = Assert.ContainsSingle(batch.Turns);
        Assert.IsNull(turn.PromptPreview);
        Assert.AreEqual(1, turn.UserMessageCount);
    }

    [TestMethod]
    public async Task CollectAsync_UsesStableIncrementalCursorAndDatabaseChangeStamp()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string database = await CreateDatabaseAsync(zcodeHome);
        await InsertSessionAsync(
            database,
            "session-1",
            null,
            directory.Path,
            "Incremental",
            "first_input");
        await InsertUsageAsync(
            database,
            "usage-1",
            "session-1",
            "turn-1",
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            40,
            5,
            0,
            10,
            0,
            45,
            null);

        var collector = new ZcodeCollector(zcodeHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch first = await CollectSingleAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport));
        Assert.ContainsSingle(first.Events);
        StoredCursor cursor = ToStoredCursor(instance, entity, first);
        Assert.IsFalse(collector.HasSourceChanged(entity, cursor));

        await InsertUsageAsync(
            database,
            "usage-2",
            "session-1",
            "turn-2",
            "deepseek-v4-pro",
            BaseTime + 200,
            BaseTime + 300,
            50,
            6,
            0,
            20,
            0,
            56,
            null);
        Assert.IsTrue(collector.HasSourceChanged(entity, cursor));

        CollectedBatch second = await CollectSingleAsync(
            collector,
            new CollectionRequest(instance, entity, cursor, CollectionReason.FileChanged));
        Assert.HasCount(2, second.Events);
        Assert.IsTrue(second.Events.Any(value =>
            value.Tokens.NormalizedTotal.Value == 56));
        Assert.IsTrue(second.Events.All(value => value.SessionId == "session-1"));

        cursor = ToStoredCursor(instance, entity, second);
        CollectedBatch third = await CollectSingleAsync(
            collector,
            new CollectionRequest(instance, entity, cursor, CollectionReason.PeriodicAudit));
        Assert.IsEmpty(third.Events);
        Assert.IsFalse(collector.HasSourceChanged(entity, ToStoredCursor(instance, entity, third)));

        string walPath = $"{database}-wal";
        Assert.AreEqual(entity.SourceEntityId, collector.GetSourceEntityId(walPath));
        Assert.IsTrue(collector.IsRelevantChangePath(walPath));
        Assert.IsTrue(collector.IsWithinMonitoredRoots(instance, walPath));

        string shmPath = $"{database}-shm";
        await File.WriteAllBytesAsync(shmPath, [1, 2, 3, 4]);
        Assert.IsFalse(
            collector.HasSourceChanged(entity, ToStoredCursor(instance, entity, third)),
            "SQLite reader-lock SHM churn must not look like durable ZCode usage data.");
        Assert.IsFalse(collector.IsRelevantChangePath(shmPath));
        Assert.IsFalse(collector.IsWithinMonitoredRoots(instance, shmPath));
    }

    [TestMethod]
    public async Task CollectAsync_FailsClosedWhenTokenShapeIsNotProven()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string database = await CreateDatabaseAsync(zcodeHome);
        await InsertSessionAsync(
            database,
            "session-1",
            null,
            directory.Path,
            "Invalid shape",
            "first_input");
        await InsertUsageAsync(
            database,
            "usage-invalid",
            "session-1",
            null,
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            input: 100,
            output: 20,
            reasoning: 5,
            cacheRead: 30,
            cacheWrite: 0,
            computedTotal: 999,
            providerTotal: null);

        var collector = new ZcodeCollector(zcodeHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CollectSingleAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport)));
    }

    [TestMethod]
    public async Task CollectAsync_ReplaysLastMillisecondAfterDatabaseChange()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string database = await CreateDatabaseAsync(zcodeHome);
        await InsertSessionAsync(
            database,
            "session-tie",
            null,
            directory.Path,
            "Same millisecond",
            "first_input");
        await InsertUsageAsync(
            database,
            "usage-z",
            "session-tie",
            null,
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            10,
            1,
            0,
            0,
            0,
            11,
            null);

        var collector = new ZcodeCollector(zcodeHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch first = await CollectSingleAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport));
        StoredCursor cursor = ToStoredCursor(instance, entity, first);

        await InsertUsageAsync(
            database,
            "usage-a",
            "session-tie",
            null,
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            20,
            2,
            0,
            0,
            0,
            22,
            null);
        CollectedBatch second = await CollectSingleAsync(
            collector,
            new CollectionRequest(instance, entity, cursor, CollectionReason.FileChanged));

        Assert.HasCount(2, second.Events);
        Assert.IsTrue(second.Events.Any(value =>
            value.Tokens.NormalizedTotal.Value == 22));
        Assert.AreEqual(2, second.Events.Select(value => value.DedupKey).Distinct().Count());
    }

    [TestMethod]
    public async Task CollectAsync_BoundsLargeLedgerAndContinuesFromCommittedCursor()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string database = await CreateDatabaseAsync(zcodeHome);
        await InsertSessionAsync(
            database,
            "session-large",
            null,
            directory.Path,
            "Large ledger",
            "first_input");
        await ExecuteAsync(
            database,
            """
            WITH RECURSIVE rows(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM rows WHERE value < 201
            )
            INSERT INTO model_usage (
                id, session_id, turn_id, model_id, status, started_at, completed_at,
                duration_ms, input_tokens, output_tokens, reasoning_tokens,
                cache_read_input_tokens, cache_creation_input_tokens,
                provider_total_tokens, computed_total_tokens)
            SELECT
                printf('usage-%03d', value), 'session-large', NULL,
                'deepseek-v4-pro', 'completed', $started + value,
                $completed + value, 100, 1, 1, 0, 0, 0, NULL, 2
            FROM rows;
            """,
            ("$started", BaseTime),
            ("$completed", BaseTime + 1_000));

        var collector = new ZcodeCollector(zcodeHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch first = await CollectSingleAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport));
        Assert.HasCount(200, first.Events);
        Assert.IsTrue(first.Diagnostics.Any(value =>
            value.Code == "collector.batch_limit_reached"));

        CollectedBatch second = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                ToStoredCursor(instance, entity, first),
                CollectionReason.ManualRequest));
        Assert.ContainsSingle(second.Events);
        Assert.IsFalse(second.Diagnostics.Any(value =>
            value.Code == "collector.batch_limit_reached"));
    }

    [TestMethod]
    public async Task CoreHost_OnceRegistersZcodeAndPersistsExactTotals()
    {
        using var directory = new TestTempDirectory();
        string zcodeHome = directory.File(".zcode");
        string zcodeDatabase = await CreateDatabaseAsync(zcodeHome);
        await InsertSessionAsync(
            zcodeDatabase,
            "session-host",
            null,
            directory.Path,
            "Host integration",
            "first_input");
        await InsertUsageAsync(
            zcodeDatabase,
            "usage-host",
            "session-host",
            "turn-host",
            "deepseek-v4-pro",
            BaseTime,
            BaseTime + 100,
            input: 100,
            output: 20,
            reasoning: 5,
            cacheRead: 30,
            cacheWrite: 10,
            computedTotal: 120,
            providerTotal: 120);
        string database = directory.File("agentally.db");
        string isolatedHome = directory.File("isolated");
        string codexHome = Path.Combine(isolatedHome, ".codex");

        int exitCode = await new CoreHost(new StorageOptions(database)).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--claude-home", Path.Combine(isolatedHome, ".claude"),
            "--kimi-home", Path.Combine(isolatedHome, ".kimi-code"),
            "--zcode-home", zcodeHome,
            "--database", database
        ]);

        var queries = new SqliteUsageQueryService(
            new SqliteConnectionFactory(new StorageOptions(database)));
        UsageOverview overview = await queries.GetOverviewAsync(
            new UsageFilter(
                DateTimeOffset.FromUnixTimeMilliseconds(BaseTime - 1),
                DateTimeOffset.FromUnixTimeMilliseconds(BaseTime + 1_000),
                agentId: "zcode"),
            CancellationToken.None);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1L, overview.RequestCount);
        Assert.AreEqual(60L, overview.UncachedInput.Value);
        Assert.AreEqual(30L, overview.CacheRead.Value);
        Assert.AreEqual(10L, overview.CacheWrite.Value);
        Assert.AreEqual(15L, overview.Output.Value);
        Assert.AreEqual(120L, overview.NormalizedTotal.Value);
    }

    private static async Task<string> CreateDatabaseAsync(string zcodeHome)
    {
        string database = Path.Combine(zcodeHome, "cli", "db", "db.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await ExecuteAsync(database, """
            CREATE TABLE session (
                id TEXT PRIMARY KEY,
                parent_id TEXT,
                directory TEXT NOT NULL,
                path TEXT,
                title TEXT NOT NULL,
                time_updated INTEGER NOT NULL,
                task_type TEXT NOT NULL
            );
            CREATE TABLE turn_usage (
                session_id TEXT NOT NULL,
                turn_id TEXT NOT NULL,
                started_at INTEGER,
                completed_at INTEGER,
                user_message_id TEXT,
                PRIMARY KEY (session_id, turn_id)
            );
            CREATE TABLE message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL,
                sequence INTEGER
            );
            CREATE TABLE part (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL,
                data TEXT NOT NULL,
                sequence INTEGER
            );
            CREATE TABLE model_usage (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                turn_id TEXT,
                model_id TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at INTEGER NOT NULL,
                completed_at INTEGER,
                duration_ms INTEGER,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                reasoning_tokens INTEGER NOT NULL,
                cache_read_input_tokens INTEGER NOT NULL,
                cache_creation_input_tokens INTEGER NOT NULL,
                provider_total_tokens INTEGER,
                computed_total_tokens INTEGER NOT NULL
            );
            """);
        return database;
    }

    private static Task InsertSessionAsync(
        string database,
        string id,
        string? parentId,
        string sourceDirectory,
        string title,
        string taskType) => ExecuteAsync(
            database,
            """
            INSERT INTO session (
                id, parent_id, directory, path, title, time_updated, task_type)
            VALUES ($id, $parent, $directory, NULL, $title, $updated, $task_type);
            """,
            ("$id", id),
            ("$parent", parentId),
            ("$directory", sourceDirectory),
            ("$title", title),
            ("$updated", BaseTime),
            ("$task_type", taskType));

    private static Task InsertTurnAsync(
        string database,
        string sessionId,
        string turnId,
        long startedAt,
        long completedAt) => ExecuteAsync(
            database,
            """
            INSERT INTO turn_usage (
                session_id, turn_id, started_at, completed_at, user_message_id)
            VALUES ($session, $turn, $started, $completed, 'message-1');
            """,
            ("$session", sessionId),
            ("$turn", turnId),
            ("$started", startedAt),
            ("$completed", completedAt));

    private static Task InsertMessageAsync(
        string database,
        string id,
        string sessionId,
        string role,
        bool synthetic,
        int sequence) => ExecuteAsync(
            database,
            """
            INSERT INTO message (
                id, session_id, time_created, time_updated, data, sequence)
            VALUES (
                $id, $session, $created, $created,
                json_object('role', $role, 'synthetic', $synthetic), $sequence);
            """,
            ("$id", id),
            ("$session", sessionId),
            ("$created", BaseTime + sequence),
            ("$role", role),
            ("$synthetic", synthetic ? 1 : 0),
            ("$sequence", sequence));

    private static Task InsertPartAsync(
        string database,
        string id,
        string messageId,
        string sessionId,
        string type,
        string? text,
        string? mime,
        bool synthetic,
        int sequence) => ExecuteAsync(
            database,
            """
            INSERT INTO part (
                id, message_id, session_id, time_created, time_updated, data, sequence)
            VALUES (
                $id, $message, $session, $created, $created,
                json_object(
                    'type', $type,
                    'text', $text,
                    'mime', $mime,
                    'synthetic', $synthetic),
                $sequence);
            """,
            ("$id", id),
            ("$message", messageId),
            ("$session", sessionId),
            ("$created", BaseTime + sequence),
            ("$type", type),
            ("$text", text),
            ("$mime", mime),
            ("$synthetic", synthetic ? 1 : 0),
            ("$sequence", sequence));

    private static Task InsertUsageAsync(
        string database,
        string id,
        string sessionId,
        string? turnId,
        string model,
        long startedAt,
        long completedAt,
        long input,
        long output,
        long reasoning,
        long cacheRead,
        long cacheWrite,
        long computedTotal,
        long? providerTotal) => ExecuteAsync(
            database,
            """
            INSERT INTO model_usage (
                id, session_id, turn_id, model_id, status, started_at, completed_at,
                duration_ms, input_tokens, output_tokens, reasoning_tokens,
                cache_read_input_tokens, cache_creation_input_tokens,
                provider_total_tokens, computed_total_tokens)
            VALUES (
                $id, $session, $turn, $model, 'completed', $started, $completed,
                $duration, $input, $output, $reasoning, $cache_read, $cache_write,
                $provider_total, $computed_total);
            """,
            ("$id", id),
            ("$session", sessionId),
            ("$turn", turnId),
            ("$model", model),
            ("$started", startedAt),
            ("$completed", completedAt),
            ("$duration", completedAt - startedAt),
            ("$input", input),
            ("$output", output),
            ("$reasoning", reasoning),
            ("$cache_read", cacheRead),
            ("$cache_write", cacheWrite),
            ("$provider_total", providerTotal),
            ("$computed_total", computedTotal));

    private static async Task ExecuteAsync(
        string database,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(SourceInstanceDescriptor, SourceEntityDescriptor)>
        ProbeSingleAsync(ZcodeCollector collector, string userProfile)
    {
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(userProfile, TimeProvider.System),
            CancellationToken.None);
        Assert.IsEmpty(probe.Diagnostics);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        Assert.AreEqual("ZCode (Windows)", instance.DisplayName);
        return (instance, entity);
    }

    private static async Task<CollectedBatch> CollectSingleAsync(
        ZcodeCollector collector,
        CollectionRequest request)
    {
        var batches = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(
                           request,
                           CancellationToken.None))
        {
            batches.Add(batch);
        }

        return Assert.ContainsSingle(batches);
    }

    private static StoredCursor ToStoredCursor(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        CollectedBatch batch) => new(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            batch.NextCursorJson,
            batch.SourceFingerprint,
            batch.ParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);
}
