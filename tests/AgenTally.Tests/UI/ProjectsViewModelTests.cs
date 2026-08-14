using AgenTally.Domain.Usage;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class ProjectsViewModelTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private const string ProjectAId = "0123456789abcdef01234567";
    private const string ProjectBId = "89abcdef0123456701234567";

    [TestMethod]
    public async Task Refresh_PassesTimeAgentAndModelToFacetQuery()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            FilterValues = new UsageFilterValues(
                ["codex"],
                ["gpt-test"])
        };
        var viewModel = await host.InvokeAsync(() => new ProjectsViewModel(
            queries,
            host.Dispatcher,
            new FixedTimeProvider(NowUtc),
            TimeZoneInfo.Utc));
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedAgent = "codex";
            viewModel.SelectedModel = "gpt-test";
            viewModel.SelectedPeriod = ProjectsViewModel.SevenDays;
        });

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        UsageFilter filter =
            Assert.ContainsSingle(queries.FilterValueFilters);
        Assert.AreEqual("codex", filter.AgentId);
        Assert.AreEqual("gpt-test", filter.NormalizedModel);
        Assert.AreEqual(
            TimeSpan.FromDays(7),
            filter.EndExclusiveUtc - filter.StartInclusiveUtc);
    }

    [TestMethod]
    public async Task Refresh_BuildsProjectSummaryDetailsAndNaturalDayMetrics()
    {
        await using var host = new StaDispatcherTestHost();
        ProjectUsageRow project = Project(
            ProjectAId,
            @"C:\Projects\AgenTally",
            new DateTimeOffset(2026, 7, 28, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero),
            total: 1_000,
            rootSessions: 2);
        var codex = Agent("codex", 750, 3);
        var claude = Agent("claude", 250, 1);
        var queries = new FakeUsageQueryService
        {
            ProjectsResult = [project],
            FilterValues = new UsageFilterValues(
                ["codex", "claude"],
                ["gpt-test", "other-test"]),
            AgentModels =
            [
                AgentModel("codex", "gpt-test", 600, 2),
                AgentModel("claude", "other-test", 400, 2)
            ],
            RootSessionsHandler = request =>
            {
                Assert.AreEqual(ProjectAId, request.Filter.ProjectId);
                Assert.IsFalse(request.Filter.UnidentifiedProjectOnly);
                return new RootSessionPage(
                    [
                        RootSession("root-a", ProjectAId, 600),
                        RootSession("root-b", ProjectAId, 400)
                    ],
                    null);
            }
        };
        queries.SetProjectRoute(
            ProjectAId,
            Task.FromResult(
                new DashboardQueryResult(
                    TestData.Dashboard(1_000).Overview,
                    [
                        Trend(2026, 7, 28, 100),
                        Trend(2026, 7, 29, 200)
                    ],
                    [],
                    [],
                    [codex, claude])));
        var viewModel = await host.InvokeAsync(() => new ProjectsViewModel(
            queries,
            host.Dispatcher,
            new FixedTimeProvider(NowUtc),
            TimeZoneInfo.Utc));

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.HasCount(1, viewModel.Projects);
        Assert.AreEqual("AgenTally", viewModel.Projects[0].NameText);
        Assert.AreEqual(@"C:\Projects\AgenTally", viewModel.Projects[0].PathText);
        Assert.AreEqual("1,000", viewModel.Projects[0].TokensText);
        Assert.AreEqual(
            "2026年7月28日至2026年7月30日",
            viewModel.PeriodSummaryText);
        Assert.IsNotNull(viewModel.Detail);
        Assert.AreEqual(viewModel.Projects[0].TokensText, viewModel.Detail.TotalTokensText);
        Assert.AreEqual("2", viewModel.Detail.RootSessionCountText);
        Assert.AreEqual("2", viewModel.Detail.ActiveDayCountText);
        Assert.AreEqual(
            "0",
            viewModel.Detail.ConsecutiveDayCountText,
            "所选范围结束日无用量时连续活跃天数必须为 0。");
        StringAssert.Contains(viewModel.Detail.PeakDayText, "7月29日");
        StringAssert.Contains(viewModel.Detail.PeakDayToolTip, "200 Token");
        Assert.HasCount(3, viewModel.Detail.TrendPoints);
        Assert.HasCount(2, viewModel.Detail.Platforms);
        Assert.AreEqual("75.0%", viewModel.Detail.Platforms[0].ShareText);
        Assert.AreEqual("25.0%", viewModel.Detail.Platforms[1].ShareText);
        Assert.HasCount(2, viewModel.Detail.Models);
        Assert.AreEqual("60.0%", viewModel.Detail.Models[0].ShareText);
        Assert.AreEqual("40.0%", viewModel.Detail.Models[1].ShareText);
        Assert.HasCount(2, viewModel.Detail.Sessions);
        Assert.AreSame(viewModel.Detail.Platforms[0], viewModel.SelectedPlatform);
        Assert.AreSame(viewModel.Detail.Models[0], viewModel.SelectedModelDetail);
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedPlatform = viewModel.Detail.Platforms[1];
            viewModel.SelectedModelDetail = viewModel.Detail.Models[1];
        });
        Assert.AreEqual("claude", viewModel.SelectedPlatform?.NameText);
        Assert.AreEqual("250", viewModel.SelectedPlatform?.TotalTokensText);
        Assert.AreEqual("other-test", viewModel.SelectedModelDetail?.NameText);
        Assert.AreEqual("400", viewModel.SelectedModelDetail?.TotalTokensText);
    }

    [TestMethod]
    public async Task FilteredProjectTrend_UsesOnlyTheProjectActiveDateSpan()
    {
        await using var host = new StaDispatcherTestHost();
        ProjectUsageRow project = Project(
            ProjectAId,
            @"C:\Projects\AgenTally",
            new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
            total: 300,
            rootSessions: 1);
        var queries = new FakeUsageQueryService
        {
            ProjectsResult = [project]
        };
        queries.SetProjectRoute(
            ProjectAId,
            Task.FromResult(
                new DashboardQueryResult(
                    TestData.Dashboard(300).Overview,
                    [
                        Trend(2026, 7, 20, 100),
                        Trend(2026, 7, 21, 200)
                    ],
                    [],
                    [],
                    [])));
        ProjectsViewModel viewModel = await host.InvokeAsync(() =>
            new ProjectsViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = ProjectsViewModel.ThirtyDays);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(
            "2026年7月1日至2026年7月30日",
            viewModel.PeriodSummaryText);
        Assert.IsNotNull(viewModel.Detail);
        Assert.HasCount(2, viewModel.Detail.TrendPoints);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            viewModel.Detail.TrendPoints[0].BucketStartUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
            viewModel.Detail.TrendPoints[^1].BucketStartUtc);
    }

    [TestMethod]
    public async Task ProjectTrend_UsesHourlyThroughTwentyFourHoursAndKeepsDailyMetrics()
    {
        await using var host = new StaDispatcherTestHost();
        ProjectUsageRow project = Project(
            ProjectAId,
            @"C:\Projects\AgenTally",
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            total: 100,
            rootSessions: 1);
        var queries = new FakeUsageQueryService
        {
            ProjectsResult = [project]
        };
        queries.SetProjectRoute(
            ProjectAId,
            Task.FromResult(new DashboardQueryResult(
                TestData.Dashboard(100).Overview,
                [
                    new UsageTrendPoint(
                        new DateTimeOffset(
                            2026,
                            7,
                            5,
                            9,
                            0,
                            0,
                            TimeSpan.Zero),
                        TestData.Aggregate(100),
                        TestData.Aggregate(20),
                        TestData.Aggregate(10),
                        TestData.Aggregate(70),
                        TestData.Aggregate(0),
                        RequestCount: 2)
                    {
                        Pricing = new PricingAggregate(
                            0.50m,
                            CompleteRecords: 2,
                            PartialRecords: 0,
                            UnpricedRecords: 0,
                            PricingMissingCategory.None)
                    }
                ],
                [],
                [],
                [])));
        ProjectsViewModel viewModel = await host.InvokeAsync(() =>
            new ProjectsViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        DateTime start = new(2026, 7, 5, 9, 0, 0);
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedPeriod = ProjectsViewModel.Custom;
            viewModel.CommitCustomRangeCommand.Execute(
                new CustomTimeRange(start, start.AddHours(24)));
        });

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsNotNull(viewModel.Detail);
        Assert.AreEqual(
            TrendGranularity.Hour,
            viewModel.Detail.TrendGranularity);
        Assert.HasCount(24, viewModel.Detail.TrendPoints);
        UsageTrendPoint hourlyPoint = viewModel.Detail.TrendPoints[0];
        Assert.AreEqual(20, hourlyPoint.UncachedInput.Value);
        Assert.AreEqual(70, hourlyPoint.CacheRead.Value);
        Assert.AreEqual(10, hourlyPoint.Output.Value);
        Assert.AreEqual(2, hourlyPoint.RequestCount);
        Assert.AreEqual(0.50m, hourlyPoint.Pricing?.KnownAmountUsd);
        Assert.AreEqual("1", viewModel.Detail.ActiveDayCountText);
        StringAssert.Contains(viewModel.Detail.PeakDayText, "7月5日");
        UsageFilter hourlyFilter = queries.TrendFilters.Last();
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero),
            hourlyFilter.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero),
            hourlyFilter.EndExclusiveUtc);

        await host.InvokeAsync(() =>
            viewModel.CommitCustomRangeCommand.Execute(
                new CustomTimeRange(start, start.AddHours(25))));
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsNotNull(viewModel.Detail);
        Assert.AreEqual(
            TrendGranularity.Day,
            viewModel.Detail.TrendGranularity);
        Assert.HasCount(2, viewModel.Detail.TrendPoints);
        UsageTrendPoint dailyPoint = viewModel.Detail.TrendPoints[0];
        Assert.AreEqual(20, dailyPoint.UncachedInput.Value);
        Assert.AreEqual(70, dailyPoint.CacheRead.Value);
        Assert.AreEqual(10, dailyPoint.Output.Value);
        Assert.AreEqual(2, dailyPoint.RequestCount);
        Assert.AreEqual(0.50m, dailyPoint.Pricing?.KnownAmountUsd);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero),
            viewModel.Detail.TrendRangeStartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero),
            viewModel.Detail.TrendRangeEndExclusiveUtc);
        Assert.AreEqual(
            "1",
            viewModel.Detail.ActiveDayCountText,
            "累计活跃天数仍应来自独立的每日聚合。 ");
        StringAssert.Contains(viewModel.Detail.PeakDayText, "7月5日");
    }

    [TestMethod]
    public async Task ProjectList_SearchesSortsAndPreservesAggregatedRows()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            ProjectsResult =
            [
                Project(
                    ProjectAId,
                    @"C:\Projects\AgenTally",
                    NowUtc.AddDays(-2),
                    NowUtc,
                    total: 1_000,
                    rootSessions: 3),
                Project(
                    ProjectBId,
                    @"D:\Projects\sample\Beta",
                    NowUtc.AddDays(-3),
                    NowUtc.AddDays(-1),
                    total: 2_000,
                    rootSessions: 5)
            ]
        };
        var viewModel = await host.InvokeAsync(() => new ProjectsViewModel(
            queries,
            host.Dispatcher,
            new FixedTimeProvider(NowUtc),
            TimeZoneInfo.Utc));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("AgenTally", viewModel.Projects[0].NameText);
        await host.InvokeAsync(() => viewModel.SelectedSort = ProjectsViewModel.SortByTokens);
        Assert.AreEqual("Beta", viewModel.Projects[0].NameText);
        await host.InvokeAsync(() => viewModel.SelectedSort = ProjectsViewModel.SortByName);
        Assert.AreEqual("AgenTally", viewModel.Projects[0].NameText);
        await host.InvokeAsync(() => viewModel.SearchText = "sample");
        Assert.HasCount(1, viewModel.Projects);
        Assert.AreEqual(ProjectBId, viewModel.Projects[0].ProjectId);
        Assert.AreEqual(
            "5",
            viewModel.Projects[0].ActivityText.Split('·')[1]
                .Trim()
                .Split(' ')[0]);
    }

    [TestMethod]
    public async Task SwitchingProject_ShowsSharedLoadingFeedbackUntilDetailCompletes()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            ProjectsResult =
            [
                Project(
                    ProjectAId,
                    @"C:\Projects\AgenTally",
                    NowUtc.AddDays(-2),
                    NowUtc,
                    total: 1_000,
                    rootSessions: 3),
                Project(
                    ProjectBId,
                    @"D:\Projects\sample\Beta",
                    NowUtc.AddDays(-3),
                    NowUtc.AddDays(-1),
                    total: 2_000,
                    rootSessions: 5)
            ]
        };
        ProjectsViewModel viewModel = await host.InvokeAsync(() =>
            new ProjectsViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        var detailRelease = new TaskCompletionSource<DashboardQueryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queries.SetProjectRoute(ProjectBId, detailRelease.Task);
        await host.InvokeAsync(() =>
        {
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName ==
                        nameof(PageViewModel.IsRefreshFeedbackVisible) &&
                    !viewModel.IsRefreshFeedbackVisible)
                {
                    feedbackCompleted.TrySetResult();
                }
            };
            viewModel.SelectedProject = viewModel.Projects.Single(project =>
                project.ProjectId == ProjectBId);
        });

        await queries.WaitForProjectCallsAsync(ProjectBId, 2)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await host.InvokeAsync(() =>
            viewModel.IsRefreshFeedbackVisible));

        detailRelease.TrySetResult(TestData.Dashboard(2_000));
        await feedbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(viewModel.IsRefreshFeedbackVisible);
        Assert.AreEqual(ProjectBId, viewModel.Detail?.ProjectId);
    }

    [TestMethod]
    public async Task UnidentifiedProject_RemainsVisibleAndUsesExplicitQueryScope()
    {
        await using var host = new StaDispatcherTestHost();
        bool inspectedSessionFilter = false;
        var queries = new FakeUsageQueryService
        {
            ProjectsResult =
            [
                Project(
                    ProjectUsageRow.UnidentifiedProjectId,
                    null,
                    NowUtc.AddDays(-1),
                    NowUtc,
                    total: 50,
                    rootSessions: 0,
                    isUnidentified: true)
            ],
            RootSessionsHandler = request =>
            {
                inspectedSessionFilter = true;
                Assert.IsNull(request.Filter.ProjectId);
                Assert.IsTrue(request.Filter.UnidentifiedProjectOnly);
                return new RootSessionPage([], null);
            }
        };
        var viewModel = await host.InvokeAsync(() => new ProjectsViewModel(
            queries,
            host.Dispatcher,
            new FixedTimeProvider(NowUtc),
            TimeZoneInfo.Utc));

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.HasCount(1, viewModel.Projects);
        Assert.AreEqual("未识别项目", viewModel.Projects[0].NameText);
        Assert.AreEqual("工作目录无法可靠识别", viewModel.Projects[0].PathText);
        Assert.IsTrue(viewModel.Detail?.IsUnidentified);
        Assert.IsTrue(inspectedSessionFilter);
        Assert.IsTrue(queries.TrendFilters.Last().UnidentifiedProjectOnly);
        Assert.IsTrue(queries.AgentFilters.Last().UnidentifiedProjectOnly);
        Assert.IsTrue(queries.AgentModelFilters.Last().UnidentifiedProjectOnly);
    }

    [TestMethod]
    public async Task MissingDailyTokens_RemainUnknownInsteadOfBecomingZero()
    {
        await using var host = new StaDispatcherTestHost();
        var project = new ProjectUsageRow(
            ProjectAId,
            @"C:\Projects\AgenTally",
            PathAvailability.Available,
            NowUtc.AddDays(-1),
            NowUtc,
            1,
            0,
            TestData.MetricSet(null));
        var queries = new FakeUsageQueryService
        {
            ProjectsResult = [project]
        };
        queries.SetProjectRoute(
            ProjectAId,
            Task.FromResult(
                new DashboardQueryResult(
                    TestData.Dashboard(0).Overview,
                    [
                        new UsageTrendPoint(
                            NowUtc.AddDays(-1),
                            TestData.Aggregate(null),
                            TestData.Aggregate(null),
                            TestData.Aggregate(null),
                            TestData.Aggregate(null),
                            TestData.Aggregate(null))
                    ],
                    [],
                    [],
                    [])));
        var viewModel = await host.InvokeAsync(() => new ProjectsViewModel(
            queries,
            host.Dispatcher,
            new FixedTimeProvider(NowUtc),
            TimeZoneInfo.Utc));

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsNotNull(viewModel.Detail);
        Assert.AreEqual("—", viewModel.Detail.ActiveDayCountText);
        Assert.AreEqual("—", viewModel.Detail.ConsecutiveDayCountText);
        Assert.AreEqual("—", viewModel.Detail.PeakDayText);
        Assert.IsTrue(viewModel.Detail.TrendPoints.All(
            static point => point.NormalizedTotal.Value is null));
    }

    [TestMethod]
    public void UsageShare_UsesFilteredTotalAndPreservesMissingState()
    {
        var quarter = new UsageSharePresentation(
            "quarter",
            "quarter",
            250,
            1_000);
        var only = new UsageSharePresentation(
            "only",
            "only",
            1_000,
            1_000);
        var missing = new UsageSharePresentation(
            "missing",
            "missing",
            null,
            1_000);

        Assert.AreEqual(25d, quarter.ShareValue);
        Assert.AreEqual("25.0%", quarter.ShareText);
        Assert.AreEqual("250", quarter.TokensText);
        Assert.AreEqual(100d, only.ShareValue);
        Assert.AreEqual("100.0%", only.ShareText);
        Assert.AreEqual(0d, missing.ShareValue);
        Assert.AreEqual("—", missing.ShareText);
        Assert.AreEqual("—", missing.TokensText);
    }

    [TestMethod]
    public async Task SummaryNote_MergesMissingTokenAndPartialPriceCoverage()
    {
        await using var host = new StaDispatcherTestHost();
        MetricAggregate known = Known(1_000, 2);
        var unavailable = new MetricAggregate(null, 0, 2);
        var metrics = new UsageMetricSet(
            known,
            known,
            known,
            unavailable,
            known,
            known,
            known,
            known,
            known);
        var project = new ProjectUsageRow(
            ProjectAId,
            @"C:\Projects\AgenTally",
            PathAvailability.Available,
            NowUtc.AddDays(-2),
            NowUtc,
            2,
            1,
            metrics)
        {
            Pricing = new PricingAggregate(
                12.34m,
                CompleteRecords: 1,
                PartialRecords: 1,
                UnpricedRecords: 0,
                PricingMissingCategory.CacheWriteTokens)
        };
        var queries = new FakeUsageQueryService
        {
            ProjectsResult = [project]
        };
        var viewModel = await host.InvokeAsync(() => new ProjectsViewModel(
            queries,
            host.Dispatcher,
            new FixedTimeProvider(NowUtc),
            TimeZoneInfo.Utc));

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsNotNull(viewModel.Detail);
        Assert.AreEqual(
            "部分 Token 字段不可取得，价格仅统计可计价记录。",
            viewModel.Detail.DataNote);
        Assert.AreEqual(
            "部分计价 · 仅含已知金额",
            viewModel.Detail.PriceCaption,
            "共享价格语义保持不变；项目摘要只绑定合并后的 DataNote。");
    }

    [TestMethod]
    public void TokenBreakdown_UsesThreeUserFacingBillingCategories()
    {
        var metrics = new UsageMetricSet(
            Known(1_000, 1),
            Known(300, 1),
            Known(700, 1),
            Known(50, 1),
            Known(200, 1),
            Known(25, 1),
            Known(10, 1),
            Known(1_250, 1),
            Known(1_250, 1));

        ProjectMetricPresentation[] rows =
            ProjectValueFormatter.CreateMetricRows(metrics).ToArray();

        CollectionAssert.AreEqual(
            new[] { "缓存输入", "未缓存输入", "输出" },
            rows.Select(static row => row.Label).ToArray());
        CollectionAssert.AreEqual(
            new[] { "700", "300", "200" },
            rows.Select(static row => row.ValueText).ToArray());
    }

    private static ProjectUsageRow Project(
        string id,
        string? path,
        DateTimeOffset startedAtUtc,
        DateTimeOffset lastActivityUtc,
        long total,
        int rootSessions,
        bool isUnidentified = false) => new(
        id,
        path,
        path is null
            ? PathAvailability.Unavailable
            : PathAvailability.Available,
        startedAtUtc,
        lastActivityUtc,
        4,
        rootSessions,
        Metrics(total))
    {
        IsUnidentified = isUnidentified
    };

    private static AgentUsageRow Agent(
        string agent,
        long total,
        long requests) => new(
        agent,
        requests,
        Known(total),
        Known(total / 2, requests),
        Known(total / 4, requests),
        Known(total / 4, requests),
        Known(0, requests))
    {
        Metrics = Metrics(total),
        StartedAtUtc = NowUtc.AddDays(-2),
        LastActivityUtc = NowUtc
    };

    private static AgentModelUsageRow AgentModel(
        string agent,
        string model,
        long total,
        long requests) => new(
        agent,
        model,
        requests,
        Known(total),
        Known(total / 2, requests),
        Known(total / 4, requests),
        Known(total / 4, requests),
        Known(0, requests))
    {
        Metrics = Metrics(total),
        StartedAtUtc = NowUtc.AddDays(-2),
        LastActivityUtc = NowUtc
    };

    private static RootSessionSummaryRow RootSession(
        string rootSessionId,
        string projectId,
        long total) => new(
        new RootSessionIdentity(
            "codex",
            "codex:windows:test",
            rootSessionId),
        NowUtc.AddDays(-2),
        NowUtc,
        projectId,
        @"C:\Projects\AgenTally",
        PathAvailability.Available,
        2,
        0,
        Metrics(total));

    private static UsageTrendPoint Trend(
        int year,
        int month,
        int day,
        long total) => new(
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero),
        Known(total),
        Known(total),
        Known(0),
        Known(0),
        Known(0));

    private static UsageMetricSet Metrics(long total) => new(
        Known(total),
        Known(total / 2),
        Known(total / 4),
        Known(0),
        Known(total / 4),
        Known(0),
        Known(0),
        Known(total),
        Known(total));

    private static MetricAggregate Known(long value, long records = 1) =>
        new(value, checked((int)records), 0);
}
