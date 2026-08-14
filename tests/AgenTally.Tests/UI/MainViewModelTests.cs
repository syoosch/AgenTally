using System.IO;
using System.ComponentModel;
using System.Windows.Input;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;
using AgenTally.UI.Runtime;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class MainViewModelTests
{
    [TestMethod]
    public async Task Navigation_LoadsFirstVisitAndReusesSuccessfulPageUntilDataChanges()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            Sources = [Source("codex")]
        };
        var timer = new FakeRefreshTimer();
        var changes = new FakeUsageDataChangeMonitor
        {
            State = UsageDataChangeState.Unchanged
        };
        (MainViewModel main, DashboardViewModel dashboard, SourcesViewModel sources,
            SettingsViewModel settings) = await CreateMainAsync(
                host,
                queries,
                timer,
                changes: changes);

        await host.InvokeAsync(main.StartAsync);

        CollectionAssert.AreEqual(
            new[] { "概览", "分析", "项目", "会话", "数据来源", "设置" },
            main.Pages.Select(page => page.Title).ToArray());
        Assert.AreSame(dashboard, main.CurrentPage);
        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(3), timer.Interval);

        await timer.TriggerAsync();
        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.AreEqual(0, queries.SourceCalls);

        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(sources));
        Assert.AreSame(sources, main.CurrentPage);
        Assert.AreEqual(1, queries.SourceCalls);

        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(dashboard));
        Assert.AreSame(dashboard, main.CurrentPage);
        Assert.AreEqual(1, queries.OverviewCalls);

        await host.InvokeAsync(() => dashboard.RefreshCommand.ExecuteAsync());
        Assert.AreEqual(2, queries.OverviewCalls);

        changes.State = UsageDataChangeState.Changed;
        await timer.TriggerAsync();
        changes.State = UsageDataChangeState.Unchanged;
        Assert.AreEqual(3, queries.OverviewCalls);

        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(sources));
        Assert.AreSame(sources, main.CurrentPage);
        Assert.AreEqual(2, queries.SourceCalls);

        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(dashboard));
        Assert.AreSame(dashboard, main.CurrentPage);
        Assert.AreEqual(3, queries.OverviewCalls);

        await host.InvokeAsync(() => settings.RefreshIntervalSeconds = 5);
        Assert.AreEqual(TimeSpan.FromSeconds(5), timer.Interval);

        await host.InvokeAsync(() =>
            settings.OpenSettingsSectionCommand.Execute(
                SettingsSection.Privacy));
        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(settings));
        Assert.AreSame(settings, main.CurrentPage);
        Assert.IsTrue(settings.IsSettingsHome);
        await host.InvokeAsync(() =>
            settings.OpenSettingsSectionCommand.Execute(
                SettingsSection.Pricing));
        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(dashboard));
        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(settings));
        Assert.IsTrue(
            settings.IsSettingsHome,
            "从全局导航重新进入设置时应回到五类首页。局部编辑状态由同一 ViewModel 保留。");

        await host.InvokeAsync(main.Stop);
        await host.InvokeAsync(main.Stop);
        await timer.TriggerAsync();
        Assert.IsFalse(timer.IsEnabled);
        Assert.AreEqual(2, queries.SourceCalls);
    }

    [TestMethod]
    public async Task Navigation_RetriesAPageWhoseFirstRefreshFailed()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            SourcesException = new InvalidOperationException("来源读取失败")
        };
        var timer = new FakeRefreshTimer();
        var changes = new FakeUsageDataChangeMonitor
        {
            State = UsageDataChangeState.Unchanged
        };
        (MainViewModel main, DashboardViewModel dashboard, SourcesViewModel sources, _) =
            await CreateMainAsync(host, queries, timer, changes: changes);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(sources));
        Assert.AreEqual(1, queries.SourceCalls);
        Assert.IsNotNull(sources.ErrorMessage);

        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(dashboard));
        queries.SourcesException = null;
        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(sources));

        Assert.AreEqual(2, queries.SourceCalls);
        Assert.IsNull(sources.ErrorMessage);
        await host.InvokeAsync(main.Dispose);
    }

    [TestMethod]
    public async Task Navigation_LatestClickWinsWhilePreviousPageIsLoading()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        (MainViewModel main, _, SourcesViewModel sources, _) =
            await CreateMainAsync(host, queries, timer);
        await host.InvokeAsync(main.StartAsync);
        Task sourceStarted = queries.BlockSourcesAsync();

        Task firstNavigation = host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(sources));
        await sourceStarted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreSame(sources, main.CurrentPage);
        Assert.IsTrue(sources.IsLoading);
        Assert.IsTrue(
            main.NavigateCommand.CanExecute(main.Projects),
            "页面加载期间侧栏的其他导航按钮必须继续接受点击。");

        Task latestNavigation = host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(main.Projects));
        try
        {
            await latestNavigation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreSame(
                main.Projects,
                main.CurrentPage,
                "加载期间后点的页面必须立即成为真实当前页面。");
            Assert.IsFalse(
                sources.IsLoading,
                "切离正在加载的页面时应立即取消其刷新反馈。");
        }
        finally
        {
            queries.ReleaseSources();
        }

        await firstNavigation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreSame(
            main.Projects,
            main.CurrentPage,
            "较早页面的刷新结束后不得覆盖最后一次导航选择。");
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task TimerTick_QueriesOnlyAfterTheDatabaseChanges()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var changes = new FakeUsageDataChangeMonitor
        {
            State = UsageDataChangeState.Unchanged
        };
        (MainViewModel main, _, _, _) =
            await CreateMainAsync(host, queries, timer, changes: changes);
        await host.InvokeAsync(main.StartAsync);

        await timer.TriggerAsync();
        await timer.TriggerAsync();

        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.AreEqual(3, changes.ObserveCalls);

        changes.State = UsageDataChangeState.Changed;
        await timer.TriggerAsync();
        changes.State = UsageDataChangeState.Unchanged;
        await timer.TriggerAsync();

        Assert.AreEqual(2, queries.OverviewCalls);
        Assert.AreEqual(5, changes.ObserveCalls);
        await host.InvokeAsync(main.Dispose);
        Assert.AreEqual(1, changes.DisposeCalls);
    }

    [TestMethod]
    public async Task TimerTick_UnavailableChangeMonitorPreservesTheCurrentSnapshot()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = TestData.Dashboard(123)
        };
        var timer = new FakeRefreshTimer();
        var changes = new FakeUsageDataChangeMonitor
        {
            State = UsageDataChangeState.Unavailable
        };
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(host, queries, timer, changes: changes);
        await host.InvokeAsync(main.StartAsync);

        await timer.TriggerAsync();

        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.AreEqual("123", dashboard.TotalTokensText);
        Assert.IsNull(dashboard.ErrorMessage);
        await host.InvokeAsync(main.Dispose);
    }

    [TestMethod]
    public async Task TimerTick_RefreshesAtTheLocalDateBoundaryWithoutDataChanges()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var changes = new FakeUsageDataChangeMonitor
        {
            State = UsageDataChangeState.Unchanged
        };
        var time = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        (MainViewModel main, _, _, _) = await CreateMainAsync(
            host,
            queries,
            timer,
            changes: changes,
            timeProvider: time,
            localTimeZone: TimeZoneInfo.Utc);
        await host.InvokeAsync(main.StartAsync);
        await timer.TriggerAsync();
        Assert.AreEqual(1, queries.OverviewCalls);

        time.UtcNow = new DateTimeOffset(2026, 7, 21, 0, 0, 1, TimeSpan.Zero);
        await timer.TriggerAsync();

        Assert.AreEqual(2, queries.OverviewCalls);
        await host.InvokeAsync(main.Dispose);
    }

    [TestMethod]
    public async Task TimerTick_SkipsReentryWhileCurrentPageIsRefreshing()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(host, queries, timer);
        await host.InvokeAsync(main.StartAsync);
        Task allQueriesStarted = queries.BlockDashboardQueriesAsync();

        Task firstTick = timer.TriggerAsync();
        await allQueriesStarted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(await host.InvokeAsync(
            () => dashboard.IsRefreshFeedbackVisible));
        await timer.TriggerAsync();

        Assert.AreEqual(2, queries.OverviewCalls);
        Assert.AreEqual(2, queries.FilterValueCalls);
        queries.ReleaseDashboardQueries();
        await firstTick;
        Assert.IsNull(timer.LastException);
    }

    [TestMethod]
    public async Task ActiveDashboardFilterChange_CancelsOldQueryAndAutomaticallyRefreshesLatest()
    {
        await using var host = new StaDispatcherTestHost();
        var oldResult = new TaskCompletionSource<DashboardQueryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new FakeUsageQueryService
        {
            DashboardResult = TestData.Dashboard(10),
            FilterValues = new UsageFilterValues(["new", "old"], [])
        };
        queries.SetRoute("old", oldResult.Task);
        queries.SetRoute("new", Task.FromResult(TestData.Dashboard(222)));
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(host, queries, timer);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() => dashboard.SelectedAgent = "old");
        await queries.WaitForAgentCallsAsync("old", 5)
            .WaitAsync(TimeSpan.FromSeconds(2));
        CancellationToken[] oldTokens = queries.DashboardCancellationTokens
            .Where(item => item.AgentId == "old")
            .Select(item => item.Token)
            .ToArray();
        Assert.HasCount(5, oldTokens);
        var latestApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? changed = null;
        changed = (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(DashboardViewModel.TotalTokensText) &&
                dashboard.TotalTokensText == "222")
            {
                latestApplied.TrySetResult();
            }
        };
        await host.InvokeAsync(() => dashboard.PropertyChanged += changed);

        await host.InvokeAsync(() => dashboard.SelectedAgent = "new");
        await latestApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(oldTokens.All(token => token.IsCancellationRequested));
        Assert.AreEqual("222", dashboard.TotalTokensText);
        oldResult.SetResult(TestData.Dashboard(111));
        await queries.WaitForAgentCompletionsAsync("old", 5)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        await host.InvokeAsync(() => { });
        Assert.AreEqual("222", dashboard.TotalTokensText);
        await host.InvokeAsync(() => dashboard.PropertyChanged -= changed);
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task DashboardFilterChange_WhenNotActiveOrNotStarted_DoesNotQuery()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            FilterValues = new UsageFilterValues(["inactive", "unstarted"], [])
        };
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, SourcesViewModel sources, _) =
            await CreateMainAsync(host, queries, timer);

        await host.InvokeAsync(() => dashboard.SelectedAgent = "unstarted");
        Assert.AreEqual(0, queries.OverviewCalls);
        await host.InvokeAsync(() => dashboard.SelectedAgent = DashboardViewModel.AllAgents);
        await host.InvokeAsync(main.StartAsync);
        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(sources));
        int dashboardCalls = queries.OverviewCalls;

        await host.InvokeAsync(() => dashboard.SelectedAgent = "inactive");
        await host.InvokeAsync(() => { });
        Assert.AreEqual(dashboardCalls, queries.OverviewCalls);

        await host.InvokeAsync(main.Stop);
        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(dashboard));
        await host.InvokeAsync(() => dashboard.SelectedAgent = "unstarted");
        Assert.AreEqual(dashboardCalls, queries.OverviewCalls);
    }

    [TestMethod]
    public async Task HeatmapDayClick_NavigatesToAnalysisWithTheSelectedContext()
    {
        await using var host = new StaDispatcherTestHost();
        const string projectId = "0123456789abcdef01234567";
        var queries = new FakeUsageQueryService
        {
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
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(host, queries, timer);
        await host.InvokeAsync(() => dashboard.SelectedProject = projectId);
        await host.InvokeAsync(main.StartAsync);
        UsageHeatmapDay selected = dashboard.HeatmapDays[^1];

        await host.InvokeAsync(() => dashboard.OpenDayCommand.Execute(selected));
        await host.InvokeAsync(() => { });

        Assert.AreSame(main.Analysis, main.CurrentPage);
        Assert.AreEqual(
            selected.Date.ToString("yyyy年M月d日"),
            main.Analysis.SelectionText);
        Assert.AreEqual(
            DashboardViewModel.ThirtyDays,
            main.Analysis.SelectedPeriod);
        Assert.AreEqual(projectId, main.Analysis.SelectedProject);
        Assert.IsTrue(main.Analysis.HasPinnedDate);
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task StatisticsFilters_SynchronizeAcrossAllFourPages()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
            {
                Projects =
                [
                    new ProjectFilterValue(
                        "0123456789abcdef01234567",
                        @"D:\Repo",
                        PathAvailability.Available)
                ]
            }
        };
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(
                host,
                queries,
                timer,
                timeProvider: new FixedTimeProvider(
                    new DateTimeOffset(
                        2026,
                        7,
                        16,
                        12,
                        0,
                        0,
                        TimeSpan.Zero)),
                localTimeZone: TimeZoneInfo.Utc);
        Assert.AreEqual(
            DashboardViewModel.ThirtyDays,
            main.Analysis.SelectedPeriod);
        Assert.AreEqual(
            ProjectsViewModel.ThirtyDays,
            main.Projects.SelectedPeriod,
            "项目页初始时间应与概览的共享默认值一致。");
        Assert.AreEqual(
            SessionsViewModel.ThirtyDays,
            main.Sessions.SelectedPeriod,
            "会话页初始时间应与概览的共享默认值一致。");
        await host.InvokeAsync(main.StartAsync);
        await host.InvokeAsync(() => main.NavigateCommand.ExecuteAsync(main.Analysis));

        await host.InvokeAsync(() =>
        {
            main.Analysis.SelectedAgent = "codex";
            main.Analysis.SelectedModel = "gpt-test";
            main.Analysis.SelectedProject = "0123456789abcdef01234567";
            main.Analysis.SelectedPeriod = DashboardViewModel.Custom;
            main.Analysis.CustomStartDate = new DateTime(2026, 7, 1, 9, 0, 0);
            main.Analysis.CustomEndDate = new DateTime(2026, 7, 16, 18, 0, 0);
        });
        await host.InvokeAsync(() => { });

        Assert.AreEqual("codex", dashboard.SelectedAgent);
        Assert.AreEqual("gpt-test", dashboard.SelectedModel);
        Assert.AreEqual(
            "0123456789abcdef01234567",
            dashboard.SelectedProject);
        Assert.AreEqual(DashboardViewModel.Custom, dashboard.SelectedPeriod);
        Assert.AreEqual(
            new DateTime(2026, 7, 1, 9, 0, 0),
            dashboard.CustomStartDate);
        Assert.AreEqual(
            new DateTime(2026, 7, 16, 18, 0, 0),
            dashboard.CustomEndDate);
        Assert.AreEqual("codex", main.Projects.SelectedAgent);
        Assert.AreEqual("gpt-test", main.Projects.SelectedModel);
        Assert.AreEqual(ProjectsViewModel.Custom, main.Projects.SelectedPeriod);
        Assert.AreEqual(
            new DateTime(2026, 7, 1, 9, 0, 0),
            main.Projects.CustomStartDate);
        Assert.AreEqual(
            new DateTime(2026, 7, 16, 18, 0, 0),
            main.Projects.CustomEndDate);
        Assert.AreEqual("codex", main.Sessions.SelectedAgent);
        Assert.AreEqual("gpt-test", main.Sessions.SelectedModel);
        Assert.AreEqual(
            "0123456789abcdef01234567",
            main.Sessions.SelectedProject);
        Assert.AreEqual(SessionsViewModel.Custom, main.Sessions.SelectedPeriod);
        Assert.AreEqual(
            new DateTime(2026, 7, 1, 9, 0, 0),
            main.Sessions.CustomStartDate);
        Assert.AreEqual(
            new DateTime(2026, 7, 16, 18, 0, 0),
            main.Sessions.CustomEndDate);
        Assert.AreEqual(
            0,
            queries.ProjectCalls,
            "隐藏的项目页只应失效，不应被预加载。");
        Assert.AreEqual(
            0,
            queries.RootSessionCalls,
            "隐藏的会话页只应失效，不应被预加载。");

        await host.InvokeAsync(() =>
        {
            main.Sessions.SelectedProject = SessionsViewModel.AllProjects;
        });
        Assert.AreEqual(
            DashboardViewModel.AllProjects,
            dashboard.SelectedProject,
            "会话页项目筛选应同步回其他统计页。");
        await host.InvokeAsync(() =>
        {
            main.Sessions.SelectedProject = "0123456789abcdef01234567";
        });
        Assert.AreEqual(
            "0123456789abcdef01234567",
            dashboard.SelectedProject);

        await host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(main.Dashboard));
        UsageFilter dashboardFilter = queries.OverviewFilters.Last();
        Assert.AreEqual("codex", dashboardFilter.AgentId);
        Assert.AreEqual("gpt-test", dashboardFilter.NormalizedModel);
        Assert.AreEqual(
            "0123456789abcdef01234567",
            dashboardFilter.ProjectId);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            dashboardFilter.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 18, 0, 0, TimeSpan.Zero),
            dashboardFilter.EndExclusiveUtc);

        await host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(main.Sessions));
        UsageFilter sessionsFilter =
            queries.RootSessionRequests.Last().Filter;
        Assert.AreEqual("codex", sessionsFilter.AgentId);
        Assert.AreEqual("gpt-test", sessionsFilter.NormalizedModel);
        Assert.AreEqual(
            "0123456789abcdef01234567",
            sessionsFilter.ProjectId);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            sessionsFilter.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 18, 0, 0, TimeSpan.Zero),
            sessionsFilter.EndExclusiveUtc);

        await host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(main.Projects));
        UsageFilter projectsFilter = queries.ProjectFilters.Last();
        Assert.AreEqual("codex", projectsFilter.AgentId);
        Assert.AreEqual("gpt-test", projectsFilter.NormalizedModel);
        Assert.IsNull(projectsFilter.ProjectId);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            projectsFilter.StartInclusiveUtc);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 16, 18, 0, 0, TimeSpan.Zero),
            projectsFilter.EndExclusiveUtc);

        await host.InvokeAsync(() =>
        {
            main.Projects.SelectedAgent = ProjectsViewModel.AllAgents;
            main.Projects.SelectedModel = ProjectsViewModel.AllModels;
            main.Projects.SelectedPeriod = ProjectsViewModel.AllTime;
        });
        Assert.AreEqual(DashboardViewModel.AllAgents, dashboard.SelectedAgent);
        Assert.AreEqual(DashboardViewModel.AllModels, dashboard.SelectedModel);
        Assert.AreEqual(DashboardViewModel.AllTime, dashboard.SelectedPeriod);
        Assert.AreEqual(
            "0123456789abcdef01234567",
            dashboard.SelectedProject,
            "项目页没有项目筛选，不应清除概览/分析共享的项目范围。");
        Assert.AreEqual(
            DashboardViewModel.AllTime,
            main.Analysis.SelectedPeriod);
        Assert.AreEqual(
            "0123456789abcdef01234567",
            main.Analysis.SelectedProject);
        await host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(main.Analysis));
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch,
            queries.OverviewFilters.Last().StartInclusiveUtc,
            "从项目页选择全部时间后，分析页应使用同一全历史查询范围。");
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task ProjectAndSessionLinks_SelectTheSameRecordsInBothDirections()
    {
        await using var host = new StaDispatcherTestHost();
        const string projectId = "0123456789abcdef01234567";
        const string rootSessionId = "root-linked";
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var project = new ProjectUsageRow(
            projectId,
            @"C:\Projects\AgenTally",
            PathAvailability.Available,
            now.AddDays(-2),
            now,
            4,
            1,
            TestData.MetricSet(1_000));
        var summary = new RootSessionSummaryRow(
            new RootSessionIdentity(
                "codex",
                "codex:windows:test",
                rootSessionId),
            now.AddDays(-2),
            now,
            projectId,
            project.ProjectPath,
            PathAvailability.Available,
            4,
            0,
            TestData.MetricSet(1_000));
        var queries = new FakeUsageQueryService
        {
            ProjectsResult = [project],
            RootSessionsHandler = request =>
                request.Filter.ProjectId == projectId
                    ? new RootSessionPage([summary], null)
                    : new RootSessionPage([], null),
            RootSessionDetailHandler = (_, requestedIdentity) =>
                requestedIdentity == summary.Identity
                    ? new RootSessionDetail(
                        summary,
                        [
                            new SessionContributionRow(
                                rootSessionId,
                                null,
                                SessionKind.Primary,
                                0,
                                4,
                                TestData.MetricSet(1_000),
                                [])
                        ])
                    : null
        };
        queries.SetProjectRoute(
            projectId,
            Task.FromResult(TestData.Dashboard(1_000)));
        var timer = new FakeRefreshTimer();
        (MainViewModel main, _, _, _) = await CreateMainAsync(
            host,
            queries,
            timer,
            timeProvider: new FixedTimeProvider(now),
            localTimeZone: TimeZoneInfo.Utc);
        await host.InvokeAsync(main.StartAsync);
        await host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(main.Projects));
        Assert.AreEqual(projectId, main.Projects.SelectedProject?.ProjectId);

        await host.InvokeAsync(() =>
        {
            main.Projects.SelectedDetailTabIndex = 3;
            main.Projects.OpenSessionCommand.Execute(summary.Identity);
        });
        await WaitUntilAsync(
            host,
            () => ReferenceEquals(main.CurrentPage, main.Sessions) &&
                main.Sessions.SelectedSession?.RootSessionId == rootSessionId &&
                main.Sessions.Detail is not null);

        Assert.AreSame(main.Sessions, main.CurrentPage);
        Assert.AreEqual(rootSessionId, main.Sessions.SelectedSession?.RootSessionId);
        Assert.AreEqual(projectId, main.Sessions.Detail?.ProjectId);
        Assert.AreEqual("AgenTally", main.Sessions.Detail?.ProjectNameText);
        Assert.AreEqual(0, main.Sessions.SelectedDetailTabIndex);
        Assert.AreEqual(
            "2026年7月1日至7月30日",
            main.Sessions.PeriodSummaryText,
            "从项目直达会话时也应先加载会话页筛选头与日期范围。");

        await host.InvokeAsync(() =>
        {
            main.Projects.SelectedDetailTabIndex = 2;
            main.Sessions.OpenProjectCommand.Execute(projectId);
        });
        await WaitUntilAsync(
            host,
            () => ReferenceEquals(main.CurrentPage, main.Projects) &&
                main.Projects.SelectedProject?.ProjectId == projectId &&
                main.Projects.Detail is not null);

        Assert.AreSame(main.Projects, main.CurrentPage);
        Assert.AreEqual(projectId, main.Projects.SelectedProject?.ProjectId);
        Assert.AreEqual(0, main.Projects.SelectedDetailTabIndex);
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task RemovedFilterOption_RefreshesTheActiveDashboardWithAllFilter()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            DashboardResult = TestData.Dashboard(222),
            FilterValues = new UsageFilterValues([], [])
        };
        queries.SetRoute("removed", Task.FromResult(TestData.Dashboard(111)));
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(host, queries, timer);
        await host.InvokeAsync(() => dashboard.SelectedAgent = "removed");

        await host.InvokeAsync(main.StartAsync);

        Assert.AreEqual(DashboardViewModel.AllAgents, dashboard.SelectedAgent);
        Assert.AreEqual("222", dashboard.TotalTokensText);
        Assert.AreEqual(2, queries.OverviewCalls);
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task ActiveProjectFilterChange_CancelsOldQueryAndAppliesLatestProject()
    {
        await using var host = new StaDispatcherTestHost();
        const string oldProject = "0123456789abcdef01234567";
        const string newProject = "89abcdef0123456701234567";
        var oldResult = new TaskCompletionSource<DashboardQueryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new FakeUsageQueryService
        {
            DashboardResult = TestData.Dashboard(10),
            FilterValues = new UsageFilterValues([], [])
            {
                Projects =
                [
                    new ProjectFilterValue(
                        oldProject,
                        @"D:\Old",
                        PathAvailability.Available),
                    new ProjectFilterValue(
                        newProject,
                        @"D:\New",
                        PathAvailability.Available)
                ]
            }
        };
        queries.SetProjectRoute(oldProject, oldResult.Task);
        queries.SetProjectRoute(
            newProject,
            Task.FromResult(TestData.Dashboard(222)));
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(host, queries, timer);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() => dashboard.SelectedProject = oldProject);
        await queries.WaitForProjectCallsAsync(oldProject, 5)
            .WaitAsync(TimeSpan.FromSeconds(2));
        CancellationToken[] oldTokens = queries.ProjectCancellationTokens
            .Where(item => item.ProjectId == oldProject)
            .Select(item => item.Token)
            .ToArray();
        Assert.HasCount(5, oldTokens);
        var latestApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? changed = null;
        changed = (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(DashboardViewModel.TotalTokensText) &&
                dashboard.TotalTokensText == "222")
            {
                latestApplied.TrySetResult();
            }
        };
        await host.InvokeAsync(() => dashboard.PropertyChanged += changed);

        await host.InvokeAsync(() => dashboard.SelectedProject = newProject);
        await latestApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(oldTokens.All(token => token.IsCancellationRequested));
        Assert.AreEqual(newProject, dashboard.SelectedProject);
        Assert.AreEqual("222", dashboard.TotalTokensText);
        oldResult.SetResult(TestData.Dashboard(111));
        await queries.WaitForProjectCompletionsAsync(oldProject, 5)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        await host.InvokeAsync(() => { });
        Assert.AreEqual("222", dashboard.TotalTokensText);
        await host.InvokeAsync(() => dashboard.PropertyChanged -= changed);
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task RemovedProjectOption_RefreshesTheActiveDashboardWithAllProjects()
    {
        await using var host = new StaDispatcherTestHost();
        const string removedProject = "0123456789abcdef01234567";
        var queries = new FakeUsageQueryService
        {
            DashboardResult = TestData.Dashboard(222),
            FilterValues = new UsageFilterValues([], [])
        };
        queries.SetProjectRoute(
            removedProject,
            Task.FromResult(TestData.Dashboard(111)));
        var timer = new FakeRefreshTimer();
        (MainViewModel main, DashboardViewModel dashboard, _, _) =
            await CreateMainAsync(host, queries, timer);
        await host.InvokeAsync(() =>
            dashboard.SelectedProject = removedProject);

        await host.InvokeAsync(main.StartAsync);

        Assert.AreEqual(
            DashboardViewModel.AllProjects,
            dashboard.SelectedProject);
        Assert.AreEqual("222", dashboard.TotalTokensText);
        Assert.AreEqual(2, queries.OverviewCalls);
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task TimerTick_SkipsBlockedNavigationRefreshOnActivePage()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var changes = new FakeUsageDataChangeMonitor();
        (MainViewModel main, _, SourcesViewModel sources, _) =
            await CreateMainAsync(host, queries, timer, changes: changes);
        await host.InvokeAsync(main.StartAsync);
        Task sourceStarted = queries.BlockSourcesAsync();

        Task navigation = host.InvokeAsync(() =>
            main.NavigateCommand.ExecuteAsync(sources));
        await sourceStarted.WaitAsync(TimeSpan.FromSeconds(2));
        Task tick = timer.TriggerAsync();
        bool tickSkipped = false;
        try
        {
            await tick.WaitAsync(TimeSpan.FromSeconds(2));
            tickSkipped = true;
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            queries.ReleaseSources();
        }

        await navigation;
        await tick;
        Assert.IsTrue(tickSkipped, "活动页面正在刷新时，计时器应立即跳过。");
        Assert.AreEqual(1, queries.SourceCalls);
        Assert.AreEqual(
            1,
            changes.ObserveCalls,
            "页面加载期间不应消费数据库变化版本。");
        await host.InvokeAsync(main.Stop);
    }

    [TestMethod]
    public async Task SourcesFailure_PreservesPreviouslyDisplayedRows()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            Sources = [Source("codex")]
        };
        SourcesViewModel viewModel = await host.InvokeAsync(() =>
            new SourcesViewModel(queries, host.Dispatcher));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        queries.SourcesException = new InvalidOperationException("来源读取失败");

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        SourceStatusRow row = Assert.ContainsSingle(viewModel.Sources);
        Assert.AreEqual("codex", row.AgentId);
        Assert.AreEqual("暂时无法读取本地统计，请重试。", viewModel.ErrorMessage);
        Assert.IsFalse(viewModel.IsLoading);
    }

    [TestMethod]
    public async Task RepeatedIdenticalSourceRefresh_PreservesCollectionInstances()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService
        {
            Sources = [Source("codex")]
        };
        SourcesViewModel viewModel = await host.InvokeAsync(() =>
            new SourcesViewModel(queries, host.Dispatcher));
        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        object sources = viewModel.Sources;
        object rows = viewModel.SourceRows;

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreSame(sources, viewModel.Sources);
        Assert.AreSame(rows, viewModel.SourceRows);
    }

    [TestMethod]
    public void SourceStatusPresentation_SeparatesCollectionAndCompatibilityState()
    {
        SourceStatusPresentation normal = new(Source("normal"));
        SourceStatusPresentation failedButCompatible = new(
            Source("failed") with { LastError = "parse failed" });
        SourceStatusPresentation waitingAndPartial = new(
            Source("waiting") with
            {
                LastSuccessAtUtc = null,
                CompatibilityLevel = CompatibilityLevel.PartiallyCompatible
            });

        Assert.AreEqual("正常", normal.CollectionStatusText);
        Assert.AreEqual("完全兼容", normal.CompatibilityText);
        Assert.AreEqual(
            "核心与分类指标可正常统计",
            normal.CompatibilityDescription);
        Assert.AreEqual("异常", failedButCompatible.CollectionStatusText);
        Assert.AreEqual("完全兼容", failedButCompatible.CompatibilityText);
        Assert.AreEqual(
            "等待首次读取",
            waitingAndPartial.CollectionStatusText);
        Assert.AreEqual("部分兼容", waitingAndPartial.CompatibilityText);
        Assert.AreEqual(
            "部分指标不可取得，可靠指标继续统计",
            waitingAndPartial.CompatibilityDescription);
    }

    [TestMethod]
    public void SourceStatusPresentation_MapsEveryCompatibilityStateAndKnownCode()
    {
        (CompatibilityLevel Level, string Text, string Description)[] cases =
        [
            (
                CompatibilityLevel.FullyCompatible,
                "完全兼容",
                "核心与分类指标可正常统计"),
            (
                CompatibilityLevel.PartiallyCompatible,
                "部分兼容",
                "部分指标不可取得，可靠指标继续统计"),
            (
                CompatibilityLevel.TemporarilyIncompatible,
                "暂不兼容",
                "核心 Token 语义无法确认，已暂停对应统计"),
            (
                CompatibilityLevel.MissingCapability,
                "能力不可用",
                "来源未提供所需能力，相关指标不可取得"),
            (
                (CompatibilityLevel)99,
                "状态未知",
                "兼容状态未知，未作推断")
        ];

        foreach ((CompatibilityLevel level, string text, string description)
            in cases)
        {
            SourceStatusPresentation presentation = new(
                Source("compatibility") with { CompatibilityLevel = level });
            Assert.AreEqual(text, presentation.CompatibilityText);
            Assert.AreEqual(description, presentation.CompatibilityDescription);
        }

        SourceStatusPresentation needsRescan = new(
            Source("rescan") with
            {
                CompatibilityLevel = CompatibilityLevel.TemporarilyIncompatible,
                CompatibilityCode = "parser_rescan_required",
                RequiresRescan = true
            });
        SourceStatusPresentation partialMetadata = new(
            Source("metadata") with
            {
                CompatibilityLevel = CompatibilityLevel.PartiallyCompatible,
                CompatibilityCode = "session_metadata_partial"
            });

        Assert.AreEqual(
            "统计数据需要更新；完成前保留现有数据",
            needsRescan.CompatibilityDescription);
        Assert.AreEqual(
            "部分会话或项目信息不可取得",
            partialMetadata.CompatibilityDescription);
        Assert.IsFalse(
            needsRescan.CompatibilityDescription.Contains(
                "parser_rescan_required",
                StringComparison.Ordinal));
        Assert.IsFalse(
            partialMetadata.CompatibilityDescription.Contains(
                "session_metadata_partial",
                StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Settings_ExposeOnlySupportedIntervalsAndReadOnlyDatabasePath()
    {
        await using var host = new StaDispatcherTestHost();
        string relativePath = Path.Combine("data", "agentally.db");
        SettingsViewModel viewModel = await host.InvokeAsync(() =>
            new SettingsViewModel(host.Dispatcher, relativePath));

        CollectionAssert.AreEqual(
            new[] { 2, 3, 5, 10, 30 },
            viewModel.RefreshIntervalOptions.ToArray());
        Assert.AreEqual(3, viewModel.RefreshIntervalSeconds);
        Assert.AreEqual(Path.GetFullPath(relativePath), viewModel.DatabasePath);
        Assert.AreEqual("当前诊断界面不联网。", viewModel.NetworkAccessDescription);
        Assert.AreEqual("不修改 Agent 配置", viewModel.AgentConfigurationDescription);
        Assert.AreEqual("仅在界面打开时检查本地数据变化", viewModel.RefreshDescription);
        await host.InvokeAsync(() =>
        {
            Assert.HasCount(0, viewModel.PriceModels);
            Assert.IsFalse(viewModel.CanEditSelectedPrice);
            Assert.IsFalse(viewModel.SavePriceCommand.CanExecute(null));
        });
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await host.InvokeAsync(() => viewModel.RefreshIntervalSeconds = 4));
    }

    [TestMethod]
    public async Task AsyncCommand_ReportsFailureAndPreventsReentry()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            calls++;
            await release.Task;
            throw new InvalidOperationException("command failure");
        });

        Task first = command.ExecuteAsync();
        Task second = command.ExecuteAsync();
        Assert.AreEqual(1, calls);
        Assert.IsTrue(command.IsExecuting);
        release.SetResult();

        InvalidOperationException failure =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await first);
        await second;
        Assert.AreEqual("command failure", failure.Message);
        Assert.AreSame(failure, command.LastException);
        Assert.IsFalse(command.IsExecuting);
    }

    [TestMethod]
    public async Task AsyncCommand_CanKeepAcceptingLatestConcurrentExecution()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var command = new AsyncRelayCommand(
            async _ =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await release.Task;
            },
            allowsConcurrentExecutions: true);

        Task first = command.ExecuteAsync("first");
        Task second = command.ExecuteAsync("latest");
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, calls);
        Assert.IsTrue(command.IsExecuting);
        Assert.IsTrue(command.CanExecute("another"));

        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.IsFalse(command.IsExecuting);
    }

    [TestMethod]
    public async Task ICommandExecute_ObservesFailureThroughCommandState()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async () =>
        {
            await release.Task;
            throw new InvalidOperationException("observed command failure");
        });
        ICommand commandInterface = command;

        commandInterface.Execute(null);
        Task execution = command.ExecutionTask ??
            throw new AssertFailedException("ICommand.Execute 应公开当前执行任务。");
        release.SetResult();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await execution);

        Assert.AreEqual("observed command failure", command.LastException?.Message);
        Assert.IsFalse(command.IsExecuting);
    }

    [TestMethod]
    public async Task MainLifecycle_MarshalsRealDispatcherTimerStopAndDispose()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        MainViewModel main = await host.InvokeAsync(() =>
        {
            var dashboard = new DashboardViewModel(queries, host.Dispatcher);
            var sources = new SourcesViewModel(queries, host.Dispatcher);
            var settings = new SettingsViewModel(
                host.Dispatcher,
                Path.Combine("data", "agentally.db"));
            return new MainViewModel(
                dashboard,
                new AnalysisViewModel(queries, host.Dispatcher),
                new ProjectsViewModel(queries, host.Dispatcher),
                new SessionsViewModel(queries, host.Dispatcher),
                sources,
                settings,
                host.Dispatcher,
                new FakeUsageDataChangeMonitor());
        });
        await host.InvokeAsync(main.StartAsync);

        await Task.Run(main.Stop);
        await Task.Run(main.Dispose);

        Assert.IsFalse(main.Dashboard.IsLoading);
    }

    [TestMethod]
    public async Task MainLifecycle_OrdinaryDisposeNeverRequestsFullShutdown()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(ReadyStatus());
        (MainViewModel main, _, _, _) =
            await CreateMainAsync(host, queries, timer, runtime);

        await host.InvokeAsync(main.StartAsync);
        await host.InvokeAsync(main.Dispose);

        Assert.AreEqual(1, runtime.EnsureCalls);
    }

    [TestMethod]
    public async Task ClearStatisticsCommand_RejectionDoesNotCallRuntimeOrRefresh()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(ReadyStatus());
        var confirmation = new FakeClearStatisticsConfirmation(false);
        (MainViewModel main, _, _, _) = await CreateMainAsync(
            host,
            queries,
            timer,
            runtime,
            clearStatisticsConfirmation: confirmation);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() =>
            main.ClearStatisticsCommand.ExecuteAsync());

        Assert.AreEqual(1, confirmation.Calls);
        Assert.AreEqual(0, runtime.ClearStatisticsCalls);
        Assert.AreEqual(1, queries.OverviewCalls);
    }

    [TestMethod]
    public async Task ClearStatisticsCommand_ConfirmationClearsAndRefreshesCurrentPage()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(ReadyStatus());
        var confirmation = new FakeClearStatisticsConfirmation(true);
        (MainViewModel main, _, _, _) = await CreateMainAsync(
            host,
            queries,
            timer,
            runtime,
            clearStatisticsConfirmation: confirmation);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() =>
            main.ClearStatisticsCommand.ExecuteAsync());

        Assert.AreEqual(1, confirmation.Calls);
        Assert.AreEqual(1, runtime.ClearStatisticsCalls);
        Assert.AreEqual(2, queries.OverviewCalls);
        Assert.AreEqual(CoreRuntimeUiState.Ready, runtime.Current.State);
        Assert.IsFalse(main.IsCoreStatusVisible);
        Assert.IsTrue(main.CoreStatusCanClear);
    }

    [TestMethod]
    public async Task CreateBackupCommand_ReportsSuccessAndRecordsBackupTime()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(ReadyStatus());
        var interaction = new FakeDataBackupInteraction
        {
            SavePath = Path.GetFullPath("manual.agentally-backup")
        };
        (MainViewModel main, _, _, SettingsViewModel settings) =
            await CreateMainAsync(
                host,
                queries,
                timer,
                runtime,
                dataBackupInteraction: interaction);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() => main.CreateBackupCommand.ExecuteAsync());

        Assert.AreEqual(1, runtime.CreateBackupCalls);
        Assert.AreEqual(interaction.SavePath, runtime.LastBackupPath);
        Assert.AreNotEqual("尚未备份", settings.LastBackupText);
        Assert.AreEqual("备份已创建并通过完整性校验。", main.DataMaintenanceStatusText);
        Assert.IsFalse(main.DataMaintenanceStatusIsError);
        Assert.IsFalse(main.IsDataMaintenanceRunning);
    }

    [TestMethod]
    public async Task CancelBackupCommand_CancelsSingleRunningMaintenance()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(ReadyStatus());
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.BackupHandler = async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadyStatus();
        };
        var interaction = new FakeDataBackupInteraction
        {
            SavePath = Path.GetFullPath("cancel.agentally-backup")
        };
        (MainViewModel main, _, _, _) = await CreateMainAsync(
            host,
            queries,
            timer,
            runtime,
            dataBackupInteraction: interaction);
        await host.InvokeAsync(main.StartAsync);

        Task running = host.InvokeAsync(() =>
            main.CreateBackupCommand.ExecuteAsync());
        await started.Task;
        await host.InvokeAsync(() =>
        {
            Assert.IsTrue(main.IsDataMaintenanceRunning);
            Assert.IsTrue(main.CanCancelBackup);
            Assert.IsFalse(main.RestoreBackupCommand.CanExecute(null));
            main.CancelBackupCommand.Execute(null);
        });
        await running;

        Assert.AreEqual(1, runtime.CreateBackupCalls);
        Assert.AreEqual("备份已取消，源数据未改变。", main.DataMaintenanceStatusText);
        Assert.IsFalse(main.IsDataMaintenanceRunning);
    }

    [TestMethod]
    public async Task RestoreBackupCommand_PausesQueriesResetsMonitorAndRefreshes()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(ReadyStatus());
        var changes = new FakeUsageDataChangeMonitor();
        var queryGate = new FakeUsageQueryMaintenanceGate();
        var interaction = new FakeDataBackupInteraction
        {
            RestorePath = Path.GetFullPath("restore.agentally-backup"),
            ConfirmRestoreResult = true
        };
        (MainViewModel main, _, _, _) = await CreateMainAsync(
            host,
            queries,
            timer,
            runtime,
            changes,
            dataBackupInteraction: interaction,
            queryMaintenanceGate: queryGate);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() => main.RestoreBackupCommand.ExecuteAsync());

        Assert.AreEqual(1, runtime.RestoreBackupCalls);
        Assert.AreEqual(interaction.RestorePath, runtime.LastBackupPath);
        Assert.AreEqual(1, changes.ResetCalls);
        Assert.AreEqual(1, queryGate.PauseCalls);
        Assert.AreEqual(1, queryGate.ReleaseCalls);
        Assert.IsTrue(timer.IsEnabled);
        Assert.AreEqual("备份已恢复，当前数据已重新加载。", main.DataMaintenanceStatusText);
        Assert.AreEqual(2, queries.OverviewCalls);
    }

    [TestMethod]
    public async Task RetryCoreCommand_ReEnsuresBeforeRefreshingCurrentPage()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(
            new CoreRuntimeUiStatus(
                CoreRuntimeUiState.MissingCore,
                "后台组件缺失",
                true,
                true),
            ReadyStatus());
        (MainViewModel main, _, _, _) =
            await CreateMainAsync(host, queries, timer, runtime);
        await host.InvokeAsync(main.StartAsync);
        Assert.AreEqual(0, queries.OverviewCalls);
        Assert.IsTrue(main.IsCoreStatusVisible);
        Assert.IsTrue(main.CoreStatusIsError);

        await host.InvokeAsync(() => main.RetryCoreCommand.ExecuteAsync());

        Assert.AreEqual(2, runtime.EnsureCalls);
        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.AreEqual("后台采集正在运行。", main.CoreStatusText);
        Assert.IsFalse(main.IsCoreStatusVisible);
        Assert.IsFalse(main.CoreStatusIsError);
    }

    [TestMethod]
    public async Task ParserRescanRequiredStillLoadsExistingReadOnlyData()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(
            new CoreRuntimeUiStatus(
                CoreRuntimeUiState.ParserRebuildRequired,
                "需要重扫 AgenTally 的 Codex 派生数据",
                true,
                false));
        (MainViewModel main, _, _, _) =
            await CreateMainAsync(host, queries, timer, runtime);

        await host.InvokeAsync(main.StartAsync);

        Assert.AreEqual(1, runtime.EnsureCalls);
        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.IsTrue(main.IsCoreStatusVisible);
        Assert.IsTrue(main.CoreStatusIsError);
        Assert.IsTrue(main.CoreStatusCanRebuild);
    }

    [TestMethod]
    public async Task AutomaticStatisticsUpdateKeepsExistingDataReadable()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(
            new CoreRuntimeUiStatus(
                CoreRuntimeUiState.UpdatingStatistics,
                "正在更新统计数据",
                false,
                false));
        (MainViewModel main, _, _, _) =
            await CreateMainAsync(host, queries, timer, runtime);

        await host.InvokeAsync(main.StartAsync);

        Assert.AreEqual(1, runtime.EnsureCalls);
        Assert.AreEqual(1, queries.OverviewCalls);
        Assert.IsTrue(main.IsCoreStatusVisible);
        Assert.IsFalse(main.CoreStatusIsError);
        Assert.IsFalse(main.CoreStatusCanRebuild);
    }

    [TestMethod]
    public async Task SourceFailureAction_NavigatesToPersistedSourceStatus()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        var timer = new FakeRefreshTimer();
        var runtime = new FakeCoreRuntimeController(
            new CoreRuntimeUiStatus(
                CoreRuntimeUiState.SourceUnavailable,
                "Codex 本地来源不可用",
                true,
                true));
        (MainViewModel main, _, SourcesViewModel sources, _) =
            await CreateMainAsync(host, queries, timer, runtime);
        await host.InvokeAsync(main.StartAsync);

        await host.InvokeAsync(() =>
            main.NavigateToSourcesCommand.ExecuteAsync());

        Assert.AreSame(sources, main.CurrentPage);
        Assert.AreEqual(1, queries.SourceCalls);
    }

    private static async Task<(
        MainViewModel Main,
        DashboardViewModel Dashboard,
        SourcesViewModel Sources,
        SettingsViewModel Settings)> CreateMainAsync(
        StaDispatcherTestHost host,
        FakeUsageQueryService queries,
        IRefreshTimer timer,
        ICoreRuntimeController? runtime = null,
        IUsageDataChangeMonitor? changes = null,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null,
        IClearStatisticsConfirmation? clearStatisticsConfirmation = null,
        IDataBackupInteraction? dataBackupInteraction = null,
        IUsageQueryMaintenanceGate? queryMaintenanceGate = null) =>
        await host.InvokeAsync(() =>
    {
        var dashboard = new DashboardViewModel(
            queries,
            host.Dispatcher,
            timeProvider,
            localTimeZone);
        var sources = new SourcesViewModel(queries, host.Dispatcher);
        var settings = new SettingsViewModel(
            host.Dispatcher,
            Path.Combine("data", "agentally.db"));
        var main = new MainViewModel(
            dashboard,
            new AnalysisViewModel(
                queries,
                host.Dispatcher,
                timeProvider,
                localTimeZone),
            new ProjectsViewModel(
                queries,
                host.Dispatcher,
                timeProvider,
                localTimeZone),
            new SessionsViewModel(
                queries,
                host.Dispatcher,
                timeProvider,
                localTimeZone),
            sources,
            settings,
            host.Dispatcher,
            changes ?? new FakeUsageDataChangeMonitor(),
            timer,
            runtime,
            timeProvider: timeProvider,
            localTimeZone: localTimeZone,
            clearStatisticsConfirmation: clearStatisticsConfirmation,
            dataBackupInteraction: dataBackupInteraction,
            queryMaintenanceGate: queryMaintenanceGate);
        return (main, dashboard, sources, settings);
    });

    private static CoreRuntimeUiStatus ReadyStatus() => new(
        CoreRuntimeUiState.Ready,
        "后台采集正在运行。",
        false,
        false);

    private static SourceStatusRow Source(string agentId) => new(
        $"{agentId}:instance",
        $"{agentId}:entity",
        agentId,
        SourceKind.Jsonl,
        agentId,
        "C:\\fixtures",
        $"C:\\fixtures\\{agentId}.jsonl",
        $"{agentId}-v1",
        DateTimeOffset.UnixEpoch,
        null,
        null);

    private static async Task WaitUntilAsync(
        StaDispatcherTestHost host,
        Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await host.InvokeAsync(condition))
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("异步页面跳转未在预期时间内完成。");
    }

    private sealed class FakeRefreshTimer : IRefreshTimer
    {
        private Func<Task>? _tick;

        public TimeSpan Interval { get; set; }

        public bool IsEnabled { get; private set; }

        public Task? LastTickTask { get; private set; }

        public Exception? LastException { get; private set; }

        public void Start(Func<Task> tick)
        {
            _tick = tick;
            IsEnabled = true;
        }

        public void Stop() => IsEnabled = false;

        public Task TriggerAsync()
        {
            if (!IsEnabled || _tick is null)
            {
                return Task.CompletedTask;
            }

            LastTickTask = ObserveAsync(_tick());
            return LastTickTask;
        }

        public void Dispose() => Stop();

        private async Task ObserveAsync(Task tick)
        {
            try
            {
                await tick;
            }
            catch (Exception exception)
            {
                LastException = exception;
                throw;
            }
        }
    }

    private sealed class FakeUsageDataChangeMonitor : IUsageDataChangeMonitor
    {
        public UsageDataChangeState State { get; set; } =
            UsageDataChangeState.Changed;

        public int ObserveCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public Task<UsageDataChangeState> ObserveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveCalls++;
            return Task.FromResult(State);
        }

        public void Dispose() => DisposeCalls++;

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCoreRuntimeController : ICoreRuntimeController
    {
        private readonly Queue<CoreRuntimeUiStatus> _ensureResults;
        private CoreRuntimeUiStatus _current;

        public FakeCoreRuntimeController(params CoreRuntimeUiStatus[] results)
        {
            _ensureResults = new Queue<CoreRuntimeUiStatus>(results);
            _current = results.LastOrDefault() ?? CoreRuntimeUiStatus.Standalone;
        }

        public int EnsureCalls { get; private set; }

        public int ClearStatisticsCalls { get; private set; }

        public int CreateBackupCalls { get; private set; }

        public int RestoreBackupCalls { get; private set; }

        public string? LastBackupPath { get; private set; }

        public Func<string, CancellationToken, Task<CoreRuntimeUiStatus>>?
            BackupHandler { get; set; }

        public CoreRuntimeUiStatus Current => _current;

        public Task<CoreRuntimeUiStatus> EnsureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCalls++;
            if (_ensureResults.Count > 0)
            {
                _current = _ensureResults.Dequeue();
            }

            return Task.FromResult(_current);
        }

        public Task<CoreRuntimeUiStatus> ReadStatusAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }

        public Task<CoreRuntimeUiStatus> RebuildCodexAsync(
            CancellationToken cancellationToken) =>
            EnsureAsync(cancellationToken);

        public Task<CoreRuntimeUiStatus> ClearStatisticsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearStatisticsCalls++;
            return Task.FromResult(_current);
        }

        public Task<CoreRuntimeUiStatus> CreateBackupAsync(
            string backupPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateBackupCalls++;
            LastBackupPath = backupPath;
            return BackupHandler?.Invoke(backupPath, cancellationToken) ??
                Task.FromResult(_current);
        }

        public Task<CoreRuntimeUiStatus> RestoreBackupAsync(
            string backupPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreBackupCalls++;
            LastBackupPath = backupPath;
            return Task.FromResult(_current);
        }

    }

    private sealed class FakeDataBackupInteraction : IDataBackupInteraction
    {
        public string? SavePath { get; init; }

        public string? RestorePath { get; init; }

        public bool ConfirmRestoreResult { get; init; }

        public string? ChooseBackupDestination(string suggestedFileName) =>
            SavePath;

        public string? ChooseBackupToRestore() => RestorePath;

        public bool ConfirmRestore(string backupPath) => ConfirmRestoreResult;
    }

    private sealed class FakeUsageQueryMaintenanceGate :
        IUsageQueryMaintenanceGate
    {
        public int PauseCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public Task<IDisposable> PauseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PauseCalls++;
            return Task.FromResult<IDisposable>(new Release(this));
        }

        private sealed class Release(FakeUsageQueryMaintenanceGate owner) :
            IDisposable
        {
            public void Dispose() => owner.ReleaseCalls++;
        }
    }

    private sealed class FakeClearStatisticsConfirmation(bool result) :
        IClearStatisticsConfirmation
    {
        public int Calls { get; private set; }

        public bool ConfirmClearStatistics()
        {
            Calls++;
            return result;
        }
    }
}
