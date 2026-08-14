using System.IO;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Storage;

[TestClass]
public sealed class SqliteUsageWriterTests
{
    [TestMethod]
    public async Task Commit_UpdatesPartialWithHigherCompletedRevision()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries, _) =
            await CreateServicesAsync(directory);

        await writer.CommitAsync(
            Batch(TestEvents.Create(
                completionState: CompletionState.Partial,
                sourceRevision: 1)),
            CancellationToken.None);

        WriteResult second = await writer.CommitAsync(
            Batch(TestEvents.Create(
                completionState: CompletionState.Completed,
                sourceRevision: 2,
                normalizedTotal: 180)),
            CancellationToken.None);

        Assert.AreEqual(1, second.AppliedCount);
        Assert.AreEqual(
            180L,
            (await queries.GetOverviewAsync(AllDay(), CancellationToken.None))
                .NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task Commit_DoesNotReplaceFinalizedWithNewerPartial()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries, _) =
            await CreateServicesAsync(directory);

        await writer.CommitAsync(
            Batch(TestEvents.Create(
                completionState: CompletionState.Finalized,
                sourceRevision: 4,
                normalizedTotal: 200)),
            CancellationToken.None);

        WriteResult second = await writer.CommitAsync(
            Batch(TestEvents.Create(
                completionState: CompletionState.Partial,
                sourceRevision: 5,
                normalizedTotal: 20)),
            CancellationToken.None);

        Assert.AreEqual(0, second.AppliedCount);
        Assert.AreEqual(1, second.IgnoredCount);
        Assert.AreEqual(
            200L,
            (await queries.GetOverviewAsync(AllDay(), CancellationToken.None))
                .NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task Commit_PersistsEventAndEntityCursorAtomically()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, _) = await CreateServicesAsync(directory);

        await writer.CommitAsync(Batch(TestEvents.Create()), CancellationToken.None);
        StoredCursor? cursor = await writer.GetCursorAsync(
            "codex:windows:test",
            "rollout:test",
            CancellationToken.None);

        Assert.IsNotNull(cursor);
        Assert.AreEqual("cursor-1", cursor.CursorJson);
        Assert.AreEqual("fixture-1", cursor.SourceFingerprint);
        Assert.AreEqual("codex-v1", cursor.ParserVersion);
    }

    [TestMethod]
    public async Task Commit_PersistsTraceableModelIdentity()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        UsageEvent value = TestEvents.Create(
            model: new ModelIdentity
            {
                RawModel = "minimax-m3-play",
                NormalizedModel = "minimax-m3",
                RouteModelId = "minimax-m3-play",
                DisplayName = "MiniMax-M3",
                ResolutionOrigin = ModelResolutionOrigin.ExactAlias
            });

        await writer.CommitAsync(Batch(value), CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                raw_model,
                normalized_model,
                route_model_id,
                model_display_name,
                model_resolution_origin
            FROM usage_events
            WHERE event_id = 'event-1';
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual("minimax-m3-play", reader.GetString(0));
        Assert.AreEqual("minimax-m3", reader.GetString(1));
        Assert.AreEqual("minimax-m3-play", reader.GetString(2));
        Assert.AreEqual("MiniMax-M3", reader.GetString(3));
        Assert.AreEqual((long)ModelResolutionOrigin.ExactAlias, reader.GetInt64(4));
    }

    [TestMethod]
    public async Task Commit_PersistsSessionMetadataTurnHashAndCursorAtomically()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        const string turnHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        UsageEvent value = TestEvents.Create() with
        {
            SessionId = "side-session",
            ParentSessionId = "primary-session",
            TurnIdHash = turnHash
        };
        UsageSessionMetadata session = Session(
            "side-session",
            SessionKind.Side,
            "primary-session",
            "history-origin");

        await writer.CommitAsync(
            Batch(value) with { Sessions = [session] },
            CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT turn_id_hash FROM usage_events WHERE event_id = 'event-1'),
                usage_sessions.session_kind,
                usage_sessions.direct_parent_session_id,
                usage_sessions.forked_from_session_id,
                usage_sessions.relation_origin,
                usage_sessions.relation_state,
                usage_sessions.replay_state,
                usage_sessions.compatibility_level,
                (SELECT cursor_json FROM source_cursors
                 WHERE source_entity_id = 'rollout:test')
            FROM usage_sessions
            WHERE session_id = 'side-session';
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(turnHash, reader.GetString(0));
        Assert.AreEqual((long)SessionKind.Side, reader.GetInt64(1));
        Assert.AreEqual("primary-session", reader.GetString(2));
        Assert.AreEqual("history-origin", reader.GetString(3));
        Assert.AreEqual(
            (long)SessionRelationOrigin.TopLevelParentThreadId,
            reader.GetInt64(4));
        Assert.AreEqual((long)SessionRelationState.Confirmed, reader.GetInt64(5));
        Assert.AreEqual((long)ReplayState.Active, reader.GetInt64(6));
        Assert.AreEqual(
            (long)CompatibilityLevel.FullyCompatible,
            reader.GetInt64(7));
        Assert.AreEqual("cursor-1", reader.GetString(8));
    }

    [TestMethod]
    public async Task SynchronizeSessionNames_UpdatesKnownSessionAndRejectsStaleValue()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            SessionOnlyBatch(Session("named-session", SessionKind.Primary)),
            CancellationToken.None);
        DateTimeOffset latest =
            new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        await writer.SynchronizeSessionNamesAsync(
            Instance(),
            [
                new UsageSessionNameMetadata(
                    "named-session",
                    "当前会话名",
                    latest),
                new UsageSessionNameMetadata(
                    "not-collected",
                    "不应创建",
                    latest)
            ],
            CancellationToken.None);
        await writer.SynchronizeSessionNamesAsync(
            Instance(),
            [
                new UsageSessionNameMetadata(
                    "named-session",
                    "过期会话名",
                    latest.AddMinutes(-1))
            ],
            CancellationToken.None);
        await writer.SynchronizeSessionNamesAsync(
            Instance(),
            [
                new UsageSessionNameMetadata(
                    "named-session",
                    "同版本最新名称",
                    latest)
            ],
            CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT session_name
                 FROM usage_sessions
                 WHERE session_id = 'named-session'),
                (SELECT session_name_updated_unix_ms
                 FROM usage_sessions
                 WHERE session_id = 'named-session'),
                (SELECT COUNT(*)
                 FROM usage_sessions
                 WHERE session_id = 'not-collected');
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual("同版本最新名称", reader.GetString(0));
        Assert.AreEqual(latest.ToUnixTimeMilliseconds(), reader.GetInt64(1));
        Assert.AreEqual(0L, reader.GetInt64(2));
    }

    [TestMethod]
    public async Task Commit_PropagatesSessionCompatibilityToSourceWithoutDowngrade()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries, _) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            SessionOnlyBatch(Session(
                "uncertain-side",
                SessionKind.Side,
                compatibilityLevel: CompatibilityLevel.PartiallyCompatible)),
            CancellationToken.None);
        await writer.CommitAsync(
            SessionOnlyBatch(Session("later-primary", SessionKind.Primary)),
            CancellationToken.None);

        SourceStatusRow source = Assert.ContainsSingle(
            await queries.GetSourcesAsync(CancellationToken.None));
        Assert.AreEqual(
            CompatibilityLevel.PartiallyCompatible,
            source.CompatibilityLevel);
        Assert.AreEqual("session_metadata_partial", source.CompatibilityCode);
        Assert.IsFalse(source.RequiresRescan);
    }

    [TestMethod]
    public async Task Commit_ReconcilesUniqueRepositoryProjectAcrossAgents()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        const string projectPath = @"C:\Projects\AgenTally";
        const string pathProjectId = "path-derived-project";
        string repositoryHash = new('a', 64);
        string repositoryProjectId = repositoryHash[..24];
        SourceInstanceDescriptor kimi = ScopedInstance(
            "kimi-code:windows:project-reconcile",
            "kimi-code",
            "Kimi project reconcile");
        SourceEntityDescriptor kimiFirst =
            ScopedEntity(kimi, "session:kimi-first");
        UsageEvent kimiFirstEvent = ScopedEvent(
            kimi,
            kimiFirst,
            "event-kimi-first") with
        {
            SessionId = "session-kimi",
            ProjectId = pathProjectId,
            ProjectPath = projectPath
        };
        await writer.CommitAsync(
            ScopedBatch(kimi, kimiFirst, kimiFirstEvent) with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        kimi.AgentId,
                        kimi.SourceInstanceId,
                        kimiFirst.SourceEntityId,
                        "session-kimi",
                        SessionKind.Primary,
                        null,
                        null,
                        SessionRelationOrigin.None,
                        SessionRelationState.None,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                    {
                        ProjectId = pathProjectId,
                        ProjectPath = projectPath
                    }
                ]
            },
            CancellationToken.None);

        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:project-reconcile-cross-agent",
            "codex",
            "Codex project reconcile");
        SourceEntityDescriptor codexEntity =
            ScopedEntity(codex, "rollout:repository-known");
        await writer.CommitAsync(
            ScopedBatch(
                codex,
                codexEntity,
                ScopedEvent(codex, codexEntity, "event-codex") with
                {
                    ProjectId = repositoryProjectId,
                    ProjectPath = projectPath,
                    ProjectRepositoryIdentityHash = repositoryHash
                }),
            CancellationToken.None);

        SourceEntityDescriptor kimiLater =
            ScopedEntity(kimi, "session:kimi-later");
        UsageEvent kimiLaterEvent = ScopedEvent(
            kimi,
            kimiLater,
            "event-kimi-later") with
        {
            SessionId = "session-kimi",
            ProjectId = pathProjectId,
            ProjectPath = projectPath
        };
        await writer.CommitAsync(
            ScopedBatch(kimi, kimiLater, kimiLaterEvent) with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        kimi.AgentId,
                        kimi.SourceInstanceId,
                        kimiLater.SourceEntityId,
                        "session-kimi",
                        SessionKind.Primary,
                        null,
                        null,
                        SessionRelationOrigin.None,
                        SessionRelationState.None,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc.AddMinutes(1),
                        "fixture-v1")
                    {
                        ProjectId = pathProjectId,
                        ProjectPath = projectPath
                    }
                ]
            },
            CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COUNT(DISTINCT project_id),
                COUNT(project_repository_hash),
                SUM(normalized_total_value),
                MIN(project_id),
                MIN(project_repository_hash),
                (SELECT project_id
                 FROM usage_sessions
                 WHERE session_id = 'session-kimi'),
                (SELECT project_repository_hash
                 FROM usage_sessions
                 WHERE session_id = 'session-kimi')
            FROM usage_events
            WHERE project_path = $project_path;
            """;
        command.Parameters.AddWithValue("$project_path", projectPath);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(3L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(3L, reader.GetInt64(2));
        Assert.AreEqual(3L, reader.GetInt64(3));
        Assert.AreEqual(repositoryProjectId, reader.GetString(4));
        Assert.AreEqual(repositoryHash, reader.GetString(5));
        Assert.AreEqual(repositoryProjectId, reader.GetString(6));
        Assert.AreEqual(repositoryHash, reader.GetString(7));
    }

    [TestMethod]
    public async Task Commit_DoesNotReconcileAmbiguousRepositoryPathAcrossAgents()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        const string projectPath = @"D:\Projects\reused";
        string firstHash = new('a', 64);
        string secondHash = new('b', 64);
        SourceInstanceDescriptor first = ScopedInstance(
            "codex:windows:first-repository",
            "codex",
            "First repository");
        SourceInstanceDescriptor second = ScopedInstance(
            "claude-code:windows:second-repository",
            "claude-code",
            "Second repository");
        SourceInstanceDescriptor pathOnly = ScopedInstance(
            "kimi-code:windows:path-only",
            "kimi-code",
            "Path only");

        (SourceInstanceDescriptor Instance, string EventId,
         string ProjectId, string? RepositoryHash)[] projects =
        [
            (first, "event-first", firstHash[..24], firstHash),
            (second, "event-second", secondHash[..24], secondHash),
            (pathOnly, "event-path-only", "path-project", null)
        ];
        foreach ((SourceInstanceDescriptor Instance, string EventId,
                  string ProjectId, string? RepositoryHash) item in projects)
        {
            SourceEntityDescriptor entity =
                ScopedEntity(item.Instance, $"entity:{item.EventId}");
            await writer.CommitAsync(
                ScopedBatch(
                    item.Instance,
                    entity,
                    ScopedEvent(item.Instance, entity, item.EventId) with
                    {
                        ProjectId = item.ProjectId,
                        ProjectPath = projectPath,
                        ProjectRepositoryIdentityHash = item.RepositoryHash
                    }),
                CancellationToken.None);
        }

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COUNT(DISTINCT project_id),
                COUNT(DISTINCT project_repository_hash),
                (SELECT project_id FROM usage_events
                 WHERE event_id = 'event-path-only'),
                (SELECT project_repository_hash FROM usage_events
                 WHERE event_id = 'event-path-only')
            FROM usage_events
            WHERE project_path = $project_path;
            """;
        command.Parameters.AddWithValue("$project_path", projectPath);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(3L, reader.GetInt64(0));
        Assert.AreEqual(3L, reader.GetInt64(1));
        Assert.AreEqual(2L, reader.GetInt64(2));
        Assert.AreEqual("path-project", reader.GetString(3));
        Assert.IsTrue(reader.IsDBNull(4));
    }

    [TestMethod]
    public async Task Commit_RollsBackEventsAndCursorWhenEventUpsertFails()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            Batch(TestEvents.Create(), cursorJson: "cursor-before"),
            CancellationToken.None);

        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_explode
                BEFORE INSERT ON usage_events
                WHEN NEW.event_id = 'explode'
                BEGIN
                    SELECT RAISE(ABORT, 'forced test failure');
                END;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        UsageEvent wouldBeInserted = TestEvents.Create(
            eventId: "would-be-partial",
            dedupKey: "codex:thread-1:2");
        UsageEvent explode = TestEvents.Create(
            eventId: "explode",
            dedupKey: "codex:thread-1:3");

        await Assert.ThrowsExactlyAsync<SqliteException>(() => writer.CommitAsync(
            Batch([wouldBeInserted, explode], cursorJson: "cursor-after") with
            {
                Sessions =
                [
                    Session(
                        "rolled-back-session",
                        SessionKind.Side,
                        "primary-session")
                ]
            },
            CancellationToken.None));

        Assert.AreEqual(
            "cursor-before",
            (await writer.GetCursorAsync(
                "codex:windows:test",
                "rollout:test",
                CancellationToken.None))?.CursorJson);
        Assert.IsFalse(await EventExistsAsync(connections, "would-be-partial"));
        Assert.IsFalse(await SessionExistsAsync(connections, "rolled-back-session"));
    }

    [TestMethod]
    public async Task Commit_ConflictingParentsAndCyclesDegradeRelationsSafely()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);

        await writer.CommitAsync(
            SessionOnlyBatch(Session("primary-a", SessionKind.Primary)),
            CancellationToken.None);
        await writer.CommitAsync(
            SessionOnlyBatch(Session(
                "side-a",
                SessionKind.Side,
                "primary-a")),
            CancellationToken.None);
        await writer.CommitAsync(
            SessionOnlyBatch(Session(
                "side-a",
                SessionKind.Side,
                "different-primary")),
            CancellationToken.None);
        await writer.CommitAsync(
            SessionOnlyBatch(Session(
                "cycle-a",
                SessionKind.Side,
                "cycle-b")),
            CancellationToken.None);
        await writer.CommitAsync(
            SessionOnlyBatch(Session(
                "cycle-b",
                SessionKind.Side,
                "cycle-a")),
            CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                session_id,
                direct_parent_session_id,
                relation_origin,
                relation_state,
                compatibility_level
            FROM usage_sessions
            WHERE session_id IN ('side-a', 'cycle-a', 'cycle-b')
            ORDER BY session_id;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        var rows = new List<(string Id, string? Parent, long Origin, long State, long Compatibility)>();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        Assert.HasCount(3, rows);
        var cycleA = rows.Single(static row => row.Id == "cycle-a");
        var cycleB = rows.Single(static row => row.Id == "cycle-b");
        var conflict = rows.Single(static row => row.Id == "side-a");
        Assert.AreEqual("cycle-b", cycleA.Parent);
        Assert.AreEqual(SessionRelationState.Confirmed, (SessionRelationState)cycleA.State);
        Assert.IsNull(cycleB.Parent);
        Assert.AreEqual(SessionRelationOrigin.None, (SessionRelationOrigin)cycleB.Origin);
        Assert.AreEqual(SessionRelationState.Uncertain, (SessionRelationState)cycleB.State);
        Assert.IsNull(conflict.Parent);
        Assert.AreEqual(SessionRelationState.Uncertain, (SessionRelationState)conflict.State);
        Assert.AreEqual(
            CompatibilityLevel.PartiallyCompatible,
            (CompatibilityLevel)conflict.Compatibility);
    }

    [TestMethod]
    public async Task Commit_ParserRepairCanReplaceSameStateFromDifferentParser()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, _) = await CreateServicesAsync(directory);
        UsageEvent original = CreateEvent(parserVersion: "codex-v1", normalizedTotal: 100);
        UsageEvent repaired = CreateEvent(parserVersion: "codex-v2", normalizedTotal: 140);

        await writer.CommitAsync(Batch(original), CancellationToken.None);
        WriteResult normal = await writer.CommitAsync(Batch(repaired), CancellationToken.None);
        WriteResult repair = await writer.CommitAsync(
            Batch(repaired, WriteIntent.ParserRepair),
            CancellationToken.None);

        Assert.AreEqual(0, normal.AppliedCount);
        Assert.AreEqual(1, repair.AppliedCount);
        StoredCursor? cursor = await writer.GetCursorAsync(
            "codex:windows:test",
            "rollout:test",
            CancellationToken.None);
        Assert.AreEqual("codex-v2", cursor?.ParserVersion);
    }

    [TestMethod]
    public async Task ResetSourceInstance_DeletesOnlyTheExactAgentAndSourceInstance()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codexA = ScopedInstance(
            "codex:windows:a",
            "codex",
            "Codex A");
        SourceEntityDescriptor entityA = ScopedEntity(codexA, "rollout:a");
        SourceInstanceDescriptor codexB = ScopedInstance(
            "codex:windows:b",
            "codex",
            "Codex B");
        SourceEntityDescriptor entityB = ScopedEntity(codexB, "rollout:b");
        SourceInstanceDescriptor mock = ScopedInstance(
            "mock:fixture:a",
            "mock",
            "Mock A");
        SourceEntityDescriptor mockEntity = ScopedEntity(mock, "mock:entity:a");

        await writer.CommitAsync(
            ScopedBatch(codexA, entityA, ScopedEvent(codexA, entityA, "event-a")),
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(codexB, entityB, ScopedEvent(codexB, entityB, "event-b")),
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(mock, mockEntity, ScopedEvent(mock, mockEntity, "event-mock")),
            CancellationToken.None);

        await writer.ResetSourceInstanceAsync(codexA, CancellationToken.None);

        Assert.AreEqual(
            0L,
            await CountEventsAsync(connections, codexA.AgentId, codexA.SourceInstanceId));
        Assert.IsNull(await writer.GetCursorAsync(
            codexA.SourceInstanceId,
            entityA.SourceEntityId,
            CancellationToken.None));
        Assert.AreEqual(
            1L,
            await CountEventsAsync(connections, codexB.AgentId, codexB.SourceInstanceId));
        Assert.IsNotNull(await writer.GetCursorAsync(
            codexB.SourceInstanceId,
            entityB.SourceEntityId,
            CancellationToken.None));
        Assert.AreEqual(
            1L,
            await CountEventsAsync(connections, mock.AgentId, mock.SourceInstanceId));
        Assert.IsNotNull(await writer.GetCursorAsync(
            mock.SourceInstanceId,
            mockEntity.SourceEntityId,
            CancellationToken.None));
        Assert.AreEqual(1L, await CountInstancesAsync(connections, codexA.SourceInstanceId));
    }

    [TestMethod]
    public async Task ResetSourceInstance_RollsBackEventDeletionWhenCursorDeletionFails()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:rollback",
            "codex",
            "Codex rollback");
        SourceEntityDescriptor entity = ScopedEntity(codex, "rollout:rollback");
        await writer.CommitAsync(
            ScopedBatch(codex, entity, ScopedEvent(codex, entity, "event-rollback")),
            CancellationToken.None);

        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_cursor_reset
                BEFORE DELETE ON source_cursors
                WHEN OLD.source_instance_id = 'codex:windows:rollback'
                BEGIN
                    SELECT RAISE(ABORT, 'forced reset failure');
                END;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            writer.ResetSourceInstanceAsync(codex, CancellationToken.None));

        Assert.AreEqual(
            1L,
            await CountEventsAsync(connections, codex.AgentId, codex.SourceInstanceId));
        Assert.IsNotNull(await writer.GetCursorAsync(
            codex.SourceInstanceId,
            entity.SourceEntityId,
            CancellationToken.None));
    }

    [TestMethod]
    public async Task GetUsageSourceEntities_ReturnsOnlyRequestedEntitiesWithEvents()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, _) = await CreateServicesAsync(directory);
        SourceInstanceDescriptor requested = ScopedInstance(
            "codex:windows:history",
            "codex",
            "Codex history");
        SourceEntityDescriptor withEvents =
            ScopedEntity(requested, "rollout:with-events");
        SourceEntityDescriptor cursorOnly =
            ScopedEntity(requested, "rollout:cursor-only");
        SourceInstanceDescriptor unrelated = ScopedInstance(
            "mock:windows:other",
            "mock",
            "Mock other");
        SourceEntityDescriptor unrelatedEntity =
            ScopedEntity(unrelated, "rollout:unrelated");
        await writer.CommitAsync(
            ScopedBatch(
                requested,
                withEvents,
                ScopedEvent(requested, withEvents, "event-history")),
            CancellationToken.None);
        await writer.CommitAsync(
            new UsageEventBatch(
                requested,
                cursorOnly,
                "cursor-only",
                "fixture-1",
                "fixture-v1",
                BatchCheckedAtUtc,
                []),
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(
                unrelated,
                unrelatedEntity,
                ScopedEvent(unrelated, unrelatedEntity, "event-unrelated")),
            CancellationToken.None);

        IReadOnlyList<StoredUsageSourceEntity> result =
            await writer.GetSourceEntitiesWithUsageEventsAsync(
                requested.AgentId,
                CancellationToken.None);

        Assert.HasCount(1, result);
        Assert.AreEqual(requested.SourceInstanceId, result[0].SourceInstanceId);
        Assert.AreEqual(withEvents.SourceEntityId, result[0].SourceEntityId);
    }

    [TestMethod]
    public async Task ReplaceFromStaging_RollsBackPrimaryDeletionWhenStageCopyFails()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:atomic-stage",
            "codex",
            "Codex atomic stage");
        SourceEntityDescriptor entity = ScopedEntity(codex, "rollout:atomic-stage");
        await writer.CommitAsync(
            ScopedBatch(
                codex,
                entity,
                ScopedEvent(codex, entity, "event-before"),
                cursorJson: "cursor-before"),
            CancellationToken.None);

        string stagingPath = directory.File("rebuild-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        await stagingWriter.CommitAsync(
            ScopedBatch(
                codex,
                entity,
                ScopedEvent(codex, entity, "event-after"),
                cursorJson: "cursor-after"),
            CancellationToken.None);
        await using (SqliteConnection connection =
            await stagingConnections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE usage_events;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            writer.ReplaceSourceInstancesFromStagingAsync(
                [codex],
                stagingPath,
                CancellationToken.None));

        Assert.AreEqual(
            1L,
            await CountEventsAsync(connections, codex.AgentId, codex.SourceInstanceId));
        Assert.IsTrue(await EventExistsAsync(connections, "event-before"));
        Assert.AreEqual(
            "cursor-before",
            (await writer.GetCursorAsync(
                codex.SourceInstanceId,
                entity.SourceEntityId,
                CancellationToken.None))?.CursorJson);
    }

    [TestMethod]
    public async Task ReplaceFromStaging_PreservesColumnAlignmentAfterV2Migration()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            Batch(TestEvents.Create(eventId: "event-before")),
            CancellationToken.None);
        await ConvertCurrentFixtureToV2Async(connections);
        await writer.InitializeAsync(CancellationToken.None);

        string stagingPath = directory.File("rebuild-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        UsageEvent staged = TestEvents.Create(
            eventId: "event-after",
            sourceRevision: 7) with
        {
            SessionId = "session-after",
            ProjectId = "project-after",
            ProjectPath = @"D:\Repo\frontend"
        };
        await stagingWriter.CommitAsync(Batch(staged), CancellationToken.None);

        await writer.ReplaceSourceInstancesFromStagingAsync(
            [Instance()],
            stagingPath,
            CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                event_id,
                session_id,
                project_id,
                project_path,
                raw_model,
                normalized_total_value,
                parser_version,
                source_revision
            FROM usage_events;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual("event-after", reader.GetString(0));
        Assert.AreEqual("session-after", reader.GetString(1));
        Assert.AreEqual("project-after", reader.GetString(2));
        Assert.AreEqual(@"D:\Repo\frontend", reader.GetString(3));
        Assert.AreEqual("gpt-test", reader.GetString(4));
        Assert.AreEqual(100L, reader.GetInt64(5));
        Assert.AreEqual("codex-v1", reader.GetString(6));
        Assert.AreEqual(7L, reader.GetInt64(7));
        Assert.IsFalse(await reader.ReadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task MergeFromStaging_ReconcilesPathOnlyHistoryWhenRepositoryIsUnique()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:project-reconcile",
            "codex",
            "Codex project reconcile");
        const string mainPath = @"C:\Projects\AgenTally";
        const string worktreePath =
            @"C:\Users\test\.codex\worktrees\2f68\AgenTally";
        string repositoryHash = new('a', 64);
        string repositoryProjectId = repositoryHash[..24];
        SourceEntityDescriptor legacyEntity =
            ScopedEntity(codex, "rollout:legacy-main");
        UsageEvent legacyEvent = ScopedEvent(
            codex,
            legacyEntity,
            "event-legacy-main") with
        {
            SessionId = "session-legacy-main",
            ProjectId = "legacy-path-project",
            ProjectPath = mainPath
        };
        await writer.CommitAsync(
            ScopedBatch(codex, legacyEntity, legacyEvent) with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        legacyEntity.SourceEntityId,
                        "session-legacy-main",
                        SessionKind.Primary,
                        null,
                        null,
                        SessionRelationOrigin.None,
                        SessionRelationState.None,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                    {
                        ProjectId = "legacy-path-project",
                        ProjectPath = mainPath
                    }
                ]
            },
            CancellationToken.None);

        string stagingPath = directory.File("project-reconcile-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        SourceEntityDescriptor currentEntity =
            ScopedEntity(codex, "rollout:current-main");
        UsageEvent currentEvent = ScopedEvent(
            codex,
            currentEntity,
            "event-current-main",
            parserVersion: "fixture-v2") with
        {
            ProjectId = repositoryProjectId,
            ProjectPath = mainPath,
            ProjectRepositoryIdentityHash = repositoryHash
        };
        await stagingWriter.CommitAsync(
            ScopedBatch(codex, currentEntity, currentEvent),
            CancellationToken.None);
        SourceEntityDescriptor worktreeEntity =
            ScopedEntity(codex, "rollout:worktree");
        UsageEvent worktreeEvent = ScopedEvent(
            codex,
            worktreeEntity,
            "event-worktree",
            parserVersion: "fixture-v2") with
        {
            ProjectId = repositoryProjectId,
            ProjectPath = worktreePath,
            ProjectRepositoryIdentityHash = repositoryHash
        };
        await stagingWriter.CommitAsync(
            ScopedBatch(codex, worktreeEntity, worktreeEvent),
            CancellationToken.None);

        await writer.MergeSourceInstancesFromStagingAsync(
            [codex],
            stagingPath,
            "fixture-v2",
            CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COUNT(DISTINCT project_id),
                COUNT(DISTINCT project_repository_hash),
                SUM(normalized_total_value),
                MIN(project_id),
                MIN(project_repository_hash),
                (SELECT project_repository_hash
                 FROM usage_sessions
                 WHERE session_id = 'session-legacy-main')
            FROM usage_events
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", codex.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            codex.SourceInstanceId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(3L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
        Assert.AreEqual(3L, reader.GetInt64(3));
        Assert.AreEqual(repositoryProjectId, reader.GetString(4));
        Assert.AreEqual(repositoryHash, reader.GetString(5));
        Assert.AreEqual(repositoryHash, reader.GetString(6));
    }

    [TestMethod]
    public async Task MergeFromStaging_ReconcilesUniqueRepositoryAcrossAgents()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        const string projectPath = @"C:\Projects\AgenTally";
        const string pathProjectId = "path-derived-project";
        string repositoryHash = new('d', 64);
        string repositoryProjectId = repositoryHash[..24];
        SourceInstanceDescriptor kimi = ScopedInstance(
            "kimi-code:windows:staged-cross-agent",
            "kimi-code",
            "Kimi staged cross-agent");
        SourceEntityDescriptor kimiEntity =
            ScopedEntity(kimi, "session:path-only-history");
        UsageEvent kimiEvent = ScopedEvent(
            kimi,
            kimiEntity,
            "event-path-only-history") with
        {
            SessionId = "session-path-only-history",
            ProjectId = pathProjectId,
            ProjectPath = projectPath
        };
        await writer.CommitAsync(
            ScopedBatch(kimi, kimiEntity, kimiEvent) with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        kimi.AgentId,
                        kimi.SourceInstanceId,
                        kimiEntity.SourceEntityId,
                        "session-path-only-history",
                        SessionKind.Primary,
                        null,
                        null,
                        SessionRelationOrigin.None,
                        SessionRelationState.None,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                    {
                        ProjectId = pathProjectId,
                        ProjectPath = projectPath
                    }
                ]
            },
            CancellationToken.None);

        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:staged-cross-agent",
            "codex",
            "Codex staged cross-agent");
        SourceEntityDescriptor codexEntity =
            ScopedEntity(codex, "rollout:repository-known");
        string stagingPath = directory.File("cross-agent-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        await stagingWriter.CommitAsync(
            ScopedBatch(
                codex,
                codexEntity,
                ScopedEvent(
                    codex,
                    codexEntity,
                    "event-repository-known",
                    parserVersion: "fixture-v2") with
                {
                    ProjectId = repositoryProjectId,
                    ProjectPath = projectPath,
                    ProjectRepositoryIdentityHash = repositoryHash
                }),
            CancellationToken.None);

        await writer.MergeSourceInstancesFromStagingAsync(
            [codex],
            stagingPath,
            "fixture-v2",
            CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COUNT(DISTINCT project_id),
                COUNT(project_repository_hash),
                SUM(normalized_total_value),
                MIN(project_id),
                MIN(project_repository_hash),
                (SELECT project_id FROM usage_sessions
                 WHERE session_id = 'session-path-only-history'),
                (SELECT project_repository_hash FROM usage_sessions
                 WHERE session_id = 'session-path-only-history')
            FROM usage_events
            WHERE project_path = $project_path;
            """;
        command.Parameters.AddWithValue("$project_path", projectPath);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(2L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(2L, reader.GetInt64(2));
        Assert.AreEqual(2L, reader.GetInt64(3));
        Assert.AreEqual(repositoryProjectId, reader.GetString(4));
        Assert.AreEqual(repositoryHash, reader.GetString(5));
        Assert.AreEqual(repositoryProjectId, reader.GetString(6));
        Assert.AreEqual(repositoryHash, reader.GetString(7));
    }

    [TestMethod]
    public async Task MergeFromStaging_DoesNotReconcileAmbiguousRepositoryPath()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:project-ambiguous",
            "codex",
            "Codex ambiguous project");
        const string reusedPath = @"D:\Projects\reused";
        SourceEntityDescriptor legacyEntity =
            ScopedEntity(codex, "rollout:legacy");
        UsageEvent legacyEvent = ScopedEvent(
            codex,
            legacyEntity,
            "event-legacy") with
        {
            ProjectId = "legacy-path-project",
            ProjectPath = reusedPath
        };
        await writer.CommitAsync(
            ScopedBatch(codex, legacyEntity, legacyEvent),
            CancellationToken.None);

        string stagingPath = directory.File("project-ambiguous-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        string firstHash = new('a', 64);
        string secondHash = new('b', 64);
        SourceEntityDescriptor firstEntity =
            ScopedEntity(codex, "rollout:first-repository");
        await stagingWriter.CommitAsync(
            ScopedBatch(
                codex,
                firstEntity,
                ScopedEvent(
                    codex,
                    firstEntity,
                    "event-first",
                    parserVersion: "fixture-v2") with
                {
                    ProjectId = firstHash[..24],
                    ProjectPath = reusedPath,
                    ProjectRepositoryIdentityHash = firstHash
                }),
            CancellationToken.None);
        SourceEntityDescriptor secondEntity =
            ScopedEntity(codex, "rollout:second-repository");
        await stagingWriter.CommitAsync(
            ScopedBatch(
                codex,
                secondEntity,
                ScopedEvent(
                    codex,
                    secondEntity,
                    "event-second",
                    parserVersion: "fixture-v2") with
                {
                    ProjectId = secondHash[..24],
                    ProjectPath = reusedPath,
                    ProjectRepositoryIdentityHash = secondHash
                }),
            CancellationToken.None);

        await writer.MergeSourceInstancesFromStagingAsync(
            [codex],
            stagingPath,
            "fixture-v2",
            CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT project_id, project_repository_hash
            FROM usage_events
            WHERE event_id = 'event-legacy';
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual("legacy-path-project", reader.GetString(0));
        Assert.IsTrue(reader.IsDBNull(1));
    }

    [TestMethod]
    public async Task MergeFromStaging_PreservesDatabaseOnlyHistoryAndBoundPrice()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:merge-stage",
            "codex",
            "Codex merge stage");
        SourceEntityDescriptor currentEntity =
            ScopedEntity(codex, "rollout:current");
        SourceEntityDescriptor missingEntity =
            ScopedEntity(codex, "rollout:database-only");
        UsageEvent currentBefore = ScopedEvent(
            codex,
            currentEntity,
            "event-current",
            dedupKey: "dedup-before");
        string staleCurrentTurn = new('8', 64);
        string staleCurrentDispatch = new('9', 64);
        await writer.CommitAsync(
            ScopedBatch(codex, currentEntity, currentBefore, "cursor-before") with
            {
                Turns =
                [
                    new UsageTurnMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        currentEntity.SourceEntityId,
                        "stale-current-session",
                        staleCurrentTurn,
                        BatchCheckedAtUtc.AddMinutes(-2),
                        BatchCheckedAtUtc.AddMinutes(-1),
                        "stale current prompt",
                        1,
                        "fixture-v1")
                ],
                Dispatches =
                [
                    new UsageTurnDispatch(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        currentEntity.SourceEntityId,
                        "stale-current-session",
                        staleCurrentTurn,
                        staleCurrentDispatch,
                        new string('a', 64),
                        TurnDispatchKind.Spawn,
                        DispatchTargetKind.AgentLeaf,
                        BatchCheckedAtUtc.AddMinutes(-1),
                        "fixture-v1")
                ]
            },
            CancellationToken.None);
        string databaseOnlyTurn = new('7', 64);
        UsageEvent databaseOnlyEvent = ScopedEvent(
            codex,
            missingEntity,
            "event-database-only") with
        {
            SessionId = "database-only-session",
            TurnIdHash = databaseOnlyTurn
        };
        await writer.CommitAsync(
            ScopedBatch(
                codex,
                missingEntity,
                databaseOnlyEvent,
                "cursor-database-only") with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        missingEntity.SourceEntityId,
                        "database-only-session",
                        SessionKind.Primary,
                        null,
                        null,
                        SessionRelationOrigin.None,
                        SessionRelationState.None,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                    {
                        SessionRole = SessionRole.Main
                    }
                ],
                Turns =
                [
                    new UsageTurnMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        missingEntity.SourceEntityId,
                        "database-only-session",
                        databaseOnlyTurn,
                        BatchCheckedAtUtc.AddHours(-1),
                        BatchCheckedAtUtc,
                        "原始日志删除后仍保留",
                        1,
                        "fixture-v1")
                ],
                Dispatches =
                [
                    new UsageTurnDispatch(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        missingEntity.SourceEntityId,
                        "database-only-session",
                        databaseOnlyTurn,
                        new string('b', 64),
                        new string('c', 64),
                        TurnDispatchKind.Spawn,
                        DispatchTargetKind.AgentLeaf,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                ]
            },
            CancellationToken.None);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE usage_events
                SET price_catalog_version = 'catalog-before',
                    price_rule_id = 'rule-before',
                    input_rate_usd_per_million = '1.25',
                    output_rate_usd_per_million = '9.5',
                    price_context_multiplier = '1',
                    estimated_cost_usd = '0.0042',
                    pricing_status = 1
                WHERE event_id = 'event-current';
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        string stagingPath = directory.File("merge-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        string stagedTurnHash = new('1', 64);
        string stagedDedupKey = new('2', 64);
        UsageEvent currentAfter = ScopedEvent(
            codex,
            currentEntity,
            "event-current",
            parserVersion: "fixture-v2",
            dedupKey: stagedDedupKey) with
        {
            SessionId = "side-session",
            TurnIdHash = stagedTurnHash
        };
        UsageEventBatch stagedBatch =
            ScopedBatch(codex, currentEntity, currentAfter, "cursor-after") with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        currentEntity.SourceEntityId,
                        "side-session",
                        SessionKind.Side,
                        "root-session",
                        null,
                        SessionRelationOrigin.TopLevelParentThreadId,
                        SessionRelationState.Confirmed,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc,
                        "fixture-v2")
                    {
                        SessionRole = SessionRole.Subagent,
                        AgentPathHash = new string('3', 64),
                        AgentLeafHash = new string('4', 64)
                    }
                ],
                Turns =
                [
                    new UsageTurnMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        currentEntity.SourceEntityId,
                        "side-session",
                        stagedTurnHash,
                        BatchCheckedAtUtc.AddMinutes(-1),
                        BatchCheckedAtUtc,
                        "staged prompt preview",
                        1,
                        "fixture-v2")
                ],
                EventTools =
                [
                    new UsageEventToolMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        currentEntity.SourceEntityId,
                        stagedDedupKey,
                        0,
                        "shell_command",
                        "fixture-v2")
                ],
                Dispatches =
                [
                    new UsageTurnDispatch(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        currentEntity.SourceEntityId,
                        "side-session",
                        stagedTurnHash,
                        new string('5', 64),
                        new string('6', 64),
                        TurnDispatchKind.Spawn,
                        DispatchTargetKind.AgentLeaf,
                        BatchCheckedAtUtc,
                        "fixture-v2")
                ]
            };
        await stagingWriter.CommitAsync(stagedBatch, CancellationToken.None);

        await writer.MergeSourceInstancesFromStagingAsync(
            [codex],
            stagingPath,
            "fixture-v2",
            CancellationToken.None);

        Assert.AreEqual(2L, await CountEventsAsync(
            connections,
            codex.AgentId,
            codex.SourceInstanceId));
        Assert.IsTrue(await EventExistsAsync(connections, "event-database-only"));
        Assert.IsTrue(await SessionExistsAsync(connections, "side-session"));
        Assert.IsNull(await writer.GetCursorAsync(
            codex.SourceInstanceId,
            missingEntity.SourceEntityId,
            CancellationToken.None));
        SourceInstanceParserState parserState =
            await writer.GetSourceInstanceParserStateAsync(
                codex,
                "fixture-v2",
                CancellationToken.None);
        Assert.IsFalse(parserState.RequiresRebuild);
        Assert.AreEqual(
            "cursor-after",
            (await writer.GetCursorAsync(
                codex.SourceInstanceId,
                currentEntity.SourceEntityId,
                CancellationToken.None))?.CursorJson);
        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                dedup_key,
                parser_version,
                price_catalog_version,
                price_rule_id,
                estimated_cost_usd,
                pricing_status,
                (SELECT prompt_preview FROM usage_turns
                 WHERE session_id = 'side-session'),
                (SELECT tool_name FROM usage_event_tools
                 WHERE event_dedup_key = usage_events.dedup_key),
                (SELECT COUNT(*) FROM usage_turn_dispatches
                 WHERE source_session_id = 'side-session'),
                (SELECT COUNT(*) FROM usage_turn_attributions
                 WHERE session_id = 'side-session'),
                (SELECT prompt_preview FROM usage_turns
                 WHERE session_id = 'database-only-session'),
                (SELECT COUNT(*) FROM usage_turns
                 WHERE session_id = 'stale-current-session'),
                (SELECT COUNT(*) FROM usage_turn_dispatches
                 WHERE source_session_id = 'stale-current-session'),
                (SELECT COUNT(*) FROM usage_turn_dispatches
                 WHERE source_session_id = 'database-only-session')
            FROM usage_events
            WHERE event_id = 'event-current';
            """;
        await using SqliteDataReader reader =
            await verifyCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(stagedDedupKey, reader.GetString(0));
        Assert.AreEqual("fixture-v2", reader.GetString(1));
        Assert.AreEqual("catalog-before", reader.GetString(2));
        Assert.AreEqual("rule-before", reader.GetString(3));
        Assert.AreEqual("0.0042", reader.GetString(4));
        Assert.AreEqual(1L, reader.GetInt64(5));
        Assert.AreEqual("staged prompt preview", reader.GetString(6));
        Assert.AreEqual("shell_command", reader.GetString(7));
        Assert.AreEqual(1L, reader.GetInt64(8));
        Assert.AreEqual(1L, reader.GetInt64(9));
        Assert.AreEqual("原始日志删除后仍保留", reader.GetString(10));
        Assert.AreEqual(0L, reader.GetInt64(11));
        Assert.AreEqual(0L, reader.GetInt64(12));
        Assert.AreEqual(1L, reader.GetInt64(13));
    }

    [TestMethod]
    public async Task ClearFromStaging_DeletesOnlyTargetStatisticsAndInstallsEofCursor()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:clear-stage",
            "codex",
            "Codex clear stage");
        SourceEntityDescriptor codexEntity =
            ScopedEntity(codex, "rollout:clear");
        SourceInstanceDescriptor unrelated = ScopedInstance(
            "mock:windows:keep",
            "mock",
            "Mock keep");
        SourceEntityDescriptor unrelatedEntity =
            ScopedEntity(unrelated, "rollout:keep");
        string turnHash = new('a', 64);
        string eventDedup = new('b', 64);
        UsageEvent eventToClear = ScopedEvent(
            codex,
            codexEntity,
            "event-clear",
            dedupKey: eventDedup) with
        {
            SessionId = "clear-session",
            TurnIdHash = turnHash
        };
        await writer.CommitAsync(
            ScopedBatch(
                codex,
                codexEntity,
                eventToClear,
                "cursor-before") with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        codexEntity.SourceEntityId,
                        "clear-session",
                        SessionKind.Primary,
                        null,
                        null,
                        SessionRelationOrigin.None,
                        SessionRelationState.None,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                    {
                        SessionRole = SessionRole.Main
                    }
                ],
                Turns =
                [
                    new UsageTurnMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        codexEntity.SourceEntityId,
                        "clear-session",
                        turnHash,
                        BatchCheckedAtUtc.AddMinutes(-1),
                        BatchCheckedAtUtc,
                        "只应保留到清除统计",
                        1,
                        "fixture-v1")
                ],
                EventTools =
                [
                    new UsageEventToolMetadata(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        codexEntity.SourceEntityId,
                        eventDedup,
                        0,
                        "shell_command",
                        "fixture-v1")
                ],
                Dispatches =
                [
                    new UsageTurnDispatch(
                        codex.AgentId,
                        codex.SourceInstanceId,
                        codexEntity.SourceEntityId,
                        "clear-session",
                        turnHash,
                        new string('c', 64),
                        new string('d', 64),
                        TurnDispatchKind.Spawn,
                        DispatchTargetKind.AgentLeaf,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                ]
            },
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(
                unrelated,
                unrelatedEntity,
                ScopedEvent(unrelated, unrelatedEntity, "event-keep"),
                "cursor-keep"),
            CancellationToken.None);

        string stagingPath = directory.File("clear-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        await stagingWriter.CommitAsync(
            ScopedBatch(
                codex,
                codexEntity,
                ScopedEvent(codex, codexEntity, "event-not-copied"),
                "cursor-at-eof"),
            CancellationToken.None);

        await writer.ClearSourceInstancesFromStagingAsync(
            [codex],
            stagingPath,
            "fixture-v2",
            CancellationToken.None);

        Assert.AreEqual(0L, await CountEventsAsync(
            connections,
            codex.AgentId,
            codex.SourceInstanceId));
        Assert.AreEqual(1L, await CountEventsAsync(
            connections,
            unrelated.AgentId,
            unrelated.SourceInstanceId));
        Assert.IsTrue(await EventExistsAsync(connections, "event-keep"));
        Assert.AreEqual(
            "cursor-at-eof",
            (await writer.GetCursorAsync(
                codex.SourceInstanceId,
                codexEntity.SourceEntityId,
                CancellationToken.None))?.CursorJson);
        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM usage_turns
                 WHERE source_instance_id = $source_instance_id),
                (SELECT COUNT(*) FROM usage_event_tools
                 WHERE source_instance_id = $source_instance_id),
                (SELECT COUNT(*) FROM usage_turn_dispatches
                 WHERE source_instance_id = $source_instance_id),
                (SELECT COUNT(*) FROM usage_turn_attributions
                 WHERE source_instance_id = $source_instance_id),
                (SELECT COUNT(*) FROM usage_sessions
                 WHERE source_instance_id = $source_instance_id);
            """;
        verifyCommand.Parameters.AddWithValue(
            "$source_instance_id",
            codex.SourceInstanceId);
        await using SqliteDataReader metadataReader =
            await verifyCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await metadataReader.ReadAsync(CancellationToken.None));
        for (int index = 0; index < metadataReader.FieldCount; index++)
        {
            Assert.AreEqual(0L, metadataReader.GetInt64(index));
        }
    }

    [TestMethod]
    public async Task ClearFromStaging_InvalidStageDoesNotDeletePrimaryStatistics()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:invalid-clear",
            "codex",
            "Codex invalid clear");
        SourceEntityDescriptor entity =
            ScopedEntity(codex, "rollout:invalid-clear");
        await writer.CommitAsync(
            ScopedBatch(
                codex,
                entity,
                ScopedEvent(codex, entity, "event-before-clear")),
            CancellationToken.None);
        string stagingPath = directory.File("invalid-clear-stage.db");
        var stagingConnections = new SqliteConnectionFactory(
            new StorageOptions(stagingPath));
        var stagingWriter = new SqliteUsageWriter(stagingConnections);
        await stagingWriter.InitializeAsync(CancellationToken.None);
        await stagingWriter.CommitAsync(
            ScopedBatch(
                codex,
                entity,
                ScopedEvent(codex, entity, "event-stage")),
            CancellationToken.None);
        await using (SqliteConnection connection =
            await stagingConnections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE usage_sessions;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            writer.ClearSourceInstancesFromStagingAsync(
                [codex],
                stagingPath,
                "fixture-v2",
                CancellationToken.None));

        Assert.AreEqual(1L, await CountEventsAsync(
            connections,
            codex.AgentId,
            codex.SourceInstanceId));
        Assert.IsTrue(await EventExistsAsync(connections, "event-before-clear"));
    }

    [TestMethod]
    public async Task ClearAllFromStaging_RemovesEverySourceAndInstallsPerParserBaselines()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:all-clear",
            "codex",
            "Codex all clear");
        SourceInstanceDescriptor claude = ScopedInstance(
            "claude-code:cli:windows:all-clear",
            "claude-code",
            "Claude all clear");
        SourceInstanceDescriptor obsolete = ScopedInstance(
            "obsolete:windows:all-clear",
            "obsolete",
            "Obsolete all clear");
        SourceEntityDescriptor codexEntity = ScopedEntity(codex, "codex-entity");
        SourceEntityDescriptor claudeEntity = ScopedEntity(claude, "claude-entity");
        SourceEntityDescriptor obsoleteEntity = ScopedEntity(obsolete, "obsolete-entity");
        await writer.CommitAsync(
            ScopedBatch(
                codex,
                codexEntity,
                ScopedEvent(codex, codexEntity, "codex-before-clear")),
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(
                claude,
                claudeEntity,
                ScopedEvent(claude, claudeEntity, "claude-before-clear")),
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(
                obsolete,
                obsoleteEntity,
                ScopedEvent(obsolete, obsoleteEntity, "obsolete-before-clear")),
            CancellationToken.None);

        string stagingPath = directory.File("all-clear-stage.db");
        var stagingWriter = new SqliteUsageWriter(
            new SqliteConnectionFactory(new StorageOptions(stagingPath)));
        await stagingWriter.InitializeAsync(CancellationToken.None);
        await stagingWriter.CommitAsync(
            EmptyScopedBatch(codex, codexEntity, "codex-v2", "codex-eof"),
            CancellationToken.None);
        await stagingWriter.CommitAsync(
            EmptyScopedBatch(claude, claudeEntity, "claude-v2", "claude-eof"),
            CancellationToken.None);

        await writer.ClearAllStatisticsFromStagingAsync(
            [
                new SourceInstanceMaintenanceState(codex, "codex-v2"),
                new SourceInstanceMaintenanceState(
                    claude,
                    "claude-v2",
                    CompatibilityLevel.PartiallyCompatible,
                    "desktop_prompt_attribution_unavailable")
            ],
            stagingPath,
            CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM usage_events),
                (SELECT COUNT(*) FROM source_instances),
                (SELECT COUNT(*) FROM source_instances
                 WHERE source_instance_id = $obsolete),
                (SELECT accepted_parser_version FROM source_instances
                 WHERE source_instance_id = $codex),
                (SELECT accepted_parser_version FROM source_instances
                 WHERE source_instance_id = $claude),
                (SELECT compatibility_level FROM source_instances
                 WHERE source_instance_id = $claude);
            """;
        command.Parameters.AddWithValue("$obsolete", obsolete.SourceInstanceId);
        command.Parameters.AddWithValue("$codex", codex.SourceInstanceId);
        command.Parameters.AddWithValue("$claude", claude.SourceInstanceId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(0L, reader.GetInt64(0));
        Assert.AreEqual(2L, reader.GetInt64(1));
        Assert.AreEqual(0L, reader.GetInt64(2));
        Assert.AreEqual("codex-v2", reader.GetString(3));
        Assert.AreEqual("claude-v2", reader.GetString(4));
        Assert.AreEqual((long)CompatibilityLevel.PartiallyCompatible, reader.GetInt64(5));
        Assert.AreEqual(
            "codex-eof",
            (await writer.GetCursorAsync(
                codex.SourceInstanceId,
                codexEntity.SourceEntityId,
                CancellationToken.None))?.CursorJson);
        Assert.AreEqual(
            "claude-eof",
            (await writer.GetCursorAsync(
                claude.SourceInstanceId,
                claudeEntity.SourceEntityId,
                CancellationToken.None))?.CursorJson);
    }

    [TestMethod]
    public async Task GetParserState_NoStoredDerivedDataIsCompatible()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, _) = await CreateServicesAsync(directory);
        SourceInstanceDescriptor instance = ScopedInstance(
            "codex:windows:empty",
            "codex",
            "Codex empty");

        SourceInstanceParserState state = await writer.GetSourceInstanceParserStateAsync(
            instance,
            "codex-rollout-v2",
            CancellationToken.None);

        Assert.IsFalse(state.HasDerivedData);
        Assert.IsFalse(state.RequiresRebuild);
    }

    [TestMethod]
    public async Task ParserStateAndStagingMerge_ReclassifyChangedSourceAgent()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        const string sourceInstanceId = "kimi-code:desktop-work:windows:test";
        SourceInstanceDescriptor previous = ScopedInstance(
            sourceInstanceId,
            "kimi-code",
            "Kimi Work Desktop before platform split");
        SourceEntityDescriptor previousEntity = ScopedEntity(
            previous,
            "kimi-code:wire:previous");
        await writer.CommitAsync(
            ScopedBatch(
                previous,
                previousEntity,
                ScopedEvent(
                    previous,
                    previousEntity,
                    "previous-event",
                    parserVersion: "fixture-v1")),
            CancellationToken.None);

        SourceInstanceDescriptor current = previous with
        {
            AgentId = "kimi-work",
            DisplayName = "Kimi Work Desktop (Windows)"
        };
        SourceInstanceParserState changedState =
            await writer.GetSourceInstanceParserStateAsync(
                current,
                "fixture-v2",
                CancellationToken.None);
        Assert.IsTrue(changedState.HasDerivedData);
        Assert.IsTrue(changedState.RequiresRebuild);

        string stagingPath = directory.File("kimi-work-reclassification.db");
        var stagingWriter = new SqliteUsageWriter(
            new SqliteConnectionFactory(new StorageOptions(stagingPath)));
        await stagingWriter.InitializeAsync(CancellationToken.None);
        SourceEntityDescriptor currentEntity = ScopedEntity(
            current,
            "kimi-code:wire:current");
        await stagingWriter.CommitAsync(
            ScopedBatch(
                current,
                currentEntity,
                ScopedEvent(
                    current,
                    currentEntity,
                    "current-event",
                    parserVersion: "fixture-v2")),
            CancellationToken.None);

        await writer.MergeSourceInstancesFromStagingAsync(
            [current],
            stagingPath,
            "fixture-v2",
            CancellationToken.None);

        Assert.AreEqual(0L, await CountEventsAsync(
            connections,
            "kimi-code",
            sourceInstanceId));
        Assert.AreEqual(1L, await CountEventsAsync(
            connections,
            "kimi-work",
            sourceInstanceId));
        SourceInstanceParserState currentState =
            await writer.GetSourceInstanceParserStateAsync(
                current,
                "fixture-v2",
                CancellationToken.None);
        Assert.IsTrue(currentState.HasDerivedData);
        Assert.IsFalse(currentState.RequiresRebuild);
    }

    [TestMethod]
    public async Task GetParserState_DetectsLegacyEventWithoutAStoredCursor()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor instance = ScopedInstance(
            "codex:windows:event-only",
            "codex",
            "Codex event only");
        SourceEntityDescriptor entity = ScopedEntity(instance, "rollout:missing-event-source");
        await writer.CommitAsync(
            ScopedBatch(instance, entity, ScopedEvent(
                instance,
                entity,
                "event-only",
                parserVersion: "codex-rollout-v1")),
            CancellationToken.None);
        await DeleteCursorsAsync(connections, instance.SourceInstanceId);

        SourceInstanceParserState state = await writer.GetSourceInstanceParserStateAsync(
            instance,
            "codex-rollout-v2",
            CancellationToken.None);

        Assert.IsTrue(state.HasDerivedData);
        Assert.IsTrue(state.RequiresRebuild);
    }

    [TestMethod]
    public async Task GetParserState_DetectsLegacyCursorWithoutAStoredEvent()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        SourceInstanceDescriptor instance = ScopedInstance(
            "codex:windows:cursor-only",
            "codex",
            "Codex cursor only");
        SourceEntityDescriptor entity = ScopedEntity(instance, "rollout:missing-cursor-source");
        await writer.CommitAsync(
            ScopedBatch(instance, entity, ScopedEvent(
                instance,
                entity,
                "cursor-only",
                parserVersion: "codex-rollout-v1")),
            CancellationToken.None);
        await DeleteEventsAsync(
            connections,
            instance.AgentId,
            instance.SourceInstanceId);

        SourceInstanceParserState state = await writer.GetSourceInstanceParserStateAsync(
            instance,
            "codex-rollout-v2",
            CancellationToken.None);

        Assert.IsTrue(state.HasDerivedData);
        Assert.IsTrue(state.RequiresRebuild);
    }

    [TestMethod]
    public async Task GetParserState_CurrentRowsAreCompatibleAndOtherInstancesAreIgnored()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, _) = await CreateServicesAsync(directory);
        SourceInstanceDescriptor current = ScopedInstance(
            "codex:windows:current",
            "codex",
            "Codex current");
        SourceEntityDescriptor currentEntity = ScopedEntity(current, "rollout:current");
        SourceInstanceDescriptor legacy = ScopedInstance(
            "codex:windows:legacy-other",
            "codex",
            "Codex legacy other");
        SourceEntityDescriptor legacyEntity = ScopedEntity(legacy, "rollout:legacy-other");
        await writer.CommitAsync(
            ScopedBatch(current, currentEntity, ScopedEvent(
                current,
                currentEntity,
                "current",
                parserVersion: "codex-rollout-v2")),
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(legacy, legacyEntity, ScopedEvent(
                legacy,
                legacyEntity,
                "legacy-other",
                parserVersion: "codex-rollout-v1")),
            CancellationToken.None);

        SourceInstanceParserState state = await writer.GetSourceInstanceParserStateAsync(
            current,
            "codex-rollout-v2",
            CancellationToken.None);

        Assert.IsTrue(state.HasDerivedData);
        Assert.IsFalse(state.RequiresRebuild);
    }

    [TestMethod]
    public async Task GetParserState_MixedVersionsInOneInstanceRequireRebuild()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, _) = await CreateServicesAsync(directory);
        SourceInstanceDescriptor instance = ScopedInstance(
            "codex:windows:mixed",
            "codex",
            "Codex mixed");
        SourceEntityDescriptor currentEntity = ScopedEntity(instance, "rollout:mixed-current");
        SourceEntityDescriptor legacyEntity = ScopedEntity(instance, "rollout:mixed-legacy");
        await writer.CommitAsync(
            ScopedBatch(instance, currentEntity, ScopedEvent(
                instance,
                currentEntity,
                "mixed-current",
                parserVersion: "codex-rollout-v2")),
            CancellationToken.None);
        await writer.CommitAsync(
            ScopedBatch(instance, legacyEntity, ScopedEvent(
                instance,
                legacyEntity,
                "mixed-legacy",
                parserVersion: "codex-rollout-v1")),
            CancellationToken.None);

        SourceInstanceParserState state = await writer.GetSourceInstanceParserStateAsync(
            instance,
            "codex-rollout-v2",
            CancellationToken.None);

        Assert.IsTrue(state.HasDerivedData);
        Assert.IsTrue(state.RequiresRebuild);
    }

    [TestMethod]
    public async Task RecordFailure_PreservesCursorAndLastSuccess()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries, _) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(Batch(TestEvents.Create()), CancellationToken.None);
        DateTimeOffset failedAtUtc = new(2026, 7, 16, 0, 5, 0, TimeSpan.Zero);
        var changedPath = new SourceEntityDescriptor(
            "codex:windows:test",
            "rollout:test",
            "C:\\codex\\moved-after-success.jsonl");

        await writer.RecordFailureAsync(
            Instance(),
            changedPath,
            "simulated read failure",
            failedAtUtc,
            CancellationToken.None);

        StoredCursor? cursor = await writer.GetCursorAsync(
            "codex:windows:test",
            "rollout:test",
            CancellationToken.None);
        SourceStatusRow status = Assert.ContainsSingle(
            await queries.GetSourcesAsync(CancellationToken.None));
        Assert.IsNotNull(cursor);
        Assert.AreEqual("C:\\codex\\rollout-test.jsonl", cursor.SourcePath);
        Assert.AreEqual("cursor-1", cursor.CursorJson);
        Assert.AreEqual("fixture-1", cursor.SourceFingerprint);
        Assert.AreEqual("codex-v1", cursor.ParserVersion);
        Assert.AreEqual(BatchCheckedAtUtc, cursor.LastSuccessAtUtc);
        Assert.AreEqual("simulated read failure", cursor.LastError);
        Assert.AreEqual(failedAtUtc, cursor.LastErrorAtUtc);
        Assert.AreEqual("C:\\codex\\rollout-test.jsonl", status.SourcePath);
        Assert.AreEqual("simulated read failure", status.LastError);
    }

    [TestMethod]
    public async Task Commit_PersistsEveryUsageEventFieldWithoutColumnDrift()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        DateTimeOffset occurredAtUtc =
            new(2026, 7, 16, 3, 4, 5, TimeSpan.Zero);
        DateTimeOffset importedAtUtc =
            new(2026, 7, 16, 3, 5, 6, TimeSpan.Zero);
        var value = new UsageEvent(
            "codex",
            "codex:windows:test",
            "rollout:test",
            "event-all-fields",
            "codex:all-fields:1",
            SourceKind.Jsonl,
            occurredAtUtc,
            importedAtUtc,
            new ModelIdentity
            {
                RawModel = "raw-model",
                NormalizedModel = "normalized-model",
                ProviderId = "provider-id",
                ResolutionOrigin = ModelResolutionOrigin.ProviderModelPair
            },
            new TokenUsage
            {
                InputReported = new TokenMetric(11, MetricOrigin.Exact),
                UncachedInput = new TokenMetric(12, MetricOrigin.Derived),
                CacheRead = new TokenMetric(13, MetricOrigin.Inferred),
                CacheWrite = new TokenMetric(14, MetricOrigin.UserMapped),
                Output = new TokenMetric(15, MetricOrigin.Estimated),
                Reasoning = new TokenMetric(16, MetricOrigin.Exact),
                Tool = new TokenMetric(17, MetricOrigin.Derived),
                ReportedTotal = new TokenMetric(18, MetricOrigin.Inferred),
                NormalizedTotal = new TokenMetric(19, MetricOrigin.Estimated),
                CacheIncludedInInput = MetricInclusion.Separate,
                ReasoningIncludedInOutput = MetricInclusion.Included
            },
            CompletionState.Finalized,
            DataQuality.ExternalSync,
            "codex-v9",
            "fingerprint-9",
            9)
        {
            SessionId = "session-9",
            ParentSessionId = "parent-9",
            ProjectId = "project-9",
            ProjectPath = @"D:\Projects\AgenTally",
            ProjectRepositoryIdentityHash = new string('a', 64),
            TurnIdHash =
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ReportedCost = 1.25m,
            Currency = "USD"
        };

        await writer.CommitAsync(Batch(value), CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                agent_id, source_instance_id, source_entity_id, event_id, dedup_key,
                source_kind, occurred_at_unix_ms, imported_at_unix_ms,
                session_id, parent_session_id, turn_id_hash, project_id, project_path,
                project_repository_hash,
                raw_model, normalized_model, provider_id, model_resolution_origin,
                input_reported_value, input_reported_origin,
                uncached_input_value, uncached_input_origin,
                cache_read_value, cache_read_origin,
                cache_write_value, cache_write_origin,
                output_value, output_origin,
                reasoning_value, reasoning_origin,
                tool_value, tool_origin,
                reported_total_value, reported_total_origin,
                normalized_total_value, normalized_total_origin,
                cache_included_in_input, reasoning_included_in_output,
                completion_state, data_quality,
                reported_cost, currency, parser_version, source_fingerprint,
                source_revision
            FROM usage_events
            WHERE event_id = 'event-all-fields';
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        object[] actual = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetValue)
            .ToArray();
        object[] expected =
        [
            "codex", "codex:windows:test", "rollout:test", "event-all-fields",
            "codex:all-fields:1", (long)SourceKind.Jsonl,
            occurredAtUtc.ToUnixTimeMilliseconds(), importedAtUtc.ToUnixTimeMilliseconds(),
            "session-9", "parent-9",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new string('a', 24), @"D:\Projects\AgenTally",
            new string('a', 64),
            "raw-model", "normalized-model", "provider-id",
            (long)ModelResolutionOrigin.ProviderModelPair,
            11L, (long)MetricOrigin.Exact,
            12L, (long)MetricOrigin.Derived,
            13L, (long)MetricOrigin.Inferred,
            14L, (long)MetricOrigin.UserMapped,
            15L, (long)MetricOrigin.Estimated,
            16L, (long)MetricOrigin.Exact,
            17L, (long)MetricOrigin.Derived,
            18L, (long)MetricOrigin.Inferred,
            19L, (long)MetricOrigin.Estimated,
            (long)MetricInclusion.Separate,
            (long)MetricInclusion.Included,
            (long)CompletionState.Finalized,
            (long)DataQuality.ExternalSync,
            "1.25", "USD", "codex-v9", "fingerprint-9", 9L
        ];
        Assert.HasCount(expected.Length, actual);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(
                expected[index],
                actual[index],
                $"Persisted field {reader.GetName(index)} drifted at ordinal {index}.");
        }
    }

    [TestMethod]
    public async Task RecordFailure_CreatesStatusWithoutInventingCursor()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries, _) =
            await CreateServicesAsync(directory);

        await writer.RecordFailureAsync(
            Instance(),
            Entity(),
            "first failure",
            new DateTimeOffset(2026, 7, 16, 0, 5, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.IsNull(await writer.GetCursorAsync(
            "codex:windows:test",
            "rollout:test",
            CancellationToken.None));
        SourceStatusRow status = Assert.ContainsSingle(
            await queries.GetSourcesAsync(CancellationToken.None));
        Assert.IsNull(status.LastSuccessAtUtc);
        Assert.AreEqual("first failure", status.LastError);
    }

    [TestMethod]
    public async Task Initialize_RejectsV1WithoutDeletingLegacyTables()
    {
        using var directory = new TestTempDirectory();
        var connections = new SqliteConnectionFactory(
            new StorageOptions(directory.File("agentally.db")));

        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE usage_records (record_id TEXT PRIMARY KEY);
                INSERT INTO usage_records (record_id) VALUES ('keep-me');
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var writer = new SqliteUsageWriter(connections);
        await Assert.ThrowsExactlyAsync<LegacyDevelopmentSchemaException>(() =>
            writer.InitializeAsync(CancellationToken.None));

        await using SqliteConnection verify =
            await connections.OpenWriterAsync(CancellationToken.None);
        await using SqliteCommand verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            "SELECT record_id FROM usage_records WHERE record_id = 'keep-me';";
        Assert.AreEqual(
            "keep-me",
            await verifyCommand.ExecuteScalarAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Initialize_MigratesV8ProjectDuplicatesAcrossAgents()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        const string projectPath = @"C:\Projects\AgenTally";
        const string pathProjectId = "path-derived-project";
        string repositoryHash = new('c', 64);
        string repositoryProjectId = repositoryHash[..24];
        SourceInstanceDescriptor kimi = ScopedInstance(
            "kimi-code:windows:migration-project",
            "kimi-code",
            "Kimi migration project");
        SourceEntityDescriptor kimiEntity =
            ScopedEntity(kimi, "session:migration-path");
        UsageEvent kimiEvent = ScopedEvent(
            kimi,
            kimiEntity,
            "event-migration-path") with
        {
            SessionId = "session-migration-path",
            ProjectId = pathProjectId,
            ProjectPath = projectPath
        };
        await writer.CommitAsync(
            ScopedBatch(kimi, kimiEntity, kimiEvent) with
            {
                Sessions =
                [
                    new UsageSessionMetadata(
                        kimi.AgentId,
                        kimi.SourceInstanceId,
                        kimiEntity.SourceEntityId,
                        "session-migration-path",
                        SessionKind.Primary,
                        null,
                        null,
                        SessionRelationOrigin.None,
                        SessionRelationState.None,
                        ReplayState.Active,
                        CompatibilityLevel.FullyCompatible,
                        BatchCheckedAtUtc,
                        "fixture-v1")
                    {
                        ProjectId = pathProjectId,
                        ProjectPath = projectPath
                    }
                ]
            },
            CancellationToken.None);
        SourceInstanceDescriptor codex = ScopedInstance(
            "codex:windows:migration-project",
            "codex",
            "Codex migration project");
        SourceEntityDescriptor codexEntity =
            ScopedEntity(codex, "rollout:migration-repository");
        await writer.CommitAsync(
            ScopedBatch(
                codex,
                codexEntity,
                ScopedEvent(codex, codexEntity, "event-migration-repository") with
                {
                    ProjectId = repositoryProjectId,
                    ProjectPath = projectPath,
                    ProjectRepositoryIdentityHash = repositoryHash
                }),
            CancellationToken.None);

        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                UPDATE usage_events
                SET project_id = $path_project_id,
                    project_repository_hash = NULL
                WHERE event_id = 'event-migration-path';

                UPDATE usage_sessions
                SET project_id = $path_project_id,
                    project_repository_hash = NULL
                WHERE session_id = 'session-migration-path';

                DROP INDEX ix_usage_events_project_path;
                DROP INDEX ix_usage_sessions_project_path;
                ALTER TABLE usage_events DROP COLUMN route_model_id;
                ALTER TABLE usage_events DROP COLUMN model_display_name;
                ALTER TABLE usage_turns DROP COLUMN prompt_origin_turn_id_hash;
                DROP TABLE model_identity_catalog_state;
                DELETE FROM schema_migrations WHERE version IN (9, 10, 11, 12, 13, 14);
                PRAGMA user_version = 8;
                """;
            downgrade.Parameters.AddWithValue(
                "$path_project_id",
                pathProjectId);
            await downgrade.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 10),
                COUNT(*),
                COUNT(DISTINCT project_id),
                COUNT(project_repository_hash),
                SUM(normalized_total_value),
                MIN(project_id),
                MIN(project_repository_hash),
                (SELECT project_id FROM usage_sessions
                 WHERE session_id = 'session-migration-path'),
                (SELECT project_repository_hash FROM usage_sessions
                 WHERE session_id = 'session-migration-path'),
                (SELECT COUNT(*) FROM sqlite_schema
                 WHERE type = 'index'
                   AND name IN ('ix_usage_events_project_path',
                                'ix_usage_sessions_project_path'))
            FROM usage_events
            WHERE project_path = $project_path;
            """;
        command.Parameters.AddWithValue("$project_path", projectPath);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(2L, reader.GetInt64(2));
        Assert.AreEqual(1L, reader.GetInt64(3));
        Assert.AreEqual(2L, reader.GetInt64(4));
        Assert.AreEqual(2L, reader.GetInt64(5));
        Assert.AreEqual(repositoryProjectId, reader.GetString(6));
        Assert.AreEqual(repositoryHash, reader.GetString(7));
        Assert.AreEqual(repositoryProjectId, reader.GetString(8));
        Assert.AreEqual(repositoryHash, reader.GetString(9));
        Assert.AreEqual(2L, reader.GetInt64(10));
    }

    [TestMethod]
    public async Task Initialize_NewDatabaseUsesV14Schema()
    {
        using var directory = new TestTempDirectory();
        (_, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 14),
                (SELECT COUNT(*) FROM pragma_table_info('usage_events')
                 WHERE name IN ('turn_id_hash', 'price_catalog_version',
                                'estimated_cost_usd',
                                'output_price_context_multiplier',
                                'pricing_missing_categories',
                                'route_model_id',
                                'model_display_name')),
                (SELECT COUNT(*) FROM pragma_table_info('source_instances')
                 WHERE name IN ('compatibility_level', 'compatibility_code',
                                'requires_rescan',
                                'accepted_parser_version')),
                (SELECT COUNT(*) FROM sqlite_schema
                 WHERE type = 'table'
                   AND name IN ('usage_sessions', 'pricing_overrides',
                                'pricing_catalog_state', 'usage_turns',
                                'usage_event_tools',
                                'usage_turn_dispatches',
                                'usage_turn_attributions',
                                'model_identity_catalog_state')),
                (SELECT COUNT(*) FROM pragma_table_info('usage_sessions')
                 WHERE name IN ('session_role', 'agent_path_hash',
                                'agent_leaf_hash',
                                'project_repository_hash',
                                'session_name',
                                'session_name_updated_unix_ms')),
                (SELECT COUNT(*) FROM pragma_table_info('usage_events')
                 WHERE name = 'project_repository_hash'),
                (SELECT COUNT(*) FROM pragma_table_info('source_cursors')
                 WHERE name = 'event_revision_high_watermark'),
                (SELECT COUNT(*) FROM pragma_table_info('usage_turns')
                 WHERE name = 'prompt_origin_turn_id_hash'),
                (SELECT COUNT(*) FROM sqlite_schema
                 WHERE type = 'index'
                   AND name IN ('ix_usage_events_source_event',
                                'ix_usage_events_source_revision',
                                'ix_usage_events_project_path',
                                'ix_usage_sessions_project_path'));
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(7L, reader.GetInt64(2));
        Assert.AreEqual(4L, reader.GetInt64(3));
        Assert.AreEqual(8L, reader.GetInt64(4));
        Assert.AreEqual(6L, reader.GetInt64(5));
        Assert.AreEqual(1L, reader.GetInt64(6));
        Assert.AreEqual(1L, reader.GetInt64(7));
        Assert.AreEqual(1L, reader.GetInt64(8));
        Assert.AreEqual(4L, reader.GetInt64(9));
    }

    [TestMethod]
    public async Task Initialize_MigratesV10TurnOriginWithoutChangingUsage()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            Batch(TestEvents.Create(eventId: "event-before-v11")),
            CancellationToken.None);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE usage_turns DROP COLUMN prompt_origin_turn_id_hash;
                DROP TABLE model_identity_catalog_state;
                DELETE FROM schema_migrations WHERE version IN (11, 12, 13, 14);
                INSERT OR IGNORE INTO schema_migrations (
                    version,
                    applied_at_unix_ms
                ) VALUES (10, 0);
                PRAGMA user_version = 10;
                """;
            await downgrade.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 11),
                (SELECT COUNT(*) FROM pragma_table_info('usage_turns')
                 WHERE name = 'prompt_origin_turn_id_hash'),
                COUNT(*),
                SUM(normalized_total_value)
            FROM usage_events;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
        Assert.AreEqual(1L, reader.GetInt64(3));
        Assert.AreEqual(100L, reader.GetInt64(4));
    }

    [TestMethod]
    public async Task Initialize_MigratesV11ModelAliasesWithoutMergingContextVariants()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        UsageEvent prefixed = TestEvents.Create(
            eventId: "event-kimi-prefixed",
            dedupKey: "model:prefixed");
        UsageEvent bare = TestEvents.Create(
            eventId: "event-kimi-bare",
            dedupKey: "model:bare");
        UsageEvent fullContext = TestEvents.Create(
            eventId: "event-kimi-full-context",
            dedupKey: "model:full-context");
        await writer.CommitAsync(
            Batch([prefixed, bare, fullContext]),
            CancellationToken.None);

        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                UPDATE usage_events
                SET agent_id = CASE event_id
                        WHEN 'event-kimi-prefixed' THEN 'kimi-code'
                        WHEN 'event-kimi-bare' THEN 'zcode'
                        ELSE 'workbuddy'
                    END,
                    normalized_model = CASE event_id
                        WHEN 'event-kimi-prefixed' THEN 'kimi-code/k3-256k'
                        WHEN 'event-kimi-bare' THEN 'k3-256k'
                        ELSE 'kimi-k3'
                    END;

                INSERT INTO pricing_overrides (
                    normalized_model,
                    input_rate_usd_per_million,
                    cached_input_rate_usd_per_million,
                    cache_write_rate_usd_per_million,
                    output_rate_usd_per_million,
                    long_context_threshold_tokens,
                    long_context_input_multiplier,
                    long_context_output_multiplier,
                    updated_at_unix_ms
                ) VALUES (
                    'kimi-code/k3-256k',
                    '1', NULL, NULL, '2', NULL, '1', '1', 1
                );

                DROP TABLE model_identity_catalog_state;
                DELETE FROM schema_migrations WHERE version IN (12, 13, 14);
                INSERT OR IGNORE INTO schema_migrations (
                    version,
                    applied_at_unix_ms
                ) VALUES (11, 0);
                PRAGMA user_version = 11;
                """;
            await downgrade.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT COUNT(DISTINCT normalized_model) FROM usage_events),
                (SELECT COUNT(*) FROM usage_events
                 WHERE normalized_model = 'kimi-k3-256k'),
                (SELECT COUNT(*) FROM usage_events
                 WHERE normalized_model = 'kimi-k3'),
                (SELECT COUNT(*) FROM pricing_overrides
                 WHERE normalized_model = 'kimi-k3-256k'),
                (SELECT COUNT(*) FROM pricing_overrides
                 WHERE normalized_model = 'kimi-code/k3-256k');
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual(2L, reader.GetInt64(1));
        Assert.AreEqual(2L, reader.GetInt64(2));
        Assert.AreEqual(1L, reader.GetInt64(3));
        Assert.AreEqual(1L, reader.GetInt64(4));
        Assert.AreEqual(0L, reader.GetInt64(5));
    }

    [TestMethod]
    public async Task Initialize_MigratesV12ConfirmedAliasesWithoutMergingVariants()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        UsageEvent[] events =
        [
            TestEvents.Create("event-qoder-route", "model:qoder-route"),
            TestEvents.Create("event-qwen-model", "model:qwen-model"),
            TestEvents.Create("event-kimi-work-route", "model:kimi-work-route"),
            TestEvents.Create("event-kimi-capability", "model:kimi-capability"),
            TestEvents.Create("event-deepseek-display", "model:deepseek-display"),
            TestEvents.Create("event-deepseek-model", "model:deepseek-model")
        ];
        await writer.CommitAsync(Batch(events), CancellationToken.None);

        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                UPDATE usage_events
                SET agent_id = CASE event_id
                        WHEN 'event-qoder-route' THEN 'qoder-cn'
                        WHEN 'event-qwen-model' THEN 'qwen-code'
                        WHEN 'event-kimi-work-route' THEN 'kimi-work'
                        WHEN 'event-kimi-capability' THEN 'workbuddy'
                        ELSE 'workbuddy'
                    END,
                    normalized_model = CASE event_id
                        WHEN 'event-qoder-route' THEN 'qmodel_38max'
                        WHEN 'event-qwen-model' THEN 'qwen3.8-max'
                        WHEN 'event-kimi-work-route' THEN 'k2d6-agent'
                        WHEN 'event-kimi-capability' THEN 'kimi-k2.6'
                        WHEN 'event-deepseek-display' THEN 'deepseek-v4 pro'
                        ELSE 'deepseek-v4-pro'
                    END;

                INSERT INTO pricing_overrides (
                    normalized_model,
                    input_rate_usd_per_million,
                    cached_input_rate_usd_per_million,
                    cache_write_rate_usd_per_million,
                    output_rate_usd_per_million,
                    long_context_threshold_tokens,
                    long_context_input_multiplier,
                    long_context_output_multiplier,
                    updated_at_unix_ms
                ) VALUES (
                    'qmodel_38max',
                    '1', NULL, NULL, '2', NULL, '1', '1', 1
                );

                DROP TABLE model_identity_catalog_state;
                DELETE FROM schema_migrations WHERE version IN (13, 14);
                INSERT OR IGNORE INTO schema_migrations (
                    version,
                    applied_at_unix_ms
                ) VALUES (12, 0);
                PRAGMA user_version = 12;
                """;
            await downgrade.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT COUNT(*) FROM usage_events
                 WHERE normalized_model = 'qwen3.8-max'),
                (SELECT COUNT(*) FROM usage_events
                 WHERE normalized_model = 'kimi-k2.6-agent'),
                (SELECT COUNT(*) FROM usage_events
                 WHERE normalized_model = 'kimi-k2.6'),
                (SELECT COUNT(*) FROM usage_events
                 WHERE normalized_model = 'deepseek-v4-pro'),
                (SELECT COUNT(*) FROM usage_events
                 WHERE normalized_model IN (
                     'qmodel_38max', 'k2d6-agent', 'deepseek-v4 pro')),
                (SELECT COUNT(*) FROM pricing_overrides
                 WHERE normalized_model = 'qwen3.8-max'),
                (SELECT COUNT(*) FROM pricing_overrides
                 WHERE normalized_model = 'qmodel_38max');
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual(2L, reader.GetInt64(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
        Assert.AreEqual(1L, reader.GetInt64(3));
        Assert.AreEqual(2L, reader.GetInt64(4));
        Assert.AreEqual(0L, reader.GetInt64(5));
        Assert.AreEqual(1L, reader.GetInt64(6));
        Assert.AreEqual(0L, reader.GetInt64(7));
    }

    [TestMethod]
    public async Task Initialize_AppliesIdentityCatalogUpgradeWithoutSchemaBump()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            Batch(TestEvents.Create(
                eventId: "event-model-catalog-upgrade",
                dedupKey: "model:catalog-upgrade")),
            CancellationToken.None);

        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand simulateOldCatalog =
                connection.CreateCommand();
            simulateOldCatalog.CommandText = """
                UPDATE usage_events
                SET raw_model = 'openai/gpt-4o',
                    normalized_model = 'openai/gpt-4o',
                    price_catalog_version = 'frozen-catalog',
                    price_rule_id = 'frozen-rule',
                    input_rate_usd_per_million = '1',
                    output_rate_usd_per_million = '2',
                    price_context_multiplier = '1',
                    output_price_context_multiplier = '1',
                    estimated_cost_usd = '0.0042',
                    pricing_status = 1
                WHERE event_id = 'event-model-catalog-upgrade';

                INSERT INTO pricing_overrides (
                    normalized_model,
                    input_rate_usd_per_million,
                    cached_input_rate_usd_per_million,
                    cache_write_rate_usd_per_million,
                    output_rate_usd_per_million,
                    long_context_threshold_tokens,
                    long_context_input_multiplier,
                    long_context_output_multiplier,
                    updated_at_unix_ms
                ) VALUES (
                    'openai/gpt-4o',
                    '3', NULL, NULL, '4', NULL, '1', '1', 1
                );

                UPDATE model_identity_catalog_state
                SET catalog_version = 'models-dev-stale';
                """;
            await simulateOldCatalog.ExecuteNonQueryAsync(
                CancellationToken.None);
        }

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = verify.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                raw_model,
                normalized_model,
                price_catalog_version,
                price_rule_id,
                estimated_cost_usd,
                (SELECT COUNT(*) FROM pricing_overrides
                 WHERE normalized_model = 'gpt-4o'),
                (SELECT COUNT(*) FROM pricing_overrides
                 WHERE normalized_model = 'openai/gpt-4o'),
                (SELECT catalog_version
                 FROM model_identity_catalog_state
                 WHERE singleton_id = 1),
                (SELECT catalog_version
                 FROM pricing_catalog_state
                 WHERE singleton_id = 1)
            FROM usage_events
            WHERE event_id = 'event-model-catalog-upgrade';
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual("openai/gpt-4o", reader.GetString(1));
        Assert.AreEqual("gpt-4o", reader.GetString(2));
        Assert.AreEqual("frozen-catalog", reader.GetString(3));
        Assert.AreEqual("frozen-rule", reader.GetString(4));
        Assert.AreEqual("0.0042", reader.GetString(5));
        Assert.AreEqual(1L, reader.GetInt64(6));
        Assert.AreEqual(0L, reader.GetInt64(7));
        Assert.AreEqual(
            ModelIdentityCanonicalizer.CatalogVersion,
            reader.GetString(8));
        Assert.AreEqual(
            OfflinePriceCatalog.CurrentVersion,
            reader.GetString(9));
    }

    [TestMethod]
    public async Task Initialize_MigratesV5WithoutChangingUsageOrProjects()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        const string projectPath = @"D:\Projects\AgenTally";
        string repositoryHash = new('b', 64);
        UsageEvent value = TestEvents.Create(eventId: "event-before-v6") with
        {
            SessionId = "session-before-v6",
            ProjectId = repositoryHash[..24],
            ProjectPath = projectPath,
            ProjectRepositoryIdentityHash = repositoryHash
        };
        UsageEventBatch batch = Batch(value) with
        {
            Sessions =
            [
                Session("session-before-v6", SessionKind.Primary) with
                {
                    ProjectId = repositoryHash[..24],
                    ProjectPath = projectPath,
                    ProjectRepositoryIdentityHash = repositoryHash
                }
            ]
        };
        await writer.CommitAsync(batch, CancellationToken.None);
        await ConvertCurrentFixtureToV5Async(connections);

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT normalized_total_value FROM usage_events
                 WHERE event_id = 'event-before-v6'),
                (SELECT project_id FROM usage_events
                 WHERE event_id = 'event-before-v6'),
                (SELECT project_path FROM usage_events
                 WHERE event_id = 'event-before-v6'),
                (SELECT project_repository_hash FROM usage_events
                 WHERE event_id = 'event-before-v6'),
                (SELECT project_repository_hash FROM usage_sessions
                 WHERE session_id = 'session-before-v6'),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 10);
            """;
        await using SqliteDataReader reader =
            await verifyCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual(100L, reader.GetInt64(1));
        Assert.AreEqual(repositoryHash[..24], reader.GetString(2));
        Assert.AreEqual(projectPath, reader.GetString(3));
        Assert.IsTrue(reader.IsDBNull(4));
        Assert.IsTrue(reader.IsDBNull(5));
        Assert.AreEqual(1L, reader.GetInt64(6));
    }

    [TestMethod]
    public async Task Initialize_MigratesV4WithoutChangingEventsPricesOrOverrides()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            Batch(TestEvents.Create(eventId: "event-before-v5")),
            CancellationToken.None);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE usage_events
                SET price_catalog_version = 'immutable-catalog',
                    price_rule_id = 'immutable-rule',
                    input_rate_usd_per_million = '1.25',
                    output_rate_usd_per_million = '9.5',
                    price_context_multiplier = '1',
                    estimated_cost_usd = '0.0042',
                    pricing_status = 1
                WHERE event_id = 'event-before-v5';

                INSERT INTO pricing_overrides (
                    normalized_model,
                    input_rate_usd_per_million,
                    cached_input_rate_usd_per_million,
                    cache_write_rate_usd_per_million,
                    output_rate_usd_per_million,
                    long_context_threshold_tokens,
                    long_context_input_multiplier,
                    long_context_output_multiplier,
                    updated_at_unix_ms
                ) VALUES (
                    'custom-model', '2', NULL, NULL, '8', NULL, '1', '1', 1
                );
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        await ConvertCurrentFixtureToV4Async(connections);

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                (SELECT user_version FROM pragma_user_version),
                (SELECT COUNT(*) FROM usage_events),
                (SELECT normalized_total_value FROM usage_events
                 WHERE event_id = 'event-before-v5'),
                (SELECT price_catalog_version FROM usage_events
                 WHERE event_id = 'event-before-v5'),
                (SELECT price_rule_id FROM usage_events
                 WHERE event_id = 'event-before-v5'),
                (SELECT estimated_cost_usd FROM usage_events
                 WHERE event_id = 'event-before-v5'),
                (SELECT COUNT(*) FROM pricing_overrides
                 WHERE normalized_model = 'custom-model'),
                (SELECT COUNT(*) FROM sqlite_schema
                 WHERE type = 'table'
                   AND name IN ('usage_turns', 'usage_event_tools',
                                'usage_turn_dispatches',
                                'usage_turn_attributions'));
            """;
        await using SqliteDataReader reader =
            await verifyCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(14L, reader.GetInt64(0));
        Assert.AreEqual(1L, reader.GetInt64(1));
        Assert.AreEqual(100L, reader.GetInt64(2));
        Assert.AreEqual("immutable-catalog", reader.GetString(3));
        Assert.AreEqual("immutable-rule", reader.GetString(4));
        Assert.AreEqual("0.0042", reader.GetString(5));
        Assert.AreEqual(1L, reader.GetInt64(6));
        Assert.AreEqual(4L, reader.GetInt64(7));
    }

    [TestMethod]
    public async Task Initialize_ExtendsEarlierV4PricingShapeWithoutLosingEvents()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        await writer.CommitAsync(
            Batch(TestEvents.Create(eventId: "event-before-v4-extension")),
            CancellationToken.None);
        await ConvertCurrentFixtureToV4Async(connections);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ix_usage_events_source_event;
                DROP INDEX ix_usage_events_source_revision;
                DROP TABLE pricing_catalog_state;
                DROP TABLE pricing_overrides;
                ALTER TABLE source_instances
                DROP COLUMN accepted_parser_version;
                ALTER TABLE usage_events
                DROP COLUMN output_price_context_multiplier;
                ALTER TABLE usage_events
                DROP COLUMN pricing_missing_categories;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM usage_events),
                (SELECT COUNT(*) FROM pragma_table_info('usage_events')
                 WHERE name IN ('output_price_context_multiplier',
                                'pricing_missing_categories')),
                (SELECT COUNT(*) FROM sqlite_schema
                 WHERE type = 'table'
                   AND name IN ('pricing_overrides', 'pricing_catalog_state')),
                (SELECT COUNT(*) FROM sqlite_schema
                 WHERE type = 'index'
                   AND name IN ('ix_usage_events_source_event',
                                'ix_usage_events_source_revision')),
                (SELECT COUNT(*) FROM pragma_table_info('source_instances')
                 WHERE name = 'accepted_parser_version');
            """;
        await using SqliteDataReader reader =
            await verifyCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(1L, reader.GetInt64(0));
        Assert.AreEqual(2L, reader.GetInt64(1));
        Assert.AreEqual(2L, reader.GetInt64(2));
        Assert.AreEqual(2L, reader.GetInt64(3));
        Assert.AreEqual(1L, reader.GetInt64(4));
    }

    [TestMethod]
    public async Task Initialize_MigratesV2WithoutChangingEventsOrCursors()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, _, SqliteConnectionFactory connections) =
            await CreateServicesAsync(directory);
        UsageEvent oldEvent = TestEvents.Create() with
        {
            SessionId = "session-before-migration",
            ProjectId = "project-before-migration"
        };
        await writer.CommitAsync(
            Batch(oldEvent, cursorJson: "cursor-before-migration"),
            CancellationToken.None);
        await ConvertCurrentFixtureToV2Async(connections);

        await writer.InitializeAsync(CancellationToken.None);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM usage_events),
                (SELECT SUM(normalized_total_value) FROM usage_events),
                (SELECT session_id FROM usage_events LIMIT 1),
                (SELECT source_entity_id FROM usage_events LIMIT 1),
                (SELECT project_path FROM usage_events LIMIT 1),
                (SELECT turn_id_hash FROM usage_events LIMIT 1),
                (SELECT pricing_status FROM usage_events LIMIT 1),
                (SELECT cursor_json FROM source_cursors LIMIT 1),
                (SELECT source_entity_id FROM source_cursors LIMIT 1),
                (SELECT COUNT(*) FROM source_instances),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 3),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 4),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 5),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 6),
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 10),
                (SELECT user_version FROM pragma_user_version);
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(1L, reader.GetInt64(0));
        Assert.AreEqual(100L, reader.GetInt64(1));
        Assert.AreEqual("session-before-migration", reader.GetString(2));
        Assert.AreEqual("rollout:test", reader.GetString(3));
        Assert.IsTrue(reader.IsDBNull(4));
        Assert.IsTrue(reader.IsDBNull(5));
        Assert.AreEqual(0L, reader.GetInt64(6));
        Assert.AreEqual("cursor-before-migration", reader.GetString(7));
        Assert.AreEqual("rollout:test", reader.GetString(8));
        Assert.AreEqual(1L, reader.GetInt64(9));
        Assert.AreEqual(1L, reader.GetInt64(10));
        Assert.AreEqual(1L, reader.GetInt64(11));
        Assert.AreEqual(1L, reader.GetInt64(12));
        Assert.AreEqual(1L, reader.GetInt64(13));
        Assert.AreEqual(1L, reader.GetInt64(14));
        Assert.AreEqual(14L, reader.GetInt64(15));
    }

    private static readonly DateTimeOffset BatchCheckedAtUtc =
        new(2026, 7, 16, 0, 1, 0, TimeSpan.Zero);

    private static async Task<(
        SqliteUsageWriter Writer,
        SqliteUsageQueryService Queries,
        SqliteConnectionFactory Connections)> CreateServicesAsync(
            TestTempDirectory directory)
    {
        var connections = new SqliteConnectionFactory(
            new StorageOptions(directory.File("agentally.db")));
        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);
        return (writer, new SqliteUsageQueryService(connections), connections);
    }

    private static async Task ConvertCurrentFixtureToV2Async(
        SqliteConnectionFactory connections)
    {
        await using SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None);
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        int version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(CancellationToken.None));
        if (version == 2)
        {
            return;
        }

        Assert.AreEqual(14, version);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX ix_usage_turn_attributions_origin;
            DROP INDEX ix_usage_turn_dispatches_target;
            DROP INDEX ix_usage_turns_started;
            DROP TABLE usage_turn_attributions;
            DROP TABLE usage_turn_dispatches;
            DROP TABLE usage_event_tools;
            DROP TABLE usage_turns;
            DROP INDEX ix_usage_events_session_occurred;
            DROP INDEX ix_usage_events_project_occurred;
            DROP INDEX ix_usage_events_turn;
            DROP INDEX ix_usage_events_source_event;
            DROP INDEX ix_usage_events_source_revision;
            DROP INDEX ix_usage_events_project_path;
            DROP TABLE model_identity_catalog_state;
            DROP TABLE pricing_catalog_state;
            DROP TABLE pricing_overrides;
            ALTER TABLE usage_sessions DROP COLUMN session_role;
            ALTER TABLE usage_sessions DROP COLUMN agent_path_hash;
            ALTER TABLE usage_sessions DROP COLUMN agent_leaf_hash;
            DROP TABLE usage_sessions;
            ALTER TABLE source_instances DROP COLUMN compatibility_level;
            ALTER TABLE source_instances DROP COLUMN compatibility_code;
            ALTER TABLE source_instances DROP COLUMN requires_rescan;
            ALTER TABLE source_instances DROP COLUMN accepted_parser_version;
            ALTER TABLE source_cursors DROP COLUMN event_revision_high_watermark;
            ALTER TABLE usage_events DROP COLUMN turn_id_hash;
            ALTER TABLE usage_events DROP COLUMN price_catalog_version;
            ALTER TABLE usage_events DROP COLUMN price_rule_id;
            ALTER TABLE usage_events DROP COLUMN input_rate_usd_per_million;
            ALTER TABLE usage_events DROP COLUMN cached_input_rate_usd_per_million;
            ALTER TABLE usage_events DROP COLUMN cache_write_rate_usd_per_million;
            ALTER TABLE usage_events DROP COLUMN output_rate_usd_per_million;
            ALTER TABLE usage_events DROP COLUMN price_context_multiplier;
            ALTER TABLE usage_events DROP COLUMN output_price_context_multiplier;
            ALTER TABLE usage_events DROP COLUMN estimated_cost_usd;
            ALTER TABLE usage_events DROP COLUMN pricing_status;
            ALTER TABLE usage_events DROP COLUMN pricing_missing_categories;
            ALTER TABLE usage_events DROP COLUMN project_repository_hash;
            ALTER TABLE usage_events DROP COLUMN route_model_id;
            ALTER TABLE usage_events DROP COLUMN model_display_name;
            ALTER TABLE usage_events DROP COLUMN project_path;
            DELETE FROM schema_migrations;
            INSERT INTO schema_migrations (version, applied_at_unix_ms)
            VALUES (2, 0);
            PRAGMA user_version = 2;
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ConvertCurrentFixtureToV5Async(
        SqliteConnectionFactory connections)
    {
        await using SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None);
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(
            14,
            Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(CancellationToken.None)));

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE source_cursors DROP COLUMN event_revision_high_watermark;
            ALTER TABLE usage_events DROP COLUMN project_repository_hash;
            ALTER TABLE usage_events DROP COLUMN route_model_id;
            ALTER TABLE usage_events DROP COLUMN model_display_name;
            ALTER TABLE usage_sessions DROP COLUMN session_name;
            ALTER TABLE usage_sessions DROP COLUMN session_name_updated_unix_ms;
            ALTER TABLE usage_sessions DROP COLUMN project_repository_hash;
            ALTER TABLE usage_turns DROP COLUMN prompt_origin_turn_id_hash;
            DROP TABLE model_identity_catalog_state;
            DELETE FROM schema_migrations WHERE version IN (6, 7, 8, 9, 10, 11, 12, 13, 14);
            PRAGMA user_version = 5;
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ConvertCurrentFixtureToV4Async(
        SqliteConnectionFactory connections)
    {
        await using SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None);
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(
            14,
            Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(CancellationToken.None)));

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE source_cursors DROP COLUMN event_revision_high_watermark;
            DROP INDEX ix_usage_turn_attributions_origin;
            DROP INDEX ix_usage_turn_dispatches_target;
            DROP INDEX ix_usage_turns_started;
            DROP TABLE usage_turn_attributions;
            DROP TABLE usage_turn_dispatches;
            DROP TABLE usage_event_tools;
            DROP TABLE usage_turns;
            ALTER TABLE usage_sessions DROP COLUMN session_role;
            ALTER TABLE usage_sessions DROP COLUMN agent_path_hash;
            ALTER TABLE usage_sessions DROP COLUMN agent_leaf_hash;
            ALTER TABLE usage_sessions DROP COLUMN session_name;
            ALTER TABLE usage_sessions DROP COLUMN session_name_updated_unix_ms;
            ALTER TABLE usage_sessions DROP COLUMN project_repository_hash;
            ALTER TABLE usage_events DROP COLUMN project_repository_hash;
            ALTER TABLE usage_events DROP COLUMN route_model_id;
            ALTER TABLE usage_events DROP COLUMN model_display_name;
            DROP TABLE model_identity_catalog_state;
            DELETE FROM schema_migrations WHERE version IN (5, 6, 7, 8, 9, 10, 11, 12, 13, 14);
            PRAGMA user_version = 4;
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static UsageEventBatch Batch(
        UsageEvent value,
        WriteIntent intent = WriteIntent.Normal,
        string cursorJson = "cursor-1") =>
        Batch([value], intent, cursorJson);

    private static UsageEventBatch Batch(
        IReadOnlyList<UsageEvent> values,
        WriteIntent intent = WriteIntent.Normal,
        string cursorJson = "cursor-1") =>
        new(
            Instance(),
            Entity(),
            cursorJson,
            values[0].SourceFingerprint,
            values[0].ParserVersion,
            BatchCheckedAtUtc,
            values,
            intent);

    private static UsageEventBatch SessionOnlyBatch(
        UsageSessionMetadata session) => new(
            Instance(),
            Entity(),
            "cursor-session",
            "fixture-1",
            session.ParserVersion,
            BatchCheckedAtUtc,
            [])
        {
            Sessions = [session]
        };

    private static UsageSessionMetadata Session(
        string sessionId,
        SessionKind kind,
        string? directParentSessionId = null,
        string? forkedFromSessionId = null,
        CompatibilityLevel compatibilityLevel =
            CompatibilityLevel.FullyCompatible) => new(
            "codex",
            "codex:windows:test",
            "rollout:test",
            sessionId,
            kind,
            directParentSessionId,
            forkedFromSessionId,
            directParentSessionId is null
                ? SessionRelationOrigin.None
                : SessionRelationOrigin.TopLevelParentThreadId,
            directParentSessionId is null
                ? SessionRelationState.None
                : SessionRelationState.Confirmed,
            ReplayState.Active,
            compatibilityLevel,
            BatchCheckedAtUtc,
            "codex-v1");

    private static SourceInstanceDescriptor Instance() =>
        new(
            "codex:windows:test",
            "codex",
            SourceKind.Jsonl,
            "Codex test",
            "C:\\codex");

    private static SourceEntityDescriptor Entity() =>
        new(
            "codex:windows:test",
            "rollout:test",
            "C:\\codex\\rollout-test.jsonl");

    private static SourceInstanceDescriptor ScopedInstance(
        string sourceInstanceId,
        string agentId,
        string displayName) =>
        new(
            sourceInstanceId,
            agentId,
            SourceKind.Jsonl,
            displayName,
            $"C:\\sources\\{sourceInstanceId.Replace(':', '-')}");

    private static SourceEntityDescriptor ScopedEntity(
        SourceInstanceDescriptor instance,
        string sourceEntityId) =>
        new(
            instance.SourceInstanceId,
            sourceEntityId,
            $"{instance.RootPath}\\{sourceEntityId.Replace(':', '-')}.jsonl");

    private static UsageEvent ScopedEvent(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        string eventId,
        string parserVersion = "fixture-v1",
        string? dedupKey = null) =>
        new(
            instance.AgentId,
            instance.SourceInstanceId,
            entity.SourceEntityId,
            eventId,
            dedupKey ?? $"{entity.SourceEntityId}:token:1",
            instance.SourceKind,
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 16, 0, 1, 0, TimeSpan.Zero),
            new ModelIdentity(),
            new TokenUsage
            {
                NormalizedTotal = new TokenMetric(1, MetricOrigin.Derived)
            },
            CompletionState.Completed,
            DataQuality.Derived,
            parserVersion,
            "fixture-1",
            1);

    private static UsageEventBatch ScopedBatch(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        UsageEvent value,
        string cursorJson = "cursor-1") =>
        new(
            instance,
            entity,
            cursorJson,
            value.SourceFingerprint,
            value.ParserVersion,
            BatchCheckedAtUtc,
            [value]);

    private static UsageEventBatch EmptyScopedBatch(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        string parserVersion,
        string cursorJson) =>
        new(
            instance,
            entity,
            cursorJson,
            new string('f', 64),
            parserVersion,
            BatchCheckedAtUtc,
            []);

    private static UsageFilter AllDay() =>
        new(
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));

    private static UsageEvent CreateEvent(
        string parserVersion,
        long normalizedTotal) =>
        new(
            "codex",
            "codex:windows:test",
            "rollout:test",
            "event-1",
            "codex:thread-1:1",
            SourceKind.Jsonl,
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 16, 0, 1, 0, TimeSpan.Zero),
            new ModelIdentity
            {
                RawModel = "gpt-test",
                NormalizedModel = "gpt-test",
                ProviderId = "openai",
                ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
            },
            new TokenUsage
            {
                NormalizedTotal = new TokenMetric(normalizedTotal, MetricOrigin.Derived)
            },
            CompletionState.Completed,
            DataQuality.Derived,
            parserVersion,
            "fixture-1",
            1);

    private static async Task<bool> EventExistsAsync(
        SqliteConnectionFactory connections,
        string eventId)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM usage_events
                WHERE event_id = $event_id
            );
            """;
        command.Parameters.AddWithValue("$event_id", eventId);

        object? result = await command.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt32(result) == 1;
    }

    private static async Task<bool> SessionExistsAsync(
        SqliteConnectionFactory connections,
        string sessionId)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM usage_sessions
                WHERE session_id = $session_id
            );
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        object? result = await command.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt32(result) == 1;
    }

    private static async Task<long> CountEventsAsync(
        SqliteConnectionFactory connections,
        string agentId,
        string sourceInstanceId)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM usage_events
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<long> CountInstancesAsync(
        SqliteConnectionFactory connections,
        string sourceInstanceId)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM source_instances
            WHERE source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task DeleteCursorsAsync(
        SqliteConnectionFactory connections,
        string sourceInstanceId)
    {
        await using SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM source_cursors
            WHERE source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task DeleteEventsAsync(
        SqliteConnectionFactory connections,
        string agentId,
        string sourceInstanceId)
    {
        await using SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM usage_events
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
