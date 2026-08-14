using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Runtime;
using AgenTally.UI.Infrastructure;
using AgenTally.UI.Runtime;
using AgenTally.UI.Updates;

namespace AgenTally.UI.ViewModels;

public enum PriceModelFilter
{
    All = 0,
    Unpriced = 1,
    Custom = 2
}

public enum SettingsSection
{
    Home = 0,
    General = 1,
    DataAndBackup = 2,
    Pricing = 3,
    Privacy = 4,
    About = 5
}

public sealed class SettingsViewModel : PageViewModel, IDisposable
{
    private const int DefaultRefreshIntervalSeconds = 3;
    private static readonly ReadOnlyCollection<int> SupportedIntervals =
        Array.AsReadOnly([2, 3, 5, 10, 30]);
    private readonly IUsageQueryService? _queries;
    private readonly IPriceCommandClient _priceCommands;
    private readonly IPriceRestoreConfirmation _restoreConfirmation;
    private readonly IUiPreferencesStore _preferencesStore;
    private readonly IManualVersionCheckCoordinator _manualVersionCheck;
    private readonly IReleasePageLauncher _releasePageLauncher;
    private readonly IDataManagementStateStore _dataManagementState;
    private readonly IStartupRegistrationStore _startupRegistration;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly CancellationTokenSource _versionCheckLifetime = new();
    private readonly List<PriceSettingPresentation> _allPriceModels = [];
    private SettingsSection _selectedSection;
    private int _refreshIntervalSeconds;
    private string _priceSearchText = string.Empty;
    private PriceModelFilter _selectedPriceFilter;
    private bool _isLongContextExpanded;
    private bool _isDataStorageExpanded;
    private bool _isDangerousDataActionsExpanded;
    private PriceSettingPresentation? _selectedPriceModel;
    private ObservableCollection<PriceSettingPresentation> _priceModels = [];
    private string _inputRateText = string.Empty;
    private string _cachedInputRateText = string.Empty;
    private string _cacheWriteRateText = string.Empty;
    private string _outputRateText = string.Empty;
    private string _longContextThresholdText = string.Empty;
    private string _longContextInputMultiplierText = "1";
    private string _longContextOutputMultiplierText = "1";
    private string? _priceValidationMessage;
    private string? _priceOperationMessage;
    private bool _priceOperationIsError;
    private bool _isPriceOperationRunning;
    private bool _isLoadingPriceEditor;
    private PriceEditorSnapshot _loadedPriceEditor = PriceEditorSnapshot.Empty;
    private string? _versionCheckStatusText;
    private bool _versionCheckStatusIsError;
    private bool _isVersionCheckRunning;
    private Uri? _availableReleasePageUri;
    private string _databaseSizeText = "—";
    private string _dataRequestCountText = "—";
    private string _dataTimeRangeText = "—";
    private string _lastBackupText = "尚未备份";
    private bool _isStartupEnabled;
    private bool _canChangeStartupRegistration;
    private string? _startupRegistrationMessage;
    private int _disposed;

    public SettingsViewModel(
        Dispatcher dispatcher,
        string databasePath,
        AgenTallyChannel? channel = null)
        : this(
            queries: null,
            new UnavailablePriceCommandClient(),
            new RejectingPriceRestoreConfirmation(),
            dispatcher,
            databasePath,
            channel)
    {
    }

    public SettingsViewModel(
        IUsageQueryService? queries,
        IPriceCommandClient priceCommands,
        IPriceRestoreConfirmation restoreConfirmation,
        Dispatcher dispatcher,
        string databasePath,
        AgenTallyChannel? channel = null)
        : this(
            queries,
            priceCommands,
            restoreConfirmation,
            dispatcher,
            databasePath,
            channel,
            new UnavailableUiPreferencesStore())
    {
    }

    internal SettingsViewModel(
        IUsageQueryService? queries,
        IPriceCommandClient priceCommands,
        IPriceRestoreConfirmation restoreConfirmation,
        Dispatcher dispatcher,
        string databasePath,
        AgenTallyChannel? channel,
        IUiPreferencesStore preferencesStore,
        IManualVersionCheckCoordinator? manualVersionCheck = null,
        IReleasePageLauncher? releasePageLauncher = null,
        IDataManagementStateStore? dataManagementState = null,
        IStartupRegistrationStore? startupRegistration = null,
        TimeZoneInfo? localTimeZone = null)
        : base("设置", dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _queries = queries;
        _priceCommands = priceCommands ??
            throw new ArgumentNullException(nameof(priceCommands));
        _restoreConfirmation = restoreConfirmation ??
            throw new ArgumentNullException(nameof(restoreConfirmation));
        _preferencesStore = preferencesStore ??
            throw new ArgumentNullException(nameof(preferencesStore));
        _manualVersionCheck = manualVersionCheck ??
            new UnavailableManualVersionCheckCoordinator();
        _releasePageLauncher = releasePageLauncher ??
            new UnavailableReleasePageLauncher();
        _dataManagementState = dataManagementState ??
            new UnavailableDataManagementStateStore();
        _startupRegistration = startupRegistration ??
            new UnavailableStartupRegistrationStore();
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        int? storedRefreshInterval =
            _preferencesStore.ReadRefreshIntervalSeconds();
        _refreshIntervalSeconds = storedRefreshInterval.HasValue &&
            SupportedIntervals.Contains(storedRefreshInterval.Value)
                ? storedRefreshInterval.Value
                : DefaultRefreshIntervalSeconds;
        DatabasePath = Path.GetFullPath(databasePath);
        ApplyLastBackup(_dataManagementState.ReadLastSuccessfulBackupUtc());
        StartupRegistrationDescription = channel switch
        {
            AgenTallyChannel.Development =>
                "Development 模拟，不修改 Windows",
            AgenTallyChannel.Stable => "登录后仅启动托盘",
            _ => "当前诊断模式不可用"
        };
        ApplyStartupRegistrationStatus(_startupRegistration.Read());
        NetworkAccessDescription = channel switch
        {
            AgenTallyChannel.Stable =>
                "正常采集、统计和价格零外联；仅版本检查可访问配置的正式发布渠道。",
            AgenTallyChannel.Development =>
                "Development 不访问真实版本渠道；正常采集、统计和价格零外联。",
            _ => "当前诊断界面不联网。"
        };
        OpenSettingsSectionCommand = new RelayCommand(OpenSettingsSection);
        BackToSettingsHomeCommand = new RelayCommand(ShowSettingsHome);
        SetPriceFilterCommand = new RelayCommand(SetPriceFilter);
        DiscardPriceChangesCommand = new RelayCommand(
            DiscardPriceChanges,
            () => HasUnsavedPriceChanges && !IsPriceOperationRunning);
        SavePriceCommand = new AsyncRelayCommand(
            SavePriceAsync,
            () => CanSaveSelectedPrice);
        RestorePriceCommand = new AsyncRelayCommand(
            RestorePriceAsync,
            () => CanRestoreSelectedPrice);
        RestoreAllPricesCommand = new AsyncRelayCommand(
            RestoreAllPricesAsync,
            () => CanRestoreAllPrices);
        CheckForUpdatesCommand = new AsyncRelayCommand(
            CheckForUpdatesAsync,
            () => !IsVersionCheckRunning && Volatile.Read(ref _disposed) == 0);
        OpenReleasePageCommand = new RelayCommand(
            OpenReleasePage,
            () => CanOpenReleasePage);
    }

    public IReadOnlyList<int> RefreshIntervalOptions => SupportedIntervals;

    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (SetProperty(ref _selectedSection, value))
            {
                OnPropertyChanged(nameof(IsSettingsHome));
                OnPropertyChanged(nameof(IsSettingsDetail));
                OnPropertyChanged(nameof(IsGeneralSettings));
                OnPropertyChanged(nameof(IsDataAndBackupSettings));
                OnPropertyChanged(nameof(IsPricingSettings));
                OnPropertyChanged(nameof(IsPrivacySettings));
                OnPropertyChanged(nameof(IsAboutSettings));
                OnPropertyChanged(nameof(SettingsSectionTitle));
            }
        }
    }

    public bool IsSettingsHome => SelectedSection == SettingsSection.Home;

    public bool IsSettingsDetail => !IsSettingsHome;

    public bool IsGeneralSettings =>
        SelectedSection == SettingsSection.General;

    public bool IsDataAndBackupSettings =>
        SelectedSection == SettingsSection.DataAndBackup;

    public bool IsPricingSettings =>
        SelectedSection == SettingsSection.Pricing;

    public bool IsPrivacySettings =>
        SelectedSection == SettingsSection.Privacy;

    public bool IsAboutSettings =>
        SelectedSection == SettingsSection.About;

    public string SettingsSectionTitle => SelectedSection switch
    {
        SettingsSection.General => "常规设置",
        SettingsSection.DataAndBackup => "数据与备份",
        SettingsSection.Pricing => "模型与计价",
        SettingsSection.Privacy => "隐私与安全",
        SettingsSection.About => "关于与更新",
        _ => "设置"
    };

    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set
        {
            if (!SupportedIntervals.Contains(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _refreshIntervalSeconds, value))
            {
                _ = _preferencesStore.TryWriteRefreshIntervalSeconds(value);
            }
        }
    }

    public bool IsStartupEnabled
    {
        get => _isStartupEnabled;
        set
        {
            if (value == _isStartupEnabled ||
                !CanChangeStartupRegistration)
            {
                return;
            }

            ApplyStartupRegistrationStatus(
                _startupRegistration.SetEnabled(value));
            OnPropertyChanged();
        }
    }

    public bool CanChangeStartupRegistration
    {
        get => _canChangeStartupRegistration;
        private set => SetProperty(
            ref _canChangeStartupRegistration,
            value);
    }

    public string StartupRegistrationDescription { get; }

    public string? StartupRegistrationMessage
    {
        get => _startupRegistrationMessage;
        private set
        {
            if (SetProperty(ref _startupRegistrationMessage, value))
            {
                OnPropertyChanged(nameof(HasStartupRegistrationMessage));
            }
        }
    }

    public bool HasStartupRegistrationMessage =>
        !string.IsNullOrWhiteSpace(StartupRegistrationMessage);

    public string DatabasePath { get; }

    public string ApplicationVersion =>
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "—";

    public string DatabaseAccessDescription => "数据库仅由 AgenTally 本地读写。";

    public string DatabaseSizeText
    {
        get => _databaseSizeText;
        private set => SetProperty(ref _databaseSizeText, value);
    }

    public string DataRequestCountText
    {
        get => _dataRequestCountText;
        private set => SetProperty(ref _dataRequestCountText, value);
    }

    public string DataTimeRangeText
    {
        get => _dataTimeRangeText;
        private set => SetProperty(ref _dataTimeRangeText, value);
    }

    public string LastBackupText
    {
        get => _lastBackupText;
        private set => SetProperty(ref _lastBackupText, value);
    }

    public string CollectionDescription => "默认只读取本机 Agent 日志和本地数据库。";

    public string NetworkAccessDescription { get; }

    public string AgentConfigurationDescription => "不修改 Agent 配置";

    public string RefreshDescription => "仅在界面打开时检查本地数据变化";

    public string? VersionCheckStatusText
    {
        get => _versionCheckStatusText;
        private set
        {
            if (SetProperty(ref _versionCheckStatusText, value))
            {
                OnPropertyChanged(nameof(HasVersionCheckStatus));
            }
        }
    }

    public bool HasVersionCheckStatus =>
        !string.IsNullOrWhiteSpace(VersionCheckStatusText);

    public bool VersionCheckStatusIsError
    {
        get => _versionCheckStatusIsError;
        private set => SetProperty(ref _versionCheckStatusIsError, value);
    }

    public bool IsVersionCheckRunning
    {
        get => _isVersionCheckRunning;
        private set
        {
            if (SetProperty(ref _isVersionCheckRunning, value))
            {
                OnPropertyChanged(nameof(CanOpenReleasePage));
                CheckForUpdatesCommand.RaiseCanExecuteChanged();
                OpenReleasePageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanOpenReleasePage =>
        _availableReleasePageUri is not null &&
        !IsVersionCheckRunning &&
        Volatile.Read(ref _disposed) == 0;

    public ObservableCollection<PriceSettingPresentation> PriceModels
    {
        get => _priceModels;
        private set => SetProperty(ref _priceModels, value);
    }

    public string PriceSearchText
    {
        get => _priceSearchText;
        set
        {
            if (SetProperty(ref _priceSearchText, value ?? string.Empty))
            {
                ApplyPriceFilter();
            }
        }
    }

    public PriceModelFilter SelectedPriceFilter
    {
        get => _selectedPriceFilter;
        set
        {
            if (SetProperty(ref _selectedPriceFilter, value))
            {
                OnPropertyChanged(nameof(IsAllPriceFilterSelected));
                OnPropertyChanged(nameof(IsUnpricedFilterSelected));
                OnPropertyChanged(nameof(IsCustomFilterSelected));
                ApplyPriceFilter();
            }
        }
    }

    public bool IsAllPriceFilterSelected =>
        SelectedPriceFilter == PriceModelFilter.All;

    public bool IsUnpricedFilterSelected =>
        SelectedPriceFilter == PriceModelFilter.Unpriced;

    public bool IsCustomFilterSelected =>
        SelectedPriceFilter == PriceModelFilter.Custom;

    public bool IsLongContextExpanded
    {
        get => _isLongContextExpanded;
        set
        {
            if (SetProperty(ref _isLongContextExpanded, value))
            {
                OnPropertyChanged(nameof(LongContextToggleGlyph));
            }
        }
    }

    public string LongContextToggleGlyph => IsLongContextExpanded ? "⌃" : "⌄";

    public bool IsDataStorageExpanded
    {
        get => _isDataStorageExpanded;
        set
        {
            if (SetProperty(ref _isDataStorageExpanded, value))
            {
                OnPropertyChanged(nameof(DataStorageToggleGlyph));
            }
        }
    }

    public string DataStorageToggleGlyph =>
        IsDataStorageExpanded ? "⌃" : "⌄";

    public bool IsDangerousDataActionsExpanded
    {
        get => _isDangerousDataActionsExpanded;
        set
        {
            if (SetProperty(ref _isDangerousDataActionsExpanded, value))
            {
                OnPropertyChanged(nameof(DangerousDataActionsToggleGlyph));
            }
        }
    }

    public string DangerousDataActionsToggleGlyph =>
        IsDangerousDataActionsExpanded ? "⌃" : "⌄";

    public PriceSettingPresentation? SelectedPriceModel
    {
        get => _selectedPriceModel;
        set
        {
            if (SameModel(_selectedPriceModel, value))
            {
                SetSelectedPriceModel(value, loadEditor: !HasUnsavedPriceChanges);
                return;
            }

            if (HasUnsavedPriceChanges)
            {
                PriceValidationMessage =
                    "当前模型有未保存修改，请先保存或放弃更改。";
                OnPropertyChanged(nameof(SelectedPriceModel));
                return;
            }

            SetSelectedPriceModel(value, loadEditor: true);
        }
    }

    public string InputRateText
    {
        get => _inputRateText;
        set => SetEditorText(ref _inputRateText, value);
    }

    public string CachedInputRateText
    {
        get => _cachedInputRateText;
        set => SetEditorText(ref _cachedInputRateText, value);
    }

    public string CacheWriteRateText
    {
        get => _cacheWriteRateText;
        set => SetEditorText(ref _cacheWriteRateText, value);
    }

    public string OutputRateText
    {
        get => _outputRateText;
        set => SetEditorText(ref _outputRateText, value);
    }

    public string LongContextThresholdText
    {
        get => _longContextThresholdText;
        set => SetEditorText(ref _longContextThresholdText, value);
    }

    public string LongContextInputMultiplierText
    {
        get => _longContextInputMultiplierText;
        set => SetEditorText(ref _longContextInputMultiplierText, value);
    }

    public string LongContextOutputMultiplierText
    {
        get => _longContextOutputMultiplierText;
        set => SetEditorText(ref _longContextOutputMultiplierText, value);
    }

    public string? PriceValidationMessage
    {
        get => _priceValidationMessage;
        private set
        {
            if (SetProperty(ref _priceValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasPriceValidationMessage));
            }
        }
    }

    public bool HasPriceValidationMessage =>
        !string.IsNullOrWhiteSpace(PriceValidationMessage);

    public string? PriceOperationMessage
    {
        get => _priceOperationMessage;
        private set
        {
            if (SetProperty(ref _priceOperationMessage, value))
            {
                OnPropertyChanged(nameof(HasPriceOperationMessage));
            }
        }
    }

    public bool HasPriceOperationMessage =>
        !string.IsNullOrWhiteSpace(PriceOperationMessage);

    public bool PriceOperationIsError
    {
        get => _priceOperationIsError;
        private set => SetProperty(ref _priceOperationIsError, value);
    }

    public bool IsPriceOperationRunning
    {
        get => _isPriceOperationRunning;
        private set
        {
            if (SetProperty(ref _isPriceOperationRunning, value))
            {
                OnPropertyChanged(nameof(CanEditSelectedPrice));
                OnPropertyChanged(nameof(CanSaveSelectedPrice));
                OnPropertyChanged(nameof(CanRestoreSelectedPrice));
                OnPropertyChanged(nameof(CanRestoreAllPrices));
                NotifyPriceCommandState();
            }
        }
    }

    public bool CanEditSelectedPrice =>
        _priceCommands.IsAvailable &&
        SelectedPriceModel is not null &&
        !IsPriceOperationRunning;

    public bool HasUnsavedPriceChanges =>
        SelectedPriceModel is not null &&
        !_loadedPriceEditor.Equals(CapturePriceEditor());

    public bool IsInheritedPriceEditor =>
        SelectedPriceModel?.HasBuiltInDefault == true &&
        SelectedPriceModel.HasCustomPrice == false &&
        !HasUnsavedPriceChanges;

    public bool CanSaveSelectedPrice =>
        CanEditSelectedPrice && HasUnsavedPriceChanges;

    public bool CanRestoreSelectedPrice =>
        CanEditSelectedPrice &&
        !HasUnsavedPriceChanges &&
        SelectedPriceModel?.HasCustomPrice == true;

    public bool CanRestoreAllPrices =>
        _priceCommands.IsAvailable &&
        !IsPriceOperationRunning &&
        !HasUnsavedPriceChanges &&
        _allPriceModels.Any(static row => row.HasCustomPrice);

    public bool HasPriceModels => _allPriceModels.Count > 0;

    public bool HasVisiblePriceModels => PriceModels.Count > 0;

    public bool HasNoVisiblePriceModels => !HasVisiblePriceModels;

    public int ObservedPriceModelCount =>
        _allPriceModels.Count(static row => row.HasObservedRecords);

    public int UnpricedPriceModelCount =>
        _allPriceModels.Count(static row => row.IsUnpriced);

    public int CustomPriceModelCount =>
        _allPriceModels.Count(static row => row.HasCustomPrice);

    public bool HasCustomPriceModels => CustomPriceModelCount > 0;

    public string PriceSummaryText =>
        $"{ObservedPriceModelCount:N0} 个已使用模型 · " +
        $"{UnpricedPriceModelCount:N0} 个未计价 · " +
        $"{CustomPriceModelCount:N0} 个自定义";

    public string AllPriceFilterText => $"全部 {_allPriceModels.Count:N0}";

    public string UnpricedPriceFilterText =>
        $"未计价 {UnpricedPriceModelCount:N0}";

    public string CustomPriceFilterText =>
        $"已自定义 {CustomPriceModelCount:N0}";

    public string RestoreAllPricesText =>
        $"恢复全部 {CustomPriceModelCount:N0} 个自定义价格";

    public string SelectedPriceRestoreText =>
        SelectedPriceModel?.HasCustomPrice == true &&
        SelectedPriceModel.HasBuiltInDefault == false
            ? "移除自定义价格"
            : "恢复默认";

    public string PriceDraftStatusText => HasUnsavedPriceChanges
        ? "当前模型有未保存修改"
        : string.Empty;

    public string LongContextSummaryText =>
        string.IsNullOrWhiteSpace(LongContextThresholdText)
            ? "未配置"
            : $"阈值 {LongContextThresholdText.Trim()} Token";

    public RelayCommand OpenSettingsSectionCommand { get; }

    public RelayCommand BackToSettingsHomeCommand { get; }

    public RelayCommand SetPriceFilterCommand { get; }

    public RelayCommand DiscardPriceChangesCommand { get; }

    public AsyncRelayCommand SavePriceCommand { get; }

    public AsyncRelayCommand RestorePriceCommand { get; }

    public AsyncRelayCommand RestoreAllPricesCommand { get; }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand OpenReleasePageCommand { get; }

    public void ShowSettingsHome()
    {
        SelectedSection = SettingsSection.Home;
    }

    public void RecordSuccessfulBackup(DateTimeOffset completedAtUtc)
    {
        UiDispatcher.VerifyAccess();
        DateTimeOffset normalized = completedAtUtc.ToUniversalTime();
        _ = _dataManagementState.TryWriteLastSuccessfulBackupUtc(normalized);
        ApplyLastBackup(normalized);
    }

    protected override async Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool showFeedback)
    {
        RefreshSession session = BeginRefresh(cancellationToken);
        await SetRefreshStartedAsync(session, showFeedback);
        try
        {
            Task<DataOverviewSnapshot> dataOverview =
                LoadDataOverviewAsync(session.Token);
            Exception? priceFailure = null;
            if (_queries is not null)
            {
                try
                {
                    IReadOnlyList<PriceSettingRow> rows =
                        await _queries.GetPriceSettingsAsync(session.Token);
                    await ApplyIfCurrentAsync(
                        session,
                        () =>
                        {
                            string? selectedModel =
                                SelectedPriceModel?.NormalizedModel;
                            bool restoreLongContextExpansion =
                                !showFeedback && IsLongContextExpanded;
                            ApplyPriceRows(rows, selectedModel);
                            if (restoreLongContextExpansion &&
                                string.Equals(
                                    selectedModel,
                                    SelectedPriceModel?.NormalizedModel,
                                    StringComparison.Ordinal))
                            {
                                IsLongContextExpanded = true;
                            }
                        });
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    priceFailure = exception;
                }
            }

            DataOverviewSnapshot overview = await dataOverview;
            await ApplyIfCurrentAsync(
                session,
                () =>
                {
                    DatabaseSizeText = overview.DatabaseSize;
                    DataRequestCountText = overview.RequestCount;
                    DataTimeRangeText = overview.TimeRange;
                    ApplyLastBackup(
                        _dataManagementState.ReadLastSuccessfulBackupUtc());
                });
            if (priceFailure is not null)
            {
                throw priceFailure;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _versionCheckLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _versionCheckLifetime.Dispose();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        VersionCheckStatusText = "正在检查…";
        VersionCheckStatusIsError = false;
        SetAvailableReleasePage(null);
        IsVersionCheckRunning = true;
        try
        {
            ManualVersionCheckResult result =
                await _manualVersionCheck.CheckAsync(
                    _versionCheckLifetime.Token);
            if (Volatile.Read(ref _disposed) == 0)
            {
                await RunOnDispatcherAsync(() =>
                    ApplyVersionCheckResult(result));
            }
        }
        catch (Exception)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await RunOnDispatcherAsync(() =>
                {
                    VersionCheckStatusIsError = true;
                    VersionCheckStatusText = "检查失败，请稍后重试。";
                });
            }
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await RunOnDispatcherAsync(() =>
                    IsVersionCheckRunning = false);
            }
        }
    }

    private void ApplyVersionCheckResult(ManualVersionCheckResult result)
    {
        UiDispatcher.VerifyAccess();
        SetAvailableReleasePage(
            result.Status == ManualVersionCheckStatus.UpdateAvailable
                ? result.ReleasePageUri
                : null);
        VersionCheckStatusIsError = result.Status is
            ManualVersionCheckStatus.NetworkFailure or
            ManualVersionCheckStatus.InvalidResponse or
            ManualVersionCheckStatus.InvalidCurrentVersion or
            ManualVersionCheckStatus.Failed;
        VersionCheckStatusText = result.Status switch
        {
            ManualVersionCheckStatus.UpdateAvailable =>
                result.LatestVersion is { } latestVersion
                    ? $"发现新版本 {latestVersion}。"
                    : "发现新版本。",
            ManualVersionCheckStatus.UpToDate =>
                result.CurrentVersion is { } currentVersion
                    ? $"已是最新版（{currentVersion}）。"
                    : "已是最新版。",
            ManualVersionCheckStatus.NetworkFailure =>
                "检查失败，请确认网络后重试。",
            ManualVersionCheckStatus.InvalidResponse =>
                "检查失败，发布渠道返回了无效信息。",
            ManualVersionCheckStatus.DevelopmentDisabled =>
                "Development 不连接真实版本渠道。",
            ManualVersionCheckStatus.StableChannelNotConfigured =>
                "尚未配置正式发布渠道；当前未联网检查。",
            ManualVersionCheckStatus.InvalidCurrentVersion =>
                "无法识别当前应用版本，未执行检查。",
            ManualVersionCheckStatus.Unavailable =>
                "当前界面不提供版本检查。",
            ManualVersionCheckStatus.Cancelled => "检查已取消。",
            _ => "检查失败，请稍后重试。"
        };
    }

    private void OpenReleasePage()
    {
        Uri? releasePageUri = _availableReleasePageUri;
        if (releasePageUri is null ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool opened;
        try
        {
            opened = _releasePageLauncher.TryOpen(releasePageUri);
        }
        catch
        {
            opened = false;
        }

        if (!opened)
        {
            VersionCheckStatusIsError = true;
            VersionCheckStatusText = "无法打开发布页面，请稍后重试。";
        }
    }

    private void SetAvailableReleasePage(Uri? releasePageUri)
    {
        if (_availableReleasePageUri == releasePageUri)
        {
            return;
        }

        _availableReleasePageUri = releasePageUri;
        OnPropertyChanged(nameof(CanOpenReleasePage));
        OpenReleasePageCommand.RaiseCanExecuteChanged();
    }

    private void SetPriceFilter(object? parameter)
    {
        if (parameter is PriceModelFilter filter)
        {
            if (SelectedPriceFilter == filter)
            {
                OnPropertyChanged(nameof(IsAllPriceFilterSelected));
                OnPropertyChanged(nameof(IsUnpricedFilterSelected));
                OnPropertyChanged(nameof(IsCustomFilterSelected));
            }
            else
            {
                SelectedPriceFilter = filter;
            }

        }
    }

    private async Task<DataOverviewSnapshot> LoadDataOverviewAsync(
        CancellationToken cancellationToken)
    {
        Task<string> sizeTask = Task.Run(
            () => FormatDatabaseSize(DatabasePath),
            cancellationToken);
        string requestCount = "暂时不可用";
        string timeRange = "暂时不可用";
        if (_queries is not null)
        {
            try
            {
                UsageOverview overview = await _queries.GetOverviewAsync(
                    new UsageFilter(
                        DateTimeOffset.UnixEpoch,
                        DateTimeOffset.MaxValue),
                    cancellationToken);
                requestCount = overview.RequestCount.ToString(
                    "N0",
                    CultureInfo.CurrentCulture);
                timeRange = overview.FirstOccurredAtUtc is { } first &&
                    overview.LastOccurredAtUtc is { } last
                    ? FormatRange(first, last)
                    : "暂无数据";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        string size;
        try
        {
            size = await sizeTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            size = "暂时不可用";
        }

        return new DataOverviewSnapshot(size, requestCount, timeRange);
    }

    private string FormatRange(DateTimeOffset firstUtc, DateTimeOffset lastUtc)
    {
        DateTimeOffset first = TimeZoneInfo.ConvertTime(firstUtc, _localTimeZone);
        DateTimeOffset last = TimeZoneInfo.ConvertTime(lastUtc, _localTimeZone);
        return first.Date == last.Date
            ? $"{first:yyyy年M月d日}"
            : $"{first:yyyy年M月d日} — {last:yyyy年M月d日}";
    }

    private void ApplyStartupRegistrationStatus(
        StartupRegistrationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _isStartupEnabled = status.IsEnabled;
        CanChangeStartupRegistration = status.State is
            StartupRegistrationState.Disabled or
            StartupRegistrationState.Enabled;
        StartupRegistrationMessage = status.Message;
        OnPropertyChanged(nameof(IsStartupEnabled));
    }

    private void ApplyLastBackup(DateTimeOffset? value)
    {
        LastBackupText = value is { } utc
            ? TimeZoneInfo.ConvertTime(utc, _localTimeZone)
                .ToString("yyyy年M月d日 HH:mm", CultureInfo.CurrentCulture)
            : "尚未备份";
    }

    private static string FormatDatabaseSize(string databasePath)
    {
        long bytes = 0;
        bool found = false;
        foreach (string path in new[]
                 {
                     databasePath,
                     $"{databasePath}-wal",
                     $"{databasePath}-shm"
                 })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            found = true;
            bytes = checked(bytes + new FileInfo(path).Length);
        }

        if (!found)
        {
            return "暂时不可用";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }

    private sealed record DataOverviewSnapshot(
        string DatabaseSize,
        string RequestCount,
        string TimeRange);

    private void OpenSettingsSection(object? parameter)
    {
        if (parameter is not SettingsSection section ||
            section == SettingsSection.Home)
        {
            throw new ArgumentException(
                "目标不是可进入的设置分类。",
                nameof(parameter));
        }

        SelectedSection = section;
    }

    private void DiscardPriceChanges()
    {
        LoadEditor(SelectedPriceModel?.EffectiveRate);
        PriceValidationMessage = null;
        PriceOperationMessage = null;
        PriceOperationIsError = false;
        ApplyPriceFilter(SelectedPriceModel?.NormalizedModel);
    }

    private async Task SavePriceAsync()
    {
        PriceSettingPresentation? selected = SelectedPriceModel;
        if (selected is null)
        {
            return;
        }

        ModelPriceRate? rate = TryBuildRate(selected.NormalizedModel);
        if (rate is null)
        {
            return;
        }

        await ExecutePriceCommandAsync(
            PriceCommandRequest.SetOverride(rate),
            selected.NormalizedModel);
    }

    private async Task RestorePriceAsync()
    {
        PriceSettingPresentation? selected = SelectedPriceModel;
        if (selected is null ||
            !selected.HasCustomPrice ||
            !_restoreConfirmation.ConfirmModelRestore(
                selected.NormalizedModel,
                selected.HasBuiltInDefault))
        {
            return;
        }

        await ExecutePriceCommandAsync(
            PriceCommandRequest.RestoreDefault(selected.NormalizedModel),
            selected.NormalizedModel);
    }

    private async Task RestoreAllPricesAsync()
    {
        int count = _allPriceModels.Count(static row => row.HasCustomPrice);
        if (count == 0 || !_restoreConfirmation.ConfirmAllRestore(count))
        {
            return;
        }

        await ExecutePriceCommandAsync(
            PriceCommandRequest.RestoreAllDefaults(),
            SelectedPriceModel?.NormalizedModel);
    }

    private async Task ExecutePriceCommandAsync(
        PriceCommandRequest request,
        string? preferredModel)
    {
        PriceValidationMessage = null;
        PriceOperationMessage = null;
        PriceOperationIsError = false;
        IsPriceOperationRunning = true;
        try
        {
            PriceCommandResponse response =
                await _priceCommands.SendAsync(
                    request,
                    CancellationToken.None);
            ApplyOperationResponse(response);
            await ReloadPriceRowsSafelyAsync(
                preferredModel,
                forceEditorReload:
                    response.Result == PriceCommandResultCode.Success);
        }
        catch (PriceCommandResultUnconfirmedException)
        {
            PriceOperationIsError = true;
            PriceOperationMessage =
                "未能确认操作结果；已重新读取当前数据库状态，请核对后再决定是否重试。";
            await ReloadPriceRowsSafelyAsync(
                preferredModel,
                forceEditorReload: true);
        }
        catch (PriceCommandUnavailableException)
        {
            PriceOperationIsError = true;
            PriceOperationMessage =
                "后台 Core 暂不可用，价格未通过 UI 写入；请稍后重试。";
        }
        catch
        {
            PriceOperationIsError = true;
            PriceOperationMessage =
                "价格设置未完成；UI 没有直接写入 SQLite，请稍后重试。";
        }
        finally
        {
            IsPriceOperationRunning = false;
        }
    }

    private void ApplyOperationResponse(PriceCommandResponse response)
    {
        PriceOperationIsError = response.Result != PriceCommandResultCode.Success;
        PriceOperationMessage = response.Result switch
        {
            PriceCommandResultCode.Success => response.NewlyPricedRecords > 0
                ? $"操作已完成，并为 {response.NewlyPricedRecords:N0} 条未计价记录补充了价格。"
                : "操作已完成；已计价历史保持不变。",
            PriceCommandResultCode.Busy =>
                "后台正在写入或更新统计，请稍后重试。",
            PriceCommandResultCode.InvalidRequest =>
                "价格参数无效，未保存任何修改。",
            PriceCommandResultCode.UnsupportedProtocol =>
                "UI 与 Core 版本不一致，请完全退出后重新打开 AgenTally。",
            _ => "价格设置未完成，请稍后重试。"
        };
    }

    private async Task ReloadPriceRowsAsync(
        string? preferredModel,
        bool forceEditorReload)
    {
        if (_queries is null)
        {
            return;
        }

        IReadOnlyList<PriceSettingRow> rows =
            await _queries.GetPriceSettingsAsync(CancellationToken.None);
        await RunOnDispatcherAsync(() => ApplyPriceRows(
            rows,
            preferredModel,
            forceEditorReload));
    }

    private async Task ReloadPriceRowsSafelyAsync(
        string? preferredModel,
        bool forceEditorReload)
    {
        try
        {
            await ReloadPriceRowsAsync(preferredModel, forceEditorReload);
        }
        catch
        {
            PriceOperationIsError = true;
            PriceOperationMessage =
                (PriceOperationMessage ?? "价格操作结果暂时无法确认。") +
                " 当前价格快照也暂时无法读取。";
        }
    }

    private void ApplyPriceRows(
        IEnumerable<PriceSettingRow> rows,
        string? preferredModel,
        bool forceEditorReload = false)
    {
        _allPriceModels.Clear();
        _allPriceModels.AddRange(rows
            .Select(static row => new PriceSettingPresentation(row))
            .OrderBy(static row => row.SortRank)
            .ThenByDescending(static row => row.ObservedRecords)
            .ThenBy(
                static row => row.NormalizedModel,
                StringComparer.OrdinalIgnoreCase));
        if (forceEditorReload &&
            (preferredModel is null ||
             !_allPriceModels.Any(row =>
                 string.Equals(
                     row.NormalizedModel,
                     preferredModel,
                     StringComparison.Ordinal) &&
                 MatchesPriceFilter(row))))
        {
            ResetPriceFilterToAllWithoutApplying();
        }

        ApplyPriceFilter(preferredModel, forceEditorReload);
        NotifyPriceCollectionState();
        NotifyPriceCommandState();
    }

    private void ApplyPriceFilter(
        string? preferredModel = null,
        bool forceEditorReload = false)
    {
        string filter = PriceSearchText.Trim();
        List<PriceSettingPresentation> visible = _allPriceModels
            .Where(row =>
                MatchesPriceFilter(row) &&
                (filter.Length == 0 ||
                 row.NormalizedModel.Contains(
                     filter,
                     StringComparison.OrdinalIgnoreCase)))
            .ToList();

        PriceSettingPresentation? current = SelectedPriceModel;
        if (!forceEditorReload &&
            HasUnsavedPriceChanges &&
            current is not null &&
            visible.All(row => !SameModel(row, current)))
        {
            visible.Insert(0, current);
        }

        PriceModels = new ObservableCollection<PriceSettingPresentation>(visible);
        string? selection = !forceEditorReload && HasUnsavedPriceChanges
            ? current?.NormalizedModel
            : preferredModel ?? current?.NormalizedModel;
        PriceSettingPresentation? selected = PriceModels.FirstOrDefault(row =>
                string.Equals(
                    row.NormalizedModel,
                    selection,
                    StringComparison.Ordinal)) ??
            PriceModels.FirstOrDefault();
        bool preserveDraft = !forceEditorReload &&
            HasUnsavedPriceChanges &&
            SameModel(current, selected);
        SetSelectedPriceModel(
            selected,
            loadEditor: !preserveDraft,
            forceEditorReload);
        OnPropertyChanged(nameof(HasVisiblePriceModels));
        OnPropertyChanged(nameof(HasNoVisiblePriceModels));
    }

    private bool MatchesPriceFilter(PriceSettingPresentation row) =>
        SelectedPriceFilter switch
        {
            PriceModelFilter.Unpriced => row.IsUnpriced,
            PriceModelFilter.Custom => row.HasCustomPrice,
            _ => true
        };

    private void ResetPriceFilterToAllWithoutApplying()
    {
        if (!SetProperty(
                ref _selectedPriceFilter,
                PriceModelFilter.All,
                nameof(SelectedPriceFilter)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsAllPriceFilterSelected));
        OnPropertyChanged(nameof(IsUnpricedFilterSelected));
        OnPropertyChanged(nameof(IsCustomFilterSelected));
    }

    private ModelPriceRate? TryBuildRate(string normalizedModel)
    {
        if (!TryParseRequiredDecimal(
                InputRateText,
                out decimal input) ||
            !TryParseRequiredDecimal(
                OutputRateText,
                out decimal output))
        {
            PriceValidationMessage =
                "输入价格和输出价格为必填数字。";
            return null;
        }

        if (!TryParseOptionalDecimal(
                CachedInputRateText,
                out decimal? cachedInput) ||
            !TryParseOptionalDecimal(
                CacheWriteRateText,
                out decimal? cacheWrite) ||
            !TryParseOptionalInt64(
                LongContextThresholdText,
                out long? threshold) ||
            !TryParseRequiredDecimal(
                LongContextInputMultiplierText,
                out decimal inputMultiplier) ||
            !TryParseRequiredDecimal(
                LongContextOutputMultiplierText,
                out decimal outputMultiplier))
        {
            PriceValidationMessage =
                "可选费率、长上下文阈值或倍率格式不正确。";
            return null;
        }

        try
        {
            return new ModelPriceRate(
                normalizedModel,
                input,
                cachedInput,
                cacheWrite,
                output,
                threshold,
                inputMultiplier,
                outputMultiplier);
        }
        catch (ArgumentOutOfRangeException)
        {
            PriceValidationMessage =
                "价格须在 0–1,000,000 之间；阈值须为正整数；倍率须在 1–100 之间。";
            return null;
        }
        catch (ArgumentException)
        {
            PriceValidationMessage = "模型名称或价格参数无效。";
            return null;
        }
    }

    private void LoadEditor(ModelPriceRate? rate)
    {
        _isLoadingPriceEditor = true;
        try
        {
            InputRateText = Format(rate?.InputUsdPerMillion);
            CachedInputRateText = Format(rate?.CachedInputUsdPerMillion);
            CacheWriteRateText = Format(rate?.CacheWriteUsdPerMillion);
            OutputRateText = Format(rate?.OutputUsdPerMillion);
            LongContextThresholdText =
                rate?.LongContextThresholdTokens?.ToString(
                    CultureInfo.InvariantCulture) ?? string.Empty;
            LongContextInputMultiplierText =
                Format(rate?.LongContextInputMultiplier ?? 1m);
            LongContextOutputMultiplierText =
                Format(rate?.LongContextOutputMultiplier ?? 1m);
            _loadedPriceEditor = CapturePriceEditor();
            IsLongContextExpanded = false;
        }
        finally
        {
            _isLoadingPriceEditor = false;
        }

        NotifyPriceEditorState();
    }

    private void SetEditorText(
        ref string field,
        string? value,
        [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(
                ref field,
                value ?? string.Empty,
                propertyName))
        {
            PriceValidationMessage = null;
            if (!_isLoadingPriceEditor)
            {
                PriceOperationMessage = null;
                PriceOperationIsError = false;
                NotifyPriceEditorState();
            }
        }
    }

    private void NotifyPriceCommandState()
    {
        DiscardPriceChangesCommand.RaiseCanExecuteChanged();
        SavePriceCommand.RaiseCanExecuteChanged();
        RestorePriceCommand.RaiseCanExecuteChanged();
        RestoreAllPricesCommand.RaiseCanExecuteChanged();
    }

    private void NotifyPriceEditorState()
    {
        OnPropertyChanged(nameof(HasUnsavedPriceChanges));
        OnPropertyChanged(nameof(CanSaveSelectedPrice));
        OnPropertyChanged(nameof(CanRestoreSelectedPrice));
        OnPropertyChanged(nameof(CanRestoreAllPrices));
        OnPropertyChanged(nameof(PriceDraftStatusText));
        OnPropertyChanged(nameof(LongContextSummaryText));
        OnPropertyChanged(nameof(IsInheritedPriceEditor));
        NotifyPriceCommandState();
    }

    private void NotifyPriceCollectionState()
    {
        OnPropertyChanged(nameof(HasPriceModels));
        OnPropertyChanged(nameof(ObservedPriceModelCount));
        OnPropertyChanged(nameof(UnpricedPriceModelCount));
        OnPropertyChanged(nameof(CustomPriceModelCount));
        OnPropertyChanged(nameof(HasCustomPriceModels));
        OnPropertyChanged(nameof(PriceSummaryText));
        OnPropertyChanged(nameof(AllPriceFilterText));
        OnPropertyChanged(nameof(UnpricedPriceFilterText));
        OnPropertyChanged(nameof(CustomPriceFilterText));
        OnPropertyChanged(nameof(RestoreAllPricesText));
        OnPropertyChanged(nameof(CanRestoreAllPrices));
    }

    private void SetSelectedPriceModel(
        PriceSettingPresentation? value,
        bool loadEditor,
        bool forceEditorReload = false)
    {
        bool changed = SetProperty(ref _selectedPriceModel, value, nameof(SelectedPriceModel));
        if (loadEditor &&
            (forceEditorReload || changed || !HasUnsavedPriceChanges))
        {
            LoadEditor(value?.EffectiveRate);
            PriceValidationMessage = null;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(CanEditSelectedPrice));
            OnPropertyChanged(nameof(SelectedPriceRestoreText));
        }

        NotifyPriceEditorState();
    }

    private PriceEditorSnapshot CapturePriceEditor() => new(
        InputRateText,
        CachedInputRateText,
        CacheWriteRateText,
        OutputRateText,
        LongContextThresholdText,
        LongContextInputMultiplierText,
        LongContextOutputMultiplierText);

    private static bool SameModel(
        PriceSettingPresentation? left,
        PriceSettingPresentation? right) =>
        string.Equals(
            left?.NormalizedModel,
            right?.NormalizedModel,
            StringComparison.Ordinal);

    private static string Format(decimal? value) =>
        value?.ToString("0.############################", CultureInfo.InvariantCulture) ??
        string.Empty;

    private static bool TryParseRequiredDecimal(
        string text,
        out decimal value) =>
        TryParseDecimal(text, out value);

    private static bool TryParseOptionalDecimal(
        string text,
        out decimal? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        bool parsed = TryParseDecimal(text, out decimal result);
        value = parsed ? result : null;
        return parsed;
    }

    private static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(
            text,
            NumberStyles.Number,
            CultureInfo.CurrentCulture,
            out value) ||
        decimal.TryParse(
            text,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);

    private static bool TryParseOptionalInt64(
        string text,
        out long? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        bool parsed = long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.CurrentCulture,
            out long result);
        value = parsed ? result : null;
        return parsed;
    }

    private sealed record PriceEditorSnapshot(
        string InputRate,
        string CachedInputRate,
        string CacheWriteRate,
        string OutputRate,
        string LongContextThreshold,
        string LongContextInputMultiplier,
        string LongContextOutputMultiplier)
    {
        public static PriceEditorSnapshot Empty { get; } = new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "1",
            "1");
    }
}

public sealed record PriceSettingPresentation(PriceSettingRow Row)
{
    public string NormalizedModel => Row.NormalizedModel;

    public ModelPriceRate? EffectiveRate => Row.EffectiveRate;

    public bool HasBuiltInDefault => Row.BuiltInRate is not null;

    public bool HasCustomPrice => Row.CustomRate is not null;

    public bool HasObservedRecords => Row.ObservedRecords > 0;

    public bool IsUnpriced => EffectiveRate is null;

    public long ObservedRecords => Row.ObservedRecords;

    public int SortRank => IsUnpriced
        ? 0
        : HasCustomPrice
            ? 1
            : 2;

    public string SourceText => Row.Source switch
    {
        PriceSettingSource.BuiltInDefault => "默认价格",
        PriceSettingSource.CustomOverride => "自定义价格",
        _ => "未计价"
    };

    public string ObservedRecordsText => Row.ObservedRecords > 0
        ? $"{Row.ObservedRecords:N0} 条"
        : "暂无记录";
}
