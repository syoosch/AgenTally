using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using AgenTally.Storage.Queries;
using AgenTally.UI.Infrastructure;
using AgenTally.UI.Runtime;

namespace AgenTally.UI.ViewModels;

public interface IRefreshTimer : IDisposable
{
    TimeSpan Interval { get; set; }

    bool IsEnabled { get; }

    void Start(Func<Task> tick);

    void Stop();
}

public sealed class DispatcherRefreshTimer : IRefreshTimer
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private Func<Task>? _tick;

    public DispatcherRefreshTimer(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(
            DispatcherPriority.Background,
            dispatcher);
        _timer.Tick += OnTick;
    }

    public TimeSpan Interval
    {
        get
        {
            _dispatcher.VerifyAccess();
            return _timer.Interval;
        }
        set
        {
            _dispatcher.VerifyAccess();
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _timer.Interval = value;
        }
    }

    public bool IsEnabled
    {
        get
        {
            _dispatcher.VerifyAccess();
            return _timer.IsEnabled;
        }
    }

    public Task? LastTickTask { get; private set; }

    public Exception? LastException { get; private set; }

    public void Start(Func<Task> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        _dispatcher.VerifyAccess();
        _tick = tick;
        LastException = null;
        _timer.Start();
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        _timer.Stop();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        Stop();
        _timer.Tick -= OnTick;
        _tick = null;
    }

    private void OnTick(object? sender, EventArgs eventArgs)
    {
        if (_tick is null)
        {
            return;
        }

        Task tick;
        try
        {
            tick = _tick();
        }
        catch (Exception exception)
        {
            tick = Task.FromException(exception);
        }

        LastTickTask = ObserveTickAsync(tick);
    }

    private async Task ObserveTickAsync(Task tick)
    {
        try
        {
            await tick;
        }
        catch (Exception exception)
        {
            LastException = exception;
        }
    }
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly IRefreshTimer _timer;
    private readonly ICoreRuntimeController _runtimeController;
    private readonly IClearStatisticsConfirmation _clearStatisticsConfirmation;
    private readonly IUsageDataChangeMonitor _dataChangeMonitor;
    private readonly IUsageQueryMaintenanceGate? _queryMaintenanceGate;
    private readonly IDataBackupInteraction _dataBackupInteraction;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _localTimeZone;
    private StatisticsFilterState _statisticsFilters;
    private PageViewModel _currentPage;
    private CoreRuntimeUiStatus _coreStatus = CoreRuntimeUiStatus.Standalone;
    private int _lastSuccessfulLocalDayNumber = int.MinValue;
    private int _pendingDataRefresh;
    private int _disposed;
    private int _started;
    private int _stopped;
    private int _tickRunning;
    private int _dataMaintenanceRunning;
    private CancellationTokenSource? _backupCancellation;
    private string? _dataMaintenanceStatusText;
    private bool _dataMaintenanceStatusIsError;

    public MainViewModel(
        DashboardViewModel dashboard,
        AnalysisViewModel analysis,
        ProjectsViewModel projects,
        SessionsViewModel sessions,
        SourcesViewModel sources,
        SettingsViewModel settings,
        Dispatcher dispatcher,
        IUsageDataChangeMonitor dataChangeMonitor,
        IRefreshTimer? timer = null,
        ICoreRuntimeController? runtimeController = null,
        string applicationDisplayName = "AgenTally",
        bool isDevelopment = false,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null,
        IClearStatisticsConfirmation? clearStatisticsConfirmation = null,
        IUsageQueryMaintenanceGate? queryMaintenanceGate = null,
        IDataBackupInteraction? dataBackupInteraction = null)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(dataChangeMonitor);
        dispatcher.VerifyAccess();
        _dispatcher = dispatcher;
        _lifetimeToken = _lifetime.Token;
        _timer = timer ?? new DispatcherRefreshTimer(dispatcher);
        _runtimeController = runtimeController ??
            new StandaloneCoreRuntimeController();
        _clearStatisticsConfirmation = clearStatisticsConfirmation ??
            new RejectingClearStatisticsConfirmation();
        _dataChangeMonitor = dataChangeMonitor;
        _queryMaintenanceGate = queryMaintenanceGate;
        _dataBackupInteraction = dataBackupInteraction ??
            new RejectingDataBackupInteraction();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDisplayName);
        ApplicationDisplayName = applicationDisplayName;
        IsDevelopment = isDevelopment;
        Dashboard = dashboard;
        Analysis = analysis;
        Projects = projects;
        Sessions = sessions;
        Sources = sources;
        Settings = settings;
        _statisticsFilters = CaptureFilters(dashboard);
        analysis.ApplySynchronizedFilters(
            _statisticsFilters.Period,
            _statisticsFilters.Agent,
            _statisticsFilters.Model,
            _statisticsFilters.CustomStartDate,
            _statisticsFilters.CustomEndDate,
            _statisticsFilters.Project);
        projects.ApplySynchronizedFilters(
            _statisticsFilters.Period,
            _statisticsFilters.Agent,
            _statisticsFilters.Model,
            _statisticsFilters.CustomStartDate,
            _statisticsFilters.CustomEndDate);
        sessions.ApplySynchronizedFilters(
            _statisticsFilters.Period,
            _statisticsFilters.Agent,
            _statisticsFilters.Model,
            _statisticsFilters.CustomStartDate,
            _statisticsFilters.CustomEndDate,
            _statisticsFilters.Project);
        Pages = new ReadOnlyCollection<PageViewModel>(
            [dashboard, Analysis, Projects, Sessions, sources, settings]);
        _currentPage = dashboard;
        _timer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        settings.PropertyChanged += OnSettingsPropertyChanged;
        dashboard.FilterChanged += OnDashboardFilterChanged;
        analysis.FilterChanged += OnAnalysisFilterChanged;
        dashboard.DaySelected += OnDashboardDaySelected;
        projects.FilterChanged += OnProjectsFilterChanged;
        sessions.FilterChanged += OnSessionsFilterChanged;
        projects.SessionRequested += OnProjectSessionRequested;
        sessions.ProjectRequested += OnSessionProjectRequested;
        NavigateCommand = new AsyncRelayCommand(
            NavigateAsync,
            CanNavigate,
            allowsConcurrentExecutions: true);
        RetryCoreCommand = new AsyncRelayCommand(
            RetryCoreAsync,
            () => CoreStatusCanRetry);
        RebuildCodexCommand = new AsyncRelayCommand(
            RebuildCodexAsync,
            () => CoreStatusCanRebuild);
        ClearStatisticsCommand = new AsyncRelayCommand(
            ClearStatisticsAsync,
            () => CoreStatusCanClear);
        CreateBackupCommand = new AsyncRelayCommand(
            CreateBackupAsync,
            () => CanStartDataMaintenance);
        CancelBackupCommand = new RelayCommand(
            CancelBackup,
            () => CanCancelBackup);
        RestoreBackupCommand = new AsyncRelayCommand(
            RestoreBackupAsync,
            () => CanStartDataMaintenance);
        NavigateToSourcesCommand = new AsyncRelayCommand(
            () => NavigateAsync(Sources),
            () => CoreStatusCanOpenSources);
    }

    public DashboardViewModel Dashboard { get; }

    public AnalysisViewModel Analysis { get; }

    public ProjectsViewModel Projects { get; }

    public SessionsViewModel Sessions { get; }

    public SourcesViewModel Sources { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public string ApplicationDisplayName { get; }

    public string WindowTitle => ApplicationDisplayName;

    public bool IsDevelopment { get; }

    public bool IsCoreStatusVisible =>
        _coreStatus.State != CoreRuntimeUiState.Standalone &&
        _coreStatus.State != CoreRuntimeUiState.Ready &&
        !string.IsNullOrWhiteSpace(_coreStatus.Message);

    public string CoreStatusText => _coreStatus.Message;

    public bool CoreStatusIsError => _coreStatus.IsError;

    public bool CoreStatusCanRetry => _coreStatus.CanRetry;

    public bool CoreStatusCanRebuild =>
        !IsDataMaintenanceRunning &&
        _coreStatus.State is CoreRuntimeUiState.Ready or
            CoreRuntimeUiState.ParserRebuildRequired or
            CoreRuntimeUiState.Failed;

    public bool CoreStatusCanClear => CoreStatusCanRebuild;

    public bool IsDataMaintenanceRunning =>
        Volatile.Read(ref _dataMaintenanceRunning) != 0;

    public bool CanStartDataMaintenance =>
        !IsDataMaintenanceRunning &&
        _coreStatus.State is CoreRuntimeUiState.Ready or
            CoreRuntimeUiState.ParserRebuildRequired or
            CoreRuntimeUiState.Failed;

    public bool CanCancelBackup =>
        IsDataMaintenanceRunning && _backupCancellation is not null;

    public string? DataMaintenanceStatusText
    {
        get => _dataMaintenanceStatusText;
        private set
        {
            if (SetProperty(ref _dataMaintenanceStatusText, value))
            {
                OnPropertyChanged(nameof(HasDataMaintenanceStatus));
            }
        }
    }

    public bool HasDataMaintenanceStatus =>
        !string.IsNullOrWhiteSpace(DataMaintenanceStatusText);

    public bool DataMaintenanceStatusIsError
    {
        get => _dataMaintenanceStatusIsError;
        private set => SetProperty(ref _dataMaintenanceStatusIsError, value);
    }

    public bool CoreStatusCanOpenSources =>
        _coreStatus.State == CoreRuntimeUiState.SourceUnavailable;

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public AsyncRelayCommand NavigateCommand { get; }

    public AsyncRelayCommand RetryCoreCommand { get; }

    public AsyncRelayCommand RebuildCodexCommand { get; }

    public AsyncRelayCommand ClearStatisticsCommand { get; }

    public AsyncRelayCommand CreateBackupCommand { get; }

    public RelayCommand CancelBackupCommand { get; }

    public AsyncRelayCommand RestoreBackupCommand { get; }

    public AsyncRelayCommand NavigateToSourcesCommand { get; }

    public async Task StartAsync()
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        CoreRuntimeUiStatus runtimeStatus = await EnsureCoreSafelyAsync();
        await InvokeOnDispatcherAsync(() =>
        {
            ApplyCoreStatus(runtimeStatus);
            return true;
        });

        if (!runtimeStatus.IsError ||
            runtimeStatus.State == CoreRuntimeUiState.ParserRebuildRequired)
        {
            _ = await ObserveDataChangesSafelyAsync();
            RefreshOperation? initialRefresh = await InvokeOnDispatcherAsync(
                () => StartCurrentPageRefreshOnDispatcher(skipIfLoading: false));
            if (initialRefresh is not null)
            {
                await CompleteRefreshAsync(initialRefresh);
            }
        }

        await InvokeOnDispatcherAsync(() =>
        {
            if (Volatile.Read(ref _stopped) == 0)
            {
                _timer.Start(TickAsync);
            }

            return true;
        });
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        SafeCancel(_lifetime);
        try
        {
            _backupCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        RunOnDispatcher(() =>
        {
            _timer.Stop();
            foreach (PageViewModel page in Pages)
            {
                page.CancelRefresh();
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        RunOnDispatcher(() =>
        {
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
            Dashboard.FilterChanged -= OnDashboardFilterChanged;
            Analysis.FilterChanged -= OnAnalysisFilterChanged;
            Dashboard.DaySelected -= OnDashboardDaySelected;
            Projects.FilterChanged -= OnProjectsFilterChanged;
            Sessions.FilterChanged -= OnSessionsFilterChanged;
            Projects.SessionRequested -= OnProjectSessionRequested;
            Sessions.ProjectRequested -= OnSessionProjectRequested;
            _timer.Dispose();
            Settings.Dispose();
        });
        _dataChangeMonitor.Dispose();
        _lifetime.Dispose();
    }

    private bool CanNavigate(object? parameter) =>
        parameter is PageViewModel page && Pages.Contains(page);

    private async Task NavigateAsync(object? parameter)
    {
        if (parameter is not PageViewModel destination || !Pages.Contains(destination))
        {
            throw new ArgumentException("目标页面不属于主导航。", nameof(parameter));
        }

        RefreshOperation? refresh = await InvokeOnDispatcherAsync(() =>
        {
            if (ReferenceEquals(destination, Settings))
            {
                Settings.ShowSettingsHome();
            }

            if (!ReferenceEquals(CurrentPage, destination))
            {
                CurrentPage.CancelRefresh();
                CurrentPage = destination;
            }

            return Volatile.Read(ref _stopped) == 0 &&
                !destination.HasSuccessfulRefresh &&
                !destination.IsLoading
                ? new RefreshOperation(
                    destination,
                    destination.RefreshAsync(_lifetimeToken))
                : null;
        });
        if (refresh is not null)
        {
            await CompleteRefreshAsync(refresh);
        }
    }

    private async Task TickAsync()
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            CoreRuntimeUiStatus runtimeStatus = await ReadCoreStatusSafelyAsync();
            await InvokeOnDispatcherAsync(() =>
            {
                ApplyCoreStatus(runtimeStatus);
                return true;
            });

            bool pageIsLoading = await InvokeOnDispatcherAsync(() =>
                Volatile.Read(ref _stopped) != 0 || CurrentPage.IsLoading);
            if (pageIsLoading)
            {
                return;
            }

            UsageDataChangeState changeState =
                await ObserveDataChangesSafelyAsync();
            if (changeState == UsageDataChangeState.Changed)
            {
                Interlocked.Exchange(ref _pendingDataRefresh, 1);
            }

            int localDayNumber = GetLocalDayNumber();
            bool localDateChanged =
                Volatile.Read(ref _lastSuccessfulLocalDayNumber) != localDayNumber;
            if (Volatile.Read(ref _pendingDataRefresh) == 0 && !localDateChanged)
            {
                return;
            }

            RefreshOperation? refresh = await InvokeOnDispatcherAsync(() =>
            {
                InvalidateSuccessfulPageRefreshesOnDispatcher();
                return StartCurrentPageRefreshOnDispatcher(
                    skipIfLoading: true,
                    showFeedback: false);
            });
            if (refresh is not null)
            {
                await CompleteRefreshAsync(refresh);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    private RefreshOperation? StartCurrentPageRefreshOnDispatcher(
        bool skipIfLoading,
        bool showFeedback = true)
    {
        _dispatcher.VerifyAccess();
        if (Volatile.Read(ref _stopped) != 0 ||
            (skipIfLoading && CurrentPage.IsLoading))
        {
            return null;
        }

        PageViewModel page = CurrentPage;
        Task refresh = showFeedback
            ? page.RefreshAsync(_lifetimeToken)
            : page.RefreshInBackgroundAsync(_lifetimeToken);
        return new RefreshOperation(page, refresh);
    }

    private async Task RetryCoreAsync()
    {
        CoreRuntimeUiStatus status = await EnsureCoreSafelyAsync();
        RefreshOperation? refresh = await InvokeOnDispatcherAsync(() =>
        {
            ApplyCoreStatus(status);
            if (status.IsError)
            {
                return null;
            }

            InvalidateSuccessfulPageRefreshesOnDispatcher();
            return StartCurrentPageRefreshOnDispatcher(skipIfLoading: false);
        });
        if (refresh is not null)
        {
            await CompleteRefreshAsync(refresh);
        }
    }

    private Task RebuildCodexAsync() =>
        RunMaintenanceAsync(
            _runtimeController.RebuildCodexAsync,
            "正在重新扫描全部 Agent 统计…");

    private Task ClearStatisticsAsync()
    {
        if (!_clearStatisticsConfirmation.ConfirmClearStatistics())
        {
            return Task.CompletedTask;
        }

        return RunMaintenanceAsync(
            _runtimeController.ClearStatisticsAsync,
            "正在清除本地统计…");
    }

    private async Task CreateBackupAsync()
    {
        string suggestedFileName =
            $"AgenTally-backup-{_timeProvider.GetLocalNow():yyyyMMdd-HHmmss}.agentally-backup";
        string? backupPath =
            _dataBackupInteraction.ChooseBackupDestination(suggestedFileName);
        if (string.IsNullOrWhiteSpace(backupPath) || !TryBeginDataMaintenance())
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeToken);
        _backupCancellation = cancellation;
        NotifyDataMaintenanceCommandState();
        SetDataMaintenanceStatus("正在创建本地备份…", isError: false);
        try
        {
            CoreRuntimeUiStatus status = await _runtimeController.CreateBackupAsync(
                Path.GetFullPath(backupPath),
                cancellation.Token);
            if (status.IsError)
            {
                await InvokeOnDispatcherAsync(() =>
                {
                    ApplyCoreStatus(status);
                    SetDataMaintenanceStatus(status.Message, isError: true);
                    return true;
                });
                return;
            }

            await InvokeOnDispatcherAsync(() =>
            {
                ApplyCoreStatus(status);
                Settings.RecordSuccessfulBackup(_timeProvider.GetUtcNow());
                SetDataMaintenanceStatus("备份已创建并通过完整性校验。", isError: false);
                return true;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await InvokeOnDispatcherAsync(() =>
            {
                SetDataMaintenanceStatus("备份已取消，源数据未改变。", isError: false);
                return true;
            });
        }
        catch
        {
            await InvokeOnDispatcherAsync(() =>
            {
                SetDataMaintenanceStatus("备份失败，源数据未改变。", isError: true);
                return true;
            });
        }
        finally
        {
            _backupCancellation = null;
            EndDataMaintenance();
        }
    }

    private void CancelBackup()
    {
        try
        {
            _backupCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RestoreBackupAsync()
    {
        string? backupPath = _dataBackupInteraction.ChooseBackupToRestore();
        if (string.IsNullOrWhiteSpace(backupPath) ||
            !_dataBackupInteraction.ConfirmRestore(backupPath) ||
            !TryBeginDataMaintenance())
        {
            return;
        }

        SetDataMaintenanceStatus("正在校验并恢复本地备份…", isError: false);
        IDisposable? queryPause = null;
        try
        {
            await InvokeOnDispatcherAsync(() =>
            {
                _timer.Stop();
                foreach (PageViewModel page in Pages)
                {
                    page.CancelRefresh();
                }
                return true;
            });
            if (_queryMaintenanceGate is not null)
            {
                queryPause = await _queryMaintenanceGate.PauseAsync(_lifetimeToken);
            }
            await _dataChangeMonitor.ResetAsync(_lifetimeToken);

            CoreRuntimeUiStatus status = await _runtimeController.RestoreBackupAsync(
                Path.GetFullPath(backupPath),
                _lifetimeToken);
            await InvokeOnDispatcherAsync(() =>
            {
                ApplyCoreStatus(status);
                SetDataMaintenanceStatus(
                    status.IsError
                        ? status.Message
                        : "备份已恢复，当前数据已重新加载。",
                    status.IsError);
                if (!status.IsError)
                {
                    InvalidateSuccessfulPageRefreshesOnDispatcher();
                }
                return true;
            });
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch
        {
            await InvokeOnDispatcherAsync(() =>
            {
                SetDataMaintenanceStatus(
                    "恢复失败，当前数据未改变或已自动回滚。",
                    isError: true);
                return true;
            });
        }
        finally
        {
            queryPause?.Dispose();
            EndDataMaintenance();
            RefreshOperation? refresh = await InvokeOnDispatcherAsync(() =>
            {
                if (Volatile.Read(ref _stopped) == 0)
                {
                    _timer.Start(TickAsync);
                }
                return Volatile.Read(ref _stopped) == 0
                    ? StartCurrentPageRefreshOnDispatcher(skipIfLoading: false)
                    : null;
            });
            if (refresh is not null)
            {
                await CompleteRefreshAsync(refresh);
            }
        }
    }

    private async Task RunMaintenanceAsync(
        Func<CancellationToken, Task<CoreRuntimeUiStatus>> operation,
        string progressMessage)
    {
        if (!TryBeginDataMaintenance())
        {
            return;
        }

        await InvokeOnDispatcherAsync(() =>
        {
            ApplyCoreStatus(new CoreRuntimeUiStatus(
                CoreRuntimeUiState.UpdatingStatistics,
                progressMessage,
                false,
                false));
            return true;
        });

        try
        {
            CoreRuntimeUiStatus status;
            try
            {
                status = await operation(_lifetimeToken);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                status = UnexpectedRuntimeFailure();
            }

            RefreshOperation? refresh = await InvokeOnDispatcherAsync(() =>
            {
                ApplyCoreStatus(status);
                if (status.IsError)
                {
                    return null;
                }

                InvalidateSuccessfulPageRefreshesOnDispatcher();
                return StartCurrentPageRefreshOnDispatcher(skipIfLoading: false);
            });
            if (refresh is not null)
            {
                await CompleteRefreshAsync(refresh);
            }
        }
        finally
        {
            EndDataMaintenance();
        }
    }

    private bool TryBeginDataMaintenance()
    {
        if (Interlocked.CompareExchange(ref _dataMaintenanceRunning, 1, 0) != 0)
        {
            return false;
        }

        RunOnDispatcher(() =>
        {
            OnPropertyChanged(nameof(IsDataMaintenanceRunning));
            OnPropertyChanged(nameof(CanStartDataMaintenance));
            OnPropertyChanged(nameof(CanCancelBackup));
            NotifyDataMaintenanceCommandState();
            RebuildCodexCommand.RaiseCanExecuteChanged();
            ClearStatisticsCommand.RaiseCanExecuteChanged();
        });
        return true;
    }

    private void EndDataMaintenance()
    {
        Interlocked.Exchange(ref _dataMaintenanceRunning, 0);
        RunOnDispatcher(() =>
        {
            OnPropertyChanged(nameof(IsDataMaintenanceRunning));
            OnPropertyChanged(nameof(CanStartDataMaintenance));
            OnPropertyChanged(nameof(CanCancelBackup));
            NotifyDataMaintenanceCommandState();
            RebuildCodexCommand.RaiseCanExecuteChanged();
            ClearStatisticsCommand.RaiseCanExecuteChanged();
        });
    }

    private void NotifyDataMaintenanceCommandState()
    {
        CreateBackupCommand.RaiseCanExecuteChanged();
        CancelBackupCommand.RaiseCanExecuteChanged();
        RestoreBackupCommand.RaiseCanExecuteChanged();
    }

    private void SetDataMaintenanceStatus(string message, bool isError)
    {
        RunOnDispatcher(() =>
        {
            DataMaintenanceStatusIsError = isError;
            DataMaintenanceStatusText = message;
        });
    }

    private async Task<UsageDataChangeState> ObserveDataChangesSafelyAsync()
    {
        try
        {
            return await _dataChangeMonitor.ObserveAsync(_lifetimeToken);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return UsageDataChangeState.Unavailable;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _stopped) != 0)
        {
            return UsageDataChangeState.Unavailable;
        }
    }

    private async Task CompleteRefreshAsync(RefreshOperation operation)
    {
        await operation.Task;
        bool succeeded = await InvokeOnDispatcherAsync(() =>
            ReferenceEquals(CurrentPage, operation.Page) &&
            operation.Page.HasSuccessfulRefresh);
        if (!succeeded)
        {
            return;
        }

        Volatile.Write(ref _lastSuccessfulLocalDayNumber, GetLocalDayNumber());
        Interlocked.Exchange(ref _pendingDataRefresh, 0);
    }

    private void InvalidateSuccessfulPageRefreshesOnDispatcher()
    {
        _dispatcher.VerifyAccess();
        foreach (PageViewModel page in Pages)
        {
            page.InvalidateSuccessfulRefresh();
        }
    }

    private int GetLocalDayNumber()
    {
        DateTime localDate = TimeZoneInfo.ConvertTime(
            _timeProvider.GetUtcNow(),
            _localTimeZone).Date;
        return DateOnly.FromDateTime(localDate).DayNumber;
    }

    private async Task<CoreRuntimeUiStatus> EnsureCoreSafelyAsync()
    {
        try
        {
            return await _runtimeController.EnsureAsync(_lifetimeToken);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return _coreStatus;
        }
        catch (Exception)
        {
            return UnexpectedRuntimeFailure();
        }
    }

    private async Task<CoreRuntimeUiStatus> ReadCoreStatusSafelyAsync()
    {
        try
        {
            return await _runtimeController.ReadStatusAsync(_lifetimeToken);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return _coreStatus;
        }
        catch (Exception)
        {
            return UnexpectedRuntimeFailure();
        }
    }

    private void ApplyCoreStatus(CoreRuntimeUiStatus status)
    {
        _dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(status);
        if (Equals(_coreStatus, status))
        {
            return;
        }

        _coreStatus = status;
        OnPropertyChanged(nameof(IsCoreStatusVisible));
        OnPropertyChanged(nameof(CoreStatusText));
        OnPropertyChanged(nameof(CoreStatusIsError));
        OnPropertyChanged(nameof(CoreStatusCanRetry));
        OnPropertyChanged(nameof(CoreStatusCanRebuild));
        OnPropertyChanged(nameof(CoreStatusCanClear));
        OnPropertyChanged(nameof(CoreStatusCanOpenSources));
        RetryCoreCommand.RaiseCanExecuteChanged();
        RebuildCodexCommand.RaiseCanExecuteChanged();
        ClearStatisticsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartDataMaintenance));
        NotifyDataMaintenanceCommandState();
        NavigateToSourcesCommand.RaiseCanExecuteChanged();
    }

    private static CoreRuntimeUiStatus UnexpectedRuntimeFailure() => new(
        CoreRuntimeUiState.Failed,
        "无法读取后台运行状态；请重试，若持续发生请完全退出后重新打开。",
        true,
        true);

    private void OnDashboardFilterChanged(object? sender, EventArgs eventArgs)
    {
        OnStatisticsFilterChanged(Dashboard);
    }

    private void OnAnalysisFilterChanged(object? sender, EventArgs eventArgs)
    {
        OnStatisticsFilterChanged(Analysis);
    }

    private void OnProjectsFilterChanged(object? sender, EventArgs eventArgs)
    {
        OnStatisticsFilterChanged(Projects);
    }

    private void OnSessionsFilterChanged(object? sender, EventArgs eventArgs)
    {
        OnStatisticsFilterChanged(Sessions);
    }

    private void OnStatisticsFilterChanged(PageViewModel source)
    {
        void Synchronize() =>
            SynchronizeStatisticsFiltersOnDispatcher(source, refreshSource: true);

        if (_dispatcher.CheckAccess())
        {
            Synchronize();
        }
        else
        {
            _ = ObserveBackgroundOperationAsync(
                _dispatcher.InvokeAsync(Synchronize).Task);
        }
    }

    private void SynchronizeStatisticsFiltersOnDispatcher(
        PageViewModel source,
        bool refreshSource,
        bool invalidateSource = true)
    {
        _dispatcher.VerifyAccess();
        StatisticsFilterState next = source switch
        {
            DashboardViewModel => CaptureFilters(Dashboard),
            AnalysisViewModel => CaptureFilters(Analysis),
            ProjectsViewModel => CaptureFilters(Projects, _statisticsFilters.Project),
            SessionsViewModel => CaptureFilters(Sessions),
            _ => throw new ArgumentException(
                "页面不属于共享统计筛选范围。",
                nameof(source))
        };
        bool sharedChanged = next != _statisticsFilters;
        _statisticsFilters = next;
        if (invalidateSource)
        {
            source.InvalidateSuccessfulRefresh();
        }

        if (sharedChanged)
        {
            if (!ReferenceEquals(source, Dashboard))
            {
                Dashboard.ApplySynchronizedFilters(
                    next.Period,
                    next.Agent,
                    next.Model,
                    next.CustomStartDate,
                    next.CustomEndDate,
                    next.Project);
                Dashboard.InvalidateSuccessfulRefresh();
            }

            if (!ReferenceEquals(source, Analysis))
            {
                Analysis.ApplySynchronizedFilters(
                    next.Period,
                    next.Agent,
                    next.Model,
                    next.CustomStartDate,
                    next.CustomEndDate,
                    next.Project);
                Analysis.InvalidateSuccessfulRefresh();
            }

            if (!ReferenceEquals(source, Projects))
            {
                Projects.ApplySynchronizedFilters(
                    next.Period,
                    next.Agent,
                    next.Model,
                    next.CustomStartDate,
                    next.CustomEndDate);
                Projects.InvalidateSuccessfulRefresh();
            }

            if (!ReferenceEquals(source, Sessions))
            {
                Sessions.ApplySynchronizedFilters(
                    next.Period,
                    next.Agent,
                    next.Model,
                    next.CustomStartDate,
                    next.CustomEndDate,
                    next.Project);
                Sessions.InvalidateSuccessfulRefresh();
            }
        }

        if (!refreshSource ||
            Volatile.Read(ref _started) == 0 ||
            Volatile.Read(ref _stopped) != 0 ||
            !ReferenceEquals(CurrentPage, source))
        {
            return;
        }

        Task refresh = source.RefreshAsync(_lifetimeToken);
        _ = ObserveBackgroundOperationAsync(refresh);
    }

    private void OnDashboardDaySelected(UsageDaySelection selection)
    {
        void Navigate()
        {
            Analysis.SelectDay(selection);
            Analysis.InvalidateSuccessfulRefresh();
            Task navigation = NavigateAsync(Analysis);
            _ = ObserveBackgroundOperationAsync(navigation);
        }

        if (_dispatcher.CheckAccess())
        {
            Navigate();
        }
        else
        {
            _ = ObserveBackgroundOperationAsync(
                _dispatcher.InvokeAsync(Navigate).Task);
        }
    }

    private void OnProjectSessionRequested(RootSessionIdentity identity)
    {
        Task navigation = NavigateToSessionAsync(identity);
        _ = ObserveBackgroundOperationAsync(navigation);
    }

    private async Task NavigateToSessionAsync(RootSessionIdentity identity)
    {
        await NavigateAsync(Sessions);
        await Sessions.SelectSessionAsync(identity, _lifetimeToken);
    }

    private void OnSessionProjectRequested(string projectId)
    {
        Task navigation = NavigateToProjectAsync(projectId);
        _ = ObserveBackgroundOperationAsync(navigation);
    }

    private async Task NavigateToProjectAsync(string projectId)
    {
        await InvokeOnDispatcherAsync(() =>
        {
            if (!ReferenceEquals(CurrentPage, Projects))
            {
                CurrentPage.CancelRefresh();
                CurrentPage = Projects;
            }

            return true;
        });
        await Projects.SelectProjectAsync(projectId, _lifetimeToken);
        await InvokeOnDispatcherAsync(() =>
        {
            SynchronizeStatisticsFiltersOnDispatcher(
                Projects,
                refreshSource: false,
                invalidateSource: false);
            return true;
        });
    }

    private static StatisticsFilterState CaptureFilters(
        DashboardViewModel viewModel) => new(
        viewModel.SelectedPeriod,
        viewModel.SelectedAgent,
        viewModel.SelectedModel,
        viewModel.CustomStartDate,
        viewModel.CustomEndDate,
        viewModel.SelectedProject);

    private static StatisticsFilterState CaptureFilters(
        AnalysisViewModel viewModel) => new(
        viewModel.SelectedPeriod,
        viewModel.SelectedAgent,
        viewModel.SelectedModel,
        viewModel.CustomStartDate,
        viewModel.CustomEndDate,
        viewModel.SelectedProject);

    private static StatisticsFilterState CaptureFilters(
        ProjectsViewModel viewModel,
        string project) => new(
        viewModel.SelectedPeriod,
        viewModel.SelectedAgent,
        viewModel.SelectedModel,
        viewModel.CustomStartDate,
        viewModel.CustomEndDate,
        project);

    private static StatisticsFilterState CaptureFilters(
        SessionsViewModel viewModel) => new(
        viewModel.SelectedPeriod,
        viewModel.SelectedAgent,
        viewModel.SelectedModel,
        viewModel.CustomStartDate,
        viewModel.CustomEndDate,
        viewModel.SelectedProject);

    private static async Task ObserveBackgroundOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch
        {
            // Page state or command state exposes operational failures to the UI.
        }
    }

    private void OnSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(SettingsViewModel.RefreshIntervalSeconds))
        {
            RunOnDispatcher(() =>
                _timer.Interval = TimeSpan.FromSeconds(
                    Settings.RefreshIntervalSeconds));
        }
    }

    private Task<T> InvokeOnDispatcherAsync<T>(Func<T> action)
    {
        if (_dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    private static void SafeCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Dispose and Stop may be requested concurrently during window shutdown.
        }
    }

    private sealed record StatisticsFilterState(
        string Period,
        string Agent,
        string Model,
        DateTime? CustomStartDate,
        DateTime? CustomEndDate,
        string Project);

    private sealed record RefreshOperation(PageViewModel Page, Task Task);
}
