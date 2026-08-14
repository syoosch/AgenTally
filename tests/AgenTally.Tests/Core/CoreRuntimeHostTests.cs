using System.IO;
using System.Text.Json;
using AgenTally.Core.Collectors.ClaudeCode;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Hosting;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Runtime;
using AgenTally.Tests.Support;
using AgenTally.UI.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
[DoNotParallelize]
public sealed class CoreRuntimeHostTests
{
    [TestMethod]
    public async Task RunAsync_OwnershipFailureDoesNotCreateDatabaseOrStatus()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        using CoreInstanceLease owner = CoreInstanceLease.TryAcquire(profile) ??
            throw new AssertFailedException("Test owner should acquire the lease.");
        var trayFactory = new FakeCoreTrayFactory();
        var host = CreateManagedHost(
            profile,
            trayFactory: trayFactory);

        int exitCode = await host.RunAsync(["--once"]);

        Assert.AreEqual(CoreExitCodes.AlreadyRunning, exitCode);
        Assert.IsFalse(File.Exists(profile.DatabasePath));
        Assert.IsFalse(File.Exists(profile.StatusPath));
        Assert.AreEqual(0, trayFactory.StartCalls);
    }

    [TestMethod]
    public async Task RunAsync_ApplicationShutdownPublishesStoppedAndReleasesLease()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        var trayFactory = new FakeCoreTrayFactory();
        var host = CreateManagedHost(
            profile,
            trayFactory: trayFactory);

        Task<int> running = host.RunAsync([]);
        CoreRuntimeStatus runningStatus = await WaitForStatusAsync(
            profile,
            CoreRuntimePhase.Running);
        Assert.AreEqual(1, trayFactory.StartCalls);
        Assert.AreEqual(profile.ProfileId, runningStatus.ProfileId);
        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(profile));

        int exitCode = await running.WaitAsync(TimeSpan.FromSeconds(10));
        CoreRuntimeStatus? stopped = await new CoreRuntimeStatusStore(profile)
            .ReadAsync(CancellationToken.None);

        Assert.AreEqual(CoreExitCodes.Success, exitCode);
        Assert.IsNotNull(stopped);
        Assert.AreEqual(CoreRuntimePhase.Stopped, stopped.Phase);
        Assert.AreEqual("core_stopped", stopped.MessageCode);
        Assert.IsTrue(trayFactory.Sessions.Single().IsDisposed);
        Assert.IsFalse(ApplicationShutdownSignal.TryRequest(
            profile.ShutdownEventName));
        using CoreInstanceLease recovered = CoreInstanceLease.TryAcquire(profile) ??
            throw new AssertFailedException("Core should release both leases.");
    }

    [TestMethod]
    public async Task RunAsync_ManagedNonContinuousModesNeverStartTray()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        var trayFactory = new FakeCoreTrayFactory();
        string[][] modes =
        [
            ["--check"],
            ["--once"],
            ["--rescan-codex"],
            ["--clear-statistics"]
        ];

        foreach (string[] mode in modes)
        {
            int exitCode = await CreateManagedHost(
                profile,
                trayFactory: trayFactory).RunAsync(mode);
            Assert.AreEqual(
                CoreExitCodes.Success,
                exitCode,
                $"Mode {string.Join(' ', mode)} should succeed.");
        }

        Assert.AreEqual(0, trayFactory.StartCalls);
    }

    [TestMethod]
    public async Task RunAsync_UnexpectedTrayFailureStopsManagedCore()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        var trayFactory = new FakeCoreTrayFactory(
            new InvalidOperationException("synthetic tray failure"));

        int exitCode = await CreateManagedHost(
            profile,
            trayFactory: trayFactory).RunAsync([]);
        CoreRuntimeStatus? status = await new CoreRuntimeStatusStore(profile)
            .ReadAsync(CancellationToken.None);

        Assert.AreEqual(CoreExitCodes.RuntimeFailure, exitCode);
        Assert.IsNotNull(status);
        Assert.AreEqual(CoreRuntimePhase.Failed, status.Phase);
        Assert.AreEqual(1, trayFactory.StartCalls);
        Assert.IsTrue(trayFactory.Sessions.Single().IsDisposed);
    }

    [TestMethod]
    public async Task RunAsync_MaintenanceShutdownStopsCoreWithoutSignallingUi()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        using var uiShutdownSignal = new ApplicationShutdownSignal(profile);
        using var uiShutdownCancellation = new CancellationTokenSource();
        Task uiShutdown = uiShutdownSignal.WaitAsync(
            uiShutdownCancellation.Token);
        var host = CreateManagedHost(profile);
        Task<int> running = host.RunAsync([]);

        try
        {
            await WaitForStatusAsync(profile, CoreRuntimePhase.Running);
            Assert.IsTrue(CoreMaintenanceShutdownSignal.TryRequest(profile));

            int exitCode = await running.WaitAsync(TimeSpan.FromSeconds(10));
            CoreRuntimeStatus? stopped = await new CoreRuntimeStatusStore(profile)
                .ReadAsync(CancellationToken.None);

            Assert.AreEqual(CoreExitCodes.Success, exitCode);
            Assert.IsNotNull(stopped);
            Assert.AreEqual(CoreRuntimePhase.Stopped, stopped.Phase);
            Assert.IsFalse(
                uiShutdown.IsCompleted,
                "Core-only maintenance shutdown must not signal a listening UI.");
            Assert.IsFalse(CoreMaintenanceShutdownSignal.TryRequest(profile));
            using CoreInstanceLease recovered =
                CoreInstanceLease.TryAcquire(profile) ??
                throw new AssertFailedException(
                    "Core should release both leases after maintenance stop.");
        }
        finally
        {
            if (!running.IsCompleted)
            {
                ApplicationShutdownSignal.TryRequest(profile);
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }

            uiShutdownCancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                async () => await uiShutdown);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunAsync_ProfileMarkerPublishesStoppedAndReleasesLease(
        bool includeUtf8Bom)
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        var host = CreateManagedHost(profile);

        Task<int> running = host.RunAsync([]);
        await WaitForStatusAsync(profile, CoreRuntimePhase.Running);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            profileId = profile.ProfileId,
            requestedAtUtcTicks = DateTime.UtcNow.Ticks
        });
        if (includeUtf8Bom)
        {
            payload = [0xEF, 0xBB, 0xBF, .. payload];
        }
        await File.WriteAllBytesAsync(profile.ShutdownRequestPath, payload);

        int exitCode = await running.WaitAsync(TimeSpan.FromSeconds(10));
        CoreRuntimeStatus? stopped = await new CoreRuntimeStatusStore(profile)
            .ReadAsync(CancellationToken.None);

        Assert.AreEqual(CoreExitCodes.Success, exitCode);
        Assert.IsNotNull(stopped);
        Assert.AreEqual(CoreRuntimePhase.Stopped, stopped.Phase);
        using CoreInstanceLease recovered = CoreInstanceLease.TryAcquire(profile) ??
            throw new AssertFailedException("Core should release both leases.");
    }

    [TestMethod]
    public async Task RunAsync_ManagedCoreOwnsPriceCommandPipeAndPersistsOverride()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        var host = CreateManagedHost(profile);
        Task<int> running = host.RunAsync([]);

        try
        {
            await WaitForStatusAsync(profile, CoreRuntimePhase.Running);
            var client = new NamedPipePriceCommandClient(
                profile.PriceCommandPipeName);
            var rate = new ModelPriceRate(
                "private-model",
                2m,
                0.2m,
                null,
                8m,
                100_000,
                2m,
                1.5m);

            PriceCommandRequest request = PriceCommandRequest.SetOverride(rate);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            PriceCommandResponse response;
            do
            {
                response = await client.SendAsync(
                    request,
                    CancellationToken.None);
                if (response.Result != PriceCommandResultCode.Busy)
                {
                    break;
                }

                await Task.Delay(50);
            }
            while (DateTimeOffset.UtcNow < deadline);

            Assert.AreEqual(PriceCommandResultCode.Success, response.Result);
            Assert.AreEqual(0, response.NewlyPricedRecords);

            var ledger = new SqlitePriceLedger(
                new SqliteConnectionFactory(
                    new StorageOptions(profile.DatabasePath)));
            CustomPriceSetting stored = (
                await ledger.GetCustomPricesAsync(CancellationToken.None))
                .Single();

            Assert.AreEqual(rate, stored.Rate);
        }
        finally
        {
            ApplicationShutdownSignal.TryRequest(profile);
            Assert.AreEqual(
                CoreExitCodes.Success,
                await running.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [TestMethod]
    public async Task RunAsync_ClaudeParserDriftAutomaticallyRescansOnlyClaudeOnce()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        string rollout = Path.Combine(
            profile.CodexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-parser-upgrade.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllLinesAsync(
            rollout,
            [
                """{"timestamp":"2026-07-16T01:00:00Z","type":"session_meta","payload":{"id":"parser-upgrade-thread","cwd":"C:\\fixture\\project","model_provider":"openai","cli_version":"fixture"}}""",
                """{"timestamp":"2026-07-16T01:00:01Z","type":"turn_context","payload":{"model":"openai/gpt-test-2026-07-01","cwd":"C:\\fixture\\project"}}""",
                """{"timestamp":"2026-07-16T01:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120},"total_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120}}}}"""
            ]);
        string claudeHome = Path.Combine(
            Path.GetDirectoryName(profile.CodexHome)!,
            ".claude");
        string claudeTranscript = Path.Combine(
            claudeHome,
            "projects",
            "fixture",
            "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(claudeTranscript)!);
        await File.WriteAllTextAsync(
            claudeTranscript,
            """{"type":"assistant","sessionId":"claude-parser-upgrade","cwd":"C:\\fixture\\project","timestamp":"2026-07-16T01:30:00Z","entrypoint":"cli","message":{"role":"assistant","id":"claude-parser-message","model":"test-claude-model","stop_reason":"end_turn","usage":{"input_tokens":10,"cache_read_input_tokens":4,"cache_creation_input_tokens":2,"output_tokens":7},"content":[]}}""" + Environment.NewLine);
        var storage = new StorageOptions(profile.DatabasePath);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", profile.CodexHome,
            "--database", profile.DatabasePath
        ]);
        var connections = new SqliteConnectionFactory(storage);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE usage_events
                SET imported_at_unix_ms = 1
                WHERE agent_id = 'codex';

                UPDATE usage_events
                SET parser_version = 'legacy-parser-fixture'
                WHERE agent_id = 'claude-code';

                UPDATE source_cursors
                SET parser_version = 'legacy-parser-fixture'
                WHERE source_instance_id IN (
                    SELECT source_instance_id
                    FROM source_instances
                    WHERE agent_id = 'claude-code'
                );

                UPDATE source_instances
                SET accepted_parser_version = NULL
                WHERE agent_id = 'claude-code';
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        using var firstOutput = new StringWriter();
        var firstHost = CreateManagedHost(
            profile,
            firstOutput,
            storage);
        Task<int> firstRun = firstHost.RunAsync([]);
        await WaitForStatusAsync(profile, CoreRuntimePhase.Running);
        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(profile));
        int firstExit = await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
        File.Delete(profile.ShutdownRequestPath);

        long currentParserRows;
        long untouchedCodexRows;
        await using (SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*)
                     FROM usage_events
                     WHERE agent_id = 'claude-code'
                       AND parser_version = $parser_version),
                    (SELECT COUNT(*)
                     FROM usage_events
                     WHERE agent_id = 'codex'
                       AND imported_at_unix_ms = 1);
                """;
            command.Parameters.AddWithValue(
                "$parser_version",
                ClaudeCodeTranscriptParser.CurrentParserVersion);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
            currentParserRows = reader.GetInt64(0);
            untouchedCodexRows = reader.GetInt64(1);
        }

        using var secondOutput = new StringWriter();
        var secondHost = CreateManagedHost(
            profile,
            secondOutput,
            storage);
        Task<int> secondRun = secondHost.RunAsync([]);
        await WaitForStatusAsync(profile, CoreRuntimePhase.Running);
        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(profile));
        int secondExit = await secondRun.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(CoreExitCodes.Success, seedExit);
        Assert.AreEqual(CoreExitCodes.Success, firstExit);
        Assert.AreEqual(CoreExitCodes.Success, secondExit);
        Assert.IsGreaterThan(0L, currentParserRows);
        Assert.IsGreaterThan(0L, untouchedCodexRows);
        Assert.Contains("正在安全更新本地统计数据", firstOutput.ToString());
        Assert.Contains("正在恢复增量采集", firstOutput.ToString());
        Assert.DoesNotContain("正在安全更新本地统计数据", secondOutput.ToString());
    }

    [TestMethod]
    public async Task RunAsync_MultipleParserDriftsAutomaticallyRescanBeforeRunning()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        string rollout = Path.Combine(
            profile.CodexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-multiple-parser-upgrades.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllLinesAsync(
            rollout,
            [
                """{"timestamp":"2026-07-16T01:00:00Z","type":"session_meta","payload":{"id":"multiple-parser-upgrades","cwd":"C:\\fixture\\project","model_provider":"openai","cli_version":"fixture"}}""",
                """{"timestamp":"2026-07-16T01:00:01Z","type":"turn_context","payload":{"model":"openai/gpt-test-2026-07-01","cwd":"C:\\fixture\\project"}}""",
                """{"timestamp":"2026-07-16T01:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120},"total_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120}}}}"""
            ]);
        string claudeHome = Path.Combine(
            Path.GetDirectoryName(profile.CodexHome)!,
            ".claude");
        string claudeTranscript = Path.Combine(
            claudeHome,
            "projects",
            "fixture",
            "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(claudeTranscript)!);
        await File.WriteAllTextAsync(
            claudeTranscript,
            """{"type":"assistant","sessionId":"multiple-parser-claude","cwd":"C:\\fixture\\project","timestamp":"2026-07-16T01:30:00Z","entrypoint":"cli","message":{"role":"assistant","id":"multiple-parser-message","model":"test-claude-model","stop_reason":"end_turn","usage":{"input_tokens":10,"cache_read_input_tokens":4,"cache_creation_input_tokens":2,"output_tokens":7},"content":[]}}""" + Environment.NewLine);
        var storage = new StorageOptions(profile.DatabasePath);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", profile.CodexHome,
            "--database", profile.DatabasePath
        ]);
        var connections = new SqliteConnectionFactory(storage);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE source_cursors
                SET parser_version = 'legacy-parser-fixture'
                WHERE source_instance_id IN (
                    SELECT source_instance_id
                    FROM source_instances
                    WHERE agent_id IN ('codex', 'claude-code')
                );

                UPDATE source_instances
                SET accepted_parser_version = NULL
                WHERE agent_id IN ('codex', 'claude-code');
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        using var output = new StringWriter();
        var host = CreateManagedHost(profile, output, storage);
        Task<int> running = host.RunAsync([]);
        await WaitForStatusAsync(profile, CoreRuntimePhase.Running);
        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(profile));
        int exitCode = await running.WaitAsync(TimeSpan.FromSeconds(10));

        string[] maintenanceStarts = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(
                "正在安全更新本地统计数据",
                StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(CoreExitCodes.Success, seedExit);
        Assert.AreEqual(CoreExitCodes.Success, exitCode);
        Assert.AreEqual(2, maintenanceStarts.Length);
        Assert.IsTrue(maintenanceStarts.Any(line => line.Contains(
            "codex",
            StringComparison.Ordinal)));
        Assert.IsTrue(maintenanceStarts.Any(line => line.Contains(
            "claude-code",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RunAsync_FailedAutomaticRescanPreservesTokensAndRetriesLater()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        string rollout = Path.Combine(
            profile.CodexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-parser-retry.jsonl");
        string[] validLines =
        [
            """{"timestamp":"2026-07-16T01:00:00Z","type":"session_meta","payload":{"id":"parser-retry-thread","cwd":"C:\\fixture\\project","model_provider":"openai","cli_version":"fixture"}}""",
            """{"timestamp":"2026-07-16T01:00:01Z","type":"turn_context","payload":{"model":"openai/gpt-test-2026-07-01","cwd":"C:\\fixture\\project"}}""",
            """{"timestamp":"2026-07-16T01:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120},"total_token_usage":{"input_tokens":100,"cached_input_tokens":60,"output_tokens":20,"reasoning_output_tokens":5,"total_tokens":120}}}}"""
        ];
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllLinesAsync(rollout, validLines);
        var storage = new StorageOptions(profile.DatabasePath);
        int seedExit = await new CoreHost(storage).RunAsync([
            "--once",
            "--codex-home", profile.CodexHome,
            "--database", profile.DatabasePath
        ]);
        var connections = new SqliteConnectionFactory(storage);
        UsageTokenState before = await ReadUsageTokenStateAsync(connections);
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE usage_events
                SET parser_version = 'legacy-parser-fixture'
                WHERE agent_id = 'codex';

                UPDATE source_cursors
                SET parser_version = 'legacy-parser-fixture';

                UPDATE source_instances
                SET accepted_parser_version = NULL
                WHERE agent_id = 'codex';
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await File.AppendAllTextAsync(
            rollout,
            """{"timestamp":"2026-07-16T01:00:03Z","type":"session_meta","payload":{"id":null}}""" +
            Environment.NewLine);
        using var failedOutput = new StringWriter();
        int failedExit = await CreateManagedHost(
            profile,
            failedOutput,
            storage).RunAsync([]);
        CoreRuntimeStatus? failedStatus = await new CoreRuntimeStatusStore(profile)
            .ReadAsync(CancellationToken.None);
        UsageTokenState afterFailure =
            await ReadUsageTokenStateAsync(connections);

        await File.WriteAllLinesAsync(rollout, validLines);
        using var retryOutput = new StringWriter();
        var retryHost = CreateManagedHost(
            profile,
            retryOutput,
            storage);
        Task<int> retryRun = retryHost.RunAsync([]);
        await WaitForStatusAsync(profile, CoreRuntimePhase.Running);
        Assert.IsTrue(ApplicationShutdownSignal.TryRequest(profile));
        int retryExit = await retryRun.WaitAsync(TimeSpan.FromSeconds(10));
        UsageTokenState afterRetry =
            await ReadUsageTokenStateAsync(connections);

        Assert.AreEqual(CoreExitCodes.Success, seedExit);
        Assert.AreEqual(CoreExitCodes.ParserRescanRequired, failedExit);
        Assert.IsNotNull(failedStatus);
        Assert.AreEqual(CoreRuntimePhase.NeedsParserRescan, failedStatus.Phase);
        Assert.AreEqual("statistics_update_incomplete", failedStatus.MessageCode);
        Assert.AreEqual(before, afterFailure);
        Assert.AreEqual(before, afterRetry);
        Assert.AreEqual(CoreExitCodes.Success, retryExit);
        Assert.Contains("主数据库保持不变", failedOutput.ToString());
        Assert.Contains("正在恢复增量采集", retryOutput.ToString());
    }

    private static AgenTallyRuntimeProfile CreateProfile(
        TestTempDirectory directory)
    {
        File.WriteAllText(directory.File("AgenTally.sln"), string.Empty);
        File.WriteAllText(directory.File(".agentally-root"), string.Empty);
        string codexHome = directory.File(Path.Combine("user", ".codex"));
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(codexHome, "archived_sessions"));
        return AgenTallyRuntimeProfile.CreateDevelopment(
            directory.Path,
            codexHome);
    }

    private static CoreHost CreateManagedHost(
        AgenTallyRuntimeProfile profile,
        TextWriter? output = null,
        StorageOptions? storage = null,
        ICoreTrayFactory? trayFactory = null) => new(
            storage ?? new StorageOptions(profile.DatabasePath),
            timeProvider: null,
            output,
            profile,
            trayFactory ?? new FakeCoreTrayFactory());

    private static async Task<CoreRuntimeStatus> WaitForStatusAsync(
        AgenTallyRuntimeProfile profile,
        CoreRuntimePhase phase)
    {
        var store = new CoreRuntimeStatusStore(profile);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            CoreRuntimeStatus? status = await store.ReadAsync(CancellationToken.None);
            if (status?.Phase == phase)
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException($"Core did not reach {phase}.");
    }

    private static async Task<UsageTokenState> ReadUsageTokenStateAsync(
        SqliteConnectionFactory connections)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(input_reported_value), 0),
                   COALESCE(SUM(cache_read_value), 0),
                   COALESCE(SUM(output_value), 0),
                   COALESCE(SUM(reasoning_value), 0),
                   COALESCE(SUM(normalized_total_value), 0)
            FROM usage_events
            WHERE agent_id = 'codex';
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        return new UsageTokenState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private sealed record UsageTokenState(
        long Rows,
        long Input,
        long CacheRead,
        long Output,
        long Reasoning,
        long NormalizedTotal);

    private sealed class FakeCoreTrayFactory : ICoreTrayFactory
    {
        private readonly Exception? _completionFailure;

        public FakeCoreTrayFactory(Exception? completionFailure = null)
        {
            _completionFailure = completionFailure;
        }

        public int StartCalls { get; private set; }

        public List<FakeCoreTraySession> Sessions { get; } = [];

        public ICoreTraySession Start(AgenTallyRuntimeProfile profile)
        {
            StartCalls++;
            var session = new FakeCoreTraySession(_completionFailure);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeCoreTraySession : ICoreTraySession
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeCoreTraySession(Exception? completionFailure)
        {
            if (completionFailure is not null)
            {
                _completion.TrySetException(completionFailure);
            }
        }

        public Task Completion => _completion.Task;

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            _completion.TrySetResult();
        }
    }
}
