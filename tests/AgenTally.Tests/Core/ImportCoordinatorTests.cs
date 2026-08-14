using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Processing;
using AgenTally.Domain.Sources;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class ImportCoordinatorTests
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    public async Task SyncAsync_ImportsIncrementallyAcrossRestartAndArchiveMove()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        const string fileName = "rollout-2026-07-16T01-00-thread-1.jsonl";
        string active = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            fileName);
        await WriteAsync(active, File.ReadAllText(Path.Combine(
            FixtureDirectory,
            "basic-rollout.jsonl")));
        string databasePath = directory.File("agentally.db");
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateStorageAsync(databasePath);
        var collector = new CodexCollector(codexHome);
        var coordinator = new ImportCoordinator(writer);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        SyncResult first = await SyncAsync(
            coordinator,
            collector,
            writer,
            instance,
            entity);
        SyncResult repeated = await SyncAsync(
            coordinator,
            collector,
            writer,
            instance,
            entity);
        await File.AppendAllTextAsync(
            active,
            "{\"timestamp\":\"2026-07-16T01:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":5,\"output_tokens\":4,\"reasoning_output_tokens\":2,\"total_tokens\":14},\"total_token_usage\":{\"input_tokens\":140,\"cached_input_tokens\":75,\"output_tokens\":32,\"reasoning_output_tokens\":9,\"total_tokens\":172}}}}\n",
            Utf8WithoutBom);
        SyncResult appended = await SyncAsync(
            coordinator,
            collector,
            writer,
            instance,
            entity);

        (SqliteUsageWriter restartedWriter, SqliteUsageQueryService restartedQueries) =
            await CreateStorageAsync(databasePath);
        var restartedCollector = new CodexCollector(codexHome);
        var restartedCoordinator = new ImportCoordinator(restartedWriter);
        (SourceInstanceDescriptor restartedInstance, SourceEntityDescriptor restartedEntity) =
            await ProbeSingleAsync(restartedCollector, directory.Path);
        SyncResult restarted = await SyncAsync(
            restartedCoordinator,
            restartedCollector,
            restartedWriter,
            restartedInstance,
            restartedEntity);

        string archived = Path.Combine(codexHome, "archived_sessions", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.Move(active, archived);
        (SourceInstanceDescriptor archivedInstance, SourceEntityDescriptor archivedEntity) =
            await ProbeSingleAsync(restartedCollector, directory.Path);
        SyncResult archivedRun = await SyncAsync(
            restartedCoordinator,
            restartedCollector,
            restartedWriter,
            archivedInstance,
            archivedEntity);
        UsageOverview overview = await restartedQueries.GetOverviewAsync(
            AllDay(),
            CancellationToken.None);
        StoredCursor archivedCursor = (await restartedWriter.GetCursorAsync(
            archivedInstance.SourceInstanceId,
            archivedEntity.SourceEntityId,
            CancellationToken.None))!;

        Assert.IsTrue(first.Succeeded);
        Assert.IsTrue(repeated.Succeeded);
        Assert.IsTrue(appended.Succeeded);
        Assert.IsTrue(restarted.Succeeded);
        Assert.IsTrue(archivedRun.Succeeded);
        Assert.AreEqual(2, first.AppliedCount);
        Assert.AreEqual(0, repeated.AppliedCount);
        Assert.AreEqual(1, appended.AppliedCount);
        Assert.AreEqual(0, restarted.AppliedCount);
        Assert.AreEqual(0, archivedRun.AppliedCount);
        Assert.AreEqual(3L, overview.RequestCount);
        Assert.AreEqual(
            entity.SourceEntityId,
            archivedEntity.SourceEntityId);
        Assert.AreEqual(Path.GetFullPath(archived), archivedCursor.SourcePath);
        Assert.AreEqual(
            3L,
            (await queries.GetOverviewAsync(AllDay(), CancellationToken.None)).RequestCount);
    }

    [TestMethod]
    public async Task SyncAsync_ClonedCallAcrossChildRolloutsCountsOnceAndKeepsUniqueCallsAfterRestart()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string sessions = Path.Combine(codexHome, "sessions", "2026", "07", "16");
        string firstPath = Path.Combine(
            sessions,
            "rollout-2026-07-16T02-00-child-a.jsonl");
        string secondPath = Path.Combine(
            sessions,
            "rollout-2026-07-16T02-01-child-b.jsonl");
        await WriteAsync(firstPath, ChildRollout("unique-turn-a", "02:00", 3, 1, 13, 3));
        await WriteAsync(secondPath, ChildRollout("unique-turn-b", "02:01", 4, 1, 14, 3));
        string databasePath = directory.File("shared-thread.db");
        (SqliteUsageWriter writer, _) = await CreateStorageAsync(databasePath);
        var collector = new CodexCollector(codexHome);
        var coordinator = new ImportCoordinator(writer);
        SourceProbeResult initialProbe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        var firstResults = new List<SyncResult>();
        foreach (SourceEntityDescriptor entity in initialProbe.Entities)
        {
            SourceInstanceDescriptor instance = Assert.ContainsSingle(
                initialProbe.Instances.Where(value =>
                    value.SourceInstanceId == entity.SourceInstanceId));
            firstResults.Add(await SyncAsync(
                coordinator,
                collector,
                writer,
                instance,
                entity));
        }

        var repeatedResults = new List<SyncResult>();
        foreach (SourceEntityDescriptor entity in initialProbe.Entities)
        {
            SourceInstanceDescriptor instance = Assert.ContainsSingle(
                initialProbe.Instances.Where(value =>
                    value.SourceInstanceId == entity.SourceInstanceId));
            repeatedResults.Add(await SyncAsync(
                coordinator,
                collector,
                writer,
                instance,
                entity));
        }

        (SqliteUsageWriter restartedWriter, SqliteUsageQueryService restartedQueries) =
            await CreateStorageAsync(databasePath);
        var restartedCollector = new CodexCollector(codexHome);
        var restartedCoordinator = new ImportCoordinator(restartedWriter);
        SourceProbeResult restartedProbe = await restartedCollector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        var restartedResults = new List<SyncResult>();
        foreach (SourceEntityDescriptor entity in restartedProbe.Entities)
        {
            SourceInstanceDescriptor instance = Assert.ContainsSingle(
                restartedProbe.Instances.Where(value =>
                    value.SourceInstanceId == entity.SourceInstanceId));
            restartedResults.Add(await SyncAsync(
                restartedCoordinator,
                restartedCollector,
                restartedWriter,
                instance,
                entity));
        }

        string archivedFirst = Path.Combine(
            codexHome,
            "archived_sessions",
            Path.GetFileName(firstPath));
        Directory.CreateDirectory(Path.GetDirectoryName(archivedFirst)!);
        File.Move(firstPath, archivedFirst);
        SourceProbeResult archivedProbe = await restartedCollector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        var archivedResults = new List<SyncResult>();
        foreach (SourceEntityDescriptor entity in archivedProbe.Entities)
        {
            SourceInstanceDescriptor instance = Assert.ContainsSingle(
                archivedProbe.Instances.Where(value =>
                    value.SourceInstanceId == entity.SourceInstanceId));
            archivedResults.Add(await SyncAsync(
                restartedCoordinator,
                restartedCollector,
                restartedWriter,
                instance,
                entity));
        }

        UsageOverview overview = await restartedQueries.GetOverviewAsync(
            AllDay(),
            CancellationToken.None);
        Assert.AreEqual(2, initialProbe.Entities.Count);
        Assert.AreEqual(3, firstResults.Sum(result => result.AppliedCount));
        Assert.AreEqual(1, firstResults.Sum(result => result.IgnoredCount));
        Assert.AreEqual(0, repeatedResults.Sum(result => result.AppliedCount));
        Assert.AreEqual(0, restartedResults.Sum(result => result.AppliedCount));
        Assert.AreEqual(0, archivedResults.Sum(result => result.AppliedCount));
        Assert.AreEqual(3L, overview.RequestCount);
        Assert.AreEqual(21L, overview.NormalizedTotal.Value);
        Assert.AreEqual(
            CodexSourceIdentity.EntityId(firstPath),
            CodexSourceIdentity.EntityId(archivedFirst));
    }

    [TestMethod]
    public async Task SyncAsync_IoFailureRecordsOnlyCurrentEntityAndPreservesItsCursor()
    {
        using var directory = new TestTempDirectory();
        string databasePath = directory.File("agentally.db");
        (SqliteUsageWriter writer, _) = await CreateStorageAsync(databasePath);
        SourceInstanceDescriptor instance = Instance(directory.Path);
        SourceEntityDescriptor failedEntity = Entity(instance, "failed", directory.File("failed.jsonl"));
        SourceEntityDescriptor healthyEntity = Entity(instance, "healthy", directory.File("healthy.jsonl"));
        await SeedCursorAsync(writer, instance, failedEntity, "failed-seed");
        await SeedCursorAsync(writer, instance, healthyEntity, "healthy-seed");
        StoredCursor failedCursor = (await writer.GetCursorAsync(
            instance.SourceInstanceId,
            failedEntity.SourceEntityId,
            CancellationToken.None))!;
        var request = new CollectionRequest(
            instance,
            failedEntity,
            failedCursor,
            CollectionReason.ManualRequest);
        var coordinator = new ImportCoordinator(writer);

        SyncResult result = await coordinator.SyncAsync(
            new IoFailingCollector(),
            request,
            CancellationToken.None);

        StoredCursor preserved = (await writer.GetCursorAsync(
            instance.SourceInstanceId,
            failedEntity.SourceEntityId,
            CancellationToken.None))!;
        StoredCursor untouched = (await writer.GetCursorAsync(
            instance.SourceInstanceId,
            healthyEntity.SourceEntityId,
            CancellationToken.None))!;
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, result.AppliedCount);
        Assert.AreEqual("failed-seed", preserved.CursorJson);
        Assert.AreEqual("Source collection failed (IOException).", preserved.LastError);
        Assert.AreEqual("healthy-seed", untouched.CursorJson);
        Assert.IsNull(untouched.LastError);
    }

    [TestMethod]
    public async Task SyncAsync_TruncatedEmptySourcePersistsAResetCursorAndDiagnostic()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(codexHome, "sessions", "rollout-truncated.jsonl");
        await WriteAsync(path, File.ReadAllText(Path.Combine(
            FixtureDirectory,
            "basic-rollout.jsonl")));
        (SqliteUsageWriter writer, _) = await CreateStorageAsync(directory.File("agentally.db"));
        var collector = new CodexCollector(codexHome);
        var coordinator = new ImportCoordinator(writer);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        SyncResult first = await SyncAsync(
            coordinator,
            collector,
            writer,
            instance,
            entity);
        StoredCursor before = (await writer.GetCursorAsync(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            CancellationToken.None))!;
        await File.WriteAllTextAsync(path, string.Empty, Utf8WithoutBom);

        SyncResult reset = await SyncAsync(
            coordinator,
            collector,
            writer,
            instance,
            entity);

        StoredCursor after = (await writer.GetCursorAsync(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            CancellationToken.None))!;
        CodexCursor resetCursor = CodexCursor.DeserializeOrStart(after.CursorJson, out _);
        Assert.AreEqual(2, first.AppliedCount);
        Assert.IsTrue(reset.Succeeded);
        Assert.AreEqual(0, reset.AppliedCount);
        Assert.AreEqual(CodexCursor.Start, resetCursor);
        Assert.AreEqual(before.SourceFingerprint, after.SourceFingerprint);
        Assert.AreEqual(
            "jsonl.source_reset",
            Assert.ContainsSingle(reset.Diagnostics).Code);
    }

    [TestMethod]
    public async Task SyncAsync_StorageCommitIoFailurePropagatesWithoutRecordingSourceFailure()
    {
        SourceInstanceDescriptor instance = Instance(@"C:\fixture");
        SourceEntityDescriptor entity = Entity(instance, "commit", @"C:\fixture\commit.jsonl");
        var request = new CollectionRequest(
            instance,
            entity,
            null,
            CollectionReason.ManualRequest);
        var writer = new ThrowingCommitWriter();
        var coordinator = new ImportCoordinator(writer);

        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(
            async () => await coordinator.SyncAsync(
                new SingleBatchCollector(),
                request,
                CancellationToken.None));

        Assert.AreEqual("private storage failure", exception.Message);
        Assert.AreEqual(0, writer.RecordFailureCalls);
    }

    [TestMethod]
    public async Task SyncAsync_RejectsBatchWhoseIdentityDoesNotMatchRequest()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _) = await CreateStorageAsync(directory.File("agentally.db"));
        SourceInstanceDescriptor instance = Instance(directory.Path);
        SourceEntityDescriptor requested = Entity(instance, "requested", directory.File("requested.jsonl"));
        SourceEntityDescriptor foreign = Entity(instance, "foreign", directory.File("foreign.jsonl"));
        var coordinator = new ImportCoordinator(writer);
        var request = new CollectionRequest(
            instance,
            requested,
            null,
            CollectionReason.ManualRequest);

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await coordinator.SyncAsync(
                    new ForeignBatchCollector(foreign),
                    request,
                    CancellationToken.None));

        Assert.AreEqual(
            "Collector returned a batch that does not match its request.",
            exception.Message);
        Assert.IsNull(await writer.GetCursorAsync(
            instance.SourceInstanceId,
            requested.SourceEntityId,
            CancellationToken.None));
        Assert.IsNull(await writer.GetCursorAsync(
            instance.SourceInstanceId,
            foreign.SourceEntityId,
            CancellationToken.None));
    }

    [TestMethod]
    public async Task SyncAsync_RejectsMoreThanTwentyFiveBufferedBatchesBeforeWriting()
    {
        var writer = new CountingWriter();
        SourceInstanceDescriptor instance = Instance(@"C:\fixture");
        SourceEntityDescriptor entity = Entity(instance, "bounded", @"C:\fixture\bounded.jsonl");
        var request = new CollectionRequest(
            instance,
            entity,
            null,
            CollectionReason.ManualRequest);
        var coordinator = new ImportCoordinator(writer);

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await coordinator.SyncAsync(
                    new ManyBatchCollector(26),
                    request,
                    CancellationToken.None));

        Assert.AreEqual(
            "Collector exceeded the bounded import buffer.",
            exception.Message);
        Assert.AreEqual(0, writer.CommitCalls);
    }

    [TestMethod]
    public async Task SyncAsync_SkipsHealthyEmptyBatchWhenCursorIsUnchanged()
    {
        var writer = new CountingWriter();
        SourceInstanceDescriptor instance = Instance(@"C:\fixture");
        SourceEntityDescriptor entity = Entity(
            instance,
            "unchanged",
            @"C:\fixture\unchanged.jsonl");
        var cursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            "next-cursor",
            new string('a', 64),
            CodexRolloutParser.CurrentParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);
        var coordinator = new ImportCoordinator(writer);

        SyncResult result = await coordinator.SyncAsync(
            new SingleBatchCollector(),
            new CollectionRequest(
                instance,
                entity,
                cursor,
                CollectionReason.PeriodicAudit),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, result.AppliedCount);
        Assert.AreEqual(0, result.IgnoredCount);
        Assert.AreEqual(0, writer.CommitCalls);
    }

    [TestMethod]
    public async Task SyncAsync_CommitsEmptyBatchWhenCursorAdvances()
    {
        var writer = new CountingWriter();
        SourceInstanceDescriptor instance = Instance(@"C:\fixture");
        SourceEntityDescriptor entity = Entity(
            instance,
            "advanced",
            @"C:\fixture\advanced.jsonl");
        var cursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            "previous-cursor",
            new string('a', 64),
            CodexRolloutParser.CurrentParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);
        var coordinator = new ImportCoordinator(writer);

        await coordinator.SyncAsync(
            new SingleBatchCollector(),
            new CollectionRequest(
                instance,
                entity,
                cursor,
                CollectionReason.FileChanged),
            CancellationToken.None);

        Assert.AreEqual(1, writer.CommitCalls);
    }

    [TestMethod]
    public async Task SyncAsync_CommitsUnchangedEmptyBatchToClearStoredFailure()
    {
        var writer = new CountingWriter();
        SourceInstanceDescriptor instance = Instance(@"C:\fixture");
        SourceEntityDescriptor entity = Entity(
            instance,
            "recovered",
            @"C:\fixture\recovered.jsonl");
        var cursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            "next-cursor",
            new string('a', 64),
            CodexRolloutParser.CurrentParserVersion,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "Source collection failed (IOException).",
            DateTimeOffset.UtcNow.AddSeconds(-30));
        var coordinator = new ImportCoordinator(writer);

        await coordinator.SyncAsync(
            new SingleBatchCollector(),
            new CollectionRequest(
                instance,
                entity,
                cursor,
                CollectionReason.FileChanged),
            CancellationToken.None);

        Assert.AreEqual(1, writer.CommitCalls);
    }

    [TestMethod]
    public async Task SyncAsync_RejectsReparsePointFileWithoutImportingExternalContentWhenSupported()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string outside = directory.File("outside-rollout.jsonl");
        await WriteAsync(outside, File.ReadAllText(Path.Combine(
            FixtureDirectory,
            "basic-rollout.jsonl")));
        string linked = Path.Combine(codexHome, "sessions", "rollout-linked.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        try
        {
            File.CreateSymbolicLink(linked, outside);
        }
        catch (Exception exception)
            when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateStorageAsync(directory.File("agentally.db"));
        var collector = new CodexCollector(codexHome);
        var coordinator = new ImportCoordinator(writer);
        var instance = new SourceInstanceDescriptor(
            CodexSourceIdentity.InstanceId(codexHome),
            "codex",
            SourceKind.Jsonl,
            "Codex (Windows)",
            CodexSourceIdentity.NormalizePath(codexHome));
        var entity = new SourceEntityDescriptor(
            instance.SourceInstanceId,
            CodexSourceIdentity.EntityId(linked),
            linked);

        SyncResult result = await coordinator.SyncAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.ManualRequest),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Source collection failed (IOException).", result.Error);
        Assert.AreEqual(
            0L,
            (await queries.GetOverviewAsync(AllDay(), CancellationToken.None)).RequestCount);
    }

    private static async Task<SyncResult> SyncAsync(
        ImportCoordinator coordinator,
        CodexCollector collector,
        IUsageWriter writer,
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity)
    {
        StoredCursor? cursor = await writer.GetCursorAsync(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            CancellationToken.None);
        return await coordinator.SyncAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                cursor,
                CollectionReason.ManualRequest),
            CancellationToken.None);
    }

    private static async Task<(
        SourceInstanceDescriptor Instance,
        SourceEntityDescriptor Entity)> ProbeSingleAsync(
            CodexCollector collector,
            string userProfilePath)
    {
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(userProfilePath, TimeProvider.System),
            CancellationToken.None);
        return (
            Assert.ContainsSingle(probe.Instances),
            Assert.ContainsSingle(probe.Entities));
    }

    private static async Task<(
        SqliteUsageWriter Writer,
        SqliteUsageQueryService Queries)> CreateStorageAsync(string databasePath)
    {
        var connections = new SqliteConnectionFactory(new StorageOptions(databasePath));
        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);
        return (writer, new SqliteUsageQueryService(connections));
    }

    private static async Task SeedCursorAsync(
        IUsageWriter writer,
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        string cursorJson)
    {
        await writer.CommitAsync(
            new UsageEventBatch(
                instance,
                entity,
                cursorJson,
                "fixture-fingerprint",
                CodexRolloutParser.CurrentParserVersion,
                new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero),
                []),
            CancellationToken.None);
    }

    private static SourceInstanceDescriptor Instance(string rootPath) => new(
        "codex:windows:test",
        "codex",
        SourceKind.Jsonl,
        "Codex test",
        rootPath);

    private static SourceEntityDescriptor Entity(
        SourceInstanceDescriptor instance,
        string suffix,
        string path) => new(
        instance.SourceInstanceId,
        $"codex:rollout:{suffix}",
        path);

    private static UsageFilter AllDay() => new(
        new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));

    private static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Utf8WithoutBom);
    }

    private static string ChildRollout(
        string uniqueTurnId,
        string minute,
        long uniqueInput,
        long uniqueOutput,
        long cumulativeInput,
        long cumulativeOutput)
    {
        string[] lines =
        [
            JsonSerializer.Serialize(new
            {
                timestamp = $"2026-07-16T{minute}:00Z",
                type = "session_meta",
                payload = new
                {
                    id = "shared-child-thread",
                    forked_from_id = "shared-parent-thread",
                    source = new
                    {
                        subagent = new { parent_thread_id = "shared-parent-thread" }
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = $"2026-07-16T{minute}:01Z",
                type = "event_msg",
                payload = new { type = "thread_settings_applied" }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = $"2026-07-16T{minute}:02Z",
                type = "turn_context",
                turn_id = "shared-cloned-turn",
                payload = new
                {
                    model = "gpt-test",
                    effort = "high"
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = $"2026-07-16T{minute}:03Z",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new
                        {
                            input_tokens = 10,
                            cached_input_tokens = 2,
                            output_tokens = 2,
                            total_tokens = 12
                        },
                        total_token_usage = new
                        {
                            input_tokens = 10,
                            cached_input_tokens = 2,
                            output_tokens = 2,
                            total_tokens = 12
                        }
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = $"2026-07-16T{minute}:04Z",
                type = "turn_context",
                turn_id = uniqueTurnId,
                payload = new
                {
                    model = "gpt-test",
                    effort = "high"
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = $"2026-07-16T{minute}:05Z",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        last_token_usage = new
                        {
                            input_tokens = uniqueInput,
                            cached_input_tokens = 0,
                            output_tokens = uniqueOutput,
                            total_tokens = uniqueInput + uniqueOutput
                        },
                        total_token_usage = new
                        {
                            input_tokens = cumulativeInput,
                            cached_input_tokens = 2,
                            output_tokens = cumulativeOutput,
                            total_tokens = cumulativeInput + cumulativeOutput
                        }
                    }
                }
            })
        ];
        return string.Join('\n', lines) + "\n";
    }

    private static string FixtureDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "Fixtures",
        "Codex"));

    private sealed class IoFailingCollector : IAgentCollector
    {
        public string AgentId => "codex";

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return Batch(request.Instance, request.Entity);
            await Task.Yield();
            throw new IOException("private failure details");
        }
    }

    private sealed class SingleBatchCollector : IAgentCollector
    {
        public string AgentId => "codex";

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return Batch(request.Instance, request.Entity);
        }
    }

    private sealed class ForeignBatchCollector : IAgentCollector
    {
        private readonly SourceEntityDescriptor _foreign;

        public ForeignBatchCollector(SourceEntityDescriptor foreign)
        {
            _foreign = foreign;
        }

        public string AgentId => "codex";

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return Batch(request.Instance, _foreign);
        }
    }

    private sealed class ManyBatchCollector : IAgentCollector
    {
        private readonly int _count;

        public ManyBatchCollector(int count)
        {
            _count = count;
        }

        public string AgentId => "codex";

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int index = 0; index < _count; index++)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return Batch(request.Instance, request.Entity);
            }
        }
    }

    private sealed class ThrowingCommitWriter : IUsageWriter
    {
        public int RecordFailureCalls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoredCursor?> GetCursorAsync(
            string sourceInstanceId,
            string sourceEntityId,
            CancellationToken cancellationToken) => Task.FromResult<StoredCursor?>(null);

        public Task<WriteResult> CommitAsync(
            UsageEventBatch batch,
            CancellationToken cancellationToken) =>
            Task.FromException<WriteResult>(new IOException("private storage failure"));

        public Task RecordFailureAsync(
            SourceInstanceDescriptor instance,
            SourceEntityDescriptor entity,
            string error,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            RecordFailureCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingWriter : IUsageWriter
    {
        public int CommitCalls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoredCursor?> GetCursorAsync(
            string sourceInstanceId,
            string sourceEntityId,
            CancellationToken cancellationToken) => Task.FromResult<StoredCursor?>(null);

        public Task<WriteResult> CommitAsync(
            UsageEventBatch batch,
            CancellationToken cancellationToken)
        {
            CommitCalls++;
            return Task.FromResult(new WriteResult(0, 0));
        }

        public Task RecordFailureAsync(
            SourceInstanceDescriptor instance,
            SourceEntityDescriptor entity,
            string error,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static CollectedBatch Batch(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity) => new(
        instance,
        entity,
        [],
        "next-cursor",
        new string('a', 64),
        CodexRolloutParser.CurrentParserVersion,
        []);
}
