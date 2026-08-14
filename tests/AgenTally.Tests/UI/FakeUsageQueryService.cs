using System.Collections.Concurrent;
using System.Windows.Threading;
using AgenTally.Storage.Queries;

namespace AgenTally.Tests.UI;

internal sealed record DashboardQueryResult(
    UsageOverview Overview,
    IReadOnlyList<UsageTrendPoint> Trend,
    IReadOnlyList<UsageRecordRow> Recent,
    IReadOnlyList<ModelUsageRow> Models,
    IReadOnlyList<AgentUsageRow> Agents);

internal sealed class FakeUsageQueryService : IUsageQueryService
{
    private readonly ConcurrentDictionary<string, Task<DashboardQueryResult>> _routes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<DashboardQueryResult>> _projectRoutes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentCallCounter> _agentCalls =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentCallCounter> _agentCompletions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentCallCounter> _projectCalls =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentCallCounter> _projectCompletions =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private TaskCompletionSource? _dashboardRelease;
    private TaskCompletionSource? _dashboardStarted;
    private int _dashboardExpected;
    private int _dashboardStartCount;
    private TaskCompletionSource? _sourceRelease;
    private TaskCompletionSource? _sourceStarted;

    public DashboardQueryResult DashboardResult { get; set; } = TestData.Dashboard(0);

    public UsageFilterValues FilterValues { get; set; } = new([], []);

    public Func<UsageFilter, UsageFilterValues>? FilterValuesHandler
    {
        get;
        set;
    }

    public IReadOnlyList<AgentModelUsageRow> AgentModels { get; set; } = [];

    public IReadOnlyList<ProjectUsageRow> ProjectsResult { get; set; } = [];

    public Func<UsageFilter, IReadOnlyList<ProjectUsageRow>>? ProjectsHandler
    {
        get;
        set;
    }

    public IReadOnlyList<SourceStatusRow> Sources { get; set; } = [];

    public IReadOnlyList<PriceSettingRow> PriceSettings { get; set; } = [];

    public Exception? DashboardException { get; set; }

    public Exception? SourcesException { get; set; }

    public int OverviewCalls { get; private set; }

    public int TrendCalls { get; private set; }

    public int RecentCalls { get; private set; }

    public int ModelCalls { get; private set; }

    public int AgentCalls { get; private set; }

    public int AgentModelCalls { get; private set; }

    public int ProjectCalls { get; private set; }

    public int FilterValueCalls { get; private set; }

    public int SourceCalls { get; private set; }

    public int PriceSettingCalls { get; private set; }

    public ConcurrentQueue<UsageFilter> OverviewFilters { get; } = new();

    public ConcurrentQueue<UsageFilter> TrendFilters { get; } = new();

    public ConcurrentQueue<UsageFilter> RecentFilters { get; } = new();

    public ConcurrentQueue<UsageFilter> ModelFilters { get; } = new();

    public ConcurrentQueue<UsageFilter> AgentFilters { get; } = new();

    public ConcurrentQueue<UsageFilter> AgentModelFilters { get; } = new();

    public ConcurrentQueue<UsageFilter> ProjectFilters { get; } = new();

    public ConcurrentQueue<UsageFilter> FilterValueFilters { get; } = new();

    public ConcurrentQueue<(string AgentId, CancellationToken Token)>
        DashboardCancellationTokens { get; } = new();

    public ConcurrentQueue<(string ProjectId, CancellationToken Token)>
        ProjectCancellationTokens { get; } = new();

    public void SetRoute(string agentId, Task<DashboardQueryResult> result) =>
        _routes[agentId] = result;

    public void SetProjectRoute(
        string projectId,
        Task<DashboardQueryResult> result) =>
        _projectRoutes[projectId] = result;

    public Task WaitForAgentCallsAsync(string agentId, int count) =>
        _agentCalls.GetOrAdd(agentId, static _ => new AgentCallCounter())
            .WaitAsync(count);

    public Task WaitForAgentCompletionsAsync(string agentId, int count) =>
        _agentCompletions.GetOrAdd(agentId, static _ => new AgentCallCounter())
            .WaitAsync(count);

    public Task WaitForProjectCallsAsync(string projectId, int count) =>
        _projectCalls.GetOrAdd(projectId, static _ => new AgentCallCounter())
            .WaitAsync(count);

    public Task WaitForProjectCompletionsAsync(string projectId, int count) =>
        _projectCompletions.GetOrAdd(projectId, static _ => new AgentCallCounter())
            .WaitAsync(count);

    public Task BlockDashboardQueriesAsync(int expectedCalls = 6)
    {
        lock (_gate)
        {
            _dashboardExpected = expectedCalls;
            _dashboardStartCount = 0;
            _dashboardRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _dashboardStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _dashboardStarted.Task;
        }
    }

    public void ReleaseDashboardQueries()
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = _dashboardRelease;
            _dashboardRelease = null;
            _dashboardStarted = null;
        }

        release?.TrySetResult();
    }

    public Task BlockSourcesAsync()
    {
        lock (_gate)
        {
            _sourceRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _sourceStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _sourceStarted.Task;
        }
    }

    public void ReleaseSources()
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = _sourceRelease;
            _sourceRelease = null;
            _sourceStarted = null;
        }

        release?.TrySetResult();
    }

    public async Task<UsageOverview> GetOverviewAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        OverviewCalls++;
        OverviewFilters.Enqueue(filter);
        DashboardQueryResult result = await ResolveAsync(filter, cancellationToken);
        return result.Overview;
    }

    public async Task<IReadOnlyList<UsageTrendPoint>> GetTrendAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        TrendCalls++;
        TrendFilters.Enqueue(filter);
        DashboardQueryResult result = await ResolveAsync(filter, cancellationToken);
        return result.Trend;
    }

    public async Task<IReadOnlyList<UsageRecordRow>> GetRecentRecordsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        RecentCalls++;
        RecentFilters.Enqueue(filter);
        DashboardQueryResult result = await ResolveAsync(filter, cancellationToken);
        return result.Recent;
    }

    public async Task<IReadOnlyList<ModelUsageRow>> GetModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ModelCalls++;
        ModelFilters.Enqueue(filter);
        DashboardQueryResult result = await ResolveAsync(filter, cancellationToken);
        return result.Models;
    }

    public async Task<IReadOnlyList<AgentUsageRow>> GetAgentsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        AgentCalls++;
        AgentFilters.Enqueue(filter);
        DashboardQueryResult result = await ResolveAsync(filter, cancellationToken);
        return result.Agents;
    }

    public async Task<IReadOnlyList<AgentModelUsageRow>> GetAgentModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        AgentModelCalls++;
        AgentModelFilters.Enqueue(filter);
        await BeforeDashboardQueryAsync(cancellationToken);
        ThrowDashboardFailure();
        return AgentModels;
    }

    public async Task<UsageFilterValues> GetFilterValuesAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        FilterValueCalls++;
        FilterValueFilters.Enqueue(filter);
        await BeforeDashboardQueryAsync(cancellationToken);
        ThrowDashboardFailure();
        return FilterValuesHandler?.Invoke(filter) ?? FilterValues;
    }

    public async Task<IReadOnlyList<SourceStatusRow>> GetSourcesAsync(
        CancellationToken cancellationToken)
    {
        SourceCalls++;
        Task? releaseTask = null;
        lock (_gate)
        {
            if (_sourceRelease is not null)
            {
                releaseTask = _sourceRelease.Task;
                _sourceStarted?.TrySetResult();
            }
        }

        if (releaseTask is not null)
        {
            await releaseTask.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (SourcesException is not null)
        {
            throw SourcesException;
        }

        return Sources;
    }

    public Task<IReadOnlyList<PriceSettingRow>> GetPriceSettingsAsync(
        CancellationToken cancellationToken)
    {
        PriceSettingCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PriceSettings);
    }

    public Task<RootSessionPage> GetRootSessionsAsync(
        RootSessionPageRequest request,
        CancellationToken cancellationToken)
    {
        RootSessionCalls++;
        RootSessionRequests.Enqueue(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionsException is not null)
        {
            throw SessionsException;
        }

        return Task.FromResult(
            RootSessionsHandler?.Invoke(request) ?? RootSessionsResult);
    }

    public Task<RootSessionDetail?> GetRootSessionDetailAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken)
    {
        RootSessionDetailCalls++;
        RootSessionDetailRequests.Enqueue(identity);
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionsException is not null)
        {
            throw SessionsException;
        }

        return Task.FromResult(
            RootSessionDetailHandler?.Invoke(filter, identity) ??
            RootSessionDetailResult);
    }

    public async Task<IReadOnlyList<ProjectUsageRow>> GetProjectsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ProjectCalls++;
        ProjectFilters.Enqueue(filter);
        await BeforeDashboardQueryAsync(cancellationToken);
        ThrowDashboardFailure();
        cancellationToken.ThrowIfCancellationRequested();
        return ProjectsHandler?.Invoke(filter) ?? ProjectsResult;
    }

    public Task<TurnUsagePage> GetTurnsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken)
    {
        TurnCalls++;
        TurnRequests.Enqueue((filter, identity));
        cancellationToken.ThrowIfCancellationRequested();
        if (SessionsException is not null)
        {
            throw SessionsException;
        }

        return TurnsAsyncHandler?.Invoke(filter, identity, cancellationToken) ??
            Task.FromResult(
                TurnsHandler?.Invoke(filter, identity) ?? TurnsResult);
    }

    public Task<IReadOnlyList<TurnCallUsageRow>> GetTurnCallsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        string turnIdHash,
        CancellationToken cancellationToken)
    {
        TurnCallRequests.Enqueue((filter, identity, turnIdHash));
        cancellationToken.ThrowIfCancellationRequested();
        return TurnCallsAsyncHandler?.Invoke(
                filter,
                identity,
                turnIdHash,
                cancellationToken) ??
            Task.FromResult(
                TurnCallsHandler?.Invoke(filter, identity, turnIdHash) ??
                TurnCallsResult);
    }

    public RootSessionPage RootSessionsResult { get; set; } = new([], null);

    public Func<RootSessionPageRequest, RootSessionPage>? RootSessionsHandler { get; set; }

    public RootSessionDetail? RootSessionDetailResult { get; set; }

    public Func<UsageFilter, RootSessionIdentity, RootSessionDetail?>?
        RootSessionDetailHandler { get; set; }

    public TurnUsagePage TurnsResult { get; set; } = new(
        TurnCoverageStatus.NoData,
        [],
        new UnattributedUsageSummary(0, EmptyMetricSet()));

    public Func<UsageFilter, RootSessionIdentity, TurnUsagePage>?
        TurnsHandler { get; set; }

    public Func<UsageFilter, RootSessionIdentity, CancellationToken,
        Task<TurnUsagePage>>? TurnsAsyncHandler { get; set; }

    public IReadOnlyList<TurnCallUsageRow> TurnCallsResult { get; set; } = [];

    public Func<UsageFilter, RootSessionIdentity, string, IReadOnlyList<TurnCallUsageRow>>?
        TurnCallsHandler { get; set; }

    public Func<
        UsageFilter,
        RootSessionIdentity,
        string,
        CancellationToken,
        Task<IReadOnlyList<TurnCallUsageRow>>>? TurnCallsAsyncHandler { get; set; }

    public Exception? SessionsException { get; set; }

    public int RootSessionCalls { get; private set; }

    public int RootSessionDetailCalls { get; private set; }

    public int TurnCalls { get; private set; }

    public ConcurrentQueue<RootSessionPageRequest> RootSessionRequests { get; } = new();

    public ConcurrentQueue<RootSessionIdentity> RootSessionDetailRequests { get; } = new();

    public ConcurrentQueue<(UsageFilter Filter, RootSessionIdentity Identity)>
        TurnRequests { get; } = new();

    public ConcurrentQueue<(
        UsageFilter Filter,
        RootSessionIdentity Identity,
        string TurnIdHash)> TurnCallRequests { get; } = new();

    private async Task<DashboardQueryResult> ResolveAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        await BeforeDashboardQueryAsync(cancellationToken);
        ThrowDashboardFailure();
        string route = filter.AgentId ?? string.Empty;
        DashboardCancellationTokens.Enqueue((route, cancellationToken));
        _agentCalls.GetOrAdd(route, static _ => new AgentCallCounter()).Signal();
        string? projectId = filter.ProjectId;
        if (projectId is not null)
        {
            ProjectCancellationTokens.Enqueue((projectId, cancellationToken));
            _projectCalls.GetOrAdd(projectId, static _ => new AgentCallCounter()).Signal();
        }

        DashboardQueryResult result =
            projectId is not null &&
            _projectRoutes.TryGetValue(
                projectId,
                out Task<DashboardQueryResult>? projectRouted)
                ? await projectRouted
                : _routes.TryGetValue(
                    route,
                    out Task<DashboardQueryResult>? routed)
                    ? await routed
                    : DashboardResult;
        _agentCompletions.GetOrAdd(route, static _ => new AgentCallCounter()).Signal();
        if (projectId is not null)
        {
            _projectCompletions
                .GetOrAdd(projectId, static _ => new AgentCallCounter())
                .Signal();
        }

        return result;
    }

    private async Task BeforeDashboardQueryAsync(CancellationToken cancellationToken)
    {
        Task? releaseTask = null;
        lock (_gate)
        {
            if (_dashboardRelease is not null)
            {
                releaseTask = _dashboardRelease.Task;
                _dashboardStartCount++;
                if (_dashboardStartCount >= _dashboardExpected)
                {
                    _dashboardStarted?.TrySetResult();
                }
            }
        }

        if (releaseTask is not null)
        {
            await releaseTask.WaitAsync(cancellationToken);
        }
    }

    private void ThrowDashboardFailure()
    {
        if (DashboardException is not null)
        {
            throw DashboardException;
        }
    }

    private sealed class AgentCallCounter
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
        private int _count;

        public void Signal()
        {
            lock (_gate)
            {
                _count++;
                foreach ((int count, TaskCompletionSource completion) in _waiters)
                {
                    if (_count >= count)
                    {
                        completion.TrySetResult();
                    }
                }

                _waiters.RemoveAll(value => value.Completion.Task.IsCompleted);
            }
        }

        public Task WaitAsync(int count)
        {
            lock (_gate)
            {
                if (_count >= count)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, completion));
                return completion.Task;
            }
        }
    }

    private static UsageMetricSet EmptyMetricSet()
    {
        var empty = new MetricAggregate(null, 0, 0);
        return new UsageMetricSet(
            empty,
            empty,
            empty,
            empty,
            empty,
            empty,
            empty,
            empty,
            empty);
    }
}

internal sealed class StaDispatcherTestHost : IAsyncDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private readonly IsolatedDesktop? _isolatedDesktop;
    private Exception? _threadFailure;

    public StaDispatcherTestHost()
        : this(useIsolatedDesktop: false)
    {
    }

    private StaDispatcherTestHost(bool useIsolatedDesktop)
    {
        var ready = new TaskCompletionSource<(Dispatcher Dispatcher, string? DesktopName)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IsolatedDesktop? isolatedDesktop = null;
        _thread = new Thread(() =>
        {
            try
            {
                if (useIsolatedDesktop)
                {
                    isolatedDesktop = IsolatedDesktop.CreateForCurrentThread();
                }

                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                ready.SetResult((dispatcher, isolatedDesktop?.Name));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                if (!ready.TrySetException(exception))
                {
                    _threadFailure = exception;
                }
            }
        })
        {
            IsBackground = true,
            Name = useIsolatedDesktop
                ? "AgenTally.Tests.WindowedDesktop"
                : "AgenTally.Tests.Dispatcher"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        (Dispatcher dispatcher, string? desktopName) initialized;
        try
        {
            initialized = ready.Task.GetAwaiter().GetResult();
        }
        catch (Exception startupException)
        {
            _thread.Join();
            try
            {
                isolatedDesktop?.CloseChecked();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    startupException,
                    cleanupException);
            }

            throw;
        }

        (Dispatcher dispatcher, string? desktopName) = initialized;
        _dispatcher = dispatcher;
        _isolatedDesktop = isolatedDesktop;
        IsolatedDesktopName = desktopName;
    }

    public static StaDispatcherTestHost CreateWindowedDesktop() =>
        new(useIsolatedDesktop: true);

    public Dispatcher Dispatcher => _dispatcher;

    public string? IsolatedDesktopName { get; }

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

    public async Task InvokeAsync(Func<Task> action)
    {
        Task inner = await _dispatcher.InvokeAsync(action).Task;
        await inner;
    }

    public async Task<T> InvokeAsync<T>(Func<T> action) =>
        await _dispatcher.InvokeAsync(action).Task;

    public async ValueTask DisposeAsync()
    {
        if (!_dispatcher.HasShutdownStarted)
        {
            await _dispatcher.InvokeAsync(
                () => _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
        }

        await Task.Run(_thread.Join);
        try
        {
            _isolatedDesktop?.CloseChecked();
        }
        catch (Exception exception)
        {
            _threadFailure = _threadFailure is null
                ? exception
                : new AggregateException(_threadFailure, exception);
        }

        if (_threadFailure is not null)
        {
            throw new InvalidOperationException(
                "AgenTally test dispatcher thread did not shut down cleanly.",
                _threadFailure);
        }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class SequenceTimeProvider(params DateTimeOffset[] utcTimes) : TimeProvider
{
    private int _calls;

    public int CallCount => Volatile.Read(ref _calls);

    public override DateTimeOffset GetUtcNow()
    {
        int index = Interlocked.Increment(ref _calls) - 1;
        if (utcTimes.Length == 0)
        {
            throw new InvalidOperationException("测试时间序列不能为空。");
        }

        return utcTimes[Math.Min(index, utcTimes.Length - 1)];
    }
}

internal static class TestData
{
    public static DashboardQueryResult Dashboard(long total) => new(
        new UsageOverview(
            1,
            Aggregate(total),
            Aggregate(total),
            Aggregate(total),
            Aggregate(total),
            Aggregate(total),
            null),
        [],
        [],
        [],
        []);

    public static MetricAggregate Aggregate(long? value) => new(
        value,
        value.HasValue ? 1 : 0,
        value.HasValue ? 0 : 1);

    public static UsageMetricSet MetricSet(long? normalizedTotal)
    {
        var empty = new MetricAggregate(null, 0, 0);
        return new UsageMetricSet(
            empty,
            empty,
            empty,
            empty,
            empty,
            empty,
            empty,
            empty,
            Aggregate(normalizedTotal));
    }
}
