using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;

namespace AgenTally.UI.ViewModels;

public sealed record AnalysisDailyUsageRow(
    DateOnly Date,
    string DateText,
    string TotalTokensText,
    string RequestCountText,
    string UncachedInputText,
    string OutputText,
    string CacheReadText,
    string CacheHitRateText);

public sealed record AnalysisAgentUsageRow(
    string AgentId,
    string TotalTokensText,
    string ShareText,
    string RequestCountText,
    string UncachedInputText,
    string OutputText,
    string CacheReadText);

public sealed record AnalysisModelUsageRow(
    string Model,
    string AgentId,
    string TotalTokensText,
    string ShareText,
    string RequestCountText,
    string UncachedInputText,
    string OutputText,
    string CacheReadText);

public sealed class AnalysisViewModel : PageViewModel
{
    private static readonly ReadOnlyCollection<string> SupportedPeriods =
        Array.AsReadOnly(
        [
            DashboardViewModel.AllTime,
            DashboardViewModel.Today,
            DashboardViewModel.SevenDays,
            DashboardViewModel.ThirtyDays,
            DashboardViewModel.NinetyDays,
            DashboardViewModel.Custom
        ]);

    private readonly TimeZoneInfo _localTimeZone;
    private readonly StatisticsPeriodResolver _periodResolver;
    private readonly IUsageQueryService _queries;
    private readonly TimeProvider _timeProvider;
    private ObservableCollection<AnalysisAgentUsageRow> _agentRows = [];
    private ObservableCollection<string> _agentOptions =
        new([DashboardViewModel.AllAgents]);
    private DateTime? _customEndDate;
    private DateTime? _customStartDate;
    private DateTime? _customEndBeforePendingSelection;
    private DateTime? _customStartBeforePendingSelection;
    private string _dailyAverageText = "—";
    private ObservableCollection<AnalysisDailyUsageRow> _dailyRows = [];
    private ObservableCollection<AnalysisModelUsageRow> _modelRows = [];
    private ObservableCollection<string> _modelOptions =
        new([DashboardViewModel.AllModels]);
    private DateOnly? _pinnedDate;
    private string _periodSummaryText = string.Empty;
    private string _equivalentValueCaption = "尚未读取";
    private string _equivalentValueText = "—";
    private ObservableCollection<ProjectFilterOption> _projectOptions =
        UsageFilterPresentation.CreateProjectOptions([]);
    private string _requestCountText = "—";
    private string _selectedAgent = DashboardViewModel.AllAgents;
    private AnalysisDailyUsageRow? _selectedDailyRow;
    private string _selectedModel = DashboardViewModel.AllModels;
    private string _selectedPeriod = DashboardViewModel.ThirtyDays;
    private string? _periodBeforePendingCustomSelection;
    private string _selectedProject = DashboardViewModel.AllProjects;
    private int _selectedViewIndex;
    private int _suppressFilterChanged;
    private bool _isCustomRangeSelectionPending;
    private StatisticsPeriodBounds? _lastEffectiveBounds;
    private string _totalTokensText = "—";

    public AnalysisViewModel(
        IUsageQueryService queries,
        Dispatcher dispatcher,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
        : base("分析", dispatcher)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _periodResolver = new StatisticsPeriodResolver(_localTimeZone);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None));
        ClearPinnedDateCommand = new RelayCommand(
            _ => ClearPinnedDate(),
            _ => HasPinnedDate);
        CommitCustomRangeCommand = new RelayCommand(CommitCustomRange);
        CancelCustomRangeCommand = new RelayCommand(CancelCustomRangeSelection);
    }

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
            if (string.IsNullOrWhiteSpace(value) ||
                !SupportedPeriods.Contains(value, StringComparer.Ordinal))
            {
                return;
            }

            string previous = _selectedPeriod;
            if (SetProperty(ref _selectedPeriod, value))
            {
                OnPropertyChanged(nameof(IsCustomPeriod));
                if (Volatile.Read(ref _suppressFilterChanged) != 0)
                {
                    return;
                }

                if (value == DashboardViewModel.Custom)
                {
                    BeginCustomRangeSelection(previous);
                    return;
                }

                AbandonPendingCustomRangeSelection();
                OnFilterChanged();
            }
        }
    }

    public bool IsCustomPeriod => SelectedPeriod == DashboardViewModel.Custom;

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

    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set => SetProperty(ref _selectedViewIndex, Math.Clamp(value, 0, 2));
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

    public string DailyAverageText
    {
        get => _dailyAverageText;
        private set => SetProperty(ref _dailyAverageText, value);
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

    public bool HasPinnedDate => _pinnedDate.HasValue;

    public string PinnedDateText => _pinnedDate is DateOnly date
        ? date.ToString("已锁定 yyyy年M月d日", CultureInfo.CurrentCulture)
        : "平台与模型显示整个时间范围";

    public string SelectionText => _pinnedDate is DateOnly date
        ? date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture)
        : "尚未选择具体日期";

    public string ContextText => HasPinnedDate
        ? $"{SelectedAgent} · {SelectedModel} · 每日细目"
        : "每日、平台与模型用量";

    public ObservableCollection<AnalysisDailyUsageRow> DailyRows
    {
        get => _dailyRows;
        private set => SetProperty(ref _dailyRows, value);
    }

    public ObservableCollection<AnalysisAgentUsageRow> AgentRows
    {
        get => _agentRows;
        private set => SetProperty(ref _agentRows, value);
    }

    public ObservableCollection<AnalysisModelUsageRow> ModelRows
    {
        get => _modelRows;
        private set => SetProperty(ref _modelRows, value);
    }

    public AnalysisDailyUsageRow? SelectedDailyRow
    {
        get => _selectedDailyRow;
        set
        {
            if (SetProperty(ref _selectedDailyRow, value) && value is not null)
            {
                PinDate(value.Date);
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ClearPinnedDateCommand { get; }

    public RelayCommand CommitCustomRangeCommand { get; }

    public RelayCommand CancelCustomRangeCommand { get; }

    public bool IsCustomRangeSelectionPending
    {
        get => _isCustomRangeSelectionPending;
        private set => SetProperty(ref _isCustomRangeSelectionPending, value);
    }

    public TimeZoneInfo LocalTimeZone => _localTimeZone;

    public void ApplyDashboardFilter(
        string period,
        string agent,
        string model,
        DateTime? customStartDate,
        DateTime? customEndDate,
        string project = DashboardViewModel.AllProjects)
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

    public void SelectDay(UsageDaySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        UiDispatcher.VerifyAccess();
        ApplyDashboardFilter(
            selection.Period,
            selection.AgentId ?? DashboardViewModel.AllAgents,
            selection.Model ?? DashboardViewModel.AllModels,
            selection.CustomStartDate,
            selection.CustomEndDate,
            selection.ProjectId ?? DashboardViewModel.AllProjects);
        SelectedViewIndex = 0;
        SetPinnedDate(selection.Date);
    }

    protected override async Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool showFeedback)
    {
        RefreshSession session = BeginRefresh(cancellationToken);
        await SetRefreshStartedAsync(session, showFeedback);
        try
        {
            FilterSelection selection = await ReadOnDispatcherAsync(() => new FilterSelection(
                SelectedPeriod,
                SelectedAgent,
                SelectedModel,
                SelectedProject,
                CustomStartDate,
                CustomEndDate,
                _pinnedDate));
            StatisticsPeriodBounds bounds = _periodResolver.Resolve(
                selection.Period,
                _timeProvider.GetUtcNow(),
                selection.CustomStartDate,
                selection.CustomEndDate);
            DateOnly? pinnedDate = IsInside(selection.PinnedDate, bounds)
                ? selection.PinnedDate
                : null;
            UsageFilter rangeFilter = CreateFilter(selection, bounds);
            UsageFilter breakdownFilter = pinnedDate is DateOnly date
                ? CreateFilter(selection, CreateDayBounds(date, bounds))
                : rangeFilter;

            Task<UsageOverview> overviewTask =
                _queries.GetOverviewAsync(rangeFilter, session.Token);
            Task<IReadOnlyList<UsageTrendPoint>> trendTask =
                _queries.GetTrendAsync(rangeFilter, session.Token);
            Task<IReadOnlyList<AgentUsageRow>> agentsTask =
                _queries.GetAgentsAsync(breakdownFilter, session.Token);
            Task<IReadOnlyList<AgentModelUsageRow>> modelsTask =
                _queries.GetAgentModelsAsync(breakdownFilter, session.Token);
            Task<UsageFilterValues> filtersTask =
                _queries.GetFilterValuesAsync(rangeFilter, session.Token);

            await Task.WhenAll(
                overviewTask,
                trendTask,
                agentsTask,
                modelsTask,
                filtersTask);

            StatisticsPeriodBounds effectiveBounds = ResolveEffectiveBounds(
                selection,
                bounds,
                overviewTask.Result,
                trendTask.Result);
            DateOnly? effectivePinnedDate = IsInside(
                selection.PinnedDate,
                effectiveBounds)
                ? selection.PinnedDate
                : null;
            bool filterReset = false;
            await ApplyIfCurrentAsync(session, () => ApplySnapshot(
                overviewTask.Result,
                trendTask.Result,
                agentsTask.Result,
                modelsTask.Result,
                filtersTask.Result,
                selection,
                effectiveBounds,
                effectivePinnedDate,
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

    private void ApplySnapshot(
        UsageOverview overview,
        IReadOnlyList<UsageTrendPoint> trend,
        IReadOnlyList<AgentUsageRow> agents,
        IReadOnlyList<AgentModelUsageRow> models,
        UsageFilterValues filterValues,
        FilterSelection selection,
        StatisticsPeriodBounds bounds,
        DateOnly? pinnedDate,
        out bool filterReset)
    {
        filterReset = false;
        _lastEffectiveBounds = bounds;
        TotalTokensText = FormatMetric(overview.NormalizedTotal.Value);
        RequestCountText = overview.RequestCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        int dayCount = GetTouchedDayCount(bounds);
        DailyAverageText = overview.NormalizedTotal.Value is long total && dayCount > 0
            ? (total / (decimal)dayCount).ToString("N0", CultureInfo.InvariantCulture)
            : "—";
        PricePresentation price =
            PricePresentationFormatter.Describe(overview.Pricing);
        EquivalentValueText = price.ValueText;
        EquivalentValueCaption = price.Caption;
        PeriodSummaryText = FormatPeriodSummary(
            selection.Period,
            bounds,
            overview.RequestCount > 0);

        SetCollectionIfChanged(
            ref _dailyRows,
            selection.Period == DashboardViewModel.AllTime &&
            overview.RequestCount == 0
                ? []
                : CreateDailyRows(trend, bounds),
            nameof(DailyRows));
        long? agentTotal = SumKnownTotals(
            agents.Select(row => row.NormalizedTotal));
        long? modelTotal = SumKnownTotals(
            models.Select(row => row.NormalizedTotal));
        SetCollectionIfChanged(
            ref _agentRows,
            agents.Select(row => CreateAgentRow(row, agentTotal)),
            nameof(AgentRows));
        SetCollectionIfChanged(
            ref _modelRows,
            models.Select(row => CreateModelRow(row, modelTotal)),
            nameof(ModelRows));
        SetCollectionIfChanged(
            ref _agentOptions,
            CreateOptions(DashboardViewModel.AllAgents, filterValues.AgentIds),
            nameof(AgentOptions));
        SetCollectionIfChanged(
            ref _modelOptions,
            CreateOptions(DashboardViewModel.AllModels, filterValues.Models),
            nameof(ModelOptions));
        SetCollectionIfChanged(
            ref _projectOptions,
            UsageFilterPresentation.CreateProjectOptions(filterValues.Projects),
            nameof(ProjectOptions));

        if (!AgentOptions.Contains(SelectedAgent))
        {
            filterReset = SetProperty(
                ref _selectedAgent,
                DashboardViewModel.AllAgents,
                nameof(SelectedAgent));
        }

        if (!ModelOptions.Contains(SelectedModel))
        {
            filterReset = SetProperty(
                ref _selectedModel,
                DashboardViewModel.AllModels,
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
                DashboardViewModel.AllProjects,
                nameof(SelectedProject)) || filterReset;
        }

        if (_pinnedDate != pinnedDate)
        {
            SetPinnedDate(pinnedDate);
        }

        AnalysisDailyUsageRow? selected = pinnedDate is DateOnly day
            ? DailyRows.FirstOrDefault(row => row.Date == day)
            : null;
        SetProperty(
            ref _selectedDailyRow,
            selected,
            nameof(SelectedDailyRow));
    }

    private IReadOnlyList<AnalysisDailyUsageRow> CreateDailyRows(
        IReadOnlyList<UsageTrendPoint> source,
        StatisticsPeriodBounds bounds)
    {
        Dictionary<DateOnly, DailyAggregate> byDay = source
            .GroupBy(point => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(point.BucketStartUtc, _localTimeZone).Date))
            .ToDictionary(
                group => group.Key,
                group => new DailyAggregate(
                    group.Sum(point => point.RequestCount),
                    Sum(group.Select(point => point.NormalizedTotal)),
                    Sum(group.Select(point => point.UncachedInput)),
                    Sum(group.Select(point => point.Output)),
                    Sum(group.Select(point => point.CacheRead))));
        var rows = new List<AnalysisDailyUsageRow>();
        for (DateTime localDay = bounds.LocalEndExclusive.AddTicks(-1).Date;
             localDay >= bounds.LocalStart.Date;
             localDay = localDay.AddDays(-1))
        {
            DateOnly date = DateOnly.FromDateTime(localDay);
            DailyAggregate aggregate = byDay.TryGetValue(date, out DailyAggregate? value)
                ? value
                : DailyAggregate.Empty;
            rows.Add(new AnalysisDailyUsageRow(
                date,
                date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture),
                FormatMetric(aggregate.Total.Value),
                aggregate.RequestCount.ToString("N0", CultureInfo.InvariantCulture),
                FormatMetric(aggregate.UncachedInput.Value),
                FormatMetric(aggregate.Output.Value),
                FormatMetric(aggregate.CacheRead.Value),
                CalculateCacheHitRate(
                    aggregate.RequestCount,
                    aggregate.UncachedInput,
                    aggregate.CacheRead)));
        }

        return rows;
    }

    private static AnalysisAgentUsageRow CreateAgentRow(
        AgentUsageRow row,
        long? rangeTotal) => new(
            row.AgentId,
            FormatMetric(row.NormalizedTotal.Value),
            FormatShare(row.NormalizedTotal.Value, rangeTotal),
            row.RequestCount.ToString("N0", CultureInfo.InvariantCulture),
            FormatMetric(row.UncachedInput.Value),
            FormatMetric(row.Output.Value),
            FormatMetric(row.CacheRead.Value));

    private static AnalysisModelUsageRow CreateModelRow(
        AgentModelUsageRow row,
        long? rangeTotal) => new(
            row.Model,
            row.AgentId,
            FormatMetric(row.NormalizedTotal.Value),
            FormatShare(row.NormalizedTotal.Value, rangeTotal),
            row.RequestCount.ToString("N0", CultureInfo.InvariantCulture),
            FormatMetric(row.UncachedInput.Value),
            FormatMetric(row.Output.Value),
            FormatMetric(row.CacheRead.Value));

    private StatisticsPeriodBounds ResolveEffectiveBounds(
        FilterSelection selection,
        StatisticsPeriodBounds queryBounds,
        UsageOverview overview,
        IReadOnlyList<UsageTrendPoint> trend)
    {
        if (selection.Period != DashboardViewModel.AllTime)
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

    private StatisticsPeriodBounds CreateDayBounds(
        DateOnly date,
        StatisticsPeriodBounds rangeBounds)
    {
        DateTime dayStart = date.ToDateTime(TimeOnly.MinValue);
        DateTime dayEnd = dayStart.AddDays(1);
        DateTime localStart = rangeBounds.LocalStart > dayStart
            ? rangeBounds.LocalStart
            : dayStart;
        DateTime localEnd = rangeBounds.LocalEndExclusive < dayEnd
            ? rangeBounds.LocalEndExclusive
            : dayEnd;
        return _periodResolver.CreateBounds(localStart, localEnd);
    }

    private static UsageFilter CreateFilter(
        FilterSelection selection,
        StatisticsPeriodBounds bounds) => new(
            bounds.StartInclusiveUtc,
            bounds.EndExclusiveUtc,
            selection.Agent == DashboardViewModel.AllAgents
                ? null
                : selection.Agent,
            selection.Model == DashboardViewModel.AllModels
                ? null
                : selection.Model,
            limit: 1000,
            projectId: selection.Project == DashboardViewModel.AllProjects
                ? null
                : selection.Project);

    private static bool IsInside(
        DateOnly? date,
        StatisticsPeriodBounds bounds) =>
        date is DateOnly value &&
        value >= DateOnly.FromDateTime(bounds.LocalStart.Date) &&
        value <= DateOnly.FromDateTime(
            bounds.LocalEndExclusive.AddTicks(-1).Date);

    private void PinDate(DateOnly date)
    {
        if (_pinnedDate == date)
        {
            return;
        }

        SetPinnedDate(date);
        OnFilterChanged();
    }

    private void ClearPinnedDate()
    {
        if (!HasPinnedDate)
        {
            return;
        }

        SetPinnedDate(null);
        SetProperty(
            ref _selectedDailyRow,
            null,
            nameof(SelectedDailyRow));
        OnFilterChanged();
    }

    private void SetPinnedDate(DateOnly? date)
    {
        if (_pinnedDate == date)
        {
            return;
        }

        _pinnedDate = date;
        OnPropertyChanged(nameof(HasPinnedDate));
        OnPropertyChanged(nameof(PinnedDateText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(ContextText));
        ClearPinnedDateCommand.RaiseCanExecuteChanged();
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

        string previousPeriod = _periodBeforePendingCustomSelection ??
            DashboardViewModel.ThirtyDays;
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

        DateTime local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return new CustomTimeRange(local, local.AddHours(1)).StartLocal;
    }

    private void OnFilterChanged()
    {
        OnPropertyChanged(nameof(ContextText));
        if (Volatile.Read(ref _suppressFilterChanged) != 0)
        {
            return;
        }

        CancelRefresh();
        FilterChanged?.Invoke(this, EventArgs.Empty);
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

    private static MetricAggregate Sum(IEnumerable<MetricAggregate> values)
    {
        MetricAggregate[] items = values.ToArray();
        int available = items.Sum(item => item.AvailableRecords);
        int unavailable = items.Sum(item => item.UnavailableRecords);
        long? total = available == 0
            ? null
            : items.Where(item => item.Value.HasValue).Sum(item => item.Value!.Value);
        return new MetricAggregate(total, available, unavailable);
    }

    private static long? SumKnownTotals(IEnumerable<MetricAggregate> values)
    {
        MetricAggregate[] items = values.ToArray();
        return items.Length > 0 && items.All(item => item.Value.HasValue)
            ? items.Sum(item => item.Value!.Value)
            : null;
    }

    private static string FormatMetric(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    private static string FormatShare(long? value, long? total) =>
        value is long numerator && total is > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.0}%",
                ((decimal)numerator / total.Value) * 100)
            : "—";

    private static string CalculateCacheHitRate(
        long requestCount,
        MetricAggregate uncachedInput,
        MetricAggregate cacheRead)
    {
        if (requestCount <= 0 ||
            uncachedInput.AvailableRecords != requestCount ||
            cacheRead.AvailableRecords != requestCount ||
            uncachedInput.UnavailableRecords != 0 ||
            cacheRead.UnavailableRecords != 0 ||
            uncachedInput.Value is not long input ||
            cacheRead.Value is not long cached ||
            input < 0 ||
            cached < 0)
        {
            return "—";
        }

        decimal denominator = (decimal)input + cached;
        return denominator > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.0}%",
                ((decimal)cached / denominator) * 100)
            : "—";
    }

    private static int GetTouchedDayCount(StatisticsPeriodBounds bounds) =>
        checked(
            (bounds.LocalEndExclusive.AddTicks(-1).Date -
             bounds.LocalStart.Date).Days + 1);

    private static string FormatPeriodSummary(
        string period,
        StatisticsPeriodBounds bounds,
        bool hasRecords)
    {
        if (period == DashboardViewModel.Custom)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{bounds.LocalStart:yyyy年M月d日 HH:00}至{bounds.LocalEndExclusive:yyyy年M月d日 HH:00}");
        }

        DateTime endInclusive = bounds.LocalEndExclusive.AddDays(-1);
        if (period == DashboardViewModel.AllTime && !hasRecords)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"截至 {endInclusive:yyyy年M月d日}");
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{bounds.LocalStart:yyyy年M月d日}至{endInclusive:M月d日}");
    }

    private sealed record FilterSelection(
        string Period,
        string Agent,
        string Model,
        string Project,
        DateTime? CustomStartDate,
        DateTime? CustomEndDate,
        DateOnly? PinnedDate);

    private sealed record DailyAggregate(
        long RequestCount,
        MetricAggregate Total,
        MetricAggregate UncachedInput,
        MetricAggregate Output,
        MetricAggregate CacheRead)
    {
        public static DailyAggregate Empty { get; } = new(
            0,
            new MetricAggregate(null, 0, 0),
            new MetricAggregate(null, 0, 0),
            new MetricAggregate(null, 0, 0),
            new MetricAggregate(null, 0, 0));
    }
}
