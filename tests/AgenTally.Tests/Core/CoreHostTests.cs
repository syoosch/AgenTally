using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using AgenTally.Core.Hosting;
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

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CoreHostTests
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    public async Task RunAsync_RejectsUnknownArgumentsWithoutTouchingExplicitPaths()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string database = directory.File("invalid-args.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var host = new CoreHost(new StorageOptions(database), output: output);

        int exitCode = await host.RunAsync([
            "--check",
            "--codex-home",
            codexHome,
            "--database",
            database,
            "--unknown"
        ]);

        Assert.AreEqual(2, exitCode);
        Assert.Contains("参数错误", output.ToString());
        Assert.IsFalse(File.Exists(database));
    }

    [TestMethod]
    public async Task RunAsync_RebuildRejectsAnotherExecutionModeWithoutCreatingDatabase()
    {
        foreach (string conflictingMode in new[] { "--check", "--once" })
        {
            using var directory = new TestTempDirectory();
            string codexHome = directory.File(".codex");
            string database = directory.File("invalid-rebuild.db");
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            var host = new CoreHost(new StorageOptions(database), output: output);

            int exitCode = await host.RunAsync([
                "--rebuild-codex",
                conflictingMode,
                "--codex-home",
                codexHome,
                "--database",
                database
            ]);

            Assert.AreEqual(2, exitCode);
            Assert.Contains("不能同时使用", output.ToString());
            Assert.IsFalse(File.Exists(database));
        }
    }

    [TestMethod]
    public async Task RunAsync_RejectsDatabaseInsideCodexSourceTreesWithoutCreatingIt()
    {
        foreach (string sourceTree in new[] { "sessions", "archived_sessions" })
        {
            using var directory = new TestTempDirectory();
            string codexHome = directory.File(".codex");
            string database = Path.Combine(codexHome, sourceTree, "agentally.db");
            using var output = new StringWriter(CultureInfo.InvariantCulture);

            int exitCode = await new CoreHost(
                new StorageOptions(directory.File("default.db")),
                output: output).RunAsync([
                    "--once",
                    "--codex-home", codexHome,
                    "--database", database
                ]);

            Assert.AreEqual(2, exitCode);
            Assert.Contains("数据库不能位于 Codex 原始日志目录", output.ToString());
            Assert.IsFalse(File.Exists(database));
        }
    }

    [TestMethod]
    public async Task RunAsync_RejectsDatabaseInsideKimiSessionsWithoutCreatingIt()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string kimiHome = directory.File(".kimi-code");
        string database = Path.Combine(kimiHome, "sessions", "agentally.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await new CoreHost(
            new StorageOptions(directory.File("default.db")),
            output: output).RunAsync([
                "--once",
                "--codex-home", codexHome,
                "--kimi-home", kimiHome,
                "--database", database
            ]);

        Assert.AreEqual(2, exitCode);
        Assert.Contains("Kimi Code CLI sessions", output.ToString());
        Assert.IsFalse(File.Exists(database));
    }

    [TestMethod]
    public async Task RunAsync_RejectsDatabaseInsideKimiDesktopSessionsWithoutCreatingIt()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string kimiDesktopHome = directory.File("kimi-desktop-home");
        string database = Path.Combine(
            kimiDesktopHome,
            "sessions",
            "agentally.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await new CoreHost(
            new StorageOptions(directory.File("default.db")),
            output: output).RunAsync([
                "--once",
                "--codex-home", codexHome,
                "--kimi-desktop-home", kimiDesktopHome,
                "--database", database
            ]);

        Assert.AreEqual(2, exitCode);
        Assert.Contains("Kimi Work Desktop sessions", output.ToString());
        Assert.IsFalse(File.Exists(database));
    }

    [TestMethod]
    public async Task RunAsync_RejectsDatabaseInsideZcodeLedgerDirectoryWithoutCreatingIt()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string zcodeHome = directory.File(".zcode");
        string database = Path.Combine(zcodeHome, "cli", "db", "agentally.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await new CoreHost(
            new StorageOptions(directory.File("default.db")),
            output: output).RunAsync([
                "--once",
                "--codex-home", codexHome,
                "--zcode-home", zcodeHome,
                "--database", database
            ]);

        Assert.AreEqual(2, exitCode);
        Assert.Contains("ZCode usage database", output.ToString());
        Assert.IsFalse(File.Exists(database));
    }

    [TestMethod]
    public async Task RunAsync_RejectsDatabaseInsideWorkBuddyProjectsWithoutCreatingIt()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string workBuddyHome = directory.File(".workbuddy");
        string database = Path.Combine(
            workBuddyHome,
            "projects",
            "agentally.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await new CoreHost(
            new StorageOptions(directory.File("default.db")),
            output: output).RunAsync([
                "--once",
                "--codex-home", codexHome,
                "--workbuddy-home", workBuddyHome,
                "--database", database
            ]);

        Assert.AreEqual(2, exitCode);
        Assert.Contains("WorkBuddy projects", output.ToString());
        Assert.IsFalse(File.Exists(database));
    }

    [TestMethod]
    public async Task RunAsync_RejectsCodexHomeAliasBeforeDatabaseCreation()
    {
        using var directory = new TestTempDirectory();
        string realHome = directory.File("real-codex-home");
        Directory.CreateDirectory(Path.Combine(realHome, "sessions"));
        string aliasHome = directory.File("codex-home-alias");
        try
        {
            Directory.CreateSymbolicLink(aliasHome, realHome);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            Assert.Inconclusive("This host cannot create a directory symbolic link.");
        }

        string database = Path.Combine(realHome, "sessions", "agentally.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await new CoreHost(
            new StorageOptions(directory.File("default.db")),
            output: output).RunAsync([
                "--once",
                "--codex-home", aliasHome,
                "--database", database
            ]);

        Assert.AreEqual(2, exitCode);
        Assert.Contains("重解析点", output.ToString());
        Assert.IsFalse(File.Exists(database));
    }

    [TestMethod]
    public async Task RunAsync_CheckReportsKnownRootsWithoutParsingOrCreatingDatabase()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string kimiHome = directory.File(".kimi-code");
        string kimiDesktopHome = directory.File("kimi-desktop-home");
        string workBuddyHome = directory.File(".workbuddy");
        string geminiHome = directory.File(".gemini");
        string openCodeHome = directory.File("opencode");
        string sessions = Path.Combine(codexHome, "sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(Path.Combine(kimiHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(kimiDesktopHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(workBuddyHome, "projects"));
        Directory.CreateDirectory(Path.Combine(geminiHome, "tmp"));
        Directory.CreateDirectory(openCodeHome);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-invalid.jsonl"),
            "private fixture text that is not jsonl\n",
            Utf8WithoutBom);
        string database = directory.File("check.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var host = new CoreHost(new StorageOptions(database), output: output);

        int exitCode = await host.RunAsync([
            "--check",
            "--codex-home",
            codexHome,
            "--kimi-home",
            kimiHome,
            "--kimi-desktop-home",
            kimiDesktopHome,
            "--workbuddy-home",
            workBuddyHome,
            "--gemini-home",
            geminiHome,
            "--opencode-home",
            openCodeHome,
            "--database",
            database
        ]);

        string text = output.ToString();
        Assert.AreEqual(0, exitCode);
        Assert.Contains(Path.GetFullPath(database), text);
        Assert.Contains("sessions：存在", text);
        Assert.Contains("archived_sessions：不存在", text);
        Assert.Contains("Kimi Code CLI sessions：存在", text);
        Assert.Contains("Kimi Work Desktop sessions：存在", text);
        Assert.Contains("ZCode usage database：不存在", text);
        Assert.Contains("WorkBuddy projects：存在", text);
        Assert.Contains("Gemini CLI tmp：存在", text);
        Assert.Contains("OpenCode data：存在", text);
        Assert.DoesNotContain("private fixture text", text);
        Assert.IsFalse(File.Exists(database));
    }

    [TestMethod]
    public async Task RunAsync_OnceImportsIncrementallyWithExplicitFixturePaths()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-2026-07-16T01-00-thread-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(
            rollout,
            await File.ReadAllTextAsync(Path.Combine(
                FixtureDirectory,
                "basic-rollout.jsonl")),
            Utf8WithoutBom);
        string database = directory.File("once.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int firstExit = await new CoreHost(
            new StorageOptions(database),
            output: output).RunAsync([
                "--once",
                "--codex-home",
                codexHome,
                "--database",
                database
            ]);
        await File.AppendAllTextAsync(
            rollout,
            "{\"timestamp\":\"2026-07-16T01:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":5,\"output_tokens\":4,\"reasoning_output_tokens\":2,\"total_tokens\":14},\"total_token_usage\":{\"input_tokens\":140,\"cached_input_tokens\":75,\"output_tokens\":32,\"reasoning_output_tokens\":9,\"total_tokens\":172}}}}\n",
            Utf8WithoutBom);
        int secondExit = await new CoreHost(
            new StorageOptions(database),
            output: output).RunAsync([
                "--once",
                "--codex-home",
                codexHome,
                "--database",
                database
            ]);

        var queries = new SqliteUsageQueryService(
            new SqliteConnectionFactory(new StorageOptions(database)));
        UsageOverview overview = await queries.GetOverviewAsync(
            new UsageFilter(
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
                agentId: "codex"),
            CancellationToken.None);

        Assert.AreEqual(0, firstExit);
        Assert.AreEqual(0, secondExit);
        Assert.AreEqual(3L, overview.RequestCount);
        Assert.Contains("已应用事件：2", output.ToString());
        Assert.Contains("已应用事件：1", output.ToString());
    }

    [TestMethod]
    public async Task RunAsync_OnceImportsClaudeAndKimiCodeSourcesAlongsideCodex()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string claudeHome = directory.File(".claude");
        string transcript = Path.Combine(
            claudeHome,
            "projects",
            "project-a",
            "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        string[] records =
        [
            JsonSerializer.Serialize(new
            {
                type = "user",
                sessionId = "claude-session",
                promptId = "claude-prompt",
                cwd = @"C:\fixture\claude-project",
                timestamp = "2026-08-01T01:00:00Z",
                message = new { role = "user", content = "Run one safe check." }
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                sessionId = "claude-session",
                cwd = @"C:\fixture\claude-project",
                timestamp = "2026-08-01T01:00:01Z",
                entrypoint = "cli",
                message = new
                {
                    role = "assistant",
                    id = "claude-message",
                    model = "test-claude-model",
                    stop_reason = "end_turn",
                    usage = new
                    {
                        input_tokens = 10,
                        cache_read_input_tokens = 4,
                        cache_creation_input_tokens = 2,
                        output_tokens = 7
                    },
                    content = Array.Empty<object>()
                }
            })
        ];
        await File.WriteAllTextAsync(
            transcript,
            string.Join(Environment.NewLine, records) + Environment.NewLine,
            Utf8WithoutBom);
        string desktopRoot = directory.File("local-agent-mode-sessions");
        string desktopTranscript = Path.Combine(
            desktopRoot,
            "desktop-session",
            "vm",
            "workspace",
            ".claude",
            "projects",
            "project-key",
            $"session_{Guid.NewGuid():D}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(desktopTranscript)!);
        await File.WriteAllTextAsync(
            desktopTranscript,
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                sessionId = "desktop-session",
                cwd = @"C:\fixture\desktop-project",
                timestamp = "2026-08-01T01:30:01Z",
                entrypoint = "desktop-local-agent",
                message = new
                {
                    role = "assistant",
                    id = "desktop-message",
                    model = "test-claude-model",
                    stop_reason = "end_turn",
                    usage = new
                    {
                        input_tokens = 10,
                        cache_read_input_tokens = 4,
                        cache_creation_input_tokens = 2,
                        output_tokens = 7
                    },
                    content = Array.Empty<object>()
                }
            }) + Environment.NewLine,
            Utf8WithoutBom);
        string kimiHome = directory.File(".kimi-code");
        string kimiSessionDirectory = Path.Combine(
            kimiHome,
            "sessions",
            "project-a",
            "session_kimi-session");
        string kimiWire = Path.Combine(
            kimiSessionDirectory,
            "agents",
            "main",
            "wire.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(kimiWire)!);
        long kimiCreatedAt = new DateTimeOffset(
            2026,
            8,
            1,
            2,
            0,
            0,
            TimeSpan.Zero).ToUnixTimeMilliseconds();
        await File.WriteAllTextAsync(
            Path.Combine(kimiSessionDirectory, "state.json"),
            JsonSerializer.Serialize(new
            {
                createdAt = kimiCreatedAt,
                updatedAt = kimiCreatedAt + 500,
                title = "Kimi fixture",
                isCustomTitle = false,
                agents = new Dictionary<string, object>
                {
                    ["main"] = new
                    {
                        homedir = @"C:\fixture\home",
                        type = "main",
                        parentAgentId = (string?)null
                    }
                },
                custom = new { },
                workDir = @"C:\fixture\kimi-project"
            }),
            Utf8WithoutBom);
        string[] kimiRecords =
        [
            JsonSerializer.Serialize(new
            {
                type = "metadata",
                protocol_version = "1.4",
                created_at = kimiCreatedAt
            }),
            JsonSerializer.Serialize(new
            {
                type = "turn.prompt",
                input = new[] { new { type = "text", text = "Run one safe check." } },
                origin = "user",
                time = kimiCreatedAt + 100
            }),
            JsonSerializer.Serialize(new
            {
                type = "context.append_loop_event",
                @event = new
                {
                    type = "step.begin",
                    uuid = "kimi-step",
                    turnId = "kimi-turn",
                    step = 1
                },
                time = kimiCreatedAt + 200
            }),
            JsonSerializer.Serialize(new
            {
                type = "context.append_loop_event",
                @event = new
                {
                    type = "step.end",
                    uuid = "kimi-step",
                    turnId = "kimi-turn",
                    step = 1,
                    usage = new
                    {
                        inputOther = 10,
                        output = 7,
                        inputCacheRead = 4,
                        inputCacheCreation = 2
                    },
                    finishReason = "stop",
                    messageId = "kimi-message"
                },
                time = kimiCreatedAt + 300
            }),
            JsonSerializer.Serialize(new
            {
                type = "usage.record",
                model = "kimi-code/k3-256k",
                usage = new
                {
                    inputOther = 10,
                    output = 7,
                    inputCacheRead = 4,
                    inputCacheCreation = 2
                },
                usageScope = "turn",
                time = kimiCreatedAt + 301
            })
        ];
        await File.WriteAllTextAsync(
            kimiWire,
            string.Join(Environment.NewLine, kimiRecords) + Environment.NewLine,
            Utf8WithoutBom);
        string kimiDesktopHome = directory.File("kimi-desktop-home");
        string kimiDesktopSessionDirectory = Path.Combine(
            kimiDesktopHome,
            "sessions",
            "project-a",
            "conv-kimi-session");
        string kimiDesktopWire = Path.Combine(
            kimiDesktopSessionDirectory,
            "agents",
            "main",
            "wire.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(kimiDesktopWire)!);
        await File.WriteAllTextAsync(
            Path.Combine(kimiDesktopSessionDirectory, "state.json"),
            JsonSerializer.Serialize(new
            {
                createdAt = kimiCreatedAt,
                updatedAt = kimiCreatedAt + 500,
                title = "Kimi Desktop fixture",
                isCustomTitle = false,
                agents = new Dictionary<string, object>
                {
                    ["main"] = new
                    {
                        homedir = @"C:\fixture\home",
                        type = "main",
                        parentAgentId = (string?)null
                    }
                },
                custom = new { },
                workDir = @"C:\fixture\kimi-project"
            }),
            Utf8WithoutBom);
        await File.WriteAllTextAsync(
            kimiDesktopWire,
            string.Join(Environment.NewLine, kimiRecords) + Environment.NewLine,
            Utf8WithoutBom);
        string database = directory.File("claude-once.db");

        int exitCode = await new CoreHost(new StorageOptions(database)).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--claude-home", claudeHome,
            "--claude-desktop-root", desktopRoot,
            "--kimi-home", kimiHome,
            "--kimi-desktop-home", kimiDesktopHome,
            "--database", database
        ]);

        var queries = new SqliteUsageQueryService(
            new SqliteConnectionFactory(new StorageOptions(database)));
        UsageOverview overview = await queries.GetOverviewAsync(
            new UsageFilter(
                new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                agentId: "claude-code"),
            CancellationToken.None);
        UsageOverview kimiOverview = await queries.GetOverviewAsync(
            new UsageFilter(
                new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                agentId: "kimi-code"),
            CancellationToken.None);
        UsageOverview kimiWorkOverview = await queries.GetOverviewAsync(
            new UsageFilter(
                new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                agentId: "kimi-work"),
            CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(2L, overview.RequestCount);
        Assert.AreEqual(46L, overview.NormalizedTotal.Value);
        Assert.AreEqual(1L, kimiOverview.RequestCount);
        Assert.AreEqual(23L, kimiOverview.NormalizedTotal.Value);
        Assert.AreEqual(1L, kimiWorkOverview.RequestCount);
        Assert.AreEqual(23L, kimiWorkOverview.NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task RunAsync_RescanAcceptsValidSessionIdentityBoundaryPrecisely()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-valid-session-boundary.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        string content = string.Join('\n',
        [
            "{\"timestamp\":\"2026-07-16T01:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"first-thread\",\"model_provider\":\"openai\",\"cwd\":\"C:\\\\fixture\\\\first\"}}",
            "{\"timestamp\":\"2026-07-16T01:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":2,\"cached_input_tokens\":1,\"output_tokens\":1,\"total_tokens\":3}}}}",
            "{\"timestamp\":\"2026-07-16T01:01:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"second-thread\",\"model_provider\":\"openai\",\"cwd\":\"C:\\\\fixture\\\\second\"}}",
            "{\"timestamp\":\"2026-07-16T01:01:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":5,\"cached_input_tokens\":2,\"output_tokens\":2,\"total_tokens\":7}}}}"
        ]) + "\n";
        await File.WriteAllTextAsync(rollout, content, Utf8WithoutBom);
        string database = directory.File("valid-session-boundary.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await new CoreHost(
            new StorageOptions(database),
            output: output).RunAsync([
                "--rescan-codex",
                "--codex-home", codexHome,
                "--database", database
            ]);

        var queries = new SqliteUsageQueryService(
            new SqliteConnectionFactory(new StorageOptions(database)));
        UsageOverview overview = await queries.GetOverviewAsync(
            new UsageFilter(
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
                agentId: "codex"),
            CancellationToken.None);
        Assert.AreEqual(0, exitCode, output.ToString());
        Assert.AreEqual(2L, overview.RequestCount);
        Assert.AreEqual(10L, overview.NormalizedTotal.Value);
        Assert.AreEqual(MetricCoverageStatus.Complete, overview.NormalizedTotal.Coverage);
        Assert.Contains("已应用事件：2", output.ToString());
    }

    [TestMethod]
    public async Task RunAsync_RescanStillRejectsMalformedSessionIdentity()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-invalid-session-boundary.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(
            rollout,
            await File.ReadAllTextAsync(Path.Combine(
                FixtureDirectory,
                "basic-rollout.jsonl")),
            Utf8WithoutBom);
        string database = directory.File("invalid-session-boundary.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        await File.AppendAllTextAsync(
            rollout,
            "{\"timestamp\":\"2026-07-16T01:05:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":null}}\n",
            Utf8WithoutBom);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rescanExit = await new CoreHost(
            storage,
            output: output).RunAsync([
                "--rescan-codex",
                "--codex-home", codexHome,
                "--database", database
            ]);

        var connections = new SqliteConnectionFactory(storage);
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(1, rescanExit);
        Assert.AreEqual(
            2L,
            await CountParserRowsAsync(
                connections,
                AgenTally.Core.Collectors.Codex.CodexRolloutParser
                    .CurrentParserVersion));
        Assert.Contains("主数据库保持不变", output.ToString());
    }

    [TestMethod]
    public async Task RunAsync_RescanPreservesMissingHistoricalEntity()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rolloutA = await WriteCodexRolloutAsync(
            codexHome,
            "rollout-a.jsonl",
            "thread-a",
            1);
        string rolloutB = await WriteCodexRolloutAsync(
            codexHome,
            "rollout-b.jsonl",
            "thread-b",
            2);
        string database = directory.File("missing-history.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> before =
            await ReadCodexEntityUsageAsync(connections);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE usage_events
                SET parser_version = 'legacy-parser-fixture';

                UPDATE source_cursors
                SET parser_version = 'legacy-parser-fixture';
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        File.Delete(rolloutB);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rescan-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);
        using var incrementalOutput =
            new StringWriter(CultureInfo.InvariantCulture);
        int incrementalExit =
            await new CoreHost(storage, output: incrementalOutput).RunAsync([
                "--once",
                "--codex-home", codexHome,
                "--database", database
            ]);

        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> after =
            await ReadCodexEntityUsageAsync(connections);
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(2, before.Count);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.AreEqual(0, incrementalExit, incrementalOutput.ToString());
        Assert.Contains("数据库独有历史", output.ToString());
        AssertEntityUsageEqual(before, after);
        Assert.IsGreaterThan(
            0L,
            await CountParserRowsAsync(connections, "legacy-parser-fixture"));
        Assert.IsGreaterThan(
            0L,
            await CountParserRowsAsync(
                connections,
                AgenTally.Core.Collectors.Codex.CodexRolloutParser
                    .CurrentParserVersion));
        Assert.IsTrue(File.Exists(rolloutA));
        Assert.IsFalse(File.Exists(rolloutB));
    }

    [TestMethod]
    public async Task RunAsync_RescanPreservesEventsMissingFromTruncatedSource()
    {
        using var directory = new TestTempDirectory();
        const string privateMarker = "private-truncated-thread";
        string codexHome = directory.File(".codex");
        string rollout = await WriteCodexRolloutAsync(
            codexHome,
            "private-truncated-rollout.jsonl",
            privateMarker,
            1);
        string database = directory.File("truncated-history.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> before =
            await ReadCodexEntityUsageAsync(connections);
        string original = await File.ReadAllTextAsync(rollout);
        int firstNewline = original.IndexOf('\n', StringComparison.Ordinal);
        Assert.IsGreaterThan(0, firstNewline);
        await File.WriteAllTextAsync(
            rollout,
            original[..(firstNewline + 1)],
            Utf8WithoutBom);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rescan-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> after =
            await ReadCodexEntityUsageAsync(connections);
        string message = output.ToString();
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rebuildExit, message);
        Assert.Contains("数据库独有历史", message);
        Assert.DoesNotContain(rollout, message);
        Assert.DoesNotContain(privateMarker, message);
        AssertEntityUsageEqual(before, after);
        Assert.IsEmpty(Directory.EnumerateFiles(
            directory.Path,
            $".{Path.GetFileName(database)}.codex-rebuild-*.tmp*"));
    }

    [TestMethod]
    public async Task RunAsync_RescanSafelyMergesReplacedHistoricalSource()
    {
        using var directory = new TestTempDirectory();
        const string privateMarker = "private-replacement-content";
        string codexHome = directory.File(".codex");
        string rollout = await WriteCodexRolloutAsync(
            codexHome,
            "private-replaced-rollout.jsonl",
            "original-thread",
            1);
        string database = directory.File("replaced-history.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> before =
            await ReadCodexEntityUsageAsync(connections);
        string replacement = (await File.ReadAllTextAsync(
                Path.Combine(FixtureDirectory, "basic-rollout.jsonl")))
            .Replace("thread-1", privateMarker, StringComparison.Ordinal);
        await File.WriteAllTextAsync(rollout, replacement, Utf8WithoutBom);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rescan-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> after =
            await ReadCodexEntityUsageAsync(connections);
        string message = output.ToString();
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rebuildExit, message);
        Assert.Contains("原子合并", message);
        Assert.DoesNotContain(rollout, message);
        Assert.DoesNotContain(privateMarker, message);
        AssertEntityUsageEqual(before, after);
    }

    [TestMethod]
    public async Task RunAsync_RebuildCompletesWhenHistoricalSourceOnlyAppends()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = await WriteCodexRolloutAsync(
            codexHome,
            "appended-rollout.jsonl",
            "appended-thread",
            1);
        string database = directory.File("appended-history.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> before =
            await ReadCodexEntityUsageAsync(connections);
        await File.AppendAllTextAsync(
            rollout,
            "{\"timestamp\":\"2026-07-16T01:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":5,\"output_tokens\":4,\"reasoning_output_tokens\":2,\"total_tokens\":14},\"total_token_usage\":{\"input_tokens\":140,\"cached_input_tokens\":75,\"output_tokens\":32,\"reasoning_output_tokens\":9,\"total_tokens\":172}}}}\n",
            Utf8WithoutBom);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rebuild-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> after =
            await ReadCodexEntityUsageAsync(connections);
        EntityUsageAggregate beforeAggregate = Assert.ContainsSingle(before).Value;
        EntityUsageAggregate afterAggregate = Assert.ContainsSingle(after).Value;
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.AreEqual(beforeAggregate.EventCount + 1, afterAggregate.EventCount);
        Assert.AreEqual(
            beforeAggregate.NormalizedTotal + 14,
            afterAggregate.NormalizedTotal);
        Assert.Contains("原子合并", output.ToString());
    }

    [TestMethod]
    public async Task RunAsync_RescanRepairsHistoricalSourceWithoutValidCommittedCursor()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        await WriteCodexRolloutAsync(
            codexHome,
            "invalid-cursor-rollout.jsonl",
            "invalid-cursor-thread",
            1);
        string database = directory.File("invalid-cursor-history.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE source_cursors
                SET cursor_json = '{}'
                WHERE source_instance_id IN (
                    SELECT source_instance_id
                    FROM source_instances
                    WHERE agent_id = 'codex'
                );
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        SourceDatabaseStateRow[] before =
            await ReadCodexSourceDatabaseStateAsync(connections);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rescan-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        SourceDatabaseStateRow[] after =
            await ReadCodexSourceDatabaseStateAsync(connections);
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.Contains("原子合并", output.ToString());
        Assert.HasCount(before.Length, after);
        Assert.AreEqual(
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion,
            Assert.ContainsSingle(after).ParserVersion);
    }

    [TestMethod]
    public async Task RunAsync_RebuildContinuityCheckIgnoresStoredParserVersion()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        await WriteCodexRolloutAsync(
            codexHome,
            "older-parser-rollout.jsonl",
            "older-parser-thread",
            1);
        string database = directory.File("older-parser-history.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE source_cursors
                SET parser_version = 'older-parser-version'
                WHERE source_instance_id IN (
                    SELECT source_instance_id
                    FROM source_instances
                    WHERE agent_id = 'codex'
                );
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rebuild-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        SourceDatabaseStateRow state = Assert.ContainsSingle(
            await ReadCodexSourceDatabaseStateAsync(connections));
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.AreEqual(
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion,
            state.ParserVersion);
        Assert.Contains("原子合并", output.ToString());
    }

    [TestMethod]
    public async Task RunAsync_RebuildCompletesWhenEveryHistoricalEntityIsPresent()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        await WriteCodexRolloutAsync(
            codexHome,
            "rollout-a.jsonl",
            "thread-a",
            1);
        await WriteCodexRolloutAsync(
            codexHome,
            "rollout-b.jsonl",
            "thread-b",
            2);
        string database = directory.File("complete-history.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> before =
            await ReadCodexEntityUsageAsync(connections);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rebuild-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> after =
            await ReadCodexEntityUsageAsync(connections);
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(2, before.Count);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.Contains("原子合并", output.ToString());
        AssertEntityUsageEqual(before, after);
    }

    [TestMethod]
    public async Task RunAsync_RebuildAllowsEmptyCurrentSourceWhenDatabaseHasNoUsageHistory()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        string database = directory.File("empty-history.db");
        var storage = new StorageOptions(database);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rebuild-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        var connections = new SqliteConnectionFactory(storage);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.Contains("原子合并", output.ToString());
        Assert.AreEqual(
            0,
            (await ReadCodexEntityUsageAsync(connections)).Count);
    }

    [TestMethod]
    public async Task RunAsync_ClearStatisticsInstallsEofBaselineAndKeepsOnlyFutureUsage()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = await WriteCodexRolloutAsync(
            codexHome,
            "clear-statistics-rollout.jsonl",
            "clear-statistics-thread",
            1);
        string database = directory.File("clear-statistics.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        Assert.AreEqual(2L, await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion));
        using var clearOutput = new StringWriter(CultureInfo.InvariantCulture);

        int clearExit = await new CoreHost(
            storage,
            output: clearOutput).RunAsync([
                "--clear-statistics",
                "--codex-home", codexHome,
                "--database", database
            ]);

        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, clearExit, clearOutput.ToString());
        Assert.Contains("EOF 基线", clearOutput.ToString());
        Assert.AreEqual(0L, await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion));
        await File.AppendAllTextAsync(
            rollout,
            "{\"timestamp\":\"2026-07-16T01:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":5,\"output_tokens\":4,\"reasoning_output_tokens\":2,\"total_tokens\":14},\"total_token_usage\":{\"input_tokens\":140,\"cached_input_tokens\":75,\"output_tokens\":32,\"reasoning_output_tokens\":9,\"total_tokens\":172}}}}\n",
            Utf8WithoutBom);

        int incrementalExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);

        Assert.AreEqual(0, incrementalExit);
        Assert.AreEqual(1L, await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion));
    }

    [TestMethod]
    public async Task RunAsync_RescanAndClearCoverClaudeCliAndDesktopBaselines()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        string claudeHome = directory.File(".claude");
        string cliTranscript = Path.Combine(
            claudeHome,
            "projects",
            "project",
            "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(cliTranscript)!);
        await File.WriteAllTextAsync(
            cliTranscript,
            ClaudeAssistantRecord(
                "cli-session",
                "cli-message-1",
                "2026-08-01T01:00:00Z",
                "cli"),
            Utf8WithoutBom);
        string desktopRoot = directory.File("local-agent-mode-sessions");
        string desktopTranscript = Path.Combine(
            desktopRoot,
            "desktop-session",
            "vm",
            "workspace",
            ".claude",
            "projects",
            "project-key",
            $"session_{Guid.NewGuid():D}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(desktopTranscript)!);
        await File.WriteAllTextAsync(
            desktopTranscript,
            ClaudeAssistantRecord(
                "desktop-session",
                "desktop-message-1",
                "2026-08-01T01:30:00Z",
                "desktop-local-agent"),
            Utf8WithoutBom);
        string kimiHome = directory.File(".kimi-code");
        string database = directory.File("all-agent-maintenance.db");
        string[] sourceArguments =
        [
            "--codex-home", codexHome,
            "--claude-home", claudeHome,
            "--claude-desktop-root", desktopRoot,
            "--kimi-home", kimiHome,
            "--database", database
        ];

        int seedExit = await new CoreHost(new StorageOptions(database)).RunAsync(
            ["--once", .. sourceArguments]);
        var connections = new SqliteConnectionFactory(new StorageOptions(database));
        using var rescanOutput = new StringWriter(CultureInfo.InvariantCulture);
        int rescanExit = await new CoreHost(
            new StorageOptions(database),
            output: rescanOutput).RunAsync(["--rescan-codex", .. sourceArguments]);

        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rescanExit, rescanOutput.ToString());
        Assert.Contains("全部 Agent 统计", rescanOutput.ToString());
        Assert.AreEqual(2L, await CountAgentRowsAsync(connections, "claude-code"));

        int clearExit = await new CoreHost(new StorageOptions(database)).RunAsync(
            ["--clear-statistics", .. sourceArguments]);

        Assert.AreEqual(0, clearExit);
        Assert.AreEqual(0L, await CountAgentRowsAsync(connections, "claude-code"));

        await File.AppendAllTextAsync(
            cliTranscript,
            ClaudeAssistantRecord(
                "cli-session",
                "cli-message-2",
                "2026-08-01T02:00:00Z",
                "cli"),
            Utf8WithoutBom);
        await File.AppendAllTextAsync(
            desktopTranscript,
            ClaudeAssistantRecord(
                "desktop-session",
                "desktop-message-2",
                "2026-08-01T02:30:00Z",
                "desktop-local-agent"),
            Utf8WithoutBom);

        int incrementalExit = await new CoreHost(new StorageOptions(database)).RunAsync(
            ["--once", .. sourceArguments]);

        Assert.AreEqual(0, incrementalExit);
        Assert.AreEqual(2L, await CountAgentRowsAsync(connections, "claude-code"));
    }

    [TestMethod]
    public async Task RunAsync_ClearKeepsCustomPriceAndRescanUsesItForRecoveredEvents()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        await WriteCodexRolloutAsync(
            codexHome,
            "priced-rescan-rollout.jsonl",
            "priced-rescan-thread",
            1);
        string database = directory.File("priced-rescan.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        var ledger = new SqlitePriceLedger(connections);
        int initiallyPriced = await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "gpt-test",
                2m,
                0.2m,
                null,
                8m),
            CancellationToken.None);

        int clearExit = await new CoreHost(storage).RunAsync([
            "--clear-statistics",
            "--codex-home", codexHome,
            "--database", database
        ]);
        int rescanExit = await new CoreHost(storage).RunAsync([
            "--rescan-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE
                    WHEN pricing_status = 1
                     AND price_catalog_version = 'user-v1'
                    THEN 1 ELSE 0
                END),
                (SELECT COUNT(*) FROM pricing_overrides)
            FROM usage_events;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(2, initiallyPriced);
        Assert.AreEqual(0, clearExit);
        Assert.AreEqual(0, rescanExit);
        Assert.AreEqual(2L, reader.GetInt64(0));
        Assert.AreEqual(2L, reader.GetInt64(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
    }

    [TestMethod]
    public async Task RunAsync_IncrementalSyncDoesNotDeleteUsageForMissingSourceFile()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        await WriteCodexRolloutAsync(
            codexHome,
            "rollout-a.jsonl",
            "thread-a",
            1);
        string rolloutB = await WriteCodexRolloutAsync(
            codexHome,
            "rollout-b.jsonl",
            "thread-b",
            2);
        string database = directory.File("incremental-missing.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> before =
            await ReadCodexEntityUsageAsync(connections);
        File.Delete(rolloutB);

        int incrementalExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);

        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> after =
            await ReadCodexEntityUsageAsync(connections);
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(2, before.Count);
        Assert.AreEqual(0, incrementalExit);
        AssertEntityUsageEqual(before, after);
    }

    [TestMethod]
    public async Task RunAsync_OldParserDataIsBlockedUntilExplicitCodexRebuild()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-2026-07-16T01-00-thread-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(
            rollout,
            await File.ReadAllTextAsync(Path.Combine(
                FixtureDirectory,
                "basic-rollout.jsonl")),
            Utf8WithoutBom);
        string database = directory.File("rebuild.db");
        var storage = new StorageOptions(database);

        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        var writer = new SqliteUsageWriter(connections);
        DateTimeOffset checkedAt = new(2026, 7, 16, 2, 0, 0, TimeSpan.Zero);
        var unrelatedInstance = new SourceInstanceDescriptor(
            "mock:windows:unrelated",
            "mock",
            SourceKind.Jsonl,
            "Mock unrelated",
            directory.File("mock-source"));
        var unrelatedEntity = new SourceEntityDescriptor(
            unrelatedInstance.SourceInstanceId,
            "mock:entity:unrelated",
            directory.File("mock-source.jsonl"));
        await writer.CommitAsync(
            new UsageEventBatch(
                unrelatedInstance,
                unrelatedEntity,
                "mock-cursor",
                "mock-fingerprint",
                "mock-parser-v1",
                checkedAt,
                [CreateUnrelatedEvent(checkedAt)]),
            CancellationToken.None);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE usage_events
                SET dedup_key = 'codex:legacy:' || source_revision,
                    event_id = 'legacy:' || source_revision,
                    parser_version = 'codex-rollout-v1'
                WHERE agent_id = 'codex';
                UPDATE source_cursors
                SET parser_version = 'codex-rollout-v1'
                WHERE source_instance_id <> 'mock:windows:unrelated';
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        using var blockedOutput = new StringWriter(CultureInfo.InvariantCulture);
        int blockedExit = await new CoreHost(
            storage,
            output: blockedOutput).RunAsync([
                "--once",
                "--codex-home", codexHome,
                "--database", database
            ]);
        long currentRowsBeforeRebuild = await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion);
        using var rebuildOutput = new StringWriter(CultureInfo.InvariantCulture);
        int rebuildExit = await new CoreHost(
            storage,
            output: rebuildOutput).RunAsync([
                "--rebuild-codex",
                "--codex-home", codexHome,
                "--database", database
            ]);

        var queries = new SqliteUsageQueryService(connections);
        UsageOverview overview = await queries.GetOverviewAsync(
            new UsageFilter(
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
                agentId: "codex"),
            CancellationToken.None);
        long legacyRows = await CountParserRowsAsync(connections, "codex-rollout-v1");
        long currentRows = await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion);
        long unrelatedRows = await CountAgentRowsAsync(connections, "mock");

        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(3, blockedExit);
        Assert.Contains("请显式运行 --rescan-codex", blockedOutput.ToString());
        Assert.AreEqual(0L, currentRowsBeforeRebuild);
        Assert.AreEqual(0, rebuildExit);
        Assert.Contains("Codex 原始文件未被修改", rebuildOutput.ToString());
        Assert.AreEqual(2L, overview.RequestCount);
        Assert.AreEqual(0L, legacyRows);
        Assert.AreEqual(2L, currentRows);
        Assert.AreEqual(1L, unrelatedRows);
    }

    [TestMethod]
    public async Task RunAsync_RescanRemovesLegacyEventSuppressedByCurrentParser()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-2026-07-16T04-00-child-thread.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(
            rollout,
            await File.ReadAllTextAsync(Path.Combine(
                FixtureDirectory,
                "subagent-replay.jsonl")),
            Utf8WithoutBom);
        string database = directory.File("suppressed-replay.db");
        var storage = new StorageOptions(database);

        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        var connections = new SqliteConnectionFactory(storage);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE usage_events
                SET event_id = 'legacy-replay-event',
                    dedup_key = 'legacy-replay-dedup',
                    parser_version = 'legacy-replay-parser',
                    source_revision = 1
                WHERE agent_id = 'codex';

                UPDATE source_cursors
                SET parser_version = 'legacy-replay-parser'
                WHERE source_instance_id IN (
                    SELECT source_instance_id
                    FROM source_instances
                    WHERE agent_id = 'codex'
                );
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rescan-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        var queries = new SqliteUsageQueryService(connections);
        UsageOverview overview = await queries.GetOverviewAsync(
            new UsageFilter(
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
                agentId: "codex"),
            CancellationToken.None);
        long legacyRows = await CountParserRowsAsync(
            connections,
            "legacy-replay-parser");
        long currentRows = await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser
                .CurrentParserVersion);

        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.Contains("原子合并", output.ToString());
        Assert.AreEqual(0L, legacyRows);
        Assert.AreEqual(1L, currentRows);
        Assert.AreEqual(1L, overview.RequestCount);
        Assert.AreEqual(11L, overview.NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task RunAsync_RescanPreservesDerivedDataWhenSourceIsEmpty()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-2026-07-16T01-00-missing.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(
            rollout,
            await File.ReadAllTextAsync(Path.Combine(
                FixtureDirectory,
                "basic-rollout.jsonl")),
            Utf8WithoutBom);
        string database = directory.File("empty-source.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        File.Delete(rollout);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rescan-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        var connections = new SqliteConnectionFactory(storage);
        long rows = await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion);
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(0, rebuildExit, output.ToString());
        Assert.Contains("数据库独有历史", output.ToString());
        Assert.AreEqual(2L, rows);
    }

    [TestMethod]
    public async Task RunAsync_RebuildFailureKeepsExistingDerivedData()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-rebuild-failure.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(
            rollout,
            await File.ReadAllTextAsync(Path.Combine(
                FixtureDirectory,
                "basic-rollout.jsonl")),
            Utf8WithoutBom);
        string database = directory.File("atomic-rebuild.db");
        var storage = new StorageOptions(database);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--database", database
        ]);
        await File.WriteAllTextAsync(
            rollout,
            new string('x', (64 * 1024) + 1) + "\n",
            Utf8WithoutBom);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int rebuildExit = await new CoreHost(storage, output: output).RunAsync([
            "--rebuild-codex",
            "--codex-home", codexHome,
            "--database", database
        ]);

        var connections = new SqliteConnectionFactory(storage);
        long rows = await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion);
        Assert.AreEqual(0, seedExit);
        Assert.AreEqual(1, rebuildExit);
        Assert.AreEqual(2L, rows);
        Assert.Contains("主数据库保持不变", output.ToString());
        Assert.IsEmpty(Directory.EnumerateFiles(
            directory.Path,
            $".{Path.GetFileName(database)}.codex-rebuild-*.tmp*"));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task RunAsync_RebuildContinuesPastCollectorBatchLimitWithoutChangingRollout()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-2026-07-16T01-00-large.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);

        var content = new StringBuilder();
        content.AppendLine(
            "{\"timestamp\":\"2026-07-16T01:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"large-thread\",\"model_provider\":\"openai\"}}");
        DateTimeOffset startedAt = new(2026, 7, 16, 1, 0, 1, TimeSpan.Zero);
        const int tokenEventCount = 5_201;
        for (int index = 1; index <= tokenEventCount; index++)
        {
            content.Append("{\"timestamp\":\"")
                .Append(startedAt.AddMilliseconds(index).ToString("O", CultureInfo.InvariantCulture))
                .Append("\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":0,\"total_tokens\":1},\"total_token_usage\":{\"input_tokens\":")
                .Append(index)
                .Append(",\"cached_input_tokens\":0,\"output_tokens\":0,\"total_tokens\":")
                .Append(index)
                .AppendLine("}}}}");
        }

        await File.WriteAllTextAsync(rollout, content.ToString(), Utf8WithoutBom);
        byte[] beforeBytes = await File.ReadAllBytesAsync(rollout);
        byte[] beforeHash = SHA256.HashData(beforeBytes);
        DateTime beforeWriteTime = File.GetLastWriteTimeUtc(rollout);
        string database = directory.File("large-rebuild.db");
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await new CoreHost(
            new StorageOptions(database),
            output: output).RunAsync([
                "--rebuild-codex",
                "--codex-home", codexHome,
                "--database", database
            ]);

        byte[] afterBytes = await File.ReadAllBytesAsync(rollout);
        var connections = new SqliteConnectionFactory(new StorageOptions(database));
        long currentRows = await CountParserRowsAsync(
            connections,
            AgenTally.Core.Collectors.Codex.CodexRolloutParser.CurrentParserVersion);

        Assert.AreEqual(0, exitCode, output.ToString());
        Assert.AreEqual(tokenEventCount, currentRows);
        Assert.AreEqual(beforeBytes.LongLength, afterBytes.LongLength);
        CollectionAssert.AreEqual(beforeHash, SHA256.HashData(afterBytes));
        Assert.AreEqual(beforeWriteTime, File.GetLastWriteTimeUtc(rollout));
        Assert.Contains("已应用事件：5201", output.ToString());
    }

    private static async Task<long> CountParserRowsAsync(
        SqliteConnectionFactory connections,
        string parserVersion)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM usage_events
            WHERE parser_version = $parser_version;
            """;
        command.Parameters.AddWithValue("$parser_version", parserVersion);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<long> CountAgentRowsAsync(
        SqliteConnectionFactory connections,
        string agentId)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_events WHERE agent_id = $agent_id;";
        command.Parameters.AddWithValue("$agent_id", agentId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static string ClaudeAssistantRecord(
        string sessionId,
        string messageId,
        string timestamp,
        string entrypoint) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId,
            cwd = @"C:\fixture\claude-project",
            timestamp,
            entrypoint,
            message = new
            {
                role = "assistant",
                id = messageId,
                model = "test-claude-model",
                stop_reason = "end_turn",
                usage = new
                {
                    input_tokens = 10,
                    cache_read_input_tokens = 4,
                    cache_creation_input_tokens = 2,
                    output_tokens = 7
                },
                content = Array.Empty<object>()
            }
        }) + Environment.NewLine;

    private static async Task<string> WriteCodexRolloutAsync(
        string codexHome,
        string fileName,
        string sessionId,
        int hour)
    {
        string content = await File.ReadAllTextAsync(
            Path.Combine(FixtureDirectory, "basic-rollout.jsonl"));
        content = content
            .Replace("thread-1", sessionId, StringComparison.Ordinal)
            .Replace(
                "2026-07-16T01:",
                $"2026-07-16T{hour:00}:",
                StringComparison.Ordinal);
        string path = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Utf8WithoutBom);
        return path;
    }

    private static async Task<
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate>>
        ReadCodexEntityUsageAsync(SqliteConnectionFactory connections)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                source_instance_id,
                source_entity_id,
                COUNT(*),
                COALESCE(SUM(normalized_total_value), 0)
            FROM usage_events
            WHERE agent_id = 'codex'
            GROUP BY source_instance_id, source_entity_id
            ORDER BY source_instance_id, source_entity_id;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        var result =
            new Dictionary<StoredUsageSourceEntity, EntityUsageAggregate>();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            result.Add(
                new StoredUsageSourceEntity(reader.GetString(0), reader.GetString(1)),
                new EntityUsageAggregate(reader.GetInt64(2), reader.GetInt64(3)));
        }

        return result;
    }

    private static async Task<SourceDatabaseStateRow[]>
        ReadCodexSourceDatabaseStateAsync(SqliteConnectionFactory connections)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                instances.source_instance_id,
                cursors.source_entity_id,
                instances.display_name,
                instances.root_path,
                instances.last_checked_unix_ms,
                cursors.source_path,
                cursors.cursor_json,
                cursors.source_fingerprint,
                cursors.parser_version,
                cursors.last_success_unix_ms,
                cursors.last_error,
                cursors.last_error_unix_ms,
                COUNT(events.dedup_key),
                COALESCE(SUM(events.normalized_total_value), 0)
            FROM source_instances AS instances
            INNER JOIN source_cursors AS cursors
              ON cursors.source_instance_id = instances.source_instance_id
            LEFT JOIN usage_events AS events
              ON events.agent_id = instances.agent_id
             AND events.source_instance_id = cursors.source_instance_id
             AND events.source_entity_id = cursors.source_entity_id
            WHERE instances.agent_id = 'codex'
            GROUP BY
                instances.source_instance_id,
                cursors.source_entity_id,
                instances.display_name,
                instances.root_path,
                instances.last_checked_unix_ms,
                cursors.source_path,
                cursors.cursor_json,
                cursors.source_fingerprint,
                cursors.parser_version,
                cursors.last_success_unix_ms,
                cursors.last_error,
                cursors.last_error_unix_ms
            ORDER BY instances.source_instance_id, cursors.source_entity_id;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        var rows = new List<SourceDatabaseStateRow>();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows.Add(new SourceDatabaseStateRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13)));
        }

        return rows.ToArray();
    }

    private static void AssertEntityUsageEqual(
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> expected,
        IReadOnlyDictionary<StoredUsageSourceEntity, EntityUsageAggregate> actual)
    {
        CollectionAssert.AreEquivalent(
            expected.Keys.ToArray(),
            actual.Keys.ToArray());
        foreach ((
                     StoredUsageSourceEntity source,
                     EntityUsageAggregate aggregate) in expected)
        {
            Assert.AreEqual(aggregate, actual[source], source.ToString());
        }
    }

    private sealed record EntityUsageAggregate(long EventCount, long NormalizedTotal);

    private sealed record SourceDatabaseStateRow(
        string SourceInstanceId,
        string SourceEntityId,
        string DisplayName,
        string RootPath,
        long LastCheckedUnixMs,
        string SourcePath,
        string CursorJson,
        string SourceFingerprint,
        string ParserVersion,
        long? LastSuccessUnixMs,
        string? LastError,
        long? LastErrorUnixMs,
        long EventCount,
        long NormalizedTotal);

    private static UsageEvent CreateUnrelatedEvent(DateTimeOffset occurredAtUtc) => new(
        "mock",
        "mock:windows:unrelated",
        "mock:entity:unrelated",
        "mock:event:1",
        "mock:dedup:1",
        SourceKind.Jsonl,
        occurredAtUtc,
        occurredAtUtc,
        new ModelIdentity
        {
            RawModel = "mock-model",
            NormalizedModel = "mock-model",
            ProviderId = "mock-provider",
            ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
        },
        new TokenUsage
        {
            InputReported = TokenMetric.Exact(1),
            UncachedInput = TokenMetric.Exact(1),
            CacheRead = TokenMetric.Exact(0),
            CacheWrite = TokenMetric.Unavailable,
            Output = TokenMetric.Exact(0),
            Reasoning = TokenMetric.Exact(0),
            Tool = TokenMetric.Unavailable,
            ReportedTotal = TokenMetric.Exact(1),
            NormalizedTotal = TokenMetric.Exact(1),
            CacheIncludedInInput = MetricInclusion.Included,
            ReasoningIncludedInOutput = MetricInclusion.Included
        },
        CompletionState.Completed,
        DataQuality.Exact,
        "mock-parser-v1",
        "mock-fingerprint",
        1);

    private static string FixtureDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "Fixtures",
        "Codex"));
}
