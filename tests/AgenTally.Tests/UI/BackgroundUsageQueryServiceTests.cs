using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
public sealed class BackgroundUsageQueryServiceTests
{
    private static readonly UsageFilter Filter = new(
        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero));

    [TestMethod]
    public async Task SynchronousInnerQuery_DoesNotBlockDispatcher()
    {
        await using var host = new StaDispatcherTestHost();
        using var inner = new BlockingUsageQueryService();
        var queries = new BackgroundUsageQueryService(inner);
        int dispatcherThreadId = await host.InvokeAsync(
            () => Environment.CurrentManagedThreadId);
        Task<UsageOverview>? query = null;

        Task invocation = host.InvokeAsync(() =>
            query = queries.GetOverviewAsync(Filter, CancellationToken.None));
        try
        {
            await invocation.WaitAsync(TimeSpan.FromSeconds(1));
            await inner.Started.WaitAsync(TimeSpan.FromSeconds(1));
            await host.InvokeAsync(() => { })
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsNotNull(query);
            Assert.IsFalse(query.IsCompleted);
            Assert.AreNotEqual(dispatcherThreadId, inner.QueryThreadId);
        }
        finally
        {
            inner.Release();
            await invocation;
            if (query is not null)
            {
                await query;
            }
        }
    }

    [TestMethod]
    public async Task CanceledQueuedQuery_DoesNotEnterInnerService()
    {
        using var inner = new BlockingUsageQueryService();
        var queries = new BackgroundUsageQueryService(inner);
        Task<UsageOverview> first = queries.GetOverviewAsync(
            Filter,
            CancellationToken.None);
        await inner.Started.WaitAsync(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        Task<UsageOverview> queued = queries.GetOverviewAsync(
            Filter,
            cancellation.Token);

        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await queued);
            Assert.AreEqual(1, inner.InvocationCount);
        }
        finally
        {
            inner.Release();
            await first;
        }
    }

    [TestMethod]
    public async Task MaintenancePause_WaitsForActiveQueryAndBlocksNewQueriesUntilReleased()
    {
        using var inner = new BlockingUsageQueryService();
        var queries = new BackgroundUsageQueryService(inner);
        Task<UsageOverview> active = queries.GetOverviewAsync(
            Filter,
            CancellationToken.None);
        await inner.Started.WaitAsync(TimeSpan.FromSeconds(1));
        Task<IDisposable> pauseTask = queries.PauseAsync(CancellationToken.None);
        Assert.IsFalse(pauseTask.IsCompleted);

        inner.Release();
        await active;
        IDisposable pause = await pauseTask.WaitAsync(TimeSpan.FromSeconds(1));
        Task<UsageOverview> queued = queries.GetOverviewAsync(
            Filter,
            CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(queued.IsCompleted);
        Assert.AreEqual(1, inner.InvocationCount);

        pause.Dispose();
        await queued.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, inner.InvocationCount);
    }

    private sealed class BlockingUsageQueryService :
        IUsageQueryService,
        IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;
        private int _queryThreadId;

        public Task Started => _started.Task;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public int QueryThreadId => Volatile.Read(ref _queryThreadId);

        public Task<UsageOverview> GetOverviewAsync(
            UsageFilter filter,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            Volatile.Write(
                ref _queryThreadId,
                Environment.CurrentManagedThreadId);
            _started.TrySetResult();
            _release.Wait(cancellationToken);
            return Task.FromResult<UsageOverview>(null!);
        }

        public Task<IReadOnlyList<UsageTrendPoint>> GetTrendAsync(
            UsageFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UsageRecordRow>> GetRecentRecordsAsync(
            UsageFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ModelUsageRow>> GetModelsAsync(
            UsageFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentUsageRow>> GetAgentsAsync(
            UsageFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentModelUsageRow>> GetAgentModelsAsync(
            UsageFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UsageFilterValues> GetFilterValuesAsync(
            UsageFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SourceStatusRow>> GetSourcesAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PriceSettingRow>> GetPriceSettingsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RootSessionPage> GetRootSessionsAsync(
            RootSessionPageRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RootSessionDetail?> GetRootSessionDetailAsync(
            UsageFilter filter,
            RootSessionIdentity identity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectUsageRow>> GetProjectsAsync(
            UsageFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TurnUsagePage> GetTurnsAsync(
            UsageFilter filter,
            RootSessionIdentity identity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TurnCallUsageRow>> GetTurnCallsAsync(
            UsageFilter filter,
            RootSessionIdentity identity,
            string turnIdHash,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }
}
