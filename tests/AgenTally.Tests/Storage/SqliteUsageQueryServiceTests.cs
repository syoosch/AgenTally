using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Storage;

[TestClass]
public sealed class SqliteUsageQueryServiceTests
{
    [TestMethod]
    public async Task Queries_ReturnExpectedAggregatesAndRows()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        await SeedAsync(writer);

        UsageOverview overview =
            await queries.GetOverviewAsync(AllDay(), CancellationToken.None);
        IReadOnlyList<UsageTrendPoint> trend =
            await queries.GetTrendAsync(AllDay(), CancellationToken.None);
        IReadOnlyList<UsageRecordRow> records =
            await queries.GetRecentRecordsAsync(AllDay(), CancellationToken.None);
        IReadOnlyList<ModelUsageRow> models =
            await queries.GetModelsAsync(AllDay(), CancellationToken.None);
        IReadOnlyList<AgentUsageRow> agents =
            await queries.GetAgentsAsync(AllDay(), CancellationToken.None);
        IReadOnlyList<AgentModelUsageRow> agentModels =
            await queries.GetAgentModelsAsync(AllDay(), CancellationToken.None);
        UsageFilterValues filterValues =
            await queries.GetFilterValuesAsync(AllDay(), CancellationToken.None);
        IReadOnlyList<SourceStatusRow> sources =
            await queries.GetSourcesAsync(CancellationToken.None);

        Assert.AreEqual(2L, overview.RequestCount);
        Assert.AreEqual(200L, overview.NormalizedTotal.Value);
        Assert.AreEqual(2, overview.NormalizedTotal.AvailableRecords);
        Assert.AreEqual(60L, overview.UncachedInput.Value);
        Assert.AreEqual(1, overview.UncachedInput.AvailableRecords);
        Assert.AreEqual(1, overview.UncachedInput.UnavailableRecords);
        Assert.AreEqual(0L, overview.CacheWrite.Value);
        Assert.AreEqual(2, overview.CacheWrite.AvailableRecords);
        Assert.AreEqual(Event1Time, overview.FirstOccurredAtUtc);
        Assert.AreEqual(Event2Time, overview.LastOccurredAtUtc);
        Assert.HasCount(2, trend);
        Assert.AreEqual(Event1Time, trend[0].BucketStartUtc);
        Assert.IsNull(trend[1].UncachedInput.Value);
        Assert.HasCount(2, records);
        Assert.AreEqual("event-2", records[0].EventId);
        Assert.AreEqual(CompletionState.Finalized, records[0].CompletionState);
        Assert.AreEqual(DataQuality.Exact, records[0].DataQuality);
        Assert.HasCount(2, models);
        Assert.HasCount(2, agents);
        Assert.AreEqual("codex", agents[0].AgentId);
        Assert.AreEqual(100L, agents[0].NormalizedTotal.Value);
        Assert.AreEqual(Event1Time, agents[0].StartedAtUtc);
        Assert.AreEqual(Event1Time, agents[0].LastActivityUtc);
        Assert.AreEqual(60L, agents[0].UncachedInput.Value);
        Assert.AreEqual(30L, agents[0].Output.Value);
        Assert.IsNull(agents[0].CacheRead.Value);
        Assert.HasCount(2, agentModels);
        Assert.AreEqual("codex", agentModels[0].AgentId);
        Assert.AreEqual("gpt-test", agentModels[0].Model);
        Assert.AreEqual(100L, agentModels[0].NormalizedTotal.Value);
        Assert.AreEqual(Event1Time, agentModels[0].StartedAtUtc);
        Assert.AreEqual(Event1Time, agentModels[0].LastActivityUtc);
        CollectionAssert.AreEqual(
            new[] { "codex", "other-agent" },
            filterValues.AgentIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "claude-test", "gpt-test" },
            filterValues.Models.ToArray());
        Assert.HasCount(2, sources);
        SourceStatusRow source = sources.Single(
            row => row.SourceInstanceId == "codex:windows:test");
        Assert.AreEqual("codex:windows:test", source.SourceInstanceId);
        Assert.AreEqual("rollout:test", source.SourceEntityId);
        Assert.AreEqual("codex-v1", source.ParserVersion);
    }

    [TestMethod]
    public async Task Queries_ApplyAgentAndNormalizedModelFiltersConsistently()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        await SeedAsync(writer);
        var filter = new UsageFilter(
            DayStart(),
            DayEnd(),
            agentId: "codex",
            normalizedModel: "gpt-test");

        UsageOverview overview =
            await queries.GetOverviewAsync(filter, CancellationToken.None);
        IReadOnlyList<UsageTrendPoint> trend =
            await queries.GetTrendAsync(filter, CancellationToken.None);
        IReadOnlyList<UsageRecordRow> records =
            await queries.GetRecentRecordsAsync(filter, CancellationToken.None);
        IReadOnlyList<ModelUsageRow> models =
            await queries.GetModelsAsync(filter, CancellationToken.None);
        IReadOnlyList<AgentUsageRow> agents =
            await queries.GetAgentsAsync(filter, CancellationToken.None);
        IReadOnlyList<AgentModelUsageRow> agentModels =
            await queries.GetAgentModelsAsync(filter, CancellationToken.None);

        Assert.AreEqual(1L, overview.RequestCount);
        Assert.AreEqual(100L, overview.NormalizedTotal.Value);
        Assert.HasCount(1, trend);
        Assert.IsTrue(records.All(row => row.Model == "gpt-test"));
        Assert.HasCount(1, models);
        Assert.AreEqual("gpt-test", models[0].Model);
        Assert.HasCount(1, agents);
        Assert.AreEqual("codex", agents[0].AgentId);
        Assert.HasCount(1, agentModels);
        Assert.AreEqual("codex", agentModels[0].AgentId);
        Assert.AreEqual("gpt-test", agentModels[0].Model);
    }

    [TestMethod]
    public async Task EmptyRange_PreservesUnavailableAggregates()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        await SeedAsync(writer);
        var filter = new UsageFilter(
            new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));

        UsageOverview overview =
            await queries.GetOverviewAsync(filter, CancellationToken.None);

        Assert.AreEqual(0L, overview.RequestCount);
        Assert.IsNull(overview.NormalizedTotal.Value);
        Assert.AreEqual(0, overview.NormalizedTotal.AvailableRecords);
        Assert.AreEqual(0, overview.NormalizedTotal.UnavailableRecords);
        Assert.IsNull(overview.FirstOccurredAtUtc);
        Assert.IsNull(overview.LastOccurredAtUtc);
        Assert.IsEmpty(await queries.GetTrendAsync(filter, CancellationToken.None));
    }

    [TestMethod]
    public async Task FilterValues_ApplyTimeAndOtherFacetsWhileKeepingEachFacetReplaceable()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        const string projectA = "111111111111111111111111";
        const string projectB = "222222222222222222222222";
        const string projectOutsideRange = "333333333333333333333333";
        UsageEvent[] codexEvents =
        [
            CreateEvent(
                "facet-codex-gpt-a",
                "facet-codex-gpt-a",
                Event1Time,
                "codex",
                "gpt-test",
                10,
                5,
                projectId: projectA,
                projectPath: @"D:\Projects\A"),
            CreateEvent(
                "facet-codex-claude-a",
                "facet-codex-claude-a",
                Event1Time,
                "codex",
                "claude-test",
                10,
                5,
                projectId: projectA,
                projectPath: @"D:\Projects\A"),
            CreateEvent(
                "facet-codex-gpt-b",
                "facet-codex-gpt-b",
                Event2Time,
                "codex",
                "gpt-test",
                10,
                5,
                projectId: projectB,
                projectPath: null),
            CreateEvent(
                "facet-codex-gpt-b-known-path",
                "facet-codex-gpt-b-known-path",
                Event3Time,
                "codex",
                "gpt-test",
                10,
                5,
                projectId: projectB,
                projectPath: @"D:\Projects\B")
        ];
        UsageEvent otherAgent = CreateEvent(
            "facet-other-gpt-a",
            "facet-other-gpt-a",
            Event2Time,
            "other-agent",
            "gpt-test",
            10,
            5,
            sourceInstanceId: "other:windows:facet",
            sourceEntityId: "other:facet",
            projectId: projectA,
            projectPath: @"D:\Projects\A");
        UsageEvent outsideRange = CreateEvent(
            "facet-late",
            "facet-late",
            Event3Time,
            "late-agent",
            "late-model",
            10,
            5,
            sourceInstanceId: "late:windows:facet",
            sourceEntityId: "late:facet",
            projectId: projectOutsideRange,
            projectPath: @"D:\Projects\Late");
        await writer.CommitAsync(Batch(codexEvents), CancellationToken.None);
        await writer.CommitAsync(Batch([otherAgent]), CancellationToken.None);
        await writer.CommitAsync(Batch([outsideRange]), CancellationToken.None);
        var filter = new UsageFilter(
            DayStart(),
            Event3Time,
            agentId: "codex",
            normalizedModel: "gpt-test",
            projectId: projectA);

        UsageFilterValues values =
            await queries.GetFilterValuesAsync(filter, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "codex", "other-agent" },
            values.AgentIds.ToArray(),
            "Agent candidates must ignore only the current Agent selection.");
        CollectionAssert.AreEqual(
            new[] { "claude-test", "gpt-test" },
            values.Models.ToArray(),
            "Model candidates must ignore only the current model selection.");
        CollectionAssert.AreEqual(
            new[] { projectA, projectB },
            values.Projects.Select(static project => project.ProjectId).ToArray(),
            "Project candidates must ignore only the current project selection.");
        Assert.AreEqual(
            @"D:\Projects\B",
            values.Projects.Single(project => project.ProjectId == projectB)
                .ProjectPath,
            "The time range constrains project membership, not known display metadata.");
        Assert.IsFalse(values.AgentIds.Contains("late-agent"));
        Assert.IsFalse(values.Models.Contains("late-model"));
        Assert.IsFalse(values.Projects.Any(
            project => project.ProjectId == projectOutsideRange));
    }

    [TestMethod]
    public async Task RecentRecords_RespectsLimitOffsetAndDescendingOrder()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        await SeedAsync(writer);
        var firstPage = new UsageFilter(DayStart(), DayEnd(), limit: 1);
        var secondPage = new UsageFilter(DayStart(), DayEnd(), limit: 1, offset: 1);

        UsageRecordRow newest = Assert.ContainsSingle(
            await queries.GetRecentRecordsAsync(firstPage, CancellationToken.None));
        UsageRecordRow older = Assert.ContainsSingle(
            await queries.GetRecentRecordsAsync(secondPage, CancellationToken.None));

        Assert.AreEqual("event-2", newest.EventId);
        Assert.AreEqual("event-1", older.EventId);
    }

    [TestMethod]
    public async Task RecentRecords_PagingUsesUniqueConflictKeyAsFinalSortOrder()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        UsageEvent first = CreateEvent(
            "same-event-id",
            "codex:same:1",
            Event1Time,
            "codex",
            "gpt-test",
            normalizedTotal: 10,
            uncachedInput: 5);
        UsageEvent second = CreateEvent(
            "same-event-id",
            "codex:same:2",
            Event1Time,
            "codex",
            "gpt-test",
            normalizedTotal: 20,
            uncachedInput: 7);
        await writer.CommitAsync(Batch([second, first]), CancellationToken.None);

        UsageRecordRow page1 = Assert.ContainsSingle(
            await queries.GetRecentRecordsAsync(
                new UsageFilter(DayStart(), DayEnd(), limit: 1),
                CancellationToken.None));
        UsageRecordRow page2 = Assert.ContainsSingle(
            await queries.GetRecentRecordsAsync(
                new UsageFilter(DayStart(), DayEnd(), limit: 1, offset: 1),
                CancellationToken.None));

        Assert.AreEqual(10L, page1.NormalizedTotal);
        Assert.AreEqual(20L, page2.NormalizedTotal);
        Assert.AreNotEqual(page1.NormalizedTotal, page2.NormalizedTotal);
    }

    [TestMethod]
    public async Task Sources_ExposeLastSuccessAndEntityError()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        await SeedAsync(writer);
        DateTimeOffset failedAtUtc = new(2026, 7, 16, 2, 5, 0, TimeSpan.Zero);
        await writer.RecordFailureAsync(
            Instance(),
            Entity(),
            "simulated failure",
            failedAtUtc,
            CancellationToken.None);

        SourceStatusRow source =
            (await queries.GetSourcesAsync(CancellationToken.None)).Single(
                row => row.SourceInstanceId == "codex:windows:test");

        Assert.AreEqual(BatchCheckedAtUtc, source.LastSuccessAtUtc);
        Assert.AreEqual("simulated failure", source.LastError);
        Assert.AreEqual(failedAtUtc, source.LastErrorAtUtc);
    }

    [TestMethod]
    public async Task Queries_UseUnknownModelFallback()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        UsageEvent unknown = CreateEvent(
            "unknown-model",
            "codex:thread-unknown:1",
            Event1Time,
            "codex",
            normalizedModel: null,
            normalizedTotal: 10,
            uncachedInput: 5);
        await writer.CommitAsync(Batch([unknown]), CancellationToken.None);
        var filter = new UsageFilter(
            DayStart(),
            DayEnd(),
            normalizedModel: "未知模型");

        UsageRecordRow row = Assert.ContainsSingle(
            await queries.GetRecentRecordsAsync(filter, CancellationToken.None));

        Assert.AreEqual("未知模型", row.Model);
        Assert.AreEqual(
            1L,
            (await queries.GetOverviewAsync(filter, CancellationToken.None)).RequestCount);
    }

    [TestMethod]
    public async Task RootSessionQueries_IncludeConfirmedDescendantsOnceAndKeepUncertainIndependent()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        const string projectId = "project-full-path";
        const string projectPath = @"C:\fixture\full-project";
        UsageEvent root = CreateEvent(
            "root-event",
            "dedup-root",
            Event1Time,
            "codex",
            "gpt-a",
            100,
            60,
            sessionId: "root-session",
            projectId: projectId,
            projectPath: projectPath,
            turnIdHash: new string('a', 64));
        UsageEvent side = CreateEvent(
            "side-event",
            "dedup-side",
            Event2Time,
            "codex",
            "gpt-b",
            50,
            30,
            sessionId: "side-session",
            projectId: projectId,
            projectPath: projectPath,
            turnIdHash: new string('b', 64));
        UsageEvent nested = CreateEvent(
            "nested-event",
            "dedup-nested",
            Event3Time,
            "codex",
            "gpt-a",
            25,
            10,
            sessionId: "nested-side",
            projectId: projectId,
            projectPath: projectPath);
        UsageEvent uncertain = CreateEvent(
            "uncertain-event",
            "dedup-uncertain",
            Event3Time,
            "codex",
            "gpt-a",
            40,
            20,
            sessionId: "uncertain-session",
            projectId: projectId,
            projectPath: projectPath);

        await writer.CommitAsync(
            Batch([root]) with
            {
                Sessions =
                [
                    Session(
                        "root-session",
                        SessionKind.Primary,
                        projectId: projectId,
                        projectPath: projectPath) with
                    {
                        SessionName = "实现会话名称展示",
                        SessionNameUpdatedAtUtc = BatchCheckedAtUtc
                    }
                ]
            },
            CancellationToken.None);
        await writer.CommitAsync(
            Batch([side]) with
            {
                Sessions =
                [
                    Session(
                        "side-session",
                        SessionKind.Side,
                        "root-session")
                ]
            },
            CancellationToken.None);
        await writer.CommitAsync(
            Batch([nested]) with
            {
                Sessions =
                [
                    Session(
                        "nested-side",
                        SessionKind.Side,
                        "side-session")
                ]
            },
            CancellationToken.None);
        await writer.CommitAsync(
            Batch([uncertain]) with
            {
                Sessions =
                [
                    Session(
                        "uncertain-session",
                        SessionKind.Side,
                        relationState: SessionRelationState.Uncertain)
                ]
            },
            CancellationToken.None);

        RootSessionPage firstPage = await queries.GetRootSessionsAsync(
            new RootSessionPageRequest(AllDay(), pageSize: 1),
            CancellationToken.None);
        RootSessionSummaryRow rootSummary = Assert.ContainsSingle(firstPage.Items);
        Assert.AreEqual("root-session", rootSummary.RootSessionId);
        Assert.AreEqual("实现会话名称展示", rootSummary.SessionName);
        Assert.AreEqual(3L, rootSummary.RequestCount);
        Assert.AreEqual(2, rootSummary.SideSessionCount);
        Assert.AreEqual(175L, rootSummary.Metrics.NormalizedTotal.Value);
        Assert.AreEqual(projectPath, rootSummary.ProjectPath);
        Assert.IsNotNull(firstPage.NextCursor);

        RootSessionPage secondPage = await queries.GetRootSessionsAsync(
            new RootSessionPageRequest(
                AllDay(),
                pageSize: 1,
                firstPage.NextCursor),
            CancellationToken.None);
        Assert.AreEqual(
            "uncertain-session",
            Assert.ContainsSingle(secondPage.Items).RootSessionId);
        Assert.IsNull(secondPage.NextCursor);

        RootSessionDetail detail =
            (await queries.GetRootSessionDetailAsync(
                AllDay(),
                rootSummary.Identity,
                CancellationToken.None))!;
        Assert.HasCount(3, detail.Contributions);
        Assert.AreEqual(
            50L,
            detail.Contributions.Single(
                static row => row.SessionId == "side-session")
                .Metrics.NormalizedTotal.Value);
        Assert.HasCount(
            2,
            detail.Contributions
                .SelectMany(static row => row.Models)
                .Select(static row => row.Model)
                .Distinct());

        UsageOverview rootOverview = await queries.GetOverviewAsync(
            new UsageFilter(
                DayStart(),
                DayEnd(),
                rootSessionId: "root-session"),
            CancellationToken.None);
        Assert.AreEqual(3L, rootOverview.RequestCount);
        Assert.AreEqual(175L, rootOverview.NormalizedTotal.Value);
        UsageOverview projectOverview = await queries.GetOverviewAsync(
            new UsageFilter(
                DayStart(),
                DayEnd(),
                projectId: projectId),
            CancellationToken.None);
        Assert.AreEqual(4L, projectOverview.RequestCount);
        Assert.AreEqual(215L, projectOverview.NormalizedTotal.Value);

        ProjectUsageRow project = Assert.ContainsSingle(
            await queries.GetProjectsAsync(AllDay(), CancellationToken.None));
        Assert.AreEqual(4L, project.RequestCount);
        Assert.AreEqual(2, project.RootSessionCount);
        Assert.AreEqual(215L, project.Metrics.NormalizedTotal.Value);
        Assert.AreEqual(PathAvailability.Available, project.PathAvailability);

        TurnUsagePage turns = await queries.GetTurnsAsync(
            AllDay(),
            rootSummary.Identity,
            CancellationToken.None);
        Assert.AreEqual(TurnCoverageStatus.Partial, turns.Coverage);
        Assert.HasCount(1, turns.Turns);
        Assert.AreEqual(1L, turns.PromptTurnCount);
        Assert.AreEqual(1L, turns.Turns.Sum(static row => row.CallCount));
        Assert.AreEqual(2L, turns.Unattributed.CallCount);
        Assert.AreEqual(75L, turns.Unattributed.Metrics.NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task RootSessionQueries_UseRootIdentityWhenOnlyChildHasUsage()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        const string rootSessionId = "child-only-root";
        const string childSessionId = "child-only-guardian";
        const string projectId = "child-only-project";
        const string projectPath = @"D:\Projects\codex\faker";
        UsageEvent child = CreateEvent(
            "child-only-event",
            "child-only-dedup",
            Event1Time,
            "codex",
            "gpt-test",
            82_382,
            60_000,
            sessionId: childSessionId,
            projectId: projectId,
            projectPath: projectPath);

        await writer.CommitAsync(
            Batch([child]) with
            {
                Sessions =
                [
                    Session(
                        rootSessionId,
                        SessionKind.Primary,
                        projectId: projectId,
                        projectPath: projectPath) with
                    {
                        SessionName = "实现后端协议分支任务",
                        SessionNameUpdatedAtUtc = BatchCheckedAtUtc
                    },
                    Session(
                        childSessionId,
                        SessionKind.Side,
                        rootSessionId)
                ]
            },
            CancellationToken.None);

        RootSessionSummaryRow summary = Assert.ContainsSingle(
            (await queries.GetRootSessionsAsync(
                new RootSessionPageRequest(AllDay()),
                CancellationToken.None)).Items);

        Assert.AreEqual(rootSessionId, summary.RootSessionId);
        Assert.AreEqual("实现后端协议分支任务", summary.SessionName);
        Assert.AreEqual(projectId, summary.ProjectId);
        Assert.AreEqual(projectPath, summary.ProjectPath);
        Assert.AreEqual(
            PathAvailability.Available,
            summary.ProjectPathAvailability);
        Assert.AreEqual(1L, summary.RequestCount);
        Assert.AreEqual(82_382L, summary.Metrics.NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task DuplicateRootSessionIds_RemainDistinctAcrossSourceInstances()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        const string rootSessionId = "shared-claude-root";
        string turnIdHash = new('d', 64);
        var cliIdentity = new RootSessionIdentity(
            "claude-code",
            "claude-code:cli:windows:test",
            rootSessionId);
        var desktopIdentity = new RootSessionIdentity(
            "claude-code",
            "claude-code:desktop-local-agent:windows:test",
            rootSessionId);
        UsageEvent cli = CreateEvent(
            "claude-cli-event",
            "claude-cli-dedup",
            Event1Time,
            "claude-code",
            "claude-cli-model",
            100,
            60,
            cliIdentity.SourceInstanceId,
            "claude-cli-entity",
            rootSessionId,
            turnIdHash: turnIdHash);
        UsageEvent desktop = CreateEvent(
            "claude-desktop-event",
            "claude-desktop-dedup",
            Event1Time,
            "claude-code",
            "claude-desktop-model",
            200,
            120,
            desktopIdentity.SourceInstanceId,
            "claude-desktop-entity",
            rootSessionId,
            turnIdHash: turnIdHash);

        await writer.CommitAsync(
            Batch([cli]) with
            {
                Sessions =
                [
                    Session(
                        rootSessionId,
                        SessionKind.Primary,
                        agentId: cliIdentity.AgentId,
                        sourceInstanceId: cliIdentity.SourceInstanceId,
                        sourceEntityId: cli.SourceEntityId)
                ],
                Turns =
                [
                    Turn(
                        rootSessionId,
                        turnIdHash,
                        Event1Time,
                        Event1Time,
                        agentId: cliIdentity.AgentId,
                        sourceInstanceId: cliIdentity.SourceInstanceId,
                        sourceEntityId: cli.SourceEntityId)
                ]
            },
            CancellationToken.None);
        await writer.CommitAsync(
            Batch([desktop]) with
            {
                Sessions =
                [
                    Session(
                        rootSessionId,
                        SessionKind.Primary,
                        agentId: desktopIdentity.AgentId,
                        sourceInstanceId: desktopIdentity.SourceInstanceId,
                        sourceEntityId: desktop.SourceEntityId)
                ],
                Turns =
                [
                    Turn(
                        rootSessionId,
                        turnIdHash,
                        Event1Time,
                        Event1Time,
                        agentId: desktopIdentity.AgentId,
                        sourceInstanceId: desktopIdentity.SourceInstanceId,
                        sourceEntityId: desktop.SourceEntityId)
                ]
            },
            CancellationToken.None);

        RootSessionPage firstPage = await queries.GetRootSessionsAsync(
            new RootSessionPageRequest(AllDay(), pageSize: 1),
            CancellationToken.None);
        Assert.AreEqual(
            cliIdentity,
            Assert.ContainsSingle(firstPage.Items).Identity);
        Assert.IsNotNull(firstPage.NextCursor);
        RootSessionPage secondPage = await queries.GetRootSessionsAsync(
            new RootSessionPageRequest(
                AllDay(),
                pageSize: 1,
                firstPage.NextCursor),
            CancellationToken.None);
        Assert.AreEqual(
            desktopIdentity,
            Assert.ContainsSingle(secondPage.Items).Identity);
        Assert.IsNull(secondPage.NextCursor);

        RootSessionDetail cliDetail = (await queries.GetRootSessionDetailAsync(
            AllDay(),
            cliIdentity,
            CancellationToken.None))!;
        RootSessionDetail desktopDetail =
            (await queries.GetRootSessionDetailAsync(
                AllDay(),
                desktopIdentity,
                CancellationToken.None))!;
        Assert.AreEqual(100L, cliDetail.Summary.Metrics.NormalizedTotal.Value);
        Assert.AreEqual(
            200L,
            desktopDetail.Summary.Metrics.NormalizedTotal.Value);
        Assert.AreEqual("claude-cli-model", Assert.ContainsSingle(cliDetail.Models).Model);
        Assert.AreEqual(
            "claude-desktop-model",
            Assert.ContainsSingle(desktopDetail.Models).Model);
        ProjectUsageRow combinedProject = Assert.ContainsSingle(
            await queries.GetProjectsAsync(AllDay(), CancellationToken.None));
        Assert.AreEqual(2, combinedProject.RootSessionCount);

        TurnUsagePage cliTurns = await queries.GetTurnsAsync(
            AllDay(),
            cliIdentity,
            CancellationToken.None);
        TurnUsagePage desktopTurns = await queries.GetTurnsAsync(
            AllDay(),
            desktopIdentity,
            CancellationToken.None);
        Assert.AreEqual(
            100L,
            Assert.ContainsSingle(cliTurns.Turns).Metrics.NormalizedTotal.Value);
        Assert.AreEqual(
            200L,
            Assert.ContainsSingle(desktopTurns.Turns).Metrics.NormalizedTotal.Value);
        IReadOnlyList<TurnCallUsageRow> cliCalls =
            await queries.GetTurnCallsAsync(
                AllDay(),
                cliIdentity,
                turnIdHash,
                CancellationToken.None);
        Assert.AreEqual("claude-cli-model", Assert.ContainsSingle(cliCalls).Model);
    }

    [TestMethod]
    public async Task Projects_KeepUnidentifiedUsageVisibleAndFilterable()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        const string knownProjectId = "0123456789abcdef01234567";
        UsageEvent known = CreateEvent(
            "known-project-event",
            "known-project-dedup",
            Event1Time,
            "codex",
            "gpt-test",
            100,
            60,
            sessionId: "known-project-session",
            projectId: knownProjectId,
            projectPath: @"D:\Repo");
        UsageEvent unidentified = CreateEvent(
            "unidentified-project-event",
            "unidentified-project-dedup",
            Event2Time,
            "codex",
            "gpt-test",
            75,
            25,
            sessionId: "unidentified-project-session");
        await writer.CommitAsync(Batch([known]), CancellationToken.None);
        await writer.CommitAsync(Batch([unidentified]), CancellationToken.None);

        IReadOnlyList<ProjectUsageRow> projects =
            await queries.GetProjectsAsync(AllDay(), CancellationToken.None);

        Assert.HasCount(2, projects);
        ProjectUsageRow unknown = projects.Single(static row => row.IsUnidentified);
        Assert.AreEqual(ProjectUsageRow.UnidentifiedProjectId, unknown.ProjectId);
        Assert.AreEqual(PathAvailability.Unavailable, unknown.PathAvailability);
        Assert.AreEqual(75L, unknown.Metrics.NormalizedTotal.Value);
        Assert.AreEqual(1, unknown.RootSessionCount);

        var unknownFilter = new UsageFilter(
            DayStart(),
            DayEnd(),
            unidentifiedProjectOnly: true);
        UsageOverview overview = await queries.GetOverviewAsync(
            unknownFilter,
            CancellationToken.None);
        RootSessionPage sessions = await queries.GetRootSessionsAsync(
            new RootSessionPageRequest(unknownFilter),
            CancellationToken.None);

        Assert.AreEqual(1L, overview.RequestCount);
        Assert.AreEqual(75L, overview.NormalizedTotal.Value);
        Assert.AreEqual(
            "unidentified-project-session",
            Assert.ContainsSingle(sessions.Items).RootSessionId);
        Assert.ThrowsExactly<ArgumentException>(() => new UsageFilter(
            DayStart(),
            DayEnd(),
            projectId: knownProjectId,
            unidentifiedProjectOnly: true));
    }

    [TestMethod]
    public async Task PromptQueries_MergeExactlySpawnedSubagentAndKeepCallsDistinct()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        string rootTurn = new('a', 64);
        string childTurn = new('b', 64);
        string childLeaf = new('c', 64);
        string rootDedup = new('d', 64);
        string childDedup = new('e', 64);
        DateTimeOffset rootStarted = Event1Time.AddMinutes(-1);
        DateTimeOffset rootCompleted = Event2Time.AddMinutes(1);

        UsageEvent root = CreateEvent(
            "prompt-root-event",
            rootDedup,
            Event1Time,
            "codex",
            "gpt-main",
            100,
            60,
            sessionId: "prompt-root",
            turnIdHash: rootTurn);
        await writer.CommitAsync(
            Batch([root]) with
            {
                Sessions =
                [
                    Session("prompt-root", SessionKind.Primary) with
                    {
                        SessionRole = SessionRole.Main
                    }
                ],
                Turns =
                [
                    new UsageTurnMetadata(
                        "codex",
                        "codex:windows:test",
                        "rollout:test",
                        "prompt-root",
                        rootTurn,
                        rootStarted,
                        rootCompleted,
                        "实现 Prompt 归因",
                        2,
                        "codex-v1")
                ],
                Dispatches =
                [
                    new UsageTurnDispatch(
                        "codex",
                        "codex:windows:test",
                        "rollout:test",
                        "prompt-root",
                        rootTurn,
                        new string('f', 64),
                        childLeaf,
                        TurnDispatchKind.Spawn,
                        DispatchTargetKind.AgentLeaf,
                        Event1Time.AddSeconds(10),
                        "codex-v1")
                ]
            },
            CancellationToken.None);

        UsageEvent child = CreateEvent(
            "prompt-child-event",
            childDedup,
            Event2Time,
            "codex",
            "gpt-child",
            50,
            30,
            sessionId: "prompt-child",
            turnIdHash: childTurn);
        await writer.CommitAsync(
            Batch([child]) with
            {
                Sessions =
                [
                    Session(
                        "prompt-child",
                        SessionKind.Side,
                        "prompt-root") with
                    {
                        SessionRole = SessionRole.Subagent,
                        AgentLeafHash = childLeaf,
                        AgentPathHash = new string('1', 64)
                    }
                ],
                Turns =
                [
                    new UsageTurnMetadata(
                        "codex",
                        "codex:windows:test",
                        "rollout:test",
                        "prompt-child",
                        childTurn,
                        Event2Time.AddSeconds(-10),
                        Event2Time.AddSeconds(10),
                        null,
                        0,
                        "codex-v1")
                ],
                EventTools =
                [
                    new UsageEventToolMetadata(
                        "codex",
                        "codex:windows:test",
                        "rollout:test",
                        childDedup,
                        0,
                        "shell_command",
                        "codex-v1"),
                    new UsageEventToolMetadata(
                        "codex",
                        "codex:windows:test",
                        "rollout:test",
                        childDedup,
                        1,
                        "shell_command",
                        "codex-v1")
                ]
            },
            CancellationToken.None);

        TurnUsagePage prompts = await queries.GetTurnsAsync(
            AllDay(),
            RootIdentity("prompt-root"),
            CancellationToken.None);
        TurnUsageRow prompt = Assert.ContainsSingle(prompts.Turns);
        Assert.AreEqual(TurnCoverageStatus.Complete, prompts.Coverage);
        Assert.AreEqual(1L, prompts.PromptTurnCount);
        Assert.AreEqual(2L, prompt.CallCount);
        Assert.AreEqual(150L, prompt.Metrics.NormalizedTotal.Value);
        Assert.AreEqual("实现 Prompt 归因", prompt.PromptPreview);
        Assert.AreEqual(2, prompt.UserMessageCount);
        Assert.AreEqual(2L, prompt.ToolCallCount);
        Assert.AreEqual(150L, prompt.MaxPromptTokens);
        Assert.AreEqual(0L, prompts.Unattributed.CallCount);

        IReadOnlyList<TurnCallUsageRow> calls =
            await queries.GetTurnCallsAsync(
                AllDay(),
                RootIdentity("prompt-root"),
                rootTurn,
                CancellationToken.None);
        Assert.AreEqual(
            2,
            calls.Count,
            string.Join(
                ", ",
                calls.Select(static call =>
                    $"{call.SessionId}:{call.Model}:{call.Metrics.NormalizedTotal.Value}")));
        Assert.AreEqual(SessionRole.Main, calls[0].SessionRole);
        Assert.AreEqual(SessionRole.Subagent, calls[1].SessionRole);
        Assert.AreEqual(2, calls[1].Tools.Count);
        Assert.IsTrue(calls[1].Tools.All(static tool => tool == "shell_command"));
        Assert.AreEqual(
            prompt.Metrics.NormalizedTotal.Value,
            calls.Sum(static call => call.Metrics.NormalizedTotal.Value));
    }

    [TestMethod]
    public async Task PromptAttribution_FollowsNestedSpawnAndLaterFollowUpAfterOutOfOrderCollection()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        string rootTurnA = new('a', 64);
        string rootTurnB = new('b', 64);
        string childTurnA = new('c', 64);
        string childTurnB = new('d', 64);
        string childLeaf = new('e', 64);
        string childPath = new('f', 64);
        string grandTurn = new('1', 64);
        string grandLeaf = new('2', 64);

        UsageEvent childA = CreateEvent(
            "out-of-order-child-a",
            new string('3', 64),
            Event1Time.AddMinutes(10),
            "codex",
            "gpt-child",
            10,
            6,
            sessionId: "nested-child",
            turnIdHash: childTurnA);
        UsageEvent childB = CreateEvent(
            "out-of-order-child-b",
            new string('4', 64),
            Event2Time.AddMinutes(10),
            "codex",
            "gpt-child",
            20,
            12,
            sessionId: "nested-child",
            turnIdHash: childTurnB);
        await writer.CommitAsync(
            Batch([childA, childB]) with
            {
                Sessions =
                [
                    Session(
                        "nested-child",
                        SessionKind.Side,
                        "nested-root") with
                    {
                        SessionRole = SessionRole.Subagent,
                        AgentLeafHash = childLeaf,
                        AgentPathHash = childPath
                    }
                ],
                Turns =
                [
                    Turn(
                        "nested-child",
                        childTurnA,
                        Event1Time.AddMinutes(9),
                        Event1Time.AddMinutes(15)),
                    Turn(
                        "nested-child",
                        childTurnB,
                        Event2Time.AddMinutes(9),
                        Event2Time.AddMinutes(15))
                ],
                Dispatches =
                [
                    Dispatch(
                        "nested-child",
                        childTurnA,
                        new string('5', 64),
                        grandLeaf,
                        TurnDispatchKind.Spawn,
                        DispatchTargetKind.AgentLeaf,
                        Event1Time.AddMinutes(11))
                ]
            },
            CancellationToken.None);

        UsageEvent grandchild = CreateEvent(
            "out-of-order-grandchild",
            new string('6', 64),
            Event1Time.AddMinutes(13),
            "codex",
            "gpt-grandchild",
            5,
            3,
            sessionId: "nested-grandchild",
            turnIdHash: grandTurn);
        await writer.CommitAsync(
            Batch([grandchild]) with
            {
                Sessions =
                [
                    Session(
                        "nested-grandchild",
                        SessionKind.Side,
                        "nested-child") with
                    {
                        SessionRole = SessionRole.Subagent,
                        AgentLeafHash = grandLeaf,
                        AgentPathHash = new string('7', 64)
                    }
                ],
                Turns =
                [
                    Turn(
                        "nested-grandchild",
                        grandTurn,
                        Event1Time.AddMinutes(12),
                        Event1Time.AddMinutes(14))
                ]
            },
            CancellationToken.None);

        UsageEvent rootA = CreateEvent(
            "out-of-order-root-a",
            new string('8', 64),
            Event1Time.AddMinutes(1),
            "codex",
            "gpt-root",
            100,
            60,
            sessionId: "nested-root",
            turnIdHash: rootTurnA);
        UsageEvent rootB = CreateEvent(
            "out-of-order-root-b",
            new string('9', 64),
            Event2Time.AddMinutes(1),
            "codex",
            "gpt-root",
            200,
            120,
            sessionId: "nested-root",
            turnIdHash: rootTurnB);
        await writer.CommitAsync(
            Batch([rootA, rootB]) with
            {
                Sessions =
                [
                    Session("nested-root", SessionKind.Primary) with
                    {
                        SessionRole = SessionRole.Main
                    }
                ],
                Turns =
                [
                    Turn(
                        "nested-root",
                        rootTurnA,
                        Event1Time,
                        Event1Time.AddMinutes(30),
                        "首轮"),
                    Turn(
                        "nested-root",
                        rootTurnB,
                        Event2Time,
                        Event2Time.AddMinutes(30),
                        "后续轮")
                ],
                Dispatches =
                [
                    Dispatch(
                        "nested-root",
                        rootTurnA,
                        new string('a', 64),
                        childLeaf,
                        TurnDispatchKind.Spawn,
                        DispatchTargetKind.AgentLeaf,
                        Event1Time.AddMinutes(5)),
                    Dispatch(
                        "nested-root",
                        rootTurnB,
                        new string('b', 64),
                        childPath,
                        TurnDispatchKind.FollowUp,
                        DispatchTargetKind.AgentPath,
                        Event2Time.AddMinutes(5))
                ]
            },
            CancellationToken.None);

        TurnUsagePage prompts = await queries.GetTurnsAsync(
            new UsageFilter(DayStart(), DayEnd(), limit: 1),
            RootIdentity("nested-root"),
            CancellationToken.None);
        Assert.AreEqual(TurnCoverageStatus.Complete, prompts.Coverage);
        Assert.HasCount(1, prompts.Turns);
        Assert.AreEqual(
            2L,
            prompts.PromptTurnCount,
            "Prompt 总轮次必须覆盖完整筛选结果，不能退化为当前分页条数。");
        Assert.AreEqual(0L, prompts.Unattributed.CallCount);
        CollectionAssert.AreEqual(
            new[] { rootTurnB },
            prompts.Turns.Select(static row => row.TurnIdHash).ToArray(),
            "Prompt 时间线应按开始时间从新到旧返回。");
        TurnUsageRow second = prompts.Turns.Single(
            row => row.TurnIdHash == rootTurnB);
        Assert.AreEqual(2L, second.CallCount);
        Assert.AreEqual(220L, second.Metrics.NormalizedTotal.Value);
        Assert.AreEqual(220L, second.MaxPromptTokens);

        IReadOnlyList<TurnCallUsageRow> firstCalls =
            await queries.GetTurnCallsAsync(
                AllDay(),
                RootIdentity("nested-root"),
                rootTurnA,
                CancellationToken.None);
        Assert.HasCount(3, firstCalls);
        Assert.HasCount(
            2,
            firstCalls.Where(static call =>
                call.SessionRole is SessionRole.Subagent));
    }

    [TestMethod]
    public async Task SourceParentSubagentAttribution_UsesUniqueParentIntervalAndLeavesOverlapUncertain()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        string rootTurnA = new('a', 64);
        string rootTurnB = new('b', 64);
        string rootTurnC = new('c', 64);
        string childTurnA = new('d', 64);
        string childTurnB = new('e', 64);

        await writer.CommitAsync(
            Batch(
            [
                CreateEvent(
                    "source-parent-root-a",
                    new string('1', 64),
                    Event1Time.AddMinutes(1),
                    "codex",
                    "gpt-root",
                    100,
                    60,
                    sessionId: "source-parent-root",
                    turnIdHash: rootTurnA),
                CreateEvent(
                    "source-parent-root-b",
                    new string('2', 64),
                    Event2Time.AddMinutes(1),
                    "codex",
                    "gpt-root",
                    200,
                    120,
                    sessionId: "source-parent-root",
                    turnIdHash: rootTurnB),
                CreateEvent(
                    "source-parent-root-c",
                    new string('3', 64),
                    Event2Time.AddMinutes(6),
                    "codex",
                    "gpt-root",
                    300,
                    180,
                    sessionId: "source-parent-root",
                    turnIdHash: rootTurnC)
            ]) with
            {
                Sessions =
                [
                    Session("source-parent-root", SessionKind.Primary) with
                    {
                        SessionRole = SessionRole.Main
                    }
                ],
                Turns =
                [
                    Turn(
                        "source-parent-root",
                        rootTurnA,
                        Event1Time,
                        Event1Time.AddMinutes(30),
                        "unique parent"),
                    Turn(
                        "source-parent-root",
                        rootTurnB,
                        Event2Time,
                        Event2Time.AddMinutes(30),
                        "overlapping parent one"),
                    Turn(
                        "source-parent-root",
                        rootTurnC,
                        Event2Time.AddMinutes(5),
                        Event2Time.AddMinutes(20),
                        "overlapping parent two")
                ]
            },
            CancellationToken.None);

        await writer.CommitAsync(
            Batch(
            [
                CreateEvent(
                    "source-parent-child-a",
                    new string('4', 64),
                    Event1Time.AddMinutes(10),
                    "codex",
                    "gpt-child",
                    10,
                    6,
                    sessionId: "source-parent-child-a",
                    turnIdHash: childTurnA),
                CreateEvent(
                    "source-parent-child-b",
                    new string('5', 64),
                    Event2Time.AddMinutes(10),
                    "codex",
                    "gpt-child",
                    20,
                    12,
                    sessionId: "source-parent-child-b",
                    turnIdHash: childTurnB)
            ]) with
            {
                Sessions =
                [
                    Session(
                        "source-parent-child-a",
                        SessionKind.Side,
                        "source-parent-root",
                        relationOrigin: SessionRelationOrigin.SourceAgentParent) with
                    {
                        SessionRole = SessionRole.Subagent
                    },
                    Session(
                        "source-parent-child-b",
                        SessionKind.Side,
                        "source-parent-root",
                        relationOrigin: SessionRelationOrigin.SourceAgentParent) with
                    {
                        SessionRole = SessionRole.Subagent
                    }
                ],
                Turns =
                [
                    Turn(
                        "source-parent-child-a",
                        childTurnA,
                        Event1Time.AddMinutes(10),
                        Event1Time.AddMinutes(11)),
                    Turn(
                        "source-parent-child-b",
                        childTurnB,
                        Event2Time.AddMinutes(10),
                        Event2Time.AddMinutes(11))
                ]
            },
            CancellationToken.None);

        TurnUsagePage prompts = await queries.GetTurnsAsync(
            AllDay(),
            RootIdentity("source-parent-root"),
            CancellationToken.None);

        Assert.AreEqual(TurnCoverageStatus.Partial, prompts.Coverage);
        Assert.AreEqual(
            110L,
            prompts.Turns.Single(row => row.TurnIdHash == rootTurnA)
                .Metrics.NormalizedTotal.Value);
        Assert.AreEqual(1L, prompts.Unattributed.CallCount);
        Assert.AreEqual(20L, prompts.Unattributed.Metrics.NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task GoalContinuationAttribution_FoldsRawTurnAndChildIntoOriginPrompt()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        string promptTurn = new('a', 64);
        string continuationTurn = new('b', 64);
        string childTurn = new('c', 64);

        await writer.CommitAsync(
            Batch(
            [
                CreateEvent(
                    "goal-root-prompt",
                    new string('1', 64),
                    Event1Time.AddMinutes(2),
                    "codex",
                    "gpt-root",
                    100,
                    60,
                    sessionId: "goal-root",
                    turnIdHash: promptTurn),
                CreateEvent(
                    "goal-root-continuation",
                    new string('2', 64),
                    Event1Time.AddMinutes(12),
                    "codex",
                    "gpt-root",
                    200,
                    120,
                    sessionId: "goal-root",
                    turnIdHash: continuationTurn)
            ]) with
            {
                Sessions =
                [
                    Session("goal-root", SessionKind.Primary) with
                    {
                        SessionRole = SessionRole.Main
                    }
                ],
                Turns =
                [
                    Turn(
                        "goal-root",
                        promptTurn,
                        Event1Time,
                        Event1Time.AddMinutes(5),
                        "user prompt"),
                    Turn(
                        "goal-root",
                        continuationTurn,
                        Event1Time.AddMinutes(10),
                        Event1Time.AddMinutes(20),
                        promptOriginTurnIdHash: promptTurn)
                ]
            },
            CancellationToken.None);

        await writer.CommitAsync(
            Batch(
            [
                CreateEvent(
                    "goal-child",
                    new string('3', 64),
                    Event1Time.AddMinutes(15),
                    "codex",
                    "gpt-child",
                    10,
                    6,
                    sessionId: "goal-child",
                    turnIdHash: childTurn)
            ]) with
            {
                Sessions =
                [
                    Session(
                        "goal-child",
                        SessionKind.Side,
                        "goal-root",
                        relationOrigin: SessionRelationOrigin.SourceAgentParent) with
                    {
                        SessionRole = SessionRole.Subagent
                    }
                ],
                Turns =
                [
                    Turn(
                        "goal-child",
                        childTurn,
                        Event1Time.AddMinutes(15),
                        Event1Time.AddMinutes(16))
                ]
            },
            CancellationToken.None);

        TurnUsagePage prompts = await queries.GetTurnsAsync(
            AllDay(),
            RootIdentity("goal-root"),
            CancellationToken.None);

        Assert.AreEqual(TurnCoverageStatus.Complete, prompts.Coverage);
        TurnUsageRow prompt = Assert.ContainsSingle(prompts.Turns);
        Assert.AreEqual(promptTurn, prompt.TurnIdHash);
        Assert.AreEqual(3L, prompt.CallCount);
        Assert.AreEqual(310L, prompt.Metrics.NormalizedTotal.Value);
        Assert.AreEqual(1L, prompts.PromptTurnCount);
        Assert.AreEqual(0L, prompts.Unattributed.CallCount);
    }

    [TestMethod]
    public async Task GuardianAttribution_UsesUniqueParentIntervalAndLeavesOverlapUncertain()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        string rootTurnA = new('a', 64);
        string rootTurnB = new('b', 64);
        string rootTurnC = new('c', 64);
        string guardianTurnA = new('d', 64);
        string guardianTurnB = new('e', 64);
        UsageEvent[] rootEvents =
        [
            CreateEvent(
                "guardian-root-a",
                new string('1', 64),
                Event1Time.AddMinutes(1),
                "codex",
                "gpt-root",
                100,
                60,
                sessionId: "guardian-root",
                turnIdHash: rootTurnA),
            CreateEvent(
                "guardian-root-b",
                new string('2', 64),
                Event2Time.AddMinutes(1),
                "codex",
                "gpt-root",
                200,
                120,
                sessionId: "guardian-root",
                turnIdHash: rootTurnB),
            CreateEvent(
                "guardian-root-c",
                new string('3', 64),
                Event2Time.AddMinutes(6),
                "codex",
                "gpt-root",
                300,
                180,
                sessionId: "guardian-root",
                turnIdHash: rootTurnC)
        ];
        await writer.CommitAsync(
            Batch(rootEvents) with
            {
                Sessions =
                [
                    Session("guardian-root", SessionKind.Primary) with
                    {
                        SessionRole = SessionRole.Main
                    }
                ],
                Turns =
                [
                    Turn(
                        "guardian-root",
                        rootTurnA,
                        Event1Time,
                        Event1Time.AddMinutes(30),
                        "唯一父轮次"),
                    Turn(
                        "guardian-root",
                        rootTurnB,
                        Event2Time,
                        Event2Time.AddMinutes(30),
                        "重叠父轮次一"),
                    Turn(
                        "guardian-root",
                        rootTurnC,
                        Event2Time.AddMinutes(5),
                        Event2Time.AddMinutes(20),
                        "重叠父轮次二")
                ]
            },
            CancellationToken.None);

        UsageEvent[] guardianEvents =
        [
            CreateEvent(
                "guardian-child-a",
                new string('4', 64),
                Event1Time.AddMinutes(10),
                "codex",
                "codex-auto-review",
                10,
                6,
                sessionId: "guardian-child",
                turnIdHash: guardianTurnA),
            CreateEvent(
                "guardian-child-b",
                new string('5', 64),
                Event2Time.AddMinutes(10),
                "codex",
                "codex-auto-review",
                20,
                12,
                sessionId: "guardian-child",
                turnIdHash: guardianTurnB)
        ];
        await writer.CommitAsync(
            Batch(guardianEvents) with
            {
                Sessions =
                [
                    Session(
                        "guardian-child",
                        SessionKind.Side,
                        "guardian-root") with
                    {
                        SessionRole = SessionRole.Guardian
                    }
                ],
                Turns =
                [
                    Turn(
                        "guardian-child",
                        guardianTurnA,
                        Event1Time.AddMinutes(10),
                        Event1Time.AddMinutes(11)),
                    Turn(
                        "guardian-child",
                        guardianTurnB,
                        Event2Time.AddMinutes(10),
                        Event2Time.AddMinutes(11))
                ]
            },
            CancellationToken.None);

        TurnUsagePage prompts = await queries.GetTurnsAsync(
            AllDay(),
            RootIdentity("guardian-root"),
            CancellationToken.None);
        Assert.AreEqual(TurnCoverageStatus.Partial, prompts.Coverage);
        Assert.HasCount(3, prompts.Turns);
        Assert.AreEqual(3L, prompts.PromptTurnCount);
        Assert.AreEqual(
            110L,
            prompts.Turns.Single(row => row.TurnIdHash == rootTurnA)
                .Metrics.NormalizedTotal.Value);
        Assert.AreEqual(20L, prompts.Unattributed.Metrics.NormalizedTotal.Value);
        Assert.AreEqual(1L, prompts.Unattributed.CallCount);
        Assert.AreEqual(
            630L,
            prompts.Turns.Sum(static row => row.Metrics.NormalizedTotal.Value) +
            prompts.Unattributed.Metrics.NormalizedTotal.Value);

        IReadOnlyList<TurnCallUsageRow> calls =
            await queries.GetTurnCallsAsync(
                AllDay(),
                RootIdentity("guardian-root"),
                rootTurnA,
                CancellationToken.None);
        Assert.HasCount(2, calls);
        Assert.AreEqual(SessionRole.Guardian, calls[1].SessionRole);
        Assert.AreEqual("codex-auto-review", calls[1].Model);
    }

    [TestMethod]
    public async Task ProjectFilterValues_KeepFullPathsAndScopeEveryAggregate()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        const string rootProjectId = "0123456789abcdef01234567";
        const string childProjectId = "89abcdef0123456701234567";
        const string legacyProjectId = "fedcba987654321001234567";
        UsageEvent root = CreateEvent(
            "project-root",
            "dedup-project-root",
            Event1Time,
            "codex",
            "gpt-5.6",
            100,
            60,
            sourceEntityId: "rollout:project-root",
            projectId: rootProjectId,
            projectPath: @"D:\Repo",
            cacheRead: TokenMetric.Exact(10),
            cacheWrite: TokenMetric.Exact(0));
        UsageEvent child = CreateEvent(
            "project-child",
            "dedup-project-child",
            Event2Time,
            "codex",
            "gpt-5.6",
            200,
            120,
            sourceEntityId: "rollout:project-child",
            projectId: childProjectId,
            projectPath: @"D:\Repo\frontend",
            cacheRead: TokenMetric.Exact(20),
            cacheWrite: TokenMetric.Exact(0));
        UsageEvent legacy = CreateEvent(
            "project-legacy",
            "dedup-project-legacy",
            Event3Time,
            "codex",
            "gpt-5.6",
            300,
            180,
            sourceEntityId: "rollout:project-legacy",
            projectId: legacyProjectId,
            cacheRead: TokenMetric.Exact(30),
            cacheWrite: TokenMetric.Exact(0));
        await writer.CommitAsync(Batch([root]), CancellationToken.None);
        await writer.CommitAsync(Batch([child]), CancellationToken.None);
        await writer.CommitAsync(Batch([legacy]), CancellationToken.None);

        UsageFilterValues values =
            await queries.GetFilterValuesAsync(AllDay(), CancellationToken.None);

        Assert.HasCount(3, values.Projects);
        Assert.AreEqual(rootProjectId, values.Projects[0].ProjectId);
        Assert.AreEqual(@"D:\Repo", values.Projects[0].ProjectPath);
        Assert.AreEqual(childProjectId, values.Projects[1].ProjectId);
        Assert.AreEqual(@"D:\Repo\frontend", values.Projects[1].ProjectPath);
        Assert.AreEqual(legacyProjectId, values.Projects[2].ProjectId);
        Assert.IsNull(values.Projects[2].ProjectPath);
        Assert.AreEqual(
            PathAvailability.Unavailable,
            values.Projects[2].PathAvailability);

        var filter = new UsageFilter(
            DayStart(),
            DayEnd(),
            projectId: childProjectId);
        UsageOverview overview =
            await queries.GetOverviewAsync(filter, CancellationToken.None);
        IReadOnlyList<UsageTrendPoint> trend =
            await queries.GetTrendAsync(filter, CancellationToken.None);
        AgentUsageRow agent = Assert.ContainsSingle(
            await queries.GetAgentsAsync(filter, CancellationToken.None));
        ModelUsageRow model = Assert.ContainsSingle(
            await queries.GetModelsAsync(filter, CancellationToken.None));
        AgentModelUsageRow agentModel = Assert.ContainsSingle(
            await queries.GetAgentModelsAsync(filter, CancellationToken.None));

        Assert.AreEqual(1L, overview.RequestCount);
        Assert.AreEqual(200L, overview.NormalizedTotal.Value);
        Assert.AreNotEqual(
            PricingCoverageStatus.Unpriced,
            overview.Pricing?.Coverage);
        Assert.IsNotNull(overview.Pricing?.KnownAmountUsd);
        Assert.HasCount(1, trend);
        Assert.AreEqual(200L, trend[0].NormalizedTotal.Value);
        Assert.AreEqual(200L, agent.NormalizedTotal.Value);
        Assert.AreEqual(200L, model.NormalizedTotal.Value);
        Assert.AreEqual(200L, agentModel.NormalizedTotal.Value);
        Assert.AreEqual(
            overview.Pricing?.KnownAmountUsd,
            trend[0].Pricing?.KnownAmountUsd);
    }

    [TestMethod]
    public async Task ProjectQueries_PreferOrdinaryCheckoutPathForMergedWorktrees()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        const string projectId = "0123456789abcdef01234567";
        UsageEvent main = CreateEvent(
            "project-main",
            "dedup-project-main",
            Event1Time,
            "codex",
            "gpt-5.6",
            100,
            60,
            sourceEntityId: "rollout:project-main",
            projectId: projectId,
            projectPath: @"C:\Projects\AgenTally");
        UsageEvent worktree = CreateEvent(
            "project-worktree",
            "dedup-project-worktree",
            Event2Time,
            "codex",
            "gpt-5.6",
            200,
            120,
            sourceEntityId: "rollout:project-worktree",
            projectId: projectId,
            projectPath:
                @"C:\Users\fixture\.codex\worktrees\2f68\AgenTally");
        await writer.CommitAsync(Batch([main]), CancellationToken.None);
        await writer.CommitAsync(Batch([worktree]), CancellationToken.None);

        UsageFilterValues filters =
            await queries.GetFilterValuesAsync(AllDay(), CancellationToken.None);
        ProjectFilterValue filterProject = Assert.ContainsSingle(filters.Projects);
        Assert.AreEqual(projectId, filterProject.ProjectId);
        Assert.AreEqual(
            @"C:\Projects\AgenTally",
            filterProject.ProjectPath);

        ProjectUsageRow project = Assert.ContainsSingle(
            await queries.GetProjectsAsync(AllDay(), CancellationToken.None));
        Assert.AreEqual(projectId, project.ProjectId);
        Assert.AreEqual(@"C:\Projects\AgenTally", project.ProjectPath);
        Assert.AreEqual(300L, project.Metrics.NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task MetricCoverage_DistinguishesZeroUnknownUnavailableAndNoData()
    {
        using var directory = new TestTempDirectory();
        (SqliteUsageWriter writer, SqliteUsageQueryService queries) =
            await CreateServicesAsync(directory);
        UsageEvent value = CreateEvent(
            "coverage-event",
            "coverage-dedup",
            Event1Time,
            "codex",
            "gpt-test",
            10,
            5,
            cacheRead: TokenMetric.Unknown,
            cacheWrite: TokenMetric.Exact(0),
            tool: TokenMetric.Unavailable);
        await writer.CommitAsync(Batch([value]), CancellationToken.None);

        UsageOverview overview =
            await queries.GetOverviewAsync(AllDay(), CancellationToken.None);
        Assert.AreEqual(0L, overview.Metrics!.CacheWrite.Value);
        Assert.AreEqual(
            MetricCoverageStatus.Complete,
            overview.Metrics.CacheWrite.Coverage);
        Assert.AreEqual(
            MetricCoverageStatus.Unknown,
            overview.Metrics.CacheRead.Coverage);
        Assert.AreEqual(1, overview.Metrics.CacheRead.UnknownRecords);
        Assert.AreEqual(
            MetricCoverageStatus.Unavailable,
            overview.Metrics.Tool.Coverage);

        UsageOverview empty = await queries.GetOverviewAsync(
            new UsageFilter(
                DayEnd(),
                DayEnd().AddDays(1)),
            CancellationToken.None);
        Assert.AreEqual(
            MetricCoverageStatus.NoData,
            empty.Metrics!.NormalizedTotal.Coverage);
    }

    [TestMethod]
    public void UsageFilter_ValidatesUtcRangeLimitOffsetAndBlankValues()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new UsageFilter(DayStart().ToOffset(TimeSpan.FromHours(8)), DayEnd()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new UsageFilter(DayStart(), DayStart()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new UsageFilter(DayStart(), DayEnd(), limit: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new UsageFilter(DayStart(), DayEnd(), limit: 1001));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new UsageFilter(DayStart(), DayEnd(), offset: -1));

        var filter = new UsageFilter(
            DayStart(),
            DayEnd(),
            agentId: " ",
            normalizedModel: "   ");
        Assert.IsNull(filter.AgentId);
        Assert.IsNull(filter.NormalizedModel);
        Assert.IsNull(filter.ProjectId);
        Assert.IsNull(filter.RootSessionId);
    }

    private static readonly DateTimeOffset Event1Time =
        new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Event2Time =
        new(2026, 7, 16, 1, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Event3Time =
        new(2026, 7, 16, 2, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset BatchCheckedAtUtc =
        new(2026, 7, 16, 2, 0, 0, TimeSpan.Zero);

    private static async Task<(SqliteUsageWriter Writer, SqliteUsageQueryService Queries)>
        CreateServicesAsync(TestTempDirectory directory)
    {
        var connections = new SqliteConnectionFactory(
            new StorageOptions(directory.File("agentally.db")));
        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);
        return (writer, new SqliteUsageQueryService(connections));
    }

    private static async Task SeedAsync(SqliteUsageWriter writer)
    {
        UsageEvent[] events =
        [
            CreateEvent(
                "event-1",
                "codex:thread-1:1",
                Event1Time,
                "codex",
                "gpt-test",
                normalizedTotal: 100,
                uncachedInput: 60),
            CreateEvent(
                "event-2",
                "codex:thread-2:1",
                Event2Time,
                "other-agent",
                "claude-test",
                normalizedTotal: 100,
                uncachedInput: null,
                sourceInstanceId: "other:windows:test",
                sourceEntityId: "other:rollout:test")
        ];

        await writer.CommitAsync(Batch([events[0]]), CancellationToken.None);
        await writer.CommitAsync(Batch([events[1]]), CancellationToken.None);
    }

    private static UsageEvent CreateEvent(
        string eventId,
        string dedupKey,
        DateTimeOffset occurredAtUtc,
        string agentId,
        string? normalizedModel,
        long normalizedTotal,
        long? uncachedInput,
        string sourceInstanceId = "codex:windows:test",
        string sourceEntityId = "rollout:test",
        string? sessionId = null,
        string? projectId = null,
        string? projectPath = null,
        string? turnIdHash = null,
        TokenMetric? cacheRead = null,
        TokenMetric? cacheWrite = null,
        TokenMetric? tool = null)
    {
        var value = new UsageEvent(
            agentId,
            sourceInstanceId,
            sourceEntityId,
            eventId,
            dedupKey,
            SourceKind.Jsonl,
            occurredAtUtc,
            BatchCheckedAtUtc,
            new ModelIdentity
            {
                RawModel = normalizedModel,
                NormalizedModel = normalizedModel,
                ProviderId = "test",
                ResolutionOrigin = normalizedModel is null
                    ? ModelResolutionOrigin.Unknown
                    : ModelResolutionOrigin.LogConfirmed
            },
            new TokenUsage
            {
                NormalizedTotal = new TokenMetric(normalizedTotal, MetricOrigin.Derived),
                UncachedInput = uncachedInput.HasValue
                    ? TokenMetric.Exact(uncachedInput.Value)
                    : TokenMetric.Unavailable,
                Output = TokenMetric.Exact(30),
                CacheRead = cacheRead ?? TokenMetric.Unavailable,
                CacheWrite = cacheWrite ?? TokenMetric.Exact(0),
                Tool = tool ?? TokenMetric.Unavailable
            },
            CompletionState.Finalized,
            DataQuality.Exact,
            "codex-v1",
            "fixture-1",
            1);

        return value with
        {
            SessionId = sessionId ?? $"session-{eventId}",
            ProjectId = projectId,
            ProjectPath = projectPath,
            TurnIdHash = turnIdHash
        };
    }

    private static UsageEventBatch Batch(IReadOnlyList<UsageEvent> events) =>
        new(
            new SourceInstanceDescriptor(
                events[0].SourceInstanceId,
                events[0].AgentId,
                events[0].SourceKind,
                $"{events[0].AgentId} test",
                $"C:\\{events[0].AgentId}"),
            new SourceEntityDescriptor(
                events[0].SourceInstanceId,
                events[0].SourceEntityId,
                $"C:\\{events[0].AgentId}\\rollout-test.jsonl"),
            "cursor-1",
            events[0].SourceFingerprint,
            events[0].ParserVersion,
            BatchCheckedAtUtc,
            events);

    private static UsageTurnMetadata Turn(
        string sessionId,
        string turnIdHash,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? promptPreview = null,
        string agentId = "codex",
        string sourceInstanceId = "codex:windows:test",
        string sourceEntityId = "rollout:test",
        string? promptOriginTurnIdHash = null) => new(
            agentId,
            sourceInstanceId,
            sourceEntityId,
            sessionId,
            turnIdHash,
            startedAtUtc,
            completedAtUtc,
            promptPreview,
            promptPreview is null ? 0 : 1,
            "codex-v1",
            promptOriginTurnIdHash);

    private static UsageTurnDispatch Dispatch(
        string sourceSessionId,
        string sourceTurnIdHash,
        string dispatchIdHash,
        string targetAgentHash,
        TurnDispatchKind dispatchKind,
        DispatchTargetKind targetKind,
        DateTimeOffset occurredAtUtc) => new(
            "codex",
            "codex:windows:test",
            "rollout:test",
            sourceSessionId,
            sourceTurnIdHash,
            dispatchIdHash,
            targetAgentHash,
            dispatchKind,
            targetKind,
            occurredAtUtc,
            "codex-v1");

    private static UsageSessionMetadata Session(
        string sessionId,
        SessionKind kind,
        string? directParentSessionId = null,
        SessionRelationState relationState = SessionRelationState.Confirmed,
        string? projectId = null,
        string? projectPath = null,
        string agentId = "codex",
        string sourceInstanceId = "codex:windows:test",
        string sourceEntityId = "rollout:test",
        SessionRelationOrigin? relationOrigin = null)
    {
        SessionRelationState effectiveState = directParentSessionId is null
            ? relationState is SessionRelationState.Uncertain
                ? SessionRelationState.Uncertain
                : SessionRelationState.None
            : SessionRelationState.Confirmed;
        return new UsageSessionMetadata(
            agentId,
            sourceInstanceId,
            sourceEntityId,
            sessionId,
            kind,
            directParentSessionId,
            null,
            directParentSessionId is null
                ? SessionRelationOrigin.None
                : relationOrigin ?? SessionRelationOrigin.TopLevelParentThreadId,
            effectiveState,
            ReplayState.Active,
            effectiveState is SessionRelationState.Uncertain
                ? CompatibilityLevel.PartiallyCompatible
                : CompatibilityLevel.FullyCompatible,
            BatchCheckedAtUtc,
            "codex-v1")
        {
            ProjectId = projectId,
            ProjectPath = projectPath
        };
    }

    private static SourceInstanceDescriptor Instance() =>
        new(
            "codex:windows:test",
            "codex",
            SourceKind.Jsonl,
            "Codex test",
            "C:\\codex");

    private static RootSessionIdentity RootIdentity(string rootSessionId) =>
        new("codex", "codex:windows:test", rootSessionId);

    private static SourceEntityDescriptor Entity() =>
        new(
            "codex:windows:test",
            "rollout:test",
            "C:\\codex\\rollout-test.jsonl");

    private static UsageFilter AllDay() => new(DayStart(), DayEnd());

    private static DateTimeOffset DayStart() =>
        new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset DayEnd() =>
        new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);
}
