using System.IO;
using System.Windows;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Runtime;
using AgenTally.UI.Infrastructure;
using AgenTally.UI.Runtime;
using AgenTally.UI.Updates;
using AgenTally.UI.ViewModels;
using System.Windows.Threading;

namespace AgenTally.UI;

internal enum UiStartupMode
{
    Interactive,
    Background
}

public partial class App : Application
{
    private const string DatabasePathEnvironmentVariable =
        "AGENTALLY_DATABASE_PATH";
    private MainViewModel? _mainViewModel;
    private ApplicationShutdownSignal? _shutdownSignal;
    private CancellationTokenSource? _shutdownCancellation;
    private Thread? _shutdownThread;
    private UiInstanceRegistration? _uiInstanceRegistration;
    private CancellationTokenSource? _activationCancellation;
    private Thread? _activationThread;
    private UiLifecycleLog? _lifecycleLog;
    private AutomaticVersionCheckCoordinator?
        _automaticVersionCheckCoordinator;
    private CancellationTokenSource? _automaticVersionCheckCancellation;
    private int _activationPending;
    private readonly bool _enableOwnedRuntimeStartup;

    public App()
        : this(enableOwnedRuntimeStartup: true)
    {
    }

    internal App(bool enableOwnedRuntimeStartup)
    {
        _enableOwnedRuntimeStartup = enableOwnedRuntimeStartup;
    }

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (!_enableOwnedRuntimeStartup)
        {
            return;
        }

        UiStartupMode startupMode = UiStartupMode.Interactive;
        try
        {
            startupMode = ResolveStartupMode(eventArgs.Args);
            AgenTallyRuntimeProfile profile =
                AgenTallyRuntimeProfile.CreateDefault();
            if (startupMode == UiStartupMode.Background)
            {
                int exitCode = await RunBackgroundStartupAsync(
                    new CoreRuntimeController(
                        profile,
                        new SystemCoreProcessRuntime()));
                Shutdown(exitCode);
                return;
            }

            string? configuredPath = Environment.GetEnvironmentVariable(
                DatabasePathEnvironmentVariable);
            string? standaloneDatabasePath =
                ResolveStandaloneDiagnosticDatabasePath(
                    profile,
                    configuredPath);

            MainWindow window;
            if (standaloneDatabasePath is not null)
            {
                window = ComposeWindow(
                    standaloneDatabasePath,
                    Dispatcher,
                    new StandaloneCoreRuntimeController(),
                    profile.DisplayName,
                    isDevelopment: true,
                    channel: profile.Channel);
            }
            else
            {
                _uiInstanceRegistration =
                    await UiInstanceRegistration.TryRegisterAsync(profile);
                if (_uiInstanceRegistration is null)
                {
                    Shutdown();
                    return;
                }

                _lifecycleLog = new UiLifecycleLog(profile);
                _lifecycleLog.Write("ui_starting");
                _lifecycleLog.Write(
                    $"profile_{profile.ProfileId.ToLowerInvariant()}");
                _lifecycleLog.WriteHashedIdentity(
                    "event",
                    profile.ShutdownEventName);
                var controller = new CoreRuntimeController(
                    profile,
                    new SystemCoreProcessRuntime());
                _automaticVersionCheckCoordinator =
                    AutomaticVersionCheckProductionComposition.Create(
                        profile,
                        typeof(App).Assembly,
                        new MessageBoxAutomaticVersionCheckPresenter(
                            new ShellReleasePageLauncher()));
                _shutdownSignal = new ApplicationShutdownSignal(profile);
                _shutdownCancellation = new CancellationTokenSource();
                _shutdownThread = new Thread(() =>
                    ObserveApplicationShutdown(
                        _shutdownSignal,
                        _shutdownCancellation.Token))
                {
                    IsBackground = true,
                    Name = "AgenTally.UI.ApplicationShutdown"
                };
                _shutdownThread.Start();
                _lifecycleLog.Write("shutdown_wait_started");
                _activationCancellation = new CancellationTokenSource();
                _activationThread = new Thread(() =>
                    ObserveUiActivation(
                        _uiInstanceRegistration.ActivationSignal,
                        _activationCancellation.Token))
                {
                    IsBackground = true,
                    Name = "AgenTally.UI.Activation"
                };
                _activationThread.Start();
                _lifecycleLog.Write("activation_wait_started");
                window = ComposeWindow(
                    profile.DatabasePath,
                    Dispatcher,
                    controller,
                    profile.DisplayName,
                    profile.Channel == AgenTallyChannel.Development,
                    profile.Channel,
                    profile);
            }

            _mainViewModel = (MainViewModel)window.DataContext;
            MainWindow = window;
            window.Show();
            _lifecycleLog?.Write("ui_shown");
            if (Interlocked.Exchange(ref _activationPending, 0) != 0)
            {
                window.ActivateFromExternalRequest();
            }

            await _mainViewModel.StartAsync();
            _lifecycleLog?.Write("ui_started");
            if (_automaticVersionCheckCoordinator is not null)
            {
                _automaticVersionCheckCancellation =
                    new CancellationTokenSource();
                _ = await _automaticVersionCheckCoordinator.RunAsync(
                    _automaticVersionCheckCancellation.Token);
            }
        }
        catch (Exception)
        {
            if (startupMode == UiStartupMode.Background)
            {
                Shutdown(1);
                return;
            }

            MessageBox.Show(
                "AgenTally 启动环境不完整或不可访问。开发版请使用仓库提供的启动脚本，正式版请重新安装。",
                "AgenTally 无法启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    internal static UiStartupMode ResolveStartupMode(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return UiStartupMode.Interactive;
        }

        if (arguments.Count == 1 && string.Equals(
                arguments[0],
                StartupRegistrationCommand.BackgroundArgument,
                StringComparison.Ordinal))
        {
            return UiStartupMode.Background;
        }

        throw new InvalidOperationException(
            "Unsupported AgenTally UI startup argument.");
    }

    internal static async Task<int> RunBackgroundStartupAsync(
        ICoreRuntimeController runtimeController,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeController);
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(20);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var cancellation = new CancellationTokenSource(effectiveTimeout);
        try
        {
            CoreRuntimeUiStatus status = await runtimeController.EnsureAsync(
                cancellation.Token);
            return status.IsError ? 1 : 0;
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            return 1;
        }
    }

    internal static string? ResolveStandaloneDiagnosticDatabasePath(
        AgenTallyRuntimeProfile profile,
        string? configuredPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Channel != AgenTallyChannel.Development ||
            string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            string candidate = Path.GetFullPath(configuredPath.Trim());
            if (!profile.IsDevelopmentOwnedPath(candidate))
            {
                throw new InvalidOperationException();
            }

            return candidate;
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                InvalidOperationException or
                NotSupportedException or
                PathTooLongException or
                System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                "Development diagnostic database override is not permitted.");
        }
    }

    internal static MainWindow ComposeWindow(
        string databasePath,
        Dispatcher dispatcher,
        ICoreRuntimeController? runtimeController = null,
        string applicationDisplayName = "AgenTally",
        bool isDevelopment = false,
        AgenTallyChannel? channel = null,
        AgenTallyRuntimeProfile? runtimeProfile = null,
        IUiPreferencesStore? preferencesStore = null,
        IDataManagementStateStore? dataManagementState = null,
        IDataBackupInteraction? dataBackupInteraction = null,
        IStartupRegistrationStore? startupRegistrationStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(dispatcher);

        var storage = new StorageOptions(databasePath);
        var connections = new SqliteConnectionFactory(storage);
        preferencesStore ??= runtimeProfile is null
            ? new UnavailableUiPreferencesStore()
            : new JsonUiPreferencesStore(runtimeProfile);
        var queries = new BackgroundUsageQueryService(
            new SqliteUsageQueryService(connections));
        var dataChanges = new SqliteUsageDataChangeMonitor(connections);
        var dashboard = new DashboardViewModel(queries, dispatcher);
        var analysis = new AnalysisViewModel(queries, dispatcher);
        var projects = new ProjectsViewModel(queries, dispatcher);
        var sessions = new SessionsViewModel(queries, dispatcher);
        var sources = new SourcesViewModel(queries, dispatcher);
        var settings = new SettingsViewModel(
            queries,
            runtimeProfile is null
                ? new UnavailablePriceCommandClient()
                : new NamedPipePriceCommandClient(
                    runtimeProfile.PriceCommandPipeName),
            runtimeProfile is null
                ? new RejectingPriceRestoreConfirmation()
                : new MessageBoxPriceRestoreConfirmation(),
            dispatcher,
            connections.DatabasePath,
            channel,
            preferencesStore,
            runtimeProfile is null
                ? new UnavailableManualVersionCheckCoordinator()
                : ManualVersionCheckProductionComposition.Create(
                    runtimeProfile,
                    typeof(App).Assembly),
            runtimeProfile is null
                ? new UnavailableReleasePageLauncher()
                : new ShellReleasePageLauncher(),
            dataManagementState ?? (runtimeProfile is null
                ? new UnavailableDataManagementStateStore()
                : new JsonDataManagementStateStore(runtimeProfile)),
            startupRegistrationStore ?? (runtimeProfile is null
                ? new UnavailableStartupRegistrationStore()
                : StartupRegistrationProductionComposition.Create(
                    runtimeProfile)));
        var mainViewModel = new MainViewModel(
            dashboard,
            analysis,
            projects,
            sessions,
            sources,
            settings,
            dispatcher,
            dataChanges,
            queryMaintenanceGate: queries,
            runtimeController: runtimeController,
            applicationDisplayName: applicationDisplayName,
            isDevelopment: isDevelopment,
            clearStatisticsConfirmation: runtimeProfile is null
                ? new RejectingClearStatisticsConfirmation()
                : new MessageBoxClearStatisticsConfirmation(),
            dataBackupInteraction: dataBackupInteraction ?? (runtimeProfile is null
                ? new RejectingDataBackupInteraction()
                : new WindowsDataBackupInteraction(runtimeProfile)));
        return new MainWindow(mainViewModel, preferencesStore);
    }

    private void ObserveApplicationShutdown(
        ApplicationShutdownSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            signal.Wait(cancellationToken);
            _lifecycleLog?.Write("shutdown_signal_observed");
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    _lifecycleLog?.Write("shutdown_invoked");
                    Shutdown();
                }));
            _lifecycleLog?.Write("shutdown_queued");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _lifecycleLog?.Write("shutdown_wait_failed");
        }
    }

    private void ObserveUiActivation(
        UiActivationSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                signal.Wait(cancellationToken);
                _lifecycleLog?.Write("activation_signal_observed");
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(ActivateMainWindow));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _lifecycleLog?.Write("activation_wait_failed");
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => Shutdown(1)));
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not MainWindow window || !window.IsLoaded)
        {
            Interlocked.Exchange(ref _activationPending, 1);
            return;
        }

        Interlocked.Exchange(ref _activationPending, 0);
        window.ActivateFromExternalRequest();
        _lifecycleLog?.Write("ui_activated");
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        _lifecycleLog?.Write("ui_exit_entered");
        _automaticVersionCheckCancellation?.Cancel();
        _automaticVersionCheckCancellation?.Dispose();
        _automaticVersionCheckCancellation = null;
        _automaticVersionCheckCoordinator?.Dispose();
        _automaticVersionCheckCoordinator = null;
        _activationCancellation?.Cancel();
        if (_activationThread is not null &&
            !_activationThread.Join(TimeSpan.FromSeconds(1)))
        {
            _lifecycleLog?.Write("activation_thread_join_timeout");
        }

        _activationThread = null;
        _activationCancellation?.Dispose();
        _activationCancellation = null;
        _uiInstanceRegistration?.Dispose();
        _uiInstanceRegistration = null;
        _mainViewModel?.Dispose();
        _mainViewModel = null;
        _shutdownCancellation?.Cancel();
        if (_shutdownThread is not null &&
            !_shutdownThread.Join(TimeSpan.FromSeconds(1)))
        {
            _lifecycleLog?.Write("shutdown_thread_join_timeout");
        }

        _shutdownThread = null;
        _shutdownCancellation?.Dispose();
        _shutdownCancellation = null;
        _shutdownSignal?.Dispose();
        _shutdownSignal = null;
        _lifecycleLog?.Write("ui_exit_completed");
        _lifecycleLog = null;
        base.OnExit(eventArgs);
    }

}
