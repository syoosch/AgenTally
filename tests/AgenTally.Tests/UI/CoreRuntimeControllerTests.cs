using System.IO;
using AgenTally.Core.Hosting;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.UI;

[TestClass]
[DoNotParallelize]
public sealed class CoreRuntimeControllerTests
{
    [TestMethod]
    public async Task EnsureAsync_ReusesHealthyCoreWithoutLaunching()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 456, profile.CoreExecutablePath);
        await WriteStatusAsync(profile, 73, 456, CoreRuntimePhase.Running);
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.AreEqual(0, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_MissingCoreReturnsActionableFailureWithoutPathSearch()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        var processes = new FakeCoreProcessRuntime();
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.MissingCore, result.State);
        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_LaunchesOnlyFixedProfilePathAndAcceptsConcurrentWinner()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        processes.OnStart = (path, _) =>
        {
            processes.SetAccessible(91, 789, profile.CoreExecutablePath);
            WriteStatusAsync(profile, 91, 789, CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.AreEqual(1, processes.StartCalls);
        Assert.AreEqual(
            Path.GetFullPath(profile.CoreExecutablePath),
            processes.LastStartedPath);
    }

    [TestMethod]
    public async Task EnsureAsync_RejectsStaleIdentityThenUsesNewWinner()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 999, profile.CoreExecutablePath);
        await WriteStatusAsync(profile, 73, 456, CoreRuntimePhase.Running);
        processes.OnStart = (_, _) =>
        {
            processes.SetAccessible(74, 1000, profile.CoreExecutablePath);
            WriteStatusAsync(profile, 74, 1000, CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.AreEqual(1, processes.StartCalls);
    }

    [TestMethod]
    public async Task ReadStatusAsync_ReportsExitedCoreAsRetryableFailureWithoutLaunching()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.Failed,
            CoreRuntimeErrorCode.UnexpectedFailure);
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.ReadStatusAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.Failed, result.State);
        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.CanRetry);
        Assert.AreEqual(0, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_MapsTerminalParserFailureWithoutRelaunchingLiveCore()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 456, profile.CoreExecutablePath);
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.NeedsParserRescan,
            CoreRuntimeErrorCode.ParserRescanRequired);
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.ParserRebuildRequired, result.State);
        Assert.IsTrue(result.IsError);
        Assert.AreEqual(0, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_RetriesCurrentTerminalParserFailureAfterCoreExited()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.NeedsParserRescan,
            CoreRuntimeErrorCode.ParserRescanRequired);
        processes.OnStart = (_, _) =>
        {
            processes.SetAccessible(74, 1000, profile.CoreExecutablePath);
            WriteStatusAsync(profile, 74, 1000, CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.IsFalse(result.IsError);
        Assert.AreEqual(1, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_ExposesAutomaticStatisticsUpdateWithoutTimingOut()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 456, profile.CoreExecutablePath);
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.UpdatingStatistics);
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.UpdatingStatistics, result.State);
        Assert.IsFalse(result.IsError);
        Assert.IsFalse(result.CanRetry);
        Assert.Contains("仍可查看原有统计", result.Message);
        Assert.AreEqual(0, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_DoesNotTrustExitedTerminalStatusFromOldApplication()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.NeedsParserRescan,
            CoreRuntimeErrorCode.ParserRescanRequired,
            applicationVersion: "0.9.0-dev");
        processes.OnStart = (_, _) =>
        {
            processes.SetAccessible(74, 1000, profile.CoreExecutablePath);
            WriteStatusAsync(
                    profile,
                    74,
                    1000,
                    CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.AreEqual(1, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_InvalidProtocolFailsClosedWithoutDeletingStatus()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        Directory.CreateDirectory(profile.RuntimeRoot);
        await File.WriteAllTextAsync(
            profile.StatusPath,
            "{\"protocolVersion\":999}");
        var processes = new FakeCoreProcessRuntime();
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.StatusInvalid, result.State);
        Assert.IsTrue(File.Exists(profile.StatusPath));
        Assert.AreEqual(0, processes.StartCalls);
    }

    [TestMethod]
    public async Task EnsureAsync_BoundsLaunchWhenNoCorePublishesStatus()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        var controller = new CoreRuntimeController(
            profile,
            processes,
            TimeProvider.System,
            startupTimeout: TimeSpan.FromMilliseconds(150),
            statusPollInterval: TimeSpan.FromMilliseconds(10));

        CoreRuntimeUiStatus result = await controller.EnsureAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.StartupTimedOut, result.State);
        Assert.AreEqual(1, processes.StartCalls);
    }

    [TestMethod]
    public async Task RebuildCodexAsync_StopsRunningCoreThenRescansAndRestarts()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.Running);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 456, profile.CoreExecutablePath);
        using var shutdownSignal = new CoreMaintenanceShutdownSignal(profile);
        using var shutdownTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task shutdownObserved = Task.Run(async () =>
        {
            await shutdownSignal.WaitAsync(shutdownTimeout.Token);
            await WriteStatusAsync(
                profile,
                73,
                456,
                CoreRuntimePhase.Stopped,
                exitCode: 0);
            processes.Remove(73);
        });
        int launches = 0;
        processes.OnStart = (_, arguments) =>
        {
            launches++;
            if (arguments.SequenceEqual(["--rescan-codex"]))
            {
                WriteStatusAsync(
                        profile,
                        91,
                        789,
                        CoreRuntimePhase.Stopped,
                        exitCode: 0)
                    .GetAwaiter()
                    .GetResult();
                return;
            }

            processes.SetAccessible(92, 790, profile.CoreExecutablePath);
            WriteStatusAsync(profile, 92, 790, CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.RebuildCodexAsync(
            CancellationToken.None);
        await shutdownObserved;

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.AreEqual(2, launches);
        CollectionAssert.AreEqual(
            new[] { "--rescan-codex" },
            processes.StartArguments[0].ToArray());
        Assert.HasCount(0, processes.StartArguments[1]);
    }

    [TestMethod]
    public async Task ClearStatisticsAsync_StopsRunningCoreThenClearsAndRestarts()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.Running);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 456, profile.CoreExecutablePath);
        using var shutdownSignal = new CoreMaintenanceShutdownSignal(profile);
        using var shutdownTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task shutdownObserved = Task.Run(async () =>
        {
            await shutdownSignal.WaitAsync(shutdownTimeout.Token);
            await WriteStatusAsync(
                profile,
                73,
                456,
                CoreRuntimePhase.Stopped,
                exitCode: 0);
            processes.Remove(73);
        });
        int launches = 0;
        processes.OnStart = (_, arguments) =>
        {
            launches++;
            if (arguments.SequenceEqual(["--clear-statistics"]))
            {
                WriteStatusAsync(
                        profile,
                        91,
                        789,
                        CoreRuntimePhase.Stopped,
                        exitCode: 0)
                    .GetAwaiter()
                    .GetResult();
                return;
            }

            processes.SetAccessible(92, 790, profile.CoreExecutablePath);
            WriteStatusAsync(profile, 92, 790, CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.ClearStatisticsAsync(
            CancellationToken.None);
        await shutdownObserved;

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.AreEqual(2, launches);
        CollectionAssert.AreEqual(
            new[] { "--clear-statistics" },
            processes.StartArguments[0].ToArray());
        Assert.HasCount(0, processes.StartArguments[1]);
    }

    [TestMethod]
    [DataRow(DataMaintenanceOperation.CreateBackup, "--create-backup")]
    [DataRow(DataMaintenanceOperation.RestoreBackup, "--restore-backup")]
    public async Task DataMaintenance_UsesFixedArgumentAndProfileScopedRequest(
        DataMaintenanceOperation operation,
        string expectedArgument)
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        await WriteStatusAsync(profile, 73, 456, CoreRuntimePhase.Running);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 456, profile.CoreExecutablePath);
        using var shutdownSignal = new CoreMaintenanceShutdownSignal(profile);
        using var shutdownTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task shutdownObserved = Task.Run(async () =>
        {
            await shutdownSignal.WaitAsync(shutdownTimeout.Token);
            await WriteStatusAsync(
                profile,
                73,
                456,
                CoreRuntimePhase.Stopped,
                exitCode: 0);
            processes.Remove(73);
        });
        string selectedPath = directory.File("selected.agentally-backup");
        DataMaintenanceRequest? observedRequest = null;
        processes.OnStart = (_, arguments) =>
        {
            if (arguments.SequenceEqual([expectedArgument]))
            {
                var store = new DataMaintenanceRequestStore(profile);
                observedRequest = store.ReadAsync(operation, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                store.Delete();
                WriteStatusAsync(
                        profile,
                        91,
                        789,
                        CoreRuntimePhase.Stopped,
                        exitCode: 0)
                    .GetAwaiter()
                    .GetResult();
                return;
            }

            processes.SetAccessible(92, 790, profile.CoreExecutablePath);
            WriteStatusAsync(profile, 92, 790, CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = operation == DataMaintenanceOperation.CreateBackup
            ? await controller.CreateBackupAsync(selectedPath, CancellationToken.None)
            : await controller.RestoreBackupAsync(selectedPath, CancellationToken.None);
        await shutdownObserved;

        Assert.AreEqual(CoreRuntimeUiState.Ready, result.State);
        Assert.IsNotNull(observedRequest);
        Assert.AreEqual(Path.GetFullPath(selectedPath), observedRequest.BackupPath);
        Assert.AreEqual(operation, observedRequest.Operation);
        CollectionAssert.AreEqual(
            new[] { expectedArgument },
            processes.StartArguments[0].ToArray());
        Assert.HasCount(0, processes.StartArguments[1]);
        Assert.IsFalse(File.Exists(profile.DataMaintenanceRequestPath));
    }

    [TestMethod]
    public async Task ClearStatisticsAsync_DoesNotClaimExistingMaintenanceClearedData()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        processes.SetAccessible(73, 456, profile.CoreExecutablePath);
        await WriteStatusAsync(
            profile,
            73,
            456,
            CoreRuntimePhase.UpdatingStatistics);
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.ClearStatisticsAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.UpdatingStatistics, result.State);
        Assert.Contains("本次未清除", result.Message);
        Assert.AreEqual(0, processes.StartCalls);
    }

    [TestMethod]
    public async Task ClearStatisticsAsync_FailureWaitsForExitAndRestartsContinuousCore()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        CreateCoreExecutable(profile);
        var processes = new FakeCoreProcessRuntime();
        int maintenanceInspections = 0;
        bool maintenanceExited = false;
        bool continuousStartedAfterMaintenanceExit = false;
        processes.OnInspect = processId =>
        {
            if (processId == 91 &&
                Interlocked.Increment(ref maintenanceInspections) == 2)
            {
                processes.Remove(91);
                maintenanceExited = true;
            }
        };
        processes.OnStart = (_, arguments) =>
        {
            if (arguments.SequenceEqual(["--clear-statistics"]))
            {
                processes.SetAccessible(91, 789, profile.CoreExecutablePath);
                WriteStatusAsync(
                        profile,
                        91,
                        789,
                        CoreRuntimePhase.Failed,
                        CoreRuntimeErrorCode.UnexpectedFailure,
                        exitCode: CoreExitCodes.RuntimeFailure)
                    .GetAwaiter()
                    .GetResult();
                return;
            }

            continuousStartedAfterMaintenanceExit = maintenanceExited;
            processes.SetAccessible(92, 790, profile.CoreExecutablePath);
            WriteStatusAsync(profile, 92, 790, CoreRuntimePhase.Running)
                .GetAwaiter()
                .GetResult();
        };
        var controller = CreateController(profile, processes);

        CoreRuntimeUiStatus result = await controller.ClearStatisticsAsync(
            CancellationToken.None);

        Assert.AreEqual(CoreRuntimeUiState.Failed, result.State);
        Assert.Contains("未清除任何统计", result.Message);
        Assert.IsTrue(continuousStartedAfterMaintenanceExit);
        Assert.AreEqual(2, processes.StartCalls);
        CollectionAssert.AreEqual(
            new[] { "--clear-statistics" },
            processes.StartArguments[0].ToArray());
        Assert.HasCount(0, processes.StartArguments[1]);
    }

    private static CoreRuntimeController CreateController(
        AgenTallyRuntimeProfile profile,
        ICoreProcessRuntime processes) => new(
            profile,
            processes,
            TimeProvider.System,
            startupTimeout: TimeSpan.FromSeconds(1),
            statusPollInterval: TimeSpan.FromMilliseconds(10),
            applicationVersion: "1.0.0-dev");

    private static AgenTallyRuntimeProfile CreateProfile(
        TestTempDirectory directory)
    {
        File.WriteAllText(directory.File("AgenTally.sln"), string.Empty);
        File.WriteAllText(directory.File(".agentally-root"), string.Empty);
        string codexHome = directory.File(Path.Combine("user", ".codex"));
        Directory.CreateDirectory(codexHome);
        return AgenTallyRuntimeProfile.CreateDevelopment(directory.Path, codexHome);
    }

    private static void CreateCoreExecutable(AgenTallyRuntimeProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(profile.CoreExecutablePath)!);
        File.WriteAllBytes(profile.CoreExecutablePath, [77, 90]);
    }

    private static Task WriteStatusAsync(
        AgenTallyRuntimeProfile profile,
        int processId,
        long processStartUtcTicks,
        CoreRuntimePhase phase,
        CoreRuntimeErrorCode errorCode = CoreRuntimeErrorCode.None,
        int? exitCode = null,
        string applicationVersion = "1.0.0-dev") =>
        new CoreRuntimeStatusStore(profile).WriteAsync(
            new CoreRuntimeStatus(
                CoreRuntimeStatus.CurrentProtocolVersion,
                profile.Channel,
                profile.ProfileId,
                applicationVersion,
                processId,
                processStartUtcTicks,
                phase,
                errorCode,
                MessageCode(phase),
                DateTimeOffset.UtcNow,
                exitCode),
            CancellationToken.None);

    private static string MessageCode(CoreRuntimePhase phase) => phase switch
    {
        CoreRuntimePhase.Running => "core_running",
        CoreRuntimePhase.UpdatingStatistics => "statistics_update_running",
        CoreRuntimePhase.NeedsParserRebuild => "parser_rebuild_required",
        CoreRuntimePhase.NeedsParserRescan => "parser_rescan_required",
        _ => "core_status"
    };

    private sealed class FakeCoreProcessRuntime : ICoreProcessRuntime
    {
        private readonly Dictionary<int, CoreProcessInspection> _processes = [];
        private readonly object _gate = new();

        public int StartCalls { get; private set; }

        public string? LastStartedPath { get; private set; }

        public List<IReadOnlyList<string>> StartArguments { get; } = [];

        public Action<string, IReadOnlyList<string>>? OnStart { get; set; }

        public Action<int>? OnInspect { get; set; }

        public CoreProcessInspection Inspect(int processId)
        {
            OnInspect?.Invoke(processId);
            lock (_gate)
            {
                return _processes.TryGetValue(
                    processId,
                    out CoreProcessInspection? process)
                    ? process
                    : CoreProcessInspection.Missing(processId);
            }
        }

        public void Start(
            string executablePath,
            IReadOnlyList<string> arguments)
        {
            StartCalls++;
            LastStartedPath = executablePath;
            StartArguments.Add(arguments.ToArray());
            OnStart?.Invoke(executablePath, arguments);
        }

        public void SetAccessible(int processId, long startTicks, string path)
        {
            lock (_gate)
            {
                _processes[processId] = CoreProcessInspection.Accessible(
                    processId,
                    startTicks,
                    path);
            }
        }

        public void Remove(int processId)
        {
            lock (_gate)
            {
                _processes.Remove(processId);
            }
        }
    }
}
