using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.ClaudeCode;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class ClaudeCodeCollectorTests
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    public void ParseLine_MergesStreamedAssistantMessageAndFlushesTerminalUsage()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            UserLine("Run the focused tests."),
            AssistantLine(outputTokens: 0, terminal: false),
            AssistantLine(outputTokens: 7, terminal: true));

        UsageEvent completed = Assert.ContainsSingle(
            results.Where(value => value.Event is not null)).Event!;
        Assert.AreEqual(CompletionState.Completed, completed.CompletionState);
        Assert.AreEqual(16L, completed.Tokens.InputReported.Value);
        Assert.AreEqual(10L, completed.Tokens.UncachedInput.Value);
        Assert.AreEqual(4L, completed.Tokens.CacheRead.Value);
        Assert.AreEqual(2L, completed.Tokens.CacheWrite.Value);
        Assert.AreEqual(7L, completed.Tokens.Output.Value);
        Assert.AreEqual(23L, completed.Tokens.NormalizedTotal.Value);
        Assert.AreEqual("deepseek-v4-pro", completed.Model.NormalizedModel);
        Assert.IsNull(completed.Model.ProviderId);
        Assert.AreEqual(results[0].TurnMetadata!.TurnIdHash, completed.TurnIdHash);
    }

    [TestMethod]
    [DataRow("synthetic-meta-string.jsonl")]
    [DataRow("synthetic-meta-array-progress.jsonl")]
    public void ParseLine_MetaInputKeepsOnePromptAndMergedUsage(string fixture)
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(MetaFixture(fixture));
        Assert.IsTrue(results.All(value => value.Diagnostic is null));
        UsageEvent completed = Assert.ContainsSingle(
            results.Where(value => value.Event is not null)).Event!;
        Assert.AreEqual(22L, completed.Tokens.InputReported.Value);
        Assert.AreEqual(17L, completed.Tokens.UncachedInput.Value);
        Assert.AreEqual(3L, completed.Tokens.CacheRead.Value);
        Assert.AreEqual(2L, completed.Tokens.CacheWrite.Value);
        Assert.AreEqual(5L, completed.Tokens.Output.Value);
        Assert.AreEqual(27L, completed.Tokens.NormalizedTotal.Value);
        Assert.HasCount(2, results.SelectMany(value => value.EventTools).ToArray());
        ClaudeCodeParseState final = results[^1].State;
        Assert.HasCount(1, final.Turns!);
        ClaudeCodeTurnState turn = Assert.ContainsSingle(final.Turns!.Values);
        Assert.AreEqual("Synthetic initial task.", turn.PromptPreview);
        Assert.AreEqual(1, turn.UserMessageCount);
        Assert.AreEqual(results[0].TurnMetadata!.TurnIdHash, completed.TurnIdHash);
        string cursor = MetaStateCursor(final).Serialize();
        Assert.DoesNotContain("Synthetic metadata only.", cursor);
    }

    [TestMethod]
    [DataRow("absent")]
    [DataRow("false")]
    [DataRow("string")]
    public void ParseLine_MetaFlagMustBeBooleanTrue(string flag)
    {
        string[] lines = MetaFixture("synthetic-meta-string.jsonl");
        JsonNode meta = JsonNode.Parse(lines[3])!;
        if (flag == "absent") { meta.AsObject().Remove("isMeta"); }
        else if (flag == "false") { meta["isMeta"] = false; }
        else { meta["isMeta"] = "true"; }
        lines[3] = meta.ToJsonString();
        AssertInvalidStreamDiagnostic(Parse(lines),
            "claude_code.invalid_stream_prompt_identity");
    }

    [TestMethod]
    public void ParseLine_MetaWithoutRetainedParentKeepsUnknownPrompt()
    {
        string[] lines = MetaFixture("synthetic-meta-string.jsonl");
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(lines[3..]);
        UsageEvent completed = Assert.ContainsSingle(
            results.Where(value => value.Event is not null)).Event!;
        Assert.IsNull(completed.TurnIdHash);
        Assert.IsEmpty(results[^1].State.Turns!);
        Assert.IsTrue(results.All(value => value.TurnMetadata is null));
        Assert.IsTrue(results.Any(value =>
            value.Diagnostic?.Code == "claude_code.causal_parent_unavailable"));
        Assert.DoesNotContain("Synthetic metadata only.",
            MetaStateCursor(results[^1].State).Serialize());
    }

    [TestMethod]
    public void ParseLine_SyntheticRealPromptConflictStillFails()
    {
        AssertInvalidStreamDiagnostic(
            Parse(MetaFixture("synthetic-real-prompt-conflict.jsonl")),
            "claude_code.invalid_stream_prompt_identity");
    }

    [TestMethod]
    [DataRow("synthetic-meta-string.jsonl")]
    [DataRow("synthetic-meta-array-progress.jsonl")]
    public async Task CollectAsync_MetaInputSurvivesTwoHundredLineCursorRestart(
        string fixture)
    {
        using var directory = new TestTempDirectory();
        string claudeHome = directory.File(".claude");
        string transcript = Path.Combine(claudeHome, "projects", "synthetic", "meta.jsonl");
        string[] lines = MetaFixture(fixture);
        using JsonDocument toolResult = JsonDocument.Parse(lines[2]);
        string parent = toolResult.RootElement.GetProperty("uuid").GetString()!;
        string sessionId = toolResult.RootElement.GetProperty("sessionId").GetString()!;
        var initial = new List<string>(lines[..3]);
        for (int index = 0; index < 197; index++)
        {
            string uuid = $"synthetic-padding-{index}";
            initial.Add(JsonSerializer.Serialize(new
            {
                type = "progress", uuid, parentUuid = parent, sessionId,
                entrypoint = "cli", timestamp = "2030-01-02T03:04:02Z"
            }));
            parent = uuid;
        }
        await WriteAsync(transcript, string.Join('\n', initial) + "\n");
        var collector = new ClaudeCodeCollector(claudeHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System), CancellationToken.None);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        CollectedBatch before = Assert.ContainsSingle(await CollectAsync(collector,
            new CollectionRequest(instance, entity, null, CollectionReason.StartupImport)));
        UsageEvent first = Assert.ContainsSingle(before.Events);
        Assert.AreEqual(200L,
            ClaudeCodeCursor.DeserializeOrStart(before.NextCursorJson, true, out _)
                .Jsonl.LineNumber);
        await File.AppendAllTextAsync(transcript,
            string.Join('\n', lines[3..]) + "\n", Utf8WithoutBom);
        var stored = new StoredCursor(instance.SourceInstanceId, entity.SourceEntityId,
            entity.SourcePath, before.NextCursorJson, before.SourceFingerprint,
            before.ParserVersion, DateTimeOffset.UtcNow, null, null);
        CollectedBatch after = Assert.ContainsSingle(await CollectAsync(
            new ClaudeCodeCollector(claudeHome),
            new CollectionRequest(instance, entity, stored, CollectionReason.FileChanged)));
        UsageEvent revised = Assert.ContainsSingle(after.Events);
        Assert.AreEqual(first.EventId, revised.EventId);
        Assert.AreEqual(first.DedupKey, revised.DedupKey);
        Assert.AreEqual(first.Tokens, revised.Tokens);
        Assert.AreEqual(first.TurnIdHash, revised.TurnIdHash);
        Assert.HasCount(2, after.EventTools);
        Assert.IsEmpty(after.Diagnostics);
        ClaudeCodeParseState final = ClaudeCodeCursor.DeserializeOrStart(
            after.NextCursorJson, true, out _).State;
        Assert.HasCount(1, final.Turns!);
        Assert.DoesNotContain("Synthetic metadata only.", after.NextCursorJson);
    }

    [TestMethod]
    public void ParseLine_SeparatesInvalidStreamUsageDiagnostic()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("user-1", null, "prompt-1", "Run it"),
            StreamAssistantLine(
                "assistant-1",
                "user-1",
                "message-1",
                terminal: true,
                cacheWriteTokens: null));

        AssertInvalidStreamDiagnostic(
            results,
            "claude_code.invalid_stream_usage");
    }

    [TestMethod]
    public void ParseLine_SeparatesInvalidStreamModelIdentityDiagnostic()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("user-1", null, "prompt-1", "Run it"),
            StreamAssistantLine(
                "assistant-1",
                "user-1",
                "message-1",
                terminal: false,
                model: "model-a"),
            StreamAssistantLine(
                "assistant-2",
                "user-1",
                "message-1",
                terminal: true,
                model: "model-b"));

        AssertInvalidStreamDiagnostic(
            results,
            "claude_code.invalid_stream_model_identity");
    }

    [TestMethod]
    public void ParseLine_SeparatesInvalidStreamTokenOrderDiagnostic()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("user-1", null, "prompt-1", "Run it"),
            StreamAssistantLine(
                "assistant-1",
                "user-1",
                "message-1",
                terminal: false,
                outputTokens: 8),
            StreamAssistantLine(
                "assistant-2",
                "user-1",
                "message-1",
                terminal: true,
                outputTokens: 7));

        AssertInvalidStreamDiagnostic(
            results,
            "claude_code.invalid_stream_token_order");
    }

    [TestMethod]
    public void ParseLine_SeparatesInvalidStreamPromptIdentityDiagnostic()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("user-1", null, "prompt-1", "First"),
            StreamAssistantLine(
                "assistant-1",
                "user-1",
                "shared-message",
                terminal: false),
            CausalUserLine("user-2", "assistant-1", "prompt-2", "Second"),
            StreamAssistantLine(
                "assistant-2",
                "user-2",
                "shared-message",
                terminal: true));

        AssertInvalidStreamDiagnostic(
            results,
            "claude_code.invalid_stream_prompt_identity");
    }

    [TestMethod]
    public void ParseLine_SeparatesInvalidStreamToolIdentityDiagnostic()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("user-1", null, "prompt-1", "Run it"),
            StreamAssistantLine(
                "assistant-1",
                "user-1",
                "message-1",
                terminal: false,
                toolId: "tool-1",
                toolName: "Read"),
            StreamAssistantLine(
                "assistant-2",
                "user-1",
                "message-1",
                terminal: true,
                toolId: "tool-1",
                toolName: "Write"));

        AssertInvalidStreamDiagnostic(
            results,
            "claude_code.invalid_stream_tool_identity");
    }

    [TestMethod]
    public void ParseLine_StoresOnlyBoundedPromptPreviewAndToolName()
    {
        string prompt = $"  {new string('界', 118)} 😀 tail  ";
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "user-uuid-1",
                parentUuid = (string?)null,
                sessionId = "session-1",
                promptId = "prompt-1",
                entrypoint = "cli",
                cwd = @"C:\fixture\project",
                timestamp = "2026-08-01T01:00:00Z",
                message = new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { path = @"C:\private\image.png" } },
                        new { type = "text", text = prompt }
                    }
                }
            }),
            AssistantLine(outputTokens: 7, terminal: true));

        UsageTurnMetadata turn = results[0].TurnMetadata!;
        Assert.IsNotNull(turn.PromptPreview);
        Assert.IsLessThanOrEqualTo(120, turn.PromptPreview.EnumerateRunes().Count());
        Assert.AreEqual(turn.PromptPreview.Trim(), turn.PromptPreview);
        StringAssert.StartsWith(turn.PromptPreview, "[图片]");
        Assert.DoesNotContain("private", turn.PromptPreview);

        ClaudeCodeParseResult completed = Assert.ContainsSingle(
            results.Where(value => value.Event is not null));
        UsageEventToolMetadata tool = Assert.ContainsSingle(completed.EventTools);
        Assert.AreEqual("Read", tool.ToolName);
        Assert.AreEqual(completed.Event!.DedupKey, tool.EventDedupKey);
    }

    [TestMethod]
    public void ParseLine_UsesCausalUserUuidInsteadOfReusedPromptId()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("user-1", null, "shared-prompt", "First"),
            CausalAssistantLine(
                "assistant-1",
                "user-1",
                "message-1",
                5,
                terminal: true),
            CausalUserLine(
                "user-2",
                "assistant-1",
                "shared-prompt",
                "Second"),
            CausalAssistantLine(
                "assistant-2",
                "user-2",
                "message-2",
                7,
                terminal: true));

        UsageTurnMetadata[] turns = results
            .Where(value => value.TurnMetadata?.PromptPreview is not null)
            .Select(value => value.TurnMetadata!)
            .DistinctBy(value => value.TurnIdHash)
            .ToArray();
        UsageEvent[] events = results
            .Where(value => value.Event is not null)
            .Select(value => value.Event!)
            .ToArray();
        Assert.HasCount(2, turns);
        Assert.HasCount(2, events);
        Assert.AreNotEqual(turns[0].TurnIdHash, turns[1].TurnIdHash);
        Assert.AreEqual(turns[0].TurnIdHash, events[0].TurnIdHash);
        Assert.AreEqual(turns[1].TurnIdHash, events[1].TurnIdHash);
    }

    [TestMethod]
    public void ParseLine_RetainsExactUsageWhenCausalParentWasNotRetained()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalAssistantLine(
                "assistant-1",
                "missing-parent",
                "message-1",
                7,
                terminal: true));

        UsageEvent usage = Assert.ContainsSingle(
            results.Where(value => value.Event is not null)).Event!;
        Assert.IsNull(usage.TurnIdHash);
        Assert.AreEqual(23L, usage.Tokens.NormalizedTotal.Value);
        Assert.IsTrue(results.Any(value =>
            value.Diagnostic?.Code ==
                "claude_code.causal_parent_unavailable"));
        Assert.IsFalse(results.Any(value =>
            value.Diagnostic?.Code.Contains(
                ".invalid_",
                StringComparison.Ordinal) is true));
    }

    [TestMethod]
    public void ParseLine_MergesToolUseMissingFromFinalStreamRecord()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("user-1", null, "prompt-1", "Use a tool"),
            CausalAssistantLine(
                "assistant-tool",
                "user-1",
                "message-1",
                0,
                terminal: false,
                includeTool: true),
            CausalAssistantLine(
                "assistant-final",
                "user-1",
                "message-1",
                7,
                terminal: true,
                includeTool: false));

        ClaudeCodeParseResult completed = Assert.ContainsSingle(
            results.Where(value => value.Event is not null));
        UsageEventToolMetadata tool = Assert.ContainsSingle(completed.EventTools);
        Assert.AreEqual("Read", tool.ToolName);
        Assert.AreEqual(TokenMetric.Unavailable, completed.Event!.Tokens.Reasoning);
        Assert.AreEqual(
            MetricInclusion.Unknown,
            completed.Event.Tokens.ReasoningIncludedInOutput);
    }

    [TestMethod]
    public void ParseLine_DoesNotCountNonterminalFinalUsage()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            UserLine("Wait for the terminal record."),
            AssistantLine(outputTokens: 7, terminal: false));

        Assert.IsFalse(results.Any(value => value.Event is not null));
    }

    [TestMethod]
    public void ParseLine_OrdinaryToolResultIsNotMisclassifiedAsSubagent()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            CausalUserLine("parent-user", null, "prompt-1", "Read a file"),
            CausalAssistantLine(
                "read-assistant",
                "parent-user",
                "read-message",
                2,
                terminal: true,
                includeTool: true),
            JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = "read-result",
                parentUuid = "read-assistant",
                sessionId = "session-1",
                promptId = "prompt-1",
                entrypoint = "cli",
                isSidechain = false,
                cwd = @"C:\fixture\project",
                timestamp = "2026-08-01T01:00:02Z",
                sourceToolAssistantUUID = "read-assistant",
                toolUseResult = new { },
                message = new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "tool_result",
                            tool_use_id = "stream-tool-1"
                        }
                    }
                }
            }));

        Assert.IsFalse(results.Any(value =>
            value.Diagnostic?.Code == "claude_code.invalid_subagent_relation"));
        Assert.IsFalse(results.Any(value => value.Dispatches.Count > 0));
    }

    [TestMethod]
    public void ParseLine_CustomTitleOverridesAiTitleWithoutInventedTimestamp()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            UserLine("Create a titled session."),
            JsonSerializer.Serialize(new
            {
                type = "ai-title",
                sessionId = "session-1",
                aiTitle = "Generated title"
            }),
            JsonSerializer.Serialize(new
            {
                type = "custom-title",
                sessionId = "session-1",
                customTitle = "User title"
            }),
            JsonSerializer.Serialize(new
            {
                type = "ai-title",
                sessionId = "session-1",
                aiTitle = "Later generated title"
            }));

        UsageSessionMetadata[] titled = results
            .Where(value => value.SessionMetadata?.SessionName is not null)
            .Select(value => value.SessionMetadata!)
            .ToArray();
        Assert.HasCount(3, titled);
        Assert.AreEqual("Generated title", titled[0].SessionName);
        Assert.AreEqual("User title", titled[1].SessionName);
        Assert.AreEqual("User title", titled[2].SessionName);
        Assert.IsTrue(titled.All(value =>
            value.SessionNameUpdatedAtUtc is null));
    }

    [TestMethod]
    public void ParseLine_ScopesRepeatedMessageIdBySession()
    {
        ClaudeCodeEventContext secondContext = Context with
        {
            Entity = new SourceEntityDescriptor(
                Context.Instance.SourceInstanceId,
                "claude-code:transcript:second",
                @"C:\fixture\.claude\projects\project\second.jsonl")
        };
        UsageEvent first = Assert.ContainsSingle(Parse(
                CausalUserLine("user-1", null, "prompt", "First"),
                CausalAssistantLine(
                    "assistant-1",
                    "user-1",
                    "shared-message",
                    5,
                    terminal: true))
            .Where(value => value.Event is not null)).Event!;
        UsageEvent second = Assert.ContainsSingle(Parse(
                secondContext,
                CausalUserLine(
                    "user-2",
                    null,
                    "prompt",
                    "Second",
                    sessionId: "session-2"),
                CausalAssistantLine(
                    "assistant-2",
                    "user-2",
                    "shared-message",
                    5,
                    terminal: true,
                    sessionId: "session-2"))
            .Where(value => value.Event is not null)).Event!;

        Assert.AreNotEqual(first.DedupKey, second.DedupKey);
    }

    [TestMethod]
    public void ParseLine_EmitsExactAgentDispatchAndMatchingChildSession()
    {
        IReadOnlyList<ClaudeCodeParseResult> parentResults = Parse(
            CausalUserLine("parent-user", null, "prompt-1", "Delegate"),
            AgentAssistantLine(),
            AgentResultLine());
        ClaudeCodeParseResult link = Assert.ContainsSingle(
            parentResults.Where(value => value.Dispatches.Count > 0));
        UsageSessionMetadata childRelation =
            Assert.ContainsSingle(link.RelatedSessions);
        UsageTurnDispatch dispatch = Assert.ContainsSingle(link.Dispatches);

        ClaudeCodeEventContext childContext = Context with
        {
            Entity = new SourceEntityDescriptor(
                Context.Instance.SourceInstanceId,
                "claude-code:transcript:child",
                @"C:\fixture\.claude\projects\project\session-1\subagents\agent-1.jsonl")
        };
        IReadOnlyList<ClaudeCodeParseResult> childResults = Parse(
            childContext,
            SubagentUserLine(),
            SubagentAssistantLine());
        UsageEvent childEvent = Assert.ContainsSingle(
            childResults.Where(value => value.Event is not null)).Event!;
        UsageSessionMetadata childSession = Assert.ContainsSingle(
            childResults
                .Where(value => value.SessionMetadata?.SessionRole ==
                    SessionRole.Subagent)
                .Select(value => value.SessionMetadata!)
                .DistinctBy(value => value.SessionId));

        Assert.AreEqual(childRelation.SessionId, childEvent.SessionId);
        Assert.AreEqual(childRelation.SessionId, childSession.SessionId);
        Assert.AreEqual("session-1", childRelation.DirectParentSessionId);
        Assert.AreEqual(SessionRelationState.Confirmed, childRelation.RelationState);
        Assert.AreEqual(SessionRelationOrigin.SourceAgentParent, childRelation.RelationOrigin);
        Assert.AreEqual(dispatch.TargetAgentHash, childRelation.AgentLeafHash);
        Assert.IsNotNull(childEvent.TurnIdHash);
    }

    [TestMethod]
    public void ParseLine_RetainsConfirmedChildSessionWithoutGuessedTurnDispatch()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            AgentAssistantLine("missing-parent"),
            AgentResultLine());

        ClaudeCodeParseResult relation = Assert.ContainsSingle(
            results.Where(value => value.RelatedSessions.Count > 0));
        UsageSessionMetadata child = Assert.ContainsSingle(
            relation.RelatedSessions);
        Assert.AreEqual("session-1", child.DirectParentSessionId);
        Assert.AreEqual(
            SessionRelationOrigin.SourceAgentParent,
            child.RelationOrigin);
        Assert.AreEqual(SessionRelationState.Confirmed, child.RelationState);
        Assert.IsEmpty(relation.Dispatches);
        Assert.IsFalse(results.Any(value =>
            value.Diagnostic?.Code ==
                "claude_code.invalid_subagent_relation"));
    }

    [TestMethod]
    public async Task ProbeAsync_FindsRootAndNestedCliTranscriptsOnly()
    {
        using var directory = new TestTempDirectory();
        string claudeHome = directory.File(".claude");
        string rootTranscript = Path.Combine(claudeHome, "projects", "project-a", "root.jsonl");
        string agentTranscript = Path.Combine(
            claudeHome,
            "projects",
            "project-a",
            "session",
            "subagents",
            "agent.jsonl");
        await WriteAsync(rootTranscript, UserLine("One") + Environment.NewLine);
        await WriteAsync(agentTranscript, UserLine("Two") + Environment.NewLine);
        await WriteAsync(
            Path.Combine(claudeHome, "projects", "project-a", "ignored.txt"),
            "not a transcript");
        await WriteAsync(
            Path.Combine(
                claudeHome,
                "projects",
                "project-a",
                "session",
                "memory",
                "auxiliary.jsonl"),
            UserLine("Must remain outside discovery."));

        var collector = new ClaudeCodeCollector(claudeHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        Assert.AreEqual("claude-code", instance.AgentId);
        Assert.AreEqual("Claude Code (Windows)", instance.DisplayName);
        Assert.HasCount(2, probe.Entities);
        Assert.IsEmpty(probe.Diagnostics);
    }

    [TestMethod]
    public async Task CollectAsync_AdvancesIncrementalCursorAcrossCliTranscript()
    {
        using var directory = new TestTempDirectory();
        string claudeHome = directory.File(".claude");
        string transcript = Path.Combine(
            claudeHome,
            "projects",
            "project-a",
            "session.jsonl");
        await WriteAsync(transcript, UserLine("Start") + Environment.NewLine);
        var collector = new ClaudeCodeCollector(claudeHome);
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
            transcript,
            AssistantLine(outputTokens: 7, terminal: true) + Environment.NewLine,
            Utf8WithoutBom);
        var stored = new AgenTally.Storage.Writing.StoredCursor(
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
        Assert.IsTrue(((IIncrementalFileCollector)collector).TryGetCursorByteOffset(
            stored with { CursorJson = second.NextCursorJson },
            out long byteOffset));
        Assert.AreEqual(new FileInfo(transcript).Length, byteOffset);
    }

    [TestMethod]
    public async Task CollectAsync_LongTranscriptStopsAtBatchBoundaryAndResumes()
    {
        using var directory = new TestTempDirectory();
        string claudeHome = directory.File(".claude");
        string transcript = Path.Combine(
            claudeHome,
            "projects",
            "project-a",
            "long-session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        await using (var stream = new StreamWriter(
                         transcript,
                         append: false,
                         Utf8WithoutBom))
        {
            await stream.WriteLineAsync(
                CausalUserLine("long-user", null, "prompt", "Long run"));
            for (int index = 0; index < 5_001; index++)
            {
                await stream.WriteLineAsync(CausalAssistantLine(
                    $"long-assistant-{index}",
                    "long-user",
                    $"long-message-{index}",
                    index + 1,
                    terminal: true));
            }
        }

        var collector = new ClaudeCodeCollector(claudeHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        IReadOnlyList<CollectedBatch> first = await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        Assert.HasCount(25, first);
        Assert.IsTrue(first.All(value => value.Events.Count <= 200));
        Assert.AreEqual(4_999, first.Sum(value => value.Events.Count));
        Assert.IsTrue(first[^1].Diagnostics.Any(value =>
            value.Code == "collector.batch_limit_reached"));

        CollectedBatch last = first[^1];
        var stored = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            last.NextCursorJson,
            last.SourceFingerprint,
            last.ParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);
        CollectedBatch resumed = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                stored,
                CollectionReason.RepairScan)));

        Assert.HasCount(2, resumed.Events);
        Assert.IsFalse(resumed.Diagnostics.Any(value =>
            value.Code == "collector.batch_limit_reached"));
    }

    [TestMethod]
    public async Task DesktopProbe_FindsOnlyVerifiedLocalAgentSessionLayout()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File("local-agent-mode-sessions");
        string transcript = Path.Combine(
            root,
            "desktop-session",
            "vm",
            "workspace",
            ".claude",
            "projects",
            "project-key",
            $"session_{Guid.NewGuid():D}.jsonl");
        await WriteAsync(transcript, DesktopAssistantLine(7) + Environment.NewLine);
        await WriteAsync(
            Path.Combine(root, "audit.jsonl"),
            DesktopAssistantLine(99));
        await WriteAsync(
            Path.Combine(root, "claude-code-sessions", $"session_{Guid.NewGuid():D}.jsonl"),
            DesktopAssistantLine(99));
        await WriteAsync(
            Path.Combine(root, "chat", ".claude", "projects", "project", "chat.jsonl"),
            DesktopAssistantLine(99));

        var collector = new ClaudeCodeDesktopCollector(root);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        Assert.AreEqual("claude-code", instance.AgentId);
        Assert.AreEqual("Claude Code Desktop Code (Windows)", instance.DisplayName);
        Assert.AreEqual(Path.GetFullPath(transcript), entity.SourcePath);
        Assert.IsEmpty(probe.Diagnostics);
    }

    [TestMethod]
    public async Task DesktopCollector_ReportsTokensWithCausalPromptAttribution()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File("local-agent-mode-sessions");
        string transcript = Path.Combine(
            root,
            "desktop-session",
            "vm",
            "workspace",
            ".claude",
            "projects",
            "project-key",
            $"session_{Guid.NewGuid():D}.jsonl");
        await WriteAsync(
            transcript,
            string.Join(
                Environment.NewLine,
                DesktopUserLine("private desktop prompt must not be retained"),
                DesktopAssistantLine(0),
                DesktopAssistantLine(7)) + Environment.NewLine);
        var collector = new ClaudeCodeDesktopCollector(root);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        var batches = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(
                           new CollectionRequest(
                               Assert.ContainsSingle(probe.Instances),
                               Assert.ContainsSingle(probe.Entities),
                               null,
                               CollectionReason.StartupImport),
                           CancellationToken.None))
        {
            batches.Add(batch);
        }

        CollectedBatch collected = Assert.ContainsSingle(batches);
        UsageEvent usage = Assert.ContainsSingle(collected.Events);
        Assert.AreEqual(23L, usage.Tokens.NormalizedTotal.Value);
        Assert.IsNotNull(usage.TurnIdHash);
        Assert.IsNotEmpty(collected.Turns);
        Assert.IsTrue(collected.Sessions.All(value =>
            value.CompatibilityLevel == CompatibilityLevel.FullyCompatible));
        Assert.DoesNotContain(
            "private desktop prompt",
            string.Join(' ', collected.Events.Select(value => value.EventId)));
    }

    [TestMethod]
    public async Task DesktopCollector_StrictLayoutAcceptsMissingEntrypoint()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File("local-agent-mode-sessions");
        string transcript = Path.Combine(
            root,
            "desktop-session",
            "vm",
            "workspace",
            ".claude",
            "projects",
            "project-key",
            $"{Guid.NewGuid():D}.jsonl");
        await WriteAsync(
            transcript,
            string.Join(
                Environment.NewLine,
                RemoveDesktopEntrypoint(DesktopUserLine("Prompt")),
                RemoveDesktopEntrypoint(DesktopAssistantLine(7))) +
            Environment.NewLine);
        var collector = new ClaudeCodeDesktopCollector(root);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        CollectedBatch batch = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                Assert.ContainsSingle(probe.Instances),
                Assert.ContainsSingle(probe.Entities),
                null,
                CollectionReason.StartupImport)));

        Assert.ContainsSingle(batch.Events);
        Assert.IsNotEmpty(batch.Turns);
        Assert.IsFalse(batch.Diagnostics.Any(value =>
            value.Code.Contains(".invalid_", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DesktopCollector_StrictLayoutRejectsUnknownEntrypoint()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File("local-agent-mode-sessions");
        string transcript = Path.Combine(
            root,
            "desktop-session",
            "vm",
            "workspace",
            ".claude",
            "projects",
            "project-key",
            $"{Guid.NewGuid():D}.jsonl");
        await WriteAsync(
            transcript,
            DesktopAssistantLine(7).Replace(
                "\"local-agent\"",
                "\"unknown-entrypoint\"",
                StringComparison.Ordinal) + Environment.NewLine);
        var collector = new ClaudeCodeDesktopCollector(root);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        CollectedBatch batch = Assert.ContainsSingle(await CollectAsync(
            collector,
            new CollectionRequest(
                Assert.ContainsSingle(probe.Instances),
                Assert.ContainsSingle(probe.Entities),
                null,
                CollectionReason.StartupImport)));

        Assert.IsEmpty(batch.Events);
        Assert.IsTrue(batch.Diagnostics.Any(value =>
            value.Code == "claude_code.invalid_record_identity"));
    }

    [TestMethod]
    public async Task DesktopProbe_ChatOnlyTreeProducesNoSourceInstance()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File("local-agent-mode-sessions");
        await WriteAsync(
            Path.Combine(root, "chat", "conversation.jsonl"),
            DesktopAssistantLine(7));

        SourceProbeResult probe = await new ClaudeCodeDesktopCollector(root)
            .ProbeAsync(
                new CollectorContext(directory.Path, TimeProvider.System),
                CancellationToken.None);

        Assert.IsEmpty(probe.Instances);
        Assert.IsEmpty(probe.Entities);
        Assert.IsEmpty(probe.Diagnostics);
    }

    private static ClaudeCodeCursor MetaStateCursor(ClaudeCodeParseState state) => new(
        new JsonlCursor(4096, string.Empty, 10, new string('a', 64)), state);

    private static string[] MetaFixture(string name) => File.ReadAllLines(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "Fixtures", "ClaudeCode", name)));

    private static IReadOnlyList<ClaudeCodeParseResult> Parse(params string[] lines)
        => Parse(Context, lines);

    private static IReadOnlyList<ClaudeCodeParseResult> Parse(
        ClaudeCodeEventContext context,
        params string[] lines)
    {
        var parser = new ClaudeCodeTranscriptParser();
        ClaudeCodeParseState state = new();
        var results = new List<ClaudeCodeParseResult>(lines.Length);
        long byteOffset = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(lines[index]);
            ClaudeCodeParseResult result = parser.ParseLine(
                new JsonlLine(index + 1, byteOffset, utf8),
                state,
                context);
            results.Add(result);
            state = result.State;
            byteOffset += utf8.LongLength + 1;
        }

        results.AddRange(parser.FlushCompletedCalls(state, context));

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

    private static string UserLine(string content) => JsonSerializer.Serialize(new
    {
        type = "user",
        uuid = "user-uuid-1",
        parentUuid = (string?)null,
        sessionId = "session-1",
        promptId = "prompt-1",
        entrypoint = "cli",
        cwd = @"C:\fixture\project",
        timestamp = "2026-08-01T01:00:00Z",
        message = new { role = "user", content }
    });

    private static string CausalUserLine(
        string uuid,
        string? parentUuid,
        string promptId,
        string content,
        string sessionId = "session-1") => JsonSerializer.Serialize(new
        {
            type = "user",
            uuid,
            parentUuid,
            sessionId,
            promptId,
            entrypoint = "cli",
            isSidechain = false,
            cwd = @"C:\fixture\project",
            timestamp = "2026-08-01T01:00:00Z",
            message = new { role = "user", content }
        });

    private static string CausalAssistantLine(
        string uuid,
        string parentUuid,
        string messageId,
        long outputTokens,
        bool terminal,
        bool includeTool = false,
        string sessionId = "session-1") => JsonSerializer.Serialize(new
        {
            type = "assistant",
            uuid,
            parentUuid,
            sessionId,
            cwd = @"C:\fixture\project",
            timestamp = "2026-08-01T01:00:01Z",
            entrypoint = "cli",
            message = new
            {
                role = "assistant",
                id = messageId,
                model = "fixture-model",
                stop_reason = terminal ? "end_turn" : null,
                usage = new
                {
                    input_tokens = 10,
                    cache_read_input_tokens = 4,
                    cache_creation_input_tokens = 2,
                    output_tokens = outputTokens
                },
                content = includeTool
                    ? new object[]
                    {
                        new
                        {
                            type = "tool_use",
                            id = "stream-tool-1",
                            name = "Read"
                        }
                    }
                    : []
            }
        });

    private static string StreamAssistantLine(
        string uuid,
        string parentUuid,
        string messageId,
        bool terminal,
        string model = "fixture-model",
        long? inputTokens = 10,
        long? cacheReadTokens = 4,
        long? cacheWriteTokens = 2,
        long? outputTokens = 7,
        string? toolId = null,
        string? toolName = null) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            uuid,
            parentUuid,
            sessionId = "session-1",
            cwd = @"C:\fixture\project",
            timestamp = "2026-08-01T01:00:01Z",
            entrypoint = "cli",
            message = new
            {
                role = "assistant",
                id = messageId,
                model,
                stop_reason = terminal ? "end_turn" : null,
                usage = new
                {
                    input_tokens = inputTokens,
                    cache_read_input_tokens = cacheReadTokens,
                    cache_creation_input_tokens = cacheWriteTokens,
                    output_tokens = outputTokens
                },
                content = toolId is null && toolName is null
                    ? []
                    : new object[]
                    {
                        new
                        {
                            type = "tool_use",
                            id = toolId,
                            name = toolName
                        }
                    }
            }
        });

    private static void AssertInvalidStreamDiagnostic(
        IReadOnlyList<ClaudeCodeParseResult> results,
        string expectedCode)
    {
        Assert.ContainsSingle(results.Where(value =>
            string.Equals(
                value.Diagnostic?.Code,
                expectedCode,
                StringComparison.Ordinal)));
        Assert.IsFalse(results.Any(value =>
            string.Equals(
                value.Diagnostic?.Code,
                "claude_code.invalid_stream_call",
                StringComparison.Ordinal)));
        Assert.IsFalse(results.Any(value => value.Event is not null));
    }

    private static string AgentAssistantLine(
        string parentUuid = "parent-user") => JsonSerializer.Serialize(new
    {
        type = "assistant",
        uuid = "agent-assistant",
        parentUuid,
        sessionId = "session-1",
        cwd = @"C:\fixture\project",
        timestamp = "2026-08-01T01:00:01Z",
        entrypoint = "cli",
        message = new
        {
            role = "assistant",
            id = "agent-message",
            model = "fixture-model",
            stop_reason = "tool_use",
            usage = new
            {
                input_tokens = 10,
                cache_read_input_tokens = 4,
                cache_creation_input_tokens = 0,
                output_tokens = 2
            },
            content = new object[]
            {
                new
                {
                    type = "tool_use",
                    id = "agent-tool-use",
                    name = "Agent"
                }
            }
        }
    });

    private static string AgentResultLine() => JsonSerializer.Serialize(new
    {
        type = "user",
        uuid = "agent-result",
        parentUuid = "agent-assistant",
        sessionId = "session-1",
        promptId = "prompt-1",
        entrypoint = "cli",
        isSidechain = false,
        cwd = @"C:\fixture\project",
        timestamp = "2026-08-01T01:00:03Z",
        sourceToolAssistantUUID = "agent-assistant",
        toolUseResult = new { agentId = "child-agent-1" },
        message = new
        {
            role = "user",
            content = new object[]
            {
                new
                {
                    type = "tool_result",
                    tool_use_id = "agent-tool-use"
                }
            }
        }
    });

    private static string SubagentUserLine() => JsonSerializer.Serialize(new
    {
        type = "user",
        uuid = "child-user",
        parentUuid = (string?)null,
        sessionId = "session-1",
        promptId = "child-prompt",
        entrypoint = "cli",
        isSidechain = true,
        agentId = "child-agent-1",
        cwd = @"C:\fixture\project",
        timestamp = "2026-08-01T01:00:01.500Z",
        message = new { role = "user", content = "child task" }
    });

    private static string SubagentAssistantLine() => JsonSerializer.Serialize(new
    {
        type = "assistant",
        uuid = "child-assistant",
        parentUuid = "child-user",
        sessionId = "session-1",
        entrypoint = "cli",
        isSidechain = true,
        agentId = "child-agent-1",
        cwd = @"C:\fixture\project",
        timestamp = "2026-08-01T01:00:02Z",
        message = new
        {
            role = "assistant",
            id = "child-message",
            model = "fixture-model",
            stop_reason = "end_turn",
            usage = new
            {
                input_tokens = 3,
                cache_read_input_tokens = 5,
                cache_creation_input_tokens = 0,
                output_tokens = 7
            },
            content = Array.Empty<object>()
        }
    });

    private static string AssistantLine(long outputTokens, bool terminal) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            uuid = terminal ? "assistant-uuid-terminal" : "assistant-uuid-partial",
            parentUuid = "user-uuid-1",
            sessionId = "session-1",
            cwd = @"C:\fixture\project",
            timestamp = "2026-08-01T01:00:01Z",
            entrypoint = "cli",
            message = new
            {
                role = "assistant",
                id = "message-1",
                model = "DeepSeek-V4-Pro",
                stop_reason = terminal ? "end_turn" : null,
                usage = new
                {
                    input_tokens = 10,
                    cache_read_input_tokens = 4,
                    cache_creation_input_tokens = 2,
                    output_tokens = outputTokens
                },
                content = new object[]
                {
                    new
                    {
                        type = "tool_use",
                        id = "tool-1",
                        name = "Read",
                        input = new { file_path = @"C:\private\secret.txt" }
                    }
                }
            }
        });

    private static string DesktopAssistantLine(long outputTokens) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            uuid = outputTokens > 0
                ? "desktop-assistant-terminal"
                : "desktop-assistant-partial",
            parentUuid = "desktop-user-uuid",
            sessionId = "desktop-session-1",
            cwd = @"C:\fixture\desktop-project",
            timestamp = "2026-08-01T01:00:01Z",
            entrypoint = "local-agent",
            message = new
            {
                role = "assistant",
                id = "desktop-message-1",
                model = "claude-sonnet-4-6",
                stop_reason = outputTokens > 0 ? "end_turn" : null,
                usage = new
                {
                    input_tokens = 10,
                    cache_read_input_tokens = 4,
                    cache_creation_input_tokens = 2,
                    output_tokens = outputTokens,
                    cache_creation = new
                    {
                        ephemeral_5m_input_tokens = 2,
                        ephemeral_1h_input_tokens = 0
                    }
                },
                content = new object[]
                {
                    new
                    {
                        type = "tool_use",
                        id = "desktop-tool-1",
                        name = "Read",
                        input = new { file_path = @"C:\private\secret.txt" }
                    }
                }
            }
        });

    private static string RemoveDesktopEntrypoint(string value) =>
        value.Replace(
            "\"entrypoint\":\"local-agent\",",
            string.Empty,
            StringComparison.Ordinal);

    private static string DesktopUserLine(string content) =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            uuid = "desktop-user-uuid",
            parentUuid = (string?)null,
            sessionId = "desktop-session-1",
            promptId = "desktop-prompt-1",
            entrypoint = "local-agent",
            isSidechain = false,
            cwd = @"C:\fixture\desktop-project",
            timestamp = "2026-08-01T01:00:00Z",
            message = new { role = "user", content }
        });

    private static Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content, Utf8WithoutBom);
    }

    private static readonly ClaudeCodeEventContext Context = new(
        new SourceInstanceDescriptor(
            "claude-code:cli:windows:test",
            "claude-code",
            SourceKind.Jsonl,
            "Claude Code CLI (Windows)",
            @"C:\fixture\.claude"),
        new SourceEntityDescriptor(
            "claude-code:cli:windows:test",
            "claude-code:transcript:test",
            @"C:\fixture\.claude\projects\project\session.jsonl"),
        new string('a', 64),
        new DateTimeOffset(2026, 8, 1, 1, 5, 0, TimeSpan.Zero));
}
