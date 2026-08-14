using System.IO;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.ClaudeCode;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class ClaudeCodeCollectorTests
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    public void ParseLine_DeduplicatesStreamedAssistantMessageByMessageId()
    {
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            UserLine("Run the focused tests."),
            AssistantLine(outputTokens: 0, terminal: false),
            AssistantLine(outputTokens: 7, terminal: true));

        UsageEvent partial = results[1].Event!;
        UsageEvent completed = results[2].Event!;
        Assert.AreEqual(partial.DedupKey, completed.DedupKey);
        Assert.AreEqual(CompletionState.Partial, partial.CompletionState);
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
    public void ParseLine_StoresOnlyBoundedPromptPreviewAndToolName()
    {
        string prompt = $"  {new string('界', 118)} 😀 tail  ";
        IReadOnlyList<ClaudeCodeParseResult> results = Parse(
            JsonSerializer.Serialize(new
            {
                type = "user",
                sessionId = "session-1",
                promptId = "prompt-1",
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

        UsageEventToolMetadata tool = Assert.ContainsSingle(results[1].EventTools);
        Assert.AreEqual("Read", tool.ToolName);
        Assert.AreEqual(results[1].Event!.DedupKey, tool.EventDedupKey);
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

        var collector = new ClaudeCodeCollector(claudeHome);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);

        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        Assert.AreEqual("claude-code", instance.AgentId);
        Assert.AreEqual("Claude Code CLI (Windows)", instance.DisplayName);
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
    public async Task DesktopCollector_ReportsTokensWithoutPromptAttribution()
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
                UserLine("private desktop prompt must not be retained"),
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
        Assert.HasCount(2, collected.Events);
        Assert.AreEqual(
            collected.Events[0].DedupKey,
            collected.Events[1].DedupKey);
        Assert.AreEqual(23L, collected.Events[1].Tokens.NormalizedTotal.Value);
        Assert.IsNull(collected.Events[1].TurnIdHash);
        Assert.IsEmpty(collected.Turns);
        Assert.IsTrue(collected.Sessions.All(value =>
            value.CompatibilityLevel == CompatibilityLevel.PartiallyCompatible));
        Assert.DoesNotContain(
            "private desktop prompt",
            string.Join(' ', collected.Events.Select(value => value.EventId)));
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

    private static IReadOnlyList<ClaudeCodeParseResult> Parse(params string[] lines)
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
                Context);
            results.Add(result);
            state = result.State;
            byteOffset += utf8.LongLength + 1;
        }

        return results;
    }

    private static async Task<IReadOnlyList<CollectedBatch>> CollectAsync(
        ClaudeCodeCollector collector,
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
        sessionId = "session-1",
        promptId = "prompt-1",
        cwd = @"C:\fixture\project",
        timestamp = "2026-08-01T01:00:00Z",
        message = new { role = "user", content }
    });

    private static string AssistantLine(long outputTokens, bool terminal) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
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
            sessionId = "desktop-session-1",
            cwd = @"C:\fixture\desktop-project",
            timestamp = "2026-08-01T01:00:01Z",
            entrypoint = "desktop-local-agent",
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
