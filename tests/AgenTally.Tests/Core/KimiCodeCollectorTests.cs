using System.IO;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Core.Collectors.KimiCode;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class KimiCodeCollectorTests
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    public void ParseLine_UsesTurnUsageAndStoresOnlyAllowedPromptAndToolMetadata()
    {
        string prompt = $"  {new string('界', 116)} 😀 tail  ";
        IReadOnlyList<KimiCodeParseResult> results = Parse(
            MetadataLine(),
            PromptLine(prompt),
            StepBeginLine(),
            RequestLine(),
            ToolCallLine(),
            SteerLine("private follow-up must not be saved"),
            StepEndLine(),
            UsageLine("turn"),
            UsageLine("session"));

        UsageEvent usageEvent = results[7].Event!;
        Assert.AreEqual("k3-256k", usageEvent.Model.RawModel);
        Assert.AreEqual("kimi-k3-256k", usageEvent.Model.NormalizedModel);
        Assert.AreEqual(
            "kimi-code/k3-256k",
            usageEvent.Model.RouteModelId);
        Assert.AreEqual(16L, usageEvent.Tokens.InputReported.Value);
        Assert.AreEqual(10L, usageEvent.Tokens.UncachedInput.Value);
        Assert.AreEqual(4L, usageEvent.Tokens.CacheRead.Value);
        Assert.AreEqual(2L, usageEvent.Tokens.CacheWrite.Value);
        Assert.AreEqual(7L, usageEvent.Tokens.Output.Value);
        Assert.AreEqual(23L, usageEvent.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(CompletionState.Completed, usageEvent.CompletionState);

        UsageTurnMetadata turn = results[7].TurnMetadata!;
        Assert.AreEqual(2, turn.UserMessageCount);
        Assert.IsNotNull(turn.PromptPreview);
        Assert.IsLessThanOrEqualTo(120, turn.PromptPreview.EnumerateRunes().Count());
        Assert.AreEqual(turn.PromptPreview.Trim(), turn.PromptPreview);
        StringAssert.StartsWith(turn.PromptPreview, "[图片]");
        Assert.DoesNotContain("follow-up", turn.PromptPreview);
        Assert.DoesNotContain("private", turn.PromptPreview);

        UsageEventToolMetadata tool = Assert.ContainsSingle(results[7].EventTools);
        Assert.AreEqual("Read", tool.ToolName);
        Assert.AreEqual(usageEvent.DedupKey, tool.EventDedupKey);
        Assert.IsNull(results[8].Event);
    }

    [TestMethod]
    public void ParseLine_AcceptsVersionOnePointFiveUsageBeforeStepEnd()
    {
        IReadOnlyList<KimiCodeParseResult> results = Parse(
            MetadataLine("1.5"),
            PromptLine("new order"),
            StepBeginLine(),
            RequestLine(),
            UsageLine("turn"),
            ToolCallLine(),
            StepEndLine());

        Assert.IsTrue(results.Take(6).All(result => result.Event is null));
        UsageEvent usageEvent = results[6].Event!;
        Assert.AreEqual("k3-256k", usageEvent.Model.RawModel);
        Assert.AreEqual("kimi-code/k3-256k", usageEvent.Model.RouteModelId);
        Assert.AreEqual(23L, usageEvent.Tokens.NormalizedTotal.Value);
        Assert.AreEqual("Read", Assert.ContainsSingle(results[6].EventTools).ToolName);
    }

    [TestMethod]
    public void ParseLine_AttributesTaskNotificationContinuationToCreatingUserPrompt()
    {
        IReadOnlyList<KimiCodeParseResult> results = Parse(
            MetadataLine("1.5"),
            PromptLine("first user prompt"),
            StepBeginLine("step-user", "turn-user", 200),
            TaskStartedLine("task-1", 220),
            StepEndLine("step-user", "turn-user", "message-user", 300),
            UsageLine("turn", time: 301),
            TaskNotificationPromptLine("task-1", 400),
            StepBeginLine("step-task", "turn-task", 420),
            UsageLine("turn", time: 490),
            StepEndLine("step-task", "turn-task", "message-task", 500),
            PromptLine("second user prompt", 600),
            StepBeginLine("step-second", "turn-second", 620),
            UsageLine("turn", time: 690),
            StepEndLine("step-second", "turn-second", "message-second", 700));

        string firstTurnHash = KimiCodeSourceIdentity.HashIdentity(
            "kimi-code-turn",
            "turn-user");
        UsageTurnMetadata firstTurn = results[5].TurnMetadata!;
        Assert.AreEqual(1, firstTurn.UserMessageCount);
        Assert.AreEqual("[图片] first user prompt", firstTurn.PromptPreview);

        Assert.IsNull(results[6].TurnMetadata);
        Assert.IsEmpty(results[6].State.TaskOrigins ?? []);
        UsageTurnMetadata taskContinuation = results[9].TurnMetadata!;
        Assert.AreEqual(firstTurnHash, taskContinuation.PromptOriginTurnIdHash);
        Assert.AreEqual(0, taskContinuation.UserMessageCount);
        Assert.IsNull(taskContinuation.PromptPreview);

        UsageTurnMetadata secondTurn = results[13].TurnMetadata!;
        Assert.AreEqual(1, secondTurn.UserMessageCount);
        Assert.AreEqual("[图片] second user prompt", secondTurn.PromptPreview);
        Assert.IsNull(secondTurn.PromptOriginTurnIdHash);
    }

    [TestMethod]
    public void ParseLine_RejectsUnconfirmedProtocolAndMismatchedTurnUsage()
    {
        IReadOnlyList<KimiCodeParseResult> unsupported = Parse(
            MetadataLine("2.0"),
            PromptLine("test"),
            StepBeginLine(),
            StepEndLine(),
            UsageLine("turn"));
        Assert.IsTrue(unsupported.All(result => result.Event is null));
        Assert.IsTrue(unsupported.Any(result =>
            result.Diagnostic?.Code == "kimi_code.unsupported_protocol"));

        IReadOnlyList<KimiCodeParseResult> mismatched = Parse(
            MetadataLine(),
            PromptLine("test"),
            StepBeginLine(),
            StepEndLine(),
            UsageLine("turn", output: 8));
        Assert.IsNull(mismatched[^1].Event);
        Assert.AreEqual(
            "kimi_code.invalid_usage_record",
            mismatched[^1].Diagnostic?.Code);
        Assert.AreEqual(
            CompatibilityLevel.TemporarilyIncompatible,
            mismatched[^1].SessionMetadata?.CompatibilityLevel);

        IReadOnlyList<KimiCodeParseResult> mismatchedNewOrder = Parse(
            MetadataLine("1.5"),
            PromptLine("test"),
            StepBeginLine(),
            UsageLine("turn", output: 8),
            StepEndLine());
        Assert.IsNull(mismatchedNewOrder[^1].Event);
        Assert.AreEqual(
            "kimi_code.invalid_usage_record",
            mismatchedNewOrder[^1].Diagnostic?.Code);
    }

    [TestMethod]
    public void ParseLine_GoalContinuationAliasesRawTurnToGoalCreatingPrompt()
    {
        IReadOnlyList<KimiCodeParseResult> results = Parse(
            MetadataLine(),
            PromptLine("Create and finish the goal."),
            StepBeginLine("step-user", "turn-user", 200),
            GoalCreateLine("goal-1", 210),
            StepEndLine("step-user", "turn-user", "message-user", 300),
            UsageLine("turn", time: 301),
            GoalContinuationLine(400),
            InjectionMessageLine(410),
            StepBeginLine("step-auto", "turn-auto", 420),
            StepEndLine("step-auto", "turn-auto", "message-auto", 500),
            UsageLine("turn", time: 501),
            GoalClearLine(510),
            GoalContinuationLine(600),
            StepBeginLine("step-orphan", "turn-orphan", 620),
            StepEndLine("step-orphan", "turn-orphan", "message-orphan", 700),
            UsageLine("turn", time: 701));

        string userTurnHash = KimiCodeSourceIdentity.HashIdentity(
            "kimi-code-turn",
            "turn-user");
        UsageTurnMetadata userTurn = results[5].TurnMetadata!;
        Assert.AreEqual(userTurnHash, userTurn.TurnIdHash);
        Assert.IsNull(userTurn.PromptOriginTurnIdHash);

        UsageTurnMetadata continuation = results[10].TurnMetadata!;
        Assert.AreEqual(
            KimiCodeSourceIdentity.HashIdentity(
                "kimi-code-turn",
                "turn-auto"),
            continuation.TurnIdHash);
        Assert.AreEqual(userTurnHash, continuation.PromptOriginTurnIdHash);
        Assert.IsNull(continuation.PromptPreview);
        Assert.AreEqual(0, continuation.UserMessageCount);

        UsageTurnMetadata orphan = results[15].TurnMetadata!;
        Assert.IsNull(orphan.PromptOriginTurnIdHash);
    }

    [TestMethod]
    public void ParseLine_BackgroundTaskAliasesNewRawTurnWithoutAddingUserMessage()
    {
        IReadOnlyList<KimiCodeParseResult> results = Parse(
            MetadataLine(),
            PromptLine("Run the requested task."),
            StepBeginLine("step-user", "turn-user", 200),
            StepEndLine("step-user", "turn-user", "message-user", 300),
            UsageLine("turn", time: 301),
            BackgroundTaskSteerLine(400),
            BackgroundTaskMessageLine(410),
            StepBeginLine("step-background", "turn-background", 420),
            StepEndLine(
                "step-background",
                "turn-background",
                "message-background",
                500),
            UsageLine("turn", time: 501));

        string userTurnHash = KimiCodeSourceIdentity.HashIdentity(
            "kimi-code-turn",
            "turn-user");
        UsageTurnMetadata userTurn = results[4].TurnMetadata!;
        Assert.AreEqual(1, userTurn.UserMessageCount);
        Assert.IsNull(userTurn.PromptOriginTurnIdHash);

        UsageTurnMetadata continuation = results[9].TurnMetadata!;
        Assert.AreEqual(userTurnHash, continuation.PromptOriginTurnIdHash);
        Assert.IsNull(continuation.PromptPreview);
        Assert.AreEqual(0, continuation.UserMessageCount);
    }

    [TestMethod]
    public void ParseLine_BackgroundTaskWithinSameRawTurnDoesNotAddUserMessage()
    {
        IReadOnlyList<KimiCodeParseResult> results = Parse(
            MetadataLine(),
            PromptLine("Run the requested task."),
            StepBeginLine("step-1", "turn-user", 200),
            StepEndLine("step-1", "turn-user", "message-1", 300),
            UsageLine("turn", time: 301),
            BackgroundTaskSteerLine(400),
            BackgroundTaskMessageLine(410),
            StepBeginLine("step-2", "turn-user", 420),
            StepEndLine("step-2", "turn-user", "message-2", 500),
            UsageLine("turn", time: 501));

        UsageTurnMetadata turn = results[9].TurnMetadata!;
        Assert.AreEqual(1, turn.UserMessageCount);
        Assert.IsNull(turn.PromptOriginTurnIdHash);
    }

    [TestMethod]
    public void ParseLine_ConflictingGoalLifecycleFailsClosedUntilClear()
    {
        IReadOnlyList<KimiCodeParseResult> results = Parse(
            MetadataLine(),
            PromptLine("Create a goal."),
            StepBeginLine("step-user", "turn-user", 200),
            GoalCreateLine("goal-1", 210),
            StepEndLine("step-user", "turn-user", "message-user", 300),
            UsageLine("turn", time: 301),
            GoalCreateLine("goal-conflict", 350),
            GoalContinuationLine(400),
            StepBeginLine("step-conflict", "turn-conflict", 420),
            StepEndLine(
                "step-conflict",
                "turn-conflict",
                "message-conflict",
                500),
            UsageLine("turn", time: 501),
            GoalClearLine(510),
            PromptLine("Create a replacement goal."),
            StepBeginLine("step-recovery", "turn-recovery", 600),
            GoalCreateLine("goal-recovery", 610),
            StepEndLine(
                "step-recovery",
                "turn-recovery",
                "message-recovery",
                700),
            UsageLine("turn", time: 701),
            GoalContinuationLine(800),
            StepBeginLine("step-auto", "turn-auto", 820),
            StepEndLine("step-auto", "turn-auto", "message-auto", 900),
            UsageLine("turn", time: 901));

        Assert.IsNull(results[10].TurnMetadata!.PromptOriginTurnIdHash);
        Assert.AreEqual(
            KimiCodeSourceIdentity.HashIdentity(
                "kimi-code-turn",
                "turn-recovery"),
            results[20].TurnMetadata!.PromptOriginTurnIdHash);
    }

    [TestMethod]
    public async Task ProbeAndMetadata_PreserveConfirmedSubagentRelationAndRootTitle()
    {
        using var directory = new TestTempDirectory();
        string kimiHome = directory.File(".kimi-code");
        string sessionDirectory = Path.Combine(
            kimiHome,
            "sessions",
            "workspace-a",
            "session_session-1");
        string mainWire = Path.Combine(
            sessionDirectory,
            "agents",
            "main",
            "wire.jsonl");
        string sideWire = Path.Combine(
            sessionDirectory,
            "agents",
            "agent-1",
            "wire.jsonl");
        await WriteAsync(mainWire, MetadataLine() + Environment.NewLine);
        await WriteAsync(sideWire, MetadataLine() + Environment.NewLine);
        await WriteAsync(
            Path.Combine(sessionDirectory, "agents", "agent-1", "ignored.jsonl"),
            MetadataLine());
        await WriteStateAsync(
            sessionDirectory,
            "  Source   generated title  ",
            new Dictionary<string, object>
            {
                ["main"] = new
                {
                    homedir = @"C:\fixture\home",
                    type = "main",
                    parentAgentId = (string?)null
                },
                ["agent-1"] = new
                {
                    homedir = @"C:\fixture\home",
                    type = "sub",
                    parentAgentId = "main"
                }
            });

        var collector = new KimiCodeCollector(kimiHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        Assert.HasCount(2, probe.Entities);
        Assert.IsEmpty(probe.Diagnostics);
        Assert.AreEqual("kimi-code", Assert.ContainsSingle(probe.Instances).AgentId);

        SourceEntityDescriptor sideEntity = probe.Entities.Single(value =>
            value.SourcePath.EndsWith(
                Path.Combine("agent-1", "wire.jsonl"),
                StringComparison.OrdinalIgnoreCase));
        KimiCodeEntityMetadataResult metadataResult =
            await new KimiCodeEntityMetadataReader().ReadAsync(
                kimiHome,
                sideEntity.SourcePath,
                sideEntity.SourceEntityId,
                CancellationToken.None);
        KimiCodeEntityMetadata metadata = metadataResult.Metadata!;
        Assert.AreEqual(SessionKind.Side, metadata.SessionKind);
        Assert.AreEqual(SessionRole.Subagent, metadata.SessionRole);
        Assert.AreEqual(SessionRelationState.Confirmed, metadata.RelationState);
        Assert.AreEqual(
            SessionRelationOrigin.SourceAgentParent,
            metadata.RelationOrigin);
        Assert.AreEqual("session-1", metadata.DirectParentSessionId);
        Assert.AreNotEqual(metadata.SessionId, metadata.DirectParentSessionId);

        IReadOnlyList<UsageSessionNameMetadata> names =
            await collector.ReadSessionNamesAsync(CancellationToken.None);
        UsageSessionNameMetadata name = Assert.ContainsSingle(names);
        Assert.AreEqual("session-1", name.SessionId);
        Assert.AreEqual("Source generated title", name.SessionName);
        Assert.DoesNotContain("private", name.SessionName!);
    }

    [TestMethod]
    public async Task MetadataReader_AcceptsVersionTwoCwdAndRejectsConflictingLegacyPath()
    {
        using var directory = new TestTempDirectory();
        string kimiHome = directory.File(".kimi-code");
        string sessionDirectory = Path.Combine(
            kimiHome,
            "sessions",
            "workspace-a",
            "session_session-1");
        string wire = Path.Combine(
            sessionDirectory,
            "agents",
            "main",
            "wire.jsonl");
        await WriteAsync(wire, MetadataLine() + Environment.NewLine);
        var agents = new Dictionary<string, object>
        {
            ["main"] = new
            {
                type = "main",
                parentAgentId = (string?)null
            }
        };
        string statePath = Path.Combine(sessionDirectory, "state.json");
        await WriteAsync(
            statePath,
            JsonSerializer.Serialize(new
            {
                version = 2,
                cwd = @"C:\fixture\current-project",
                agents
            }));
        var reader = new KimiCodeEntityMetadataReader();

        KimiCodeEntityMetadataResult accepted = await reader.ReadAsync(
            kimiHome,
            wire,
            sourceEntityId: "entity",
            CancellationToken.None);

        Assert.IsNull(accepted.Diagnostic);
        Assert.AreEqual(
            Path.GetFullPath(@"C:\fixture\current-project"),
            accepted.Metadata!.ProjectPath);

        await WriteAsync(
            statePath,
            JsonSerializer.Serialize(new
            {
                version = 2,
                workDir = @"C:\fixture\legacy-project",
                cwd = @"C:\fixture\current-project",
                agents
            }));

        KimiCodeEntityMetadataResult conflicting = await reader.ReadAsync(
            kimiHome,
            wire,
            sourceEntityId: "entity",
            CancellationToken.None);

        Assert.IsNull(conflicting.Metadata);
        Assert.AreEqual(
            "kimi_code.invalid_project_identity",
            conflicting.Diagnostic!.Code);
    }

    [TestMethod]
    public async Task CollectAsync_CompletesPendingUsageAfterIncrementalAppendExactlyOnce()
    {
        using var directory = new TestTempDirectory();
        string kimiHome = directory.File(".kimi-code");
        string sessionDirectory = Path.Combine(
            kimiHome,
            "sessions",
            "workspace-a",
            "session_session-1");
        string wire = Path.Combine(
            sessionDirectory,
            "agents",
            "main",
            "wire.jsonl");
        await WriteStateAsync(
            sessionDirectory,
            "Incremental session",
            new Dictionary<string, object>
            {
                ["main"] = new
                {
                    homedir = @"C:\fixture\home",
                    type = "main",
                    parentAgentId = (string?)null
                }
            });
        await WriteAsync(
            wire,
            string.Join(
                Environment.NewLine,
                MetadataLine(),
                PromptLine("Run one check."),
                StepBeginLine(),
                RequestLine(),
                UsageLine("turn"),
                ToolCallLine()) + Environment.NewLine);

        var collector = new KimiCodeCollector(kimiHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        CollectedBatch first = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport)));
        Assert.IsEmpty(first.Events);

        await File.AppendAllTextAsync(
            wire,
            StepEndLine() + Environment.NewLine,
            Utf8WithoutBom);
        var stored = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            first.NextCursorJson,
            first.SourceFingerprint,
            first.ParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);
        CollectedBatch second = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                stored,
                CollectionReason.FileChanged)));
        Assert.ContainsSingle(second.Events);

        stored = stored with
        {
            CursorJson = second.NextCursorJson,
            SourceFingerprint = second.SourceFingerprint
        };
        CollectedBatch third = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                stored,
                CollectionReason.ManualRequest)));
        Assert.IsEmpty(third.Events);
        Assert.IsTrue(((IIncrementalFileCollector)collector).TryGetCursorByteOffset(
            stored,
            out long byteOffset));
        Assert.AreEqual(new FileInfo(wire).Length, byteOffset);
    }

    [TestMethod]
    public async Task CollectAsync_MissingSessionStateFailsBeforeAdvancingCursor()
    {
        using var directory = new TestTempDirectory();
        string kimiHome = directory.File(".kimi-code");
        string wire = Path.Combine(
            kimiHome,
            "sessions",
            "workspace-a",
            "session_session-1",
            "agents",
            "main",
            "wire.jsonl");
        await WriteAsync(
            wire,
            MetadataLine() + Environment.NewLine);
        var collector = new KimiCodeCollector(kimiHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport)));
    }

    [TestMethod]
    public async Task DesktopProbeAndCollect_AcceptsWorkLayoutsAsIndependentSource()
    {
        using var directory = new TestTempDirectory();
        string desktopHome = directory.File("kimi-desktop-home");
        string sessionsRoot = Path.Combine(desktopHome, "sessions", "workspace-a");
        string[] acceptedSessionNames =
        [
            "conv-conversation-1",
            "ctitle-title-task-1"
        ];
        foreach (string sessionName in acceptedSessionNames)
        {
            string sessionDirectory = Path.Combine(sessionsRoot, sessionName);
            await WriteStateAsync(
                sessionDirectory,
                $"Desktop {sessionName}",
                new Dictionary<string, object>
                {
                    ["main"] = new
                    {
                        homedir = @"C:\fixture\home",
                        type = "main",
                        parentAgentId = (string?)null
                    }
                },
                includeWorkDirectory: false);
            await WriteAsync(
                Path.Combine(sessionDirectory, "agents", "main", "wire.jsonl"),
                string.Join(
                    Environment.NewLine,
                    MetadataLine(),
                    PromptLine("Desktop Work prompt."),
                    StepBeginLine(),
                    RequestLine(),
                    StepEndLine(),
                    UsageLine("turn")) + Environment.NewLine);
        }

        string ignoredCliSession = Path.Combine(
            sessionsRoot,
            "session_cli-only");
        await WriteStateAsync(
            ignoredCliSession,
            "CLI-only layout",
            new Dictionary<string, object>
            {
                ["main"] = new
                {
                    homedir = @"C:\fixture\home",
                    type = "main",
                    parentAgentId = (string?)null
                }
            },
            includeWorkDirectory: false);
        await WriteAsync(
            Path.Combine(ignoredCliSession, "agents", "main", "wire.jsonl"),
            MetadataLine() + Environment.NewLine);

        var collector = new KimiCodeDesktopCollector(desktopHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        Assert.AreEqual(
            KimiCodeDesktopSourceIdentity.InstanceId(desktopHome),
            instance.SourceInstanceId);
        Assert.AreEqual("kimi-work", collector.AgentId);
        Assert.AreEqual("kimi-work", instance.AgentId);
        Assert.AreEqual("Kimi Work Desktop (Windows)", instance.DisplayName);
        Assert.HasCount(2, probe.Entities);
        Assert.IsEmpty(probe.Diagnostics);
        Assert.AreNotEqual(
            KimiCodeSourceIdentity.InstanceId(desktopHome),
            instance.SourceInstanceId);

        var rootSessionIds = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<UsageEvent>();
        var sessions = new List<UsageSessionMetadata>();
        foreach (SourceEntityDescriptor entity in probe.Entities)
        {
            IReadOnlyList<CollectedBatch> batches = await CollectAsync(
                collector,
                new CollectionRequest(
                    instance,
                    entity,
                    null,
                    CollectionReason.StartupImport));
            events.AddRange(batches.SelectMany(value => value.Events));
            sessions.AddRange(batches.SelectMany(value => value.Sessions));
            rootSessionIds.UnionWith(
                batches.SelectMany(value => value.Sessions)
                    .Where(value => value.SessionKind is SessionKind.Primary)
                    .Select(value => value.SessionId));
        }

        Assert.HasCount(2, events);
        CollectionAssert.AreEquivalent(acceptedSessionNames, rootSessionIds.ToArray());
        Assert.IsTrue(events.All(value =>
            value.SourceInstanceId == instance.SourceInstanceId));
        Assert.IsTrue(events.All(value =>
            value.AgentId == "kimi-work" &&
            value.ProjectId is null &&
            value.ProjectPath is null &&
            value.ProjectRepositoryIdentityHash is null));
        Assert.IsTrue(sessions.All(value =>
            value.AgentId == "kimi-work" &&
            value.ProjectId is null &&
            value.ProjectPath is null &&
            value.ProjectRepositoryIdentityHash is null));
        IReadOnlyList<UsageSessionNameMetadata> names =
            await collector.ReadSessionNamesAsync(CancellationToken.None);
        CollectionAssert.AreEquivalent(
            acceptedSessionNames,
            names.Select(value => value.SessionId).ToArray());
    }

    private static IReadOnlyList<KimiCodeParseResult> Parse(params string[] lines)
    {
        var parser = new KimiCodeWireParser();
        KimiCodeParseState state = new();
        var results = new List<KimiCodeParseResult>(lines.Length);
        long byteOffset = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(lines[index]);
            KimiCodeParseResult result = parser.ParseLine(
                new JsonlLine(index + 1, byteOffset, utf8),
                state,
                Context);
            results.Add(result);
            state = result.State;
            byteOffset += utf8.LongLength + 1;
        }

        return results;
    }

    private static async Task<IReadOnlyList<CollectedBatch>> CollectAsync(
        IAgentCollector collector,
        CollectionRequest request)
    {
        var batches = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(
                           request,
                           CancellationToken.None))
        {
            batches.Add(batch);
        }

        return batches;
    }

    private static string MetadataLine(string protocol = "1.4") =>
        JsonSerializer.Serialize(new
        {
            type = "metadata",
            protocol_version = protocol,
            created_at = 1_754_012_800_000L
        });

    private static string PromptLine(string text, long time = 100) =>
        JsonSerializer.Serialize(new
    {
        type = "turn.prompt",
        input = new object[]
        {
            new
            {
                type = "image_url",
                image_url = new { url = @"C:\private\image.png" }
            },
            new { type = "text", text }
        },
        origin = "user",
        time = 1_754_012_800_000L + time
    });

    private static string TaskStartedLine(string taskId, long time) =>
        JsonSerializer.Serialize(new
        {
            type = "task.started",
            info = new
            {
                taskId,
                kind = "agent",
                status = "running"
            },
            time = 1_754_012_800_000L + time
        });

    private static string TaskNotificationPromptLine(string taskId, long time) =>
        JsonSerializer.Serialize(new
        {
            type = "turn.prompt",
            input = new object[]
            {
                new { type = "text", text = "private task notification" }
            },
            origin = new
            {
                kind = "task",
                taskId,
                notificationId = "notification-1",
                status = "completed"
            },
            time = 1_754_012_800_000L + time
        });

    private static string SteerLine(string text) => JsonSerializer.Serialize(new
    {
        type = "turn.steer",
        input = new object[] { new { type = "text", text } },
        origin = "user",
        time = 1_754_012_800_250L
    });

    private static string BackgroundTaskSteerLine(long time) =>
        JsonSerializer.Serialize(new
        {
            type = "turn.steer",
            input = new object[]
            {
                new { type = "text", text = "private background task" }
            },
            origin = new
            {
                kind = "background_task",
                taskId = "task-1",
                status = "completed"
            },
            time = 1_754_012_800_000L + time
        });

    private static string StepBeginLine(
        string stepId = "step-1",
        string turnId = "turn-1",
        long timeOffset = 200) => JsonSerializer.Serialize(new
    {
        type = "context.append_loop_event",
        @event = new
        {
            type = "step.begin",
            uuid = stepId,
            turnId,
            step = 1
        },
        time = 1_754_012_800_000L + timeOffset
    });

    private static string RequestLine() => JsonSerializer.Serialize(new
    {
        type = "llm.request",
        kind = "chat",
        provider = "kimi-code",
        model = "k3-256k",
        modelAlias = "kimi-for-coding",
        turnStep = 1,
        time = 1_754_012_800_210L
    });

    private static string ToolCallLine() => JsonSerializer.Serialize(new
    {
        type = "context.append_loop_event",
        @event = new
        {
            type = "tool.call",
            uuid = "event-1",
            turnId = "turn-1",
            step = 1,
            stepUuid = "step-1",
            toolCallId = "tool-1",
            name = "Read",
            args = new { path = @"C:\private\secret.txt" }
        },
        time = 1_754_012_800_220L
    });

    private static string StepEndLine(
        string stepId = "step-1",
        string turnId = "turn-1",
        string messageId = "message-1",
        long timeOffset = 300) => JsonSerializer.Serialize(new
    {
        type = "context.append_loop_event",
        @event = new
        {
            type = "step.end",
            uuid = stepId,
            turnId,
            step = 1,
            usage = Usage(),
            finishReason = "stop",
            messageId
        },
        time = 1_754_012_800_000L + timeOffset
    });

    private static string UsageLine(
        string scope,
        long output = 7,
        long time = 301) =>
        JsonSerializer.Serialize(new
        {
            type = "usage.record",
            model = "kimi-code/k3-256k",
            usage = Usage(output),
            usageScope = scope,
            time = 1_754_012_800_000L + time
        });

    private static string GoalCreateLine(string goalId, long time) =>
        JsonSerializer.Serialize(new
        {
            type = "goal.create",
            goalId,
            objective = "private goal body",
            completionCriterion = "private completion body",
            time = 1_754_012_800_000L + time
        });

    private static string GoalClearLine(long time) => JsonSerializer.Serialize(new
    {
        type = "goal.clear",
        time = 1_754_012_800_000L + time
    });

    private static string GoalContinuationLine(long time) =>
        JsonSerializer.Serialize(new
        {
            type = "context.append_message",
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "private automatic prompt" }
                },
                origin = new
                {
                    kind = "system_trigger",
                    name = "goal_continuation"
                },
                toolCalls = Array.Empty<object>()
            },
            time = 1_754_012_800_000L + time
        });

    private static string BackgroundTaskMessageLine(long time) =>
        JsonSerializer.Serialize(new
        {
            type = "context.append_message",
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "private background task" }
                },
                origin = new
                {
                    kind = "background_task",
                    taskId = "task-1",
                    status = "completed"
                },
                toolCalls = Array.Empty<object>()
            },
            time = 1_754_012_800_000L + time
        });

    private static string InjectionMessageLine(long time) =>
        JsonSerializer.Serialize(new
        {
            type = "context.append_message",
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "private injected context" }
                },
                origin = new { kind = "injection", variant = "goal" },
                toolCalls = Array.Empty<object>()
            },
            time = 1_754_012_800_000L + time
        });

    private static object Usage(long output = 7) => new
    {
        inputOther = 10,
        output,
        inputCacheRead = 4,
        inputCacheCreation = 2
    };

    private static async Task WriteStateAsync(
        string sessionDirectory,
        string title,
        Dictionary<string, object> agents,
        bool includeWorkDirectory = true)
    {
        var state = new Dictionary<string, object?>
        {
            ["createdAt"] = 1_754_012_800_000L,
            ["updatedAt"] = 1_754_012_800_500L,
            ["title"] = title,
            ["isCustomTitle"] = false,
            ["agents"] = agents,
            ["custom"] = new { },
            ["lastPrompt"] = "private prompt body"
        };
        if (includeWorkDirectory)
        {
            state["workDir"] = @"C:\fixture\project";
        }

        await WriteAsync(
            Path.Combine(sessionDirectory, "state.json"),
            JsonSerializer.Serialize(state));
    }

    private static Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content, Utf8WithoutBom);
    }

    private static readonly KimiCodeEventContext Context = new(
        new SourceInstanceDescriptor(
            "kimi-code:cli:windows:test",
            "kimi-code",
            SourceKind.Jsonl,
            "Kimi Code CLI (Windows)",
            @"C:\fixture\.kimi-code"),
        new SourceEntityDescriptor(
            "kimi-code:cli:windows:test",
            "kimi-code:wire:test",
            @"C:\fixture\.kimi-code\sessions\workspace\session_session-1\agents\main\wire.jsonl"),
        new string('a', 64),
        new DateTimeOffset(2026, 8, 1, 1, 5, 0, TimeSpan.Zero),
        new KimiCodeEntityMetadata(
            "session-1",
            "session-1",
            SessionKind.Primary,
            null,
            SessionRelationOrigin.None,
            SessionRelationState.None,
            SessionRole.Main,
            new string('b', 64),
            new string('c', 64),
            "path:test",
            @"C:\fixture\project",
            null,
            "Test session",
            new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero)));
}
