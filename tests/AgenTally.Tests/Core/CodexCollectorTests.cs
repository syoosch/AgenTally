using System.IO;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CodexCollectorTests
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    public async Task CollectAsync_EnrichesSessionWithSourceProvidedName()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(
            codexHome,
            "sessions",
            "rollout-named.jsonl");
        await CopyBasicFixtureAsync(path);
        DateTimeOffset updatedAt =
            new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        using var collector = new CodexCollector(
            codexHome,
            sessionNames: new FakeSessionNameSource(
                [new UsageSessionNameMetadata(
                    "thread-1",
                    "来源会话名",
                    updatedAt)]));
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        CollectedBatch batch = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport)));

        UsageSessionMetadata session = Assert.ContainsSingle(batch.Sessions);
        Assert.AreEqual("来源会话名", session.SessionName);
        Assert.AreEqual(updatedAt, session.SessionNameUpdatedAtUtc);
    }

    [TestMethod]
    public async Task CollectAsync_YieldsZeroEventBatchAndAdvancesPastKnownLines()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(
            codexHome,
            "sessions",
            "rollout-irrelevant.jsonl");
        await WriteAsync(path, "{}\n{}\n");
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        CollectedBatch batch = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport)));

        Assert.IsEmpty(batch.Events);
        CodexCursor cursor = CodexCursor.DeserializeOrStart(
            batch.NextCursorJson,
            out CollectorDiagnostic? diagnostic);
        Assert.IsNull(diagnostic);
        Assert.AreEqual(2L, cursor.Jsonl.LineNumber);
        Assert.IsGreaterThan(0L, cursor.Jsonl.ByteOffset);
    }

    [TestMethod]
    public async Task CollectAsync_ReadsAtMostTwoHundredCompleteLinesPerBatch()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(
            codexHome,
            "sessions",
            "rollout-bounded.jsonl");
        await WriteAsync(path, string.Concat(Enumerable.Repeat("{}\n", 201)));
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        IReadOnlyList<CollectedBatch> batches = await CollectAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport));

        Assert.HasCount(2, batches);
        CollectionAssert.AreEqual(
            new long[] { 200, 201 },
            batches.Select(batch => CodexCursor.DeserializeOrStart(
                    batch.NextCursorJson,
                    out _)
                .Jsonl.LineNumber)
                .ToArray());
        Assert.IsTrue(batches.All(batch => batch.Events.Count <= 200));
    }

    [TestMethod]
    public async Task CollectAsync_MalformedCursorResetsWithFixedPrivacySafeDiagnostic()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(
            codexHome,
            "sessions",
            "rollout-basic.jsonl");
        await CopyBasicFixtureAsync(path);
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        const string privatePath = @"C:\private\project";
        string maliciousCursor = JsonSerializer.Serialize(new
        {
            jsonl = JsonlCursor.Start,
            state = new
            {
                threadId = "thread-1",
                projectId = privatePath,
                tokenEventIndex = -1
            }
        });
        var storedCursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            maliciousCursor,
            "fixture-fingerprint",
            CodexRolloutParser.CurrentParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);

        IReadOnlyList<CollectedBatch> batches = await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                storedCursor,
                CollectionReason.StartupImport));

        Assert.AreEqual(2, batches.Sum(batch => batch.Events.Count));
        CollectorDiagnostic diagnostic = Assert.ContainsSingle(
            batches.SelectMany(batch => batch.Diagnostics)
                .Where(item => item.Code == "codex.invalid_cursor"));
        Assert.AreEqual(
            "Codex collection cursor was invalid and has been reset.",
            diagnostic.Message);
        Assert.DoesNotContain(privatePath, diagnostic.Message);
        Assert.IsTrue(batches
            .SelectMany(batch => batch.Events)
            .All(value => value.ProjectId is { Length: 24 } &&
                !value.ProjectId.Contains("private", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CursorDeserializer_RejectsInvalidNestedStateAndAcceptsValidState()
    {
        string longValue = new('x', 1025);
        string[] invalidCursors =
        [
            "{}",
            JsonSerializer.Serialize(new { jsonl = (object?)null, state = new { } }),
            JsonSerializer.Serialize(new { jsonl = JsonlCursor.Start, state = (object?)null }),
            JsonSerializer.Serialize(new
            {
                jsonl = new JsonlCursor(-1, string.Empty, 0, string.Empty),
                state = new { }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new { tokenEventIndex = -1 }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new
                {
                    previousCumulative = new
                    {
                        input = -1,
                        cachedInput = (long?)null,
                        output = (long?)null,
                        reasoning = (long?)null,
                        cacheWrite = (long?)null,
                        total = (long?)null
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new { projectId = @"C:\private\project" }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new { projectId = "ABCDEF0123456789ABCDEF01" }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new { threadId = longValue }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new { replayTargetSessionId = longValue }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new { isReplayTargetContextPending = true }
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = JsonlCursor.Start,
                state = new { },
                unknownPrivateField = "must-not-be-accepted"
            }),
            JsonSerializer.Serialize(new
            {
                jsonl = new JsonlCursor(
                    1,
                    string.Empty,
                    1,
                    new string('a', 64)),
                state = new { tokenEventIndex = 2 }
            })
        ];

        foreach (string json in invalidCursors)
        {
            CodexCursor cursor = CodexCursor.DeserializeOrStart(
                json,
                out CollectorDiagnostic? diagnostic);

            Assert.AreEqual(CodexCursor.Start, cursor);
            Assert.IsNotNull(diagnostic);
            Assert.AreEqual("codex.invalid_cursor", diagnostic.Code);
        }

        var expected = new CodexCursor(
            new JsonlCursor(4, string.Empty, 4, new string('a', 64)),
            new CodexParseState(
                ThreadId: "thread-valid",
                ParentSessionId: "parent-valid",
                CurrentRawModel: "provider/model",
                CurrentProviderId: "provider",
                ProjectId: "0123456789abcdef01234567",
                PreviousCumulative: new CodexTokenCounters(1, 0, 2, 1, 0, 3),
                TokenEventIndex: 4,
                IsHistoryReplay: false,
                ReplayTarget: new CodexReplayTargetState(
                    "thread-valid",
                    null,
                    null,
                    SessionKind.Primary,
                    SessionRelationOrigin.None,
                    SessionRelationState.None,
                    CompatibilityLevel.FullyCompatible,
                    SessionRole.Main,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null)));

        string serialized = expected.Serialize();
        CodexCursor restored = CodexCursor.DeserializeOrStart(
            serialized,
            out CollectorDiagnostic? validDiagnostic);

        Assert.IsNull(validDiagnostic);
        Assert.AreEqual(expected, restored);
    }

    [TestMethod]
    public async Task CollectAsync_PersistsForkReplayTargetAcrossIncrementalAppend()
    {
        const string target = "019fbba4-1b1a-7560-b2d0-006521985379";
        const string origin = "019fb899-9af0-7000-8000-000000000001";
        const string historicalTurn = "019fb89a-0000-7000-8000-000000000002";
        const string activeTurn = "019fbba5-18b0-7000-8000-000000000003";
        const string laterTurn = "019fbbb0-0000-7000-8000-000000000004";
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(
            codexHome,
            "sessions",
            "rollout-fork-continuation.jsonl");
        string[] prefix =
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T04:45:22Z",
                type = "session_meta",
                payload = new
                {
                    id = target,
                    forked_from_id = origin,
                    cwd = @"D:\Projects\codex\faker"
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T04:45:22Z",
                type = "session_meta",
                payload = new
                {
                    id = origin,
                    cwd = @"D:\Projects\codex\faker"
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T04:45:25Z",
                type = "event_msg",
                payload = new { type = "task_started", turn_id = historicalTurn }
            }),
            "{\"timestamp\":\"2026-08-01T04:45:26Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120},\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40,\"output_tokens\":20,\"total_tokens\":120}}}}",
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T04:45:27Z",
                type = "response_item",
                payload = new
                {
                    type = "function_call",
                    name = "spawn_agent",
                    call_id = "replayed-call",
                    arguments = "{\"task_name\":\"replayed-worker\"}"
                }
            }),
            "{\"timestamp\":\"2026-08-01T04:45:31Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\"}}"
        ];
        await WriteAsync(path, string.Join('\n', prefix) + "\n");
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        CollectedBatch initial = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport)));
        Assert.IsEmpty(initial.Events);
        Assert.IsEmpty(initial.Turns);
        Assert.IsEmpty(initial.Dispatches);
        Assert.AreEqual(1L, initial.EventRevisionHighWatermark);
        CodexCursor cursor = CodexCursor.DeserializeOrStart(
            initial.NextCursorJson,
            out CollectorDiagnostic? cursorDiagnostic);
        Assert.IsNull(cursorDiagnostic);
        Assert.AreEqual(origin, cursor.State.ThreadId);
        Assert.AreEqual(target, cursor.State.ReplayTarget!.SessionId);
        Assert.IsTrue(cursor.State.IsHistoryReplay);

        string[] suffix =
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T04:46:27Z",
                type = "event_msg",
                payload = new { type = "task_started", turn_id = activeTurn }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T04:46:28Z",
                type = "turn_context",
                turn_id = activeTurn,
                payload = new { cwd = @"D:\Projects\codex\faker" }
            }),
            "{\"timestamp\":\"2026-08-01T04:46:29Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"branch prompt\"}}",
            "{\"timestamp\":\"2026-08-01T04:46:30Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":9,\"cached_input_tokens\":6,\"output_tokens\":2,\"total_tokens\":11},\"total_token_usage\":{\"input_tokens\":109,\"cached_input_tokens\":46,\"output_tokens\":22,\"total_tokens\":131}}}}"
        ];
        await File.AppendAllTextAsync(
            path,
            string.Join('\n', suffix) + "\n",
            Utf8WithoutBom);
        var storedCursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            initial.NextCursorJson,
            initial.SourceFingerprint,
            initial.ParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);

        CollectedBatch continuation = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                storedCursor,
                CollectionReason.FileChanged)));

        var active = Assert.ContainsSingle(continuation.Events);
        Assert.AreEqual(2L, continuation.EventRevisionHighWatermark);
        Assert.AreEqual(target, active.SessionId);
        Assert.AreEqual(11L, active.Tokens.NormalizedTotal.Value);
        Assert.IsNotEmpty(continuation.Turns);
        Assert.IsTrue(continuation.Turns.All(
            turn => string.Equals(turn.SessionId, target, StringComparison.Ordinal)));
        CodexCursor restoredCursor = CodexCursor.DeserializeOrStart(
            continuation.NextCursorJson,
            out CollectorDiagnostic? restoredDiagnostic);
        Assert.IsNull(restoredDiagnostic);
        Assert.AreEqual(target, restoredCursor.State.ThreadId);
        Assert.IsNull(restoredCursor.State.ReplayTarget);
        Assert.IsFalse(restoredCursor.State.IsHistoryReplay);

        string[] later =
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T05:00:00Z",
                type = "event_msg",
                payload = new { type = "task_started", turn_id = laterTurn }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T05:00:01Z",
                type = "turn_context",
                turn_id = laterTurn,
                payload = new { cwd = @"D:\Projects\codex\faker" }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-08-01T05:00:02Z",
                type = "response_item",
                payload = new
                {
                    type = "function_call",
                    name = "followup_task",
                    call_id = "live-followup",
                    arguments = "{\"target\":\"root/protocol_worker\"}"
                }
            }),
            "{\"timestamp\":\"2026-08-01T05:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":5,\"cached_input_tokens\":2,\"output_tokens\":1,\"total_tokens\":6},\"total_token_usage\":{\"input_tokens\":114,\"cached_input_tokens\":48,\"output_tokens\":23,\"total_tokens\":137}}}}"
        ];
        await File.AppendAllTextAsync(
            path,
            string.Join('\n', later) + "\n",
            Utf8WithoutBom);
        var restoredStoredCursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            continuation.NextCursorJson,
            continuation.SourceFingerprint,
            continuation.ParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);

        CollectedBatch appendedAgain = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                restoredStoredCursor,
                CollectionReason.FileChanged)));

        Assert.AreEqual(target, Assert.ContainsSingle(appendedAgain.Events).SessionId);
        Assert.IsNotEmpty(appendedAgain.Turns);
        Assert.IsTrue(appendedAgain.Turns.All(
            turn => string.Equals(turn.SessionId, target, StringComparison.Ordinal)));
        Assert.AreEqual(target, Assert.ContainsSingle(appendedAgain.Dispatches).SourceSessionId);
    }

    [TestMethod]
    public async Task CollectAsync_RejectsAnEntityOutsideTheInjectedCodexHome()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        var collector = new CodexCollector(codexHome);
        string outside = directory.File("rollout-outside.jsonl");
        await WriteAsync(outside, "{}\n");
        var instance = new SourceInstanceDescriptor(
            CodexSourceIdentity.InstanceId(codexHome),
            "codex",
            SourceKind.Jsonl,
            "Codex (Windows)",
            CodexSourceIdentity.NormalizePath(codexHome));
        var entity = new SourceEntityDescriptor(
            instance.SourceInstanceId,
            CodexSourceIdentity.EntityId(outside),
            outside);
        var request = new CollectionRequest(
            instance,
            entity,
            null,
            CollectionReason.ManualRequest);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await CollectAsync(collector, request));
    }

    [TestMethod]
    public async Task CollectAsync_BlankStoredCursorIsMalformedAndResetsFromHead()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(codexHome, "sessions", "rollout-blank-cursor.jsonl");
        await CopyBasicFixtureAsync(path);
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        var storedCursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            " ",
            new string('a', 64),
            CodexRolloutParser.CurrentParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);

        IReadOnlyList<CollectedBatch> batches = await CollectAsync(
            collector,
            new CollectionRequest(instance, entity, storedCursor, CollectionReason.StartupImport));

        Assert.AreEqual(2, batches.Sum(batch => batch.Events.Count));
        CollectorDiagnostic diagnostic = Assert.ContainsSingle(
            batches.SelectMany(batch => batch.Diagnostics)
                .Where(item => item.Code == "codex.invalid_cursor"));
        Assert.AreEqual(
            "Codex collection cursor was invalid and has been reset.",
            diagnostic.Message);
    }

    [TestMethod]
    public async Task CollectAsync_OldParserCursorRequiresExplicitRebuildBeforeReading()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(codexHome, "sessions", "rollout-old-parser.jsonl");
        await CopyBasicFixtureAsync(path);
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        var storedCursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            CodexCursor.Start.Serialize(),
            new string('a', 64),
            "codex-rollout-v1",
            DateTimeOffset.UtcNow,
            null,
            null);
        var request = new CollectionRequest(
            instance,
            entity,
            storedCursor,
            CollectionReason.StartupImport);

        CodexParserRebuildRequiredException exception =
            await Assert.ThrowsExactlyAsync<CodexParserRebuildRequiredException>(
                async () => await CollectAsync(collector, request));

        Assert.AreEqual("codex-rollout-v1", exception.StoredParserVersion);
        Assert.AreEqual(
            CodexRolloutParser.CurrentParserVersion,
            exception.RequiredParserVersion);
        Assert.DoesNotContain(entity.SourcePath, exception.Message);
        Assert.DoesNotContain(entity.SourceEntityId, exception.Message);
    }

    [TestMethod]
    public async Task CollectAsync_FiltersUnsafeStateStringsBeforeSerializingCursor()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(codexHome, "sessions", "rollout-unsafe-state.jsonl");
        string tooLong = new('x', 1025);
        string[] lines =
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T01:00:00Z",
                type = "session_meta",
                payload = new { id = "safe-thread", model_provider = "safe-provider" }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T01:00:01Z",
                type = "turn_context",
                payload = new { model = "safe-model", model_provider = "safe-provider" }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T01:00:02Z",
                type = "session_meta",
                payload = new
                {
                    id = string.Empty,
                    forked_from_id = "bad\u0001parent",
                    session_id = tooLong,
                    model_provider = "bad\u0002provider"
                }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-16T01:00:03Z",
                type = "turn_context",
                payload = new { model = tooLong, model_provider = "bad\u0003provider" }
            }),
            "{\"timestamp\":\"2026-07-16T01:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":0,\"output_tokens\":1,\"total_tokens\":3}}}}"
        ];
        await WriteAsync(path, string.Join('\n', lines) + "\n");
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        CollectedBatch batch = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport)));

        CodexCursor cursor = CodexCursor.DeserializeOrStart(
            batch.NextCursorJson,
            out CollectorDiagnostic? diagnostic);
        Assert.IsNull(diagnostic);
        Assert.IsNull(cursor.State.ThreadId);
        Assert.IsNull(cursor.State.ParentSessionId);
        Assert.IsNull(cursor.State.CurrentRawModel);
        Assert.IsNull(cursor.State.CurrentProviderId);
        Assert.IsNull(cursor.State.PreviousCumulative);
        Assert.IsTrue(cursor.State.IsHistoryReplay);
        Assert.IsEmpty(batch.Events);
        Assert.HasCount(
            2,
            batch.Diagnostics.Where(item => item.Code == "codex.invalid_state_metadata"));
        Assert.DoesNotContain(tooLong, batch.NextCursorJson);
    }

    [TestMethod]
    public async Task CollectAsync_ObservesCancellationBetweenReaderAndParserLoop()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(codexHome, "sessions", "rollout-cancel-parser.jsonl");
        await WriteAsync(path, "{}\n{}\n");
        using var cancellation = new CancellationTokenSource();
        var collector = new CodexCollector(
            codexHome,
            new CancellingTimeProvider(cancellation));
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await CollectAsync(
                collector,
                new CollectionRequest(instance, entity, null, CollectionReason.ManualRequest),
                cancellation.Token));
    }

    [TestMethod]
    public async Task CollectAsync_StopsAtFiveThousandLinesAndResumesAtFiveThousandOne()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(codexHome, "sessions", "rollout-5001.jsonl");
        await WriteAsync(path, string.Concat(Enumerable.Repeat("{}\n", 5001)));
        var collector = new CodexCollector(codexHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);

        IReadOnlyList<CollectedBatch> firstRun = await CollectAsync(
            collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport));
        CollectedBatch last = firstRun[^1];
        CodexCursor stopped = CodexCursor.DeserializeOrStart(last.NextCursorJson, out _);
        var storedCursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            last.NextCursorJson,
            last.SourceFingerprint,
            last.ParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);
        IReadOnlyList<CollectedBatch> continuation = await CollectAsync(
            collector,
            new CollectionRequest(instance, entity, storedCursor, CollectionReason.StartupImport));

        Assert.HasCount(25, firstRun);
        Assert.AreEqual(5000L, stopped.Jsonl.LineNumber);
        Assert.AreEqual(
            "collector.batch_limit_reached",
            Assert.ContainsSingle(last.Diagnostics).Code);
        Assert.AreEqual(
            5001L,
            CodexCursor.DeserializeOrStart(
                Assert.ContainsSingle(continuation).NextCursorJson,
                out _).Jsonl.LineNumber);
    }

    [TestMethod]
    public async Task CollectAsync_EmptyResetUsesDeterministicFallbackForInvalidLastKnownFingerprint()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string path = Path.Combine(codexHome, "sessions", "rollout-empty-reset.jsonl");
        await WriteAsync(path, string.Empty);
        var collector = new CodexCollector(codexHome);
        var instance = new SourceInstanceDescriptor(
            CodexSourceIdentity.InstanceId(codexHome),
            "codex",
            SourceKind.Jsonl,
            "Codex (Windows)",
            CodexSourceIdentity.NormalizePath(codexHome));
        var entity = new SourceEntityDescriptor(
            instance.SourceInstanceId,
            CodexSourceIdentity.EntityId(path),
            path);
        string cursorJson = new CodexCursor(
            new JsonlCursor(1, string.Empty, 1, new string('a', 64)),
            new CodexParseState()).Serialize();
        const string privateInvalidFingerprint = @"C:\private\fingerprint";
        var storedCursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            cursorJson,
            privateInvalidFingerprint,
            CodexRolloutParser.CurrentParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);

        CollectedBatch reset = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(instance, entity, storedCursor, CollectionReason.StartupImport)));

        Assert.AreEqual(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            reset.SourceFingerprint);
        Assert.DoesNotContain(privateInvalidFingerprint, reset.SourceFingerprint);
        Assert.AreEqual(CodexCursor.Start, CodexCursor.DeserializeOrStart(
            reset.NextCursorJson,
            out _));
        Assert.AreEqual("jsonl.source_reset", Assert.ContainsSingle(reset.Diagnostics).Code);
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

    private static async Task<IReadOnlyList<CollectedBatch>> CollectAsync(
        CodexCollector collector,
        CollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var batches = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(
                           request,
                           cancellationToken))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static Task CopyBasicFixtureAsync(string destination) =>
        WriteAsync(destination, File.ReadAllText(Path.Combine(
            FixtureDirectory,
            "basic-rollout.jsonl")));

    private static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Utf8WithoutBom);
    }

    private static string FixtureDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "Fixtures",
        "Codex"));

    private sealed class CancellingTimeProvider : TimeProvider
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingTimeProvider(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public override DateTimeOffset GetUtcNow()
        {
            _cancellation.Cancel();
            return new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
        }
    }

    private sealed class FakeSessionNameSource(
        IReadOnlyList<UsageSessionNameMetadata> names)
        : IUsageSessionNameSource
    {
        public Task<IReadOnlyList<UsageSessionNameMetadata>> ReadSessionNamesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(names);
        }
    }
}
