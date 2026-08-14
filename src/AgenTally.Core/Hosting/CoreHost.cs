using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.ClaudeCode;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.GeminiCli;
using AgenTally.Core.Collectors.KimiCode;
using AgenTally.Core.Collectors.OpenCode;
using AgenTally.Core.Collectors.Qoder;
using AgenTally.Core.Collectors.QwenCode;
using AgenTally.Core.Collectors.WorkBuddy;
using AgenTally.Core.Collectors.Zcode;
using AgenTally.Core.Processing;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Storage.Backup;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Runtime;
using AgenTally.Storage.Writing;
using Microsoft.Data.Sqlite;
using System.Security;
using System.Reflection;
using System.Text.Json;

namespace AgenTally.Core.Hosting;

public sealed class CoreHost
{
    private readonly StorageOptions _defaultStorageOptions;
    private readonly AgenTallyRuntimeProfile? _runtimeProfile;
    private readonly TimeProvider _timeProvider;
    private readonly TextWriter _output;
    private readonly ICoreTrayFactory _trayFactory;

    public CoreHost(
        StorageOptions storageOptions,
        TimeProvider? timeProvider = null,
        TextWriter? output = null,
        AgenTallyRuntimeProfile? runtimeProfile = null)
        : this(
            storageOptions,
            timeProvider,
            output,
            runtimeProfile,
            SystemCoreTrayFactory.Instance)
    {
    }

    internal CoreHost(
        StorageOptions storageOptions,
        TimeProvider? timeProvider,
        TextWriter? output,
        AgenTallyRuntimeProfile? runtimeProfile,
        ICoreTrayFactory trayFactory)
    {
        _defaultStorageOptions = storageOptions ??
            throw new ArgumentNullException(nameof(storageOptions));
        ArgumentException.ThrowIfNullOrWhiteSpace(storageOptions.DatabasePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _output = output ?? Console.Out;
        _runtimeProfile = runtimeProfile;
        _trayFactory = trayFactory ??
            throw new ArgumentNullException(nameof(trayFactory));
    }

    public Task<int> RunAsync(string[] args) =>
        RunAsync(args, CancellationToken.None);

    public async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out CoreOptions options, out string? error))
        {
            await _output.WriteLineAsync($"参数错误：{error}");
            await _output.WriteLineAsync(
                "用法：AgenTally.Core [--check|--once|--rescan-codex|--clear-statistics|--create-backup|--restore-backup] [--codex-home <path>] [--claude-home <path>] [--claude-desktop-root <path>] [--kimi-home <path>] [--kimi-desktop-home <path>] [--qwen-home <path>] [--qoder-root <path>] [--qoder-cn-root <path>] [--qoder-cli-home <path>] [--zcode-home <path>] [--workbuddy-home <path>] [--gemini-home <path>] [--opencode-home <path>] [--database <path>]");
            return CoreExitCodes.InvalidArguments;
        }

        if (options.Check)
        {
            return await CheckAsync(options);
        }

        if (options.RestoreBackup)
        {
            return await RestoreBackupAsync(options, cancellationToken);
        }

        var sourceLeaseNames = new List<string>
        {
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.CodexHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.ClaudeHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.KimiHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.QwenHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.QoderCliHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.ZcodeHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.WorkBuddyHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.GeminiHome),
            AgenTallyRuntimeProfile.CreateSourceLeaseName(options.OpenCodeHome)
        };
        if (options.ClaudeDesktopRoot is not null)
        {
            sourceLeaseNames.Add(AgenTallyRuntimeProfile.CreateSourceLeaseName(
                options.ClaudeDesktopRoot));
        }
        if (options.KimiDesktopHome is not null)
        {
            sourceLeaseNames.Add(AgenTallyRuntimeProfile.CreateSourceLeaseName(
                options.KimiDesktopHome));
        }
        if (options.QoderRoot is not null)
        {
            sourceLeaseNames.Add(AgenTallyRuntimeProfile.CreateSourceLeaseName(
                options.QoderRoot));
        }
        if (options.QoderCnRoot is not null)
        {
            sourceLeaseNames.Add(AgenTallyRuntimeProfile.CreateSourceLeaseName(
                options.QoderCnRoot));
        }

        using CoreInstanceLease? lease = CoreInstanceLease.TryAcquire(
            sourceLeaseNames,
            AgenTallyRuntimeProfile.CreateDatabaseLeaseName(options.DatabasePath));
        if (lease is null)
        {
            await _output.WriteLineAsync(
                "另一个 AgenTally Core 正在使用相同来源或数据库；本进程未扫描来源且未打开数据库。");
            return CoreExitCodes.AlreadyRunning;
        }

        using CoreRuntimeSession? runtime = IsManagedProfile(options)
            ? new CoreRuntimeSession(_runtimeProfile!)
            : null;
        using ApplicationShutdownSignal? applicationShutdownSignal =
            runtime is null
            ? null
            : new ApplicationShutdownSignal(_runtimeProfile!);
        using CoreMaintenanceShutdownSignal? maintenanceShutdownSignal =
            runtime is null
            ? null
            : new CoreMaintenanceShutdownSignal(_runtimeProfile!);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var shutdownWatch = new CancellationTokenSource();
        Task shutdownTask = runtime is null
            ? Task.CompletedTask
            : WatchForCoreShutdownAsync(
                applicationShutdownSignal!,
                maintenanceShutdownSignal!,
                runtime!,
                shutdown,
                shutdownWatch.Token);
        try
        {
            if (runtime is not null)
            {
                await runtime.PublishAsync(
                    CoreRuntimePhase.Starting,
                    CoreRuntimeErrorCode.None,
                    "core_starting");
            }

            if (options.CreateBackup)
            {
                if (runtime is not null)
                {
                    await runtime.PublishAsync(
                        CoreRuntimePhase.UpdatingStatistics,
                        CoreRuntimeErrorCode.None,
                        "database_backup_running");
                }

                int backupResult = await CreateBackupAsync(
                    options,
                    shutdown.Token);
                await PublishStoppedOrFailedAsync(runtime, backupResult);
                return backupResult;
            }

            var storageOptions = new StorageOptions(options.DatabasePath);
            var connections = new SqliteConnectionFactory(storageOptions);
            using var writeGate = new CoreDatabaseWriteGate();
            var writer = new SerializedUsageWriter(
                new SqliteUsageWriter(connections),
                writeGate);
            using var collector = new CodexCollector(options.CodexHome, _timeProvider);
            var claudeCollector = new ClaudeCodeCollector(
                options.ClaudeHome,
                _timeProvider);
            var kimiCollector = new KimiCodeCollector(
                options.KimiHome,
                _timeProvider);
            var qwenCollector = new QwenCodeCollector(
                options.QwenHome,
                _timeProvider);
            var qoderCliCollector = new QoderCliCollector(
                options.QoderCliHome,
                _timeProvider);
            var zcodeCollector = new ZcodeCollector(
                options.ZcodeHome,
                _timeProvider);
            var workBuddyCollector = new WorkBuddyCollector(
                options.WorkBuddyHome,
                _timeProvider);
            var geminiCollector = new GeminiCliCollector(
                options.GeminiHome,
                _timeProvider);
            var openCodeCollector = new OpenCodeCollector(
                options.OpenCodeHome,
                _timeProvider);
            var collectors = new List<IAgentCollector>
            {
                collector,
                claudeCollector,
                kimiCollector,
                qwenCollector,
                qoderCliCollector,
                zcodeCollector,
                workBuddyCollector,
                geminiCollector,
                openCodeCollector
            };
            if (options.ClaudeDesktopRoot is not null)
            {
                collectors.Add(new ClaudeCodeDesktopCollector(
                    options.ClaudeDesktopRoot,
                    _timeProvider));
            }
            if (options.KimiDesktopHome is not null)
            {
                collectors.Add(new KimiCodeDesktopCollector(
                    options.KimiDesktopHome,
                    _timeProvider));
            }
            if (options.QoderRoot is not null)
            {
                collectors.Add(new QoderDesktopCollector(
                    options.QoderRoot,
                    QoderEdition.International,
                    _timeProvider));
            }
            if (options.QoderCnRoot is not null)
            {
                collectors.Add(new QoderDesktopCollector(
                    options.QoderCnRoot,
                    QoderEdition.China,
                    _timeProvider));
            }
            var context = new CollectorContext(
                Path.GetDirectoryName(options.CodexHome) ?? options.CodexHome,
                _timeProvider);

            if (options.RescanCodex)
            {
                if (runtime is not null)
                {
                    await runtime.PublishAsync(
                        CoreRuntimePhase.UpdatingStatistics,
                        CoreRuntimeErrorCode.None,
                        "statistics_update_running");
                }

                int rescanResult = await RescanStatisticsAsync(
                    collectors,
                    writer,
                    context,
                    storageOptions,
                    shutdown.Token);
                await PublishStoppedOrFailedAsync(runtime, rescanResult);
                return rescanResult;
            }

            if (options.ClearStatistics)
            {
                if (runtime is not null)
                {
                    await runtime.PublishAsync(
                        CoreRuntimePhase.UpdatingStatistics,
                        CoreRuntimeErrorCode.None,
                        "statistics_clear_running");
                }

                int clearResult = await ClearStatisticsAsync(
                    collectors,
                    writer,
                    context,
                    storageOptions,
                    shutdown.Token);
                await PublishStoppedOrFailedAsync(runtime, clearResult);
                return clearResult;
            }

            if (options.Once)
            {
                int onceResult = CoreExitCodes.Success;
                foreach (IAgentCollector currentCollector in collectors)
                {
                    onceResult = await RunOnceAsync(
                        currentCollector,
                        writer,
                        context,
                        shutdown.Token);
                    if (onceResult != CoreExitCodes.Success)
                    {
                        break;
                    }
                }
                await PublishStoppedOrFailedAsync(runtime, onceResult);
                return onceResult;
            }

            int continuousResult = runtime is null
                ? await RunContinuousWithAutomaticRescanAsync(
                    collectors,
                    writer,
                    context,
                    storageOptions,
                    runtime,
                    writeGate,
                    shutdown.Token)
                : await RunManagedContinuousAsync(
                    collectors,
                    writer,
                    context,
                    storageOptions,
                    runtime,
                    connections,
                    writeGate,
                    shutdown.Token);
            if (runtime is not null &&
                continuousResult == CoreExitCodes.Success)
            {
                await runtime.PublishAsync(
                    CoreRuntimePhase.Stopped,
                    CoreRuntimeErrorCode.None,
                    "core_stopped",
                    continuousResult);
            }

            return continuousResult;
        }
        catch (AgentParserRebuildRequiredException exception)
        {
            await _output.WriteLineAsync(
                $"检测到旧版 {exception.AgentId} 派生数据，请显式运行 --rescan-codex 安全重建全部来源统计。");
            if (runtime is not null)
            {
                await runtime.PublishAsync(
                    CoreRuntimePhase.NeedsParserRescan,
                    CoreRuntimeErrorCode.ParserRescanRequired,
                    "parser_rescan_required",
                    CoreExitCodes.ParserRebuildRequired);
            }

            return CoreExitCodes.ParserRebuildRequired;
        }
        catch (SourceProbeIncompleteException)
        {
            await _output.WriteLineAsync(
                "Agent 来源探测不完整，后台同步已停止且未继续写入。请检查本地来源目录后重试。");
            if (runtime is not null)
            {
                await runtime.PublishAsync(
                    CoreRuntimePhase.SourceUnavailable,
                    CoreRuntimeErrorCode.SourceUnavailable,
                    "source_unavailable",
                    CoreExitCodes.RuntimeFailure);
            }

            return CoreExitCodes.RuntimeFailure;
        }
        catch (Exception exception)
            when (exception is SqliteException
                or LegacyDevelopmentSchemaException
                or IOException
                or UnauthorizedAccessException)
        {
            await _output.WriteLineAsync(
                "AgenTally 派生数据库不可用；程序已停止写入且不会自动删除或重建数据库。");
            if (runtime is not null)
            {
                CoreRuntimeErrorCode code = exception is LegacyDevelopmentSchemaException
                    ? CoreRuntimeErrorCode.SchemaIncompatible
                    : CoreRuntimeErrorCode.DatabaseUnavailable;
                await runtime.PublishAsync(
                    CoreRuntimePhase.DatabaseUnavailable,
                    code,
                    code == CoreRuntimeErrorCode.SchemaIncompatible
                        ? "schema_incompatible"
                        : "database_unavailable",
                    CoreExitCodes.RuntimeFailure);
            }

            return CoreExitCodes.RuntimeFailure;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            if (runtime is not null)
            {
                await runtime.PublishAsync(
                    CoreRuntimePhase.Stopped,
                    CoreRuntimeErrorCode.None,
                    "core_stopped",
                    CoreExitCodes.Success);
            }

            return CoreExitCodes.Success;
        }
        catch (Exception)
        {
            await _output.WriteLineAsync(
                "AgenTally Core 遇到未预期故障，已停止以保护来源和派生数据库。");
            if (runtime is not null)
            {
                await runtime.PublishAsync(
                    CoreRuntimePhase.Failed,
                    CoreRuntimeErrorCode.UnexpectedFailure,
                    "core_failed",
                    CoreExitCodes.RuntimeFailure);
            }

            return CoreExitCodes.RuntimeFailure;
        }
        finally
        {
            shutdownWatch.Cancel();
            try
            {
                await shutdownTask;
            }
            catch (OperationCanceledException) when (shutdownWatch.IsCancellationRequested)
            {
            }
        }
    }

    private bool IsManagedProfile(CoreOptions options) =>
        _runtimeProfile is not null &&
        string.Equals(
            Path.GetFullPath(options.CodexHome),
            Path.GetFullPath(_runtimeProfile.CodexHome),
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetFullPath(options.DatabasePath),
            Path.GetFullPath(_runtimeProfile.DatabasePath),
            StringComparison.OrdinalIgnoreCase);

    private async Task<int> CreateBackupAsync(
        CoreOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsManagedProfile(options))
        {
            await _output.WriteLineAsync(
                "备份只能由匹配频道和 Profile 的 AgenTally Core 执行。");
            return CoreExitCodes.InvalidArguments;
        }

        var requests = new DataMaintenanceRequestStore(_runtimeProfile!);
        try
        {
            DataMaintenanceRequest request = await requests.ReadAsync(
                DataMaintenanceOperation.CreateBackup,
                cancellationToken);
            if (_runtimeProfile!.Channel == AgenTallyChannel.Development &&
                !_runtimeProfile.IsDevelopmentOwnedPath(request.BackupPath))
            {
                throw new InvalidDataException(
                    "Development backups must remain inside the Development root.");
            }
            var archive = new DatabaseBackupArchive();
            await archive.CreateAsync(
                options.DatabasePath,
                request.BackupPath,
                _runtimeProfile!.TempRoot,
                _runtimeProfile.Channel,
                ApplicationVersion(),
                _timeProvider.GetUtcNow(),
                cancellationToken);
            await _output.WriteLineAsync("AgenTally 本地备份已创建并完成完整性校验。");
            return CoreExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _output.WriteLineAsync("AgenTally 本地备份已取消，源数据库未改变。");
            return CoreExitCodes.RuntimeFailure;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                JsonException or
                InvalidOperationException or
                NotSupportedException or
                IOException or
                UnauthorizedAccessException or
                SqliteException)
        {
            await _output.WriteLineAsync(
                "AgenTally 本地备份失败；源数据库未改变，未报告成功。");
            return CoreExitCodes.RuntimeFailure;
        }
        finally
        {
            requests.Delete();
        }
    }

    private async Task<int> RestoreBackupAsync(
        CoreOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsManagedProfile(options))
        {
            await _output.WriteLineAsync(
                "恢复只能由匹配频道和 Profile 的 AgenTally Core 执行。");
            return CoreExitCodes.InvalidArguments;
        }

        using var runtime = new CoreRuntimeSession(_runtimeProfile!);
        using var applicationShutdownSignal =
            new ApplicationShutdownSignal(_runtimeProfile!);
        using var maintenanceShutdownSignal =
            new CoreMaintenanceShutdownSignal(_runtimeProfile!);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var shutdownWatch = new CancellationTokenSource();
        Task shutdownTask = WatchForCoreShutdownAsync(
            applicationShutdownSignal,
            maintenanceShutdownSignal,
            runtime,
            shutdown,
            shutdownWatch.Token);
        var requests = new DataMaintenanceRequestStore(_runtimeProfile!);
        try
        {
            await runtime.PublishAsync(
                CoreRuntimePhase.UpdatingStatistics,
                CoreRuntimeErrorCode.None,
                "database_restore_validating");
            DataMaintenanceRequest request = await requests.ReadAsync(
                DataMaintenanceOperation.RestoreBackup,
                shutdown.Token);
            var archive = new DatabaseBackupArchive();
            using StagedBackupRestore staged = await archive.StageRestoreAsync(
                request.BackupPath,
                options.DatabasePath,
                _runtimeProfile!.Channel,
                faultInjection: null,
                shutdown.Token);
            shutdown.Token.ThrowIfCancellationRequested();
            using CoreInstanceLease? lease = CoreInstanceLease.TryAcquireDatabase(
                AgenTallyRuntimeProfile.CreateDatabaseLeaseName(options.DatabasePath));
            if (lease is null)
            {
                await _output.WriteLineAsync(
                    "另一个 AgenTally 进程仍在使用当前数据库；未执行恢复替换。");
                await PublishStoppedOrFailedAsync(runtime, CoreExitCodes.AlreadyRunning);
                return CoreExitCodes.AlreadyRunning;
            }

            await runtime.PublishAsync(
                CoreRuntimePhase.UpdatingStatistics,
                CoreRuntimeErrorCode.None,
                "database_restore_switching");
            await archive.CommitRestoreAsync(
                staged,
                options.DatabasePath,
                faultInjection: null,
                shutdown.Token);
            await runtime.PublishAsync(
                CoreRuntimePhase.Stopped,
                CoreRuntimeErrorCode.None,
                "database_restore_complete",
                CoreExitCodes.Success);
            await _output.WriteLineAsync(
                "AgenTally 本地备份已恢复并通过数据库完整性校验。");
            return CoreExitCodes.Success;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            await PublishStoppedOrFailedAsync(runtime, CoreExitCodes.RuntimeFailure);
            await _output.WriteLineAsync(
                "AgenTally 恢复已取消；未完成的替换已保持原库或回滚。");
            return CoreExitCodes.RuntimeFailure;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                JsonException or
                InvalidOperationException or
                NotSupportedException or
                IOException or
                UnauthorizedAccessException or
                SqliteException)
        {
            await PublishStoppedOrFailedAsync(runtime, CoreExitCodes.RuntimeFailure);
            await _output.WriteLineAsync(
                "AgenTally 恢复失败；当前数据库未改变或已从内部回滚副本复原。");
            return CoreExitCodes.RuntimeFailure;
        }
        finally
        {
            requests.Delete();
            shutdownWatch.Cancel();
            try
            {
                await shutdownTask;
            }
            catch (OperationCanceledException) when (shutdownWatch.IsCancellationRequested)
            {
            }
        }
    }

    private static string ApplicationVersion() =>
        typeof(CoreHost).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .SingleOrDefault()?.InformationalVersion ??
        typeof(CoreHost).Assembly.GetName().Version?.ToString() ??
        "unknown";

    private static async Task WatchForCoreShutdownAsync(
        ApplicationShutdownSignal applicationSignal,
        CoreMaintenanceShutdownSignal maintenanceSignal,
        CoreRuntimeSession runtime,
        CancellationTokenSource shutdown,
        CancellationToken cancellationToken)
    {
        using var competingWaits =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task applicationShutdown =
            applicationSignal.WaitAsync(competingWaits.Token);
        Task maintenanceShutdown =
            maintenanceSignal.WaitAsync(competingWaits.Token);
        Task completed = await Task.WhenAny(
            applicationShutdown,
            maintenanceShutdown);
        await completed;
        competingWaits.Cancel();
        try
        {
            await Task.WhenAll(applicationShutdown, maintenanceShutdown);
        }
        catch (OperationCanceledException)
            when (competingWaits.IsCancellationRequested)
        {
        }

        await runtime.PublishAsync(
            CoreRuntimePhase.Stopping,
            CoreRuntimeErrorCode.None,
            "core_stopping");
        shutdown.Cancel();
    }

    private static Task PublishStoppedOrFailedAsync(
        CoreRuntimeSession? runtime,
        int exitCode)
    {
        if (runtime is null)
        {
            return Task.CompletedTask;
        }

        return exitCode == CoreExitCodes.Success
            ? runtime.PublishAsync(
                CoreRuntimePhase.Stopped,
                CoreRuntimeErrorCode.None,
                "core_stopped",
                exitCode)
            : runtime.PublishAsync(
                CoreRuntimePhase.Failed,
                CoreRuntimeErrorCode.UnexpectedFailure,
                "core_failed",
                exitCode);
    }

    private async Task<int> CheckAsync(CoreOptions options)
    {
        string sessions = Path.Combine(options.CodexHome, "sessions");
        string archived = Path.Combine(options.CodexHome, "archived_sessions");
        string claudeProjects = Path.Combine(options.ClaudeHome, "projects");
        string kimiSessions = Path.Combine(options.KimiHome, "sessions");
        string? kimiDesktopSessions = options.KimiDesktopHome is null
            ? null
            : Path.Combine(options.KimiDesktopHome, "sessions");
        string zcodeDatabase = Path.Combine(
            options.ZcodeHome,
            "cli",
            "db",
            ZcodeSourceIdentity.DatabaseFileName);
        string workBuddyProjects = Path.Combine(
            options.WorkBuddyHome,
            "projects");
        string geminiTemp = Path.Combine(options.GeminiHome, "tmp");
        await _output.WriteLineAsync("AgenTally.Core 配置检查通过。");
        await _output.WriteLineAsync($"数据库路径：{options.DatabasePath}");
        await _output.WriteLineAsync(
            $"Codex sessions：{(Directory.Exists(sessions) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"Codex archived_sessions：{(Directory.Exists(archived) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"Claude Code CLI projects：{(Directory.Exists(claudeProjects) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"Claude Desktop Code local-agent：{(options.ClaudeDesktopRoot is not null && Directory.Exists(options.ClaudeDesktopRoot) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"Kimi Code CLI sessions：{(Directory.Exists(kimiSessions) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"Kimi Work Desktop sessions：{(kimiDesktopSessions is not null && Directory.Exists(kimiDesktopSessions) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"ZCode usage database：{(File.Exists(zcodeDatabase) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"WorkBuddy projects：{(Directory.Exists(workBuddyProjects) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"Gemini CLI tmp：{(Directory.Exists(geminiTemp) ? "存在" : "不存在")}");
        await _output.WriteLineAsync(
            $"OpenCode data：{(Directory.Exists(options.OpenCodeHome) ? "存在" : "不存在")}");
        return 0;
    }

    private async Task<int> RunOnceAsync(
        IAgentCollector collector,
        IUsageWriter writer,
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        await writer.InitializeAsync(cancellationToken);
        SourceProbeResult probe = await collector.ProbeAsync(context, cancellationToken);
        if (probe.Diagnostics.Count > 0)
        {
            await _output.WriteLineAsync(
                $"{collector.AgentId} 来源探测不完整，本次同步未写入数据。请检查本地来源目录后重试。");
            return 1;
        }

        await EnsureCurrentParserStateAsync(
            collector,
            writer,
            probe.Instances,
            cancellationToken);
        int result = await ImportProbeAsync(
            collector,
            writer,
            probe,
            CollectionReason.ManualRequest,
            cancellationToken);
        if (result == CoreExitCodes.Success)
        {
            await SynchronizeSessionNamesAsync(
                collector,
                writer,
                probe.Instances,
                cancellationToken);
        }

        return result;
    }

    private async Task<int> ImportProbeAsync(
        IAgentCollector collector,
        IUsageWriter writer,
        SourceProbeResult probe,
        CollectionReason reason,
        CancellationToken cancellationToken,
        bool failOnCompatibilityDiagnostics = false)
    {
        var coordinator = new ImportCoordinator(writer, _timeProvider);
        int applied = 0;
        int ignored = 0;
        int failures = 0;
        int compatibilityFailures = 0;

        foreach (SourceEntityDescriptor entity in probe.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceInstanceDescriptor instance = probe.Instances.First(value =>
                string.Equals(
                    value.SourceInstanceId,
                    entity.SourceInstanceId,
                    StringComparison.Ordinal));
            EntitySyncSummary result = await SyncEntityToEndAsync(
                collector,
                writer,
                coordinator,
                instance,
                entity,
                reason,
                cancellationToken);
            applied += result.AppliedCount;
            ignored += result.IgnoredCount;
            failures += result.FailureCount;
            compatibilityFailures += result.CompatibilityFailureCount;
        }

        await _output.WriteLineAsync($"已发现来源实体：{probe.Entities.Count}");
        await _output.WriteLineAsync($"已应用事件：{applied}");
        await _output.WriteLineAsync($"已忽略重复事件：{ignored}");
        if (failures > 0)
        {
            await _output.WriteLineAsync($"同步失败来源：{failures}");
            return 1;
        }

        if (failOnCompatibilityDiagnostics && compatibilityFailures > 0)
        {
            await _output.WriteLineAsync(
                "staging 中存在无法安全规范化的记录，维护操作未修改主数据库。");
            return 1;
        }

        return 0;
    }

    private async Task<EntitySyncSummary> SyncEntityToEndAsync(
        IAgentCollector collector,
        IUsageWriter writer,
        ImportCoordinator coordinator,
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        CollectionReason reason,
        CancellationToken cancellationToken)
    {
        StoredCursor? cursor = await writer.GetCursorAsync(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            cancellationToken);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        if (cursor is not null)
        {
            seenCursors.Add(cursor.CursorJson);
        }

        int applied = 0;
        int ignored = 0;
        int compatibilityFailures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyncResult result = await coordinator.SyncAsync(
                collector,
                new CollectionRequest(instance, entity, cursor, reason),
                cancellationToken);
            applied += result.AppliedCount;
            ignored += result.IgnoredCount;
            compatibilityFailures += result.Diagnostics.Count(
                IsMaintenanceBlockingDiagnostic);
            if (!result.Succeeded)
            {
                return new EntitySyncSummary(
                    applied,
                    ignored,
                    1,
                    compatibilityFailures);
            }

            bool batchLimitReached = result.Diagnostics.Any(static value =>
                string.Equals(
                    value.Code,
                    "collector.batch_limit_reached",
                    StringComparison.Ordinal));
            if (!batchLimitReached)
            {
                return new EntitySyncSummary(
                    applied,
                    ignored,
                    0,
                    compatibilityFailures);
            }

            StoredCursor? nextCursor = await writer.GetCursorAsync(
                instance.SourceInstanceId,
                entity.SourceEntityId,
                cancellationToken);
            if (nextCursor is null || !seenCursors.Add(nextCursor.CursorJson))
            {
                await _output.WriteLineAsync(
                    "来源达到单批上限后游标没有前进，已停止以避免不完整或无限重建。");
                return new EntitySyncSummary(
                    applied,
                    ignored,
                    1,
                    compatibilityFailures);
            }

            cursor = nextCursor;
        }
    }

    private async Task<int> RescanAgentStatisticsAsync(
        string agentId,
        IReadOnlyList<IAgentCollector> collectors,
        IUsageWriter writer,
        CollectorContext context,
        StorageOptions storageOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        IAgentCollector[] matchingCollectors = collectors
            .Where(value => string.Equals(
                value.AgentId,
                agentId,
                StringComparison.Ordinal))
            .ToArray();
        if (matchingCollectors.Length == 0 ||
            matchingCollectors.Any(static value =>
                value is not IParserVersionedCollector))
        {
            await _output.WriteLineAsync(
                $"{agentId} 没有完整的带版本 Collector，未修改主数据库。");
            return 1;
        }

        await writer.InitializeAsync(cancellationToken);
        var probes = new List<(IAgentCollector Collector, SourceProbeResult Probe)>();
        var maintenance = new List<SourceInstanceMaintenanceState>();
        foreach (IAgentCollector collector in matchingCollectors)
        {
            var versionedCollector = (IParserVersionedCollector)collector;
            SourceProbeResult probe = await collector.ProbeAsync(
                context,
                cancellationToken);
            if (probe.Diagnostics.Count > 0)
            {
                await _output.WriteLineAsync(
                    $"{agentId} 来源探测不完整，未修改主数据库。");
                return 1;
            }

            probes.Add((collector, probe));
            maintenance.AddRange(probe.Instances
                .Where(value => string.Equals(
                    value.AgentId,
                    agentId,
                    StringComparison.Ordinal))
                .Select(instance => new SourceInstanceMaintenanceState(
                    instance,
                    versionedCollector.ParserVersion,
                    versionedCollector.MaintenanceCompatibilityLevel,
                    versionedCollector.MaintenanceCompatibilityCode)));
        }

        if (maintenance.Count == 0)
        {
            await _output.WriteLineAsync(
                $"{agentId} 没有可安全重建的来源实例，未修改主数据库。");
            return 1;
        }

        string stagingDatabasePath = CreateStagingDatabasePath(
            storageOptions.DatabasePath);
        try
        {
            var stagingConnections = new SqliteConnectionFactory(
                new StorageOptions(stagingDatabasePath));
            var stagingWriter = new SqliteUsageWriter(stagingConnections);
            await stagingWriter.InitializeAsync(cancellationToken);
            var primaryPriceLedger = new SqlitePriceLedger(
                new SqliteConnectionFactory(storageOptions));
            var stagingPriceLedger = new SqlitePriceLedger(stagingConnections);
            await stagingPriceLedger.ReplaceCustomPricesAsync(
                await primaryPriceLedger.GetCustomPricesAsync(cancellationToken),
                cancellationToken);
            foreach ((IAgentCollector collector, SourceProbeResult probe) in probes)
            {
                int result = await ImportProbeAsync(
                    collector,
                    stagingWriter,
                    probe,
                    CollectionReason.RepairScan,
                    cancellationToken,
                    failOnCompatibilityDiagnostics: true);
                if (result != CoreExitCodes.Success)
                {
                    await _output.WriteLineAsync(
                        $"{agentId} 重扫 staging 未完成；主数据库保持不变，Agent 原始日志未修改。");
                    return result;
                }
            }

            await writer.MergeSourceInstancesFromStagingAsync(
                maintenance,
                stagingDatabasePath,
                cancellationToken);
            foreach ((IAgentCollector collector, SourceProbeResult probe) in probes)
            {
                await SynchronizeSessionNamesAsync(
                    collector,
                    writer,
                    probe.Instances,
                    cancellationToken);
            }
            await _output.WriteLineAsync(
                $"已从 staging 原子合并 {agentId} 统计；未重扫其他 Agent，所有 Agent 原始日志均未修改。");
            return CoreExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync(
                $"{agentId} 统计重扫失败（{exception.GetType().Name}）；主数据库保持不变，Agent 原始日志未修改。");
            return 1;
        }
        finally
        {
            DeleteStagingDatabaseFiles(stagingDatabasePath);
        }
    }

    private async Task<int> RescanStatisticsAsync(
        IReadOnlyList<IAgentCollector> collectors,
        IUsageWriter writer,
        CollectorContext context,
        StorageOptions storageOptions,
        CancellationToken cancellationToken)
    {
        await writer.InitializeAsync(cancellationToken);
        var probes = new List<(IAgentCollector Collector, SourceProbeResult Probe)>();
        var maintenance = new List<SourceInstanceMaintenanceState>();
        foreach (IAgentCollector collector in collectors)
        {
            if (collector is not IParserVersionedCollector versionedCollector)
            {
                await _output.WriteLineAsync(
                    $"{collector.AgentId} 未声明 Parser 版本，已在建立临时派生库前停止重扫。主数据库保持不变。");
                return 1;
            }

            SourceProbeResult probe = await collector.ProbeAsync(
                context,
                cancellationToken);
            if (probe.Diagnostics.Count > 0)
            {
                await _output.WriteLineAsync(
                    $"{collector.AgentId} 来源探测不完整，已在建立临时派生库前停止重扫。主数据库保持不变。");
                return 1;
            }

            probes.Add((collector, probe));
            maintenance.AddRange(probe.Instances
                .Where(value => string.Equals(
                    value.AgentId,
                    collector.AgentId,
                    StringComparison.Ordinal))
                .Select(instance => new SourceInstanceMaintenanceState(
                    instance,
                    versionedCollector.ParserVersion,
                    versionedCollector.MaintenanceCompatibilityLevel,
                    versionedCollector.MaintenanceCompatibilityCode)));
        }

        string stagingDatabasePath = CreateStagingDatabasePath(storageOptions.DatabasePath);
        try
        {
            var stagingConnections = new SqliteConnectionFactory(
                new StorageOptions(stagingDatabasePath));
            var stagingWriter = new SqliteUsageWriter(stagingConnections);
            await stagingWriter.InitializeAsync(cancellationToken);
            var primaryPriceLedger = new SqlitePriceLedger(
                new SqliteConnectionFactory(storageOptions));
            var stagingPriceLedger = new SqlitePriceLedger(stagingConnections);
            await stagingPriceLedger.ReplaceCustomPricesAsync(
                await primaryPriceLedger.GetCustomPricesAsync(cancellationToken),
                cancellationToken);
            foreach ((IAgentCollector collector, SourceProbeResult probe) in probes)
            {
                int result = await ImportProbeAsync(
                    collector,
                    stagingWriter,
                    probe,
                    CollectionReason.RepairScan,
                    cancellationToken,
                    failOnCompatibilityDiagnostics: true);
                if (result != 0)
                {
                    await _output.WriteLineAsync(
                        $"{collector.AgentId} 重扫 staging 未完成；主数据库保持不变，Agent 原始日志未修改。");
                    return result;
                }
            }

            await writer.MergeSourceInstancesFromStagingAsync(
                maintenance,
                stagingDatabasePath,
                cancellationToken);
            foreach ((IAgentCollector collector, SourceProbeResult probe) in probes)
            {
                await SynchronizeSessionNamesAsync(
                    collector,
                    writer,
                    probe.Instances,
                    cancellationToken);
            }

            await _output.WriteLineAsync(
                "已从 staging 原子合并全部 Agent 统计；数据库独有历史和既有费率绑定已保留；" +
                "Codex 原始文件未被修改，其他 Agent 原始文件也未被修改。");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync(
                $"统计重扫失败（{exception.GetType().Name}）；主数据库保持不变，Agent 原始日志未修改。");
            return 1;
        }
        finally
        {
            DeleteStagingDatabaseFiles(stagingDatabasePath);
        }
    }

    private async Task<int> ClearStatisticsAsync(
        IReadOnlyList<IAgentCollector> collectors,
        IUsageWriter writer,
        CollectorContext context,
        StorageOptions storageOptions,
        CancellationToken cancellationToken)
    {
        await writer.InitializeAsync(cancellationToken);
        var probes = new List<(IAgentCollector Collector, SourceProbeResult Probe)>();
        var maintenance = new List<SourceInstanceMaintenanceState>();
        foreach (IAgentCollector collector in collectors)
        {
            if (collector is not IParserVersionedCollector versionedCollector)
            {
                await _output.WriteLineAsync(
                    $"{collector.AgentId} 未声明 Parser 版本，未清除任何统计。");
                return 1;
            }

            SourceProbeResult probe = await collector.ProbeAsync(
                context,
                cancellationToken);
            if (probe.Diagnostics.Count > 0)
            {
                await _output.WriteLineAsync(
                    $"{collector.AgentId} 来源探测不完整，未清除任何统计。");
                return 1;
            }

            probes.Add((collector, probe));
            maintenance.AddRange(probe.Instances
                .Where(value => string.Equals(
                    value.AgentId,
                    collector.AgentId,
                    StringComparison.Ordinal))
                .Select(instance => new SourceInstanceMaintenanceState(
                    instance,
                    versionedCollector.ParserVersion,
                    versionedCollector.MaintenanceCompatibilityLevel,
                    versionedCollector.MaintenanceCompatibilityCode)));
        }

        string stagingDatabasePath = CreateStagingDatabasePath(
            storageOptions.DatabasePath);
        try
        {
            var stagingWriter = new SqliteUsageWriter(
                new SqliteConnectionFactory(new StorageOptions(stagingDatabasePath)));
            await stagingWriter.InitializeAsync(cancellationToken);
            foreach ((IAgentCollector collector, SourceProbeResult probe) in probes)
            {
                int result = await ImportProbeAsync(
                    collector,
                    stagingWriter,
                    probe,
                    CollectionReason.RepairScan,
                    cancellationToken,
                    failOnCompatibilityDiagnostics: true);
                if (result != 0)
                {
                    await _output.WriteLineAsync(
                        $"{collector.AgentId} EOF 基线 staging 未完成，未清除任何统计。");
                    return result;
                }
            }

            await writer.ClearAllStatisticsFromStagingAsync(
                maintenance,
                stagingDatabasePath,
                cancellationToken);
            await _output.WriteLineAsync(
                "已清除全部 Agent 本地统计并原子换入当前 EOF 基线；之后只累计新增记录。");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync(
                $"全部统计清除失败（{exception.GetType().Name}）；主数据库保持不变。");
            return 1;
        }
        finally
        {
            DeleteStagingDatabaseFiles(stagingDatabasePath);
        }
    }

    private static async Task<bool> HasContinuousHistoricalSourcesAsync(
        CodexCollector collector,
        IUsageWriter writer,
        IReadOnlyList<SourceInstanceDescriptor> instances,
        IReadOnlyList<SourceEntityDescriptor> entities,
        IReadOnlyList<StoredUsageSourceEntity> storedUsageSources,
        CancellationToken cancellationToken)
    {
        foreach (StoredUsageSourceEntity storedSource in storedUsageSources)
        {
            SourceInstanceDescriptor? instance = instances.FirstOrDefault(value =>
                string.Equals(
                    value.SourceInstanceId,
                    storedSource.SourceInstanceId,
                    StringComparison.Ordinal));
            SourceEntityDescriptor? entity = entities.FirstOrDefault(value =>
                string.Equals(
                    value.SourceInstanceId,
                    storedSource.SourceInstanceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    value.SourceEntityId,
                    storedSource.SourceEntityId,
                    StringComparison.Ordinal));
            StoredCursor? cursor = await writer.GetCursorAsync(
                storedSource.SourceInstanceId,
                storedSource.SourceEntityId,
                cancellationToken);
            if (instance is null ||
                entity is null ||
                !await collector.HasContinuousSourceAsync(
                    instance,
                    entity,
                    cursor,
                    cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task EnsureCurrentParserStateAsync(
        IAgentCollector collector,
        IUsageWriter writer,
        IReadOnlyList<SourceInstanceDescriptor> instances,
        CancellationToken cancellationToken)
    {
        if (collector is not IParserVersionedCollector parserVersionedCollector)
        {
            return;
        }

        foreach (SourceInstanceDescriptor instance in instances.Where(value =>
                     string.Equals(
                         value.AgentId,
                         collector.AgentId,
                         StringComparison.Ordinal)))
        {
            SourceInstanceParserState state =
                await writer.GetSourceInstanceParserStateAsync(
                    instance,
                    parserVersionedCollector.ParserVersion,
                    cancellationToken);
            if (state.RequiresRescan)
            {
                await writer.SetSourceCompatibilityAsync(
                    instance,
                    CompatibilityLevel.PartiallyCompatible,
                    "parser_rescan_required",
                    requiresRescan: true,
                    cancellationToken);
                throw new AgentParserRebuildRequiredException(
                    collector.AgentId,
                    "stored-derived-data",
                    parserVersionedCollector.ParserVersion);
            }
        }
    }

    private static async Task SynchronizeSessionNamesAsync(
        IAgentCollector collector,
        IUsageWriter writer,
        IReadOnlyList<SourceInstanceDescriptor> instances,
        CancellationToken cancellationToken)
    {
        if (collector is not IUsageSessionNameSource sessionNameSource)
        {
            return;
        }

        IReadOnlyList<UsageSessionNameMetadata> sessionNames =
            await sessionNameSource.ReadSessionNamesAsync(cancellationToken);
        foreach (SourceInstanceDescriptor instance in instances.Where(value =>
                     string.Equals(
                         value.AgentId,
                         collector.AgentId,
                         StringComparison.Ordinal)))
        {
            await writer.SynchronizeSessionNamesAsync(
                instance,
                sessionNames,
                cancellationToken);
        }
    }

    private async Task<int> RunContinuousAsync(
        IReadOnlyList<IAgentCollector> collectors,
        IUsageWriter writer,
        CollectorContext context,
        CoreRuntimeSession? runtime,
        CancellationToken cancellationToken)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var schedulers = new List<CollectionScheduler>(collectors.Count);
        try
        {
            foreach (IAgentCollector collector in collectors)
            {
                var scheduler = new CollectionScheduler(
                    collector,
                    writer,
                    context,
                    timeProvider: _timeProvider);
                schedulers.Add(scheduler);
                await scheduler.StartAsync(shutdown.Token);
            }

            if (runtime is not null)
            {
                await runtime.PublishAsync(
                    CoreRuntimePhase.Running,
                    CoreRuntimeErrorCode.None,
                    "core_running");
            }
            await _output.WriteLineAsync("AgenTally.Core 已启动。按 Ctrl+C 退出。");
            await _output.WriteLineAsync(
                "后台仅监视本地 Codex、Claude Code CLI、Claude Desktop Code local-Agent、" +
                "Kimi Code CLI、Kimi Work Desktop、WorkBuddy JSONL、ZCode SQLite、" +
                "Qwen Code CLI、Qoder/Qoder CN Desktop、Qoder CLI、Gemini CLI 与 OpenCode，" +
                "不联网、不修改 Agent 配置。");

            Task cancelled = Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            Task[] completions = [cancelled, .. schedulers.Select(value => value.Completion)];
            Task completed = await Task.WhenAny(completions);
            if (completed != cancelled)
            {
                await completed;
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal Ctrl+C or caller cancellation.
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            for (int index = schedulers.Count - 1; index >= 0; index--)
            {
                await schedulers[index].DisposeAsync();
            }
        }

        return 0;
    }

    private async Task<int> RunManagedContinuousAsync(
        IReadOnlyList<IAgentCollector> collectors,
        IUsageWriter writer,
        CollectorContext context,
        StorageOptions storageOptions,
        CoreRuntimeSession runtime,
        SqliteConnectionFactory connections,
        CoreDatabaseWriteGate writeGate,
        CancellationToken cancellationToken)
    {
        using var serviceCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using ICoreTraySession tray = _trayFactory.Start(_runtimeProfile!);
        var handler = new PriceCommandHandler(
            new SqlitePriceLedger(connections),
            writeGate);
        var server = new PriceCommandServer(
            _runtimeProfile!.PriceCommandPipeName,
            handler);
        Task serverTask = server.RunAsync(serviceCancellation.Token);
        Task<int> coreTask = RunContinuousWithAutomaticRescanAsync(
            collectors,
            writer,
            context,
            storageOptions,
            runtime,
            writeGate,
            serviceCancellation.Token);
        Task trayTask = tray.Completion;
        Exception? serverFailure = null;
        try
        {
            Task completed = await Task.WhenAny(
                coreTask,
                serverTask,
                trayTask);
            if (completed == trayTask)
            {
                Exception trayFailure;
                try
                {
                    await trayTask;
                    trayFailure = new InvalidOperationException(
                        "Tray message loop stopped unexpectedly.");
                }
                catch (Exception exception)
                {
                    trayFailure = exception;
                }

                serviceCancellation.Cancel();
                try
                {
                    await coreTask;
                }
                catch (OperationCanceledException)
                    when (serviceCancellation.IsCancellationRequested)
                {
                }

                throw new InvalidOperationException(
                    "Tray message loop stopped unexpectedly.",
                    trayFailure);
            }

            if (completed == serverTask)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return await coreTask;
                }

                try
                {
                    await serverTask;
                    serverFailure = new InvalidOperationException(
                        "Pricing command server stopped unexpectedly.");
                }
                catch (Exception exception)
                {
                    serverFailure = exception;
                }

                serviceCancellation.Cancel();
                await coreTask;
                throw new InvalidOperationException(
                    "Pricing command server stopped unexpectedly.",
                    serverFailure);
            }

            return await coreTask;
        }
        finally
        {
            serviceCancellation.Cancel();
            if (serverFailure is null)
            {
                try
                {
                    await serverTask;
                }
                catch (OperationCanceledException)
                    when (serviceCancellation.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task<int> RunContinuousWithAutomaticRescanAsync(
        IReadOnlyList<IAgentCollector> collectors,
        IUsageWriter writer,
        CollectorContext context,
        StorageOptions storageOptions,
        CoreRuntimeSession? runtime,
        CoreDatabaseWriteGate writeGate,
        CancellationToken cancellationToken)
    {
        var rescannedAgents = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            try
            {
                return await RunContinuousAsync(
                    collectors,
                    writer,
                    context,
                    runtime,
                    cancellationToken);
            }
            catch (AgentParserRebuildRequiredException exception)
                when (runtime is not null)
            {
                if (!rescannedAgents.Add(exception.AgentId))
                {
                    await _output.WriteLineAsync(
                        $"{exception.AgentId} 统计解析规则在自动更新后仍不兼容；主数据库保持不变，请稍后重试。");
                    await runtime.PublishAsync(
                        CoreRuntimePhase.NeedsParserRescan,
                        CoreRuntimeErrorCode.ParserRescanRequired,
                        "statistics_update_incomplete",
                        CoreExitCodes.ParserRescanRequired);
                    return CoreExitCodes.ParserRescanRequired;
                }

                await _output.WriteLineAsync(
                    $"检测到 {exception.AgentId} 统计解析规则已更新，正在安全更新本地统计数据。");
                await runtime.PublishAsync(
                    CoreRuntimePhase.UpdatingStatistics,
                    CoreRuntimeErrorCode.None,
                    "statistics_update_running");

                int rescanResult;
                using (writeGate.BlockPricing())
                {
                    rescanResult = await RescanAgentStatisticsAsync(
                        exception.AgentId,
                        collectors,
                        writer,
                        context,
                        storageOptions,
                        cancellationToken);
                }
                if (rescanResult != CoreExitCodes.Success)
                {
                    await runtime.PublishAsync(
                        CoreRuntimePhase.NeedsParserRescan,
                        CoreRuntimeErrorCode.ParserRescanRequired,
                        "statistics_update_incomplete",
                        CoreExitCodes.ParserRescanRequired);
                    return CoreExitCodes.ParserRescanRequired;
                }

                await _output.WriteLineAsync(
                    $"{exception.AgentId} 本地统计数据已更新，正在恢复增量采集。");
            }
        }
    }

    private bool TryParseOptions(
        string[] args,
        out CoreOptions options,
        out string? error)
    {
        options = null!;
        error = null;
        if (args is null)
        {
            error = "参数集合不能为空。";
            return false;
        }

        bool check = false;
        bool once = false;
        bool rescanCodex = false;
        bool clearStatistics = false;
        bool createBackup = false;
        bool restoreBackup = false;
        string? codexHome = null;
        string? claudeHome = null;
        string? claudeDesktopRoot = null;
        string? kimiHome = null;
        string? kimiDesktopHome = null;
        string? qwenHome = null;
        string? qoderRoot = null;
        string? qoderCnRoot = null;
        string? qoderCliHome = null;
        string? zcodeHome = null;
        string? workBuddyHome = null;
        string? geminiHome = null;
        string? openCodeHome = null;
        string? databasePath = null;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, "--check", StringComparison.OrdinalIgnoreCase))
            {
                if (check)
                {
                    error = "--check 不能重复。";
                    return false;
                }

                check = true;
                continue;
            }

            if (string.Equals(argument, "--once", StringComparison.OrdinalIgnoreCase))
            {
                if (once)
                {
                    error = "--once 不能重复。";
                    return false;
                }

                once = true;
                continue;
            }

            if (string.Equals(
                    argument,
                    "--rescan-codex",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    argument,
                    "--rebuild-codex",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (rescanCodex)
                {
                    error = "--rescan-codex 不能重复。";
                    return false;
                }

                rescanCodex = true;
                continue;
            }

            if (string.Equals(
                    argument,
                    "--clear-statistics",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (clearStatistics)
                {
                    error = "--clear-statistics 不能重复。";
                    return false;
                }

                clearStatistics = true;
                continue;
            }

            if (string.Equals(argument, "--codex-home", StringComparison.OrdinalIgnoreCase))
            {
                if (codexHome is not null || !TryReadValue(args, ref index, out codexHome))
                {
                    error = "--codex-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--claude-home", StringComparison.OrdinalIgnoreCase))
            {
                if (claudeHome is not null || !TryReadValue(args, ref index, out claudeHome))
                {
                    error = "--claude-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(
                    argument,
                    "--claude-desktop-root",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (claudeDesktopRoot is not null ||
                    !TryReadValue(args, ref index, out claudeDesktopRoot))
                {
                    error = "--claude-desktop-root 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--kimi-home", StringComparison.OrdinalIgnoreCase))
            {
                if (kimiHome is not null || !TryReadValue(args, ref index, out kimiHome))
                {
                    error = "--kimi-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(
                    argument,
                    "--create-backup",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (createBackup)
                {
                    error = "--create-backup 不能重复。";
                    return false;
                }

                createBackup = true;
                continue;
            }

            if (string.Equals(
                    argument,
                    "--restore-backup",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (restoreBackup)
                {
                    error = "--restore-backup 不能重复。";
                    return false;
                }

                restoreBackup = true;
                continue;
            }

            if (string.Equals(
                    argument,
                    "--kimi-desktop-home",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (kimiDesktopHome is not null ||
                    !TryReadValue(args, ref index, out kimiDesktopHome))
                {
                    error = "--kimi-desktop-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--qwen-home", StringComparison.OrdinalIgnoreCase))
            {
                if (qwenHome is not null || !TryReadValue(args, ref index, out qwenHome))
                {
                    error = "--qwen-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--qoder-root", StringComparison.OrdinalIgnoreCase))
            {
                if (qoderRoot is not null || !TryReadValue(args, ref index, out qoderRoot))
                {
                    error = "--qoder-root 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--qoder-cn-root", StringComparison.OrdinalIgnoreCase))
            {
                if (qoderCnRoot is not null || !TryReadValue(args, ref index, out qoderCnRoot))
                {
                    error = "--qoder-cn-root 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--qoder-cli-home", StringComparison.OrdinalIgnoreCase))
            {
                if (qoderCliHome is not null ||
                    !TryReadValue(args, ref index, out qoderCliHome))
                {
                    error = "--qoder-cli-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--zcode-home", StringComparison.OrdinalIgnoreCase))
            {
                if (zcodeHome is not null || !TryReadValue(args, ref index, out zcodeHome))
                {
                    error = "--zcode-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(
                    argument,
                    "--workbuddy-home",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (workBuddyHome is not null ||
                    !TryReadValue(args, ref index, out workBuddyHome))
                {
                    error = "--workbuddy-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--gemini-home", StringComparison.OrdinalIgnoreCase))
            {
                if (geminiHome is not null || !TryReadValue(args, ref index, out geminiHome))
                {
                    error = "--gemini-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--opencode-home", StringComparison.OrdinalIgnoreCase))
            {
                if (openCodeHome is not null ||
                    !TryReadValue(args, ref index, out openCodeHome))
                {
                    error = "--opencode-home 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            if (string.Equals(argument, "--database", StringComparison.OrdinalIgnoreCase))
            {
                if (databasePath is not null || !TryReadValue(args, ref index, out databasePath))
                {
                    error = "--database 需要一个且只能提供一个路径。";
                    return false;
                }

                continue;
            }

            error = $"未知参数 {argument}。";
            return false;
        }

        if ((check ? 1 : 0) +
            (once ? 1 : 0) +
            (rescanCodex ? 1 : 0) +
            (clearStatistics ? 1 : 0) +
            (createBackup ? 1 : 0) +
            (restoreBackup ? 1 : 0) > 1)
        {
            error = "Core 操作参数不能同时使用。";
            return false;
        }

        try
        {
            string selectedHome;
            if (codexHome is not null)
            {
                selectedHome = codexHome;
            }
            else if (_runtimeProfile is not null)
            {
                selectedHome = _runtimeProfile.CodexHome;
            }
            else
            {
                string userProfile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    error = "无法确定默认用户目录，请显式提供 --codex-home。";
                    return false;
                }

                selectedHome = Path.Combine(userProfile, ".codex");
            }

            string normalizedHome = CodexSourceIdentity.NormalizePath(selectedHome);
            string selectedClaudeHome;
            if (claudeHome is not null)
            {
                selectedClaudeHome = claudeHome;
            }
            else
            {
                string userProfile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                string? configuredClaudeHome = Environment.GetEnvironmentVariable(
                    "CLAUDE_CONFIG_DIR");
                bool usesDefaultCodexHome = !string.IsNullOrWhiteSpace(userProfile) &&
                    string.Equals(
                        normalizedHome,
                        CodexSourceIdentity.NormalizePath(
                            Path.Combine(userProfile, ".codex")),
                        StringComparison.OrdinalIgnoreCase);
                selectedClaudeHome = usesDefaultCodexHome &&
                    !string.IsNullOrWhiteSpace(configuredClaudeHome)
                        ? configuredClaudeHome.Trim()
                        : Path.Combine(
                            Path.GetDirectoryName(normalizedHome) ?? normalizedHome,
                            ".claude");
            }
            string normalizedClaudeHome = ClaudeCodeSourceIdentity.NormalizePath(
                selectedClaudeHome);
            string? normalizedClaudeDesktopRoot = claudeDesktopRoot is not null
                ? ClaudeCodeSourceIdentity.NormalizePath(claudeDesktopRoot)
                : codexHome is null
                    ? ClaudeCodeDesktopSourceIdentity.DefaultRoot()
                    : null;
            string selectedKimiHome;
            if (kimiHome is not null)
            {
                selectedKimiHome = kimiHome;
            }
            else
            {
                string userProfile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                string? configuredKimiHome = Environment.GetEnvironmentVariable(
                    "KIMI_CODE_HOME");
                bool usesDefaultCodexHome = !string.IsNullOrWhiteSpace(userProfile) &&
                    string.Equals(
                        normalizedHome,
                        CodexSourceIdentity.NormalizePath(
                            Path.Combine(userProfile, ".codex")),
                        StringComparison.OrdinalIgnoreCase);
                selectedKimiHome = usesDefaultCodexHome &&
                    !string.IsNullOrWhiteSpace(configuredKimiHome)
                        ? configuredKimiHome.Trim()
                        : Path.Combine(
                            Path.GetDirectoryName(normalizedHome) ?? normalizedHome,
                            ".kimi-code");
            }
            string normalizedKimiHome = KimiCodeSourceIdentity.NormalizePath(
                selectedKimiHome);
            string? normalizedKimiDesktopHome = kimiDesktopHome is not null
                ? KimiCodeSourceIdentity.NormalizePath(kimiDesktopHome)
                : codexHome is null
                    ? KimiCodeDesktopSourceIdentity.DefaultHome()
                    : null;
            string profileParent = Path.GetDirectoryName(normalizedHome) ?? normalizedHome;
            string normalizedQwenHome = QwenCodeSourceIdentity.NormalizePath(
                qwenHome ?? Path.Combine(profileParent, ".qwen"));
            string normalizedQoderCliHome = QoderSourceIdentity.NormalizePath(
                qoderCliHome ?? Path.Combine(profileParent, ".qoder"));
            string? normalizedQoderRoot = qoderRoot is not null
                ? QoderSourceIdentity.NormalizePath(qoderRoot)
                : codexHome is null
                    ? DefaultApplicationDataRoot("Qoder")
                    : null;
            string? normalizedQoderCnRoot = qoderCnRoot is not null
                ? QoderSourceIdentity.NormalizePath(qoderCnRoot)
                : codexHome is null
                    ? DefaultApplicationDataRoot("QoderCN")
                    : null;
            string selectedZcodeHome = zcodeHome ?? Path.Combine(
                profileParent,
                ".zcode");
            string normalizedZcodeHome = ZcodeSourceIdentity.NormalizePath(
                selectedZcodeHome);
            string selectedWorkBuddyHome = workBuddyHome ?? Path.Combine(
                profileParent,
                ".workbuddy");
            string normalizedWorkBuddyHome = WorkBuddySourceIdentity.NormalizePath(
                selectedWorkBuddyHome);
            string configuredGeminiHome = geminiHome ??
                Environment.GetEnvironmentVariable("GEMINI_CLI_HOME") ??
                Path.Combine(profileParent, ".gemini");
            string normalizedGeminiHome = GeminiCliSourceIdentity.NormalizePath(
                configuredGeminiHome);
            string? configuredOpenCodeHome = openCodeHome;
            if (configuredOpenCodeHome is null)
            {
                configuredOpenCodeHome = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR")?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
            }
            string normalizedOpenCodeHome = OpenCodeSourceIdentity.NormalizePath(
                configuredOpenCodeHome ?? Path.Combine(
                    profileParent,
                    ".local",
                    "share",
                    "opencode"));
            string selectedDatabase = Path.GetFullPath(
                databasePath ?? _defaultStorageOptions.DatabasePath);
            if (ContainsReparsePoint(normalizedHome) ||
                ContainsReparsePoint(Path.Combine(normalizedHome, "sessions")) ||
                ContainsReparsePoint(Path.Combine(normalizedHome, "archived_sessions")) ||
                ContainsReparsePoint(normalizedClaudeHome) ||
                ContainsReparsePoint(Path.Combine(normalizedClaudeHome, "projects")) ||
                (normalizedClaudeDesktopRoot is not null &&
                 ContainsReparsePoint(normalizedClaudeDesktopRoot)) ||
                ContainsReparsePoint(normalizedKimiHome) ||
                ContainsReparsePoint(Path.Combine(normalizedKimiHome, "sessions")) ||
                (normalizedKimiDesktopHome is not null &&
                 (ContainsReparsePoint(normalizedKimiDesktopHome) ||
                  ContainsReparsePoint(Path.Combine(
                      normalizedKimiDesktopHome,
                      "sessions")))) ||
                ContainsReparsePoint(normalizedQwenHome) ||
                ContainsReparsePoint(Path.Combine(normalizedQwenHome, "projects")) ||
                ContainsReparsePoint(normalizedQoderCliHome) ||
                ContainsReparsePoint(Path.Combine(normalizedQoderCliHome, "projects")) ||
                (normalizedQoderRoot is not null &&
                 ContainsReparsePoint(normalizedQoderRoot)) ||
                (normalizedQoderCnRoot is not null &&
                 ContainsReparsePoint(normalizedQoderCnRoot)) ||
                ContainsReparsePoint(normalizedZcodeHome) ||
                ContainsReparsePoint(Path.Combine(normalizedZcodeHome, "cli")) ||
                ContainsReparsePoint(Path.Combine(normalizedZcodeHome, "cli", "db")) ||
                ContainsReparsePoint(Path.Combine(
                    normalizedZcodeHome,
                    "cli",
                    "db",
                    ZcodeSourceIdentity.DatabaseFileName)) ||
                ContainsReparsePoint(normalizedWorkBuddyHome) ||
                ContainsReparsePoint(Path.Combine(
                    normalizedWorkBuddyHome,
                    "projects")) ||
                ContainsReparsePoint(normalizedGeminiHome) ||
                ContainsReparsePoint(Path.Combine(normalizedGeminiHome, "tmp")) ||
                ContainsReparsePoint(normalizedOpenCodeHome) ||
                ContainsReparsePoint(Path.Combine(
                    normalizedOpenCodeHome,
                    "storage",
                    "message")) ||
                ContainsReparsePoint(selectedDatabase))
            {
                error = "Agent 来源和数据库路径不能经过符号链接或重解析点。";
                return false;
            }

            if (IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedHome, "sessions")) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedHome, "archived_sessions")) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedClaudeHome, "projects")) ||
                (normalizedClaudeDesktopRoot is not null &&
                 IsWithinOrEqual(selectedDatabase, normalizedClaudeDesktopRoot)) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedKimiHome, "sessions")) ||
                (normalizedKimiDesktopHome is not null &&
                 IsWithinOrEqual(
                     selectedDatabase,
                     Path.Combine(normalizedKimiDesktopHome, "sessions"))) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedQwenHome, "projects")) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedQoderCliHome, "projects")) ||
                (normalizedQoderRoot is not null &&
                 IsWithinOrEqual(selectedDatabase, normalizedQoderRoot)) ||
                (normalizedQoderCnRoot is not null &&
                 IsWithinOrEqual(selectedDatabase, normalizedQoderCnRoot)) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedZcodeHome, "cli", "db")) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedWorkBuddyHome, "projects")) ||
                IsWithinOrEqual(
                    selectedDatabase,
                    Path.Combine(normalizedGeminiHome, "tmp")) ||
                IsWithinOrEqual(selectedDatabase, normalizedOpenCodeHome))
            {
                error =
                    "数据库不能位于 Codex 原始日志目录、Claude Code CLI projects、" +
                    "Claude Desktop Code local-Agent、Kimi Code CLI sessions、" +
                    "Kimi Work Desktop sessions、Qwen Code projects、Qoder/Qoder CN Desktop 数据库、" +
                    "Qoder CLI projects、ZCode usage database、WorkBuddy projects、" +
                    "Gemini CLI tmp 或 OpenCode data 目录中。";
                return false;
            }

            options = new CoreOptions(
                check,
                once,
                rescanCodex,
                clearStatistics,
                createBackup,
                restoreBackup,
                normalizedHome,
                normalizedClaudeHome,
                normalizedClaudeDesktopRoot,
                normalizedKimiHome,
                normalizedKimiDesktopHome,
                normalizedQwenHome,
                normalizedQoderRoot,
                normalizedQoderCnRoot,
                normalizedQoderCliHome,
                normalizedZcodeHome,
                normalizedWorkBuddyHome,
                normalizedGeminiHome,
                normalizedOpenCodeHome,
                selectedDatabase);
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException
                or SecurityException)
        {
            error = "路径格式无效。";
            return false;
        }
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        out string? value)
    {
        value = null;
        if (index + 1 >= args.Count ||
            string.IsNullOrWhiteSpace(args[index + 1]) ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool IsWithinOrEqual(string path, string rootPath)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(rootPath);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        return string.Equals(relative, ".", StringComparison.Ordinal) ||
            (!string.Equals(relative, "..", StringComparison.Ordinal) &&
             !relative.StartsWith(
                 $"..{Path.DirectorySeparatorChar}",
                 StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
    }

    private static bool ContainsReparsePoint(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private static string? DefaultApplicationDataRoot(string directoryName)
    {
        string applicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(applicationData)
            ? null
            : QoderSourceIdentity.NormalizePath(
                Path.Combine(applicationData, directoryName));
    }

    private static string CreateStagingDatabasePath(string databasePath)
    {
        string fullDatabasePath = Path.GetFullPath(databasePath);
        string directory = Path.GetDirectoryName(fullDatabasePath) ??
            throw new InvalidOperationException(
                "The database path has no parent directory.");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullDatabasePath)}.codex-rebuild-{Guid.NewGuid():N}.tmp");
    }

    private static bool IsMaintenanceBlockingDiagnostic(
        CollectorDiagnostic diagnostic) =>
        diagnostic.Code.Contains(".invalid_", StringComparison.Ordinal) ||
        diagnostic.Code.Contains(".unsupported_", StringComparison.Ordinal) ||
        string.Equals(
            diagnostic.Code,
            "codex.missing_thread_identity",
            StringComparison.Ordinal);

    private static void DeleteStagingDatabaseFiles(string stagingDatabasePath)
    {
        foreach (string path in new[]
                 {
                     stagingDatabasePath,
                     $"{stagingDatabasePath}-wal",
                     $"{stagingDatabasePath}-shm"
                 })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort only. The file contains derived counters, never raw prompts.
            }
        }
    }

    private sealed record EntitySyncSummary(
        int AppliedCount,
        int IgnoredCount,
        int FailureCount,
        int CompatibilityFailureCount);

    private sealed record CoreOptions(
        bool Check,
        bool Once,
        bool RescanCodex,
        bool ClearStatistics,
        bool CreateBackup,
        bool RestoreBackup,
        string CodexHome,
        string ClaudeHome,
        string? ClaudeDesktopRoot,
        string KimiHome,
        string? KimiDesktopHome,
        string QwenHome,
        string? QoderRoot,
        string? QoderCnRoot,
        string QoderCliHome,
        string ZcodeHome,
        string WorkBuddyHome,
        string GeminiHome,
        string OpenCodeHome,
        string DatabasePath);
}
