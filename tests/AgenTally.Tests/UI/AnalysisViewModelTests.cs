using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class AnalysisViewModelTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Refresh_BuildsConfirmedSummaryAndThreeAnalysisViews()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                Overview(3, 300),
                [
                    Point(new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero), 100, 1, 20, 30, 50),
                    Point(new DateTimeOffset(2026, 7, 16, 2, 0, 0, TimeSpan.Zero), 200, 2, 50, 60, 100)
                ],
                [],
                [],
                [Agent("codex", 3, 300, 70, 90, 150)]),
            AgentModels =
            [
                new AgentModelUsageRow(
                    "codex",
                    "gpt-test",
                    3,
                    Aggregate(300, 3),
                    Aggregate(70, 3),
                    Aggregate(90, 3),
                    Aggregate(150, 3),
                    Aggregate(0, 3))
            ],
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
        };
        AnalysisViewModel viewModel = await host.InvokeAsync(() =>
            new AnalysisViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedPeriod = DashboardViewModel.Custom;
            viewModel.CustomStartDate = new DateTime(2026, 7, 15);
            viewModel.CustomEndDate = new DateTime(2026, 7, 17);
        });

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("300", viewModel.TotalTokensText);
        Assert.AreEqual("3", viewModel.RequestCountText);
        Assert.AreEqual("150", viewModel.DailyAverageText);
        Assert.AreEqual("—", viewModel.EquivalentValueText);
        Assert.AreEqual("计价信息不可取得", viewModel.EquivalentValueCaption);
        Assert.HasCount(2, viewModel.DailyRows);
        Assert.AreEqual(new DateOnly(2026, 7, 16), viewModel.DailyRows[0].Date);
        Assert.AreEqual("200", viewModel.DailyRows[0].TotalTokensText);
        Assert.AreEqual("66.7%", viewModel.DailyRows[0].CacheHitRateText);
        AnalysisAgentUsageRow agent = Assert.ContainsSingle(viewModel.AgentRows);
        Assert.AreEqual("codex", agent.AgentId);
        Assert.AreEqual("100.0%", agent.ShareText);
        AnalysisModelUsageRow model = Assert.ContainsSingle(viewModel.ModelRows);
        Assert.AreEqual("gpt-test", model.Model);
        Assert.AreEqual("codex", model.AgentId);
        Assert.AreEqual("100.0%", model.ShareText);
    }

    [TestMethod]
    public async Task AllTime_UsesTheFirstFilteredRecordThroughToday()
    {
        await using var host = new StaDispatcherTestHost();
        DateTimeOffset first =
            new(2026, 4, 10, 12, 0, 0, TimeSpan.Zero);
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                Overview(2, 300) with
                {
                    FirstOccurredAtUtc = first
                },
                [
                    Point(first, 100, 1, 20, 30, 50),
                    Point(
                        new DateTimeOffset(
                            2026,
                            7,
                            16,
                            2,
                            0,
                            0,
                            TimeSpan.Zero),
                        200,
                        1,
                        40,
                        60,
                        100)
                ],
                [],
                [],
                [])
        };
        AnalysisViewModel viewModel = await host.InvokeAsync(() =>
            new AnalysisViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        CollectionAssert.Contains(
            viewModel.PeriodOptions.ToArray(),
            DashboardViewModel.AllTime);
        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = DashboardViewModel.AllTime);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(
            "2026年4月10日至7月16日",
            viewModel.PeriodSummaryText);
        Assert.AreEqual("3", viewModel.DailyAverageText);
        Assert.HasCount(98, viewModel.DailyRows);
        Assert.AreEqual(
            new DateOnly(2026, 7, 16),
            viewModel.DailyRows[0].Date);
        Assert.AreEqual(
            new DateOnly(2026, 4, 10),
            viewModel.DailyRows[^1].Date);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch,
            queries.OverviewFilters.Last().StartInclusiveUtc);

        var empty = new MetricAggregate(null, 0, 0);
        queries.DashboardResult = new DashboardQueryResult(
            new UsageOverview(
                0,
                empty,
                empty,
                empty,
                empty,
                empty,
                null),
            [],
            [],
            [],
            []);
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(
            "截至 2026年7月16日",
            viewModel.PeriodSummaryText);
        Assert.IsEmpty(viewModel.DailyRows);
    }

    [TestMethod]
    public async Task RepeatedIdenticalRefresh_PreservesCollectionInstances()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                Overview(1, 100),
                [Point(NowUtc, 100, 1, 20, 30, 50)],
                [],
                [],
                [Agent("codex", 1, 100, 20, 30, 50)]),
            AgentModels =
            [
                new AgentModelUsageRow(
                    "codex",
                    "gpt-test",
                    1,
                    Aggregate(100, 1),
                    Aggregate(20, 1),
                    Aggregate(30, 1),
                    Aggregate(50, 1),
                    Aggregate(0, 1))
            ],
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
        };
        AnalysisViewModel viewModel = await host.InvokeAsync(() =>
            new AnalysisViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        object days = viewModel.DailyRows;
        object agents = viewModel.AgentRows;
        object models = viewModel.ModelRows;
        object agentOptions = viewModel.AgentOptions;
        object modelOptions = viewModel.ModelOptions;

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreSame(days, viewModel.DailyRows);
        Assert.AreSame(agents, viewModel.AgentRows);
        Assert.AreSame(models, viewModel.ModelRows);
        Assert.AreSame(agentOptions, viewModel.AgentOptions);
        Assert.AreSame(modelOptions, viewModel.ModelOptions);
    }

    [TestMethod]
    public async Task ProjectSelection_AppliesToRangeAndPinnedBreakdownQueries()
    {
        await using var host = new StaDispatcherTestHost();
        const string projectId = "0123456789abcdef01234567";
        var queries = new FakeUsageQueryService
        {
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
            {
                Projects =
                [
                    new ProjectFilterValue(
                        projectId,
                        @"D:\Repo",
                        PathAvailability.Available)
                ]
            }
        };
        AnalysisViewModel viewModel = await host.InvokeAsync(() =>
            new AnalysisViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedProject = projectId;
            viewModel.SelectDay(new UsageDaySelection(
                new DateOnly(2026, 7, 16),
                "codex",
                "gpt-test",
                ProjectId: projectId));
        });

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(projectId, viewModel.SelectedProject);
        Assert.IsTrue(queries.OverviewFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.TrendFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.AgentFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.AgentModelFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.FilterValueFilters.All(
            filter => filter.ProjectId == projectId));
    }

    [TestMethod]
    public async Task PartialPrice_ShowsKnownAmountWithCompactNeutralCopy()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = TestData.Dashboard(100) with
            {
                Overview = TestData.Dashboard(100).Overview with
                {
                    Pricing = new PricingAggregate(
                        3.21m,
                        1,
                        0,
                        1,
                        PricingMissingCategory.ModelRate)
                }
            }
        };
        AnalysisViewModel viewModel = await host.InvokeAsync(() =>
            new AnalysisViewModel(queries, host.Dispatcher));

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("$3.21", viewModel.EquivalentValueText);
        Assert.AreEqual(
            "部分计价 · 仅含已知金额",
            viewModel.EquivalentValueCaption);
    }

    [TestMethod]
    public async Task HeatmapSelection_PreservesRangeAndScopesBreakdownsUntilCleared()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                Overview(1, 100),
                [Point(new DateTimeOffset(2026, 7, 16, 1, 0, 0, TimeSpan.Zero), 100, 1, 20, 30, 50)],
                [],
                [],
                [Agent("codex", 1, 100, 20, 30, 50)]),
            AgentModels =
            [
                new AgentModelUsageRow(
                    "codex",
                    "gpt-test",
                    1,
                    Aggregate(100, 1),
                    Aggregate(20, 1),
                    Aggregate(30, 1),
                    Aggregate(50, 1),
                    Aggregate(0, 1))
            ],
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
        };
        AnalysisViewModel viewModel = await host.InvokeAsync(() =>
            new AnalysisViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));

        await host.InvokeAsync(() => viewModel.SelectDay(new UsageDaySelection(
            new DateOnly(2026, 7, 16),
            "codex",
            "gpt-test",
            DashboardViewModel.SevenDays)));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(DashboardViewModel.SevenDays, viewModel.SelectedPeriod);
        Assert.AreEqual("codex", viewModel.SelectedAgent);
        Assert.AreEqual("gpt-test", viewModel.SelectedModel);
        Assert.AreEqual(0, viewModel.SelectedViewIndex);
        Assert.IsTrue(viewModel.HasPinnedDate);
        Assert.AreEqual("已锁定 2026年7月16日", viewModel.PinnedDateText);
        Assert.AreEqual(new DateOnly(2026, 7, 16), viewModel.SelectedDailyRow?.Date);
        UsageFilter rangeFilter = queries.TrendFilters.Last();
        UsageFilter scopedFilter = queries.AgentFilters.Last();
        Assert.AreEqual(TimeSpan.FromDays(7), rangeFilter.EndExclusiveUtc - rangeFilter.StartInclusiveUtc);
        Assert.AreEqual(TimeSpan.FromDays(1), scopedFilter.EndExclusiveUtc - scopedFilter.StartInclusiveUtc);

        await host.InvokeAsync(() => viewModel.ClearPinnedDateCommand.Execute(null));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsFalse(viewModel.HasPinnedDate);
        Assert.AreEqual(TimeSpan.FromDays(7),
            queries.AgentFilters.Last().EndExclusiveUtc -
            queries.AgentFilters.Last().StartInclusiveUtc);
    }

    [TestMethod]
    public async Task CustomPinnedBoundaryDays_IntersectTheExactHourRange()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        AnalysisViewModel viewModel = await host.InvokeAsync(() =>
            new AnalysisViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TimeZoneInfo.Utc));
        DateTime start = new(2026, 7, 15, 9, 0, 0);
        DateTime end = new(2026, 7, 16, 10, 0, 0);

        await host.InvokeAsync(() => viewModel.SelectDay(new UsageDaySelection(
            new DateOnly(2026, 7, 15),
            null,
            null,
            DashboardViewModel.Custom,
            start,
            end)));
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        UsageFilter firstDay = queries.AgentFilters.Last();
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
            firstDay.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            firstDay.EndExclusiveUtc);

        await host.InvokeAsync(() => viewModel.SelectDay(new UsageDaySelection(
            new DateOnly(2026, 7, 16),
            null,
            null,
            DashboardViewModel.Custom,
            start,
            end)));
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        UsageFilter lastDay = queries.AgentFilters.Last();
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            lastDay.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero),
            lastDay.EndExclusiveUtc);
    }

    private static UsageOverview Overview(long requests, long total) => new(
        requests,
        Aggregate(total, checked((int)requests)),
        Aggregate(70, checked((int)requests)),
        Aggregate(90, checked((int)requests)),
        Aggregate(150, checked((int)requests)),
        Aggregate(0, checked((int)requests)),
        null);

    private static AgentUsageRow Agent(
        string agent,
        long requests,
        long total,
        long input,
        long output,
        long cacheRead) => new(
            agent,
            requests,
            Aggregate(total, checked((int)requests)),
            Aggregate(input, checked((int)requests)),
            Aggregate(output, checked((int)requests)),
            Aggregate(cacheRead, checked((int)requests)),
            Aggregate(0, checked((int)requests)));

    private static UsageTrendPoint Point(
        DateTimeOffset atUtc,
        long total,
        long requests,
        long input,
        long output,
        long cacheRead) => new(
            atUtc,
            Aggregate(total, checked((int)requests)),
            Aggregate(input, checked((int)requests)),
            Aggregate(output, checked((int)requests)),
            Aggregate(cacheRead, checked((int)requests)),
            Aggregate(0, checked((int)requests)),
            requests);

    private static MetricAggregate Aggregate(long value, int records) =>
        new(value, records, 0);
}
