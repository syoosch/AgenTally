using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Qoder;
using AgenTally.Core.Collectors.QwenCode;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class QwenQoderCollectorTests
{
    private const long BaseTime = 1_786_080_000_000L;

    [TestMethod]
    public async Task QwenCode_MapsInclusiveCountersPromptAndToolWithoutBodiesInCursor()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File(".qwen");
        string project = directory.File("project");
        string chats = Path.Combine(home, "projects", "encoded-project", "chats");
        Directory.CreateDirectory(chats);
        string path = Path.Combine(chats, "session-qwen.jsonl");
        object[] records =
        [
            new
            {
                type = "user",
                uuid = "user-1",
                sessionId = "session-qwen",
                timestamp = "2026-08-07T01:00:00.000Z",
                cwd = project,
                message = new { role = "user", parts = new[] { new { text = "  检查\nQwen 接入  " } } }
            },
            new
            {
                type = "assistant",
                uuid = "assistant-1",
                parentUuid = "user-1",
                sessionId = "session-qwen",
                timestamp = "2026-08-07T01:00:01.000Z",
                cwd = project,
                model = "qwen3-coder-plus",
                message = new
                {
                    role = "model",
                    parts = new object[]
                    {
                        new { text = "private response" },
                        new { functionCall = new { name = "read_file", args = "private args" } }
                    }
                },
                usageMetadata = new
                {
                    promptTokenCount = 100,
                    candidatesTokenCount = 20,
                    thoughtsTokenCount = 5,
                    cachedContentTokenCount = 60,
                    totalTokenCount = 120
                }
            }
        ];
        await File.WriteAllLinesAsync(
            path,
            records.Select(static value => JsonSerializer.Serialize(value)),
            new UTF8Encoding(false));

        var collector = new QwenCodeCollector(home);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(collector, instance, entity);

        Assert.AreEqual("qwen-code", instance.AgentId);
        Assert.AreEqual("Qwen Code CLI (Windows)", instance.DisplayName);
        UsageEvent usage = Assert.ContainsSingle(batch.Events);
        Assert.AreEqual(100L, usage.Tokens.InputReported.Value);
        Assert.AreEqual(40L, usage.Tokens.UncachedInput.Value);
        Assert.AreEqual(60L, usage.Tokens.CacheRead.Value);
        Assert.AreEqual(MetricOrigin.Unavailable, usage.Tokens.CacheWrite.Origin);
        Assert.AreEqual(15L, usage.Tokens.Output.Value);
        Assert.AreEqual(5L, usage.Tokens.Reasoning.Value);
        Assert.AreEqual(120L, usage.Tokens.ReportedTotal.Value);
        Assert.AreEqual(120L, usage.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricInclusion.Included, usage.Tokens.CacheIncludedInInput);
        Assert.AreEqual(MetricInclusion.Included, usage.Tokens.ReasoningIncludedInOutput);
        Assert.AreEqual(Path.GetFullPath(project), usage.ProjectPath);
        Assert.AreEqual("检查 Qwen 接入", batch.Turns.Last().PromptPreview);
        Assert.AreEqual("read_file", Assert.ContainsSingle(batch.EventTools).ToolName);
        Assert.DoesNotContain("private response", batch.NextCursorJson);
        Assert.DoesNotContain("private args", batch.NextCursorJson);
    }

    [TestMethod]
    public async Task QoderDesktop_SeparatesEditionsAndReplaysCorrectionsWithHigherRevision()
    {
        using var directory = new TestTempDirectory();
        string project = directory.File("qoder-project");
        string internationalRoot = directory.File("Qoder");
        string chinaRoot = directory.File("QoderCN");
        string internationalDatabase = await CreateQoderDatabaseAsync(
            internationalRoot,
            project,
            "international-session");
        await CreateQoderDatabaseAsync(chinaRoot, project, "china-session");

        var international = new QoderDesktopCollector(
            internationalRoot,
            QoderEdition.International);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(international, directory.Path);
        CollectedBatch first = await CollectSingleAsync(international, instance, entity);
        UsageEvent usage = Assert.ContainsSingle(first.Events);
        Assert.AreEqual("qoder", usage.AgentId);
        Assert.AreEqual(100L, usage.Tokens.InputReported.Value);
        Assert.AreEqual(70L, usage.Tokens.UncachedInput.Value);
        Assert.AreEqual(30L, usage.Tokens.CacheRead.Value);
        Assert.AreEqual(20L, usage.Tokens.Output.Value);
        Assert.AreEqual(120L, usage.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(MetricOrigin.Unavailable, usage.Tokens.ReportedTotal.Origin);
        Assert.AreEqual("qmodel_38max", usage.Model.RawModel);
        Assert.AreEqual("qwen3.8-max", usage.Model.NormalizedModel);
        Assert.AreEqual("qmodel_38max", usage.Model.RouteModelId);
        Assert.AreEqual("Qwen3.8-Max", usage.Model.DisplayName);
        Assert.AreEqual(ModelResolutionOrigin.ExactAlias, usage.Model.ResolutionOrigin);
        Assert.AreEqual("检查 Qoder Desktop", Assert.ContainsSingle(first.Turns).PromptPreview);
        Assert.AreEqual(Path.GetFullPath(project), usage.ProjectPath);
        Assert.AreEqual(CompatibilityLevel.PartiallyCompatible,
            Assert.ContainsSingle(first.Sessions).CompatibilityLevel);

        await UpdateQoderTokensAsync(internationalDatabase, prompt: 110, cached: 40, completion: 25);
        StoredCursor stored = Store(first);
        CollectedBatch corrected = await CollectSingleAsync(
            international,
            instance,
            entity,
            stored);
        UsageEvent correctedUsage = Assert.ContainsSingle(corrected.Events);
        Assert.AreEqual(135L, correctedUsage.Tokens.NormalizedTotal.Value);
        Assert.IsGreaterThan(usage.SourceRevision, correctedUsage.SourceRevision);
        Assert.AreEqual(usage.DedupKey, correctedUsage.DedupKey);

        var china = new QoderDesktopCollector(chinaRoot, QoderEdition.China);
        SourceProbeResult chinaProbe = await china.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        SourceInstanceDescriptor chinaInstance = Assert.ContainsSingle(chinaProbe.Instances);
        Assert.AreEqual("qoder-cn", chinaInstance.AgentId);
        Assert.AreEqual("Qoder CN Desktop (Windows)", chinaInstance.DisplayName);
        Assert.AreNotEqual(instance.SourceInstanceId, chinaInstance.SourceInstanceId);
    }

    [TestMethod]
    public async Task QoderDesktop_OmitsOpaqueEncryptedPromptPayloads()
    {
        using var directory = new TestTempDirectory();
        string root = directory.File("QoderCN");
        string opaquePrompt = Convert.ToBase64String(
            Enumerable.Repeat((byte)0xff, 16).ToArray());
        await CreateQoderDatabaseAsync(
            root,
            directory.File("qoder-project"),
            "encrypted-session",
            opaquePrompt);

        var collector = new QoderDesktopCollector(root, QoderEdition.China);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(collector, instance, entity);

        Assert.IsNull(Assert.ContainsSingle(batch.Turns).PromptPreview);
        Assert.AreEqual(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("abcdefghijklmnop")),
            QoderDesktopCollector.NormalizePromptPreview(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("abcdefghijklmnop"))));
    }

    [TestMethod]
    public async Task QoderCli_IndexesSafeStructureAndFailsClosedForUndocumentedTokens()
    {
        using var directory = new TestTempDirectory();
        string home = directory.File(".qoder");
        string project = directory.File("cli-project");
        string transcript = Path.Combine(home, "projects", "encoded", "transcript");
        Directory.CreateDirectory(transcript);
        string path = Path.Combine(transcript, "session-cli.jsonl");
        object[] records =
        [
            new
            {
                type = "user",
                uuid = "user-cli",
                session_id = "session-cli",
                timestamp = "2026-08-07T02:00:00Z",
                cwd = project,
                message = new { role = "user", content = "  检查\nCLI transcript  " }
            },
            new
            {
                type = "assistant",
                uuid = "assistant-cli",
                session_id = "session-cli",
                timestamp = "2026-08-07T02:00:01Z",
                cwd = project,
                message = new
                {
                    role = "assistant",
                    content = new object[] { new { type = "text", text = "private answer" } }
                }
            }
        ];
        await File.WriteAllLinesAsync(
            path,
            records.Select(static value => JsonSerializer.Serialize(value)),
            new UTF8Encoding(false));

        var collector = new QoderCliCollector(home);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(collector, instance, entity);

        Assert.AreEqual("qoder", instance.AgentId);
        Assert.AreEqual("Qoder CLI (Windows)", instance.DisplayName);
        Assert.AreEqual(
            "qoder_cli_token_usage_unavailable",
            collector.MaintenanceCompatibilityCode);
        Assert.IsEmpty(batch.Events);
        Assert.AreEqual(CompatibilityLevel.MissingCapability,
            batch.Sessions.Last().CompatibilityLevel);
        UsageTurnMetadata turn = batch.Turns.Last();
        Assert.AreEqual("检查 CLI transcript", turn.PromptPreview);
        Assert.IsNotNull(turn.CompletedAtUtc);
        Assert.DoesNotContain("private answer", batch.NextCursorJson);
    }

    private static async Task<string> CreateQoderDatabaseAsync(
        string root,
        string project,
        string sessionId,
        string userContent = "  检查\nQoder Desktop  ")
    {
        string database = QoderSourceIdentity.DatabasePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await using var connection = new SqliteConnection(
            $"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE chat_message (
                id TEXT PRIMARY KEY,
                session_id TEXT,
                request_id TEXT,
                role TEXT,
                content TEXT,
                token_info TEXT,
                model_info TEXT,
                gmt_create INTEGER
            );
            CREATE TABLE chat_record (
                request_id TEXT,
                session_id TEXT,
                extra TEXT
            );
            CREATE TABLE chat_session (
                session_id TEXT PRIMARY KEY,
                session_title TEXT,
                project_uri TEXT,
                gmt_modified INTEGER,
                preferred_model_info TEXT,
                parent_session_id TEXT
            );
            INSERT INTO chat_session VALUES (
                $session, 'Qoder test', $project, $modified,
                '{"model_key":"fallback-model"}', NULL);
            INSERT INTO chat_record VALUES (
                'request-1', $session, '{"modelConfig":{"key":"record-model"}}');
            INSERT INTO chat_message VALUES (
                'user-1', $session, 'request-1', 'user',
                $content, NULL, NULL, $created);
            INSERT INTO chat_message VALUES (
                'assistant-1', $session, 'request-1', 'assistant',
                'private response',
                '{"prompt_tokens":100,"cached_tokens":30,"completion_tokens":20,"max_input_tokens":1000000}',
                '{"model_key":"qmodel_38max"}', $completed);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$project", new Uri(Path.GetFullPath(project)).AbsoluteUri);
        command.Parameters.AddWithValue("$modified", BaseTime + 2);
        command.Parameters.AddWithValue("$created", BaseTime);
        command.Parameters.AddWithValue("$completed", BaseTime + 1);
        command.Parameters.AddWithValue("$content", userContent);
        await command.ExecuteNonQueryAsync();
        return database;
    }

    private static async Task UpdateQoderTokensAsync(
        string database,
        long prompt,
        long cached,
        long completion)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE chat_message
               SET token_info = $tokens
             WHERE id = 'assistant-1';
            """;
        command.Parameters.AddWithValue("$tokens", JsonSerializer.Serialize(new
        {
            prompt_tokens = prompt,
            cached_tokens = cached,
            completion_tokens = completion,
            max_input_tokens = 1_000_000
        }));
        await command.ExecuteNonQueryAsync();
    }

    private static StoredCursor Store(CollectedBatch batch) => new(
        batch.Instance.SourceInstanceId,
        batch.Entity.SourceEntityId,
        batch.Entity.SourcePath,
        batch.NextCursorJson,
        batch.SourceFingerprint,
        batch.ParserVersion,
        DateTimeOffset.UtcNow,
        null,
        null);

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
        SourceEntityDescriptor entity,
        StoredCursor? cursor = null)
    {
        var batches = new List<CollectedBatch>();
        await foreach (CollectedBatch batch in collector.CollectAsync(
            new CollectionRequest(instance, entity, cursor, CollectionReason.StartupImport),
            CancellationToken.None))
        {
            batches.Add(batch);
        }
        return Assert.ContainsSingle(batches);
    }
}
