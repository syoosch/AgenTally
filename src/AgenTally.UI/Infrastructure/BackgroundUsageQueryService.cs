using AgenTally.Storage.Queries;

namespace AgenTally.UI.Infrastructure;

public interface IUsageQueryMaintenanceGate
{
    Task<IDisposable> PauseAsync(CancellationToken cancellationToken);
}

internal sealed class BackgroundUsageQueryService :
    IUsageQueryService,
    IUsageQueryMaintenanceGate
{
    private readonly IUsageQueryService _inner;
    private readonly SemaphoreSlim _queryGate = new(1, 1);

    public BackgroundUsageQueryService(IUsageQueryService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<UsageOverview> GetOverviewAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetOverviewAsync(filter, token),
            cancellationToken);

    public Task<IReadOnlyList<UsageTrendPoint>> GetTrendAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetTrendAsync(filter, token),
            cancellationToken);

    public Task<IReadOnlyList<UsageRecordRow>> GetRecentRecordsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetRecentRecordsAsync(filter, token),
            cancellationToken);

    public Task<IReadOnlyList<ModelUsageRow>> GetModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetModelsAsync(filter, token),
            cancellationToken);

    public Task<IReadOnlyList<AgentUsageRow>> GetAgentsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetAgentsAsync(filter, token),
            cancellationToken);

    public Task<IReadOnlyList<AgentModelUsageRow>> GetAgentModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetAgentModelsAsync(filter, token),
            cancellationToken);

    public Task<UsageFilterValues> GetFilterValuesAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetFilterValuesAsync(filter, token),
            cancellationToken);

    public Task<IReadOnlyList<SourceStatusRow>> GetSourcesAsync(
        CancellationToken cancellationToken) =>
        RunAsync(_inner.GetSourcesAsync, cancellationToken);

    public Task<IReadOnlyList<PriceSettingRow>> GetPriceSettingsAsync(
        CancellationToken cancellationToken) =>
        RunAsync(_inner.GetPriceSettingsAsync, cancellationToken);

    public Task<RootSessionPage> GetRootSessionsAsync(
        RootSessionPageRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetRootSessionsAsync(request, token),
            cancellationToken);

    public Task<RootSessionDetail?> GetRootSessionDetailAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetRootSessionDetailAsync(
                filter,
                identity,
                token),
            cancellationToken);

    public Task<IReadOnlyList<ProjectUsageRow>> GetProjectsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetProjectsAsync(filter, token),
            cancellationToken);

    public Task<TurnUsagePage> GetTurnsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetTurnsAsync(filter, identity, token),
            cancellationToken);

    public async Task<IDisposable> PauseAsync(
        CancellationToken cancellationToken)
    {
        await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new QueryPause(_queryGate);
    }

    public Task<IReadOnlyList<TurnCallUsageRow>> GetTurnCallsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        string turnIdHash,
        CancellationToken cancellationToken) =>
        RunAsync(
            token => _inner.GetTurnCallsAsync(
                filter,
                identity,
                turnIdHash,
                token),
            cancellationToken);

    private async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> query,
        CancellationToken cancellationToken)
    {
        await _queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => query(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _queryGate.Release();
        }
    }

    private sealed class QueryPause : IDisposable
    {
        private SemaphoreSlim? _gate;

        public QueryPause(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
