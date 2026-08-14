using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;

namespace AgenTally.UI.ViewModels;

public enum SessionPriceState
{
    NoData = 0,
    Unpriced = 1,
    Partial = 2,
    Complete = 3,
}

public sealed class SessionsViewModel : PageViewModel
{
    public const int SessionPageSize = 50;
    public const int TurnPageSize = 50;
    public const string AllAgents = DashboardViewModel.AllAgents;
    public const string AllModels = DashboardViewModel.AllModels;
    public const string AllProjects = DashboardViewModel.AllProjects;
    public const string AllTime = DashboardViewModel.AllTime;
    public const string Today = DashboardViewModel.Today;
    public const string SevenDays = DashboardViewModel.SevenDays;
    public const string ThirtyDays = DashboardViewModel.ThirtyDays;
    public const string NinetyDays = DashboardViewModel.NinetyDays;
    public const string Custom = DashboardViewModel.Custom;

    private static readonly ReadOnlyCollection<string> SupportedPeriods =
        Array.AsReadOnly(
            [AllTime, Today, SevenDays, ThirtyDays, NinetyDays, Custom]);

    private readonly IUsageQueryService _queries;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly StatisticsPeriodResolver _periodResolver;
    private ObservableCollection<string> _agentOptions = new([AllAgents]);
    private DateTime? _customEndDate;
    private DateTime? _customStartDate;
    private DateTime? _customEndBeforePendingSelection;
    private DateTime? _customStartBeforePendingSelection;
    private ObservableCollection<SessionListItemPresentation> _sessions = [];
    private SessionListItemPresentation? _selectedSession;
    private SessionDetailPresentation? _detail;
    private ObservableCollection<string> _modelOptions = new([AllModels]);
    private RootSessionCursor? _nextCursor;
    private string? _detailErrorMessage;
    private string _periodSummaryText = string.Empty;
    private ObservableCollection<ProjectFilterOption> _projectOptions =
        UsageFilterPresentation.CreateProjectOptions([]);
    private string _selectedAgent = AllAgents;
    private string _selectedModel = AllModels;
    private string _selectedPeriod = ThirtyDays;
    private string? _periodBeforePendingCustomSelection;
    private string _selectedProject = AllProjects;
    private bool _hasMoreSessions;
    private bool _isLoadingMoreSessions;
    private bool _isDetailLoading;
    private bool _hasMoreTurns;
    private bool _isLoadingMoreTurns;
    private bool _suppressSelectionLoad;
    private int _suppressFilterChanged;
    private bool _isCustomRangeSelectionPending;
    private StatisticsPeriodBounds? _lastEffectiveBounds;
    private UsageFilter? _detailTurnFilter;
    private int _loadedTurnCount;
    private int _selectedDetailTabIndex;
    private CancellationTokenSource? _turnCallCancellation;

    public SessionsViewModel(
        IUsageQueryService queries,
        Dispatcher dispatcher,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
        : base("会话", dispatcher)
    {
        ArgumentNullException.ThrowIfNull(queries);
        _queries = queries;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _periodResolver = new StatisticsPeriodResolver(_localTimeZone);
        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None));
        LoadMoreSessionsCommand = new AsyncRelayCommand(
            LoadMoreSessionsAsync,
            CanLoadMoreSessions);
        LoadMoreTurnsCommand = new AsyncRelayCommand(
            LoadMoreTurnsAsync,
            CanLoadMoreTurns);
        OpenProjectCommand = new RelayCommand(
            OpenProject,
            static parameter => parameter is string value &&
                !string.IsNullOrWhiteSpace(value));
        CommitCustomRangeCommand = new RelayCommand(CommitCustomRange);
        CancelCustomRangeCommand = new RelayCommand(CancelCustomRangeSelection);
    }

    public event Action<string>? ProjectRequested;

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

    public string PeriodSummaryText
    {
        get => _periodSummaryText;
        private set => SetProperty(ref _periodSummaryText, value);
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

    public ObservableCollection<SessionListItemPresentation> Sessions
    {
        get => _sessions;
        private set => SetProperty(ref _sessions, value);
    }

    public bool HasSessions => Sessions.Count > 0;

    public bool HasMoreSessions
    {
        get => _hasMoreSessions;
        private set
        {
            if (SetProperty(ref _hasMoreSessions, value))
            {
                LoadMoreSessionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingMoreSessions
    {
        get => _isLoadingMoreSessions;
        private set
        {
            if (SetProperty(ref _isLoadingMoreSessions, value))
            {
                LoadMoreSessionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public SessionListItemPresentation? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                if (!_suppressSelectionLoad && value is not null)
                {
                    _ = LoadDetailForSelectionAsync(value.Identity);
                }
            }
        }
    }

    public bool HasSelection => SelectedSession is not null;

    public SessionDetailPresentation? Detail
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

    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        private set
        {
            if (SetProperty(ref _isDetailLoading, value))
            {
                LoadMoreTurnsCommand.RaiseCanExecuteChanged();
            }
        }
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

    public bool HasMoreTurns
    {
        get => _hasMoreTurns;
        private set
        {
            if (SetProperty(ref _hasMoreTurns, value))
            {
                LoadMoreTurnsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingMoreTurns
    {
        get => _isLoadingMoreTurns;
        private set
        {
            if (SetProperty(ref _isLoadingMoreTurns, value))
            {
                LoadMoreTurnsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand LoadMoreSessionsCommand { get; }

    public AsyncRelayCommand LoadMoreTurnsCommand { get; }

    public RelayCommand OpenProjectCommand { get; }

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
        if (!showFeedback &&
            await ReadOnDispatcherAsync(() => IsLoadingMoreTurns))
        {
            return;
        }

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
            UsageFilter listFilter = CreateFilter(
                selection,
                bounds,
                SessionPageSize);
            Task<RootSessionPage> pageTask = _queries.GetRootSessionsAsync(
                new RootSessionPageRequest(listFilter, SessionPageSize),
                session.Token);
            Task<UsageFilterValues> filtersTask =
                _queries.GetFilterValuesAsync(listFilter, session.Token);
            Task<UsageOverview?> overviewTask =
                selection.Period == AllTime
                    ? ReadOverviewAsync(listFilter, session.Token)
                    : Task.FromResult<UsageOverview?>(null);
            await Task.WhenAll(pageTask, filtersTask, overviewTask);

            RootSessionPage page = pageTask.Result;
            StatisticsPeriodBounds effectiveBounds = ResolveEffectiveBounds(
                selection,
                bounds,
                overviewTask.Result,
                page.Items);
            RootSessionIdentity? detailIdentity = null;
            bool filterReset = false;
            await ApplyIfCurrentAsync(session, () =>
            {
                SessionListItemPresentation[] items = page.Items
                    .Select(row => new SessionListItemPresentation(
                        row,
                        _localTimeZone))
                    .ToArray();
                SetCollectionIfChanged(ref _sessions, items, nameof(Sessions));
                OnPropertyChanged(nameof(HasSessions));
                _nextCursor = page.NextCursor;
                HasMoreSessions = page.NextCursor is not null;
                IsLoadingMoreSessions = false;
                SessionListItemPresentation? selected =
                    _selectedSession is not null
                        ? items.FirstOrDefault(item =>
                            item.Identity == _selectedSession.Identity)
                        : null;
                selected ??= items.FirstOrDefault();
                detailIdentity = selected?.Identity;
                _suppressSelectionLoad = true;
                try
                {
                    SelectedSession = selected;
                }
                finally
                {
                    _suppressSelectionLoad = false;
                }
                _lastEffectiveBounds = effectiveBounds;
                PeriodSummaryText = FormatPeriodSummary(
                    selection.Period,
                    effectiveBounds,
                    overviewTask.Result,
                    page.Items);
                ApplyFilterOptions(filtersTask.Result, out filterReset);
            });
            if (filterReset)
            {
                await RunOnDispatcherAsync(OnFilterChanged);
                return;
            }

            if (detailIdentity is null)
            {
                await ApplyIfCurrentAsync(session, () =>
                {
                    Detail = null;
                    DetailErrorMessage = null;
                    IsDetailLoading = false;
                    HasMoreTurns = false;
                    IsLoadingMoreTurns = false;
                    _loadedTurnCount = 0;
                    _detailTurnFilter = null;
                });
            }
            else
            {
                await LoadDetailCoreAsync(
                    detailIdentity,
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

    public async Task<bool> SelectSessionAsync(
        RootSessionIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using IDisposable feedback = await BeginInteractionFeedbackAsync();
        RefreshSession session = BeginRefresh(cancellationToken);
        try
        {
            FilterSelection selection = await ReadSelectionAsync();
            StatisticsPeriodBounds bounds = ResolvePeriodBounds(selection);
            UsageFilter turnFilter = CreateFilter(
                selection,
                bounds,
                TurnPageSize,
                offset: 0);
            await ApplyIfCurrentAsync(session, () =>
            {
                _turnCallCancellation?.Cancel();
                _turnCallCancellation?.Dispose();
                _turnCallCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                SelectedDetailTabIndex = 0;
                IsDetailLoading = true;
                DetailErrorMessage = null;
                HasMoreTurns = false;
                IsLoadingMoreTurns = false;
                _loadedTurnCount = 0;
                _detailTurnFilter = null;
            });
            Task<RootSessionDetail?> detailTask =
                _queries.GetRootSessionDetailAsync(
                    CreateFilter(selection, bounds, SessionPageSize),
                    identity,
                    session.Token);
            Task<TurnUsagePage> turnsTask = _queries.GetTurnsAsync(
                turnFilter,
                identity,
                session.Token);
            await Task.WhenAll(detailTask, turnsTask);
            if (detailTask.Result is not RootSessionDetail detailValue)
            {
                await ApplyIfCurrentAsync(session, () =>
                {
                    Detail = null;
                    DetailErrorMessage = "未找到对应的根会话。";
                    IsDetailLoading = false;
                });
                return false;
            }

            await ApplyIfCurrentAsync(session, () =>
            {
                SessionListItemPresentation? item = Sessions.FirstOrDefault(value =>
                    value.Identity == identity);
                if (item is null)
                {
                    item = new SessionListItemPresentation(
                        detailValue.Summary,
                        _localTimeZone);
                    Sessions.Insert(0, item);
                    OnPropertyChanged(nameof(HasSessions));
                }

                _suppressSelectionLoad = true;
                try
                {
                    SelectedSession = item;
                }
                finally
                {
                    _suppressSelectionLoad = false;
                }

                var detail = new SessionDetailPresentation(
                    detailValue,
                    _localTimeZone,
                    turn => LoadTurnCallsAsync(
                        identity,
                        turn,
                        _turnCallCancellation?.Token ??
                        CancellationToken.None));
                detail.ApplyTurns(turnsTask.Result, _localTimeZone);
                _loadedTurnCount = turnsTask.Result.Turns.Count;
                HasMoreTurns = turnsTask.Result.Turns.Count == TurnPageSize;
                _detailTurnFilter = turnFilter;
                Detail = detail;
                IsDetailLoading = false;
            });
            return true;
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            await ApplyIfCurrentAsync(session, () =>
            {
                DetailErrorMessage = UiErrorMessageClassifier.Classify(exception);
                IsDetailLoading = false;
            });
            return false;
        }
        finally
        {
            await EndRefreshAsync(session);
        }
    }

    private bool CanLoadMoreSessions() =>
        HasMoreSessions && !IsLoadingMoreSessions && !IsLoading;

    private async Task LoadMoreSessionsAsync()
    {
        RootSessionCursor? cursor =
            await ReadOnDispatcherAsync(() => _nextCursor);
        if (cursor is null)
        {
            return;
        }

        FilterSelection selection = await ReadSelectionAsync();
        StatisticsPeriodBounds bounds = ResolvePeriodBounds(selection);
        RefreshSession session = BeginRefresh(CancellationToken.None);
        await ApplyIfCurrentAsync(session, () => IsLoadingMoreSessions = true);
        try
        {
            RootSessionPage page = await _queries.GetRootSessionsAsync(
                new RootSessionPageRequest(
                    CreateFilter(selection, bounds, SessionPageSize),
                    SessionPageSize,
                    cursor),
                session.Token);
            await ApplyIfCurrentAsync(session, () =>
            {
                foreach (RootSessionSummaryRow row in page.Items)
                {
                    Sessions.Add(new SessionListItemPresentation(
                        row,
                        _localTimeZone));
                }

                OnPropertyChanged(nameof(HasSessions));
                _nextCursor = page.NextCursor;
                HasMoreSessions = page.NextCursor is not null;
                IsLoadingMoreSessions = false;
            });
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

    private bool CanLoadMoreTurns() =>
        HasMoreTurns && !IsLoadingMoreTurns && !IsDetailLoading;

    private async Task LoadMoreTurnsAsync()
    {
        RootSessionIdentity? identity =
            await ReadOnDispatcherAsync(() => _selectedSession?.Identity);
        if (identity is null)
        {
            return;
        }

        FilterSelection selection = await ReadSelectionAsync();
        StatisticsPeriodBounds bounds = ResolvePeriodBounds(selection);
        RefreshSession session = BeginRefresh(CancellationToken.None);
        await ApplyIfCurrentAsync(session, () => IsLoadingMoreTurns = true);
        try
        {
            TurnUsagePage page = await _queries.GetTurnsAsync(
                CreateFilter(
                    selection,
                    bounds,
                    TurnPageSize,
                    _loadedTurnCount),
                identity,
                session.Token);
            await ApplyIfCurrentAsync(session, () =>
            {
                Detail?.AppendTurns(page, _localTimeZone);
                _loadedTurnCount += page.Turns.Count;
                HasMoreTurns = page.Turns.Count == TurnPageSize;
                IsLoadingMoreTurns = false;
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
                IsLoadingMoreTurns = false;
            });
        }
        finally
        {
            await EndRefreshAsync(session);
        }
    }

    private async Task LoadDetailForSelectionAsync(RootSessionIdentity identity)
    {
        using IDisposable feedback = await BeginInteractionFeedbackAsync();
        RefreshSession session = BeginRefresh(CancellationToken.None);
        try
        {
            FilterSelection selection = await ReadSelectionAsync();
            StatisticsPeriodBounds bounds = ResolvePeriodBounds(selection);
            await LoadDetailCoreAsync(
                identity,
                selection,
                bounds,
                session);
        }
        finally
        {
            await EndRefreshAsync(session);
        }
    }

    private async Task LoadDetailCoreAsync(
        RootSessionIdentity identity,
        FilterSelection selection,
        StatisticsPeriodBounds bounds,
        RefreshSession session)
    {
        SessionDetailPresentation? previousDetail = null;
        bool preserveTurnDepth = false;
        int requestedTurnCount = TurnPageSize;
        UsageFilter turnFilter = CreateFilter(
            selection,
            bounds,
            TurnPageSize,
            offset: 0);
        await ApplyIfCurrentAsync(session, () =>
        {
            if (Detail?.Identity == identity)
            {
                previousDetail = Detail;
                preserveTurnDepth = _detailTurnFilter == turnFilter &&
                    _loadedTurnCount > 0;
                if (preserveTurnDepth)
                {
                    requestedTurnCount = Math.Max(
                        TurnPageSize,
                        _loadedTurnCount);
                }
            }

            _turnCallCancellation?.Cancel();
            _turnCallCancellation?.Dispose();
            _turnCallCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            IsLoadingMoreTurns = false;
            DetailErrorMessage = null;
            if (!preserveTurnDepth)
            {
                IsDetailLoading = true;
                HasMoreTurns = false;
                IsLoadingMoreTurns = false;
                _loadedTurnCount = 0;
                _detailTurnFilter = null;
            }
        });
        try
        {
            Task<RootSessionDetail?> detailTask =
                _queries.GetRootSessionDetailAsync(
                    CreateFilter(selection, bounds, SessionPageSize),
                    identity,
                    session.Token);
            Task<TurnSnapshot> turnsTask = ReadTurnSnapshotAsync(
                selection,
                bounds,
                identity,
                requestedTurnCount,
                preserveTurnDepth,
                session.Token);
            await Task.WhenAll(detailTask, turnsTask);
            var refreshedTurnCalls =
                new Dictionary<string, IReadOnlyList<TurnCallUsageRow>>(
                    StringComparer.Ordinal);
            var turnCallErrors = new Dictionary<string, string>(
                StringComparer.Ordinal);
            if (previousDetail is not null)
            {
                HashSet<string> refreshedTurnIds = turnsTask.Result.Page.Turns
                    .Select(turn => turn.TurnIdHash)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (string turnIdHash in previousDetail.Turns
                             .Where(turn => turn.IsExpanded)
                             .Select(turn => turn.TurnIdHash)
                             .Where(refreshedTurnIds.Contains))
                {
                    try
                    {
                        refreshedTurnCalls[turnIdHash] =
                            await _queries.GetTurnCallsAsync(
                                CreateFilter(
                                    selection,
                                    bounds,
                                    TurnPageSize,
                                    offset: 0),
                                identity,
                                turnIdHash,
                                session.Token);
                    }
                    catch (OperationCanceledException)
                        when (session.Token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        turnCallErrors[turnIdHash] =
                            UiErrorMessageClassifier.Classify(exception);
                    }
                }
            }

            await ApplyIfCurrentAsync(session, () =>
            {
                SessionDetailPresentation? detail = detailTask.Result is { } value
                    ? new SessionDetailPresentation(
                        value,
                        _localTimeZone,
                        turn => LoadTurnCallsAsync(
                            identity,
                            turn,
                            _turnCallCancellation?.Token ??
                            CancellationToken.None))
                    : null;
                detail?.ApplyTurns(turnsTask.Result.Page, _localTimeZone);
                if (detail is not null && previousDetail is not null)
                {
                    detail.RestoreInteractionState(
                        previousDetail,
                        refreshedTurnCalls,
                        turnCallErrors,
                        _localTimeZone);
                }

                _loadedTurnCount = turnsTask.Result.Page.Turns.Count;
                HasMoreTurns = turnsTask.Result.HasMore;
                _detailTurnFilter = turnFilter;
                Detail = detail;
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

    private async Task<TurnSnapshot> ReadTurnSnapshotAsync(
        FilterSelection selection,
        StatisticsPeriodBounds bounds,
        RootSessionIdentity identity,
        int requestedTurnCount,
        bool includeLookahead,
        CancellationToken cancellationToken)
    {
        int targetCount = Math.Max(TurnPageSize, requestedTurnCount);
        int readCount = includeLookahead && targetCount < int.MaxValue
            ? targetCount + 1
            : targetCount;
        var turns = new List<TurnUsageRow>(readCount);
        TurnUsagePage? firstPage = null;
        int offset = 0;
        while (turns.Count < readCount)
        {
            int limit = Math.Min(1000, readCount - turns.Count);
            TurnUsagePage page = await _queries.GetTurnsAsync(
                CreateFilter(selection, bounds, limit, offset),
                identity,
                cancellationToken);
            firstPage ??= page;
            turns.AddRange(page.Turns);
            if (page.Turns.Count < limit)
            {
                break;
            }

            offset += page.Turns.Count;
        }

        if (firstPage is null)
        {
            throw new InvalidOperationException(
                "Prompt snapshot query did not execute.");
        }
        bool hasMore = includeLookahead
            ? turns.Count > targetCount
            : turns.Count == targetCount;
        IReadOnlyList<TurnUsageRow> visibleTurns = turns.Count > targetCount
            ? turns.Take(targetCount).ToArray()
            : turns;
        return new TurnSnapshot(
            firstPage with { Turns = visibleTurns },
            hasMore);
    }

    private async Task LoadTurnCallsAsync(
        RootSessionIdentity identity,
        TurnUsagePresentation turn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        using IDisposable feedback = await BeginInteractionFeedbackAsync();
        await RunOnDispatcherAsync(turn.SetLoading);
        try
        {
            FilterSelection selection = await ReadSelectionAsync();
            StatisticsPeriodBounds bounds = ResolvePeriodBounds(selection);
            IReadOnlyList<TurnCallUsageRow> calls =
                await _queries.GetTurnCallsAsync(
                    CreateFilter(
                        selection,
                        bounds,
                        TurnPageSize,
                        offset: 0),
                    identity,
                    turn.TurnIdHash,
                    cancellationToken);
            await RunOnDispatcherAsync(() =>
            {
                if (SelectedSession?.Identity == identity &&
                    Detail?.Turns.Contains(turn) is true)
                {
                    turn.ApplyCalls(calls, _localTimeZone);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnDispatcherAsync(() =>
                turn.SetError(UiErrorMessageClassifier.Classify(exception)));
        }
    }

    private async Task<UsageOverview?> ReadOverviewAsync(
        UsageFilter filter,
        CancellationToken cancellationToken) =>
        await _queries.GetOverviewAsync(filter, cancellationToken);

    private FilterSelection ReadSelection() => new(
        SelectedPeriod,
        SelectedAgent,
        SelectedModel,
        SelectedProject,
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
        SetCollectionIfChanged(
            ref _projectOptions,
            UsageFilterPresentation.CreateProjectOptions(values.Projects),
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

    private StatisticsPeriodBounds ResolvePeriodBounds(
        FilterSelection selection) => _periodResolver.Resolve(
        selection.Period,
        _timeProvider.GetUtcNow(),
        selection.CustomStartDate,
        selection.CustomEndDate);

    private StatisticsPeriodBounds ResolveEffectiveBounds(
        FilterSelection selection,
        StatisticsPeriodBounds queryBounds,
        UsageOverview? overview,
        IReadOnlyList<RootSessionSummaryRow> sessions)
    {
        if (selection.Period != AllTime)
        {
            return queryBounds;
        }

        DateTimeOffset? first = overview?.FirstOccurredAtUtc;
        if (first is null && sessions.Count > 0)
        {
            first = sessions.Min(static session => session.StartedAtUtc);
        }

        DateTime localStart = first is DateTimeOffset value
            ? TimeZoneInfo.ConvertTime(value, _localTimeZone).Date
            : queryBounds.LocalEndExclusive.AddDays(-1);
        return _periodResolver.CreateBounds(
            localStart,
            queryBounds.LocalEndExclusive);
    }

    private static UsageFilter CreateFilter(
        FilterSelection selection,
        StatisticsPeriodBounds bounds,
        int limit,
        int offset = 0) => new(
        bounds.StartInclusiveUtc,
        bounds.EndExclusiveUtc,
        selection.Agent == AllAgents ? null : selection.Agent,
        selection.Model == AllModels ? null : selection.Model,
        limit,
        offset,
        projectId: selection.Project == AllProjects
            ? null
            : selection.Project);

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

    private string FormatPeriodSummary(
        string period,
        StatisticsPeriodBounds bounds,
        UsageOverview? overview,
        IReadOnlyList<RootSessionSummaryRow> sessions)
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
                $"{bounds.LocalStart:yyyy年M月d日}至{endInclusive:M月d日}");
        }

        DateTimeOffset? first = overview?.FirstOccurredAtUtc;
        if (first is null && sessions.Count > 0)
        {
            first = sessions.Min(static session => session.StartedAtUtc);
        }

        if (first is null)
        {
            return string.Create(
                CultureInfo.CurrentCulture,
                $"截至 {endInclusive:yyyy年M月d日}");
        }

        DateTime localStart = TimeZoneInfo.ConvertTime(
            first.Value,
            _localTimeZone).Date;
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{localStart:yyyy年M月d日}至{endInclusive:M月d日}");
    }

    private void OpenProject(object? parameter)
    {
        if (parameter is string projectId &&
            !string.IsNullOrWhiteSpace(projectId))
        {
            ProjectRequested?.Invoke(projectId);
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

        DateTime local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return new CustomTimeRange(local, local.AddHours(1)).StartLocal;
    }

    private void OnFilterChanged()
    {
        if (Volatile.Read(ref _suppressFilterChanged) != 0)
        {
            return;
        }

        CancelRefresh();
        _turnCallCancellation?.Cancel();
        _detailTurnFilter = null;
        _loadedTurnCount = 0;
        HasMoreTurns = false;
        IsLoadingMoreTurns = false;
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record FilterSelection(
        string Period,
        string Agent,
        string Model,
        string Project,
        DateTime? CustomStartDate,
        DateTime? CustomEndDate);

    private sealed record TurnSnapshot(
        TurnUsagePage Page,
        bool HasMore);

}

public sealed record SessionListItemPresentation
{
    private readonly RootSessionSummaryRow _row;

    public SessionListItemPresentation(
        RootSessionSummaryRow row,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        _row = row;
        TitleText = SessionValueFormatter.DescribeSessionName(row.SessionName);
        ProjectPathText = SessionValueFormatter.DescribeProject(
            row.ProjectPath,
            row.ProjectId,
            row.ProjectPathAvailability);
        DateTime lastActivity = TimeZoneInfo.ConvertTime(
            row.LastActivityUtc,
            localTimeZone).DateTime;
        SubtitleText = string.Create(
            CultureInfo.CurrentCulture,
            $"{lastActivity:M月d日 HH:mm} 最后活跃 · {row.RequestCount:N0} 次调用{(row.SideSessionCount > 0 ? $" · {row.SideSessionCount} 个子会话" : string.Empty)}");
        TokensText = SessionValueFormatter.FormatTokens(
            row.Metrics.NormalizedTotal);
        (PriceText, PriceState, _) = SessionValueFormatter.DescribePrice(
            row.Pricing);
    }

    public string RootSessionId => _row.RootSessionId;

    public RootSessionIdentity Identity => _row.Identity;

    public string TitleText { get; }

    public string ProjectPathText { get; }

    public string SubtitleText { get; }

    public string TokensText { get; }

    public string PriceText { get; }

    public SessionPriceState PriceState { get; }
}

public sealed class SessionDetailPresentation
{
    private readonly Func<TurnUsagePresentation, Task>? _loadTurnCalls;

    public SessionDetailPresentation(
        RootSessionDetail detail,
        TimeZoneInfo localTimeZone,
        Func<TurnUsagePresentation, Task>? loadTurnCalls = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        RootSessionSummaryRow summary = detail.Summary;
        Identity = summary.Identity;
        TitleText = SessionValueFormatter.DescribeSessionName(summary.SessionName);
        ProjectPathText = SessionValueFormatter.DescribeProject(
            summary.ProjectPath,
            summary.ProjectId,
            summary.ProjectPathAvailability);
        ProjectId = summary.ProjectId;
        ProjectNameText = ProjectValueFormatter.DescribeProjectName(
            summary.ProjectPath,
            summary.ProjectId,
            summary.ProjectPathAvailability);
        CanOpenProject = !string.IsNullOrWhiteSpace(summary.ProjectId);
        ProjectNote = summary.ProjectPathAvailability == PathAvailability.Unavailable
            ? "项目路径不可取得，显示的是项目标识。"
            : null;
        DateTime started = TimeZoneInfo.ConvertTime(
            summary.StartedAtUtc,
            localTimeZone).DateTime;
        DateTime lastActivity = TimeZoneInfo.ConvertTime(
            summary.LastActivityUtc,
            localTimeZone).DateTime;
        TimeRangeText = string.Create(
            CultureInfo.CurrentCulture,
            $"{started:M月d日 HH:mm} — {lastActivity:M月d日 HH:mm} · {summary.RequestCount:N0} 次调用");
        TotalTokensText = SessionValueFormatter.FormatTokens(
            summary.Metrics.NormalizedTotal);
        MetricsNote = SessionValueFormatter.HasCoverageGap(summary.Metrics)
            ? "部分用量字段不可取得，合计仅覆盖已统计到的记录。"
            : null;
        (PriceText, PriceState, PriceNote) =
            SessionValueFormatter.DescribePrice(summary.Pricing);
        _loadTurnCalls = loadTurnCalls;
        Contributions = new ObservableCollection<SessionContributionPresentation>(
            detail.Contributions.Select(row =>
                new SessionContributionPresentation(row)));
        Models = new ObservableCollection<SessionModelUsagePresentation>(
            detail.Models.Select(row => new SessionModelUsagePresentation(row)));
    }

    public RootSessionIdentity Identity { get; }

    public string TitleText { get; }

    public string ProjectPathText { get; }

    public string? ProjectId { get; }

    public string ProjectNameText { get; }

    public bool CanOpenProject { get; }

    public string? ProjectNote { get; }

    public string TimeRangeText { get; }

    public string TotalTokensText { get; }

    public string? MetricsNote { get; }

    public string PriceText { get; }

    public string PromptTurnCountText { get; private set; } = "—";

    public SessionPriceState PriceState { get; }

    public string? PriceNote { get; }

    public bool HasPriceNote => PriceNote is not null;

    public ObservableCollection<SessionContributionPresentation> Contributions { get; }

    public ObservableCollection<SessionModelUsagePresentation> Models { get; }

    public ObservableCollection<TurnUsagePresentation> Turns { get; } = [];

    public TurnCoverageStatus TurnCoverage { get; private set; } =
        TurnCoverageStatus.NoData;

    public string? TurnCoverageNote { get; private set; }

    public bool HasTurnCoverageNote => TurnCoverageNote is not null;

    public string? UnattributedText { get; private set; }

    public bool HasUnattributed => UnattributedText is not null;

    public void ApplyTurns(TurnUsagePage page, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        Turns.Clear();
        AppendTurns(page, localTimeZone);
    }

    public void AppendTurns(TurnUsagePage page, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        TurnCoverage = page.Coverage;
        PromptTurnCountText = page.Coverage switch
        {
            TurnCoverageStatus.Complete =>
                page.PromptTurnCount.ToString(
                    "N0",
                    CultureInfo.CurrentCulture),
            TurnCoverageStatus.Partial =>
                "≥" + page.PromptTurnCount.ToString(
                    "N0",
                    CultureInfo.CurrentCulture),
            TurnCoverageStatus.NoData => "0",
            _ => "—",
        };
        TurnCoverageNote = page.Coverage switch
        {
            TurnCoverageStatus.NoData => "该会话暂无 Prompt 用量记录。",
            TurnCoverageStatus.Partial =>
                "部分调用无法可靠归属到 Prompt，已单独列出。",
            TurnCoverageStatus.Unsupported =>
                "当前来源缺少可靠 Prompt 轮次元数据，仅显示汇总用量。",
            _ => null,
        };
        foreach (TurnUsageRow row in page.Turns)
        {
            Turns.Add(new TurnUsagePresentation(
                row,
                localTimeZone,
                _loadTurnCalls));
        }

        string unattributedTokens = SessionValueFormatter.FormatTokens(
            page.Unattributed.Metrics.NormalizedTotal);
        UnattributedText = page.Unattributed.CallCount > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Prompt 归属未确定：{page.Unattributed.CallCount:N0} 次调用{(unattributedTokens == "—" ? string.Empty : $" · {unattributedTokens} Token")}")
            : null;
    }

    public void RestoreInteractionState(
        SessionDetailPresentation previous,
        IReadOnlyDictionary<string, IReadOnlyList<TurnCallUsageRow>>
            refreshedTurnCalls,
        IReadOnlyDictionary<string, string> turnCallErrors,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(refreshedTurnCalls);
        ArgumentNullException.ThrowIfNull(turnCallErrors);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        if (previous.Identity != Identity)
        {
            return;
        }

        Dictionary<string, SessionContributionPresentation>
            previousContributions = previous.Contributions
                .GroupBy(item => item.SessionId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.Ordinal);
        foreach (SessionContributionPresentation contribution in Contributions)
        {
            if (previousContributions.TryGetValue(
                    contribution.SessionId,
                    out SessionContributionPresentation? prior))
            {
                contribution.IsExpanded = prior.IsExpanded;
            }
        }

        Dictionary<string, TurnUsagePresentation> previousTurns =
            previous.Turns
                .GroupBy(item => item.TurnIdHash, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.Ordinal);
        foreach (TurnUsagePresentation turn in Turns)
        {
            if (previousTurns.TryGetValue(
                    turn.TurnIdHash,
                    out TurnUsagePresentation? prior) &&
                prior.IsExpanded)
            {
                turn.RestoreExpandedState(prior);
                if (refreshedTurnCalls.TryGetValue(
                        turn.TurnIdHash,
                        out IReadOnlyList<TurnCallUsageRow>? calls))
                {
                    turn.ApplyCalls(calls, localTimeZone);
                }
                else if (turnCallErrors.TryGetValue(
                             turn.TurnIdHash,
                             out string? error))
                {
                    turn.SetError(error);
                }
            }
        }
    }
}

public sealed class SessionContributionPresentation : ObservableObject
{
    private bool _isExpanded;

    public SessionContributionPresentation(SessionContributionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        SessionId = row.SessionId;
        KindText = row.SessionKind switch
        {
            _ when row.SessionRole is SessionRole.Guardian => "Guardian",
            _ when row.SessionRole is SessionRole.Internal => "Codex 内部任务",
            _ when row.SessionRole is SessionRole.Subagent => "subagent",
            SessionKind.Primary => "主会话",
            SessionKind.Side => "子会话",
            _ => "会话",
        };
        SessionIdText = SessionValueFormatter.ShortenId(row.SessionId);
        Indent = new Thickness(row.Depth * 18, 0, 0, 0);
        RequestCountText = row.RequestCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        TokensText = SessionValueFormatter.FormatTokens(
            row.Metrics.NormalizedTotal);
        (PriceText, PriceState, _) = SessionValueFormatter.DescribePrice(
            row.Pricing);
        HasModels = row.Models.Count > 0;
        Models = row.Models
            .Select(model => new SessionModelUsagePresentation(model))
            .ToArray();
        ToggleExpandedCommand = new RelayCommand(
            _ => IsExpanded = !IsExpanded,
            _ => HasModels);
    }

    public string SessionId { get; }

    public string KindText { get; }

    public string SessionIdText { get; }

    public Thickness Indent { get; }

    public string RequestCountText { get; }

    public string TokensText { get; }

    public string PriceText { get; }

    public SessionPriceState PriceState { get; }

    public bool HasModels { get; }

    public IReadOnlyList<SessionModelUsagePresentation> Models { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public RelayCommand ToggleExpandedCommand { get; }
}

public sealed record SessionModelUsagePresentation
{
    public SessionModelUsagePresentation(SessionModelUsageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ModelText = row.Model;
        RequestCountText = row.RequestCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        TokensText = SessionValueFormatter.FormatTokens(
            row.Metrics.NormalizedTotal);
        (PriceText, PriceState, _) = SessionValueFormatter.DescribePrice(
            row.Pricing);
    }

    public string ModelText { get; }

    public string RequestCountText { get; }

    public string TokensText { get; }

    public string PriceText { get; }

    public SessionPriceState PriceState { get; }
}

public sealed class TurnUsagePresentation : ObservableObject
{
    private readonly Func<TurnUsagePresentation, Task>? _loadCalls;
    private bool _isExpanded;
    private bool _isLoading;
    private bool _isLoaded;
    private string? _errorText;

    public TurnUsagePresentation(
        TurnUsageRow row,
        TimeZoneInfo localTimeZone,
        Func<TurnUsagePresentation, Task>? loadCalls = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        _loadCalls = loadCalls;
        TurnIdHash = row.TurnIdHash;
        DateTime started = TimeZoneInfo.ConvertTime(
            row.StartedAtUtc,
            localTimeZone).DateTime;
        TimeText = string.Create(
            CultureInfo.CurrentCulture,
            $"{started:M月d日 HH:mm}");
        CallCountText = row.CallCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        ToolCallCountText = row.ToolCallCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        PromptText = row.PromptPreview ?? "Prompt 摘要不可取得";
        UserMessageText = row.UserMessageCount > 1
            ? $"含 {row.UserMessageCount:N0} 条用户消息"
            : string.Empty;
        HasUserMessageNote = row.UserMessageCount > 1;
        ActivityText = HasUserMessageNote
            ? $"{CallCountText} 次模型调用 · {ToolCallCountText} 次工具调用 · {UserMessageText}"
            : $"{CallCountText} 次模型调用 · {ToolCallCountText} 次工具调用";
        TokensText = SessionValueFormatter.FormatTokens(
            row.Metrics.NormalizedTotal);
        long tokenValue = row.Metrics.NormalizedTotal.Value.GetValueOrDefault();
        RelativeUsage = row.MaxPromptTokens > 0
            ? Math.Clamp(
                (double)tokenValue / row.MaxPromptTokens,
                0,
                1)
            : 0;
        (PriceText, PriceState, _) = SessionValueFormatter.DescribePrice(
            row.Pricing);
        ToggleExpandedCommand = new AsyncRelayCommand(ToggleExpandedAsync);
    }

    public string TurnIdHash { get; }

    public string TimeText { get; }

    public string PromptText { get; }

    public string UserMessageText { get; }

    public bool HasUserMessageNote { get; }

    public string ActivityText { get; }

    public string CallCountText { get; }

    public string ToolCallCountText { get; }

    public string TokensText { get; }

    public string PriceText { get; }

    public SessionPriceState PriceState { get; }

    public double RelativeUsage { get; }

    public ObservableCollection<TurnCallUsagePresentation> Calls { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => SetProperty(ref _isLoaded, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorText is not null;

    public AsyncRelayCommand ToggleExpandedCommand { get; }

    public void SetLoading()
    {
        IsExpanded = true;
        IsLoading = true;
        ErrorText = null;
    }

    public void ApplyCalls(
        IReadOnlyList<TurnCallUsageRow> rows,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Calls.Clear();
        int index = 0;
        foreach (TurnCallUsageRow row in rows)
        {
            Calls.Add(new TurnCallUsagePresentation(
                ++index,
                row,
                localTimeZone));
        }

        IsLoaded = true;
        IsLoading = false;
        IsExpanded = true;
        ErrorText = null;
    }

    public void SetError(string message)
    {
        ErrorText = message;
        IsLoading = false;
        IsExpanded = true;
    }

    internal void RestoreExpandedState(TurnUsagePresentation previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        Calls.Clear();
        foreach (TurnCallUsagePresentation call in previous.Calls)
        {
            Calls.Add(call);
        }

        IsLoaded = previous.IsLoaded;
        IsLoading = false;
        ErrorText = previous.ErrorText;
        IsExpanded = true;
    }

    private async Task ToggleExpandedAsync()
    {
        if (IsExpanded && (IsLoaded || HasError))
        {
            IsExpanded = false;
            return;
        }

        IsExpanded = true;
        if (!IsLoaded && !IsLoading && _loadCalls is not null)
        {
            await _loadCalls(this);
        }
    }
}

public sealed record TurnCallUsagePresentation
{
    public TurnCallUsagePresentation(
        int index,
        TurnCallUsageRow row,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        TitleText = $"模型调用 #{index:N0}";
        DateTime local = TimeZoneInfo.ConvertTime(
            row.OccurredAtUtc,
            localTimeZone).DateTime;
        TimeText = local.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        ModelText = row.Model;
        SourceText = row.SessionRole switch
        {
            SessionRole.Guardian => "Guardian",
            SessionRole.Internal => "Codex 内部任务",
            SessionRole.Subagent => "subagent",
            _ when row.SessionKind is SessionKind.Primary => "主会话",
            _ => "子会话"
        };
        MetricsText = string.Create(
            CultureInfo.InvariantCulture,
            $"缓存输入 {SessionValueFormatter.FormatTokens(row.Metrics.CacheRead)} · 未缓存输入 {SessionValueFormatter.FormatTokens(row.Metrics.UncachedInput)} · 输出 {SessionValueFormatter.FormatTokens(row.Metrics.Output)}");
        TokensText = SessionValueFormatter.FormatTokens(
            row.Metrics.NormalizedTotal);
        (PriceText, PriceState, _) = SessionValueFormatter.DescribePrice(
            row.Pricing);
        ToolText = string.Join(
            " · ",
            row.Tools
                .GroupBy(static value => value, StringComparer.Ordinal)
                .Select(group => group.Count() == 1
                    ? group.Key
                    : $"{group.Key} ×{group.Count():N0}"));
        HasTools = ToolText.Length > 0;
    }

    public string TitleText { get; }

    public string TimeText { get; }

    public string ModelText { get; }

    public string SourceText { get; }

    public string MetricsText { get; }

    public string TokensText { get; }

    public string PriceText { get; }

    public SessionPriceState PriceState { get; }

    public string ToolText { get; }

    public bool HasTools { get; }
}

internal static class SessionValueFormatter
{
    public static string DescribeSessionName(string? sessionName) =>
        string.IsNullOrWhiteSpace(sessionName)
            ? "未命名会话"
            : sessionName;

    public static string FormatTokens(MetricAggregate aggregate) =>
        aggregate.Value is long value
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : "—";

    public static (string Text, SessionPriceState State, string? Note)
        DescribePrice(PricingAggregate? pricing)
    {
        PricePresentation presentation =
            PricePresentationFormatter.Describe(pricing);
        return presentation.State switch
        {
            PricePresentationState.Complete =>
                (presentation.ValueText, SessionPriceState.Complete, null),
            PricePresentationState.Partial =>
                (
                    presentation.ValueText,
                    SessionPriceState.Partial,
                    "部分记录未完整计价，金额为已计价部分合计。"),
            PricePresentationState.Unpriced =>
                (
                    presentation.ValueText,
                    SessionPriceState.Unpriced,
                    "缺少价格快照，暂无法估算金额。"),
            _ => ("—", SessionPriceState.NoData, null),
        };
    }

    public static bool HasCoverageGap(UsageMetricSet metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        MetricAggregate[] aggregates =
        [
            metrics.InputReported,
            metrics.UncachedInput,
            metrics.CacheRead,
            metrics.CacheWrite,
            metrics.Output,
            metrics.Reasoning,
            metrics.Tool,
            metrics.ReportedTotal,
            metrics.NormalizedTotal,
        ];
        return aggregates.Any(aggregate =>
            aggregate.Coverage is MetricCoverageStatus.Unknown or
                MetricCoverageStatus.Unavailable);
    }

    public static string DescribeProject(
        string? projectPath,
        string? projectId,
        PathAvailability pathAvailability)
    {
        if (pathAvailability == PathAvailability.Available &&
            !string.IsNullOrWhiteSpace(projectPath))
        {
            return projectPath;
        }

        return !string.IsNullOrWhiteSpace(projectId)
            ? $"项目 {projectId}"
            : "未命名会话";
    }

    public static string ShortenId(string sessionId) =>
        sessionId.Length <= 12 ? sessionId : $"{sessionId[..12]}…";

}
