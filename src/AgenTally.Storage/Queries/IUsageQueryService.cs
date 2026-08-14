namespace AgenTally.Storage.Queries;

public interface IUsageQueryService
{
    Task<UsageOverview> GetOverviewAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UsageTrendPoint>> GetTrendAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UsageRecordRow>> GetRecentRecordsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ModelUsageRow>> GetModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentUsageRow>> GetAgentsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentModelUsageRow>> GetAgentModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<UsageFilterValues> GetFilterValuesAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceStatusRow>> GetSourcesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PriceSettingRow>> GetPriceSettingsAsync(
        CancellationToken cancellationToken);

    Task<RootSessionPage> GetRootSessionsAsync(
        RootSessionPageRequest request,
        CancellationToken cancellationToken);

    Task<RootSessionDetail?> GetRootSessionDetailAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectUsageRow>> GetProjectsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken);

    Task<TurnUsagePage> GetTurnsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TurnCallUsageRow>> GetTurnCallsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        string turnIdHash,
        CancellationToken cancellationToken);
}
