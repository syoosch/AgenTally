using System.Text;
using System.Text.Json;
using System.IO;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.WorkBuddy;
using AgenTally.Core.Hosting;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class WorkBuddyCollectorTests
{
    private const long BaseTime = 1_785_729_600_000;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [TestMethod]
    [DataRow("hy3", "hy3", "Hy3", "hy3")]
    [DataRow("glm-5.2", "glm-5.2", "GLM-5.2", "glm-5.2")]
    [DataRow("glm-5.1", "glm-5.1", "GLM-5.1", "glm-5.1")]
    [DataRow("glm-5v-turbo", "glm-5v-turbo", "GLM-5v-Turbo", "glm-5v-turbo")]
    [DataRow("minimax-m3-play", "minimax-m3-play", "MiniMax-M3", "minimax-m3")]
    [DataRow("kimi-k3", "kimi-k3", "Kimi-K3", "kimi-k3")]
    [DataRow("kimi-k2.7-code", "kimi-k2.7-code", "Kimi-K2.7-Code", "kimi-k2.7-code")]
    [DataRow("kimi-k2.6", "kimi-k2.6", "Kimi-K2.6", "kimi-k2.6")]
    [DataRow("deepseek-v4-flash", "deepseek-v4-flash", "Deepseek-V4-Flash", "deepseek-v4-flash")]
    [DataRow("deepseek-v4-pro", "deepseek-v4-pro", "Deepseek-V4-Pro", "deepseek-v4-pro")]
    public void ModelIdentityResolver_MapsConfirmedWorkBuddyCatalogNames(
        string providerModel,
        string routeModelId,
        string displayName,
        string normalizedModel)
    {
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                providerData = new
                {
                    model = providerModel,
                    requestModelId = routeModelId,
                    requestModelName = displayName
                }
            }));

        ModelIdentity identity = WorkBuddyModelIdentityResolver.Resolve(
            document.RootElement);

        Assert.AreEqual(providerModel, identity.RawModel);
        Assert.AreEqual(normalizedModel, identity.NormalizedModel);
        Assert.AreEqual(routeModelId, identity.RouteModelId);
        Assert.AreEqual(displayName, identity.DisplayName);
    }

    [TestMethod]
    public void ModelIdentityResolver_UsesNewCorroboratedCatalogNamesGenerically()
    {
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                providerData = new
                {
                    providerId = "vendor",
                    model = "vendor/future-model-v9",
                    requestModelId = "future-model-v9",
                    requestModelName = "Future Model V9"
                }
            }));

        ModelIdentity identity = WorkBuddyModelIdentityResolver.Resolve(
            document.RootElement);

        Assert.AreEqual("future-model-v9", identity.NormalizedModel);
        Assert.AreEqual("vendor/future-model-v9", identity.RawModel);
        Assert.AreEqual(ModelResolutionOrigin.ExactAlias, identity.ResolutionOrigin);
    }

    [TestMethod]
    public async Task CollectAsync_MapsCodexBaselineMetadataAndInclusiveTokens()
    {
        using var directory = new TestTempDirectory();
        string workBuddyHome = directory.File(".workbuddy");
        string projectPath = directory.File("workbuddy-project");
        string sessionFile = await CreateSessionAsync(
            workBuddyHome,
            projectPath,
            "session-1",
            [
                UserRecord(
                    "session-1",
                    "user-1",
                    BaseTime,
                    projectPath,
                    "<system-reminder data-role=\"user-context\">" +
                    "private injected context" +
                    "</system-reminder>\n  请检查\n当前实现  "),
                new
                {
                    id = "title-1",
                    timestamp = BaseTime + 1,
                    type = "ai-title",
                    sessionId = "session-1",
                    cwd = projectPath,
                    aiTitle = "检查 WorkBuddy 支持"
                },
                new
                {
                    id = "reasoning-1",
                    parentId = "user-1",
                    timestamp = BaseTime + 2,
                    type = "reasoning",
                    sessionId = "session-1",
                    cwd = projectPath,
                    reasoning = "private reasoning body"
                },
                new
                {
                    id = "tool-item-1",
                    parentId = "reasoning-1",
                    timestamp = BaseTime + 3,
                    type = "function_call",
                    sessionId = "session-1",
                    cwd = projectPath,
                    callId = "tool-call-1",
                    name = "read_file",
                    arguments = "private tool arguments",
                    status = "completed"
                },
                AssistantRecord(
                    "session-1",
                    "assistant-1",
                    "tool-item-1",
                    "message-1",
                    BaseTime + 4,
                    projectPath,
                    input: 100,
                    cacheRead: 60,
                    cacheMiss: 40,
                    output: 20,
                    reasoning: 5,
                    total: 120,
                    responseText: "private assistant response")
            ]);

        var collector = new WorkBuddyCollector(workBuddyHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        UsageEvent usage = Assert.ContainsSingle(batch.Events);
        Assert.AreEqual("workbuddy", usage.AgentId);
        Assert.AreEqual("deepseek-v4-pro", usage.Model.NormalizedModel);
        Assert.AreEqual(100L, usage.Tokens.InputReported.Value);
        Assert.AreEqual(40L, usage.Tokens.UncachedInput.Value);
        Assert.AreEqual(60L, usage.Tokens.CacheRead.Value);
        Assert.AreEqual(0L, usage.Tokens.CacheWrite.Value);
        Assert.AreEqual(15L, usage.Tokens.Output.Value);
        Assert.AreEqual(5L, usage.Tokens.Reasoning.Value);
        Assert.AreEqual(120L, usage.Tokens.ReportedTotal.Value);
        Assert.AreEqual(120L, usage.Tokens.NormalizedTotal.Value);
        Assert.AreEqual(
            MetricInclusion.Included,
            usage.Tokens.CacheIncludedInInput);
        Assert.AreEqual(
            MetricInclusion.Included,
            usage.Tokens.ReasoningIncludedInOutput);
        Assert.AreEqual("session-1", usage.SessionId);
        Assert.AreEqual(Path.GetFullPath(projectPath), usage.ProjectPath);
        Assert.IsNotNull(usage.TurnIdHash);

        UsageEventToolMetadata tool = Assert.ContainsSingle(batch.EventTools);
        Assert.AreEqual("read_file", tool.ToolName);
        Assert.AreEqual(usage.DedupKey, tool.EventDedupKey);
        UsageTurnMetadata turn = batch.Turns.Last(value =>
            value.CompletedAtUtc.HasValue);
        Assert.AreEqual("请检查 当前实现", turn.PromptPreview);
        Assert.AreEqual(1, turn.UserMessageCount);
        UsageSessionMetadata session = batch.Sessions.Last();
        Assert.AreEqual("检查 WorkBuddy 支持", session.SessionName);
        Assert.AreEqual(SessionRole.Main, session.SessionRole);
        Assert.AreEqual(
            CompatibilityLevel.PartiallyCompatible,
            session.CompatibilityLevel);
        Assert.IsNull(session.DirectParentSessionId);
        Assert.DoesNotContain("private tool arguments", batch.NextCursorJson);
        Assert.DoesNotContain("private assistant response", batch.NextCursorJson);
        Assert.DoesNotContain("private reasoning body", batch.NextCursorJson);
        Assert.DoesNotContain("private injected context", batch.NextCursorJson);
        Assert.DoesNotContain("user-1", batch.NextCursorJson);
        Assert.AreEqual(sessionFile, entity.SourcePath);
    }

    [TestMethod]
    public async Task CollectAsync_PreservesRoutesAndResolvesOnlyConfirmedModelAliases()
    {
        using var directory = new TestTempDirectory();
        string workBuddyHome = directory.File(".workbuddy");
        string projectPath = directory.File("workbuddy-project");
        await CreateSessionAsync(
            workBuddyHome,
            projectPath,
            "session-models",
            [
                UserRecord(
                    "session-models",
                    "user-minimax",
                    BaseTime,
                    projectPath,
                    "测试 MiniMax"),
                AssistantRecord(
                    "session-models",
                    "assistant-minimax",
                    "user-minimax",
                    "message-minimax",
                    BaseTime + 1,
                    projectPath,
                    input: 10,
                    cacheRead: 0,
                    cacheMiss: 10,
                    output: 2,
                    reasoning: 0,
                    total: 12,
                    responseText: "private response",
                    providerModel: "minimax-m3-play",
                    requestModelId: "minimax-m3-play",
                    requestModelName: "MiniMax-M3"),
                UserRecord(
                    "session-models",
                    "user-custom",
                    BaseTime + 2,
                    projectPath,
                    "测试自定义路由"),
                AssistantRecord(
                    "session-models",
                    "assistant-custom",
                    "user-custom",
                    "message-custom",
                    BaseTime + 3,
                    projectPath,
                    input: 10,
                    cacheRead: 0,
                    cacheMiss: 10,
                    output: 2,
                    reasoning: 0,
                    total: 12,
                    responseText: "private response",
                    providerModel: "deployment-blue",
                    requestModelId: "custom-local:minimax-m3",
                    requestModelName: "MiniMax-M3"),
                UserRecord(
                    "session-models",
                    "user-kimi",
                    BaseTime + 4,
                    projectPath,
                    "测试 Kimi"),
                AssistantRecord(
                    "session-models",
                    "assistant-kimi",
                    "user-kimi",
                    "message-kimi",
                    BaseTime + 5,
                    projectPath,
                    input: 10,
                    cacheRead: 0,
                    cacheMiss: 10,
                    output: 2,
                    reasoning: 0,
                    total: 12,
                    responseText: "private response",
                    providerModel: "kimi-k2.7-code",
                    requestModelId: "kimi-k2.7-code",
                    requestModelName: "Kimi-K2.7-Code")
            ]);

        var collector = new WorkBuddyCollector(workBuddyHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        UsageEvent minimax = batch.Events.Single(value =>
            value.Model.RawModel == "minimax-m3-play");
        Assert.AreEqual("minimax-m3", minimax.Model.NormalizedModel);
        Assert.AreEqual("minimax-m3-play", minimax.Model.RouteModelId);
        Assert.AreEqual("MiniMax-M3", minimax.Model.DisplayName);
        Assert.AreEqual(
            ModelResolutionOrigin.ExactAlias,
            minimax.Model.ResolutionOrigin);

        UsageEvent custom = batch.Events.Single(value =>
            value.Model.RawModel == "deployment-blue");
        Assert.AreEqual("deployment-blue", custom.Model.NormalizedModel);
        Assert.AreEqual("custom-local:minimax-m3", custom.Model.RouteModelId);
        Assert.AreEqual("MiniMax-M3", custom.Model.DisplayName);
        Assert.AreEqual(
            ModelResolutionOrigin.LogConfirmed,
            custom.Model.ResolutionOrigin);

        UsageEvent kimi = batch.Events.Single(value =>
            value.Model.RawModel == "kimi-k2.7-code");
        Assert.AreEqual("kimi-k2.7-code", kimi.Model.NormalizedModel);
        Assert.AreEqual("kimi-k2.7-code", kimi.Model.RouteModelId);
        Assert.AreEqual("Kimi-K2.7-Code", kimi.Model.DisplayName);
        Assert.AreEqual(
            ModelResolutionOrigin.LogConfirmed,
            kimi.Model.ResolutionOrigin);
    }

    [TestMethod]
    public async Task CollectAsync_FoldsInternalErrorRecoveryIntoOriginalPrompt()
    {
        using var directory = new TestTempDirectory();
        string workBuddyHome = directory.File(".workbuddy");
        string projectPath = directory.File("recovery-project");
        await CreateSessionAsync(
            workBuddyHome,
            projectPath,
            "session-recovery",
            [
                UserRecord(
                    "session-recovery",
                    "user-original",
                    BaseTime,
                    projectPath,
                    "<system-reminder data-role=\"user-context\">" +
                    "private user context" +
                    "</system-reminder>\nretry prompt"),
                new
                {
                    id = "assistant-incomplete",
                    parentId = "user-original",
                    timestamp = BaseTime + 1,
                    type = "message",
                    role = "assistant",
                    status = "incomplete",
                    sessionId = "session-recovery",
                    cwd = projectPath,
                    content = new object[]
                    {
                        new { type = "output_text", text = "private partial response" }
                    }
                },
                new
                {
                    id = "internal-recovery",
                    parentId = "assistant-incomplete",
                    timestamp = BaseTime + 2,
                    type = "message",
                    role = "user",
                    sessionId = "session-recovery",
                    cwd = projectPath,
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "<system-reminder data-role=\"error-recovery\">" +
                                "private recovery instruction" +
                                "</system-reminder>"
                        }
                    }
                },
                AssistantRecord(
                    "session-recovery",
                    "assistant-completed",
                    "internal-recovery",
                    "message-recovery",
                    BaseTime + 3,
                    projectPath,
                    10,
                    0,
                    10,
                    2,
                    0,
                    12,
                    "completed response")
            ]);

        var collector = new WorkBuddyCollector(workBuddyHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        UsageEvent usage = Assert.ContainsSingle(batch.Events);
        Assert.IsNotNull(usage.TurnIdHash);
        Assert.AreEqual(
            1,
            batch.Turns.Select(value => value.TurnIdHash).Distinct().Count());
        UsageTurnMetadata completed = batch.Turns.Last(value =>
            value.CompletedAtUtc.HasValue);
        Assert.AreEqual("retry prompt", completed.PromptPreview);
        Assert.AreEqual(usage.TurnIdHash, completed.TurnIdHash);
        Assert.DoesNotContain("private user context", batch.NextCursorJson);
        Assert.DoesNotContain("private recovery instruction", batch.NextCursorJson);
        Assert.DoesNotContain("private partial response", batch.NextCursorJson);
    }

    [TestMethod]
    public async Task CollectAsync_ResumesIncrementallyAndRetainsTurnState()
    {
        using var directory = new TestTempDirectory();
        string workBuddyHome = directory.File(".workbuddy");
        string projectPath = directory.File("incremental-project");
        string sessionFile = await CreateSessionAsync(
            workBuddyHome,
            projectPath,
            "session-incremental",
            [
                UserRecord(
                    "session-incremental",
                    "user-1",
                    BaseTime,
                    projectPath,
                    "first prompt"),
                AssistantRecord(
                    "session-incremental",
                    "assistant-1",
                    "user-1",
                    "message-1",
                    BaseTime + 1,
                    projectPath,
                    10,
                    0,
                    10,
                    2,
                    0,
                    12,
                    "first response")
            ]);
        var collector = new WorkBuddyCollector(workBuddyHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch first = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        await AppendRecordsAsync(
            sessionFile,
            [
                UserRecord(
                    "session-incremental",
                    "user-2",
                    BaseTime + 2,
                    projectPath,
                    "second prompt"),
                new
                {
                    id = "tool-2",
                    parentId = "user-2",
                    timestamp = BaseTime + 3,
                    type = "function_call",
                    sessionId = "session-incremental",
                    cwd = projectPath,
                    callId = "call-2",
                    name = "shell_command",
                    arguments = "must not persist",
                    status = "completed"
                },
                AssistantRecord(
                    "session-incremental",
                    "assistant-2",
                    "tool-2",
                    "message-2",
                    BaseTime + 4,
                    projectPath,
                    20,
                    5,
                    15,
                    4,
                    1,
                    24,
                    "second response")
            ]);
        CollectedBatch second = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                ToStoredCursor(instance, entity, first),
                CollectionReason.FileChanged));
        CollectedBatch third = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                ToStoredCursor(instance, entity, second),
                CollectionReason.PeriodicAudit));

        UsageEvent usage = Assert.ContainsSingle(second.Events);
        Assert.AreEqual(15L, usage.Tokens.UncachedInput.Value);
        Assert.AreEqual(5L, usage.Tokens.CacheRead.Value);
        Assert.AreEqual(3L, usage.Tokens.Output.Value);
        Assert.AreEqual(1L, usage.Tokens.Reasoning.Value);
        Assert.AreEqual(24L, usage.Tokens.NormalizedTotal.Value);
        Assert.AreEqual("second prompt", second.Turns.Last().PromptPreview);
        Assert.AreEqual(
            "shell_command",
            Assert.ContainsSingle(second.EventTools).ToolName);
        Assert.DoesNotContain("must not persist", second.NextCursorJson);
        Assert.IsEmpty(third.Events);
        Assert.IsEmpty(third.EventTools);
    }

    [TestMethod]
    public async Task CollectAsync_FailsClosedForUnprovenTokenOverlap()
    {
        using var directory = new TestTempDirectory();
        string workBuddyHome = directory.File(".workbuddy");
        string projectPath = directory.File("invalid-project");
        await CreateSessionAsync(
            workBuddyHome,
            projectPath,
            "session-invalid",
            [
                UserRecord(
                    "session-invalid",
                    "user-invalid",
                    BaseTime,
                    projectPath,
                    "invalid counters"),
                AssistantRecord(
                    "session-invalid",
                    "assistant-invalid",
                    "user-invalid",
                    "message-invalid",
                    BaseTime + 1,
                    projectPath,
                    100,
                    60,
                    40,
                    20,
                    5,
                    999,
                    "ignored response")
            ]);

        var collector = new WorkBuddyCollector(workBuddyHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        Assert.IsEmpty(batch.Events);
        Assert.IsTrue(batch.Diagnostics.Any(value =>
            value.Code == "workbuddy.invalid_usage_record"));
    }

    [TestMethod]
    public async Task CollectAsync_DoesNotGuessPromptForOrphanedUsageBranch()
    {
        using var directory = new TestTempDirectory();
        string workBuddyHome = directory.File(".workbuddy");
        string projectPath = directory.File("orphan-project");
        await CreateSessionAsync(
            workBuddyHome,
            projectPath,
            "session-orphan",
            [
                UserRecord(
                    "session-orphan",
                    "user-current",
                    BaseTime,
                    projectPath,
                    "current prompt"),
                AssistantRecord(
                    "session-orphan",
                    "assistant-old-branch",
                    "user-from-old-branch",
                    "message-old-branch",
                    BaseTime + 1,
                    projectPath,
                    10,
                    0,
                    10,
                    2,
                    0,
                    12,
                    "orphan response")
            ]);

        var collector = new WorkBuddyCollector(workBuddyHome);
        (SourceInstanceDescriptor instance, SourceEntityDescriptor entity) =
            await ProbeSingleAsync(collector, directory.Path);
        CollectedBatch batch = await CollectSingleAsync(
            collector,
            new CollectionRequest(
                instance,
                entity,
                null,
                CollectionReason.StartupImport));

        UsageEvent usage = Assert.ContainsSingle(batch.Events);
        Assert.IsNull(usage.TurnIdHash);
        Assert.IsFalse(batch.Turns.Any(value => value.CompletedAtUtc.HasValue));
        Assert.IsEmpty(batch.EventTools);
    }

    [TestMethod]
    public async Task CoreHost_OnceRegistersWorkBuddyAndPersistsExactTotals()
    {
        using var directory = new TestTempDirectory();
        string workBuddyHome = directory.File(".workbuddy");
        string projectPath = directory.File("host-project");
        await CreateSessionAsync(
            workBuddyHome,
            projectPath,
            "session-host",
            [
                UserRecord(
                    "session-host",
                    "user-host",
                    BaseTime,
                    projectPath,
                    "host prompt"),
                AssistantRecord(
                    "session-host",
                    "assistant-host",
                    "user-host",
                    "message-host",
                    BaseTime + 1,
                    projectPath,
                    100,
                    60,
                    40,
                    20,
                    5,
                    120,
                    "host response")
            ]);
        string database = directory.File("agentally.db");
        string isolated = directory.File("isolated");

        int exitCode = await new CoreHost(
            new StorageOptions(database)).RunAsync([
                "--once",
                "--codex-home", Path.Combine(isolated, ".codex"),
                "--claude-home", Path.Combine(isolated, ".claude"),
                "--kimi-home", Path.Combine(isolated, ".kimi-code"),
                "--zcode-home", Path.Combine(isolated, ".zcode"),
                "--workbuddy-home", workBuddyHome,
                "--database", database
            ]);

        var queries = new SqliteUsageQueryService(
            new SqliteConnectionFactory(new StorageOptions(database)));
        UsageOverview overview = await queries.GetOverviewAsync(
            new UsageFilter(
                DateTimeOffset.FromUnixTimeMilliseconds(BaseTime - 1),
                DateTimeOffset.FromUnixTimeMilliseconds(BaseTime + 100),
                agentId: "workbuddy"),
            CancellationToken.None);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1L, overview.RequestCount);
        Assert.AreEqual(40L, overview.UncachedInput.Value);
        Assert.AreEqual(60L, overview.CacheRead.Value);
        Assert.AreEqual(15L, overview.Output.Value);
        Assert.AreEqual(120L, overview.NormalizedTotal.Value);
    }

    private static object UserRecord(
        string sessionId,
        string id,
        long timestamp,
        string cwd,
        string prompt) => new
    {
        id,
        timestamp,
        type = "message",
        role = "user",
        sessionId,
        cwd,
        content = new object[]
        {
            new { type = "input_text", text = prompt }
        }
    };

    private static object AssistantRecord(
        string sessionId,
        string id,
        string parentId,
        string messageId,
        long timestamp,
        string cwd,
        long input,
        long cacheRead,
        long cacheMiss,
        long output,
        long reasoning,
        long total,
        string responseText,
        string providerModel = "deepseek-v4-pro",
        string? requestModelId = null,
        string? requestModelName = null) => new
    {
        id,
        parentId,
        timestamp,
        type = "message",
        role = "assistant",
        status = "completed",
        sessionId,
        cwd,
        content = new object[]
        {
            new { type = "output_text", text = responseText }
        },
        message = new
        {
            usage = new
            {
                input_tokens = input,
                cache_read_input_tokens = cacheRead,
                output_tokens = output,
                total_tokens = total
            }
        },
        providerData = new
        {
            model = providerModel,
            requestModelId,
            requestModelName,
            messageId,
            usage = new
            {
                inputTokens = input,
                inputTokensDetails = new[] { new { cached_tokens = cacheRead } },
                outputTokens = output,
                outputTokensDetails = new[] { new { reasoning_tokens = reasoning } },
                totalTokens = total,
                requests = 1
            },
            rawUsage = new
            {
                prompt_tokens = input,
                prompt_cache_hit_tokens = cacheRead,
                prompt_cache_miss_tokens = cacheMiss,
                prompt_cache_write_tokens = 0,
                completion_tokens = output,
                completion_tokens_details = new { reasoning_tokens = reasoning },
                total_tokens = total
            }
        }
    };

    private static async Task<string> CreateSessionAsync(
        string workBuddyHome,
        string projectPath,
        string sessionId,
        IReadOnlyList<object> records)
    {
        string projectKey = Path.GetFileName(projectPath);
        string path = Path.Combine(
            workBuddyHome,
            "projects",
            projectKey,
            $"{sessionId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AppendRecordsAsync(path, records);
        return Path.GetFullPath(path);
    }

    private static Task AppendRecordsAsync(
        string path,
        IReadOnlyList<object> records) => File.AppendAllTextAsync(
        path,
        string.Join(
            Environment.NewLine,
            records.Select(value => JsonSerializer.Serialize(value))) +
        Environment.NewLine,
        Utf8WithoutBom);

    private static async Task<(SourceInstanceDescriptor, SourceEntityDescriptor)>
        ProbeSingleAsync(WorkBuddyCollector collector, string userProfile)
    {
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(userProfile, TimeProvider.System),
            CancellationToken.None);
        Assert.IsEmpty(probe.Diagnostics);
        SourceInstanceDescriptor instance = Assert.ContainsSingle(probe.Instances);
        SourceEntityDescriptor entity = Assert.ContainsSingle(probe.Entities);
        Assert.AreEqual("WorkBuddy (Windows)", instance.DisplayName);
        return (instance, entity);
    }

    private static async Task<CollectedBatch> CollectSingleAsync(
        WorkBuddyCollector collector,
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
