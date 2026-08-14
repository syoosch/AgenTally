using System.IO;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class DashboardViewModelTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 7, 16, 18, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task QueryFailure_DoesNotExposeExceptionPathsOrMessages()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardException = new IOException(
                @"failed at C:\private\agentally.db")
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(
            "暂时无法读取 AgenTally 派生数据库；请检查磁盘和文件占用后重试。",
            viewModel.ErrorMessage);
        Assert.DoesNotContain("private", viewModel.ErrorMessage!);
    }

    [TestMethod]
    public async Task FirstFailedRefresh_LeavesRequestCountUnknown()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardException = new InvalidOperationException("读取失败")
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));

        Assert.AreEqual("—", viewModel.RequestCountText);

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("—", viewModel.RequestCountText);
        Assert.AreEqual("暂时无法读取本地统计，请重试。", viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task SuccessfulEmptyRefresh_ShowsExplicitZeroRequestCount()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                new UsageOverview(
                    0,
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    null),
                [],
                [],
                [],
                [])
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("0", viewModel.RequestCountText);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task Refresh_QueriesInParallelAndAppliesOneCompleteSnapshot()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = CreateDashboardResult(),
            FilterValues = new UsageFilterValues(
                ["claude", "codex"],
                ["claude-sonnet", "gpt-test"])
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));
        Task allQueriesStarted = queries.BlockDashboardQueriesAsync();

        Task refresh = host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        bool parallel = false;
        try
        {
            await allQueriesStarted.WaitAsync(TimeSpan.FromSeconds(2));
            parallel = true;
            Assert.IsTrue(await host.InvokeAsync(
                () => viewModel.IsRefreshFeedbackVisible));
        }
        catch (TimeoutException)
        {
            // The assertion below reports a clearer failure after releasing the queries.
        }
        finally
        {
            queries.ReleaseDashboardQueries();
        }

        await refresh;

        Assert.IsTrue(parallel, "六项概览查询应当在等待结果前全部启动。");
        Assert.AreEqual("1,200", viewModel.TotalTokensText);
        Assert.AreEqual("7", viewModel.RequestCountText);
        Assert.AreEqual("—", viewModel.UncachedInputText);
        Assert.AreEqual("0", viewModel.OutputText);
        Assert.AreEqual("90", viewModel.CacheReadText);
        Assert.AreEqual("—", viewModel.CacheWriteText);
        Assert.HasCount(30, viewModel.TrendPoints);
        Assert.IsNotEmpty(viewModel.HeatmapDays);
        Assert.HasCount(2, viewModel.ModelRows);
        Assert.HasCount(2, viewModel.AgentRows);
        CollectionAssert.AreEqual(
            new[] { "全部平台", "claude", "codex" },
            viewModel.AgentOptions.ToArray());
        CollectionAssert.AreEqual(
            new[] { "全部模型", "claude-sonnet", "gpt-test" },
            viewModel.ModelOptions.ToArray());
        Assert.IsFalse(viewModel.IsLoading);
        Assert.IsFalse(viewModel.IsRefreshFeedbackVisible);
        Assert.IsNull(viewModel.ErrorMessage);

        UsageFilter overviewFilter = Assert.ContainsSingle(queries.OverviewFilters);
        Assert.AreEqual(
            new DateTimeOffset(2026, 6, 17, 16, 0, 0, TimeSpan.Zero),
            overviewFilter.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 17, 16, 0, 0, TimeSpan.Zero),
            overviewFilter.EndExclusiveUtc);
        Assert.AreEqual(0, queries.RecentCalls);
        Assert.AreEqual(DashboardViewModel.ThirtyDays, viewModel.SelectedPeriod);
    }

    [TestMethod]
    public async Task Rankings_UseFilteredTotalAndTheSameTopFourRule()
    {
        await using var host = new StaDispatcherTestHost();
        var rows = new[]
        {
            ("first", 500L),
            ("second", 250L),
            ("third", 125L),
            ("fourth", 75L),
            ("fifth", 50L)
        };
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                TestData.Dashboard(1_000).Overview,
                [],
                [],
                rows.Select(row => Model(row.Item1, 1, row.Item2)).ToArray(),
                rows.Select(row => Agent(row.Item1, 1, row.Item2)).ToArray())
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.HasCount(4, viewModel.AgentRows);
        Assert.HasCount(4, viewModel.ModelRows);
        CollectionAssert.AreEqual(
            new[] { "50.0%", "25.0%", "12.5%", "7.5%" },
            viewModel.AgentRows.Select(static row => row.ShareText).ToArray());
        CollectionAssert.AreEqual(
            viewModel.AgentRows.Select(static row => row.ShareText).ToArray(),
            viewModel.ModelRows.Select(static row => row.ShareText).ToArray());
        Assert.IsFalse(viewModel.AgentRows.Any(static row => row.NameText == "fifth"));
        Assert.IsFalse(viewModel.ModelRows.Any(static row => row.NameText == "fifth"));
    }

    [TestMethod]
    public async Task ProjectSelection_AppliesToEveryDashboardQueryAndKeepsFullPath()
    {
        await using var host = new StaDispatcherTestHost();
        const string projectId = "0123456789abcdef01234567";
        const string projectPath = @"D:\Repo\frontend";
        var queries = new FakeUsageQueryService
        {
            DashboardResult = CreateDashboardResult(),
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
            {
                Projects =
                [
                    new ProjectFilterValue(
                        projectId,
                        projectPath,
                        PathAvailability.Available)
                ]
            }
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));
        await host.InvokeAsync(() => viewModel.SelectedProject = projectId);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(projectId, viewModel.SelectedProject);
        Assert.HasCount(2, viewModel.ProjectOptions);
        ProjectFilterOption option = viewModel.ProjectOptions[1];
        Assert.AreEqual(projectId, option.ProjectId);
        Assert.AreEqual(projectPath, option.DisplayText);
        Assert.AreEqual(projectPath, option.ToolTipText);
        Assert.IsTrue(queries.OverviewFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.TrendFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.ModelFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.AgentFilters.All(
            filter => filter.ProjectId == projectId));
        Assert.IsTrue(queries.FilterValueFilters.All(
            filter => filter.ProjectId == projectId));
    }

    [TestMethod]
    public async Task ProjectSelection_RemainsWhenCurrentRangeHasNoData()
    {
        await using var host = new StaDispatcherTestHost();
        const string projectId = "0123456789abcdef01234567";
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                new UsageOverview(
                    0,
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    TestData.Aggregate(null),
                    null)
                {
                    Pricing = new PricingAggregate(
                        null,
                        0,
                        0,
                        0,
                        PricingMissingCategory.None)
                },
                [],
                [],
                [],
                []),
            FilterValues = new UsageFilterValues([], [])
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
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));
        await host.InvokeAsync(() => viewModel.SelectedProject = projectId);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(projectId, viewModel.SelectedProject);
        Assert.AreEqual("—", viewModel.TotalTokensText);
        Assert.AreEqual("暂无数据", viewModel.EquivalentValueCaption);
    }

    [TestMethod]
    public async Task PricePresentation_DistinguishesCoverageAndTrueZero()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));
        (PricingAggregate? Pricing, string Value, string Caption)[] cases =
        [
            (new PricingAggregate(12.34m, 1, 0, 0, PricingMissingCategory.None),
                "$12.34", "完整计价 · 非实际账单"),
            (new PricingAggregate(
                    4.56m,
                    1,
                    0,
                    1,
                    PricingMissingCategory.ModelRate),
                "$4.56", "部分计价 · 仅含已知金额"),
            (new PricingAggregate(
                    null,
                    0,
                    1,
                    0,
                    PricingMissingCategory.CacheWriteTokens),
                "—", "部分计价 · 无已知金额"),
            (new PricingAggregate(
                    null,
                    0,
                    0,
                    1,
                    PricingMissingCategory.ModelRate),
                "未计价", "缺少适用价格"),
            (new PricingAggregate(
                    null,
                    0,
                    0,
                    0,
                    PricingMissingCategory.None),
                "—", "暂无数据"),
            (null, "—", "计价信息不可取得"),
            (new PricingAggregate(0m, 1, 0, 0, PricingMissingCategory.None),
                "$0.00", "完整计价 · 非实际账单"),
            (new PricingAggregate(0.0001m, 1, 0, 0, PricingMissingCategory.None),
                "$0.0001", "完整计价 · 非实际账单"),
        ];

        foreach ((PricingAggregate? pricing, string value, string caption) in cases)
        {
            queries.DashboardResult = TestData.Dashboard(1) with
            {
                Overview = TestData.Dashboard(1).Overview with
                {
                    Pricing = pricing
                }
            };

            await host.InvokeAsync(() =>
                viewModel.RefreshAsync(CancellationToken.None));

            Assert.AreEqual(value, viewModel.EquivalentValueText);
            Assert.AreEqual(caption, viewModel.EquivalentValueCaption);
        }
    }

    [TestMethod]
    public async Task RepeatedIdenticalRefresh_PreservesCollectionInstances()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = CreateDashboardResult(),
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        object trend = viewModel.TrendPoints;
        object heatmap = viewModel.HeatmapDays;
        object models = viewModel.ModelRows;
        object agents = viewModel.AgentRows;
        object agentOptions = viewModel.AgentOptions;
        object modelOptions = viewModel.ModelOptions;

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreSame(trend, viewModel.TrendPoints);
        Assert.AreSame(heatmap, viewModel.HeatmapDays);
        Assert.AreSame(models, viewModel.ModelRows);
        Assert.AreSame(agents, viewModel.AgentRows);
        Assert.AreSame(agentOptions, viewModel.AgentOptions);
        Assert.AreSame(modelOptions, viewModel.ModelOptions);
    }

    [TestMethod]
    public async Task Refresh_UsesSevenAndThirtyLocalCalendarDayBoundaries()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));

        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = DashboardViewModel.SevenDays);
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = DashboardViewModel.ThirtyDays);
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        UsageFilter[] filters = queries.OverviewFilters.ToArray();
        Assert.HasCount(2, filters);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 10, 16, 0, 0, TimeSpan.Zero),
            filters[0].StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 6, 17, 16, 0, 0, TimeSpan.Zero),
            filters[1].StartInclusiveUtc);
        Assert.AreEqual(filters[0].EndExclusiveUtc, filters[1].EndExclusiveUtc);
    }

    [TestMethod]
    public async Task Trend_CompletesTodaySevenAndThirtyDayRangesWithZeroBuckets()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));

        queries.DashboardResult = TestData.Dashboard(24) with
        {
            Trend =
            [
                TrendPoint(
                    new DateTimeOffset(
                        2026,
                        7,
                        16,
                        21,
                        0,
                        0,
                        TimeSpan.Zero),
                    24)
            ]
        };
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedPeriod = DashboardViewModel.Today;
            return viewModel.RefreshAsync(CancellationToken.None);
        });

        Assert.HasCount(24, viewModel.TrendPoints);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[0].BucketStartUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 17, 15, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[^1].BucketStartUtc);
        Assert.AreEqual(24, viewModel.TrendPoints[5].NormalizedTotal.Value);
        Assert.AreEqual(24, viewModel.TrendPoints[5].CacheRead.Value);
        Assert.AreEqual(24, viewModel.TrendPoints[5].UncachedInput.Value);
        Assert.AreEqual(24, viewModel.TrendPoints[5].Output.Value);
        Assert.AreEqual(1, viewModel.TrendPoints[5].RequestCount);
        Assert.AreEqual(0.24m, viewModel.TrendPoints[5].Pricing?.KnownAmountUsd);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendRangeStartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 17, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendRangeEndExclusiveUtc);
        Assert.AreEqual(0, viewModel.TrendPoints[4].NormalizedTotal.Value);
        Assert.AreEqual(0, viewModel.TrendPoints[6].Output.Value);

        queries.DashboardResult = TestData.Dashboard(70) with
        {
            Trend =
            [
                TrendPoint(
                    new DateTimeOffset(
                        2026,
                        7,
                        13,
                        16,
                        0,
                        0,
                        TimeSpan.Zero),
                    70)
            ]
        };
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedPeriod = DashboardViewModel.SevenDays;
            return viewModel.RefreshAsync(CancellationToken.None);
        });

        Assert.HasCount(7, viewModel.TrendPoints);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 10, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[0].BucketStartUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[^1].BucketStartUtc);
        Assert.AreEqual(70, viewModel.TrendPoints[3].NormalizedTotal.Value);
        Assert.IsTrue(viewModel.TrendPoints
            .Where((_, index) => index != 3)
            .All(static point =>
                point.NormalizedTotal.Value == 0 &&
                point.Output.Value == 0 &&
                point.RequestCount == 0));

        queries.DashboardResult = TestData.Dashboard(300) with
        {
            Trend =
            [
                TrendPoint(
                    new DateTimeOffset(
                        2026,
                        7,
                        1,
                        16,
                        0,
                        0,
                        TimeSpan.Zero),
                    300)
            ]
        };
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedPeriod = DashboardViewModel.ThirtyDays;
            return viewModel.RefreshAsync(CancellationToken.None);
        });

        Assert.HasCount(30, viewModel.TrendPoints);
        Assert.AreEqual(
            new DateTimeOffset(2026, 6, 17, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[0].BucketStartUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[^1].BucketStartUtc);
        Assert.AreEqual(300, viewModel.TrendPoints[14].NormalizedTotal.Value);
        Assert.AreEqual(0, viewModel.TrendPoints[0].NormalizedTotal.Value);
        Assert.AreEqual(0, viewModel.TrendPoints[^1].NormalizedTotal.Value);
    }

    [TestMethod]
    public async Task AllTime_UsesTheFirstFilteredRecordThroughToday()
    {
        await using var host = new StaDispatcherTestHost();
        DateTimeOffset first =
            new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);
        DashboardQueryResult result = TestData.Dashboard(300);
        var queries = new FakeUsageQueryService
        {
            DashboardResult = result with
            {
                Overview = result.Overview with
                {
                    FirstOccurredAtUtc = first
                },
                Trend = [TrendPoint(first, 300)]
            }
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));
        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = DashboardViewModel.AllTime);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(
            "2026年3月2日至7月17日",
            viewModel.PeriodSummaryText);
        Assert.AreEqual("每周 Token 总量与输出", viewModel.TrendSubtitle);
        Assert.HasCount(20, viewModel.TrendPoints);
        Assert.AreEqual(
            new DateTimeOffset(2026, 3, 1, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[0].BucketStartUtc);
        Assert.AreEqual(300, viewModel.TrendPoints[0].NormalizedTotal.Value);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 12, 16, 0, 0, TimeSpan.Zero),
            viewModel.TrendPoints[^1].BucketStartUtc);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch,
            queries.OverviewFilters.Last().StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 17, 16, 0, 0, TimeSpan.Zero),
            queries.OverviewFilters.Last().EndExclusiveUtc);

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
            "截至 2026年7月17日",
            viewModel.PeriodSummaryText);
        Assert.IsEmpty(viewModel.TrendPoints);
    }

    [TestMethod]
    public async Task ConfirmedPeriods_DefaultToThirtyDaysAndLongCustomUsesWeeks()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = CreateDashboardResult()
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));

        CollectionAssert.AreEqual(
            new[]
            {
                DashboardViewModel.AllTime,
                DashboardViewModel.Today,
                DashboardViewModel.SevenDays,
                DashboardViewModel.ThirtyDays,
                DashboardViewModel.NinetyDays,
                DashboardViewModel.Custom
            },
            viewModel.PeriodOptions.ToArray());
        Assert.AreEqual(DashboardViewModel.ThirtyDays, viewModel.SelectedPeriod);

        await host.InvokeAsync(() =>
        {
            viewModel.CustomStartDate = new DateTime(2026, 1, 1);
            viewModel.CustomEndDate = new DateTime(2026, 7, 17);
            viewModel.SelectedPeriod = DashboardViewModel.Custom;
        });
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("每周 Token 总量与输出", viewModel.TrendSubtitle);
        UsageFilter filter = Assert.ContainsSingle(queries.OverviewFilters);
        Assert.AreEqual(
            new DateTimeOffset(2025, 12, 31, 16, 0, 0, TimeSpan.Zero),
            filter.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero),
            filter.EndExclusiveUtc);
    }

    [TestMethod]
    public async Task CustomTrend_UsesExactHourDayAndWeekThresholds()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = CreateDashboardResult()
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));
        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = DashboardViewModel.Custom);

        async Task AssertRangeAsync(
            DateTime start,
            DateTime end,
            TrendGranularity expectedGranularity,
            int expectedPointCount)
        {
            await host.InvokeAsync(() =>
                viewModel.CommitCustomRangeCommand.Execute(
                    new CustomTimeRange(start, end)));
            await host.InvokeAsync(() =>
                viewModel.RefreshAsync(CancellationToken.None));
            Assert.AreEqual(expectedGranularity, viewModel.TrendGranularity);
            Assert.HasCount(expectedPointCount, viewModel.TrendPoints);
        }

        DateTime start = new(2026, 1, 1, 0, 0, 0);
        await AssertRangeAsync(
            start,
            start.AddHours(72),
            TrendGranularity.Hour,
            72);
        await AssertRangeAsync(
            start,
            start.AddHours(73),
            TrendGranularity.Day,
            4);
        await AssertRangeAsync(
            start,
            start.AddDays(90),
            TrendGranularity.Day,
            90);
        await AssertRangeAsync(
            start,
            start.AddDays(90).AddHours(1),
            TrendGranularity.Week,
            14);
    }

    [TestMethod]
    public async Task CustomSelection_CancelsDraftAndCommitsOnlyCompleteRange()
    {
        await using var host = new StaDispatcherTestHost();
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                new FakeUsageQueryService(),
                host.Dispatcher,
                new FixedTimeProvider(NowUtc),
                TestTimeZone()));
        int filterChanges = 0;
        viewModel.FilterChanged += (_, _) => filterChanges++;

        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = DashboardViewModel.Custom);
        Assert.IsTrue(viewModel.IsCustomRangeSelectionPending);
        Assert.AreEqual(0, filterChanges);

        await host.InvokeAsync(() =>
            viewModel.CancelCustomRangeCommand.Execute(null));
        Assert.AreEqual(DashboardViewModel.ThirtyDays, viewModel.SelectedPeriod);
        Assert.IsFalse(viewModel.IsCustomRangeSelectionPending);
        Assert.AreEqual(0, filterChanges);

        await host.InvokeAsync(() =>
        {
            viewModel.SelectedPeriod = DashboardViewModel.Custom;
            viewModel.CommitCustomRangeCommand.Execute(new CustomTimeRange(
                new DateTime(2026, 8, 1, 9, 0, 0),
                new DateTime(2026, 8, 1, 10, 0, 0)));
        });
        Assert.IsFalse(viewModel.IsCustomRangeSelectionPending);
        Assert.AreEqual(1, filterChanges);
        Assert.AreEqual(
            new DateTime(2026, 8, 1, 9, 0, 0),
            viewModel.CustomStartDate);
        Assert.AreEqual(
            new DateTime(2026, 8, 1, 10, 0, 0),
            viewModel.CustomEndDate);
    }

    [TestMethod]
    public async Task Refresh_UsesOneNowSnapshotForEveryQueryAcrossLocalMidnight()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var time = new SequenceTimeProvider(
            new DateTimeOffset(2026, 7, 16, 15, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 16, 16, 0, 1, TimeSpan.Zero));
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(
                queries,
                host.Dispatcher,
                time,
                TestTimeZone()));

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(1, time.CallCount);
        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.AreEqual(2, queries.TrendCalls);
        Assert.AreEqual(0, queries.RecentCalls);
        Assert.AreEqual(1, queries.ModelCalls);
        Assert.AreEqual(1, queries.AgentCalls);
        Assert.AreEqual(1, queries.FilterValueCalls);
        UsageFilter[] filters =
        [
            Assert.ContainsSingle(queries.OverviewFilters),
            queries.TrendFilters.First(),
            Assert.ContainsSingle(queries.ModelFilters),
            Assert.ContainsSingle(queries.AgentFilters),
            Assert.ContainsSingle(queries.FilterValueFilters)
        ];
        DateTimeOffset expectedStart =
            new(2026, 6, 16, 16, 0, 0, TimeSpan.Zero);
        DateTimeOffset expectedEnd =
            new(2026, 7, 16, 16, 0, 0, TimeSpan.Zero);
        Assert.IsTrue(filters.All(filter =>
            filter.StartInclusiveUtc == expectedStart &&
            filter.EndExclusiveUtc == expectedEnd));
        Assert.AreEqual(200, filters[0].Limit);
        Assert.AreEqual(200, filters[1].Limit);
        Assert.AreEqual(200, filters[2].Limit);
        Assert.AreEqual(200, filters[3].Limit);
    }

    [TestMethod]
    public async Task FailedRefresh_PreservesPreviouslyDisplayedSnapshot()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = CreateDashboardResult()
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        queries.DashboardException = new InvalidOperationException("读取失败");

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("1,200", viewModel.TotalTokensText);
        Assert.HasCount(30, viewModel.TrendPoints);
        Assert.IsNotEmpty(viewModel.HeatmapDays);
        Assert.AreEqual("暂时无法读取本地统计，请重试。", viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.IsLoading);
    }

    [TestMethod]
    public async Task LateOlderRefresh_CannotOverwriteNewerResultOrState()
    {
        await using var host = new StaDispatcherTestHost();
        var oldResult = new TaskCompletionSource<DashboardQueryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new FakeUsageQueryService
        {
            FilterValues = new UsageFilterValues(["new", "old"], [])
        };
        queries.SetRoute("old", oldResult.Task);
        queries.SetRoute("new", Task.FromResult(TestData.Dashboard(222)));
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));
        await host.InvokeAsync(() => viewModel.SelectedAgent = "old");

        Task oldRefresh = host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        await queries.WaitForAgentCallsAsync("old", 5)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await host.InvokeAsync(() => viewModel.SelectedAgent = "new");
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        oldResult.SetResult(TestData.Dashboard(111));
        await oldRefresh;

        Assert.AreEqual("222", viewModel.TotalTokensText);
        Assert.IsNull(viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.IsLoading);
    }

    [TestMethod]
    public async Task CompletedRefresh_DoesNotRevertAFilterChangedWhileQueryWasRunning()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            FilterValues = new UsageFilterValues(["new", "old"], [])
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));
        await host.InvokeAsync(() => viewModel.SelectedAgent = "old");
        Task allQueriesStarted = queries.BlockDashboardQueriesAsync();

        Task refresh = host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        await allQueriesStarted.WaitAsync(TimeSpan.FromSeconds(2));
        await host.InvokeAsync(() => viewModel.SelectedAgent = "new");
        queries.ReleaseDashboardQueries();
        await refresh;

        Assert.AreEqual("new", viewModel.SelectedAgent);
    }

    [TestMethod]
    public async Task CacheHitRate_RequiresCompleteCoverageAndPositiveDenominator()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = new DashboardQueryResult(
                new UsageOverview(
                    2,
                    TestData.Aggregate(400),
                    new MetricAggregate(100, 2, 0),
                    TestData.Aggregate(0),
                    new MetricAggregate(300, 2, 0),
                    TestData.Aggregate(0),
                    NowUtc),
                [],
                [],
                [],
                [])
        };
        DashboardViewModel viewModel = await host.InvokeAsync(() =>
            new DashboardViewModel(queries, host.Dispatcher));

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        Assert.AreEqual("75.0%", viewModel.CacheHitRateText);

        queries.DashboardResult = queries.DashboardResult with
        {
            Overview = queries.DashboardResult.Overview with
            {
                CacheRead = new MetricAggregate(300, 1, 1)
            }
        };
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        Assert.AreEqual("—", viewModel.CacheHitRateText);

        queries.DashboardResult = queries.DashboardResult with
        {
            Overview = queries.DashboardResult.Overview with
            {
                UncachedInput = new MetricAggregate(0, 2, 0),
                CacheRead = new MetricAggregate(0, 2, 0)
            }
        };
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        Assert.AreEqual("—", viewModel.CacheHitRateText);
    }

    private static DashboardQueryResult CreateDashboardResult() => new(
        new UsageOverview(
            7,
            TestData.Aggregate(1200),
            TestData.Aggregate(null),
            TestData.Aggregate(0),
            TestData.Aggregate(90),
            TestData.Aggregate(null),
            NowUtc),
        [
            TrendPoint(NowUtc.AddHours(-3), 5),
            TrendPoint(NowUtc.AddHours(-2), 8),
            TrendPoint(NowUtc.AddHours(-1), 10),
            TrendPoint(NowUtc, 20)
        ],
        [
            Record("event-2", NowUtc),
            Record("event-1", NowUtc.AddMinutes(-1))
        ],
        [
            Model("gpt-test", 4, 800),
            Model("claude-sonnet", 3, 400)
        ],
        [
            Agent("codex", 5, 900),
            Agent("claude", 2, 300)
        ]);

    private static UsageTrendPoint TrendPoint(DateTimeOffset atUtc, long value) =>
        new(
            atUtc,
            TestData.Aggregate(value),
            TestData.Aggregate(value),
            TestData.Aggregate(value),
            TestData.Aggregate(value),
            TestData.Aggregate(value),
            RequestCount: 1)
        {
            Pricing = new PricingAggregate(
                value / 100m,
                CompleteRecords: 1,
                PartialRecords: 0,
                UnpricedRecords: 0,
                PricingMissingCategory.None)
        };

    private static UsageRecordRow Record(string eventId, DateTimeOffset atUtc) => new(
        "fixture:instance",
        "fixture:entity",
        eventId,
        atUtc,
        "codex",
        "gpt-test",
        10,
        5,
        5,
        0,
        0,
        CompletionState.Finalized,
        DataQuality.Exact);

    private static ModelUsageRow Model(string model, long requests, long total) => new(
        model,
        requests,
        TestData.Aggregate(total),
        TestData.Aggregate(total),
        TestData.Aggregate(total),
        TestData.Aggregate(total),
        TestData.Aggregate(total));

    private static AgentUsageRow Agent(string agent, long requests, long total) => new(
        agent,
        requests,
        TestData.Aggregate(total));

    private static TimeZoneInfo TestTimeZone() => TimeZoneInfo.CreateCustomTimeZone(
        "AgenTally.Tests.UTC+08",
        TimeSpan.FromHours(8),
        "AgenTally Tests UTC+08",
        "AgenTally Tests UTC+08");
}
