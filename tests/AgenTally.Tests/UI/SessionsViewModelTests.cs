using AgenTally.Domain.Usage;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class SessionsViewModelTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 7, 27, 6, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Refresh_LoadsFirstPageAndSelectsFirstSession()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow first = Root("root-a", NowUtc.AddHours(-1));
        RootSessionSummaryRow second = Root("root-b", NowUtc.AddHours(-2));
        queries.RootSessionsResult = new RootSessionPage(
            [first, second],
            new RootSessionCursor(second.LastActivityUtc, second.Identity));
        queries.RootSessionDetailResult = Detail(first.RootSessionId);
        queries.TurnsResult = new TurnUsagePage(
            TurnCoverageStatus.Complete,
            [Turn(0)],
            EmptyUnattributed());
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsTrue(viewModel.HasSessions);
        Assert.IsTrue(viewModel.HasMoreSessions);
        Assert.AreEqual(2, viewModel.Sessions.Count);
        Assert.AreSame(viewModel.Sessions[0], viewModel.SelectedSession);
        Assert.IsTrue(viewModel.HasSelection);
        Assert.IsTrue(viewModel.HasDetail);
        Assert.AreEqual(1, queries.RootSessionCalls);
        Assert.AreEqual(1, queries.RootSessionDetailCalls);
        Assert.AreEqual(1, queries.TurnCalls);
        Assert.AreEqual(
            first.Identity,
            queries.RootSessionDetailRequests.Single());
    }

    [TestMethod]
    public async Task LoadMoreSessions_AppendsNextPageAndStopsAtEnd()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow first = Root("root-a", NowUtc.AddHours(-1));
        RootSessionSummaryRow second = Root("root-b", NowUtc.AddHours(-2));
        var cursor = new RootSessionCursor(
            second.LastActivityUtc,
            second.Identity);
        var calls = 0;
        queries.RootSessionsHandler = _ =>
        {
            calls++;
            return calls == 1
                ? new RootSessionPage([first, second], cursor)
                : new RootSessionPage(
                    [Root("root-c", NowUtc.AddHours(-3)),
                     Root("root-d", NowUtc.AddHours(-4))],
                    null);
        };
        queries.RootSessionDetailResult = Detail(first.RootSessionId);
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));
        Assert.IsTrue(viewModel.LoadMoreSessionsCommand.CanExecute(null));

        await host.InvokeAsync(() =>
            viewModel.LoadMoreSessionsCommand.ExecuteAsync());

        Assert.AreEqual(4, viewModel.Sessions.Count);
        Assert.IsFalse(viewModel.HasMoreSessions);
        Assert.IsFalse(viewModel.LoadMoreSessionsCommand.CanExecute(null));
        Assert.AreEqual(2, queries.RootSessionCalls);
        RootSessionPageRequest secondRequest =
            queries.RootSessionRequests.Skip(1).Single();
        Assert.AreEqual(cursor, secondRequest.After);
    }

    [TestMethod]
    public async Task DuplicateSessionIds_SelectDetailBySourceIdentity()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow cli = Root(
            "shared-root",
            NowUtc.AddHours(-1),
            "claude-code",
            "claude-code:cli:windows:test");
        RootSessionSummaryRow desktop = Root(
            "shared-root",
            NowUtc.AddHours(-2),
            "claude-code",
            "claude-code:desktop-local-agent:windows:test");
        var desktopRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queries.RootSessionsResult = new RootSessionPage([cli, desktop], null);
        queries.RootSessionDetailHandler = (_, identity) =>
        {
            RootSessionSummaryRow summary = identity == cli.Identity
                ? cli
                : desktop;
            if (identity == desktop.Identity)
            {
                desktopRequested.TrySetResult();
            }

            return new RootSessionDetail(summary, []);
        };
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        await host.InvokeAsync(() =>
            viewModel.SelectedSession = viewModel.Sessions[1]);
        await desktopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(desktop.Identity, viewModel.SelectedSession?.Identity);
        Assert.AreEqual(
            desktop.Identity,
            queries.RootSessionDetailRequests.Last());
    }

    [TestMethod]
    public async Task SwitchingSession_ShowsSharedLoadingFeedbackUntilDetailCompletes()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow first = Root("root-a", NowUtc.AddHours(-1));
        RootSessionSummaryRow second = Root("root-b", NowUtc.AddHours(-2));
        queries.RootSessionsResult = new RootSessionPage([first, second], null);
        queries.RootSessionDetailHandler = (_, identity) =>
            Detail(identity.RootSessionId);
        SessionsViewModel viewModel = CreateViewModel(host, queries);
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        var detailStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var detailRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queries.TurnsAsyncHandler = async (_, identity, cancellationToken) =>
        {
            if (identity == second.Identity)
            {
                detailStarted.TrySetResult();
                await detailRelease.Task.WaitAsync(cancellationToken);
            }

            return new TurnUsagePage(
                TurnCoverageStatus.NoData,
                [],
                EmptyUnattributed());
        };
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
            viewModel.SelectedSession = viewModel.Sessions[1];
        });

        await detailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await host.InvokeAsync(() =>
            viewModel.IsRefreshFeedbackVisible));

        detailRelease.TrySetResult();
        await feedbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(viewModel.IsRefreshFeedbackVisible);
        Assert.AreEqual(second.Identity, viewModel.Detail?.Identity);
    }

    [TestMethod]
    public async Task Detail_MapsContributionsWithKindIndentAndModels()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        var primary = new SessionContributionRow(
            "root-a",
            null,
            SessionKind.Primary,
            0,
            5,
            TestData.MetricSet(800),
            [new SessionModelUsageRow("gpt-5", 5, TestData.MetricSet(800))])
        {
            Pricing = new PricingAggregate(
                0.40m, 1, 0, 0, PricingMissingCategory.None),
        };
        var side = new SessionContributionRow(
            "side-1",
            "root-a",
            SessionKind.Side,
            1,
            2,
            TestData.MetricSet(200),
            []);
        queries.RootSessionDetailResult =
            new RootSessionDetail(summary, [primary, side]);
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        SessionDetailPresentation detail = viewModel.Detail!;
        Assert.AreEqual(2, detail.Contributions.Count);
        SessionContributionPresentation primaryItem = detail.Contributions[0];
        Assert.AreEqual("主会话", primaryItem.KindText);
        Assert.AreEqual(0d, primaryItem.Indent.Left);
        Assert.AreEqual("800", primaryItem.TokensText);
        Assert.AreEqual(SessionPriceState.Complete, primaryItem.PriceState);
        Assert.IsTrue(primaryItem.HasModels);
        Assert.AreEqual(1, primaryItem.Models.Count);
        Assert.AreEqual("gpt-5", primaryItem.Models[0].ModelText);
        SessionContributionPresentation sideItem = detail.Contributions[1];
        Assert.AreEqual("子会话", sideItem.KindText);
        Assert.AreEqual(18d, sideItem.Indent.Left);
        Assert.AreEqual(SessionPriceState.NoData, sideItem.PriceState);
        Assert.IsFalse(sideItem.HasModels);
    }

    [TestMethod]
    public void Detail_ProjectAssociationPreservesKnownAndUnknownStates()
    {
        RootSessionDetail knownDetail = Detail("root-known");
        var known = new SessionDetailPresentation(
            knownDetail,
            TimeZoneInfo.Utc);
        RootSessionSummaryRow unknownSummary = knownDetail.Summary with
        {
            ProjectId = null,
            ProjectPath = null,
            ProjectPathAvailability = PathAvailability.Unavailable
        };
        var unknown = new SessionDetailPresentation(
            new RootSessionDetail(unknownSummary, knownDetail.Contributions),
            TimeZoneInfo.Utc);

        Assert.AreEqual("project-1", known.ProjectId);
        Assert.AreEqual("demo", known.ProjectNameText);
        Assert.IsTrue(known.CanOpenProject);
        Assert.IsNull(unknown.ProjectId);
        Assert.AreEqual("所属项目无法识别", unknown.ProjectNameText);
        Assert.IsFalse(unknown.CanOpenProject);
    }

    [TestMethod]
    public void SessionPresentations_AddNameWithoutReplacingProjectPath()
    {
        RootSessionSummaryRow summary = Root(
            "root-named",
            NowUtc.AddHours(-1)) with
        {
            SessionName = "实现会话名称"
        };

        var listItem = new SessionListItemPresentation(
            summary,
            TimeZoneInfo.Utc);
        var detail = new SessionDetailPresentation(
            new RootSessionDetail(summary, []),
            TimeZoneInfo.Utc);
        var unnamed = new SessionListItemPresentation(
            summary with { SessionName = null },
            TimeZoneInfo.Utc);

        Assert.AreEqual("实现会话名称", listItem.TitleText);
        Assert.AreEqual(@"D:\Projects\demo", listItem.ProjectPathText);
        Assert.AreEqual("实现会话名称", detail.TitleText);
        Assert.AreEqual(@"D:\Projects\demo", detail.ProjectPathText);
        Assert.AreEqual("未命名会话", unnamed.TitleText);
        Assert.AreEqual(@"D:\Projects\demo", unnamed.ProjectPathText);
    }

    [TestMethod]
    public async Task Turns_NoDataCoverage_ShowsEmptyNote()
    {
        SessionDetailPresentation detail = await RefreshWithTurnsAsync(
            new TurnUsagePage(TurnCoverageStatus.NoData, [], EmptyUnattributed()));

        Assert.AreEqual("该会话暂无 Prompt 用量记录。", detail.TurnCoverageNote);
        Assert.AreEqual("0", detail.PromptTurnCountText);
        Assert.IsFalse(detail.HasUnattributed);
    }

    [TestMethod]
    public async Task Turns_CompleteCoverage_HidesCoverageNote()
    {
        SessionDetailPresentation detail = await RefreshWithTurnsAsync(
            new TurnUsagePage(
                TurnCoverageStatus.Complete,
                [Turn(0)],
                EmptyUnattributed(),
                1));

        Assert.IsNull(detail.TurnCoverageNote);
        Assert.AreEqual("1", detail.PromptTurnCountText);
        Assert.AreEqual(1, detail.Turns.Count);
    }

    [TestMethod]
    public async Task PromptTimeline_MapsSummaryModelsAndLazyLoadsCallsOnlyOnce()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        queries.RootSessionDetailResult = Detail(summary.RootSessionId) with
        {
            Models =
            [
                new SessionModelUsageRow(
                    "gpt-root",
                    2,
                    TestData.MetricSet(200))
            ]
        };
        queries.TurnsResult = new TurnUsagePage(
            TurnCoverageStatus.Complete,
            [
                Turn(0) with
                {
                    PromptPreview = "实现 Prompt 归因",
                    UserMessageCount = 3,
                    ToolCallCount = 2,
                    MaxPromptTokens = 20
                }
            ],
            EmptyUnattributed());
        queries.TurnCallsResult =
        [
            new TurnCallUsageRow(
                NowUtc.AddMinutes(-2),
                "gpt-root",
                "root-a",
                SessionKind.Primary,
                SessionRole.Main,
                ["shell_command", "shell_command"],
                TestData.MetricSet(6)),
            new TurnCallUsageRow(
                NowUtc.AddMinutes(-1),
                "codex-auto-review",
                "guardian-a",
                SessionKind.Side,
                SessionRole.Guardian,
                [],
                TestData.MetricSet(4))
        ];
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        SessionDetailPresentation detail = viewModel.Detail!;
        Assert.HasCount(1, detail.Models);
        Assert.AreEqual("gpt-root", detail.Models[0].ModelText);
        TurnUsagePresentation prompt = Assert.ContainsSingle(detail.Turns);
        Assert.AreEqual("实现 Prompt 归因", prompt.PromptText);
        Assert.AreEqual("含 3 条用户消息", prompt.UserMessageText);
        Assert.AreEqual("2", prompt.ToolCallCountText);
        Assert.AreEqual(0.5d, prompt.RelativeUsage, 0.001d);
        Assert.HasCount(0, queries.TurnCallRequests);

        await host.InvokeAsync(() =>
            prompt.ToggleExpandedCommand.ExecuteAsync());

        Assert.IsTrue(prompt.IsExpanded);
        Assert.IsTrue(prompt.IsLoaded);
        Assert.HasCount(2, prompt.Calls);
        Assert.HasCount(1, queries.TurnCallRequests);
        Assert.AreEqual("主会话", prompt.Calls[0].SourceText);
        Assert.AreEqual("shell_command ×2", prompt.Calls[0].ToolText);
        Assert.AreEqual("Guardian", prompt.Calls[1].SourceText);
        Assert.AreEqual("codex-auto-review", prompt.Calls[1].ModelText);

        await host.InvokeAsync(() =>
            prompt.ToggleExpandedCommand.ExecuteAsync());
        await host.InvokeAsync(() =>
            prompt.ToggleExpandedCommand.ExecuteAsync());

        Assert.IsTrue(prompt.IsExpanded);
        Assert.HasCount(1, queries.TurnCallRequests);
    }

    [TestMethod]
    public async Task ExpandingPrompt_ShowsSharedLoadingFeedbackUntilCallsComplete()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        queries.RootSessionDetailResult = Detail(summary.RootSessionId);
        queries.TurnsResult = new TurnUsagePage(
            TurnCoverageStatus.Complete,
            [Turn(0)],
            EmptyUnattributed());
        SessionsViewModel viewModel = CreateViewModel(host, queries);
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        TurnUsagePresentation prompt =
            Assert.ContainsSingle(viewModel.Detail!.Turns);
        var callsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callsRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var feedbackCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queries.TurnCallsAsyncHandler =
            async (_, _, _, cancellationToken) =>
            {
                callsStarted.TrySetResult();
                await callsRelease.Task.WaitAsync(cancellationToken);
                return [];
            };
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
            prompt.ToggleExpandedCommand.Execute(null);
        });

        await callsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await host.InvokeAsync(() =>
            viewModel.IsRefreshFeedbackVisible));
        Assert.IsTrue(prompt.IsLoading);

        callsRelease.TrySetResult();
        await feedbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(viewModel.IsRefreshFeedbackVisible);
        Assert.IsTrue(prompt.IsLoaded);
    }

    [TestMethod]
    public async Task BackgroundRefresh_PreservesExpandedRowsAndReloadsCalls()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        queries.RootSessionDetailResult = new RootSessionDetail(
            summary,
            [new SessionContributionRow(
                summary.RootSessionId,
                null,
                SessionKind.Primary,
                0,
                3,
                TestData.MetricSet(1000),
                [new SessionModelUsageRow(
                    "gpt-old",
                    3,
                    TestData.MetricSet(1000))])]);
        queries.TurnsResult = new TurnUsagePage(
            TurnCoverageStatus.Complete,
            [Turn(0)],
            EmptyUnattributed());
        queries.TurnCallsResult =
        [
            new TurnCallUsageRow(
                NowUtc.AddMinutes(-2),
                "gpt-old",
                summary.RootSessionId,
                SessionKind.Primary,
                SessionRole.Main,
                [],
                TestData.MetricSet(10))
        ];
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        SessionDetailPresentation previous = viewModel.Detail!;
        TurnUsagePresentation previousTurn =
            Assert.ContainsSingle(previous.Turns);
        await host.InvokeAsync(() =>
            previousTurn.ToggleExpandedCommand.ExecuteAsync());
        await host.InvokeAsync(() =>
        {
            previous.Contributions[0].IsExpanded = true;
            viewModel.SelectedDetailTabIndex = 1;
        });
        queries.TurnCallsResult =
        [
            new TurnCallUsageRow(
                NowUtc.AddMinutes(-1),
                "gpt-new",
                summary.RootSessionId,
                SessionKind.Primary,
                SessionRole.Main,
                [],
                TestData.MetricSet(20))
        ];

        await host.InvokeAsync(() =>
            viewModel.RefreshInBackgroundAsync(CancellationToken.None));

        SessionDetailPresentation refreshed = viewModel.Detail!;
        Assert.AreNotSame(previous, refreshed);
        Assert.AreEqual(1, viewModel.SelectedDetailTabIndex);
        Assert.IsTrue(refreshed.Contributions[0].IsExpanded);
        TurnUsagePresentation refreshedTurn =
            Assert.ContainsSingle(refreshed.Turns);
        Assert.IsTrue(refreshedTurn.IsExpanded);
        Assert.IsTrue(refreshedTurn.IsLoaded);
        Assert.AreEqual(
            "gpt-new",
            Assert.ContainsSingle(refreshedTurn.Calls).ModelText);
        Assert.HasCount(2, queries.TurnCallRequests);
        Assert.IsFalse(viewModel.IsRefreshFeedbackVisible);
    }

    [TestMethod]
    public void TurnCallMetrics_UsesThreeUserFacingBillingCategories()
    {
        var metrics = new UsageMetricSet(
            TestData.Aggregate(1_000),
            TestData.Aggregate(300),
            TestData.Aggregate(700),
            TestData.Aggregate(50),
            TestData.Aggregate(200),
            TestData.Aggregate(25),
            TestData.Aggregate(10),
            TestData.Aggregate(1_250),
            TestData.Aggregate(1_250));
        var row = new TurnCallUsageRow(
            NowUtc,
            "gpt-test",
            "root-a",
            SessionKind.Primary,
            SessionRole.Main,
            [],
            metrics);

        var presentation = new TurnCallUsagePresentation(
            1,
            row,
            TimeZoneInfo.Utc);

        Assert.AreEqual(
            "缓存输入 700 · 未缓存输入 300 · 输出 200",
            presentation.MetricsText);
    }

    [TestMethod]
    public async Task SwitchingSession_CancelsExpandedPromptCallQuery()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow first = Root("root-a", NowUtc.AddHours(-1));
        RootSessionSummaryRow second = Root("root-b", NowUtc.AddHours(-2));
        queries.RootSessionsResult = new RootSessionPage([first, second], null);
        queries.RootSessionDetailHandler = (_, identity) =>
            Detail(identity.RootSessionId);
        queries.TurnsHandler = (_, identity) => new TurnUsagePage(
            TurnCoverageStatus.Complete,
            [Turn(0) with { PromptPreview = identity.RootSessionId }],
            EmptyUnattributed());
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queries.TurnCallsAsyncHandler =
            async (_, identity, _, cancellationToken) =>
            {
                if (identity != first.Identity)
                {
                    return [];
                }

                started.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    canceled.TrySetResult();
                    throw;
                }

                return [];
            };
        SessionsViewModel viewModel = CreateViewModel(host, queries);
        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        TurnUsagePresentation stalePrompt =
            Assert.ContainsSingle(viewModel.Detail!.Turns);

        await host.InvokeAsync(() =>
            stalePrompt.ToggleExpandedCommand.Execute(null));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.InvokeAsync(() =>
            viewModel.SelectedSession = viewModel.Sessions[1]);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.HasCount(1, queries.TurnCallRequests);
        Assert.AreEqual(
            first.Identity,
            queries.TurnCallRequests.Single().Identity);
    }

    [TestMethod]
    public async Task Turns_PartialCoverage_ShowsNoteAndUnattributed()
    {
        SessionDetailPresentation detail = await RefreshWithTurnsAsync(
            new TurnUsagePage(
                TurnCoverageStatus.Partial,
                [Turn(0)],
                new UnattributedUsageSummary(3, TestData.MetricSet(120)),
                1));

        Assert.AreEqual(
            "部分调用无法可靠归属到 Prompt，已单独列出。",
            detail.TurnCoverageNote);
        Assert.AreEqual("≥1", detail.PromptTurnCountText);
        Assert.IsTrue(detail.HasUnattributed);
        StringAssert.Contains(detail.UnattributedText!, "Prompt 归属未确定");
        StringAssert.Contains(detail.UnattributedText!, "3 次调用");
        StringAssert.Contains(detail.UnattributedText!, "120 Token");
    }

    [TestMethod]
    public async Task Turns_UnsupportedCoverage_ShowsUnsupportedNote()
    {
        SessionDetailPresentation detail = await RefreshWithTurnsAsync(
            new TurnUsagePage(
                TurnCoverageStatus.Unsupported,
                [],
                EmptyUnattributed()));

        Assert.AreEqual(
            "当前来源缺少可靠 Prompt 轮次元数据，仅显示汇总用量。",
            detail.TurnCoverageNote);
        Assert.AreEqual("—", detail.PromptTurnCountText);
    }

    [TestMethod]
    public async Task Refresh_EmptyPage_ClearsSelectionAndDetail()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsFalse(viewModel.HasSessions);
        Assert.IsFalse(viewModel.HasSelection);
        Assert.IsNull(viewModel.SelectedSession);
        Assert.IsFalse(viewModel.HasDetail);
        Assert.IsFalse(viewModel.HasDetailError);
        Assert.AreEqual(0, queries.RootSessionDetailCalls);
        Assert.AreEqual(0, queries.TurnCalls);
    }

    [TestMethod]
    public async Task Refresh_MissingDetail_ShowsPlaceholderWithoutError()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        queries.RootSessionsResult = new RootSessionPage(
            [Root("root-a", NowUtc.AddHours(-1))],
            null);
        queries.RootSessionDetailResult = null;
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsTrue(viewModel.HasSelection);
        Assert.IsFalse(viewModel.HasDetail);
        Assert.IsFalse(viewModel.HasDetailError);
        Assert.IsNull(viewModel.Detail);
    }

    [TestMethod]
    public async Task LoadMoreTurns_AppendsWithOffsetFilter()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        queries.RootSessionDetailResult = Detail(summary.RootSessionId);
        queries.TurnsHandler = (filter, _) =>
            filter.Offset == 0
                ? new TurnUsagePage(
                    TurnCoverageStatus.Complete,
                    Enumerable.Range(0, SessionsViewModel.TurnPageSize)
                        .Select(Turn)
                        .ToArray(),
                    EmptyUnattributed())
                : new TurnUsagePage(
                    TurnCoverageStatus.Complete,
                    [Turn(100), Turn(101), Turn(102)],
                    EmptyUnattributed());
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() => viewModel.RefreshAsync(CancellationToken.None));

        Assert.IsTrue(viewModel.HasMoreTurns);
        Assert.AreEqual(SessionsViewModel.TurnPageSize, viewModel.Detail!.Turns.Count);
        Assert.IsTrue(viewModel.LoadMoreTurnsCommand.CanExecute(null));

        await host.InvokeAsync(() =>
            viewModel.LoadMoreTurnsCommand.ExecuteAsync());

        Assert.AreEqual(SessionsViewModel.TurnPageSize + 3, viewModel.Detail!.Turns.Count);
        Assert.IsFalse(viewModel.HasMoreTurns);
        Assert.AreEqual(2, queries.TurnCalls);
        Assert.AreEqual(50, queries.TurnRequests.Skip(1).Single().Filter.Offset);
    }

    [TestMethod]
    public async Task BackgroundRefresh_PreservesLoadedPromptDepthAndLoadMoreState()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        queries.RootSessionDetailResult = Detail(summary.RootSessionId);
        var refreshStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queries.TurnsAsyncHandler = async (filter, _, cancellationToken) =>
        {
            if (filter.Offset == 0 && filter.Limit == 101)
            {
                refreshStarted.TrySetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return new TurnUsagePage(
                    TurnCoverageStatus.Complete,
                    Enumerable.Range(0, 101).Select(Turn).ToArray(),
                    EmptyUnattributed(),
                    101);
            }

            int start = filter.Offset;
            return new TurnUsagePage(
                TurnCoverageStatus.Complete,
                Enumerable.Range(start, SessionsViewModel.TurnPageSize)
                    .Select(Turn)
                    .ToArray(),
                EmptyUnattributed(),
                101);
        };
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        await host.InvokeAsync(() =>
            viewModel.LoadMoreTurnsCommand.ExecuteAsync());
        Assert.AreEqual(100, viewModel.Detail!.Turns.Count);
        Assert.IsTrue(viewModel.HasMoreTurns);

        Task? refresh = null;
        await host.InvokeAsync(() =>
        {
            refresh = viewModel.RefreshInBackgroundAsync(CancellationToken.None);
        });
        await refreshStarted.Task;
        await host.InvokeAsync(() =>
        {
            Assert.AreEqual(100, viewModel.Detail!.Turns.Count);
            Assert.IsTrue(viewModel.HasMoreTurns);
            Assert.IsTrue(viewModel.LoadMoreTurnsCommand.CanExecute(null));
            Assert.IsFalse(viewModel.IsDetailLoading);
        });

        releaseRefresh.TrySetResult();
        await refresh!;

        Assert.AreEqual(100, viewModel.Detail!.Turns.Count);
        Assert.IsTrue(viewModel.HasMoreTurns);
        Assert.IsTrue(viewModel.LoadMoreTurnsCommand.CanExecute(null));
        Assert.IsTrue(queries.TurnRequests.Any(request =>
            request.Filter.Offset == 0 && request.Filter.Limit == 101));
    }

    [TestMethod]
    public async Task BackgroundRefresh_DoesNotCancelActivePromptLoadMore()
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        queries.RootSessionDetailResult = Detail(summary.RootSessionId);
        var loadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queries.TurnsAsyncHandler = async (filter, _, cancellationToken) =>
        {
            if (filter.Offset == SessionsViewModel.TurnPageSize)
            {
                loadStarted.TrySetResult();
                await releaseLoad.Task.WaitAsync(cancellationToken);
            }

            return new TurnUsagePage(
                TurnCoverageStatus.Complete,
                Enumerable.Range(
                        filter.Offset,
                        SessionsViewModel.TurnPageSize)
                    .Select(Turn)
                    .ToArray(),
                EmptyUnattributed(),
                SessionsViewModel.TurnPageSize * 2);
        };
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));
        Task? loadMore = null;
        await host.InvokeAsync(() =>
        {
            loadMore = viewModel.LoadMoreTurnsCommand.ExecuteAsync();
        });
        await loadStarted.Task;

        Task? refresh = null;
        await host.InvokeAsync(() =>
        {
            refresh = viewModel.RefreshInBackgroundAsync(CancellationToken.None);
        });
        await refresh!;

        Assert.IsTrue(viewModel.IsLoadingMoreTurns);
        Assert.AreEqual(50, viewModel.Detail!.Turns.Count);
        Assert.AreEqual(1, queries.RootSessionRequests.Count);

        releaseLoad.TrySetResult();
        await loadMore!;

        Assert.IsFalse(viewModel.IsLoadingMoreTurns);
        Assert.AreEqual(100, viewModel.Detail!.Turns.Count);
    }

    [TestMethod]
    public async Task Filters_ApplyToListDetailPromptAndCallQueries()
    {
        await using var host = new StaDispatcherTestHost();
        const string projectId = "0123456789abcdef01234567";
        RootSessionSummaryRow summary = Root(
            "root-filtered",
            new DateTimeOffset(2026, 7, 26, 5, 0, 0, TimeSpan.Zero)) with
        {
            ProjectId = projectId,
            ProjectPath = @"D:\Projects\filtered"
        };
        UsageFilter? detailFilter = null;
        UsageFilter? turnFilter = null;
        UsageFilter? callFilter = null;
        var queries = new FakeUsageQueryService
        {
            FilterValues = new UsageFilterValues(["codex"], ["gpt-test"])
            {
                Projects =
                [
                    new ProjectFilterValue(
                        projectId,
                        @"D:\Projects\filtered",
                        PathAvailability.Available)
                ]
            },
            RootSessionsResult = new RootSessionPage([summary], null),
            RootSessionDetailHandler = (filter, identity) =>
            {
                detailFilter = filter;
                return Detail(identity.RootSessionId) with { Summary = summary };
            },
            TurnsHandler = (filter, _) =>
            {
                turnFilter = filter;
                return new TurnUsagePage(
                    TurnCoverageStatus.Complete,
                    [Turn(0)],
                    EmptyUnattributed());
            },
            TurnCallsHandler = (filter, _, _) =>
            {
                callFilter = filter;
                return [];
            }
        };
        SessionsViewModel viewModel = CreateViewModel(host, queries);
        await host.InvokeAsync(() =>
        {
            viewModel.SelectedAgent = "codex";
            viewModel.SelectedModel = "gpt-test";
            viewModel.SelectedProject = projectId;
            viewModel.SelectedPeriod = SessionsViewModel.Custom;
            viewModel.CustomStartDate = new DateTime(2026, 7, 20, 9, 0, 0);
            viewModel.CustomEndDate = new DateTime(2026, 7, 27, 18, 0, 0);
        });

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(
            "2026年7月20日 09:00至2026年7月27日 18:00",
            viewModel.PeriodSummaryText);
        Assert.AreEqual("codex", viewModel.SelectedAgent);
        Assert.AreEqual("gpt-test", viewModel.SelectedModel);
        Assert.AreEqual(projectId, viewModel.SelectedProject);
        Assert.HasCount(2, viewModel.AgentOptions);
        Assert.HasCount(2, viewModel.ModelOptions);
        Assert.HasCount(2, viewModel.ProjectOptions);
        UsageFilter listFilter =
            Assert.ContainsSingle(queries.RootSessionRequests).Filter;
        AssertFilter(listFilter);
        AssertFilter(Assert.ContainsSingle(queries.FilterValueFilters));
        Assert.IsNotNull(detailFilter);
        Assert.IsNotNull(turnFilter);
        AssertFilter(detailFilter);
        AssertFilter(turnFilter);

        TurnUsagePresentation prompt =
            Assert.ContainsSingle(viewModel.Detail!.Turns);
        await host.InvokeAsync(() =>
            prompt.ToggleExpandedCommand.ExecuteAsync());

        Assert.IsNotNull(callFilter);
        AssertFilter(callFilter);

        void AssertFilter(UsageFilter filter)
        {
            Assert.AreEqual(
                new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
                filter.StartInclusiveUtc);
            Assert.AreEqual(
                new DateTimeOffset(2026, 7, 27, 18, 0, 0, TimeSpan.Zero),
                filter.EndExclusiveUtc);
            Assert.AreEqual("codex", filter.AgentId);
            Assert.AreEqual("gpt-test", filter.NormalizedModel);
            Assert.AreEqual(projectId, filter.ProjectId);
        }
    }

    [TestMethod]
    public async Task AllTimeSummary_UsesFirstFilteredRecordWithoutPeriodPrefix()
    {
        await using var host = new StaDispatcherTestHost();
        DateTimeOffset first =
            new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);
        DashboardQueryResult dashboard = TestData.Dashboard(300);
        RootSessionSummaryRow summary = Root(
            "root-all-time",
            NowUtc.AddHours(-1));
        var queries = new FakeUsageQueryService
        {
            DashboardResult = dashboard with
            {
                Overview = dashboard.Overview with
                {
                    FirstOccurredAtUtc = first
                }
            },
            RootSessionsResult = new RootSessionPage([summary], null),
            RootSessionDetailResult = Detail(summary.RootSessionId)
        };
        SessionsViewModel viewModel = CreateViewModel(host, queries);
        await host.InvokeAsync(() =>
            viewModel.SelectedPeriod = SessionsViewModel.AllTime);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        Assert.AreEqual(
            "2026年3月2日至7月27日",
            viewModel.PeriodSummaryText);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch,
            Assert.ContainsSingle(queries.RootSessionRequests)
                .Filter.StartInclusiveUtc);
        Assert.AreEqual(1, queries.OverviewCalls);
    }

    private static SessionsViewModel CreateViewModel(
        StaDispatcherTestHost host,
        FakeUsageQueryService queries) =>
        new(
            queries,
            host.Dispatcher,
            new FixedTimeProvider(NowUtc),
            TimeZoneInfo.Utc);

    private static async Task<SessionDetailPresentation> RefreshWithTurnsAsync(
        TurnUsagePage turns)
    {
        await using var host = new StaDispatcherTestHost();
        var queries = new FakeUsageQueryService();
        RootSessionSummaryRow summary = Root("root-a", NowUtc.AddHours(-1));
        queries.RootSessionsResult = new RootSessionPage([summary], null);
        queries.RootSessionDetailResult = Detail(summary.RootSessionId);
        queries.TurnsResult = turns;
        SessionsViewModel viewModel = CreateViewModel(host, queries);

        await host.InvokeAsync(() =>
            viewModel.RefreshAsync(CancellationToken.None));

        return viewModel.Detail!;
    }

    private static RootSessionSummaryRow Root(
        string id,
        DateTimeOffset lastActivityUtc,
        string agentId = "codex",
        string sourceInstanceId = "codex:windows:test") =>
        new(
            new RootSessionIdentity(agentId, sourceInstanceId, id),
            lastActivityUtc.AddHours(-2),
            lastActivityUtc,
            "project-1",
            @"D:\Projects\demo",
            PathAvailability.Available,
            3,
            0,
            TestData.MetricSet(1000))
        {
            Pricing = new PricingAggregate(
                0.50m, 1, 0, 0, PricingMissingCategory.None),
        };

    private static RootSessionDetail Detail(string rootSessionId) =>
        new(
            Root(rootSessionId, NowUtc.AddHours(-1)),
            [new SessionContributionRow(
                rootSessionId,
                null,
                SessionKind.Primary,
                0,
                3,
                TestData.MetricSet(1000),
                [])]);

    private static TurnUsageRow Turn(int index) =>
        new(
            $"turn-{index}",
            NowUtc.AddHours(-1).AddMinutes(index),
            NowUtc.AddHours(-1).AddMinutes(index).AddSeconds(30),
            1,
            TestData.MetricSet(10 + index))
        {
            Pricing = new PricingAggregate(
                0.001m, 1, 0, 0, PricingMissingCategory.None),
        };

    private static UnattributedUsageSummary EmptyUnattributed() =>
        new(0, TestData.MetricSet(null));
}
