using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AgenTally.Storage.Runtime;

namespace AgenTally.UI.Runtime;

public enum CoreRuntimeUiState
{
    Standalone,
    Ready,
    Starting,
    UpdatingStatistics,
    Stopping,
    MissingCore,
    LaunchFailed,
    StartupTimedOut,
    StatusInvalid,
    SourceUnavailable,
    ParserRebuildRequired,
    DatabaseUnavailable,
    Failed
}

public sealed record CoreRuntimeUiStatus(
    CoreRuntimeUiState State,
    string Message,
    bool IsError,
    bool CanRetry)
{
    public static CoreRuntimeUiStatus Standalone { get; } = new(
        CoreRuntimeUiState.Standalone,
        string.Empty,
        false,
        false);
}

public enum CoreProcessInspectionState
{
    Missing,
    Accessible,
    Inaccessible
}

public sealed record CoreProcessInspection(
    CoreProcessInspectionState State,
    int ProcessId,
    long? ProcessStartUtcTicks,
    string? ExecutablePath)
{
    public static CoreProcessInspection Missing(int processId) => new(
        CoreProcessInspectionState.Missing,
        processId,
        null,
        null);

    public static CoreProcessInspection Inaccessible(int processId) => new(
        CoreProcessInspectionState.Inaccessible,
        processId,
        null,
        null);

    public static CoreProcessInspection Accessible(
        int processId,
        long processStartUtcTicks,
        string executablePath) => new(
            CoreProcessInspectionState.Accessible,
            processId,
            processStartUtcTicks,
            Path.GetFullPath(executablePath));
}

public interface ICoreProcessRuntime
{
    CoreProcessInspection Inspect(int processId);

    void Start(string executablePath, IReadOnlyList<string> arguments);
}

public interface ICoreRuntimeController
{
    Task<CoreRuntimeUiStatus> EnsureAsync(CancellationToken cancellationToken);

    Task<CoreRuntimeUiStatus> ReadStatusAsync(CancellationToken cancellationToken);

    Task<CoreRuntimeUiStatus> RebuildCodexAsync(
        CancellationToken cancellationToken);

    Task<CoreRuntimeUiStatus> ClearStatisticsAsync(
        CancellationToken cancellationToken);

    Task<CoreRuntimeUiStatus> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken) => EnsureAsync(cancellationToken);

    Task<CoreRuntimeUiStatus> RestoreBackupAsync(
        string backupPath,
        CancellationToken cancellationToken) => EnsureAsync(cancellationToken);
}

public sealed class StandaloneCoreRuntimeController : ICoreRuntimeController
{
    public Task<CoreRuntimeUiStatus> EnsureAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CoreRuntimeUiStatus.Standalone);
    }

    public Task<CoreRuntimeUiStatus> ReadStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CoreRuntimeUiStatus.Standalone);
    }

    public Task<CoreRuntimeUiStatus> RebuildCodexAsync(
        CancellationToken cancellationToken) =>
        EnsureAsync(cancellationToken);

    public Task<CoreRuntimeUiStatus> ClearStatisticsAsync(
        CancellationToken cancellationToken) =>
        EnsureAsync(cancellationToken);

    public Task<CoreRuntimeUiStatus> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken) =>
        EnsureAsync(cancellationToken);

    public Task<CoreRuntimeUiStatus> RestoreBackupAsync(
        string backupPath,
        CancellationToken cancellationToken) =>
        EnsureAsync(cancellationToken);
}

public sealed class SystemCoreProcessRuntime : ICoreProcessRuntime
{
    public CoreProcessInspection Inspect(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return CoreProcessInspection.Missing(processId);
            }

            string? executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return CoreProcessInspection.Inaccessible(processId);
            }

            return CoreProcessInspection.Accessible(
                processId,
                process.StartTime.ToUniversalTime().Ticks,
                executablePath);
        }
        catch (ArgumentException)
        {
            return CoreProcessInspection.Missing(processId);
        }
        catch (InvalidOperationException)
        {
            return CoreProcessInspection.Missing(processId);
        }
        catch (Exception exception)
            when (exception is Win32Exception or UnauthorizedAccessException)
        {
            return CoreProcessInspection.Inaccessible(processId);
        }
    }

    public void Start(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        string fullPath = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(fullPath) || !File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The fixed AgenTally Core executable is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ??
                throw new InvalidOperationException(
                    "The Core executable has no parent directory."),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in arguments)
        {
            if (!string.Equals(
                    argument,
                    "--rescan-codex",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    argument,
                    "--rebuild-codex",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    argument,
                    "--clear-statistics",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    argument,
                    "--create-backup",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    argument,
                    "--restore-backup",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Unsupported AgenTally Core launch argument.",
                    nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "Windows did not create the AgenTally Core process.");
    }
}

public sealed class CoreRuntimeController : ICoreRuntimeController
{
    private readonly AgenTallyRuntimeProfile _profile;
    private readonly ICoreProcessRuntime _processes;
    private readonly CoreRuntimeStatusStore _statusStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _maintenanceTimeout;
    private readonly TimeSpan _statusPollInterval;
    private readonly string _applicationVersion;
    private readonly DataMaintenanceRequestStore _dataRequests;

    public CoreRuntimeController(
        AgenTallyRuntimeProfile profile,
        ICoreProcessRuntime processes,
        TimeProvider? timeProvider = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? statusPollInterval = null,
        string? applicationVersion = null,
        TimeSpan? maintenanceTimeout = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _statusStore = new CoreRuntimeStatusStore(profile);
        _dataRequests = new DataMaintenanceRequestStore(profile);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
        _maintenanceTimeout = maintenanceTimeout ?? TimeSpan.FromMinutes(10);
        _statusPollInterval = statusPollInterval ?? TimeSpan.FromMilliseconds(100);
        _applicationVersion = applicationVersion ??
            typeof(CoreRuntimeController).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ??
            typeof(CoreRuntimeController).Assembly.GetName().Version?.ToString() ??
            "unknown";
        if (string.IsNullOrWhiteSpace(_applicationVersion) ||
            _applicationVersion.Length > 128)
        {
            throw new ArgumentException(
                "Application version is invalid.",
                nameof(applicationVersion));
        }

        if (_startupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        if (_maintenanceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maintenanceTimeout));
        }

        if (_statusPollInterval <= TimeSpan.Zero ||
            _statusPollInterval > _startupTimeout ||
            _statusPollInterval > _maintenanceTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(statusPollInterval));
        }
    }

    public async Task<CoreRuntimeUiStatus> EnsureAsync(
        CancellationToken cancellationToken)
    {
        StatusEvaluation current = await EvaluateStatusAsync(cancellationToken);
        if (current.ReturnImmediately)
        {
            return current.Status;
        }

        if (current.ShouldLaunch)
        {
            if (!File.Exists(_profile.CoreExecutablePath))
            {
                return Status(
                    CoreRuntimeUiState.MissingCore,
                    "后台组件缺失，请重新发布或安装 AgenTally。",
                    isError: true,
                    canRetry: false);
            }

            try
            {
                _processes.Start(
                    Path.GetFullPath(_profile.CoreExecutablePath),
                    Array.Empty<string>());
            }
            catch (Exception exception)
                when (exception is Win32Exception
                    or IOException
                    or InvalidOperationException
                    or UnauthorizedAccessException)
            {
                return Status(
                    CoreRuntimeUiState.LaunchFailed,
                    "无法启动后台组件，请检查安装完整性和文件权限后重试。",
                    isError: true,
                    canRetry: true);
            }
        }

        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _startupTimeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusEvaluation evaluation = await EvaluateStatusAsync(
                cancellationToken);
            if (evaluation.ReturnImmediately)
            {
                return evaluation.Status;
            }

            await Task.Delay(
                _statusPollInterval,
                _timeProvider,
                cancellationToken);
        }

        return Status(
            CoreRuntimeUiState.StartupTimedOut,
            "后台启动超时；请重试，若持续发生请完全退出后重新打开。",
            isError: true,
            canRetry: true);
    }

    public async Task<CoreRuntimeUiStatus> ReadStatusAsync(
        CancellationToken cancellationToken)
    {
        StatusEvaluation evaluation = await EvaluateStatusAsync(cancellationToken);
        if (evaluation.ShouldLaunch)
        {
            return Status(
                CoreRuntimeUiState.Failed,
                "后台采集未运行；请重试，若持续发生请完全退出后重新打开。",
                isError: true,
                canRetry: true);
        }

        return evaluation.Status;
    }

    public Task<CoreRuntimeUiStatus> RebuildCodexAsync(
        CancellationToken cancellationToken) =>
        RunMaintenanceAsync(
            CoreMaintenanceOperation.RescanCodex,
            backupPath: null,
            cancellationToken);

    public Task<CoreRuntimeUiStatus> ClearStatisticsAsync(
        CancellationToken cancellationToken) =>
        RunMaintenanceAsync(
            CoreMaintenanceOperation.ClearStatistics,
            backupPath: null,
            cancellationToken);

    public Task<CoreRuntimeUiStatus> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        return RunMaintenanceAsync(
            CoreMaintenanceOperation.CreateBackup,
            Path.GetFullPath(backupPath),
            cancellationToken);
    }

    public Task<CoreRuntimeUiStatus> RestoreBackupAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        return RunMaintenanceAsync(
            CoreMaintenanceOperation.RestoreBackup,
            Path.GetFullPath(backupPath),
            cancellationToken);
    }

    private async Task<CoreRuntimeUiStatus> RunMaintenanceAsync(
        CoreMaintenanceOperation operation,
        string? backupPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunMaintenanceCoreAsync(
                operation,
                backupPath,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecoverAfterCancelledMaintenanceAsync();
            throw;
        }
    }

    private async Task<CoreRuntimeUiStatus> RunMaintenanceCoreAsync(
        CoreMaintenanceOperation operation,
        string? backupPath,
        CancellationToken cancellationToken)
    {
        CoreRuntimeStatus? previous;
        try
        {
            previous = await _statusStore.ReadAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return Status(
                CoreRuntimeUiState.StatusInvalid,
                $"后台状态不兼容或无法验证；不能安全{operation.ActionName}。",
                isError: true,
                canRetry: true);
        }

        if (!File.Exists(_profile.CoreExecutablePath))
        {
            return Status(
                CoreRuntimeUiState.MissingCore,
                "后台组件缺失，请重新发布或安装 AgenTally。",
                isError: true,
                canRetry: false);
        }

        if (previous is not null)
        {
            CoreProcessInspection previousProcess =
                _processes.Inspect(previous.ProcessId);
            if (previousProcess.State == CoreProcessInspectionState.Inaccessible)
            {
                return Status(
                    CoreRuntimeUiState.StatusInvalid,
                    $"无法验证当前 Core 的身份；为避免并发写入，未开始{operation.ActionName}。",
                    isError: true,
                    canRetry: true);
            }

            if (Matches(previous, previousProcess))
            {
                if (!MatchesApplicationVersion(previous))
                {
                    return Status(
                        CoreRuntimeUiState.StatusInvalid,
                        $"后台组件版本与界面不匹配；为避免并发写入，未开始{operation.ActionName}。",
                        isError: true,
                        canRetry: true);
                }

                if (previous.Phase == CoreRuntimePhase.UpdatingStatistics)
                {
                    return Status(
                        CoreRuntimeUiState.UpdatingStatistics,
                        operation.AlreadyRunningMessage,
                        isError: false,
                        canRetry: false);
                }

                if (!CoreMaintenanceShutdownSignal.TryRequest(_profile))
                {
                    return Status(
                        CoreRuntimeUiState.Failed,
                        $"无法请求当前后台安全退出；未开始{operation.ActionName}。",
                        isError: true,
                        canRetry: true);
                }

                DateTimeOffset stopDeadline =
                    _timeProvider.GetUtcNow() + _startupTimeout;
                while (Matches(previous, previousProcess) &&
                       _timeProvider.GetUtcNow() < stopDeadline)
                {
                    await Task.Delay(
                        _statusPollInterval,
                        _timeProvider,
                        cancellationToken);
                    previousProcess = _processes.Inspect(previous.ProcessId);
                }

                if (previousProcess.State == CoreProcessInspectionState.Inaccessible)
                {
                    return Status(
                        CoreRuntimeUiState.StatusInvalid,
                        $"无法确认当前 Core 已安全退出；为避免并发写入，未开始{operation.ActionName}。",
                        isError: true,
                        canRetry: true);
                }

                if (Matches(previous, previousProcess))
                {
                    return Status(
                        CoreRuntimeUiState.StartupTimedOut,
                        $"当前 Core 尚未安全退出，无法开始{operation.ActionName}；请稍后重试。",
                        isError: true,
                        canRetry: true);
                }
            }
        }

        try
        {
            previous = await _statusStore.ReadAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return Status(
                CoreRuntimeUiState.StatusInvalid,
                $"无法建立{operation.ActionName}前的安全状态基线；未启动维护进程。",
                isError: true,
                canRetry: true);
        }

        try
        {
            if (operation.RequestOperation is { } requestOperation)
            {
                if (string.IsNullOrWhiteSpace(backupPath))
                {
                    throw new InvalidOperationException(
                        "A backup path is required for this maintenance operation.");
                }

                await _dataRequests.WriteAsync(
                    requestOperation,
                    backupPath,
                    cancellationToken);
            }

            _processes.Start(
                Path.GetFullPath(_profile.CoreExecutablePath),
                [operation.Argument]);
        }
        catch (Exception exception)
            when (exception is Win32Exception
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            _dataRequests.Delete();
            return Status(
                CoreRuntimeUiState.LaunchFailed,
                $"无法启动{operation.ActionName}，请检查安装完整性和文件权限后重试。",
                isError: true,
                canRetry: true);
        }

        DateTimeOffset maintenanceDeadline =
            _timeProvider.GetUtcNow() + _maintenanceTimeout;
        while (_timeProvider.GetUtcNow() < maintenanceDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CoreRuntimeStatus? current;
            try
            {
                current = await _statusStore.ReadAsync(cancellationToken);
            }
            catch (Exception exception)
                when (exception is InvalidDataException
                    or JsonException
                    or IOException
                    or UnauthorizedAccessException)
            {
                return Status(
                    CoreRuntimeUiState.StatusInvalid,
                    $"{operation.ActionName}期间无法验证后台状态；{operation.SafeFailureMessage}，请重试。",
                    isError: true,
                    canRetry: true);
            }

            bool isNewRun = current is not null &&
                (previous is null ||
                 current.ProcessId != previous.ProcessId ||
                 current.ProcessStartUtcTicks != previous.ProcessStartUtcTicks ||
                 current.ChangedAtUtc > previous.ChangedAtUtc);
            if (!isNewRun)
            {
                await Task.Delay(
                    _statusPollInterval,
                    _timeProvider,
                    cancellationToken);
                continue;
            }

            bool maintenanceSucceeded =
                current!.Phase == CoreRuntimePhase.Stopped &&
                current.ExitCode == 0;
            CoreRuntimeUiStatus? terminal = maintenanceSucceeded
                ? null
                : MapTerminalStatus(current, operation);
            if (maintenanceSucceeded || terminal is not null)
            {
                return await CompleteMaintenanceAsync(
                    current,
                    operation,
                    terminal,
                    cancellationToken);
            }

            await Task.Delay(
                _statusPollInterval,
                _timeProvider,
                cancellationToken);
        }

        return Status(
            CoreRuntimeUiState.StartupTimedOut,
            $"{operation.ActionName}超时；{operation.SafeFailureMessage}，请重试。",
            isError: true,
            canRetry: true);
    }

    private async Task RecoverAfterCancelledMaintenanceAsync()
    {
        _ = CoreMaintenanceShutdownSignal.TryRequest(_profile);
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _startupTimeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            CoreRuntimeStatus? status;
            try
            {
                status = await _statusStore.ReadAsync(CancellationToken.None);
            }
            catch
            {
                break;
            }

            if (status is null ||
                _processes.Inspect(status.ProcessId).State ==
                    CoreProcessInspectionState.Missing)
            {
                break;
            }

            await Task.Delay(
                _statusPollInterval,
                _timeProvider,
                CancellationToken.None);
        }

        _dataRequests.Delete();
        _ = await EnsureAsync(CancellationToken.None);
    }

    private async Task<CoreRuntimeUiStatus> CompleteMaintenanceAsync(
        CoreRuntimeStatus maintenanceStatus,
        CoreMaintenanceOperation operation,
        CoreRuntimeUiStatus? maintenanceFailure,
        CancellationToken cancellationToken)
    {
        CoreProcessInspection maintenanceProcess =
            _processes.Inspect(maintenanceStatus.ProcessId);
        DateTimeOffset exitDeadline =
            _timeProvider.GetUtcNow() + _startupTimeout;
        while (Matches(maintenanceStatus, maintenanceProcess) &&
               _timeProvider.GetUtcNow() < exitDeadline)
        {
            await Task.Delay(
                _statusPollInterval,
                _timeProvider,
                cancellationToken);
            maintenanceProcess = _processes.Inspect(
                maintenanceStatus.ProcessId);
        }

        if (maintenanceProcess.State == CoreProcessInspectionState.Inaccessible ||
            Matches(maintenanceStatus, maintenanceProcess))
        {
            return Status(
                CoreRuntimeUiState.StartupTimedOut,
                $"{operation.ActionName}已结束，但维护进程尚未安全退出；为避免并发写入，持续 Core 未恢复。",
                isError: true,
                canRetry: true);
        }

        CoreRuntimeUiStatus resumed = await EnsureAsync(cancellationToken);
        if (maintenanceFailure is null || resumed.IsError)
        {
            return maintenanceFailure is not null && resumed.IsError
                ? Status(
                    resumed.State,
                    $"{maintenanceFailure.Message} 持续 Core 也未能恢复：{resumed.Message}",
                    isError: true,
                    canRetry: resumed.CanRetry)
                : resumed;
        }

        return maintenanceFailure;
    }

    private bool Matches(
        CoreRuntimeStatus status,
        CoreProcessInspection inspection) =>
        inspection.State == CoreProcessInspectionState.Accessible &&
        inspection.ProcessStartUtcTicks == status.ProcessStartUtcTicks &&
        string.Equals(
            Path.GetFullPath(inspection.ExecutablePath!),
            Path.GetFullPath(_profile.CoreExecutablePath),
            StringComparison.OrdinalIgnoreCase);

    private static CoreRuntimeUiStatus? MapTerminalStatus(
        CoreRuntimeStatus status,
        CoreMaintenanceOperation operation) => status.Phase switch
        {
            CoreRuntimePhase.NeedsParserRebuild or
            CoreRuntimePhase.NeedsParserRescan => Status(
                CoreRuntimeUiState.ParserRebuildRequired,
                operation.ParserFailureMessage,
                isError: true,
                canRetry: true),
            CoreRuntimePhase.SourceUnavailable => Status(
                CoreRuntimeUiState.SourceUnavailable,
                operation.SourceUnavailableMessage,
                isError: true,
                canRetry: true),
            CoreRuntimePhase.DatabaseUnavailable => Status(
                CoreRuntimeUiState.DatabaseUnavailable,
                operation.DatabaseUnavailableMessage,
                isError: true,
                canRetry: true),
            CoreRuntimePhase.Failed => Status(
                CoreRuntimeUiState.Failed,
                operation.FailureMessage,
                isError: true,
                canRetry: true),
            CoreRuntimePhase.Stopped when status.ExitCode is not 0 => Status(
                CoreRuntimeUiState.Failed,
                operation.FailureMessage,
                isError: true,
                canRetry: true),
            _ => null
        };

    private async Task<StatusEvaluation> EvaluateStatusAsync(
        CancellationToken cancellationToken)
    {
        CoreRuntimeStatus? status;
        try
        {
            status = await _statusStore.ReadAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or JsonException
                or UnauthorizedAccessException)
        {
            return StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.StatusInvalid,
                "后台状态不兼容或无法验证；请使用匹配版本后重试。",
                isError: true,
                canRetry: true));
        }
        catch (FileNotFoundException)
        {
            status = null;
        }
        catch (DirectoryNotFoundException)
        {
            status = null;
        }
        catch (IOException)
        {
            return StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.StatusInvalid,
                "暂时无法读取后台状态；请稍后重试。",
                isError: true,
                canRetry: true));
        }

        if (status is null)
        {
            return StatusEvaluation.Launch(Status(
                CoreRuntimeUiState.Starting,
                "正在启动后台采集…",
                isError: false,
                canRetry: false));
        }

        CoreProcessInspection inspection = _processes.Inspect(status.ProcessId);
        if (inspection.State == CoreProcessInspectionState.Inaccessible)
        {
            return StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.StatusInvalid,
                "无法验证后台进程身份；为避免重复采集，AgenTally 未启动第二个 Core。",
                isError: true,
                canRetry: true));
        }

        bool matches = inspection.State == CoreProcessInspectionState.Accessible &&
            inspection.ProcessStartUtcTicks == status.ProcessStartUtcTicks &&
            string.Equals(
                Path.GetFullPath(inspection.ExecutablePath!),
                Path.GetFullPath(_profile.CoreExecutablePath),
                StringComparison.OrdinalIgnoreCase);
        bool applicationMatches = MatchesApplicationVersion(status);
        if (!matches)
        {
            if (inspection.State == CoreProcessInspectionState.Missing &&
                applicationMatches &&
                status.Phase is (
                    CoreRuntimePhase.NeedsParserRebuild or
                    CoreRuntimePhase.NeedsParserRescan))
            {
                return StatusEvaluation.Launch(Status(
                    CoreRuntimeUiState.ParserRebuildRequired,
                    "统计数据更新未完成；重试时会自动继续，Codex 原始日志不会被修改。",
                    isError: true,
                    canRetry: true));
            }

            return StatusEvaluation.Launch(Status(
                CoreRuntimeUiState.Starting,
                "正在启动后台采集…",
                isError: false,
                canRetry: false));
        }

        if (!applicationMatches)
        {
            return StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.StatusInvalid,
                "后台组件版本与界面不匹配；为避免重复采集，未启动第二个 Core。",
                isError: true,
                canRetry: true));
        }

        return status.Phase switch
        {
            CoreRuntimePhase.Running => StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.Ready,
                "后台采集正在运行。",
                isError: false,
                canRetry: false)),
            CoreRuntimePhase.UpdatingStatistics =>
                StatusEvaluation.Immediate(Status(
                    CoreRuntimeUiState.UpdatingStatistics,
                    "正在更新统计数据，期间仍可查看原有统计；完成后会自动恢复。",
                    isError: false,
                    canRetry: false)),
            CoreRuntimePhase.Starting => StatusEvaluation.Wait(Status(
                CoreRuntimeUiState.Starting,
                "正在启动后台采集…",
                isError: false,
                canRetry: false)),
            CoreRuntimePhase.Stopping or CoreRuntimePhase.Stopped =>
                StatusEvaluation.Immediate(Status(
                    CoreRuntimeUiState.Stopping,
                    "AgenTally 正在完全退出…",
                    isError: false,
                    canRetry: false)),
            CoreRuntimePhase.NeedsParserRebuild or
            CoreRuntimePhase.NeedsParserRescan => StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.ParserRebuildRequired,
                "统计数据更新未完成；Codex 原始日志不会被修改，请重试。",
                isError: true,
                canRetry: true)),
            CoreRuntimePhase.SourceUnavailable => StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.SourceUnavailable,
                "Codex 本地来源不可用；请确认来源目录可读后重试。",
                isError: true,
                canRetry: true)),
            CoreRuntimePhase.DatabaseUnavailable => StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.DatabaseUnavailable,
                "AgenTally 派生数据库不可用；请检查磁盘空间和文件权限。数据库不会被自动删除。",
                isError: true,
                canRetry: true)),
            _ => StatusEvaluation.Immediate(Status(
                CoreRuntimeUiState.Failed,
                "后台采集遇到故障；请重试，若持续发生请完全退出后重新打开。",
                isError: true,
                canRetry: true))
        };
    }

    private static CoreRuntimeUiStatus Status(
        CoreRuntimeUiState state,
        string message,
        bool isError,
        bool canRetry) => new(state, message, isError, canRetry);

    private bool MatchesApplicationVersion(CoreRuntimeStatus status) =>
        string.Equals(
            status.ApplicationVersion,
            _applicationVersion,
            StringComparison.Ordinal);

    private sealed record StatusEvaluation(
        CoreRuntimeUiStatus Status,
        bool ShouldLaunch,
        bool ReturnImmediately)
    {
        public static StatusEvaluation Immediate(CoreRuntimeUiStatus status) =>
            new(status, false, true);

        public static StatusEvaluation Launch(CoreRuntimeUiStatus status) =>
            new(status, true, false);

        public static StatusEvaluation Wait(CoreRuntimeUiStatus status) =>
            new(status, false, false);
    }

    private sealed record CoreMaintenanceOperation(
        string Argument,
        DataMaintenanceOperation? RequestOperation,
        string ActionName,
        string AlreadyRunningMessage,
        string SafeFailureMessage,
        string ParserFailureMessage,
        string SourceUnavailableMessage,
        string DatabaseUnavailableMessage,
        string FailureMessage)
    {
        public static CoreMaintenanceOperation RescanCodex { get; } = new(
            "--rescan-codex",
            null,
            "重新扫描统计数据",
            "正在更新统计数据，完成后会自动恢复。",
            "原数据库未被自动删除",
            "统计数据更新仍未完成；所有 Agent 原始日志保持不变，请重试。",
            "重新扫描时某个 Agent 本地来源不可用；请确认各来源目录可读后重试。",
            "重新扫描时本地统计数据库不可用；请检查磁盘空间和权限。原数据库未被自动删除。",
            "统计数据重新扫描失败；原数据库保持不变，请重试。");

        public static CoreMaintenanceOperation ClearStatistics { get; } = new(
            "--clear-statistics",
            null,
            "清除本地统计",
            "当前正在更新统计数据；本次未清除，请完成后重试。",
            "未清除任何统计",
            "统计清除未完成；未清除任何统计，请重试。",
            "清除统计时某个 Agent 本地来源不可用；未清除任何统计。请确认各来源目录可读后重试。",
            "清除统计时本地统计数据库不可用；未清除任何统计。请检查磁盘空间和权限。",
            "本地统计清除失败；未清除任何统计，请重试。");

        public static CoreMaintenanceOperation CreateBackup { get; } = new(
            "--create-backup",
            DataMaintenanceOperation.CreateBackup,
            "创建本地备份",
            "正在创建本地备份，请等待当前任务完成。",
            "源数据库未改变",
            "备份未完成；源数据库保持不变，请重试。",
            "备份时本地来源不可用；源数据库保持不变，请重试。",
            "备份时本地统计数据库不可用；源数据库保持不变，请检查磁盘空间和权限。",
            "本地备份失败；源数据库保持不变，请重试。");

        public static CoreMaintenanceOperation RestoreBackup { get; } = new(
            "--restore-backup",
            DataMaintenanceOperation.RestoreBackup,
            "恢复本地备份",
            "正在恢复本地备份，请等待当前任务完成。",
            "当前数据库未改变或已回滚",
            "恢复未完成；当前数据库未改变或已回滚，请重试。",
            "恢复时本地来源不可用；当前数据库未改变或已回滚，请重试。",
            "恢复时备份或数据库不可用；当前数据库未改变或已回滚，请检查文件和权限。",
            "本地备份恢复失败；当前数据库未改变或已回滚，请重试。");
    }
}
