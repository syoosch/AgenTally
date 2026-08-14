using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;

namespace AgenTally.UI.ViewModels;

public sealed record UsageSharePresentation
{
    public UsageSharePresentation(
        string key,
        string nameText,
        long? tokens,
        long? rangeTotal,
        string? contextText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameText);
        Key = key;
        NameText = nameText;
        ContextText = contextText;
        TokensText = ProjectValueFormatter.FormatTokens(tokens);
        if (tokens is >= 0 && rangeTotal is > 0)
        {
            decimal share = Math.Clamp(
                (decimal)tokens.Value / rangeTotal.Value,
                0m,
                1m);
            ShareValue = decimal.ToDouble(share * 100m);
            ShareText = string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.0}%",
                share * 100m);
        }
        else
        {
            ShareValue = 0d;
            ShareText = "—";
        }
    }

    public string Key { get; }

    public string NameText { get; }

    public string? ContextText { get; }

    public string TokensText { get; }

    public double ShareValue { get; }

    public string ShareText { get; }
}

public sealed record ProjectMetricPresentation(
    string Label,
    string ValueText);

public sealed record ProjectListItemPresentation
{
    internal ProjectListItemPresentation(
        ProjectUsageRow row,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        Row = row;
        ProjectId = row.ProjectId;
        IsUnidentified = row.IsUnidentified;
        NameText = ProjectValueFormatter.DescribeProjectName(row);
        PathText = ProjectValueFormatter.DescribeProjectPath(row);
        DateTime lastActivity = TimeZoneInfo.ConvertTime(
            row.LastActivityUtc,
            localTimeZone).DateTime;
        ActivityText = string.Create(
            CultureInfo.CurrentCulture,
            $"{lastActivity:M月d日 HH:mm} 最近活跃 · {row.RootSessionCount:N0} 个根会话");
        TokensText = SessionValueFormatter.FormatTokens(
            row.Metrics.NormalizedTotal);
        (PriceText, PriceState, _) = SessionValueFormatter.DescribePrice(
            row.Pricing);
    }

    internal ProjectUsageRow Row { get; }

    public string ProjectId { get; }

    public bool IsUnidentified { get; }

    public string NameText { get; }

    public string PathText { get; }

    public string ActivityText { get; }

    public string TokensText { get; }

    public string PriceText { get; }

    public SessionPriceState PriceState { get; }
}

public sealed record ProjectPlatformPresentation
{
    internal ProjectPlatformPresentation(
        AgentUsageRow row,
        long? projectTotal,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        Row = row;
        Share = new UsageSharePresentation(
            row.AgentId,
            row.AgentId,
            row.NormalizedTotal.Value,
            projectTotal);
        ContextText = null;
        TotalTokensText = SessionValueFormatter.FormatTokens(
            row.NormalizedTotal);
        RequestCountText = row.RequestCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        CacheHitRateText = ProjectValueFormatter.CalculateCacheHitRate(
            row.RequestCount,
            row.UncachedInput,
            row.CacheRead);
        PricePresentation price = PricePresentationFormatter.Describe(row.Pricing);
        PriceText = price.ValueText;
        PriceCaption = price.Caption;
        StartedAtText = ProjectValueFormatter.FormatLocalTime(
            row.StartedAtUtc,
            localTimeZone);
        LastActivityText = ProjectValueFormatter.FormatLocalTime(
            row.LastActivityUtc,
            localTimeZone);
        Metrics = ProjectValueFormatter.CreateMetricRows(row.Metrics);
    }

    internal AgentUsageRow Row { get; }

    public UsageSharePresentation Share { get; }

    public string NameText => Share.NameText;

    public string TokensText => Share.TokensText;

    public double ShareValue => Share.ShareValue;

    public string ShareText => Share.ShareText;

    public string? ContextText { get; }

    public bool HasContext => ContextText is not null;

    public string TotalTokensText { get; }

    public string RequestCountText { get; }

    public string CacheHitRateText { get; }

    public string PriceText { get; }

    public string PriceCaption { get; }

    public string StartedAtText { get; }

    public string LastActivityText { get; }

    public IReadOnlyList<ProjectMetricPresentation> Metrics { get; }
}

public sealed record ProjectModelPresentation
{
    internal ProjectModelPresentation(
        AgentModelUsageRow row,
        long? projectTotal,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        Row = row;
        string key = string.Concat(row.AgentId, "\u001f", row.Model);
        Share = new UsageSharePresentation(
            key,
            row.Model,
            row.NormalizedTotal.Value,
            projectTotal,
            row.AgentId);
        AgentText = row.AgentId;
        ContextText = $"所属平台：{row.AgentId}";
        TotalTokensText = SessionValueFormatter.FormatTokens(
            row.NormalizedTotal);
        RequestCountText = row.RequestCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        CacheHitRateText = ProjectValueFormatter.CalculateCacheHitRate(
            row.RequestCount,
            row.UncachedInput,
            row.CacheRead);
        PricePresentation price = PricePresentationFormatter.Describe(row.Pricing);
        PriceText = price.ValueText;
        PriceCaption = price.Caption;
        StartedAtText = ProjectValueFormatter.FormatLocalTime(
            row.StartedAtUtc,
            localTimeZone);
        LastActivityText = ProjectValueFormatter.FormatLocalTime(
            row.LastActivityUtc,
            localTimeZone);
        Metrics = ProjectValueFormatter.CreateMetricRows(row.Metrics);
    }

    internal AgentModelUsageRow Row { get; }

    public UsageSharePresentation Share { get; }

    public string NameText => Share.NameText;

    public string TokensText => Share.TokensText;

    public double ShareValue => Share.ShareValue;

    public string ShareText => Share.ShareText;

    public string AgentText { get; }

    public string ContextText { get; }

    public bool HasContext => true;

    public string TotalTokensText { get; }

    public string RequestCountText { get; }

    public string CacheHitRateText { get; }

    public string PriceText { get; }

    public string PriceCaption { get; }

    public string StartedAtText { get; }

    public string LastActivityText { get; }

    public IReadOnlyList<ProjectMetricPresentation> Metrics { get; }
}

public sealed class ProjectDetailPresentation
{
    internal ProjectDetailPresentation(
        ProjectUsageRow project,
        IReadOnlyList<UsageTrendPoint> chartTrend,
        IReadOnlyList<UsageTrendPoint> dailyTrend,
        IReadOnlyList<AgentUsageRow> agents,
        IReadOnlyList<AgentModelUsageRow> models,
        IReadOnlyList<RootSessionSummaryRow> sessions,
        DateTime localRangeStart,
        DateTime localRangeEndInclusive,
        string periodText,
        TrendGranularity trendGranularity,
        DateTimeOffset trendRangeStartInclusiveUtc,
        DateTimeOffset trendRangeEndExclusiveUtc,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(chartTrend);
        ArgumentNullException.ThrowIfNull(dailyTrend);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        ProjectId = project.ProjectId;
        IsUnidentified = project.IsUnidentified;
        NameText = ProjectValueFormatter.DescribeProjectName(project);
        PathText = ProjectValueFormatter.DescribeProjectPath(project);
        PeriodText = periodText;
        StartedAtText = ProjectValueFormatter.FormatLocalTime(
            project.StartedAtUtc,
            localTimeZone);
        LastActivityText = ProjectValueFormatter.FormatLocalTime(
            project.LastActivityUtc,
            localTimeZone);
        TotalTokensText = SessionValueFormatter.FormatTokens(
            project.Metrics.NormalizedTotal);
        RootSessionCountText = project.RootSessionCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        PricePresentation price = PricePresentationFormatter.Describe(
            project.Pricing);
        PriceText = price.ValueText;
        PriceCaption = price.Caption;
        DataNote = ProjectValueFormatter.CreateDataNote(
            project.Metrics,
            price);
        TokenMetrics = new ObservableCollection<ProjectMetricPresentation>(
            ProjectValueFormatter.CreateMetricRows(project.Metrics));
        TrendPoints = new ObservableCollection<UsageTrendPoint>(chartTrend);
        TrendGranularity = trendGranularity;
        TrendRangeStartInclusiveUtc = trendRangeStartInclusiveUtc;
        TrendRangeEndExclusiveUtc = trendRangeEndExclusiveUtc;
        TrendSubtitle = trendGranularity == TrendGranularity.Hour
            ? "当前筛选范围内的每小时 Token 总量"
            : "当前筛选范围内的每日 Token 总量";

        bool hasUnknownDay = dailyTrend.Any(static point =>
            !point.NormalizedTotal.Value.HasValue);
        UsageTrendPoint[] knownDays = dailyTrend
            .Where(static point => point.NormalizedTotal.Value.HasValue)
            .ToArray();
        ActiveDayCountText = hasUnknownDay
            ? "—"
            : knownDays.Count(static point =>
                    point.NormalizedTotal.Value > 0)
                .ToString("N0", CultureInfo.InvariantCulture);
        ConsecutiveDayCountText = hasUnknownDay
            ? "—"
            : CalculateConsecutiveDays(
                knownDays,
                localRangeStart,
                localRangeEndInclusive,
                localTimeZone).ToString(
                    "N0",
                    CultureInfo.InvariantCulture);
        UsageTrendPoint? peak = hasUnknownDay
            ? null
            : knownDays
                .OrderByDescending(static point => point.NormalizedTotal.Value)
                .ThenBy(static point => point.BucketStartUtc)
                .FirstOrDefault();
        if (peak?.NormalizedTotal.Value is long peakValue)
        {
            DateTime peakDate = TimeZoneInfo.ConvertTime(
                peak.BucketStartUtc,
                localTimeZone).DateTime;
            PeakDayText = string.Create(
                CultureInfo.CurrentCulture,
                $"{peakDate:M月d日} · {ProjectValueFormatter.FormatCompact(peakValue)}");
            PeakDayToolTip = string.Create(
                CultureInfo.CurrentCulture,
                $"{peakDate:yyyy年M月d日} · {peakValue.ToString("N0", CultureInfo.InvariantCulture)} Token");
        }
        else
        {
            PeakDayText = "—";
            PeakDayToolTip = "当前范围暂无可用 Token 总量";
        }

        Platforms = new ObservableCollection<ProjectPlatformPresentation>(
            agents.Select(row => new ProjectPlatformPresentation(
                row,
                project.Metrics.NormalizedTotal.Value,
                localTimeZone)));
        Models = new ObservableCollection<ProjectModelPresentation>(
            models.Select(row => new ProjectModelPresentation(
                row,
                project.Metrics.NormalizedTotal.Value,
                localTimeZone)));
        Sessions = new ObservableCollection<SessionListItemPresentation>(
            sessions.Select(row => new SessionListItemPresentation(
                row,
                localTimeZone)));
    }

    private static int CalculateConsecutiveDays(
        IEnumerable<UsageTrendPoint> knownDays,
        DateTime localRangeStart,
        DateTime localRangeEndInclusive,
        TimeZoneInfo localTimeZone)
    {
        HashSet<DateTime> positiveDays = knownDays
            .Where(static point => point.NormalizedTotal.Value > 0)
            .Select(point => TimeZoneInfo.ConvertTime(
                point.BucketStartUtc,
                localTimeZone).Date)
            .ToHashSet();
        int streak = 0;
        for (DateTime date = localRangeEndInclusive.Date;
             date >= localRangeStart.Date && positiveDays.Contains(date);
             date = date.AddDays(-1))
        {
            streak++;
        }

        return streak;
    }

    public string ProjectId { get; }

    public bool IsUnidentified { get; }

    public string NameText { get; }

    public string PathText { get; }

    public string PeriodText { get; }

    public string StartedAtText { get; }

    public string LastActivityText { get; }

    public string TotalTokensText { get; }

    public string RootSessionCountText { get; }

    public string PriceText { get; }

    public string PriceCaption { get; }

    public string? DataNote { get; }

    public bool HasDataNote => DataNote is not null;

    public string ActiveDayCountText { get; }

    public string ConsecutiveDayCountText { get; }

    public string PeakDayText { get; }

    public string PeakDayToolTip { get; }

    public ObservableCollection<UsageTrendPoint> TrendPoints { get; }

    public TrendGranularity TrendGranularity { get; }

    public DateTimeOffset TrendRangeStartInclusiveUtc { get; }

    public DateTimeOffset TrendRangeEndExclusiveUtc { get; }

    public string TrendSubtitle { get; }

    public ObservableCollection<ProjectMetricPresentation> TokenMetrics { get; }

    public ObservableCollection<ProjectPlatformPresentation> Platforms { get; }

    public ObservableCollection<ProjectModelPresentation> Models { get; }

    public ObservableCollection<SessionListItemPresentation> Sessions { get; }

    public bool HasPlatforms => Platforms.Count > 0;

    public bool HasModels => Models.Count > 0;

    public bool HasSessions => Sessions.Count > 0;
}

public sealed class ProjectsViewModel : PageViewModel
{
    public const string AllAgents = "全部平台";
    public const string AllModels = "全部模型";
    public const string AllTime = "全部时间";
    public const string Today = "今天";
    public const string SevenDays = "近 7 天";
    public const string ThirtyDays = "近 30 天";
    public const string NinetyDays = "近 90 天";
    public const string Custom = "自定义";
    public const string SortByRecent = "最近活跃";
    public const string SortByTokens = "总 Token";
    public const string SortByName = "项目名称";
    private const int RootSessionPageSize = 200;

    private static readonly ReadOnlyCollection<string> SupportedPeriods =
        Array.AsReadOnly(
            [AllTime, Today, SevenDays, ThirtyDays, NinetyDays, Custom]);
    private static readonly ReadOnlyCollection<string> SupportedSorts =
        Array.AsReadOnly([SortByRecent, SortByTokens, SortByName]);
    private readonly IUsageQueryService _queries;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly StatisticsPeriodResolver _periodResolver;
    private IReadOnlyList<ProjectListItemPresentation> _allProjects = [];
    private ObservableCollection<ProjectListItemPresentation> _projects = [];
    private ObservableCollection<string> _agentOptions = new([AllAgents]);
    private ObservableCollection<string> _modelOptions = new([AllModels]);
    private string _selectedAgent = AllAgents;
    private string _selectedModel = AllModels;
    private string _selectedPeriod = AllTime;
    private string _selectedSort = SortByRecent;
    private DateTime? _customStartDate;
    private DateTime? _customEndDate;
    private DateTime? _customEndBeforePendingSelection;
    private DateTime? _customStartBeforePendingSelection;
    private string _searchText = string.Empty;
    private ProjectListItemPresentation? _selectedProject;
    private ProjectDetailPresentation? _detail;
    private ProjectPlatformPresentation? _selectedPlatform;
    private ProjectModelPresentation? _selectedModelDetail;
    private bool _isDetailLoading;
    private string? _detailErrorMessage;
    private bool _suppressSelectionLoad;
    private bool _suppressFilterChanged;
    private bool _isCustomRangeSelectionPending;
    private int _selectedDetailTabIndex;
    private string? _requestedProjectId;
    private string _periodSummaryText = string.Empty;
    private string? _periodBeforePendingCustomSelection;
    private StatisticsPeriodBounds? _lastEffectiveBounds;

    public ProjectsViewModel(
        IUsageQueryService queries,
        Dispatcher dispatcher,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
        : base("项目", dispatcher)
    {
        ArgumentNullException.ThrowIfNull(queries);
        _queries = queries;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _periodResolver = new StatisticsPeriodResolver(_localTimeZone);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None));
        OpenSessionCommand = new RelayCommand(
            OpenSession,
            static parameter => parameter is RootSessionIdentity);
        CommitCustomRangeCommand = new RelayCommand(CommitCustomRange);
        CancelCustomRangeCommand = new RelayCommand(CancelCustomRangeSelection);
    }

    public event EventHandler? FilterChanged;

    public event Action<RootSessionIdentity>? SessionRequested;

    public IReadOnlyList<string> PeriodOptions => SupportedPeriods;

    public IReadOnlyList<string> SortOptions => SupportedSorts;

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
                if (_suppressFilterChanged)
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

    internal void ApplySynchronizedFilters(
        string period,
        string agent,
        string model,
        DateTime? customStartDate,
        DateTime? customEndDate)
    {
        UiDispatcher.VerifyAccess();
        _suppressFilterChanged = true;
        try
        {
            IsCustomRangeSelectionPending = false;
            _periodBeforePendingCustomSelection = null;
            CustomStartDate = customStartDate;
            CustomEndDate = customEndDate;
            SelectedPeriod = period;
            SelectedAgent = agent;
            SelectedModel = model;
        }
        finally
        {
            _suppressFilterChanged = false;
        }
    }

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

    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !SupportedSorts.Contains(value, StringComparer.Ordinal))
            {
                return;
            }

            if (SetProperty(ref _selectedSort, value))
            {
                ApplyProjectProjection();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyProjectProjection();
            }
        }
    }

    public string PeriodSummaryText
    {
        get => _periodSummaryText;
        private set => SetProperty(ref _periodSummaryText, value);
    }

    public ObservableCollection<ProjectListItemPresentation> Projects
    {
        get => _projects;
        private set => SetProperty(ref _projects, value);
    }

    public bool HasProjects => Projects.Count > 0;

    public ProjectListItemPresentation? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                if (!_suppressSelectionLoad && value is not null)
                {
                    _ = LoadDetailForSelectionAsync(value);
                }
            }
        }
    }

    public bool HasSelection => SelectedProject is not null;

    public ProjectDetailPresentation? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    public bool HasDetail => Detail is not null;

    public ProjectPlatformPresentation? SelectedPlatform
    {
        get => _selectedPlatform;
        set => SetProperty(ref _selectedPlatform, value);
    }

    public ProjectModelPresentation? SelectedModelDetail
    {
        get => _selectedModelDetail;
        set => SetProperty(ref _selectedModelDetail, value);
    }

    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        private set => SetProperty(ref _isDetailLoading, value);
    }

    public string? DetailErrorMessage
    {
        get => _detailErrorMessage;
        private set
        {
            if (SetProperty(ref _detailErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasDetailError));
            }
        }
    }

    public bool HasDetailError => DetailErrorMessage is not null;

    public int SelectedDetailTabIndex
    {
        get => _selectedDetailTabIndex;
        set => SetProperty(ref _selectedDetailTabIndex, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand OpenSessionCommand { get; }

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
            FilterSelection selection = await ReadSelectionAsync();
            StatisticsPeriodBounds bounds = _periodResolver.Resolve(
                selection.Period,
                _timeProvider.GetUtcNow(),
                selection.CustomStartDate,
                selection.CustomEndDate);
            UsageFilter filter = CreateFilter(selection, bounds);
            Task<IReadOnlyList<ProjectUsageRow>> projectsTask =
                _queries.GetProjectsAsync(filter, session.Token);
            Task<UsageFilterValues> filtersTask =
                _queries.GetFilterValuesAsync(filter, session.Token);
            await Task.WhenAll(projectsTask, filtersTask);
            StatisticsPeriodBounds pageEffectiveBounds =
                ResolvePageEffectiveBounds(
                    selection,
                    bounds,
                    projectsTask.Result);

            ProjectListItemPresentation? selected = null;
            bool filterReset = false;
            await ApplyIfCurrentAsync(session, () =>
            {
                _allProjects = projectsTask.Result
                    .Select(row => new ProjectListItemPresentation(
                        row,
                        _localTimeZone))
                    .ToArray();
                _lastEffectiveBounds = pageEffectiveBounds;
                PeriodSummaryText = FormatPagePeriodSummary(
                    selection.Period,
                    pageEffectiveBounds,
                    projectsTask.Result);
                ApplyFilterOptions(filtersTask.Result, out filterReset);
                ApplyProjectProjectionCore();
                selected = ResolveSelectedProject();
                SetSelectedWithoutLoading(selected);
            });
            if (filterReset)
            {
                await RunOnDispatcherAsync(OnFilterChanged);
                return;
            }

            if (selected is null)
            {
                await ApplyIfCurrentAsync(session, ClearDetail);
            }
            else
            {
                await LoadDetailCoreAsync(
                    selected,
                    selection,
                    bounds,
                    session);
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

    public async Task<bool> SelectProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using IDisposable feedback = await BeginInteractionFeedbackAsync();
        await RunOnDispatcherAsync(() =>
        {
            SelectedDetailTabIndex = 0;
            _requestedProjectId = projectId;
        });
        ProjectListItemPresentation? existing = await ReadOnDispatcherAsync(() =>
            _allProjects.FirstOrDefault(item => string.Equals(
                item.ProjectId,
                projectId,
                StringComparison.Ordinal)));
        if (existing is null)
        {
            await RunOnDispatcherAsync(() =>
            {
                _suppressFilterChanged = true;
                try
                {
                    SelectedAgent = AllAgents;
                    SelectedModel = AllModels;
                    SelectedPeriod = AllTime;
                    CustomStartDate = null;
                    CustomEndDate = null;
                    SearchText = string.Empty;
                }
                finally
                {
                    _suppressFilterChanged = false;
                }
            });
            await RefreshAsync(cancellationToken);
        }
        else
        {
            RefreshSession session = BeginRefresh(cancellationToken);
            try
            {
                FilterSelection selection = await ReadSelectionAsync();
                StatisticsPeriodBounds bounds = _periodResolver.Resolve(
                    selection.Period,
                    _timeProvider.GetUtcNow(),
                    selection.CustomStartDate,
                    selection.CustomEndDate);
                await RunOnDispatcherAsync(() =>
                    SetSelectedWithoutLoading(existing));
                await LoadDetailCoreAsync(existing, selection, bounds, session);
            }
            finally
            {
                await EndRefreshAsync(session);
            }
        }

        bool selected = await ReadOnDispatcherAsync(() =>
            string.Equals(
                SelectedProject?.ProjectId,
                projectId,
                StringComparison.Ordinal) &&
            Detail is not null);
        await RunOnDispatcherAsync(() => _requestedProjectId = null);
        return selected;
    }

    private async Task LoadDetailForSelectionAsync(
        ProjectListItemPresentation project)
    {
        using IDisposable feedback = await BeginInteractionFeedbackAsync();
        RefreshSession session = BeginRefresh(CancellationToken.None);
        try
        {
            FilterSelection selection = await ReadSelectionAsync();
            StatisticsPeriodBounds bounds = _periodResolver.Resolve(
                selection.Period,
                _timeProvider.GetUtcNow(),
                selection.CustomStartDate,
                selection.CustomEndDate);
            await LoadDetailCoreAsync(project, selection, bounds, session);
        }
        finally
        {
            await EndRefreshAsync(session);
        }
    }

    private async Task LoadDetailCoreAsync(
        ProjectListItemPresentation project,
        FilterSelection selection,
        StatisticsPeriodBounds bounds,
        RefreshSession session)
    {
        await ApplyIfCurrentAsync(session, () =>
        {
            IsDetailLoading = true;
            DetailErrorMessage = null;
        });
        try
        {
            UsageFilter filter = CreateProjectFilter(selection, bounds, project);
            Task<IReadOnlyList<UsageTrendPoint>> trendTask =
                _queries.GetTrendAsync(filter, session.Token);
            Task<IReadOnlyList<AgentUsageRow>> agentsTask =
                _queries.GetAgentsAsync(filter, session.Token);
            Task<IReadOnlyList<AgentModelUsageRow>> modelsTask =
                _queries.GetAgentModelsAsync(filter, session.Token);
            Task<IReadOnlyList<RootSessionSummaryRow>> sessionsTask =
                LoadAllRootSessionsAsync(filter, session.Token);
            await Task.WhenAll(
                trendTask,
                agentsTask,
                modelsTask,
                sessionsTask);
            DateTime metricRangeStart = selection.Period == AllTime
                ? TimeZoneInfo.ConvertTime(
                    project.Row.StartedAtUtc,
                    _localTimeZone).Date
                : bounds.LocalStart;
            DateTime projectActiveStart = TimeZoneInfo.ConvertTime(
                project.Row.StartedAtUtc,
                _localTimeZone).Date;
            DateTime projectActiveEndExclusive = TimeZoneInfo.ConvertTime(
                project.Row.LastActivityUtc,
                _localTimeZone).Date.AddDays(1);
            DateTime chartStart = projectActiveStart < bounds.LocalStart
                ? bounds.LocalStart
                : projectActiveStart;
            DateTime chartEndExclusive =
                projectActiveEndExclusive > bounds.LocalEndExclusive
                    ? bounds.LocalEndExclusive
                    : projectActiveEndExclusive;
            StatisticsPeriodBounds chartBounds = _periodResolver.CreateBounds(
                chartStart,
                chartEndExclusive);
            TrendGranularity chartGranularity = chartBounds.Elapsed <=
                TimeSpan.FromHours(24)
                    ? TrendGranularity.Hour
                    : TrendGranularity.Day;
            IReadOnlyList<UsageTrendPoint> chart = CreateProjectTrend(
                trendTask.Result,
                chartBounds,
                chartGranularity);
            IReadOnlyList<UsageTrendPoint> daily = CreateProjectTrend(
                trendTask.Result,
                chartBounds,
                TrendGranularity.Day);
            var detail = new ProjectDetailPresentation(
                project.Row,
                chart,
                daily,
                agentsTask.Result,
                modelsTask.Result,
                sessionsTask.Result,
                chartBounds.LocalStart,
                chartBounds.LocalEndExclusive.AddTicks(-1).Date,
                FormatPeriodSummary(selection.Period, bounds, metricRangeStart),
                chartGranularity,
                chartBounds.StartInclusiveUtc,
                chartBounds.EndExclusiveUtc,
                _localTimeZone);
            await ApplyIfCurrentAsync(session, () =>
            {
                if (!string.Equals(
                        SelectedProject?.ProjectId,
                        project.ProjectId,
                        StringComparison.Ordinal))
                {
                    return;
                }

                Detail = detail;
                SelectedPlatform = detail.Platforms.FirstOrDefault();
                SelectedModelDetail = detail.Models.FirstOrDefault();
                IsDetailLoading = false;
            });
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ApplyIfCurrentAsync(session, () =>
            {
                DetailErrorMessage = UiErrorMessageClassifier.Classify(exception);
                IsDetailLoading = false;
            });
        }
    }

    private async Task<IReadOnlyList<RootSessionSummaryRow>>
        LoadAllRootSessionsAsync(
            UsageFilter filter,
            CancellationToken cancellationToken)
    {
        var rows = new List<RootSessionSummaryRow>();
        RootSessionCursor? cursor = null;
        do
        {
            RootSessionPage page = await _queries.GetRootSessionsAsync(
                new RootSessionPageRequest(
                    filter,
                    RootSessionPageSize,
                    cursor),
                cancellationToken);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return rows;
    }

    private FilterSelection ReadSelection() => new(
        SelectedAgent,
        SelectedModel,
        SelectedPeriod,
        CustomStartDate,
        CustomEndDate);

    private Task<FilterSelection> ReadSelectionAsync() =>
        ReadOnDispatcherAsync(ReadSelection);

    private void ApplyFilterOptions(
        UsageFilterValues values,
        out bool filterReset)
    {
        filterReset = false;
        SetCollectionIfChanged(
            ref _agentOptions,
            CreateOptions(AllAgents, values.AgentIds),
            nameof(AgentOptions));
        SetCollectionIfChanged(
            ref _modelOptions,
            CreateOptions(AllModels, values.Models),
            nameof(ModelOptions));
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
    }

    private void ApplyProjectProjection()
    {
        ApplyProjectProjectionCore();
        ProjectListItemPresentation? selected = ResolveSelectedProject();
        if (ReferenceEquals(selected, SelectedProject))
        {
            return;
        }

        SelectedProject = selected;
    }

    private void ApplyProjectProjectionCore()
    {
        IEnumerable<ProjectListItemPresentation> filtered = _allProjects;
        string search = SearchText.Trim();
        if (search.Length > 0)
        {
            filtered = filtered.Where(item =>
                item.NameText.Contains(
                    search,
                    StringComparison.CurrentCultureIgnoreCase) ||
                item.PathText.Contains(
                    search,
                    StringComparison.CurrentCultureIgnoreCase));
        }

        filtered = SelectedSort switch
        {
            SortByTokens => filtered
                .OrderByDescending(item =>
                    item.Row.Metrics.NormalizedTotal.Value.HasValue)
                .ThenByDescending(item =>
                    item.Row.Metrics.NormalizedTotal.Value)
                .ThenBy(item => item.NameText, StringComparer.CurrentCultureIgnoreCase),
            SortByName => filtered
                .OrderBy(item => item.NameText, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.PathText, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered
                .OrderByDescending(item => item.Row.LastActivityUtc)
                .ThenBy(item => item.ProjectId, StringComparer.Ordinal)
        };
        SetCollectionIfChanged(
            ref _projects,
            filtered.ToArray(),
            nameof(Projects));
        OnPropertyChanged(nameof(HasProjects));
    }

    private ProjectListItemPresentation? ResolveSelectedProject()
    {
        if (_requestedProjectId is not null)
        {
            ProjectListItemPresentation? requested = Projects.FirstOrDefault(item =>
                string.Equals(
                    item.ProjectId,
                    _requestedProjectId,
                    StringComparison.Ordinal));
            if (requested is not null)
            {
                return requested;
            }
        }

        if (SelectedProject is not null)
        {
            ProjectListItemPresentation? retained = Projects.FirstOrDefault(item =>
                string.Equals(
                    item.ProjectId,
                    SelectedProject.ProjectId,
                    StringComparison.Ordinal));
            if (retained is not null)
            {
                return retained;
            }
        }

        return Projects.FirstOrDefault();
    }

    private void SetSelectedWithoutLoading(
        ProjectListItemPresentation? project)
    {
        _suppressSelectionLoad = true;
        try
        {
            SelectedProject = project;
        }
        finally
        {
            _suppressSelectionLoad = false;
        }
    }

    private void ClearDetail()
    {
        Detail = null;
        SelectedPlatform = null;
        SelectedModelDetail = null;
        IsDetailLoading = false;
        DetailErrorMessage = null;
    }

    private static UsageFilter CreateFilter(
        FilterSelection selection,
        StatisticsPeriodBounds bounds) => new(
        bounds.StartInclusiveUtc,
        bounds.EndExclusiveUtc,
        selection.Agent == AllAgents ? null : selection.Agent,
        selection.Model == AllModels ? null : selection.Model,
        limit: 200);

    private static UsageFilter CreateProjectFilter(
        FilterSelection selection,
        StatisticsPeriodBounds bounds,
        ProjectListItemPresentation project) => new(
        bounds.StartInclusiveUtc,
        bounds.EndExclusiveUtc,
        selection.Agent == AllAgents ? null : selection.Agent,
        selection.Model == AllModels ? null : selection.Model,
        limit: 200,
        projectId: project.IsUnidentified ? null : project.ProjectId,
        unidentifiedProjectOnly: project.IsUnidentified);

    private IReadOnlyList<UsageTrendPoint> CreateProjectTrend(
        IReadOnlyList<UsageTrendPoint> source,
        StatisticsPeriodBounds bounds,
        TrendGranularity granularity)
    {
        if (granularity == TrendGranularity.Week)
        {
            throw new ArgumentOutOfRangeException(nameof(granularity));
        }

        bool hasKnownTotal = source.Any(static point =>
            point.NormalizedTotal.Value.HasValue);
        Dictionary<DateTimeOffset, UsageTrendPoint> byBucket = source
            .GroupBy(point => GetProjectBucketStart(
                point.BucketStartUtc,
                granularity))
            .ToDictionary(
                static group => group.Key,
                group => new UsageTrendPoint(
                    group.Key,
                    ProjectValueFormatter.Sum(
                        group.Select(point => point.NormalizedTotal)),
                    ProjectValueFormatter.Sum(
                        group.Select(point => point.UncachedInput)),
                    ProjectValueFormatter.Sum(
                        group.Select(point => point.Output)),
                    ProjectValueFormatter.Sum(
                        group.Select(point => point.CacheRead)),
                    ProjectValueFormatter.Sum(
                        group.Select(point => point.CacheWrite)),
                    group.Sum(static point => point.RequestCount))
                {
                    Pricing = SumPricing(group.Select(point => point.Pricing))
                });
        var result = new List<UsageTrendPoint>();
        if (granularity == TrendGranularity.Hour)
        {
            for (DateTimeOffset hour = bounds.StartInclusiveUtc;
                 hour < bounds.EndExclusiveUtc;
                 hour = hour.AddHours(1))
            {
                result.Add(CreateProjectTrendPoint(
                    hour,
                    byBucket,
                    hasKnownTotal));
            }

            return result;
        }

        for (DateTime local = bounds.LocalStart.Date;
             local < bounds.LocalEndExclusive;
             local = local.AddDays(1))
        {
            DateTimeOffset bucketStart = new(
                TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
                    _localTimeZone),
                TimeSpan.Zero);
            result.Add(CreateProjectTrendPoint(
                bucketStart,
                byBucket,
                hasKnownTotal));
        }

        return result;
    }

    private static UsageTrendPoint CreateProjectTrendPoint(
        DateTimeOffset bucketStartUtc,
        IReadOnlyDictionary<DateTimeOffset, UsageTrendPoint> byBucket,
        bool hasKnownTotal)
    {
        if (byBucket.TryGetValue(
            bucketStartUtc,
            out UsageTrendPoint? aggregate))
        {
            return aggregate;
        }

        MetricAggregate empty = hasKnownTotal
            ? new MetricAggregate(0, 0, 0)
            : new MetricAggregate(null, 0, 0);
        return new UsageTrendPoint(
            bucketStartUtc,
            empty,
            empty,
            empty,
            empty,
            empty,
            0);
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

    private DateTimeOffset GetProjectBucketStart(
        DateTimeOffset bucketStartUtc,
        TrendGranularity granularity)
    {
        if (granularity == TrendGranularity.Hour)
        {
            DateTime utc = bucketStartUtc.UtcDateTime;
            return new DateTimeOffset(
                utc.Ticks - (utc.Ticks % TimeSpan.TicksPerHour),
                TimeSpan.Zero);
        }

        DateTime local = TimeZoneInfo.ConvertTime(
            bucketStartUtc,
            _localTimeZone).DateTime;
        DateTime localDay = local.Date;
        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(localDay, _localTimeZone),
            TimeSpan.Zero);
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

    private static string FormatPeriodSummary(
        string period,
        StatisticsPeriodBounds bounds,
        DateTime effectiveStart)
    {
        if (period == Custom)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{period} · {bounds.LocalStart:yyyy年M月d日 HH:00}至{bounds.LocalEndExclusive:yyyy年M月d日 HH:00}");
        }

        DateTime endInclusive = bounds.LocalEndExclusive.AddTicks(-1).Date;
        return period == AllTime
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"全部时间 · {effectiveStart:yyyy年M月d日}至{endInclusive:yyyy年M月d日}")
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{period} · {bounds.LocalStart:yyyy年M月d日}至{endInclusive:yyyy年M月d日}");
    }

    private StatisticsPeriodBounds ResolvePageEffectiveBounds(
        FilterSelection selection,
        StatisticsPeriodBounds queryBounds,
        IReadOnlyList<ProjectUsageRow> projects)
    {
        if (selection.Period != AllTime)
        {
            return queryBounds;
        }

        DateTime localStart = projects.Count == 0
            ? queryBounds.LocalEndExclusive.AddDays(-1)
            : projects
                .Select(project => TimeZoneInfo.ConvertTime(
                    project.StartedAtUtc,
                    _localTimeZone).Date)
                .Min();
        return _periodResolver.CreateBounds(
            localStart,
            queryBounds.LocalEndExclusive);
    }

    private string FormatPagePeriodSummary(
        string period,
        StatisticsPeriodBounds bounds,
        IReadOnlyList<ProjectUsageRow> projects)
    {
        if (period == Custom)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{bounds.LocalStart:yyyy年M月d日 HH:00}至{bounds.LocalEndExclusive:yyyy年M月d日 HH:00}");
        }

        DateTime endInclusive = bounds.LocalEndExclusive.AddTicks(-1).Date;
        if (period != AllTime)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{bounds.LocalStart:yyyy年M月d日}至{endInclusive:yyyy年M月d日}");
        }

        if (projects.Count == 0)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"截至 {endInclusive:yyyy年M月d日}");
        }

        DateTime earliest = projects
            .Select(project => TimeZoneInfo.ConvertTime(
                project.StartedAtUtc,
                _localTimeZone).Date)
            .Min();
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{earliest:yyyy年M月d日}至{endInclusive:yyyy年M月d日}");
    }

    private void OpenSession(object? parameter)
    {
        if (parameter is RootSessionIdentity identity)
        {
            SessionRequested?.Invoke(identity);
        }
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

        string previousPeriod = _periodBeforePendingCustomSelection ?? AllTime;
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
        if (_suppressFilterChanged)
        {
            return;
        }

        CancelRefresh();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record FilterSelection(
        string Agent,
        string Model,
        string Period,
        DateTime? CustomStartDate,
        DateTime? CustomEndDate);

}

internal static class ProjectValueFormatter
{
    public static string DescribeProjectName(ProjectUsageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsUnidentified)
        {
            return "未识别项目";
        }

        if (row.PathAvailability == PathAvailability.Available &&
            !string.IsNullOrWhiteSpace(row.ProjectPath))
        {
            string trimmed = row.ProjectPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                return leaf;
            }

            return row.ProjectPath;
        }

        return $"项目 {row.ProjectId}";
    }

    public static string DescribeProjectPath(ProjectUsageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsUnidentified)
        {
            return "工作目录无法可靠识别";
        }

        return row.PathAvailability == PathAvailability.Available &&
            !string.IsNullOrWhiteSpace(row.ProjectPath)
                ? row.ProjectPath
                : "完整工作目录不可取得";
    }

    public static string DescribeProjectName(
        string? projectPath,
        string? projectId,
        PathAvailability pathAvailability)
    {
        if (pathAvailability == PathAvailability.Available &&
            !string.IsNullOrWhiteSpace(projectPath))
        {
            string trimmed = projectPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(leaf) ? projectPath : leaf;
        }

        return !string.IsNullOrWhiteSpace(projectId)
            ? $"项目 {projectId}"
            : "所属项目无法识别";
    }

    public static string FormatTokens(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    public static string FormatCompact(long value) => value switch
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

    public static string FormatLocalTime(
        DateTimeOffset? utc,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        return utc is DateTimeOffset value
            ? TimeZoneInfo.ConvertTime(value, localTimeZone)
                .ToString("yyyy年M月d日 HH:mm", CultureInfo.CurrentCulture)
            : "—";
    }

    public static IReadOnlyList<ProjectMetricPresentation> CreateMetricRows(
        UsageMetricSet? metrics)
    {
        if (metrics is null)
        {
            return
            [
                new("缓存输入", "—"),
                new("未缓存输入", "—"),
                new("输出", "—")
            ];
        }

        return
        [
            new("缓存输入", SessionValueFormatter.FormatTokens(metrics.CacheRead)),
            new("未缓存输入", SessionValueFormatter.FormatTokens(metrics.UncachedInput)),
            new("输出", SessionValueFormatter.FormatTokens(metrics.Output))
        ];
    }

    public static string CalculateCacheHitRate(
        long requestCount,
        MetricAggregate uncachedInput,
        MetricAggregate cacheRead)
    {
        if (requestCount <= 0 ||
            uncachedInput.AvailableRecords != requestCount ||
            cacheRead.AvailableRecords != requestCount ||
            uncachedInput.UnavailableRecords != 0 ||
            cacheRead.UnavailableRecords != 0 ||
            uncachedInput.UnknownRecords != 0 ||
            cacheRead.UnknownRecords != 0 ||
            uncachedInput.Value is not long uncached ||
            cacheRead.Value is not long cached)
        {
            return "—";
        }

        decimal denominator = (decimal)uncached + cached;
        return denominator > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.0}%",
                ((decimal)cached / denominator) * 100m)
            : "—";
    }

    public static string? CreateDataNote(
        UsageMetricSet metrics,
        PricePresentation price)
    {
        bool metricGap = SessionValueFormatter.HasCoverageGap(metrics);
        return (metricGap, price.State) switch
        {
            (true, PricePresentationState.Partial) =>
                "部分 Token 字段不可取得，价格仅统计可计价记录。",
            (true, _) => "部分 Token 字段不可取得，缺失值未按 0 处理。",
            (false, PricePresentationState.Partial) =>
                "部分记录未完整计价，价格仅统计可计价记录。",
            (false, PricePresentationState.Unpriced) =>
                "当前记录缺少适用价格，Token 统计不受影响。",
            _ => null
        };
    }

    public static MetricAggregate Sum(IEnumerable<MetricAggregate> values)
    {
        MetricAggregate[] items = values.ToArray();
        int available = items.Sum(static item => item.AvailableRecords);
        int unavailable = items.Sum(static item => item.UnavailableRecords);
        int unknown = items.Sum(static item => item.UnknownRecords);
        long? total = available == 0
            ? null
            : items.Where(static item => item.Value.HasValue)
                .Sum(static item => item.Value!.Value);
        return new MetricAggregate(total, available, unavailable, unknown);
    }
}
