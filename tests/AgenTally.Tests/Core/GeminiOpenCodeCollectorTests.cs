using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.GeminiCli;
using AgenTally.Core.Collectors.OpenCode;
using AgenTally.Core.Hosting;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class GeminiOpenCodeCollectorTests
{
    [TestMethod]
    public async Task GeminiCli_MapsOfficialCountersAndRejectsUnprovenTotalWithoutBodies()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File(".gemini");
        string chats = Path.Combine(home, "tmp", "project-hash", "chats");
        Directory.CreateDirectory(chats);
        string transcript = Path.Combine(chats, "session.json");
        await File.WriteAllTextAsync(
            transcript,
            JsonSerializer.Serialize(new
            {
                sessionId = "gemini-session",
                projectHash = "opaque-project-hash",
                messages = new object[]
                {
                    new
                    {
                        id = "user-1",
                        type = "user",
                        content = "private-gemini-prompt"
                    },
                    new
                    {
                        id = "assistant-1",
                        type = "gemini",
                        timestamp = "2026-08-10T01:02:03Z",
                        model = "gemini-2.5-pro",
                        content = "private-gemini-response",
                        tokens = new
                        {
                            promptTokenCount = 100,
                            candidatesTokenCount = 20,
                            cachedContentTokenCount = 40,
                            thoughtsTokenCount = 5,
                            toolUsePromptTokenCount = 7,
                            totalTokenCount = 125
                        }
                    },
                    new
                    {
                        id = "assistant-invalid",
                        type = "gemini",
                        timestamp = "2026-08-10T01:02:04Z",
                        model = "gemini-2.5-pro",
                        tokens = new
                        {
                            promptTokenCount = 10,
                            candidatesTokenCount = 2,
                            totalTokenCount = 99
                        }
                    }
                }
            }));

        var collector = new GeminiCliCollector(home);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        Assert.ContainsSingle(probe.Instances);
        Assert.AreEqual(SourceKind.Mixed, probe.Instances[0].SourceKind);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            probe.Instances[0],
            entity);

        UsageEvent usage = Assert.ContainsSingle(batch.Events);
        Assert.AreEqual(100L, usage.Tokens.InputReported.Value);
        Assert.AreEqual(60L, usage.Tokens.UncachedInput.Value);
        Assert.AreEqual(40L, usage.Tokens.CacheRead.Value);
        Assert.AreEqual(20L, usage.Tokens.Output.Value);
        Assert.AreEqual(5L, usage.Tokens.Reasoning.Value);
        Assert.AreEqual(7L, usage.Tokens.Tool.Value);
        Assert.AreEqual(125L, usage.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricInclusion.Included, usage.Tokens.CacheIncludedInInput);
        Assert.AreEqual(MetricInclusion.Separate, usage.Tokens.ReasoningIncludedInOutput);
        Assert.AreEqual(DataQuality.Exact, usage.DataQuality);
        Assert.AreEqual("google", usage.Model.ProviderId);
        Assert.IsNotNull(usage.ProjectId);
        Assert.ContainsSingle(batch.Diagnostics);
        Assert.AreEqual(
            "gemini-cli.unsupported_token_record",
            batch.Diagnostics[0].Code);
        Assert.DoesNotContain("private-gemini-prompt", batch.NextCursorJson);
        Assert.DoesNotContain("private-gemini-response", batch.NextCursorJson);
    }

    [TestMethod]
    public async Task GeminiCli_MapsHeadlessModelStatsWithCacheInclusivePrompt()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File(".gemini");
        string chats = Path.Combine(home, "tmp", "headless", "chats");
        Directory.CreateDirectory(chats);
        string transcript = Path.Combine(chats, "session-headless.jsonl");
        await File.WriteAllLinesAsync(
            transcript,
            [
                JsonSerializer.Serialize(new
                {
                    type = "init",
                    session_id = "headless-session",
                    model = "gemini-2.5-flash"
                }),
                JsonSerializer.Serialize(new
                {
                    type = "result",
                    timestamp = "2026-08-10T02:00:00Z",
                    stats = new
                    {
                        models = new Dictionary<string, object>
                        {
                            ["gemini-2.5-flash"] = new
                            {
                                tokens = new
                                {
                                    prompt = 100,
                                    candidates = 20,
                                    cached = 40,
                                    thoughts = 5
                                }
                            }
                        }
                    }
                })
            ]);

        var collector = new GeminiCliCollector(home);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(collector, instance, entity);
        UsageEvent usage = Assert.ContainsSingle(batch.Events);
        Assert.AreEqual("headless-session", usage.SessionId);
        Assert.AreEqual(60L, usage.Tokens.UncachedInput.Value);
        Assert.AreEqual(40L, usage.Tokens.CacheRead.Value);
        Assert.AreEqual(125L, usage.Tokens.NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task OpenCode_MapsSqliteV1V2AndSharesDedupWithLegacyJson()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File("opencode");
        Directory.CreateDirectory(home);
        string database = Path.Combine(home, "opencode.db");
        string project = directory.File("project");
        Directory.CreateDirectory(project);
        await CreateOpenCodeDatabaseAsync(database, project);

        string legacyDirectory = Path.Combine(home, "storage", "message", "session-v1");
        Directory.CreateDirectory(legacyDirectory);
        string duplicateJson = Path.Combine(legacyDirectory, "message-v1.json");
        await File.WriteAllTextAsync(duplicateJson, V1MessageJson(project));

        var collector = new OpenCodeCollector(home);
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        Assert.ContainsSingle(probe.Instances);
        Assert.HasCount(2, probe.Entities);
        SourceEntityDescriptor databaseEntity = probe.Entities.Single(value =>
            value.SourcePath.EndsWith("opencode.db", StringComparison.OrdinalIgnoreCase));
        SourceEntityDescriptor jsonEntity = probe.Entities.Single(value =>
            value.SourcePath.EndsWith("message-v1.json", StringComparison.OrdinalIgnoreCase));

        CollectedBatch databaseBatch = await CollectSingleAsync(
            collector,
            probe.Instances[0],
            databaseEntity);
        Assert.HasCount(2, databaseBatch.Events);
        UsageEvent v1 = databaseBatch.Events.Single(value => value.SessionId == "session-v1");
        Assert.AreEqual(100L, v1.Tokens.UncachedInput.Value);
        Assert.AreEqual(20L, v1.Tokens.CacheRead.Value);
        Assert.AreEqual(5L, v1.Tokens.CacheWrite.Value);
        Assert.AreEqual(10L, v1.Tokens.Output.Value);
        Assert.AreEqual(5L, v1.Tokens.Reasoning.Value);
        Assert.AreEqual(140L, v1.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(DataQuality.Exact, v1.DataQuality);
        Assert.AreEqual("anthropic", v1.Model.ProviderId);
        Assert.AreEqual(Path.GetFullPath(project), v1.ProjectPath);
        Assert.IsNull(v1.ReportedCost);

        UsageEvent v2 = databaseBatch.Events.Single(value => value.SessionId == "session-v2");
        Assert.AreEqual("gpt-5", v2.Model.RawModel);
        Assert.AreEqual("openai", v2.Model.ProviderId);
        Assert.AreEqual(70L, v2.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricOrigin.Derived, v2.Tokens.Reasoning.Origin);
        Assert.AreEqual(DataQuality.Derived, v2.DataQuality);

        CollectedBatch legacyBatch = await CollectSingleAsync(
            collector,
            probe.Instances[0],
            jsonEntity);
        UsageEvent duplicate = Assert.ContainsSingle(legacyBatch.Events);
        Assert.AreEqual(v1.DedupKey, duplicate.DedupKey);
        Assert.DoesNotContain("private-opencode-response", databaseBatch.NextCursorJson);
        Assert.DoesNotContain("private-opencode-response", legacyBatch.NextCursorJson);
    }

    [TestMethod]
    public async Task OpenCode_DoesNotMergeSameMessageIdAcrossDifferentSessions()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File("opencode");
        Directory.CreateDirectory(home);
        string database = Path.Combine(home, "opencode.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false
        };
        await using (var connection = new SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE message (id TEXT, session_id TEXT, data TEXT);
                INSERT INTO message (id, session_id, data) VALUES
                    ('row-a', 'session-a', $first),
                    ('row-b', 'session-b', $second);
                """;
            command.Parameters.AddWithValue("$first", OpenCodeMessageJson(
                "shared-message", "session-a", 1_786_331_000_000L, 10));
            command.Parameters.AddWithValue("$second", OpenCodeMessageJson(
                "shared-message", "session-b", 1_786_332_000_000L, 20));
            await command.ExecuteNonQueryAsync();
        }

        var collector = new OpenCodeCollector(home);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(collector, instance, entity);

        Assert.HasCount(2, batch.Events);
        Assert.HasCount(2, batch.Events.Select(static value => value.DedupKey).Distinct());
        Assert.AreEqual(
            10L,
            batch.Events.Single(static value => value.SessionId == "session-a")
                .Tokens.UncachedInput.Value);
        Assert.AreEqual(
            20L,
            batch.Events.Single(static value => value.SessionId == "session-b")
                .Tokens.UncachedInput.Value);
    }

    [TestMethod]
    public async Task CoreHost_OnceRegistersGeminiAndOpenCodeCollectors()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string geminiHome = directory.File(".gemini");
        string geminiChats = Path.Combine(geminiHome, "tmp", "project", "chats");
        Directory.CreateDirectory(geminiChats);
        await File.WriteAllTextAsync(
            Path.Combine(geminiChats, "session.json"),
            JsonSerializer.Serialize(new
            {
                sessionId = "host-gemini",
                messages = new[]
                {
                    new
                    {
                        id = "host-message",
                        type = "gemini",
                        timestamp = "2026-08-10T03:00:00Z",
                        model = "gemini-2.5-pro",
                        tokens = new
                        {
                            promptTokenCount = 10,
                            candidatesTokenCount = 2,
                            totalTokenCount = 12
                        }
                    }
                }
            }));

        string openCodeHome = directory.File("opencode");
        string openCodeMessages = Path.Combine(
            openCodeHome,
            "storage",
            "message",
            "session-v1");
        Directory.CreateDirectory(openCodeMessages);
        string project = directory.File("project");
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(openCodeMessages, "message-v1.json"),
            V1MessageJson(project));
        string database = directory.File("agentally.db");

        int exitCode = await new CoreHost(new StorageOptions(database)).RunAsync([
            "--once",
            "--codex-home", codexHome,
            "--gemini-home", geminiHome,
            "--opencode-home", openCodeHome,
            "--database", database
        ]);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id, COUNT(*)
            FROM usage_events
            WHERE agent_id IN ('gemini-cli', 'opencode')
            GROUP BY agent_id
            ORDER BY agent_id;
            """;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts.Add(reader.GetString(0), reader.GetInt64(1));
        }

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1L, counts["gemini-cli"]);
        Assert.AreEqual(1L, counts["opencode"]);
    }

    private static async Task CreateOpenCodeDatabaseAsync(string path, string project)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE message (id TEXT, session_id TEXT, data TEXT);
            CREATE TABLE session_message (id TEXT, session_id TEXT, type TEXT, data TEXT);
            CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT, title TEXT);
            INSERT INTO session (id, directory, title) VALUES
                ('session-v1', $project, 'Private title'),
                ('session-v2', $project, 'Private title');
            INSERT INTO message (id, session_id, data)
                VALUES ('row-v1', 'session-v1', $v1);
            INSERT INTO session_message (id, session_id, type, data)
                VALUES ('row-v2', 'session-v2', 'assistant', $v2);
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$v1", V1MessageJson(project));
        command.Parameters.AddWithValue(
            "$v2",
            JsonSerializer.Serialize(new
            {
                id = "message-v2",
                sessionID = "session-v2",
                model = new { id = "gpt-5", providerID = "openai" },
                time = new { created = 1_786_332_000_000L },
                content = "private-opencode-response",
                tokens = new
                {
                    input = 50,
                    output = 10,
                    cache = new { read = 10, write = 0 },
                    total = 70
                },
                cost = 99.0m
            }));
        await command.ExecuteNonQueryAsync();
    }

    private static string V1MessageJson(string project) => JsonSerializer.Serialize(new
    {
        id = "message-v1",
        sessionID = "session-v1",
        role = "assistant",
        modelID = "claude-sonnet-4-5",
        providerID = "anthropic",
        time = new { created = 1_786_331_000_000L },
        path = new { root = project },
        content = "private-opencode-response",
        tokens = new
        {
            input = 100,
            output = 10,
            reasoning = 5,
            cache = new { read = 20, write = 5 },
            total = 140
        },
        cost = 42.0m
    });

    private static string OpenCodeMessageJson(
        string messageId,
        string sessionId,
        long createdAt,
        long input) => JsonSerializer.Serialize(new
    {
        id = messageId,
        sessionID = sessionId,
        role = "assistant",
        modelID = "test-model",
        providerID = "test-provider",
        time = new { created = createdAt },
        tokens = new
        {
            input,
            output = 1,
            cache = new { read = 0, write = 0 },
            total = input + 1
        }
    });

    private static async Task<(SourceInstanceDescriptor, SourceEntityDescriptor)> ProbeSingleAsync(
        IAgentCollector collector,
        string userProfile)
    {
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(userProfile, TimeProvider.System),
            CancellationToken.None);
        Assert.IsEmpty(probe.Diagnostics);
        return (Assert.ContainsSingle(probe.Instances), Assert.ContainsSingle(probe.Entities));
    }

    private static async Task<CollectedBatch> CollectSingleAsync(
        IAgentCollector collector,
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity)
    {
        var batches = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(
                           new CollectionRequest(
                               instance,
                               entity,
                               null,
                               CollectionReason.StartupImport),
                           CancellationToken.None))
        {
            batches.Add(batch);
        }
        return Assert.ContainsSingle(batches);
    }
}
