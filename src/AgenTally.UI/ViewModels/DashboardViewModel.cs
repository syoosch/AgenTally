using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;

namespace AgenTally.UI.ViewModels;

public sealed record UsageHeatmapDay(
    DateTime Date,
    long? TotalTokens,
    int AvailableRecords,
    int UnavailableRecords);

public sealed record UsageDaySelection(
    DateOnly Date,
    string? AgentId,
    string? Model,
    string Period = DashboardViewModel.ThirtyDays,
    DateTime? CustomStartDate = null,
    DateTime? CustomEndDate = null,
    string? ProjectId = null);

public sealed class DashboardViewModel : PageViewModel
{
    public const string AllAgents = "全部平台";
    public const string AllModels = "全部模型";
    public const string AllProjects = "全部项目";
    public const string AllTime = "全部时间";
    public const string Today = "今天";
    public const string SevenDays = "近 7 天";
    public const string ThirtyDays = "近 30 天";
    public const string NinetyDays = "近 90 天";
    public const string Custom = "自定义";

    private static readonly ReadOnlyCollection<string> SupportedPeriods =
        Array.AsReadOnly(
            [AllTime, Today, SevenDays, ThirtyDays, NinetyDays, Custom]);
    private readonly TimeZoneInfo _localTimeZone;
    private readonly StatisticsPeriodResolver _periodResolver;
    private readonly IUsageQueryService _queries;
    private readonly TimeProvider _timeProvider;
    private ObservableCollection<UsageSharePresentation> _agentRows = [];
    private ObservableCollection<string> _agentOptions = new([AllAgents]);
    private string _averageTokensText = "—";
    private string _cacheHitRateText = "—";
    private string _cacheReadText = "—";
    private string _cacheWriteText = "—";
    private DateTime? _customEndDate;
    private DateTime? _customStartDate;
    private DateTime? _customEndBeforePendingSelection;
    private DateTime? _customStartBeforePendingSelection;
    private ObservableCollection<UsageHeatmapDay> _heatmapDays = [];
    private ObservableCollection<UsageSharePresentation> _modelRows = [];
    private ObservableCollection<string> _modelOptions = new([AllModels]);
    private string _mostActiveDayText = "暂无使用记录";
    private string _outputText = "—";
    private string _periodSummaryText = string.Empty;
    private string _equivalentValueCaption = "尚未读取";
    private string _equivalentValueText = "—";
    private ObservableCollection<ProjectFilterOption> _projectOptions =
        UsageFilterPresentation.CreateProjectOptions([]);
    private string _requestCountText = "—";
    private string _selectedAgent = AllAgents;
    private string _selectedModel = AllModels;
    private string _selectedPeriod = ThirtyDays;
    private string? _periodBeforePendingCustomSelection;
    private string _selectedProject = AllProjects;
    private int _suppressFilterChanged;
    private bool _isCustomRangeSelectionPending;
    private StatisticsPeriodBounds? _lastEffectiveBounds;
    private string _totalTokensText = "—";
    private ObservableCollection<UsageTrendPoint> _trendPoints = [];
    private DateTimeOffset? _trendRangeEndExclusiveUtc;
    private DateTimeOffset? _trendRangeStartInclusiveUtc;
    private string _trendSubtitle = "每日 Token 总量与输出";
    private TrendGranularity _trendGranularity = TrendGranularity.Day;
    private string _uncachedInputText = "—";

    public DashboardViewModel(
        IUsageQueryService queries,
        Dispatcher dispatcher,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
        : base("概览", dispatcher)
    {
        ArgumentNullException.ThrowIfNull(queries);
        _queries = queries;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _periodResolver = new StatisticsPeriodResolver(_localTimeZone);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None));
        OpenDayCommand = new RelayCommand(OpenDay);
        CommitCustomRangeCommand = new RelayCommand(CommitCustomRange);
        CancelCustomRangeCommand = new RelayCommand(CancelCustomRangeSelection);
    }

    public event Action<UsageDaySelection>? DaySelected;

    public event EventHandler? FilterChanged;

    public IReadOnlyList<string> PeriodOptions => SupportedPeriods;

    public ObservableCollection<string> AgentOptions
    {
        get => _agentOptions;
        private set => SetProperty(ref _agentOptions, value);
    }

    public ObservableCollection<string> ModelOptions
    {
        get => _modelOptions;
        private set => SetProperty(ref _modelOptions, value);
    }

    public ObservableCollection<ProjectFilterOption> ProjectOptions
    {
        get => _projectOptions;
        private set => SetProperty(ref _projectOptions, value);
    }

    public string SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!SupportedPeriods.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            string previous = _selectedPeriod;
            if (SetProperty(ref _selectedPeriod, value))
            {
                OnPropertyChanged(nameof(IsCustomPeriod));
                if (Volatile.Read(ref _suppressFilterChanged) != 0)
                {
                    return;
                }

                if (value == Custom)
                {
                    BeginCustomRangeSelection(previous);
                    return;
                }

                AbandonPendingCustomRangeSelection();
                OnFilterChanged();
            }
        }
    }

    public bool IsCustomPeriod => SelectedPeriod == Custom;

    public DateTime? CustomStartDate
    {
        get => _customStartDate;
        set
        {
            if (SetProperty(ref _customStartDate, NormalizeNullableHour(value)) &&
                IsCustomPeriod &&
                !IsCustomRangeSelectionPending &&
                HasValidCustomRange())
            {
                OnFilterChanged();
            }
        }
    }

    public DateTime? CustomEndDate
    {
        get => _customEndDate;
        set
        {
            if (SetProperty(ref _customEndDate, NormalizeNullableHour(value)) &&
                IsCustomPeriod &&
                HasValidCustomRange())
            {
                IsCustomRangeSelectionPending = false;
                _periodBeforePendingCustomSelection = null;
                OnFilterChanged();
            }
        }
    }

    public string SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                SetProperty(ref _selectedAgent, value))
            {
                OnFilterChanged();
            }
        }
    }

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                SetProperty(ref _selectedModel, value))
            {
                OnFilterChanged();
            }
        }
    }

    public string SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                SetProperty(ref _selectedProject, value))
            {
                OnFilterChanged();
            }
        }
    }

    internal void ApplySynchronizedFilters(
        string period,
        string agent,
        string model,
        DateTime? customStartDate,
        DateTime? customEndDate,
        string project)
    {
        UiDispatcher.VerifyAccess();
        Interlocked.Increment(ref _suppressFilterChanged);
        try
        {
            IsCustomRangeSelectionPending = false;
            _periodBeforePendingCustomSelection = null;
            CustomStartDate = customStartDate;
            CustomEndDate = customEndDate;
            SelectedPeriod = period;
            SelectedAgent = agent;
            SelectedModel = model;
            SelectedProject = project;
        }
        finally
        {
            Interlocked.Decrement(ref _suppressFilterChanged);
        }
    }

    public string TotalTokensText
    {
        get => _totalTokensText;
        private set => SetProperty(ref _totalTokensText, value);
    }

    public string RequestCountText
    {
        get => _requestCountText;
        private set => SetProperty(ref _requestCountText, value);
    }

    public string AverageTokensText
    {
        get => _averageTokensText;
        private set => SetProperty(ref _averageTokensText, value);
    }

    public string UncachedInputText
    {
        get => _uncachedInputText;
        private set => SetProperty(ref _uncachedInputText, value);
    }

    public string OutputText
    {
        get => _outputText;
        private set => SetProperty(ref _outputText, value);
    }

    public string CacheReadText
    {
        get => _cacheReadText;
        private set => SetProperty(ref _cacheReadText, value);
    }

    public string CacheHitRateText
    {
        get => _cacheHitRateText;
        private set => SetProperty(ref _cacheHitRateText, value);
    }

    public string CacheWriteText
    {
        get => _cacheWriteText;
        private set => SetProperty(ref _cacheWriteText, value);
    }

    public string EquivalentValueText
    {
        get => _equivalentValueText;
        private set => SetProperty(ref _equivalentValueText, value);
    }

    public string EquivalentValueCaption
    {
        get => _equivalentValueCaption;
        private set => SetProperty(ref _equivalentValueCaption, value);
    }

    public string PeriodSummaryText
    {
        get => _periodSummaryText;
        private set => SetProperty(ref _periodSummaryText, value);
    }

    public string TrendSubtitle
    {
        get => _trendSubtitle;
        private set => SetProperty(ref _trendSubtitle, value);
    }

    public TrendGranularity TrendGranularity
    {
        get => _trendGranularity;
        private set => SetProperty(ref _trendGranularity, value);
    }

    public string MostActiveDayText
    {
        get => _mostActiveDayText;
        private set => SetProperty(ref _mostActiveDayText, value);
    }

    public ObservableCollection<UsageTrendPoint> TrendPoints
    {
        get => _trendPoints;
        private set => SetProperty(ref _trendPoints, value);
    }

    public DateTimeOffset? TrendRangeStartInclusiveUtc
    {
        get => _trendRangeStartInclusiveUtc;
        private set => SetProperty(ref _trendRangeStartInclusiveUtc, value);
    }

    public DateTimeOffset? TrendRangeEndExclusiveUtc
    {
        get => _trendRangeEndExclusiveUtc;
        private set => SetProperty(ref _trendRangeEndExclusiveUtc, value);
    }

    public ObservableCollection<UsageHeatmapDay> HeatmapDays
    {
        get => _heatmapDays;
        private set => SetProperty(ref _heatmapDays, value);
    }

    public ObservableCollection<UsageSharePresentation> ModelRows
    {
        get => _modelRows;
        private set => SetProperty(ref _modelRows, value);
    }

    public ObservableCollection<UsageSharePresentation> AgentRows
    {
        get => _agentRows;
        private set => SetProperty(ref _agentRows, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand OpenDayCommand { get; }

    public RelayCommand CommitCustomRangeCommand { get; }

    public RelayCommand CancelCustomRangeCommand { get; }

    public bool IsCustomRangeSelectionPending
    {
        get => _isCustomRangeSelectionPending;
        private set => SetProperty(ref _isCustomRangeSelectionPending, value);
    }

    public TimeZoneInfo LocalTimeZone => _localTimeZone;

    protected override async Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool showFeedback)
    {
        RefreshSession session = BeginRefresh(cancellationToken);
        await SetRefreshStartedAsync(session, showFeedback);
        try
        {
            FilterSelection selection = await ReadOnDispatcherAsync(
                () => new FilterSelection(
                    SelectedPeriod,
                    SelectedAgent,
                    SelectedModel,
                    SelectedProject,
                    CustomStartDate,
                    CustomEndDate));
            DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
            StatisticsPeriodBounds bounds = _periodResolver.Resolve(
                selection.Period,
                nowUtc,
                selection.CustomStartDate,
                selection.CustomEndDate);
            StatisticsPeriodBounds heatmapBounds = CreateHeatmapBounds(nowUtc);
            UsageFilter filter = CreateFilter(selection, bounds);
            UsageFilter heatmapFilter = CreateFilter(selection, heatmapBounds);

            Task<UsageOverview> overviewTask =
                _queries.GetOverviewAsync(filter, session.Token);
            Task<IReadOnlyList<UsageTrendPoint>> trendTask =
                _queries.GetTrendAsync(filter, session.Token);
            Task<IReadOnlyList<UsageTrendPoint>> heatmapTask =
                _queries.GetTrendAsync(heatmapFilter, session.Token);
            Task<IReadOnlyList<ModelUsageRow>> modelsTask =
                _queries.GetModelsAsync(filter, session.Token);
            Task<IReadOnlyList<AgentUsageRow>> agentsTask =
                _queries.GetAgentsAsync(filter, session.Token);
            Task<UsageFilterValues> filtersTask =
                _queries.GetFilterValuesAsync(filter, session.Token);

            await Task.WhenAll(
                overviewTask,
                trendTask,
                heatmapTask,
                modelsTask,
                agentsTask,
                filtersTask);

            StatisticsPeriodBounds effectiveBounds = ResolveEffectiveBounds(
                selection,
                bounds,
                overviewTask.Result,
                trendTask.Result);
            IReadOnlyList<UsageTrendPoint> trend =
                selection.Period == AllTime &&
                overviewTask.Result.RequestCount == 0
                    ? []
                    : AggregateTrend(
                        trendTask.Result,
                        GetTrendBucket(selection, effectiveBounds),
                        effectiveBounds);
            bool filterReset = false;
            await ApplyIfCurrentAsync(session, () => ApplySnapshot(
                overviewTask.Result,
                trend,
                CreateHeatmap(heatmapTask.Result, heatmapBounds),
                modelsTask.Result,
                agentsTask.Result,
                filtersTask.Result,
                selection,
                effectiveBounds,
                out filterReset));
            if (filterReset)
            {
                await RunOnDispatcherAsync(OnFilterChanged);
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetRefreshFailureAsync(session, exception);
        }
        finally
        {
            await EndRefreshAsync(session);
        }
    }

    private StatisticsPeriodBounds ResolveEffectiveBounds(
        FilterSelection selection,
        StatisticsPeriodBounds queryBounds,
        UsageOverview overview,
        IReadOnlyList<UsageTrendPoint> trend)
    {
        if (selection.Period != AllTime)
        {
            return queryBounds;
        }

        DateTimeOffset? first = overview.FirstOccurredAtUtc ??
            trend.OrderBy(point => point.BucketStartUtc)
                .Select(point => (DateTimeOffset?)point.BucketStartUtc)
                .FirstOrDefault();
        DateTime localStart = first is DateTimeOffset value
            ? TimeZoneInfo.ConvertTime(value, _localTimeZone).Date
            : queryBounds.LocalEndExclusive.AddDays(-1);
        return _periodResolver.CreateBounds(
            localStart,
            queryBounds.LocalEndExclusive);
    }

    private StatisticsPeriodBounds CreateHeatmapBounds(DateTimeOffset nowUtc)
    {
        DateTime localEnd =
            TimeZoneInfo.ConvertTime(nowUtc, _localTimeZone).Date.AddDays(1);
        return _periodResolver.CreateBounds(
            localEnd.AddMonths(-12),
            localEnd);
    }

    private static UsageFilter CreateFilter(
        FilterSelection selection,
        StatisticsPeriodBounds bounds) => new(
            bounds.StartInclusiveUtc,
            bounds.EndExclusiveUtc,
            selection.Agent == AllAgents ? null : selection.Agent,
            selection.Model == AllModels ? null : selection.Model,
            limit: 200,
            projectId: selection.Project == AllProjects
                ? null
                : selection.Project);

    private void ApplySnapshot(
        UsageOverview overview,
        IReadOnlyList<UsageTrendPoint> trend,
        IReadOnlyList<UsageHeatmapDay> heatmap,
        IReadOnlyList<ModelUsageRow> models,
        IReadOnlyList<AgentUsageRow> agents,
        UsageFilterValues filterValues,
        FilterSelection selection,
        StatisticsPeriodBounds bounds,
        out bool filterReset)
    {
        filterReset = false;
        _lastEffectiveBounds = bounds;
        TrendRangeStartInclusiveUtc = bounds.StartInclusiveUtc;
        TrendRangeEndExclusiveUtc = bounds.EndExclusiveUtc;
        TotalTokensText = FormatMetric(overview.NormalizedTotal.Value);
        RequestCountText = overview.RequestCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        AverageTokensText = overview.RequestCount > 0 &&
            overview.NormalizedTotal.Value is long total
            ? FormatCompact(total / overview.RequestCount)
            : "—";
        UncachedInputText = FormatMetric(overview.UncachedInput.Value);
        OutputText = FormatMetric(overview.Output.Value);
        CacheReadText = FormatMetric(overview.CacheRead.Value);
        CacheWriteText = FormatMetric(overview.CacheWrite.Value);
        CacheHitRateText = CalculateCacheHitRate(overview);
        PricePresentation price =
            PricePresentationFormatter.Describe(overview.Pricing);
        EquivalentValueText = price.ValueText;
        EquivalentValueCaption = price.Caption;
        SetCollectionIfChanged(ref _trendPoints, trend, nameof(TrendPoints));
        SetCollectionIfChanged(ref _heatmapDays, heatmap, nameof(HeatmapDays));
        SetCollectionIfChanged(
            ref _modelRows,
            models.Take(4).Select(row => new UsageSharePresentation(
                row.Model,
                row.Model,
                row.NormalizedTotal.Value,
                overview.NormalizedTotal.Value)),
            nameof(ModelRows));
        SetCollectionIfChanged(
            ref _agentRows,
            agents.Take(4).Select(row => new UsageSharePresentation(
                row.AgentId,
                row.AgentId,
                row.NormalizedTotal.Value,
                overview.NormalizedTotal.Value)),
            nameof(AgentRows));
        PeriodSummaryText = FormatPeriodSummary(
            selection.Period,
            bounds,
            overview.RequestCount > 0);
        TrendGranularity = GetTrendBucket(selection, bounds);
        TrendSubtitle = TrendGranularity switch
        {
            TrendGranularity.Hour => "每小时 Token 总量与输出",
            TrendGranularity.Week => "每周 Token 总量与输出",
            _ => "每日 Token 总量与输出"
        };
        UsageHeatmapDay? mostActive = heatmap
            .Where(day => day.TotalTokens.HasValue)
            .OrderByDescending(day => day.TotalTokens)
            .FirstOrDefault();
        MostActiveDayText = mostActive is null
            ? "暂无使用记录"
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{mostActive.Date:M月d日} · {FormatCompact(mostActive.TotalTokens!.Value)}");

        SetCollectionIfChanged(
            ref _agentOptions,
            CreateOptions(AllAgents, filterValues.AgentIds),
            nameof(AgentOptions));
        SetCollectionIfChanged(
            ref _modelOptions,
            CreateOptions(AllModels, filterValues.Models),
            nameof(ModelOptions));
        SetCollectionIfChanged(
            ref _projectOptions,
            UsageFilterPresentation.CreateProjectOptions(filterValues.Projects),
            nameof(ProjectOptions));
        if (!AgentOptions.Contains(SelectedAgent))
        {
            filterReset = SetProperty(
                ref _selectedAgent,
                AllAgents,
                nameof(SelectedAgent));
        }

        if (!ModelOptions.Contains(SelectedModel))
        {
            filterReset = SetProperty(
                ref _selectedModel,
                AllModels,
                nameof(SelectedModel)) || filterReset;
        }

        if (!ProjectOptions.Any(option =>
                string.Equals(
                    option.SelectionValue,
                    SelectedProject,
                    StringComparison.Ordinal)))
        {
            filterReset = SetProperty(
                ref _selectedProject,
                AllProjects,
                nameof(SelectedProject)) || filterReset;
        }
    }

    private IReadOnlyList<UsageTrendPoint> AggregateTrend(
        IReadOnlyList<UsageTrendPoint> source,
        TrendGranularity bucket,
        StatisticsPeriodBounds bounds)
    {
        Dictionary<DateTimeOffset, UsageTrendPoint> observed = source
            .GroupBy(point => GetBucketStart(point.BucketStartUtc, bucket))
            .Where(group => group.Key < bounds.EndExclusiveUtc)
            .ToDictionary(
                group => group.Key,
                group => new UsageTrendPoint(
                    group.Key,
                    Sum(group.Select(point => point.NormalizedTotal)),
                    Sum(group.Select(point => point.UncachedInput)),
                    Sum(group.Select(point => point.Output)),
                    Sum(group.Select(point => point.CacheRead)),
                    Sum(group.Select(point => point.CacheWrite)),
                    group.Sum(point => point.RequestCount))
                {
                    Pricing = SumPricing(group.Select(point => point.Pricing))
                });
        var zero = new MetricAggregate(0, 0, 0);
        return CreateTrendBucketStarts(bucket, bounds)
            .Select(bucketStart => observed.TryGetValue(
                bucketStart,
                out UsageTrendPoint? point)
                ? point
                : new UsageTrendPoint(
                    bucketStart,
                    zero,
                    zero,
                    zero,
                    zero,
                    zero,
                    0))
            .ToArray();
    }

    private IReadOnlyList<DateTimeOffset> CreateTrendBucketStarts(
        TrendGranularity bucket,
        StatisticsPeriodBounds bounds)
    {
        if (bucket == TrendGranularity.Hour)
        {
            var utcHours = new List<DateTimeOffset>();
            DateTime utcStart = bounds.StartInclusiveUtc.UtcDateTime;
            DateTimeOffset firstHour = new(
                utcStart.Ticks - (utcStart.Ticks % TimeSpan.TicksPerHour),
                TimeSpan.Zero);
            for (DateTimeOffset hour = firstHour;
                 hour < bounds.EndExclusiveUtc;
                 hour = hour.AddHours(1))
            {
                utcHours.Add(hour);
            }

            return utcHours;
        }

        var starts = new List<DateTimeOffset>();
        DateTime local = bucket switch
        {
            TrendGranularity.Day => bounds.LocalStart.Date,
            TrendGranularity.Week => bounds.LocalStart.Date.AddDays(
                -(((int)bounds.LocalStart.DayOfWeek + 6) % 7)),
            _ => throw new ArgumentOutOfRangeException(nameof(bucket))
        };
        while (local < bounds.LocalEndExclusive)
        {
            starts.Add(new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
                    _localTimeZone),
                TimeSpan.Zero));
            local = bucket switch
            {
                TrendGranularity.Day => local.AddDays(1),
                TrendGranularity.Week => local.AddDays(7),
                _ => throw new ArgumentOutOfRangeException(nameof(bucket))
            };
        }

        return starts;
    }

    private IReadOnlyList<UsageHeatmapDay> CreateHeatmap(
        IReadOnlyList<UsageTrendPoint> source,
        StatisticsPeriodBounds bounds)
    {
        Dictionary<DateTime, MetricAggregate> byDay = source
            .GroupBy(point =>
                TimeZoneInfo.ConvertTime(point.BucketStartUtc, _localTimeZone).Date)
            .ToDictionary(
                group => group.Key,
                group => Sum(group.Select(point => point.NormalizedTotal)));
        var days = new List<UsageHeatmapDay>();
        for (DateTime day = bounds.LocalStart;
             day < bounds.LocalEndExclusive;
             day = day.AddDays(1))
        {
            if (byDay.TryGetValue(day, out MetricAggregate? aggregate))
            {
                days.Add(new UsageHeatmapDay(
                    day,
                    aggregate.Value,
                    aggregate.AvailableRecords,
                    aggregate.UnavailableRecords));
            }
            else
            {
                days.Add(new UsageHeatmapDay(day, null, 0, 0));
            }
        }

        return days;
    }

    private DateTimeOffset GetBucketStart(
        DateTimeOffset bucketStartUtc,
        TrendGranularity bucket)
    {
        if (bucket == TrendGranularity.Hour)
        {
            DateTime utc = bucketStartUtc.UtcDateTime;
            return new DateTimeOffset(
                utc.Ticks - (utc.Ticks % TimeSpan.TicksPerHour),
                TimeSpan.Zero);
        }

        DateTime local = TimeZoneInfo.ConvertTime(
            bucketStartUtc,
            _localTimeZone).DateTime;
        DateTime localBucket = bucket switch
        {
            TrendGranularity.Day => local.Date,
            TrendGranularity.Week => local.Date.AddDays(
                -(((int)local.DayOfWeek + 6) % 7)),
            _ => throw new ArgumentOutOfRangeException(nameof(bucket))
        };
        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(localBucket, _localTimeZone),
            TimeSpan.Zero);
    }

    private static MetricAggregate Sum(IEnumerable<MetricAggregate> values)
    {
        MetricAggregate[] items = values.ToArray();
        int available = items.Sum(item => item.AvailableRecords);
        int unavailable = items.Sum(item => item.UnavailableRecords);
        int unknown = items.Sum(item => item.UnknownRecords);
        long? total = available == 0
            ? null
            : items.Where(item => item.Value.HasValue)
                .Sum(item => item.Value!.Value);
        return new MetricAggregate(total, available, unavailable, unknown);
    }

    private static PricingAggregate? SumPricing(
        IEnumerable<PricingAggregate?> values)
    {
        PricingAggregate?[] source = values.ToArray();
        if (source.Length == 0 || source.Any(static value => value is null))
        {
            return null;
        }

        PricingAggregate[] items = source
            .Select(static value => value!)
            .ToArray();

        decimal? amount = items.Any(static item => item.KnownAmountUsd.HasValue)
            ? items.Where(static item => item.KnownAmountUsd.HasValue)
                .Sum(static item => item.KnownAmountUsd!.Value)
            : null;
        PricingMissingCategory missing = items.Aggregate(
            PricingMissingCategory.None,
            static (current, item) => current | item.MissingCategories);
        return new PricingAggregate(
            amount,
            items.Sum(static item => item.CompleteRecords),
            items.Sum(static item => item.PartialRecords),
            items.Sum(static item => item.UnpricedRecords),
            missing);
    }

    private static TrendGranularity GetTrendBucket(
        FilterSelection selection,
        StatisticsPeriodBounds bounds)
    {
        if (bounds.Elapsed <= TimeSpan.FromHours(72))
        {
            return TrendGranularity.Hour;
        }

        return selection.Period is Custom or AllTime &&
            bounds.Elapsed > TimeSpan.FromDays(90)
            ? TrendGranularity.Week
            : TrendGranularity.Day;
    }

    private static ObservableCollection<string> CreateOptions(
        string allOption,
        IReadOnlyList<string> values)
    {
        var options = new ObservableCollection<string> { allOption };
        foreach (string value in values.Distinct(StringComparer.Ordinal))
        {
            options.Add(value);
        }

        return options;
    }

    private static string FormatMetric(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    private static string FormatCompact(long value) => value switch
    {
        >= 1_000_000_000 => string.Create(
            CultureInfo.InvariantCulture,
            $"{value / 1_000_000_000d:0.#}B"),
        >= 1_000_000 => string.Create(
            CultureInfo.InvariantCulture,
            $"{value / 1_000_000d:0.#}M"),
        >= 1_000 => string.Create(
            CultureInfo.InvariantCulture,
            $"{value / 1_000d:0.#}K"),
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    private static string FormatPeriodSummary(
        string period,
        StatisticsPeriodBounds bounds,
        bool hasRecords)
    {
        if (period == Custom)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{bounds.LocalStart:yyyy年M月d日 HH:00}至{bounds.LocalEndExclusive:yyyy年M月d日 HH:00}");
        }

        DateTime endInclusive = bounds.LocalEndExclusive.AddDays(-1);
        if (period == AllTime && !hasRecords)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"截至 {endInclusive:yyyy年M月d日}");
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{bounds.LocalStart:yyyy年M月d日}至{endInclusive:M月d日}");
    }

    private static string CalculateCacheHitRate(UsageOverview overview)
    {
        if (overview.RequestCount <= 0 ||
            overview.UncachedInput.AvailableRecords != overview.RequestCount ||
            overview.CacheRead.AvailableRecords != overview.RequestCount ||
            overview.UncachedInput.UnavailableRecords != 0 ||
            overview.CacheRead.UnavailableRecords != 0 ||
            overview.UncachedInput.Value is not long uncachedInput ||
            overview.CacheRead.Value is not long cacheRead ||
            uncachedInput < 0 ||
            cacheRead < 0)
        {
            return "—";
        }

        decimal denominator = (decimal)uncachedInput + cacheRead;
        return denominator > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.0}%",
                ((decimal)cacheRead / denominator) * 100)
            : "—";
    }

    private void OpenDay(object? parameter)
    {
        if (parameter is not UsageHeatmapDay day)
        {
            return;
        }

        DaySelected?.Invoke(new UsageDaySelection(
            DateOnly.FromDateTime(day.Date),
            SelectedAgent == AllAgents ? null : SelectedAgent,
            SelectedModel == AllModels ? null : SelectedModel,
            SelectedPeriod,
            CustomStartDate,
            CustomEndDate,
            SelectedProject == AllProjects ? null : SelectedProject));
    }

    private void BeginCustomRangeSelection(string previousPeriod)
    {
        _periodBeforePendingCustomSelection = previousPeriod;
        _customStartBeforePendingSelection = _customStartDate;
        _customEndBeforePendingSelection = _customEndDate;
        CustomTimeRange seed = HasValidCustomRange()
            ? new CustomTimeRange(
                _customStartDate!.Value,
                _customEndDate!.Value)
            : _periodResolver.CreateDraftSeed(
                previousPeriod,
                _timeProvider.GetUtcNow(),
                _lastEffectiveBounds);
        SetProperty(
            ref _customStartDate,
            seed.StartLocal,
            nameof(CustomStartDate));
        SetProperty(
            ref _customEndDate,
            seed.EndExclusiveLocal,
            nameof(CustomEndDate));
        IsCustomRangeSelectionPending = true;
    }

    private void CommitCustomRange(object? parameter)
    {
        if (parameter is not CustomTimeRange range)
        {
            return;
        }

        SetProperty(
            ref _customStartDate,
            range.StartLocal,
            nameof(CustomStartDate));
        SetProperty(
            ref _customEndDate,
            range.EndExclusiveLocal,
            nameof(CustomEndDate));
        IsCustomRangeSelectionPending = false;
        _periodBeforePendingCustomSelection = null;
        OnFilterChanged();
    }

    private void CancelCustomRangeSelection()
    {
        if (!IsCustomRangeSelectionPending)
        {
            return;
        }

        string previousPeriod = _periodBeforePendingCustomSelection ?? ThirtyDays;
        SetProperty(
            ref _customStartDate,
            _customStartBeforePendingSelection,
            nameof(CustomStartDate));
        SetProperty(
            ref _customEndDate,
            _customEndBeforePendingSelection,
            nameof(CustomEndDate));
        SetProperty(ref _selectedPeriod, previousPeriod, nameof(SelectedPeriod));
        OnPropertyChanged(nameof(IsCustomPeriod));
        IsCustomRangeSelectionPending = false;
        _periodBeforePendingCustomSelection = null;
    }

    private void AbandonPendingCustomRangeSelection()
    {
        if (!IsCustomRangeSelectionPending)
        {
            return;
        }

        SetProperty(
            ref _customStartDate,
            _customStartBeforePendingSelection,
            nameof(CustomStartDate));
        SetProperty(
            ref _customEndDate,
            _customEndBeforePendingSelection,
            nameof(CustomEndDate));
        IsCustomRangeSelectionPending = false;
        _periodBeforePendingCustomSelection = null;
    }

    private bool HasValidCustomRange()
    {
        if (_customStartDate is not DateTime start ||
            _customEndDate is not DateTime end)
        {
            return false;
        }

        try
        {
            _ = new CustomTimeRange(start, end);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static DateTime? NormalizeNullableHour(DateTime? value)
    {
        if (value is not DateTime date)
        {
            return null;
        }

        return new CustomTimeRange(
            DateTime.SpecifyKind(date, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(date, DateTimeKind.Unspecified).AddHours(1))
            .StartLocal;
    }

    private void OnFilterChanged()
    {
        if (Volatile.Read(ref _suppressFilterChanged) != 0)
        {
            return;
        }

        CancelRefresh();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record FilterSelection(
        string Period,
        string Agent,
        string Model,
        string Project,
        DateTime? CustomStartDate,
        DateTime? CustomEndDate);

}
